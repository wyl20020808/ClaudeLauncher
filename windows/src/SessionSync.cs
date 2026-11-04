using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Web.Script.Serialization;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace ClaudeLauncher {
    internal sealed class SyncVersion { public string Id; public long Stamp; }
    // Stamp：最后写入时间的 Ticks（100 ns，NTFS 的最小单位）。Versions：关联组里各账号的版本。
    internal sealed class SyncSessionRow { public string Path, Id, Title, ProjectPath, ProjectName; public DateTime Modified; public long Stamp; public int Account; public bool Linked; public Dictionary<int, SyncVersion> Versions = new Dictionary<int, SyncVersion>(); }
    internal sealed class CleanedSession { public string Error, SessionId, Slug, Title, Preview, Report, FromId; public byte[] Jsonl, Record; public int Messages, Images, From; public long Stamp; public bool Replaces; }
    // 问题诊断用：列表时顺带记下每一步跳过了什么、为什么。只记 ID、cwd 和原因，不记标题和正文。
    internal sealed class SyncScan {
        public readonly List<string> Problems = new List<string>();
        public int Records, Unreadable, Invalid;
        public readonly List<KeyValuePair<string, string>> Archived = new List<KeyValuePair<string, string>>();
        public readonly Dictionary<string, string> Active = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public readonly List<SyncSkip> Skipped = new List<SyncSkip>();
        public bool NoProjectsFolder;
    }
    // Reason：outside 不在清单项目里；slug 路径太长；missing 没有正文；empty 正文为空；link 正文或目录是链接。
    internal sealed class SyncSkip { public string Id, Cwd, Reason, ProjectPath; }
    // 同步会话（SPEC 项目清单 / 同步会话）：只读来源，从白名单重新生成清理副本，只新增不覆盖。不启动 CLI、不发请求。
    internal static class SessionSync {
        const long MaxSourceBytes = 512L * 1024 * 1024;
        const int TailBytes = 256 * 1024, MaxSessions = 2000, MaxSlug = 200;
        const string Terminator = "\\s`'\"()\\[\\]<>|*?，。；：！？、）】》”’";
        static readonly Regex Tags = new Regex(@"<(system-reminder|command-name|command-message|command-args|local-command-stdout|local-command-stderr|local-command-caveat|task-notification|ide_selection|ide_opened_file)\b[^>]*>.*?</\1>", RegexOptions.Singleline);
        static readonly Regex Email = new Regex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}");
        static readonly Regex Key = new Regex(@"\b(?:sk-[A-Za-z0-9_-]{16,}|gh[pousr]_[A-Za-z0-9]{20,}|AKIA[0-9A-Z]{16}|xox[baprs]-[A-Za-z0-9-]{10,})\b");
        static readonly Regex Bearer = new Regex(@"\bBearer\s+[A-Za-z0-9._~+/=-]{16,}");
        static readonly Regex OldSuffix = new Regex(@"(?:（清理副本）| \(cleaned copy\)|（(?:副本|同步) \d\d/\d\d \d\d:\d\d）| \((?:Copy|Synced) \d\d/\d\d \d\d:\d\d\))$");

        // 与官方 replace(/[^a-zA-Z0-9]/g,"-") 相同，按 UTF-16 单元处理；超过 200 字符官方改用哈希，这里拒绝。
        internal static string Slug(string cwd) {
            var slug = new StringBuilder(cwd.Length);
            foreach (char c in cwd) slug.Append((c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ? c : '-');
            return slug.Length > 0 && slug.Length <= MaxSlug ? slug.ToString() : null;
        }

        // ---- 路径：大小写不敏感、正反斜杠等价、消去 .. ；链接按真实路径判断 ----
        internal static string Normalize(string path) {
            if (path == null) return null;
            if (path.StartsWith(@"\\?\")) path = path.Substring(4);
            if (!Regex.IsMatch(path, @"^[A-Za-z]:[\\/]")) return null;
            var parts = new List<string>();
            foreach (string part in path.Substring(3).Split('\\', '/')) {
                if (part.Length == 0 || part == ".") continue;
                if (part == "..") { if (parts.Count == 0) return null; parts.RemoveAt(parts.Count - 1); }
                else parts.Add(part);
            }
            return path.Substring(0, 2) + "\\" + String.Join("\\", parts);
        }
        // 返回相对 root 的路径（统一用 /），root 本身返回空串，不在其内返回 null。按整段比较，D:\ab 不在 D:\a 内。
        internal static string Within(string path, string root) {
            string[] items = path.TrimEnd('\\').Split('\\'), bases = root.TrimEnd('\\').Split('\\');
            if (items.Length < bases.Length) return null;
            for (int i = 0; i < bases.Length; i++) if (!String.Equals(items[i], bases[i], StringComparison.OrdinalIgnoreCase)) return null;
            return String.Join("/", items.Skip(bases.Length));
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern int GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, int size, int flags);
        static string FinalPath(string path) {
            using (var handle = CreateFile(path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero)) {
                if (handle.IsInvalid) return null;
                var text = new StringBuilder(32768);
                return GetFinalPathNameByHandle(handle, text, text.Capacity, 0) > 0 ? text.ToString() : null;
            }
        }
        // 解析最近的已存在上级，未存在的尾部保留。非固定盘只做字面判断，不为一条历史记录访问网络盘。
        internal static string Real(string path) {
            string full = Normalize(path);
            if (full == null) return null;
            try { if (new DriveInfo(full.Substring(0, 1)).DriveType != DriveType.Fixed) return full; } catch (ArgumentException) { return full; }
            var tail = new Stack<string>();
            for (string at = full; ; ) {
                string real = FinalPath(at);
                if (real != null) {
                    string normal = Normalize(real);
                    if (normal == null) return null; // 映射到网络或设备路径：不算项目内。
                    return tail.Count == 0 ? normal : normal.TrimEnd('\\') + "\\" + String.Join("\\", tail);
                }
                if (at.Length <= 3) return full;
                int slash = at.LastIndexOf('\\');
                tail.Push(at.Substring(slash + 1));
                at = slash == 2 ? at.Substring(0, 3) : at.Substring(0, slash);
            }
        }
        static string InProject(string path, string root) { string real = Real(path); return real == null ? null : Within(real, root); }

        // ---- 列表 ----
        // 侧栏以官方记录为准：只列未归档记录指向的正文，按记录的 cwd 归到最深的清单项目（项目内的 worktree 也算）。
        // 没有记录的 JSONL 是已删除的会话，或改模型／回退前留下的旧段（记录的 priorCliSessionIds），不列。
        internal static List<SyncSessionRow> List(string config, IEnumerable<string> profiles, IList<string> projects) { return ListWith(config, profiles, projects, null); }
        internal sealed class ProjectRoot { public string Path, Real; }
        internal static List<ProjectRoot> Roots(IEnumerable<string> projects) { return projects.Select(p => new ProjectRoot { Path = p, Real = Real(p) }).Where(p => p.Real != null).OrderByDescending(p => p.Real.Length).ToList(); }
        // 最深的清单项目；不在任何项目里返回 null。
        internal static string ProjectOf(string cwd, IList<ProjectRoot> roots) {
            string real = Real(cwd); if (real == null) return null;
            var project = roots.FirstOrDefault(p => Within(real, p.Real) != null);
            return project == null ? null : project.Path;
        }
        // scan 不为 null 时记下跳过原因（问题诊断）；列出的结果与不传时相同。
        internal static List<SyncSessionRow> ListWith(string config, IEnumerable<string> profiles, IList<string> projects, SyncScan scan) {
            var rows = new List<SyncSessionRow>();
            var roots = Roots(projects);
            Action<string, string, string, string> skip = (id, cwd, reason, project) => { if (scan != null) scan.Skipped.Add(new SyncSkip { Id = id, Cwd = cwd, Reason = reason, ProjectPath = project }); };
            try {
                var folder = new DirectoryInfo(Path.Combine(config, "projects"));
                if (!folder.Exists || (folder.Attributes & FileAttributes.ReparsePoint) != 0) { if (scan != null) { scan.NoProjectsFolder = true; ActiveSessions(profiles, scan); } return rows; }
                foreach (var session in ActiveSessions(profiles, scan)) {
                    if (rows.Count >= MaxSessions) { if (scan != null) scan.Problems.Add("超过 " + MaxSessions + " 条，后面的没有列出"); break; }
                    string slug = Slug(session.Value), project = ProjectOf(session.Value, roots);
                    if (project == null) { skip(session.Key, session.Value, "outside", null); continue; }
                    if (slug == null) { skip(session.Key, session.Value, "slug", project); continue; }
                    var parent = new DirectoryInfo(Path.Combine(folder.FullName, slug)); var file = new FileInfo(Path.Combine(parent.FullName, session.Key + ".jsonl"));
                    if (!parent.Exists || !file.Exists) { skip(session.Key, session.Value, "missing", project); continue; }
                    if (((parent.Attributes | file.Attributes) & FileAttributes.ReparsePoint) != 0) { skip(session.Key, session.Value, "link", project); continue; }
                    if (file.Length == 0) { skip(session.Key, session.Value, "empty", project); continue; }
                    rows.Add(new SyncSessionRow { Path = file.FullName, Id = session.Key, Title = TailTitle(file.FullName) ?? "未命名会话", Modified = file.LastWriteTime, Stamp = file.LastWriteTimeUtc.Ticks, ProjectPath = project });
                }
            } catch (IOException error) { if (scan != null) scan.Problems.Add("列出正文时出错：" + error.GetType().Name + " " + error.Message); }
            catch (UnauthorizedAccessException error) { if (scan != null) scan.Problems.Add("列出正文时没有权限：" + error.Message); }
            return rows.OrderByDescending(r => r.Stamp).ToList();
        }
        // 来源账号全部账号／组织目录下的记录：cliSessionId → cwd，跳过 isArchived 为 true 的。只留这三个字段，其余内容不保存。
        // 目录不安全或记录打不开、解析不了时跳过，宁可少列；一个组织目录读不了只跳过它，不影响其他目录。
        static Dictionary<string, string> ActiveSessions(IEnumerable<string> profiles, SyncScan scan = null) {
            var result = scan != null ? scan.Active : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Action<string> problem = text => { if (scan != null) scan.Problems.Add(text); };
            foreach (string root in SessionRoots(profiles)) {
                var accounts = ChildrenSafe(root, problem); if (accounts == null) continue;
                foreach (string account in accounts) {
                    var organizations = ChildrenSafe(Path.Combine(root, account), problem); if (organizations == null) continue;
                    foreach (string organization in organizations) {
                        string directory = Path.Combine(root, account, organization);
                        try {
                            foreach (string path in Directory.EnumerateFiles(directory, "local_*.json")) {
                                if (scan != null) scan.Records++;
                                Dictionary<string, object> record = null; bool unreadable = false;
                                try {
                                    using (var stream = LocalFiles.OpenRead(path, 8 * 1024 * 1024, 7)) {
                                        var data = new byte[stream.Length]; int count = 0, n;
                                        while (count < data.Length && (n = stream.Read(data, count, data.Length - count)) > 0) count += n;
                                        record = Json().DeserializeObject(new UTF8Encoding(false, true).GetString(data, 0, count)) as Dictionary<string, object>;
                                    }
                                } catch (IOException) { unreadable = true; } catch (UnauthorizedAccessException) { unreadable = true; } catch (ArgumentException) { } catch (InvalidOperationException) { }
                                string id = Str(record, "cliSessionId"), cwd = Str(record, "cwd"); Guid guid;
                                if (id == null || cwd == null || id.Length != 36 || !Guid.TryParse(id, out guid)) { if (scan != null) { if (unreadable) scan.Unreadable++; else scan.Invalid++; } continue; }
                                if (True(record, "isArchived")) { if (scan != null) scan.Archived.Add(new KeyValuePair<string, string>(id.ToLowerInvariant(), cwd)); continue; }
                                result[id.ToLowerInvariant()] = cwd;
                            }
                        } catch (IOException error) { problem("读取记录目录 " + Tail(directory) + " 出错：" + error.Message); }
                        catch (UnauthorizedAccessException error) { problem("读取记录目录 " + Tail(directory) + " 没有权限：" + error.Message); }
                    }
                }
            }
            return result;
        }
        static string Tail(string directory) { var parts = directory.TrimEnd('\\').Split('\\'); return String.Join("\\", parts.Skip(Math.Max(0, parts.Length - 3))); }
        static List<string> ChildrenSafe(string parent, Action<string> problem) {
            try {
                var reasons = new List<string>(); var names = Children(parent, reasons);
                if (names == null) problem("记录目录 " + Tail(parent) + " 被当作不安全，整层跳过：" + String.Join("；", reasons));
                return names;
            } catch (IOException error) { problem("读取记录目录 " + Tail(parent) + " 出错：" + error.Message); return null; }
            catch (UnauthorizedAccessException error) { problem("读取记录目录 " + Tail(parent) + " 没有权限：" + error.Message); return null; }
        }
        internal static bool IsSyncCopy(string title) { return title != null && OldSuffix.IsMatch(title); }

        // ---- 关联（SPEC 版本判断）：每组是 {账号: 会话 ID}，只存编号和 ID，不存标题或正文 ----
        internal static List<Dictionary<int, string>> Link(IEnumerable<Dictionary<int, string>> links, int from, string fromId, int to, string toId) {
            var joined = new Dictionary<int, string>(); var result = new List<Dictionary<int, string>>();
            foreach (var group in links) {
                string a, b;
                if ((group.TryGetValue(from, out a) && a == fromId) || (group.TryGetValue(to, out b) && b == toId)) foreach (var pair in group) joined[pair.Key] = pair.Value;
                else result.Add(group);
            }
            joined[from] = fromId; joined[to] = toId; result.Add(joined);
            return result;
        }
        // 每组一行，代表取最新版本（sessions 已按 Stamp 从新到旧）；关联不跨项目，目标文件按本项目目录查找。
        internal static List<SyncSessionRow> Merge(IEnumerable<SyncSessionRow> sessions, IList<Dictionary<int, string>> links) {
            var groups = new Dictionary<string, int>();
            for (int i = 0; i < links.Count; i++) foreach (var pair in links[i]) groups[pair.Key + ":" + pair.Value] = i;
            var merged = new Dictionary<string, SyncSessionRow>(StringComparer.OrdinalIgnoreCase); var rows = new List<SyncSessionRow>();
            foreach (var session in sessions) {
                int group; bool linked = groups.TryGetValue(session.Account + ":" + session.Id, out group);
                string key = session.ProjectPath + "\n" + (linked ? "#" + group : session.Account + ":" + session.Id);
                SyncSessionRow row;
                if (!merged.TryGetValue(key, out row)) { row = session; row.Versions = new Dictionary<int, SyncVersion>(); merged[key] = row; rows.Add(row); }
                row.Versions[session.Account] = new SyncVersion { Id = session.Id, Stamp = session.Stamp };
                if (linked) row.Linked = true;
            }
            return rows;
        }
        // 启动器自己的文件，恢复默认不清除；损坏或版本不符时当作没有关联，写入时不覆盖损坏的原文件。
        internal static List<Dictionary<int, string>> LoadLinks(string path) {
            var result = new List<Dictionary<int, string>>();
            if (!File.Exists(path)) return result;
            var root = Json().DeserializeObject(LocalFiles.Read(path, 4 * 1024 * 1024)) as Dictionary<string, object>;
            object version, raw;
            if (root == null || !root.TryGetValue("version", out version) || !(version is int) || (int)version != 1 || !root.TryGetValue("links", out raw) || !(raw is object[])) throw new InvalidDataException("会话关联记录无法识别。");
            foreach (object item in (object[])raw) {
                var map = item as Dictionary<string, object>; if (map == null) continue;
                var group = new Dictionary<int, string>(); Guid id; int number;
                foreach (var pair in map) if (Int32.TryParse(pair.Key, out number) && number >= 1 && number <= 10 && pair.Key == number.ToString() && pair.Value is string && Guid.TryParse((string)pair.Value, out id) && ((string)pair.Value).Length == 36) group[number] = ((string)pair.Value).ToLowerInvariant();
                if (group.Count >= 2) result.Add(group);
            }
            return result;
        }
        internal static void SaveLinks(string path, IEnumerable<Dictionary<int, string>> links) {
            var items = links.Select(g => g.ToDictionary(p => p.Key.ToString(), p => (object)p.Value)).ToList();
            LocalFiles.Write(path, Json().Serialize(new Dictionary<string, object> { { "version", 1 }, { "links", items } }));
        }
        static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue, RecursionLimit = 1000 }; }
        // 共享打开：来源账号可能正在运行，不能挡住官方继续写入。
        static string TailTitle(string path) {
            try {
                using (var stream = LocalFiles.OpenRead(path, MaxSourceBytes, 7)) {
                    long start = Math.Max(0, stream.Length - TailBytes); stream.Seek(start, SeekOrigin.Begin);
                    var data = new byte[(int)(stream.Length - start)]; int count = 0, n;
                    while (count < data.Length && (n = stream.Read(data, count, data.Length - count)) > 0) count += n;
                    string text = Encoding.UTF8.GetString(data, 0, count), title = null;
                    string[] lines = text.Split('\n');
                    for (int i = start > 0 ? 1 : 0; i < lines.Length - 1; i++) {
                        if (lines[i].IndexOf("\"custom-title\"", StringComparison.Ordinal) < 0) continue;
                        try { var row = Json().DeserializeObject(lines[i]) as Dictionary<string, object>; object value; if (row != null && row.TryGetValue("customTitle", out value) && value is string) title = (string)value; }
                        catch (ArgumentException) { } catch (InvalidOperationException) { }
                    }
                    return title;
                }
            } catch (IOException) { return null; } catch (UnauthorizedAccessException) { return null; }
        }

        // ---- 读取：只把白名单字段留在内存 ----
        sealed class Block { public string Kind, Text, Name, FilePath; }
        sealed class Row { public string Type, Uuid, Parent, Logical, Cwd, Stamp, Queued; public bool Skip; public string TextContent; public List<Block> Blocks; }
        static string Str(Dictionary<string, object> map, string key) { object value; return map != null && map.TryGetValue(key, out value) ? value as string : null; }
        static bool True(Dictionary<string, object> map, string key) { object value; return map.TryGetValue(key, out value) && value is bool && (bool)value; }
        static Row ParseRow(Dictionary<string, object> map) {
            var row = new Row { Type = Str(map, "type"), Uuid = Str(map, "uuid"), Parent = Str(map, "parentUuid"), Logical = Str(map, "logicalParentUuid"), Cwd = Str(map, "cwd"), Stamp = Str(map, "timestamp") };
            row.Skip = True(map, "isMeta") || True(map, "isApiErrorMessage") || True(map, "isVisibleInTranscriptOnly") || True(map, "isCompactSummary");
            object raw;
            if (map.TryGetValue("attachment", out raw)) {
                var attachment = raw as Dictionary<string, object>;
                string mode = Str(attachment, "commandMode");
                if (Str(attachment, "type") == "queued_command" && (mode == null || mode == "prompt")) row.Queued = Str(attachment, "prompt");
            }
            if (!map.TryGetValue("message", out raw)) return row;
            var message = raw as Dictionary<string, object>; object content;
            if (message == null || !message.TryGetValue("content", out content)) return row;
            row.TextContent = content as string;
            var items = content as object[];
            if (items != null) {
                row.Blocks = new List<Block>();
                foreach (object item in items) {
                    var value = item as Dictionary<string, object>; string kind = Str(value, "type");
                    if (kind == null) continue;
                    var block = new Block { Kind = kind };
                    if (kind == "text") block.Text = Str(value, "text");
                    if (kind == "tool_use") {
                        block.Name = Str(value, "name"); object input; var inputs = value.TryGetValue("input", out input) ? input as Dictionary<string, object> : null;
                        block.FilePath = new[] { Str(inputs, "file_path"), Str(inputs, "notebook_path") }.FirstOrDefault(p => !String.IsNullOrEmpty(p));
                    }
                    row.Blocks.Add(block);
                }
            }
            return row;
        }
        // 读一份快照；没有换行结尾的行是正在写入的半行，忽略。
        static string ReadRows(string path, List<Row> rows, ref string title, ref int sidechain, ref long version) {
            FileStream stream;
            try {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxSourceBytes) return "来源会话文件超过 512 MiB，未处理。";
                stream = LocalFiles.OpenRead(path, MaxSourceBytes, 7);
            } catch (IOException) { return "无法安全打开来源会话文件。"; } catch (UnauthorizedAccessException) { return "无法安全打开来源会话文件。"; }
            using (stream) {
                long size = stream.Length; DateTime stamp = File.GetLastWriteTimeUtc(path);
                var decoder = new UTF8Encoding(false, true); var serializer = Json();
                var line = new MemoryStream(); var buffer = new byte[65536]; int count; long total = 0;
                while ((count = stream.Read(buffer, 0, buffer.Length)) > 0) {
                    total += count; int start = 0;
                    for (int i = 0; i < count; i++) {
                        if (buffer[i] != 10) continue;
                        line.Write(buffer, start, i - start); start = i + 1;
                        Dictionary<string, object> map;
                        try { map = serializer.DeserializeObject(decoder.GetString(line.GetBuffer(), 0, (int)line.Length)) as Dictionary<string, object>; }
                        catch (ArgumentException) { map = null; } catch (InvalidOperationException) { map = null; }
                        if (map == null) return "来源会话含无法识别的行，未处理。";
                        line.SetLength(0);
                        if (Str(map, "type") == "custom-title" && Str(map, "customTitle") != null) title = Str(map, "customTitle");
                        if (True(map, "isSidechain")) sidechain++;
                        else if (Str(map, "uuid") != null) rows.Add(ParseRow(map));
                    }
                    line.Write(buffer, start, count - start);
                }
                if (total != size || stream.Length != size || File.GetLastWriteTimeUtc(path) != stamp) return "来源会话正在写入，请等它结束后重试。";
                version = stamp.Ticks;
            }
            return null;
        }

        // ---- 清理 ----
        sealed class Cleaner {
            internal string Root; internal int Redacted, Outside; Regex paths;
            internal Cleaner(string root, string home) {
                Root = root;
                Func<string, string> pattern = value => String.Join(@"[\\/]+", value.TrimEnd('\\').Split('\\').Select(Regex.Escape));
                string homes = String.Join("|", new[] { root, home }.Where(p => p != null).Select(pattern));
                // 本机主目录和项目目录，加任意 X:\Users\<名字> 及 Git Bash 的 /c/Users/<名字>；大小写、正反斜杠、JSON 转义都算。
                string other = @"[A-Za-z]:[\\/]+Users[\\/]+[^\\/" + Terminator + @"]+|(?:/mnt)?/[A-Za-z]/Users/[^\\/" + Terminator + "]+";
                paths = new Regex(@"(?<![A-Za-z0-9])(?:" + homes + "|" + other + @")(?:[\\/][^" + Terminator + @"]*|(?=[" + Terminator + "]|$))", RegexOptions.IgnoreCase);
            }
            string Count(Regex rule, string text, string with) { int hits = rule.Matches(text).Count; if (hits == 0) return text; Redacted += hits; return rule.Replace(text, with); }
            internal string Text(string text) {
                text = Tags.Replace(text, "");
                text = Count(Email, text, "[已移除:邮箱]"); text = Count(Key, text, "[已移除:密钥]"); text = Count(Bearer, text, "[已移除:令牌]");
                text = paths.Replace(text, match => {
                    string relative = InProject(match.Value, Root);
                    if (relative == null) { Outside++; return "[项目外路径已移除]"; }
                    return relative.Length > 0 ? relative : ".";
                });
                return text.Trim();
            }
            internal string ToolNote(Block block, string rowCwd) {
                string name = block.Name != null && Regex.IsMatch(block.Name, "^[A-Za-z0-9_.:-]{1,100}$") ? block.Name : "unknown";
                if (block.FilePath == null) return "[历史工具请求：" + name + "]";
                // 相对路径基于当时的工作目录；/x 这种无盘符路径和 UNC 一律算项目外。
                bool absolute = Regex.IsMatch(block.FilePath, @"^[A-Za-z]:[\\/]"), rooted = block.FilePath.StartsWith("/") || block.FilePath.StartsWith("\\");
                string relative = rooted ? null : InProject(absolute ? block.FilePath : rowCwd + "\\" + block.FilePath, Root);
                return relative == null ? "[历史工具请求：" + name + "，项目外路径已省略]" : "[历史工具请求：" + name + "，" + (relative.Length > 0 ? relative : ".") + "]";
            }
        }
        internal static string CopyTitle(string title, DateTime date) {
            string text = OldSuffix.Replace(title, ""), time = date.ToString("MM/dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
            return Appearance.Language == 1 ? text + " (Synced " + time + ")" : text + "（同步 " + time + "）";
        }
        static string Serialize(object value) {
            // 官方按行读取时 U+2028/2029 也可能断行，转义后才能保证一条记录一行。
            return Json().Serialize(value).Replace("\u2028", "\\u2028").Replace("\u2029", "\\u2029");
        }
        sealed class Turn { public string Role, Text, Stamp; }
        const string CopyNote = "[这是从其他账号同步的清理副本：只保留对话文字和工具请求说明，工具结果、思考内容和图片都已移除。历史中说已完成的改动，请以磁盘上的文件和 git 状态为准。]\n\n";
        // targetId：替换已关联的副本时沿用目标的会话 ID，否则新建。
        internal static CleanedSession Clean(string source, string project, string home, string targetId = null) {
            var rows = new List<Row>(); string title = null; int sidechain = 0; long version = 0;
            string failure = ReadRows(source, rows, ref title, ref sidechain, ref version);
            if (failure != null) return new CleanedSession { Error = failure };
            var byId = new Dictionary<string, Row>(rows.Count); Row leaf = null;
            foreach (var row in rows) { byId[row.Uuid] = row; if (row.Type == "user" || row.Type == "assistant") leaf = row; }
            if (leaf == null) return new CleanedSession { Error = "来源会话没有可同步的对话。" };
            var chain = new List<Row>(); var seen = new HashSet<string>();
            for (Row at = leaf; at != null && seen.Add(at.Uuid); ) {
                chain.Add(at); string parent = at.Parent ?? at.Logical; Row next;
                at = parent != null && byId.TryGetValue(parent, out next) ? next : null;
            }
            chain.Reverse();
            // 会话最后所在目录决定项目：官方可能把临时工作区的会话移进项目，更早的 cwd 可以在外面。
            string root = Real(project); if (root == null) return new CleanedSession { Error = "来源会话的工作目录与所选项目不一致，未处理。" };
            string lastCwd = chain.Where(r => !String.IsNullOrEmpty(r.Cwd)).Select(r => r.Cwd).LastOrDefault();
            if (lastCwd == null || InProject(lastCwd, root) == null) return new CleanedSession { Error = "来源会话的工作目录与所选项目不一致，未处理。" };
            string slug = Slug(root); if (slug == null) return new CleanedSession { Error = "项目路径过长，暂不支持同步会话。" };

            var cleaner = new Cleaner(root, home == null ? null : Normalize(home));
            var turns = new List<Turn>(); int toolNotes = 0, toolResults = 0, thinking = 0, images = 0; string currentCwd = root;
            Action<string, string, string> add = (role, text, stamp) => {
                if (String.IsNullOrEmpty(text)) return;
                if (turns.Count > 0 && turns[turns.Count - 1].Role == role) turns[turns.Count - 1].Text += "\n\n" + text;
                else turns.Add(new Turn { Role = role, Text = text, Stamp = stamp });
            };
            // 文字里的相对路径只有对照当时的目录才有意义，切换时标注；项目外不写路径。
            Func<string, string> cwdNote = rowCwd => {
                if (String.Equals(Normalize(rowCwd), Normalize(currentCwd), StringComparison.OrdinalIgnoreCase)) return "";
                currentCwd = rowCwd; string relative = InProject(rowCwd, root);
                return "[工作目录：" + (relative == null ? "项目外" : relative.Length > 0 ? relative : "项目根目录") + "]\n";
            };
            foreach (var row in chain) {
                string rowCwd = String.IsNullOrEmpty(row.Cwd) ? currentCwd : row.Cwd;
                if (row.Type == "attachment") { if (row.Queued != null) add("user", cwdNote(rowCwd) + cleaner.Text(row.Queued), row.Stamp); continue; }
                if ((row.Type != "user" && row.Type != "assistant") || row.Skip) continue;
                var parts = new List<string>();
                if (row.TextContent != null) parts.Add(cleaner.Text(row.TextContent));
                else if (row.Blocks != null) foreach (var block in row.Blocks) {
                    if (block.Kind == "text") { if (block.Text != null && !block.Text.StartsWith("[Request interrupted", StringComparison.Ordinal)) parts.Add(cleaner.Text(block.Text)); }
                    else if (block.Kind == "tool_use") { parts.Add(cleaner.ToolNote(block, rowCwd)); toolNotes++; }
                    else if (block.Kind == "tool_result") toolResults++;
                    else if (block.Kind == "thinking") thinking++;
                    else if (block.Kind == "image") images++;
                }
                parts.RemoveAll(String.IsNullOrEmpty);
                if (parts.Count == 0) continue;
                add(row.Type, cwdNote(rowCwd) + String.Join("\n", parts), row.Stamp);
            }
            while (turns.Count > 0 && turns[0].Role != "user") turns.RemoveAt(0);
            if (turns.Count == 0) return new CleanedSession { Error = "来源会话没有可同步的对话文字。" };
            if (!turns[0].Text.StartsWith(CopyNote, StringComparison.Ordinal)) turns[0].Text = CopyNote + turns[0].Text; // 来回同步时已带说明，不叠加
            if (turns[turns.Count - 1].Role != "assistant") add("assistant", "（同步的历史到此结束，上一条请求没有留下回复。）", turns[turns.Count - 1].Stamp);

            string session = targetId ?? Guid.NewGuid().ToString(), parentId = null;
            string clean = CopyTitle(cleaner.Text(String.IsNullOrEmpty(title) ? "未命名会话" : title), DateTime.Now);
            var jsonl = new StringBuilder(); var body = new StringBuilder();
            foreach (var turn in turns) {
                string id = Guid.NewGuid().ToString();
                object message = turn.Role == "user"
                    ? (object)new Dictionary<string, object> { { "role", "user" }, { "content", turn.Text } }
                    : new Dictionary<string, object> { { "role", "assistant" }, { "type", "message" }, { "content", new object[] { new Dictionary<string, object> { { "type", "text" }, { "text", turn.Text } } } } };
                jsonl.Append(Serialize(new Dictionary<string, object> { { "type", turn.Role }, { "uuid", id }, { "parentUuid", parentId }, { "isSidechain", false }, { "sessionId", session }, { "cwd", root }, { "timestamp", turn.Stamp }, { "message", message } })).Append('\n');
                parentId = id;
                body.Append("【").Append(Appearance.Text(turn.Role == "user" ? "用户" : "助手")).Append("】\n").Append(turn.Text).Append("\n\n");
            }
            jsonl.Append(Serialize(new Dictionary<string, object> { { "type", "custom-title" }, { "customTitle", clean }, { "sessionId", session } })).Append('\n');
            long now = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
            // 前四个键顺序与官方相同，之后“识别并导入”只读前缀时也能读到目录。
            string record = Serialize(new Dictionary<string, object> { { "sessionId", "local_" + session }, { "cliSessionId", session }, { "cwd", root }, { "originCwd", root }, { "title", clean }, { "createdAt", now }, { "lastActivityAt", now }, { "isArchived", false } });
            string report = "保留 " + turns.Count + " 条消息，" + toolNotes + " 条工具说明。\n丢弃：" + toolResults + " 个工具结果、" + thinking + " 个思考块、" + images + " 张图片、" + sidechain + " 行子代理记录、" + (rows.Count - chain.Count) + " 行非主链记录。\n替换：" + cleaner.Redacted + " 处邮箱／密钥／令牌，" + cleaner.Outside + " 个项目外路径。";
            return new CleanedSession { SessionId = session, Slug = slug, Title = clean, Jsonl = new UTF8Encoding(false).GetBytes(jsonl.ToString()), Record = new UTF8Encoding(false).GetBytes(record), Preview = body.ToString(), Report = Appearance.Text(report), Messages = turns.Count, Images = images, Stamp = version, Replaces = targetId != null };
        }

        // ---- 格式兼容（SPEC 写入）：不按版本号放行；目标已有记录时，至少一条须是已知格式，否则视为官方格式已变，拒绝写入 ----
        static readonly Regex KnownRecord = new Regex("^\\s*\\{\\s*\"sessionId\"\\s*:\\s*\"[^\"\\\\]{1,64}\"\\s*,\\s*\"cliSessionId\"\\s*:\\s*\"[^\"\\\\]{1,64}\"\\s*,\\s*\"cwd\"\\s*:\\s*\"(?:[^\"\\\\]|\\\\.)*\"\\s*,\\s*\"originCwd\"\\s*:");
        internal static bool FormatKnown(string records) {
            try { return FormatKnownIn(records); } catch (IOException) { return false; } catch (UnauthorizedAccessException) { return false; }
        }
        static bool FormatKnownIn(string records) {
            bool any = false; int entries = 0;
            foreach (string path in Directory.EnumerateFiles(records, "local_*.json")) {
                if (++entries > 5000) return false;
                FileStream stream;
                any = true;
                // 打不开（不属于本人、链接、超限）的记录算未知格式，不当作“没有记录”放行。
                try { stream = LocalFiles.OpenRead(path, 8 * 1024 * 1024, 7); } catch (IOException) { continue; } catch (UnauthorizedAccessException) { continue; }
                using (stream) {
                    var head = new byte[(int)Math.Min(4096, stream.Length)]; int count = 0, n;
                    while (count < head.Length && (n = stream.Read(head, count, head.Length - count)) > 0) count += n;
                    if (KnownRecord.IsMatch(Encoding.UTF8.GetString(head, 0, count))) return true;
                }
            }
            return !any;
        }
        // 写入位置的所有者：记录目录及其上两级（账号、claude-code-sessions），项目目录及其上一级。启动前的存储检查不覆盖这些目录。
        internal static bool TargetOwned(string records, string config, string slug) {
            try {
                string account = Path.GetDirectoryName(records), projects = Path.Combine(config, "projects");
                foreach (string directory in new[] { records, account, Path.GetDirectoryName(account), projects, Path.Combine(projects, slug) })
                    if (Directory.Exists(directory) && !Storage.OwnedByMe((SecurityIdentifier)Directory.GetAccessControl(directory, AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)))) return false;
                return true;
            } catch (IOException) { return false; } catch (UnauthorizedAccessException) { return false; }
        }

        // ---- 目录：候选里可能是同一目录（从商店版派生的进程会继承文件重定向），按文件身份去重 ----
        internal static string[] DistinctExisting(IEnumerable<string> paths) {
            var seen = new HashSet<string>(); var result = new List<string>();
            foreach (string path in paths) if (Directory.Exists(path) && seen.Add(LocalFiles.Identity(path))) result.Add(path);
            return result.ToArray();
        }
        internal static string ConfigFolder(int number, string profile, string roaming, string local) {
            if (number == 1) return Path.Combine(profile, ".claude");
            var found = DistinctExisting(ProfileDeletion.Candidates(number, roaming, local).Select(Core.CodeConfig));
            return found.Length == 1 ? found[0] : null;
        }
        // 一层账号或组织目录：只认 GUID 名的子目录；链接、GUID 名的文件或其他名字的目录都算不安全，返回 null。
        // reasons 不为 null 时记下判为不安全的条目（只记名字，最多 40 个字符）。
        static List<string> Children(string parent, List<string> reasons = null) {
            var names = new List<string>(); int entries = 0; Guid id;
            Func<string, string, List<string>> reject = (entry, why) => { if (reasons != null) { string name = Path.GetFileName(entry); reasons.Add(why + "「" + (name.Length > 40 ? name.Substring(0, 40) + "…" : name) + "」"); } return null; };
            foreach (string entry in Directory.EnumerateFileSystemEntries(parent)) {
                if (++entries > 20000) return reject(parent, "条目超过 20000 个");
                var attributes = File.GetAttributes(entry); bool directory = (attributes & FileAttributes.Directory) != 0;
                if ((attributes & FileAttributes.ReparsePoint) != 0) return reject(entry, "链接");
                if (!Guid.TryParse(Path.GetFileName(entry), out id)) { if (directory) return reject(entry, "名字不是 GUID 的文件夹"); continue; }
                if (!directory) return reject(entry, "GUID 名的文件");
                names.Add(Path.GetFileName(entry));
            }
            return names;
        }
        // 账号层可能有多次登录；同一组织出现在两个账号目录下仍有歧义，拒绝。total 是全部组织目录数，不安全时为 0。
        static string OrganizationDirectory(string root, string organization, out int total) {
            total = 0; string found = null; int count = 0;
            var accounts = Children(root);
            if (accounts == null) return null;
            foreach (string account in accounts) {
                var organizations = Children(Path.Combine(root, account));
                if (organizations == null) return null;
                count += organizations.Count;
                string match = organization == null ? null : organizations.FirstOrDefault(o => String.Equals(o, organization, StringComparison.OrdinalIgnoreCase));
                if (match == null) continue;
                if (found != null) return null;
                found = Path.Combine(root, account, match);
            }
            total = count;
            return found;
        }
        static string[] SessionRoots(IEnumerable<string> profiles) { return DistinctExisting(profiles.Select(p => Path.Combine(p, "claude-code-sessions"))); }
        // organization 为 null 时须只有一个账号／组织目录；否则只取该组织的同名目录。
        internal static string RecordsDirectory(IEnumerable<string> profiles, string organization = null) {
            var roots = SessionRoots(profiles);
            if (roots.Length != 1) return null;
            int total;
            if (organization != null) return OrganizationDirectory(roots[0], organization, out total);
            var accounts = Children(roots[0]);
            if (accounts == null || accounts.Count != 1) return null;
            string account = Path.Combine(roots[0], accounts[0]); var organizations = Children(account);
            return organizations == null || organizations.Count != 1 ? null : Path.Combine(account, organizations[0]);
        }
        // 新账号提示只看目录名：有唯一的账号／组织目录才算就绪，不读组织标识。
        internal static bool TargetReady(IEnumerable<string> profiles) {
            try { return RecordsDirectory(profiles) != null; } catch (IOException) { return false; } catch (UnauthorizedAccessException) { return false; }
        }
        internal delegate string OrganizationReader(string profile, out string why);
        // 决定写入位置：目录唯一或没有目录时不调用 read（它会解密组织标识）；多套目录时只认当前组织的同名目录。
        internal static bool ResolveTarget(string[] profiles, OrganizationReader read, out string organization, out string failure) {
            organization = null; failure = null;
            try {
                if (RecordsDirectory(profiles) != null) return true;
                var roots = SessionRoots(profiles); int total = 0;
                if (roots.Length == 1) OrganizationDirectory(roots[0], null, out total);
                if (total == 0) { failure = "目标账号还没有可用的本地记录目录。请先在该账号正常创建一次本地会话。"; return false; }
                string why = null, current = read(Path.GetDirectoryName(roots[0]), out why);
                if (current == null) { failure = "目标账号有多套账号／组织记录，要读到它当前的组织才能确定写入位置，但这次没有读到（" + Appearance.Text(why ?? "组织记录不可读") + "）。"; return false; }
                if (RecordsDirectory(profiles, current) == null) { failure = "目标账号当前的组织还没有可用的本地记录目录。请先在该账号正常创建一次本地会话。"; return false; }
                organization = current; return true;
            }
            catch (IOException) { failure = "目标账号的记录目录已变化或不安全，未写入。请重新同步。"; return false; }
            catch (UnauthorizedAccessException) { failure = "目标账号的记录目录已变化或不安全，未写入。请重新同步。"; return false; }
        }

        // ---- 发布：同目录临时文件，再用不替换的 File.Move；只给本人权限 ----
        static string WriteTemporary(string directory, byte[] data) {
            string temp = Path.Combine(directory, ".claude-launcher-session-" + Guid.NewGuid().ToString("N"));
            var security = new FileSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User, FileSystemRights.FullControl, AccessControlType.Allow));
            try { using (var stream = new FileStream(temp, FileMode.CreateNew, FileSystemRights.Write, FileShare.None, 4096, FileOptions.None, security)) { stream.Write(data, 0, data.Length); stream.Flush(true); } }
            catch { Remove(temp); throw; }
            return temp;
        }
        static void Remove(string path) { try { if (path != null && File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        static bool TryMove(string from, string to) { try { File.Move(from, to); return true; } catch (IOException) { return false; } catch (UnauthorizedAccessException) { return false; } }
        static string PublishExclusive(string directory, string name, byte[] data, Func<string> guard) {
            string temp = null;
            try {
                temp = WriteTemporary(directory, data);
                string failure = guard();
                if (failure != null) return failure;
                try { File.Move(temp, Path.Combine(directory, name)); } catch (IOException) { return "无法原子发布新文件，未覆盖任何同名文件。"; }
                return null;
            }
            catch (IOException) { return "写入失败，原有文件未覆盖。"; }
            catch (UnauthorizedAccessException) { return "写入失败，原有文件未覆盖。"; }
            finally { Remove(temp); }
        }
        static bool ValidTarget(string session, string slug) { Guid id; return Guid.TryParse(session, out id) && session.Length == 36 && slug != null && Regex.IsMatch(slug, "^[A-Za-z0-9-]{1,200}$"); }
        static string Records(IEnumerable<string> profiles, string organization) {
            try { return RecordsDirectory(profiles, organization); } catch (IOException) { return null; } catch (UnauthorizedAccessException) { return null; }
        }
        internal static string Publish(IEnumerable<string> profiles, string organization, string config, string slug, string session, byte[] jsonl, byte[] record, Func<string> guard) {
            string failure = guard();
            if (failure != null) return failure;
            if (!ValidTarget(session, slug)) return "会话标识或项目目录名无效。";
            if (config == null) return "目标账号的项目目录无法安全打开，未写入。";
            string records = Records(profiles, organization);
            if (records == null) return "目标账号的记录目录已变化或不安全，未写入。请重新同步。";
            if (!TargetOwned(records, config, slug)) return "目标账号的会话目录不属于当前用户，未写入。";
            if (!FormatKnown(records)) return "目标账号的会话记录格式与启动器不兼容（Claude 可能改了格式），未写入。";
            string folder = Path.Combine(config, "projects", slug), name = session + ".jsonl";
            try {
                using (new LocalFiles(config, false)) using (new LocalFiles(records, false)) {
                    // 逐级先查重解析点再创建，不会穿过链接建目录。
                    using (new LocalFiles(folder)) {
                        failure = PublishExclusive(folder, name, jsonl, guard);
                        if (failure != null) return failure;
                        failure = PublishExclusive(records, "local_" + session + ".json", record, guard);
                        if (failure != null) File.Delete(Path.Combine(folder, name));
                        return failure;
                    }
                }
            }
            catch (IOException) { return failure ?? "目标账号的项目目录无法安全打开，未写入。"; }
            catch (UnauthorizedAccessException) { return failure ?? "目标账号的项目目录无法安全打开，未写入。"; }
        }

        // ---- 替换（SPEC 写入）：目标会话 ID 不变，旧版本进回收站 ----
        static readonly Regex RecordHead = new Regex("^\\s*\\{\\s*\"sessionId\"\\s*:\\s*\"[^\"\\\\]{1,64}\"\\s*,\\s*\"cliSessionId\"\\s*:\\s*\"([^\"\\\\]{1,64})\"");
        static readonly string[] RecordKeys = { "sessionId", "cliSessionId", "cwd", "originCwd", "title", "createdAt", "lastActivityAt", "isArchived" };
        // 按 cliSessionId 找目标自己的记录（官方建的记录名字可能不是 local_<ID>.json）；其他记录只读开头 512 字节。
        // 打不开、链接、多链接、过大或解析失败的 local_*.json 都设 unsafeFile：它可能就是这一条。
        static string RecordFor(string records, string session, out Dictionary<string, object> value, out bool unsafeFile) {
            value = null; unsafeFile = false;
            foreach (string path in Directory.EnumerateFileSystemEntries(records, "local_*")) {
                string name = Path.GetFileName(path);
                if (!name.EndsWith(".json", StringComparison.Ordinal)) continue;
                FileStream stream;
                try { stream = LocalFiles.OpenRead(path, 8 * 1024 * 1024, 7); }
                catch (IOException) { unsafeFile = true; return null; } catch (UnauthorizedAccessException) { unsafeFile = true; return null; }
                using (stream) {
                    var head = new byte[(int)Math.Min(512, stream.Length)]; int count = 0, n;
                    while (count < head.Length && (n = stream.Read(head, count, head.Length - count)) > 0) count += n;
                    Match match = RecordHead.Match(Encoding.UTF8.GetString(head, 0, count));
                    if (!match.Success || match.Groups[1].Value != session) continue;
                    stream.Seek(0, SeekOrigin.Begin);
                    var data = new byte[stream.Length]; count = 0;
                    while (count < data.Length && (n = stream.Read(data, count, data.Length - count)) > 0) count += n;
                    try { value = Json().DeserializeObject(new UTF8Encoding(false, true).GetString(data, 0, count)) as Dictionary<string, object>; }
                    catch (ArgumentException) { } catch (InvalidOperationException) { }
                    if (value == null) { unsafeFile = true; return null; }
                    return name;
                }
            }
            return null;
        }
        // 识别导入只读固定前缀，所以已知字段排在前面，其余按名字排序。
        static byte[] RecordData(Dictionary<string, object> record) {
            var keys = RecordKeys.Where(record.ContainsKey).Concat(record.Keys.Except(RecordKeys).OrderBy(k => k, StringComparer.Ordinal));
            return new UTF8Encoding(false).GetBytes("{" + String.Join(",", keys.Select(k => Serialize(k) + ":" + Serialize(record[k]))) + "}");
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool MoveFileEx(string from, string to, uint flags);
        // stamp：来源的最后写入 Ticks；目标比它新时拒绝。recycle 把文件送进回收站，成功返回 true。
        // 顺序：写好两个临时文件 → 再查目标未运行、目标文件未变 → 发布记录（失败则什么都没变）→ 旧正文同目录改名留底 →
        // 发布新正文（失败则放回）→ 留底送回收站（失败则换回旧版本）。记录已改的标题和时间是接受的残留，和 macOS 相同。
        internal static string Replace(IEnumerable<string> profiles, string organization, string config, string slug, string session, byte[] jsonl, byte[] record, long stamp, Func<string, bool> recycle, Func<string> guard) {
            string failure = guard();
            if (failure != null) return failure;
            if (!ValidTarget(session, slug)) return "会话标识或项目目录名无效。";
            string records = config == null ? null : Records(profiles, organization);
            if (records == null) return "目标账号的记录目录已变化或不安全，未写入。请重新同步。";
            if (!TargetOwned(records, config, slug)) return "目标账号的会话目录不属于当前用户，未写入。";
            if (!FormatKnown(records)) return "目标账号的会话记录格式与启动器不兼容（Claude 可能改了格式），未写入。";
            string folder = Path.Combine(config, "projects", slug), target = Path.Combine(folder, session + ".jsonl"), freshName = "local_" + session + ".json";
            string transcriptTemp = null, recordTemp = null, aside = null;
            try {
                using (new LocalFiles(config, false)) using (new LocalFiles(records, false)) using (new LocalFiles(folder, false)) {
                    string before = LocalFiles.FileState(target);
                    if (before == null) return "目标账号里的旧版本无法安全打开，未替换。";
                    if (File.GetLastWriteTimeUtc(target).Ticks > stamp) return "目标账号里的这条会话比来源更新，未替换。";
                    Dictionary<string, object> existing; bool unsafeFile;
                    string recordName = RecordFor(records, session, out existing, out unsafeFile);
                    if (unsafeFile) return "目标账号的记录目录里有无法安全读取的文件，未替换。";
                    // 这个名字下是另一条会话的记录，不能覆盖。
                    if (recordName == null && (File.Exists(Path.Combine(records, freshName)) || Directory.Exists(Path.Combine(records, freshName)))) return "目标账号里已有同名记录，但不属于这条会话，未替换。";
                    byte[] recordBytes = record;
                    if (recordName != null) {
                        var fresh = (Dictionary<string, object>)Json().DeserializeObject(Encoding.UTF8.GetString(record));
                        existing["title"] = fresh["title"]; existing["lastActivityAt"] = fresh["lastActivityAt"];
                        recordBytes = RecordData(existing);
                    }
                    transcriptTemp = WriteTemporary(folder, jsonl); recordTemp = WriteTemporary(records, recordBytes);
                    failure = guard();
                    if (failure != null) return failure;
                    if (LocalFiles.FileState(target) != before) return "目标账号里的这条会话刚有变化，未替换。";
                    bool recordDone = recordName != null ? MoveFileEx(recordTemp, Path.Combine(records, recordName), 0x1 | 0x8) : TryMove(recordTemp, Path.Combine(records, freshName));
                    if (!recordDone) return "无法更新目标账号的记录，未替换。";
                    recordTemp = null;
                    aside = Path.Combine(folder, ".claude-launcher-old-" + Guid.NewGuid().ToString("N"));
                    if (!TryMove(target, aside)) { aside = null; return "无法把旧版本移到回收站，未替换。"; }
                    if (BeforePublish != null) BeforePublish(target);
                    if (!TryMove(transcriptTemp, target)) {
                        if (TryMove(aside, target)) { aside = null; return "无法原子发布新文件，旧版本已放回。"; }
                        return Kept(ref aside, "新版本发布失败，旧版本保留在同目录的 {0}，需要手动改回原名。");
                    }
                    transcriptTemp = null;
                    bool recycled; try { recycled = recycle(aside) && !File.Exists(aside); } catch (IOException) { recycled = false; } catch (UnauthorizedAccessException) { recycled = false; } catch (COMException) { recycled = false; }
                    if (!recycled) {
                        string undo = Path.Combine(folder, ".claude-launcher-session-" + Guid.NewGuid().ToString("N"));
                        if (TryMove(target, undo)) {
                            if (TryMove(aside, target)) { aside = null; transcriptTemp = undo; return "无法把旧版本移到回收站，未替换。"; }
                            TryMove(undo, target);
                        }
                        return Kept(ref aside, "旧版本没能移到回收站，保留在同目录的 {0}。");
                    }
                    aside = null;
                    return null;
                }
            }
            catch (IOException) { return "目标账号里的旧版本无法安全打开，未替换。"; }
            catch (UnauthorizedAccessException) { return "目标账号里的旧版本无法安全打开，未替换。"; }
            finally {
                // 意外中断时，正文不在原位就把留底放回。
                if (aside != null && !File.Exists(target)) TryMove(aside, target);
                Remove(transcriptTemp); Remove(recordTemp);
            }
        }
        internal static Action<string> BeforePublish; // 只供自检制造“新正文发布失败”，正式运行为 null。
        static string Kept(ref string aside, string format) { string name = Path.GetFileName(aside); aside = null; return String.Format(format, name); }
        // 让目标正文的修改时间等于来源，两边因此读作同一版本。
        internal static void SetDate(string config, string slug, string session, long stamp) {
            if (config == null || !ValidTarget(session, slug)) return;
            string folder = Path.Combine(config, "projects", slug), path = Path.Combine(folder, session + ".jsonl");
            try { using (new LocalFiles(folder, false)) if (LocalFiles.FileState(path) != null) File.SetLastWriteTimeUtc(path, new DateTime(stamp, DateTimeKind.Utc)); }
            catch (IOException) { } catch (UnauthorizedAccessException) { } catch (ArgumentOutOfRangeException) { }
        }
    }
}
