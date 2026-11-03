using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Web.Script.Serialization;
using System.Windows.Controls;
using ClaudeLauncher;
// 同步会话：只用临时目录和合成会话，不读真实账号、不启动 Claude。
internal static class SessionSyncTests {
    static int checks;
    static void Check(bool value, string name) { checks++; if (!value) throw new Exception("session sync: " + name); }
    static string Quote(object value) { return new JavaScriptSerializer().Serialize(value); }
    static Dictionary<string, object> Message(string type, string uuid, string parent, string cwd, object content) {
        return new Dictionary<string, object> { { "type", type }, { "uuid", uuid }, { "parentUuid", parent }, { "isSidechain", false }, { "cwd", cwd }, { "timestamp", "2026-10-30T10:00:00.000Z" }, { "sessionId", "SOURCESESSIONID" }, { "entrypoint", "claude-desktop" }, { "requestId", "SOURCEREQUEST" }, { "permissionMode", "bypassPermissions" }, { "version", "SOURCEVERSION" },
            { "message", new Dictionary<string, object> { { "role", type }, { "model", "SOURCEMODEL" }, { "id", "SOURCEMSG" }, { "usage", new Dictionary<string, object> { { "input_tokens", 1 } } }, { "content", content } } } };
    }
    static Dictionary<string, object> Block(params object[] pairs) { var map = new Dictionary<string, object>(); for (int i = 0; i < pairs.Length; i += 2) map[(string)pairs[i]] = pairs[i + 1]; return map; }
    internal static void Run(LauncherUI ui) {
        string root = Path.Combine(Path.GetTempPath(), "css-" + Guid.NewGuid().ToString("N").Substring(0, 8)); // 短名：夹具路径要留在 MAX_PATH 内。
        try { Fixture(root, ui); Console.WriteLine("SESSION SYNC PASS: " + checks + " checks (temporary fixtures only)"); }
        finally {
            foreach (string link in new[] { "same-as-profile" }) try { Directory.Delete(Path.Combine(root, link)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
    static void Fixture(string root, LauncherUI ui) {
        // 目录名规则：与本机真实目录名核对过的样例。
        Check(SessionSync.Slug(@"D:\Claude-MultiLauncher") == "D--Claude-MultiLauncher", "slug keeps the drive letter and doubles the separator");
        Check(SessionSync.Slug(@"C:\Users\x\AppData\Local\claude_pzs") == "C--Users-x-AppData-Local-claude-pzs", "slug replaces underscore");
        Check(SessionSync.Slug(@"D:\a b\中.x") == "D--a-b---x", "slug counts UTF-16 units");
        Check(SessionSync.Slug(@"D:\" + new string('a', 198)) == null && SessionSync.Slug(@"D:\" + new string('a', 197)) != null, "slug length limit");
        // 项目内外：大小写、正反斜杠、.. 和相似前缀。
        Check(SessionSync.Within(SessionSync.Normalize(@"d:\PROJ/src\a.cs"), @"D:\proj") == "src/a.cs", "case and slashes are equivalent");
        Check(SessionSync.Within(SessionSync.Normalize(@"D:\proj\..\other\a"), @"D:\proj") == null, "dot-dot leaves the project");
        Check(SessionSync.Within(SessionSync.Normalize(@"D:\proj\x\..\a"), @"D:\proj") == "a", "dot-dot inside stays in");
        Check(SessionSync.Within(SessionSync.Normalize(@"D:\project2\a"), @"D:\proj") == null, "similar prefix is outside");
        Check(SessionSync.Within(SessionSync.Normalize(@"D:\proj"), @"D:\proj") == "", "root itself");
        Check(SessionSync.Normalize(@"\\server\share\a") == null && SessionSync.Normalize("/etc/passwd") == null && SessionSync.Normalize(@"D:\..\..\x") == null, "UNC, posix and escaping paths are rejected");
        // 版本白名单：只放行唯一的 4.4212.0.0 商店包。
        Check(SessionSync.VersionAllowed(new[] { "Claude_4.4212.0.0_x64__pzs8sxrjxfjjc" }), "verified version");
        Check(!SessionSync.VersionAllowed(new[] { "Claude_4.4213.0.0_x64__pzs8sxrjxfjjc" }) && !SessionSync.VersionAllowed(new string[0]), "other version or no package");
        Check(!SessionSync.VersionAllowed(new[] { "Claude_4.4212.0.0_x64__pzs8sxrjxfjjc", "Claude_4.4213.0.0_x64__pzs8sxrjxfjjc" }), "two installed versions");
        // 标题后缀替换，不叠加。
        var fixedDate = new DateTime(2026, 10, 31, 12, 5, 0);
        Check(SessionSync.CopyTitle(SessionSync.CopyTitle("甲", fixedDate), fixedDate) == "甲（同步 10/31 12:05）", "suffix does not stack");
        foreach (string old in new[] { "甲（清理副本）", "甲（副本 10/26 12:00）", "甲（同步 10/26 12:00）", "甲 (Synced 10/26 12:00)", "甲 (Copy 10/26 12:00)", "甲 (cleaned copy)" }) Check(SessionSync.CopyTitle(old, fixedDate) == "甲（同步 10/31 12:05）", "old suffix replaced: " + old);
        Check(SessionSync.CopyTitle("甲（同步说明）", fixedDate) == "甲（同步说明）（同步 10/31 12:05）", "ordinary brackets are kept");

        string home = Path.Combine(root, "Users", "Secret Name".Replace(" ", "")), project = Path.Combine(home, "Project"), sub = Path.Combine(project, "windows");
        string profile = Path.Combine(root, "Profile"), config = Path.Combine(profile, "ClaudeCodeConfig");
        Directory.CreateDirectory(sub); Directory.CreateDirectory(Path.Combine(home, "Private"));
        project = SessionSync.Real(project); sub = SessionSync.Real(sub); home = SessionSync.Real(home);
        string folder = Path.Combine(config, "projects", SessionSync.Slug(project));
        string records = Path.Combine(profile, "claude-code-sessions", Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(folder); Directory.CreateDirectory(records);
        string outside = home + @"\Private\plan.txt", inside = project + @"\src\a.cs";
        string mixedOutside = home.ToUpperInvariant().Replace('\\', '/') + "/Private/MIXED.txt", mixedInside = project.ToLowerInvariant().Replace('\\', '/') + "/src/mixed.cs";
        var lines = new List<string>();
        Action<object> row = value => lines.Add(Quote(value));
        row(Block("type", "custom-title", "customTitle", "原标题", "sessionId", "SOURCESESSIONID"));
        row(Block("type", "api-request", "apiKey", "SOURCEAPIROW"));
        row(Message("user", "u1", null, project, "<system-reminder>INJECTED</system-reminder>第一问 me@example.com sk-abcdefghijklmnopqrstu " + outside + " " + inside + " " + mixedOutside + " " + mixedInside + " C:\\Users\\SomeoneElse\\x.txt /c/Users/GitBashName/y.txt 边界 " + home + "2\\keep Bearer abcdefghijklmnopqrstuvwx"));
        row(Message("assistant", "a1", "u1", project, new object[] { Block("type", "thinking", "thinking", "PRIVATETHOUGHT", "signature", "SOURCESIGNATURE"), Block("type", "text", "text", "答一"),
            Block("type", "tool_use", "id", "t1", "name", "Edit", "input", Block("file_path", inside, "old_string", "TOOLINPUT")),
            Block("type", "tool_use", "id", "t2", "name", "Read", "input", Block("file_path", outside)),
            Block("type", "tool_use", "id", "t3", "name", "Bash", "input", Block("command", "SECRETCOMMAND")),
            Block("type", "tool_use", "id", "t4", "name", "Read", "input", Block("file_path", project + @"\..\Private\escape.txt")),
            Block("type", "tool_use", "id", "t5", "name", "Read", "input", Block("file_path", mixedInside)),
            Block("type", "tool_use", "id", "t6", "name", "Read", "input", Block("file_path", "/etc/passwd")),
            Block("type", "tool_use", "id", "t7", "name", "Read", "input", Block("file_path", @"\\server\share\unc.txt")),
            Block("type", "tool_use", "id", "t8", "name", "Read", "input", Block("file_path", @"rel\b.cs")) }));
        row(Message("user", "r1", "a1", sub, new object[] { Block("type", "tool_result", "tool_use_id", "t1", "content", "TOOLOUTPUT"), Block("type", "image", "source", Block("data", "IMAGEDATA")) }));
        row(Message("assistant", "dead", "r1", sub, new object[] { Block("type", "text", "text", "ABANDONEDBRANCH") }));
        row(Block("type", "assistant", "uuid", "side", "parentUuid", "r1", "isSidechain", true, "cwd", project, "message", Block("role", "assistant", "content", "SIDECHAINTEXT")));
        row(Block("type", "attachment", "uuid", "q1", "parentUuid", "r1", "cwd", sub, "timestamp", "2026-10-30T10:01:00.000Z", "attachment", Block("type", "queued_command", "prompt", "排队指令", "organizationUuid", "SOURCEORG")));
        row(Block("type", "system", "subtype", "compact_boundary", "uuid", "b1", "parentUuid", null, "logicalParentUuid", "q1", "cwd", sub));
        var summary = Message("user", "s1", "b1", sub, "COMPACTSUMMARY"); summary["isCompactSummary"] = true; row(summary);
        row(Message("assistant", "a2", "s1", project, new object[] { Block("type", "text", "text", "答二") }));
        string source = Path.Combine(folder, Guid.NewGuid() + ".jsonl");
        File.WriteAllText(source, String.Join("\n", lines) + "\n{\"type\":\"user\",\"uuid\":\"partial", new UTF8Encoding(false));
        byte[] original = File.ReadAllBytes(source); DateTime originalStamp = File.GetLastWriteTimeUtc(source);
        var listed = SessionSync.List(config, project);
        Check(listed.Count == 1 && listed[0].Title == "原标题" && listed[0].Path == source, "lists session with tail title");
        Check(SessionSync.List(config, project.ToUpperInvariant()).Count == 1, "listing ignores path case");
        File.WriteAllText(Path.Combine(folder, "not-a-session.jsonl"), "x"); Check(SessionSync.List(config, project).Count == 1, "other files are not listed");

        var clean = SessionSync.Clean(source, project, home);
        Check(clean.Error == null, "clean succeeds: " + clean.Error);
        Check(File.ReadAllBytes(source).SequenceEqual(original) && File.GetLastWriteTimeUtc(source) == originalStamp, "source stays unchanged");
        string text = Encoding.UTF8.GetString(clean.Jsonl);
        string json = new JavaScriptSerializer().Deserialize<string>(Quote(text)); // 同一文本，便于同时检查转义前后。
        string homeName = Path.GetFileName(home);
        foreach (string forbidden in new[] { "SOURCE", "PRIVATETHOUGHT", "TOOLINPUT", "TOOLOUTPUT", "SECRETCOMMAND", "IMAGEDATA", "ABANDONEDBRANCH", "SIDECHAINTEXT", "COMPACTSUMMARY", "INJECTED", "me@example.com", "sk-abc", "plan.txt", "MIXED.txt", "escape.txt", "SomeoneElse", "GitBashName", "partial", "abcdefghijklmnopqrstuvwx", "server", "passwd" })
            Check(text.IndexOf(forbidden, StringComparison.OrdinalIgnoreCase) < 0 && clean.Preview.IndexOf(forbidden, StringComparison.OrdinalIgnoreCase) < 0, "not carried: " + forbidden);
        foreach (string kept in new[] { "第一问", "答一", "答二", "排队指令", "src/a.cs", "src/mixed.cs", "[项目外路径已移除]", "[已移除:邮箱]", "[已移除:密钥]", "[已移除:令牌]", "[历史工具请求：Edit，src/a.cs]", "[历史工具请求：Read，src/mixed.cs]", "[历史工具请求：Read，项目外路径已省略]", "[历史工具请求：Bash]", "[历史工具请求：Read，rel/b.cs]", "[工作目录：windows]", "[工作目录：项目根目录]", "原标题（同步 " })
            Check(clean.Preview.Contains(kept) || text.Contains(kept), "kept: " + kept);
        Check(clean.Preview.IndexOf(homeName, StringComparison.OrdinalIgnoreCase) < 0, "home name is gone");
        // 主目录按整段匹配：相似的兄弟目录不算主目录。
        string sibling = Path.Combine(folder, Guid.NewGuid() + ".jsonl");
        File.WriteAllText(sibling, Quote(Message("user", "u", null, project, @"D:\SyntheticHome2\keep d:/SYNTHETICHOME\drop.txt D:\SyntheticHome 结束")) + "\n" + Quote(Message("assistant", "v", "u", project, new object[] { Block("type", "text", "text", "好") })) + "\n", new UTF8Encoding(false));
        var boundary = SessionSync.Clean(sibling, project, @"D:\SyntheticHome").Preview;
        Check(boundary.Contains(@"D:\SyntheticHome2\keep") && !boundary.Contains("drop.txt") && !boundary.Contains(@"D:\SyntheticHome 结束") && boundary.Contains("[项目外路径已移除] 结束"), "home matches whole components, including the bare folder");
        File.Delete(sibling);
        var serializer = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
        var output = text.TrimEnd('\n').Split('\n');
        Check(output.Length == clean.Messages + 1, "one row per merged message plus title");
        var allowed = new[] { "type", "uuid", "parentUuid", "isSidechain", "sessionId", "cwd", "timestamp", "message" }; string previous = null, role = null;
        foreach (string line in output.Take(output.Length - 1)) {
            var value = serializer.Deserialize<Dictionary<string, object>>(line);
            Check(value.Keys.OrderBy(k => k, StringComparer.Ordinal).SequenceEqual(allowed.OrderBy(k => k, StringComparer.Ordinal)), "row field whitelist");
            Check((string)value["sessionId"] == clean.SessionId && (string)value["cwd"] == project, "row identity and cwd");
            Check((string)value["parentUuid"] == previous, "rebuilt linear chain"); Check((string)value["type"] != role, "roles alternate");
            var message = (Dictionary<string, object>)value["message"];
            Check(message.Keys.All(k => k == "role" || k == "type" || k == "content"), "message field whitelist");
            previous = (string)value["uuid"]; role = (string)value["type"];
        }
        Check(role == "assistant", "ends with an assistant message");
        var titleRow = serializer.Deserialize<Dictionary<string, object>>(output[output.Length - 1]);
        Check(titleRow.Count == 3 && (string)titleRow["type"] == "custom-title" && ((string)titleRow["customTitle"]).StartsWith("原标题（同步 "), "title row");
        string recordText = Encoding.UTF8.GetString(clean.Record); var record = serializer.Deserialize<Dictionary<string, object>>(recordText);
        Check(record.Keys.SequenceEqual(new[] { "sessionId", "cliSessionId", "cwd", "originCwd", "title", "createdAt", "lastActivityAt", "isArchived" }), "eight-field record in official leading order");
        Check((string)record["cliSessionId"] == clean.SessionId && (string)record["sessionId"] == "local_" + clean.SessionId && (bool)record["isArchived"] == false, "record identity");
        using (var stream = new MemoryStream(clean.Record)) Check(Projects.ReadPrefix(stream) == project, "the record stays readable by the import prefix reader");
        foreach (string forbidden in new[] { "model", "permission", "effort", "homeTenancy", "accountId", "orgId" }) Check(recordText.IndexOf(forbidden, StringComparison.OrdinalIgnoreCase) < 0, "record carries no " + forbidden);

        // U+2028 不能在输出里断行。
        string separator = Path.Combine(folder, Guid.NewGuid() + ".jsonl");
        File.WriteAllText(separator, Quote(Message("user", "u", null, project, "甲\u2028乙")) + "\n" + Quote(Message("assistant", "v", "u", project, new object[] { Block("type", "text", "text", "丙") })) + "\n", new UTF8Encoding(false));
        Check(Encoding.UTF8.GetString(SessionSync.Clean(separator, project, home).Jsonl).IndexOf('\u2028') < 0, "line separators are escaped");
        // 工作目录规则。
        Check(SessionSync.Clean(source, sub, home).Error != null, "root must be the selected project");
        string wrong = Path.Combine(folder, Guid.NewGuid() + ".jsonl");
        Func<string, string, string> two = (first, last) => Quote(Message("user", "u", null, first, "a")) + "\n" + Quote(Message("assistant", "v", "u", last, new object[] { Block("type", "text", "text", "b") })) + "\n";
        File.WriteAllText(wrong, two(project, home)); Check(SessionSync.Clean(wrong, project, home).Error != null, "a session ending outside the project is refused");
        File.WriteAllText(wrong, two(project, project.ToUpperInvariant().Replace('\\', '/') + "/WINDOWS")); Check(SessionSync.Clean(wrong, project, home).Error == null, "case and slash do not move a session out");
        File.WriteAllText(wrong, two(project, project + @"\..\Private")); Check(SessionSync.Clean(wrong, project, home).Error != null, "dot-dot cannot fake the last directory");
        string scratch = Path.Combine(home, "scratch-gone");
        File.WriteAllText(wrong, two(scratch, project)); var moved = SessionSync.Clean(wrong, project, home);
        Check(moved.Error == null && moved.Preview.Contains("[工作目录：项目外]") && !moved.Preview.Contains("scratch-gone") && !Encoding.UTF8.GetString(moved.Jsonl).Contains("scratch-gone"), "a session moved into the project is accepted without its old path");
        File.WriteAllText(wrong, "not json\n"); Check(SessionSync.Clean(wrong, project, home).Error != null, "unknown line is refused");
        File.WriteAllText(wrong, Quote(Block("type", "custom-title", "customTitle", "x")) + "\n"); Check(SessionSync.Clean(wrong, project, home).Error != null, "no conversation is refused");
        // 项目内的目录链接指向外面：按真实路径算项目外。
        string outsideDir = Path.Combine(home, "Private"), junction = Path.Combine(project, "linked");
        var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c mklink /J \"" + junction + "\" \"" + outsideDir + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
        mklink.WaitForExit();
        Check(Directory.Exists(junction), "junction fixture");
        Check(SessionSync.Within(SessionSync.Real(junction + @"\x.txt"), project) == null, "a link inside the project resolves outside");
        File.WriteAllText(wrong, two(project, junction)); Check(SessionSync.Clean(wrong, project, home).Error != null, "a linked last directory is refused");
        File.WriteAllText(wrong, Quote(Message("user", "u", null, project, "看 " + junction + @"\x.txt")) + "\n" + Quote(Message("assistant", "v", "u", project, new object[] { Block("type", "tool_use", "name", "Read", "input", Block("file_path", junction + @"\y.txt")) })) + "\n");
        var linked = SessionSync.Clean(wrong, project, home);
        Check(linked.Error == null && linked.Preview.Contains("[项目外路径已移除]") && linked.Preview.Contains("[历史工具请求：Read，项目外路径已省略]") && !linked.Preview.Contains(@"x.txt"), "linked paths in text and tool notes count as outside");
        Directory.Delete(junction);

        // 发布：运行中拒绝、任一步失败不留文件、只新增不覆盖、只有本人权限。
        string target = Path.Combine(config, "projects", "new-project-slug"), jsonlFile = Path.Combine(target, clean.SessionId + ".jsonl"), recordFile = Path.Combine(records, "local_" + clean.SessionId + ".json");
        string[] profiles = { profile };
        Func<string> safe = () => null; int calls;
        Check(SessionSync.Publish(profiles, config, "new-project-slug", clean.SessionId, clean.Jsonl, clean.Record, () => "running") == "running" && !File.Exists(jsonlFile), "running target refused");
        calls = 0; Check(SessionSync.Publish(profiles, config, "new-project-slug", clean.SessionId, clean.Jsonl, clean.Record, () => ++calls >= 2 ? "started" : null) == "started", "target started before the transcript is published");
        Check(!File.Exists(jsonlFile) && !File.Exists(recordFile), "late guard failure leaves neither file");
        calls = 0; Check(SessionSync.Publish(profiles, config, "new-project-slug", clean.SessionId, clean.Jsonl, clean.Record, () => ++calls >= 3 ? "started" : null) == "started", "target started before the record is published");
        Check(!File.Exists(jsonlFile) && !File.Exists(recordFile), "record failure removes only the new transcript");
        File.WriteAllText(recordFile, "existing record");
        Check(SessionSync.Publish(profiles, config, "new-project-slug", clean.SessionId, clean.Jsonl, clean.Record, safe) != null, "an existing record is not overwritten");
        Check(File.ReadAllText(recordFile) == "existing record" && !File.Exists(jsonlFile), "record collision keeps the old record and rolls the transcript back");
        File.Delete(recordFile);
        Check(SessionSync.Publish(profiles, config, "new-project-slug", clean.SessionId, clean.Jsonl, clean.Record, safe) == null, "publish succeeds");
        Check(File.ReadAllBytes(jsonlFile).SequenceEqual(clean.Jsonl) && File.ReadAllBytes(recordFile).SequenceEqual(clean.Record), "exact bytes published");
        var me = WindowsIdentity.GetCurrent().User;
        foreach (string file in new[] { jsonlFile, recordFile }) {
            var security = File.GetAccessControl(file); var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
            Check(security.AreAccessRulesProtected && rules.Count == 1 && rules[0].IdentityReference.Equals(me), "private file: " + Path.GetFileName(file));
        }
        Check(SessionSync.Publish(profiles, config, "new-project-slug", clean.SessionId, Encoding.UTF8.GetBytes("other"), clean.Record, safe) != null && File.ReadAllBytes(jsonlFile).SequenceEqual(clean.Jsonl), "never overwrites the transcript");
        Check(Directory.GetFiles(target).Length == 1 && Directory.GetFiles(records).Length == 1, "no temporary files left");
        Check(SessionSync.Publish(profiles, config, "..\\escape", Guid.NewGuid().ToString(), clean.Jsonl, clean.Record, safe) != null && SessionSync.Publish(profiles, config, "x", "not-a-guid", clean.Jsonl, clean.Record, safe) != null, "invalid slug or session id refused");
        Check(SessionSync.Publish(new[] { Path.Combine(root, "missing") }, config, "x", Guid.NewGuid().ToString(), clean.Jsonl, clean.Record, safe) != null, "no record directory refused");
        // 重解析点：目录链接不能作为写入目标。
        string outsideTarget = Path.Combine(root, "outside-target"), linkedSlug = Path.Combine(config, "projects", "linked-slug"); Directory.CreateDirectory(outsideTarget);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c mklink /J \"" + linkedSlug + "\" \"" + outsideTarget + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true }).WaitForExit();
        string fresh = Guid.NewGuid().ToString();
        Check(Directory.Exists(linkedSlug) && SessionSync.Publish(profiles, config, "linked-slug", fresh, clean.Jsonl, clean.Record, safe) != null && Directory.GetFiles(outsideTarget).Length == 0, "a linked project folder is refused");
        Directory.Delete(linkedSlug);
        // 账号／组织目录不唯一时不猜身份。
        string second = Path.Combine(profile, "claude-code-sessions", Guid.NewGuid().ToString()); Directory.CreateDirectory(second);
        Check(SessionSync.Publish(profiles, config, "new-project-slug", Guid.NewGuid().ToString(), clean.Jsonl, clean.Record, safe) != null, "ambiguous account location refused");
        Directory.Delete(second);
        string duplicate = Path.Combine(root, "same-as-profile"); // 同一目录经两条路径出现（商店版重定向）算一个。
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c mklink /J \"" + duplicate + "\" \"" + profile + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true }).WaitForExit();
        Check(SessionSync.DistinctExisting(new[] { profile, duplicate, Path.Combine(root, "missing") }).Length == 1, "the same directory reached twice counts once");
        Directory.Delete(duplicate);

        // 英文：Windows 动态拼出的文字都能对上词条，也不提 Mac。
        int language = Appearance.Language; Appearance.Language = 1;
        try {
            foreach (string chinese in new[] { "已选 2 条", "账号 02（运行中）", "账号 02 · 10/31 12:05", "预览：同步 3 条会话到账号 02", "会话同步：账号 02 新增 1 条，替换 0 条，共选择 3 条", "共 12 KB。脱敏只认固定格式，其他私人信息请自己看一遍。", "3 条无法处理，不会同步：", "3 条无法处理，没有同步。", "已写入 2 条。打开账号 02 后，它们会出现在侧栏，标题后带“同步”和时间。第一条消息由你发送；如果项目还没被该账号信任，发送时官方会请你确认。", "打开账号 02", "展开或折叠：demo", "同步会话只支持商店版 Claude 4.4212.0.0；当前版本不匹配，已停止写入。", "保留 4 条消息，2 条工具说明。\n丢弃：1 个工具结果、1 个思考块、0 张图片、0 行子代理记录、2 行非主链记录。\n替换：1 处邮箱／密钥／令牌，1 个项目外路径。", "图文说明，随时翻阅。文档在本机打开，不联网。" }) {
                string english = Appearance.Text(chinese);
                Check(!System.Text.RegularExpressions.Regex.IsMatch(english, @"[一-鿿]") && !english.Contains("Mac"), "English text for: " + chinese + " => " + english);
            }
            Check(SessionSync.CopyTitle("甲（同步 10/26 12:00）", fixedDate) == "甲 (Synced 10/31 12:05)", "English title suffix");
        } finally { Appearance.Language = language; }

        // 选择窗：默认折叠不选、目标账号一次一个、运行中不可选、目标自己的会话不可勾选。
        var sessions = new List<SyncSessionRow> {
            new SyncSessionRow { Path = "a", Title = "甲", Account = 1, ProjectPath = @"D:\p1", ProjectName = "p1", Modified = DateTime.Now },
            new SyncSessionRow { Path = "b", Title = "乙", Account = 2, ProjectPath = @"D:\p1", ProjectName = "p1", Modified = DateTime.Now },
            new SyncSessionRow { Path = "c", Title = "丙", Account = 1, ProjectPath = @"D:\p2", ProjectName = "p2", Modified = DateTime.Now } };
        var picker = ui.BuildSessionPicker(sessions, new[] { 1, 2, 3 }, new[] { 1 });
        Check(picker.Expanders.Count == 2 && picker.Expanders.All(e => !e.IsExpanded), "projects start collapsed");
        Check(picker.Selected().Count == 0 && !picker.Confirm.IsEnabled, "nothing is selected by default");
        var items = picker.Target.Items.Cast<ComboBoxItem>().ToList();
        Check(!items[0].IsEnabled && items[1].IsEnabled && picker.TargetNumber == 2, "running account cannot be chosen; first free one is selected");
        Check(!picker.Rows[1].IsEnabled && picker.Rows[0].IsEnabled, "sessions already in the target cannot be ticked");
        picker.Toggle(picker.Members[0]); Check(picker.Selected().Select(s => s.Path).SequenceEqual(new[] { "a" }) && picker.Groups[0].IsChecked == true && picker.All.IsChecked == null && picker.Confirm.IsEnabled, "project toggle ticks usable rows");
        picker.Toggle(Enumerable.Range(0, picker.Rows.Count)); Check(picker.Selected().Count == 2 && picker.All.IsChecked == true, "select all from a mixed state");
        picker.Toggle(Enumerable.Range(0, picker.Rows.Count)); Check(picker.Selected().Count == 0 && !picker.Confirm.IsEnabled, "select all toggles off");
        picker.Rows[0].IsChecked = true; picker.Target.SelectedIndex = 2; picker.Rows[1].IsChecked = true; picker.Update();
        Check(picker.TargetNumber == 3 && picker.Rows[1].IsEnabled && picker.Selected().Count == 2, "another target re-enables its rows");
        var blocked = ui.BuildSessionPicker(sessions, new[] { 1 }, new[] { 1 });
        Check(blocked.TargetNumber == 0 && !blocked.Confirm.IsEnabled, "no free target disables confirmation");
        bool confirmed = false;
        var preview = ui.BuildSessionPreview(new List<CleanedSession> { new CleanedSession { Title = "副本", Report = "报告", Preview = "全部文字", Jsonl = new byte[200000], Images = 1, From = 1 } }, new List<string> { "「x」：坏" }, 2, () => confirmed = true);
        var previewText = LauncherTests.Children(preview).OfType<TextBox>().Select(t => t.Text).ToList();
        Check(previewText.Any(t => t.Contains("全部文字") && t.Contains("报告")) && previewText.Any(t => t.Contains("「x」：坏")) && !confirmed, "preview shows all text, the report and failures, and writes nothing");
        Check(LauncherTests.Children(preview).OfType<TextBox>().All(t => t.IsReadOnly) && LauncherTests.Children(preview).OfType<System.Windows.Controls.Button>().Single(b => b.IsCancel).IsDefault, "preview is read-only and defaults to cancel");
    }
}
