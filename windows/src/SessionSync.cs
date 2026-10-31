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
    internal sealed class SyncSessionRow { public string Path, Title, ProjectPath, ProjectName; public DateTime Modified; public int Account; }
    internal sealed class CleanedSession { public string Error, SessionId, Slug, Title, Preview, Report; public byte[] Jsonl, Record; public int Messages, Images, From; }
    // 同步会话（SPEC 项目清单 / 同步会话）：只读来源，从白名单重新生成清理副本，只新增不覆盖。不启动 CLI、不发请求。
    internal static class SessionSync {
        const long MaxSourceBytes = 512L * 1024 * 1024;
        const int TailBytes = 256 * 1024, MaxSessions = 2000, MaxSlug = 200;
        const string Publisher = "__pzs8sxrjxfjjc";
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
        static bool SessionFileName(string name) { Guid id; return name.EndsWith(".jsonl", StringComparison.Ordinal) && name.Length == 42 && Guid.TryParse(name.Substring(0, 36), out id); }
        internal static List<SyncSessionRow> List(string config, string project) {
            var rows = new List<SyncSessionRow>();
            string slug = Slug(project); if (slug == null) return rows;
            try {
                var projects = new DirectoryInfo(Path.Combine(config, "projects")); var folder = new DirectoryInfo(Path.Combine(projects.FullName, slug));
                if (!projects.Exists || !folder.Exists || ((projects.Attributes | folder.Attributes) & FileAttributes.ReparsePoint) != 0) return rows;
                foreach (var file in folder.EnumerateFiles("*.jsonl")) {
                    if (rows.Count >= MaxSessions) break;
                    if (!SessionFileName(file.Name) || (file.Attributes & FileAttributes.ReparsePoint) != 0 || file.Length == 0) continue;
                    rows.Add(new SyncSessionRow { Path = file.FullName, Title = TailTitle(file.FullName) ?? "未命名会话", Modified = file.LastWriteTime, ProjectPath = project });
                }
            } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return rows.OrderByDescending(r => r.Modified).ToList();
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
        static string ReadRows(string path, List<Row> rows, ref string title, ref int sidechain) {
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
        internal static CleanedSession Clean(string source, string project, string home) {
            var rows = new List<Row>(); string title = null; int sidechain = 0;
            string failure = ReadRows(source, rows, ref title, ref sidechain);
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
            turns[0].Text = "[这是从其他账号同步的清理副本：只保留对话文字和工具请求说明，工具结果、思考内容和图片都已移除。历史中说已完成的改动，请以磁盘上的文件和 git 状态为准。]\n\n" + turns[0].Text;
            if (turns[turns.Count - 1].Role != "assistant") add("assistant", "（同步的历史到此结束，上一条请求没有留下回复。）", turns[turns.Count - 1].Stamp);

            string session = Guid.NewGuid().ToString(), parentId = null;
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
            return new CleanedSession { SessionId = session, Slug = slug, Title = clean, Jsonl = new UTF8Encoding(false).GetBytes(jsonl.ToString()), Record = new UTF8Encoding(false).GetBytes(record), Preview = body.ToString(), Report = Appearance.Text(report), Messages = turns.Count, Images = images };
        }

        // ---- 版本：只放行 4.4212.0.0 商店版（官方记录格式已核对；Windows 客户端续聊未实测，见 docs/STATUS.md） ----
        internal static string[] InstalledPackages() {
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages"))
                return key == null ? new string[0] : key.GetSubKeyNames().Where(n => n.StartsWith("Claude_", StringComparison.Ordinal) && n.EndsWith(Publisher, StringComparison.Ordinal) && Regex.IsMatch(n, "_(x64|arm64|x86)__")).ToArray();
        }
        internal static bool VersionAllowed(string[] packages) { return packages.Length == 1 && Regex.IsMatch(packages[0], @"^Claude_4\.4212\.0\.0_(x64|arm64)__pzs8sxrjxfjjc$"); }

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
        static string UniqueChild(string parent) {
            string found = null; int entries = 0; Guid id;
            foreach (string entry in Directory.EnumerateFileSystemEntries(parent)) {
                if (++entries > 20000) return null;
                var attributes = File.GetAttributes(entry); bool directory = (attributes & FileAttributes.Directory) != 0;
                if ((attributes & FileAttributes.ReparsePoint) != 0) return null;
                if (!Guid.TryParse(Path.GetFileName(entry), out id)) { if (directory) return null; continue; }
                if (!directory || found != null) return null;
                found = entry;
            }
            return found;
        }
        // 目标须有唯一的账号／组织目录：多个位置时不猜身份。
        internal static string RecordsDirectory(IEnumerable<string> profiles) {
            var roots = DistinctExisting(profiles.Select(p => Path.Combine(p, "claude-code-sessions")));
            if (roots.Length != 1) return null;
            string account = UniqueChild(roots[0]);
            return account == null ? null : UniqueChild(account);
        }

        // ---- 发布：同目录临时文件，再用不替换的 File.Move；只给本人权限 ----
        static string PublishExclusive(string directory, string name, byte[] data, Func<string> guard) {
            string temp = Path.Combine(directory, ".claude-launcher-session-" + Guid.NewGuid().ToString("N")), target = Path.Combine(directory, name);
            try {
                var security = new FileSecurity();
                security.SetAccessRuleProtection(true, false);
                security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User, FileSystemRights.FullControl, AccessControlType.Allow));
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileSystemRights.Write, FileShare.None, 4096, FileOptions.None, security)) { stream.Write(data, 0, data.Length); stream.Flush(true); }
                string failure = guard();
                if (failure != null) return failure;
                try { File.Move(temp, target); } catch (IOException) { return "无法原子发布新文件，未覆盖任何同名文件。"; }
                return null;
            }
            catch (IOException) { return "写入失败，原有文件未覆盖。"; }
            catch (UnauthorizedAccessException) { return "写入失败，原有文件未覆盖。"; }
            finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
        internal static string Publish(IEnumerable<string> profiles, string config, string slug, string session, byte[] jsonl, byte[] record, Func<string> guard) {
            string failure = guard();
            if (failure != null) return failure;
            Guid id;
            if (!Guid.TryParse(session, out id) || slug == null || !Regex.IsMatch(slug, "^[A-Za-z0-9-]{1,200}$")) return "会话标识或项目目录名无效。";
            if (config == null) return "目标账号的项目目录无法安全打开，未写入。";
            string records;
            try { records = RecordsDirectory(profiles); } catch (IOException) { records = null; } catch (UnauthorizedAccessException) { records = null; }
            if (records == null) return "目标没有唯一且安全的账号／组织记录目录。请先在该账号正常创建一次本地会话；多位置时不猜测身份。";
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
    }
}
