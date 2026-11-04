using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Web.Script.Serialization;

namespace ClaudeLauncher {
    // 问题诊断（SPEC 问题诊断）：只读检查已知问题，生成可读报告。
    // 报告不含对话正文、会话标题、凭据；路径只保留盘符和最后一级目录名。是否保存、复制、提交由用户决定，启动器不自动上传。
    internal sealed class SupportInput {
        public string Version = "", Windows = "";
        public bool UpdatesEnabled, ProjectsUnreadable;
        public List<LauncherUI.LocalProject> Projects = new List<LauncherUI.LocalProject>();
        public Func<string, string> ProjectProblem = p => null;   // 同步窗口不收这个项目的原因；可用时为 null
        public int[] Accounts = new int[0];
        public Func<int, string> Config = n => null;
        public Func<int, string[]> Profiles = n => new string[0];
        public Func<int, string> TargetGuard = n => null;           // 不能作为同步目标的原因；可以时为 null
        public string LinksPath, LogDirectory;
        public DateTime Now = DateTime.Now;
    }
    internal sealed class SupportResult { public string Text, Summary; public int Problems, Notices; }

    internal static class SupportReport {
        public const string IssuesNew = "https://github.com/wyl20020808/ClaudeLauncher/issues/new";
        const int MaxListed = 8;

        sealed class Finding { public bool Problem; public string Text; }
        sealed class ProjectCount { public int Listed, Archived, Missing, Elsewhere, Empty, Link, Slug, OldFiles; }
        sealed class AccountResult {
            public int Number; public string Config, Error; public int Roots;
            public SyncScan Scan = new SyncScan(); public List<SyncSessionRow> Rows = new List<SyncSessionRow>();
            public Dictionary<string, ProjectCount> Counts = new Dictionary<string, ProjectCount>(StringComparer.OrdinalIgnoreCase);
            public string Target;
        }

        // 只留盘符和最后一级目录名：D:\…\ai-media。
        internal static string Mask(string path) {
            if (String.IsNullOrEmpty(path)) return "（空）";
            string normal = path.Replace('/', '\\').TrimEnd('\\');
            string name = Path.GetFileName(normal);
            string drive = normal.Length >= 2 && normal[1] == ':' ? normal.Substring(0, 2).ToUpperInvariant() : "";
            if (normal.Length <= 3) return drive + "\\";
            string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\');
            if (String.Equals(normal, user, StringComparison.OrdinalIgnoreCase)) return "%USERPROFILE%";
            return (drive.Length > 0 ? drive + "\\…\\" : "…\\") + name;
        }
        static string Two(int n) { return n.ToString("00"); }

        internal static SupportResult Build(SupportInput input) {
            var findings = new List<Finding>();
            Action<string> problem = t => findings.Add(new Finding { Problem = true, Text = t });
            Action<string> notice = t => findings.Add(new Finding { Problem = false, Text = t });
            var body = new StringBuilder();

            // ---- 项目清单 ----
            var projects = input.Projects.Where(p => p != null && !String.IsNullOrEmpty(p.Path)).ToList();
            var label = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var usable = new List<string>();
            body.AppendLine("== 项目清单（" + projects.Count + " 个）==");
            if (input.ProjectsUnreadable) problem("项目清单文件读不了，同步窗口不会列出任何项目。请在项目页重新添加项目，或把诊断发给开发者。");
            for (int i = 0; i < projects.Count; i++) {
                var project = projects[i];
                string name = String.IsNullOrEmpty(project.Name) ? Path.GetFileName(project.Path.TrimEnd('\\')) : project.Name;
                string tag = "P" + (i + 1) + "「" + name + "」";
                if (!label.ContainsKey(project.Path)) label[project.Path] = tag;
                string why = null;
                try { why = input.ProjectProblem(project.Path); } catch (Exception error) { why = error.Message; }
                if (why == null && SessionSync.Real(project.Path) == null) why = "路径无法解析";
                if (why == null && usable.Contains(project.Path, StringComparer.OrdinalIgnoreCase)) why = "和前面的项目重复";
                if (why == null) usable.Add(project.Path);
                else if (why == "和前面的项目重复") notice(tag + " 和前面的项目是同一个目录，会合并显示。");
                else problem(tag + " 在同步窗口里不会出现：" + why + "。");
                body.AppendLine("  " + tag + "  " + Mask(project.Path) + (why == null ? "" : "  [不可用：" + why + "]"));
            }
            var roots = SessionSync.Roots(usable);
            var nameOwners = usable.GroupBy(p => Path.GetFileName(p.TrimEnd('\\')), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            // ---- 各账号 ----
            var results = new List<AccountResult>();
            foreach (int n in input.Accounts) {
                var result = new AccountResult { Number = n }; results.Add(result);
                try {
                    var profiles = input.Profiles(n) ?? new string[0];
                    result.Roots = SessionSync.DistinctExisting(profiles.Select(p => Path.Combine(p, "claude-code-sessions"))).Length;
                    result.Config = input.Config(n);
                    if (result.Config != null) result.Rows = SessionSync.ListWith(result.Config, profiles, usable, result.Scan);
                    else ScanOnly(profiles, result.Scan);
                } catch (Exception error) { result.Error = error.GetType().Name + "：" + error.Message; }
                try { result.Target = input.TargetGuard(n); } catch (Exception error) { result.Target = error.Message; }
                foreach (string project in usable) result.Counts[project] = new ProjectCount();
                foreach (var row in result.Rows) result.Counts[row.ProjectPath].Listed++;
                foreach (var pair in result.Scan.Archived) { string project = SessionSync.ProjectOf(pair.Value, roots); if (project != null) result.Counts[project].Archived++; }
                foreach (var skip in result.Scan.Skipped.Where(s => s.ProjectPath != null)) {
                    var count = result.Counts[skip.ProjectPath];
                    if (skip.Reason == "missing") { count.Missing++; if (result.Config != null && FoundElsewhere(result.Config, skip.Id)) count.Elsewhere++; }
                    else if (skip.Reason == "empty") count.Empty++;
                    else if (skip.Reason == "link") count.Link++;
                    else if (skip.Reason == "slug") count.Slug++;
                }
                if (result.Config != null) foreach (string project in usable) result.Counts[project].OldFiles = OldStyleFiles(result.Config, project);
            }

            // ---- 同步窗口会显示什么（直接回答“为什么只显示部分项目”）----
            var shown = usable.Where(p => results.Any(r => r.Counts[p].Listed > 0)).ToList();
            var hidden = usable.Where(p => !shown.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();
            foreach (string project in hidden) {
                int archived = results.Sum(r => r.Counts[project].Archived), missing = results.Sum(r => r.Counts[project].Missing), elsewhere = results.Sum(r => r.Counts[project].Elsewhere);
                int old = results.Sum(r => r.Counts[project].OldFiles), other = results.Sum(r => r.Counts[project].Empty + r.Counts[project].Link + r.Counts[project].Slug);
                var reasons = new List<string>();
                if (archived > 0) reasons.Add(archived + " 条会话在来源账号里是“已归档”（同步只列未归档的；Claude 的自动归档设置也会归档）");
                if (elsewhere > 0) reasons.Add(elsewhere + " 条会话的正文不在记录指向的位置（启动器的问题，请提交诊断）");
                if (missing - elsewhere > 0) reasons.Add((missing - elsewhere) + " 条会话找不到正文文件");
                if (other > 0) reasons.Add(other + " 条会话的正文为空、是链接或路径太长");
                if (reasons.Count == 0 && old > 0) reasons.Add("项目目录下有 " + old + " 个旧会话文件，但没有对应的官方会话记录（多为改模型或回退留下的旧段、已删除的会话）");
                if (reasons.Count == 0) reasons.Add("各账号里都没有这个项目的会话记录");
                (elsewhere > 0 ? problem : notice)(label[project] + " 在同步窗口里不显示：" + String.Join("；", reasons) + "。");
            }

            foreach (var r in results) {
                string who = "账号 " + Two(r.Number);
                if (r.Error != null) problem(who + " 检查时出错：" + r.Error);
                if (r.Config == null && r.Scan.Records > 0) problem(who + " 有会话记录，但找不到唯一的 Claude Code 配置目录（可能同时装了商店版和普通版），这个账号的会话都不会列出。");
                if (r.Roots > 1) notice(who + " 有 " + r.Roots + " 套会话记录目录（商店版和普通版都用过），列表会合并读取；作为目标时启动器会拒绝写入。");
                foreach (string text in r.Scan.Problems) problem(who + "：" + text + "。这一部分的会话不会出现在同步窗口里。");
                if (r.Scan.Unreadable > 0) notice(who + " 有 " + r.Scan.Unreadable + " 条会话记录打不开（被占用、是链接、超过 8 MiB 或不属于当前用户），这些会话不会列出。");
                if (r.Scan.Invalid > 0) notice(who + " 有 " + r.Scan.Invalid + " 条会话记录格式不对（不是合法 JSON 或缺少会话 ID、工作目录），这些会话不会列出。");
                if (r.Scan.NoProjectsFolder && r.Scan.Active.Count > 0) problem(who + " 有未归档的会话记录，但 Claude Code 配置目录里没有 projects 文件夹（或它是链接），这个账号的会话都不会列出。");
                var moved = r.Scan.Skipped.Where(s => s.Reason == "outside").Select(s => s.Cwd).Where(c => nameOwners.ContainsKey(Path.GetFileName((c ?? "").Replace('/', '\\').TrimEnd('\\'))))
                    .GroupBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
                foreach (var group in moved.Take(MaxListed)) {
                    string owner = nameOwners[Path.GetFileName(group.Key.Replace('/', '\\').TrimEnd('\\'))];
                    notice(who + " 有 " + group.Count() + " 条会话的工作目录是 " + Mask(group.Key) + "，和项目 " + label[owner] + " 同名但路径不同（项目可能搬过家或换了盘符）。它们不会归到这个项目；把这个目录也加进项目清单就能看到。");
                }
                if (r.Target != null) notice(who + " 现在不能作为同步目标：" + r.Target);
            }

            // ---- 关联记录与目标账号里的同步副本 ----
            List<Dictionary<int, string>> links = null; string linkError = null;
            if (input.LinksPath != null && File.Exists(input.LinksPath)) {
                try { links = SessionSync.LoadLinks(input.LinksPath); } catch (Exception error) { linkError = error.Message; }
            }
            if (linkError != null) problem("会话关联记录（session-links.json）读不了：" + linkError + "。同步时不会识别已同步过的会话，可能产生重复副本。");

            // ---- 正文 ----
            body.AppendLine();
            body.AppendLine("== 同步窗口 ==");
            body.AppendLine("  会显示的项目（括号里是各来源账号合计的会话数）：" + (shown.Count == 0 ? "无" :String.Join("、", shown.Select(p => label[p] + "（" + results.Sum(r => r.Counts[p].Listed) + "）"))));
            body.AppendLine("  不会显示的项目：" + (hidden.Count == 0 ? "无" : String.Join("、", hidden.Select(p => label[p]))));
            foreach (var r in results) {
                body.AppendLine();
                body.AppendLine("== 账号 " + Two(r.Number) + " ==");
                body.AppendLine("  Claude Code 配置目录：" + (r.Config == null ? "没有找到唯一的" : "有") + "；会话记录目录 " + r.Roots + " 套");
                body.AppendLine("  会话记录：共 " + r.Scan.Records + " 条；未归档 " + r.Scan.Active.Count + "，已归档 " + r.Scan.Archived.Count + "，打不开 " + r.Scan.Unreadable + "，格式不对 " + r.Scan.Invalid);
                foreach (string text in r.Scan.Problems) body.AppendLine("  [跳过] " + text);
                body.AppendLine("  按项目（可同步／已归档／缺正文／其他原因跳过／项目目录下的会话文件数）：");
                foreach (string project in usable) {
                    var c = r.Counts[project];
                    body.AppendLine("    " + label[project] + "：" + c.Listed + " ／ " + c.Archived + " ／ " + c.Missing + (c.Elsewhere > 0 ? "（其中 " + c.Elsewhere + " 条正文在别的目录）" : "") + " ／ " + (c.Empty + c.Link + c.Slug) + " ／ " + c.OldFiles);
                }
                var outside = r.Scan.Skipped.Where(s => s.Reason == "outside").GroupBy(s => (s.Cwd ?? "").Replace('/', '\\').IndexOf("\\scratch-workspaces\\", StringComparison.OrdinalIgnoreCase) >= 0 ? "临时工作区（scratch-workspaces）" : Mask(s.Cwd), StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).ToList();
                if (outside.Count > 0) {
                    body.AppendLine("  不在清单项目里的未归档会话：" + outside.Sum(g => g.Count()) + " 条，分布在 " + outside.Count + " 个目录：");
                    foreach (var g in outside.Take(MaxListed)) body.AppendLine("    " + g.Count() + " 条  " + g.Key);
                    if (outside.Count > MaxListed) body.AppendLine("    ……另有 " + (outside.Count - MaxListed) + " 个目录");
                }
                body.AppendLine("  作为同步目标：" + (r.Target == null ? "可以" : "不可以（" + r.Target + "）") + "；记录格式：" + TargetFormat(input, r.Number));
                if (links != null) {
                    var ids = links.Where(l => l.ContainsKey(r.Number)).Select(l => l[r.Number]).Where(id => id != null).ToList();
                    if (ids.Count > 0) {
                        int active = ids.Count(id => r.Scan.Active.ContainsKey(id)), archived = ids.Count(id => r.Scan.Archived.Any(a => String.Equals(a.Key, id, StringComparison.OrdinalIgnoreCase)));
                        body.AppendLine("  启动器记录的关联会话：" + ids.Count + " 条；其中未归档 " + active + "，已归档 " + archived + "，已不存在 " + (ids.Count - active - archived));
                    }
                }
            }
            body.AppendLine();
            body.AppendLine("== 最近的启动器错误 ==");
            var errors = RecentErrors(input.LogDirectory);
            if (errors.Count == 0) body.AppendLine("  无");
            foreach (string line in errors) body.AppendLine("  " + line);
            if (errors.Any(e => e.Contains("session_"))) notice("最近有同步会话相关的错误记录，详见报告末尾。");

            // ---- 汇总 ----
            int problems = findings.Count(f => f.Problem), notices = findings.Count - problems;
            var report = new StringBuilder();
            report.AppendLine("Claude 多开启动器 · 问题诊断");
            report.AppendLine("版本 " + input.Version + "；Windows " + input.Windows + "；联网更新 " + (input.UpdatesEnabled ? "开" : "关") + "；生成于 " + input.Now.ToString("yyyy-MM-dd HH:mm"));
            report.AppendLine("只读检查。不含对话内容、会话标题和凭据；路径只保留盘符和最后一级目录名。");
            report.AppendLine();
            report.AppendLine("== 结论 ==");
            if (findings.Count == 0) report.AppendLine("  没有发现已知问题。");
            foreach (var f in findings.Where(f => f.Problem)) report.AppendLine("  [问题] " + f.Text);
            foreach (var f in findings.Where(f => !f.Problem)) report.AppendLine("  [注意] " + f.Text);
            report.AppendLine();
            report.Append(body);
            string summary = findings.Count == 0 ? "没有发现已知问题" : "发现 " + problems + " 个问题、" + notices + " 个注意事项";
            return new SupportResult { Text = Diagnostics.Redact(report.ToString()), Summary = summary, Problems = problems, Notices = notices };
        }

        // 没有配置目录时仍统计记录，便于说明原因。
        static void ScanOnly(string[] profiles, SyncScan scan) { SessionSync.ListWith(Path.Combine(Path.GetTempPath(), "claude-launcher-no-config-" + Guid.NewGuid().ToString("N")), profiles, new List<string>(), scan); scan.NoProjectsFolder = false; }

        static bool FoundElsewhere(string config, string id) {
            try {
                foreach (string folder in Directory.EnumerateDirectories(Path.Combine(config, "projects")).Take(2000))
                    if (File.Exists(Path.Combine(folder, id + ".jsonl"))) return true;
            } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return false;
        }
        // 1.8.6 及更早的列法：项目目录名下的全部非空 JSONL。
        static int OldStyleFiles(string config, string project) {
            try {
                string slug = SessionSync.Slug(project.TrimEnd('\\')); if (slug == null) return 0;
                var folder = new DirectoryInfo(Path.Combine(config, "projects", slug));
                return folder.Exists ? folder.EnumerateFiles("*.jsonl").Count(f => f.Length > 0) : 0;
            } catch (IOException) { return 0; } catch (UnauthorizedAccessException) { return 0; }
        }
        static string TargetFormat(SupportInput input, int n) {
            try {
                string records = SessionSync.RecordsDirectory(input.Profiles(n));
                if (records == null) return "没有唯一的记录目录（有多套账号／组织目录时，写入前按当前组织确定；也可能还没建过本地会话）";
                return SessionSync.FormatKnown(records) ? "已知格式" : "未知格式（启动器会拒绝写入，可能是 Claude 更新改了格式）";
            } catch (Exception error) { return "检查出错：" + error.Message; }
        }
        // 最近 3 个本地诊断日志里的错误：时间、动作和第一行（已替换用户目录），最多 15 条。
        internal static List<string> RecentErrors(string directory) {
            var lines = new List<string>();
            try {
                if (directory == null || !Directory.Exists(directory)) return lines;
                var json = new JavaScriptSerializer();
                foreach (var file in new DirectoryInfo(directory).GetFiles("launcher-*.jsonl").OrderByDescending(f => f.LastWriteTimeUtc).Take(3).Reverse()) {
                    string[] content;
                    try { content = File.ReadAllLines(file.FullName, Encoding.UTF8); } catch (IOException) { continue; }
                    foreach (string line in content) {
                        Dictionary<string, object> entry;
                        try { entry = json.DeserializeObject(line) as Dictionary<string, object>; } catch (Exception) { continue; }
                        if (entry == null) continue;
                        string action = entry.ContainsKey("action") ? entry["action"] as string : null;
                        if (action == null || !(action.Contains("error") || action.Contains("fatal") || action.Contains("unhandled") || action.Contains("failed"))) continue;
                        string detail = (entry.ContainsKey("detail") ? entry["detail"] as string : "") ?? "";
                        detail = detail.Split('\n')[0].Trim(); if (detail.Length > 160) detail = detail.Substring(0, 160) + "…";
                        string utc = entry.ContainsKey("utc") ? entry["utc"] as string : "";
                        DateTime when; string time = DateTime.TryParse(utc, null, System.Globalization.DateTimeStyles.RoundtripKind, out when) ? when.ToLocalTime().ToString("MM-dd HH:mm") : "";
                        lines.Add(time + "\t" + action + "  " + Diagnostics.Redact(detail));
                    }
                }
            } catch (IOException) { } catch (UnauthorizedAccessException) { }
            // 连续相同的错误合并成一行：最后一次的时间，加上次数。
            var merged = new List<string>(); string last = null; int repeat = 0;
            foreach (string line in lines) {
                string what = line.Substring(line.IndexOf('\t') + 1);
                if (what == last) { repeat++; merged[merged.Count - 1] = line.Replace("\t", "  ") + "（连续 " + repeat + " 次）"; continue; }
                last = what; repeat = 1; merged.Add(line.Replace("\t", "  "));
            }
            return merged.Skip(Math.Max(0, merged.Count - 15)).ToList();
        }

        // 报告存到本地诊断日志目录下的 reports，保留最近 10 份。
        internal static string Save(string directory, string text, DateTime now) {
            string folder = Path.Combine(directory, "reports");
            string path = Path.Combine(folder, "diagnose-" + now.ToString("yyyyMMdd-HHmmss") + ".txt");
            LocalFiles.Write(path, text);
            foreach (var old in new DirectoryInfo(folder).GetFiles("diagnose-*.txt").OrderByDescending(f => f.Name).Skip(10)) { LocalFiles.Validate(old.FullName, 16 * 1024 * 1024); old.Delete(); }
            return path;
        }

        // GitHub 新建问题页：内容放得下就整份预填，放不下只填结论并提示粘贴（报告已复制）。true 表示整份已预填。
        internal const int MaxUrl = 7500;
        internal static string IssueUrl(SupportResult result, out bool complete) {
            string title = "[诊断] " + result.Summary;
            string full = Link(title, "```\n" + result.Text.TrimEnd() + "\n```");
            complete = full.Length <= MaxUrl;
            if (complete) return full;
            string head = result.Text; int cut = head.IndexOf("\n== ", head.IndexOf("== 结论 ==", StringComparison.Ordinal) + 1, StringComparison.Ordinal);
            if (cut > 0) head = head.Substring(0, cut);
            string note = "完整报告太长，已复制到剪贴板：请把这段说明删掉，在这里按 Ctrl+V 粘贴完整报告。\n\n";
            for (int length = head.Length; length > 0; length = length * 3 / 4) {
                string url = Link(title, note + "```\n" + head.Substring(0, length).TrimEnd() + (length < head.Length ? "\n……" : "") + "\n```");
                if (url.Length <= MaxUrl) return url;
            }
            return Link(title, note);
        }
        static string Link(string title, string body) { return IssuesNew + "?labels=bug&title=" + Uri.EscapeDataString(title) + "&body=" + Uri.EscapeDataString(body); }
    }
}
