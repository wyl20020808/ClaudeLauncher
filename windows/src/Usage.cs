using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Automation;

namespace ClaudeLauncher {
    // SPEC 本地界面用量 [Windows]：只读官方 Code 页已显示的用量按钮；不点击、不展开、不注册事件，不联网。
    public sealed class UsageMetric {
        public double Percent;
        public string Reset = "";
        public DateTime Observed; // 本机观察时间（本地时间），不是服务器采样时间
    }
    public sealed class UsageRecord {
        public string Status, Identity;
        public UsageMetric FiveHour, Weekly;
        public UsageRecord Copy() { return (UsageRecord)MemberwiseClone(); }
    }
    // 合成测试节点：自检只用它，不访问真实 Claude。
    public sealed class UsageNode {
        public string Type = "Group", Name = "", Url = "";
        public bool Expandable;
        public double? Range;
        public List<UsageNode> Children = new List<UsageNode>();
    }

    // SPEC 本地界面用量 [Windows] D3：只注册 ExpandCollapseState 属性变化这一种通知；用户手动展开官方用量弹窗且该 Claude 在前台时回调一次。
    public sealed class PopupWatch : IDisposable {
        readonly AutomationElement root;
        readonly AutomationPropertyChangedEventHandler handler;
        readonly Action opened;
        readonly int processId;
        int[] open;
        internal PopupWatch() { } // 仅供自检 Accept
        PopupWatch(int pid, AutomationElement root, Action opened) {
            processId = pid; this.root = root; this.opened = opened;
            handler = Changed;
            Automation.AddAutomationPropertyChangedEventHandler(root, TreeScope.Subtree, handler, ExpandCollapsePattern.ExpandCollapseStateProperty);
        }
        // 应在后台（MTA）线程调用；没有可见窗口或注册失败返回 null，调用方不重试。
        public static PopupWatch Start(int pid, Action opened) {
            IntPtr window = Native.WindowFor(pid);
            if (window == IntPtr.Zero) return null;
            try { return new PopupWatch(pid, AutomationElement.FromHandle(window), opened); }
            catch (Exception error) { Diagnostics.Write("usage_watch_error", error.GetType().Name); return null; }
        }
        // 同一次展开的重复通知只采集一次；关闭后允许下一次手动展开。
        internal bool Accept(int[] button, bool expanded) {
            lock (this) {
                bool same = open != null && open.SequenceEqual(button);
                if (!expanded) { if (same) open = null; return false; }
                if (same) return false;
                open = button; return true;
            }
        }
        void Changed(object sender, AutomationPropertyChangedEventArgs e) {
            var element = sender as AutomationElement;
            if (element == null || e.NewValue == null) return;
            try {
                if (element.Current.ControlType != ControlType.Button || UsageInterface.ButtonMetric(element.Current.Name) == null) return;
                if (!Accept(element.GetRuntimeId(), Convert.ToInt32(e.NewValue) == (int)ExpandCollapseState.Expanded)) return;
                if (Native.ForegroundProcess() != processId || !UsageInterface.InCodePage(element)) return;
                opened();
            } catch (ElementNotAvailableException) { } catch (InvalidOperationException) { } catch (System.Runtime.InteropServices.COMException) { }
        }
        public void Dispose() {
            if (root == null) return;
            try { Automation.RemoveAutomationPropertyChangedEventHandler(root, handler); } catch (Exception) { }
        }
    }

    public static class UsageInterface {
        public const string Observed = "界面已读取";
        public static readonly string DefaultPath = Path.Combine(LauncherSettings.DataDirectory, "usage-observations.json");
        const int MaxNodes = 16000, MaxDepth = 40;
        // 这些分支是正文、输入框、列表、链接等，不进入、不读取。
        static readonly HashSet<string> Skipped = new HashSet<string> { "Text", "Edit", "Image", "Hyperlink", "Header", "HeaderItem", "Table", "Tree", "List", "DataGrid" };

        // 返回 ("fh"|"sd", 百分比, 重置说明)；未知格式、越界百分比返回 null。
        public static Tuple<string, double, string> ButtonMetric(string label) {
            if (String.IsNullOrEmpty(label) || label.Length > 512) return null;
            var patterns = new[] {
                Tuple.Create(@"^用量[：:]\s*5 小时限额的 ([0-9]{1,3}(?:\.[0-9]+)?)%(.*)$", "fh"),
                Tuple.Create(@"^Usage: ([0-9]{1,3}(?:\.[0-9]+)?)% of (?:the )?5-hour limit(.*)$", "fh"),
                Tuple.Create(@"^用量[：:]\s*每周 · 所有模型[：:]\s*([0-9]{1,3}(?:\.[0-9]+)?)%(.*)$", "sd") };
            foreach (var pattern in patterns) {
                Match match = Regex.Match(label, pattern.Item1);
                if (!match.Success) continue;
                double percent = Double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                return percent >= 0 && percent <= 100 ? Tuple.Create(pattern.Item2, percent, match.Groups[2].Value.Trim()) : null;
            }
            return null;
        }
        // 只接受 Code 首页或对话页。
        public static bool CodeUrl(string value) {
            Uri url;
            if (String.IsNullOrEmpty(value) || !Uri.TryCreate(value, UriKind.Absolute, out url)) return false;
            string path = url.AbsolutePath;
            return url.Scheme == "https" && url.Host == "claude.ai" && url.IsDefaultPort && url.UserInfo == "" && url.Query == "" && url.Fragment == "" &&
                !value.Contains("?") && !value.Contains("#") && (path == "/epitaxy" || (path.StartsWith("/epitaxy/") && path.Length > 9));
        }

        // 弹窗面板里的指标名；模型专属周限额与上下文不是账号周限额。
        static string MetricKey(string label) {
            if (label == "5 小时限额" || label == "5-hour limit") return "fh";
            if (label == "每周 · 所有模型" || label == "Weekly · all models") return "sd";
            return null;
        }
        static bool UsageSettingsUrl(string value) {
            Uri url;
            return !String.IsNullOrEmpty(value) && Uri.TryCreate(value, UriKind.Absolute, out url) && url.Scheme == "https" && url.Host == "claude.ai" &&
                url.IsDefaultPort && url.UserInfo == "" && url.AbsolutePath == "/settings/usage";
        }

        // 有界遍历（同 macOS UsageAXReader）；节点访问通过委托，便于用合成树自检。
        sealed class Walker<T> where T : class {
            public Func<T, string> Type, Name, Url;
            public Func<T, bool> Expandable;
            public Func<T, double?> Range;
            public Func<T, IList<T>> Children;
            public long Deadline;
            int count, codeAreas;
            bool limited, conflicted;
            readonly List<Tuple<string, UsageMetric>> buttons = new List<Tuple<string, UsageMetric>>();
            readonly List<Dictionary<string, UsageMetric>> panels = new List<Dictionary<string, UsageMetric>>();
            bool Budget() { if (++count > MaxNodes || Stopwatch.GetTimestamp() > Deadline) limited = true; return !limited; }
            IList<T> Kids(T node) {
                IList<T> children = Children(node);
                if (children != null && children.Count > 2000) { limited = true; return new T[0]; }
                return children ?? new T[0];
            }
            // 弹窗必须含官方用量设置链接，才读取其中的文字。
            bool HasUsageLink(T node, int depth) {
                if (depth > 12 || limited || !Budget()) return false;
                string type = Type(node);
                if (type == "Hyperlink") return UsageSettingsUrl(Url(node));
                if (type == "Document" || type == "Edit" || type == "Text" || type == "Image" || type == "Button" || type == "ProgressBar") return false;
                foreach (T child in Kids(node)) if (HasUsageLink(child, depth + 1)) return true;
                return false;
            }
            bool Panel(T node, Dictionary<string, UsageMetric> metrics, ref string reset, int depth) {
                if (depth > 12) { limited = true; return false; }
                if (!Budget()) return false;
                string type = Type(node);
                if (type == "Document" || type == "Edit") return false;
                if (type == "Hyperlink") return true;
                if (type == "Text") {
                    string text = Name(node) ?? "";
                    if (text.Length > 512) return false;
                    if (MetricKey(text) != null) reset = null;
                    else if (text.Contains("重置") || text.StartsWith("Resets ") || text.Contains("第一条消息")) reset = text;
                    return true;
                }
                if (type == "ProgressBar" || type == "Slider") {
                    string key = MetricKey(Name(node));
                    if (key == null) return true;
                    double? value = Range(node);
                    if (value == null || Double.IsNaN(value.Value) || value < 0 || value > 100 || metrics.ContainsKey(key)) return false;
                    metrics[key] = new UsageMetric { Percent = value.Value, Reset = reset ?? "" }; reset = null;
                    return true;
                }
                foreach (T child in Kids(node)) if (!Panel(child, metrics, ref reset, depth + 1)) return false;
                return !limited;
            }
            void Walk(T node, bool inPage, int depth) {
                if (limited || depth > MaxDepth || !Budget()) return;
                string type = Type(node);
                if (type == "Document") {
                    if (inPage) return; // 不进入嵌套网页
                    if (!CodeUrl(Url(node))) return; // 原生外壳的其他 Document 与 Code 页是兄弟，不是祖先
                    inPage = true; codeAreas++;
                }
                if (type == "Button") {
                    if (!inPage || !Expandable(node)) return;
                    var shown = ButtonMetric(Name(node));
                    if (shown != null) buttons.Add(Tuple.Create(shown.Item1, new UsageMetric { Percent = shown.Item2, Reset = shown.Item3 }));
                    return;
                }
                if (type == "Window" && inPage && HasUsageLink(node, 0)) {
                    var metrics = new Dictionary<string, UsageMetric>(); string reset = null;
                    if (Panel(node, metrics, ref reset, 0)) panels.Add(metrics); else conflicted = true;
                    return;
                }
                if (Skipped.Contains(type)) return;
                foreach (T child in Kids(node)) Walk(child, inPage, depth + 1);
            }
            public UsageRecord Result(T root) {
                try { Walk(root, false, 0); } catch (ElementNotAvailableException) { limited = true; } catch (InvalidOperationException) { limited = true; } catch (System.Runtime.InteropServices.COMException) { limited = true; }
                if (limited) return new UsageRecord { Status = "界面读取未完成" };
                if (buttons.Count > 1) return new UsageRecord { Status = "多个用量界面，待核对" };
                if (buttons.Count == 0) return new UsageRecord { Status = codeAreas > 0 ? "未观察到 Code 用量控件" : "未观察到 Code 用量页面" };
                string key = buttons[0].Item1; UsageMetric shown = buttons[0].Item2;
                var found = new Dictionary<string, UsageMetric> { { key, shown } };
                if (conflicted || panels.Count > 1) return new UsageRecord { Status = "界面数据待核对" };
                if (panels.Count == 1) {
                    UsageMetric same;
                    // 面板必须与按钮同项一致，才采用面板中的两项。
                    if (!panels[0].TryGetValue(key, out same) || same.Percent != shown.Percent) return new UsageRecord { Status = "界面数据待核对" };
                    found = panels[0];
                }
                UsageMetric fh, sd; found.TryGetValue("fh", out fh); found.TryGetValue("sd", out sd);
                return new UsageRecord { Status = Observed, FiveHour = fh, Weekly = sd };
            }
        }
        static long After(double seconds) { return Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency); }

        public static UsageRecord ReadSynthetic(UsageNode root, double seconds) {
            return new Walker<UsageNode> { Type = n => n.Type, Name = n => n.Name, Url = n => n.Url, Expandable = n => n.Expandable, Range = n => n.Range, Children = n => n.Children, Deadline = After(seconds) }.Result(root);
        }

        // 真实读取：UIA 只批量取控件类型与“是否支持展开”；只对 Document／链接取地址、对可展开按钮取名称，面板内才读文字与进度值。
        static UsageRecord ReadWindow(IntPtr window, double seconds) {
            var request = new CacheRequest { TreeScope = TreeScope.Element | TreeScope.Children, TreeFilter = Automation.RawViewCondition, AutomationElementMode = AutomationElementMode.Full };
            request.Add(AutomationElement.ControlTypeProperty);
            request.Add(AutomationElement.IsExpandCollapsePatternAvailableProperty);
            using (request.Activate()) {
                AutomationElement root = AutomationElement.FromHandle(window);
                return new Walker<AutomationElement> {
                    Type = e => { var t = e.Cached.ControlType; return t == null ? "" : t.ProgrammaticName.Replace("ControlType.", ""); },
                    Expandable = e => (bool)e.GetCachedPropertyValue(AutomationElement.IsExpandCollapsePatternAvailableProperty),
                    Name = e => e.Current.Name,
                    Url = e => { object p; return e.TryGetCurrentPattern(ValuePattern.Pattern, out p) ? ((ValuePattern)p).Current.Value : ""; },
                    Range = e => { object p; return e.TryGetCurrentPattern(RangeValuePattern.Pattern, out p) ? ((RangeValuePattern)p).Current.Value : (double?)null; },
                    Children = e => e.FindAll(TreeScope.Children, Condition.TrueCondition).Cast<AutomationElement>().ToList(),
                    Deadline = After(seconds)
                }.Result(root);
            }
        }

        static Thread worker; // 前一次读取未结束（Claude 无响应）时不开始新读取
        static readonly HashSet<string> prepared = new HashSet<string>();
        // 在后台线程读取一个已核实的官方主进程；首次连接该进程时可在 2.5 秒内等待网页辅助功能树准备好。
        public static UsageRecord ReadProcess(int pid, string identity) {
            IntPtr window = Native.WindowFor(pid);
            if (window == IntPtr.Zero) return new UsageRecord { Status = "未观察到窗口" };
            if (worker != null && worker.IsAlive) return new UsageRecord { Status = "界面读取未完成" };
            bool first; lock (prepared) first = prepared.Add(identity);
            UsageRecord result = null;
            var watch = Stopwatch.StartNew();
            worker = new Thread(() => {
                try {
                    UsageRecord attempt = ReadWindow(window, 1.5);
                    while (first && attempt.Status.StartsWith("未观察到 Code 用量") && watch.Elapsed.TotalSeconds < 2.2) {
                        Thread.Sleep(300);
                        attempt = ReadWindow(window, Math.Max(0.1, 2.5 - watch.Elapsed.TotalSeconds));
                    }
                    result = attempt;
                } catch (Exception error) { Diagnostics.Write("usage_read_error", error.GetType().Name); }
            }) { IsBackground = true };
            worker.SetApartmentState(ApartmentState.MTA);
            worker.Start();
            if (!worker.Join(TimeSpan.FromSeconds(first ? 3.0 : 2.0))) return new UsageRecord { Status = "界面读取未完成" };
            return result ?? new UsageRecord { Status = "界面读取未完成" };
        }

        // 用量按钮所在的最近 Document 必须是官方 Code 页。
        internal static bool InCodePage(AutomationElement element) {
            AutomationElement node = element;
            for (int depth = 0; depth < 40 && node != null; depth++) {
                node = TreeWalker.RawViewWalker.GetParent(node);
                if (node == null || node.Current.ControlType != ControlType.Document) continue;
                object p; return node.TryGetCurrentPattern(ValuePattern.Pattern, out p) && CodeUrl(((ValuePattern)p).Current.Value);
            }
            return false;
        }

        // 身份 = 编号 | PID | 进程启动时间 | 账号目录及其创建时间。任一项变化即不沿用缓存。
        public static string Identity(int number, Instance process) {
            string folder = Core.DisplayFolder(number);
            long created = 0;
            try { if (Directory.Exists(folder)) created = Directory.GetCreationTimeUtc(folder).Ticks; } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return number + "|" + process.ProcessId + "|" + process.Started.Ticks + "|" + created + "|" + folder.ToLowerInvariant();
        }

        // 合并一次读取（同 macOS MergeUsageInterface）：按钮只给一项时，另一项沿用同一身份下的上次观察值和原时间。
        public static UsageRecord Merge(UsageRecord previous, UsageRecord snapshot, string identity, DateTime now) {
            bool same = previous != null && previous.Status == Observed && identity != null && previous.Identity == identity;
            if (snapshot == null || snapshot.Status != Observed || identity == null) {
                bool unavailable = snapshot != null && (snapshot.Status.StartsWith("未观察到") || snapshot.Status == "界面读取未完成");
                return unavailable && same ? previous : snapshot ?? new UsageRecord { Status = "未观察到 Code 用量" };
            }
            var result = new UsageRecord { Status = Observed, Identity = identity };
            result.FiveHour = Pick(snapshot.FiveHour, same ? previous.FiveHour : null, now);
            result.Weekly = Pick(snapshot.Weekly, same ? previous.Weekly : null, now);
            return result;
        }
        static UsageMetric Pick(UsageMetric fresh, UsageMetric old, DateTime now) {
            if (fresh != null) return new UsageMetric { Percent = fresh.Percent, Reset = fresh.Reset, Observed = now };
            return old != null && old.Observed <= now ? old : null;
        }
        public static string TimeLabel(UsageMetric metric, DateTime now) {
            if (metric == null || metric.Observed > now) return "尚未观察到";
            return metric.Observed.ToString(metric.Observed.Date == now.Date ? "HH:mm:ss" : "MM-dd HH:mm");
        }

        // 观察记录：只存百分比、重置说明、观察时间与身份；不存页面、会话、正文或凭据。
        public static void Save(string path, IEnumerable<UsageRecord> records) {
            var items = records.Where(r => r != null && r.Status == Observed && r.Identity != null && (r.FiveHour != null || r.Weekly != null)).Select(r => {
                var item = new Dictionary<string, object> { { "identity", r.Identity } };
                if (r.FiveHour != null) item["fh"] = Store(r.FiveHour);
                if (r.Weekly != null) item["sd"] = Store(r.Weekly);
                return item;
            }).ToList();
            LauncherSettings.WriteAtomic(path, new JavaScriptSerializer().Serialize(new Dictionary<string, object> { { "version", 1 }, { "records", items } }));
        }
        static Dictionary<string, object> Store(UsageMetric m) {
            return new Dictionary<string, object> { { "percent", m.Percent }, { "reset", m.Reset }, { "observed", new DateTimeOffset(m.Observed).ToUnixTimeMilliseconds() } };
        }
        // 逐项校验；损坏或版本不符返回空。同一账号重复的记录全部丢弃。
        public static Dictionary<int, UsageRecord> Load(string path, DateTime now) {
            var result = new Dictionary<int, UsageRecord>();
            try {
                if (!File.Exists(path)) return result;
                var values = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object>;
                object version, raw;
                if (values == null || !values.TryGetValue("version", out version) || !(version is int) || (int)version != 1 || !values.TryGetValue("records", out raw) || !(raw is object[]) || ((object[])raw).Length > 10) return result;
                var duplicates = new HashSet<int>();
                foreach (object entry in (object[])raw) {
                    var item = entry as Dictionary<string, object>;
                    object identity;
                    if (item == null || !item.TryGetValue("identity", out identity) || !(identity is string)) continue;
                    Match match = Regex.Match((string)identity, @"^(10|[1-9])\|[1-9][0-9]{0,9}\|[1-9][0-9]{0,18}\|[0-9]{1,19}\|.{1,400}$");
                    if (!match.Success) continue;
                    int number = Int32.Parse(match.Groups[1].Value);
                    var record = new UsageRecord { Status = Observed, Identity = (string)identity, FiveHour = Restore(item, "fh", now), Weekly = Restore(item, "sd", now) };
                    if (record.FiveHour == null && record.Weekly == null) continue;
                    if (result.ContainsKey(number) || duplicates.Contains(number)) { result.Remove(number); duplicates.Add(number); continue; }
                    result[number] = record;
                }
            } catch (Exception) { result.Clear(); }
            return result;
        }
        static UsageMetric Restore(Dictionary<string, object> item, string key, DateTime now) {
            object raw; if (!item.TryGetValue(key, out raw)) return null;
            var metric = raw as Dictionary<string, object>;
            object percent, reset, observed;
            if (metric == null || !metric.TryGetValue("percent", out percent) || !metric.TryGetValue("reset", out reset) || !metric.TryGetValue("observed", out observed)) return null;
            if (!(percent is int || percent is decimal || percent is double) || !(reset is string) || ((string)reset).Length > 512 || !(observed is int || observed is long)) return null;
            double value = Convert.ToDouble(percent, CultureInfo.InvariantCulture);
            long ms = Convert.ToInt64(observed);
            if (value < 0 || value > 100 || ms <= 0 || ms > 253402300799000) return null;
            DateTime at = DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;
            return at > now ? null : new UsageMetric { Percent = value, Reset = (string)reset, Observed = at };
        }

        // --usage-interface-check：只输出账号编号、状态、两项是否观察到与耗时；不输出百分比、页面或正文。
        public static int Check() {
            var output = new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false)) { AutoFlush = true }; // GUI 程序无控制台句柄，不能设置 OutputEncoding
            List<Instance> running;
            try { running = Core.Scan(); } catch (Exception error) { output.WriteLine("进程扫描失败：" + error.Message); return 1; }
            for (int n = 1; n <= 10; n++) {
                var found = running.Where(x => x.Number == n).ToList();
                if (found.Count == 0) continue;
                if (found.Count > 1) { output.WriteLine("账号 " + n.ToString("00") + "：多个主进程，未读取"); continue; }
                var watch = Stopwatch.StartNew();
                UsageRecord record = ReadProcess(found[0].ProcessId, Identity(n, found[0]));
                output.WriteLine("账号 " + n.ToString("00") + "：" + record.Status + "；5小时=" + (record.FiveHour != null ? "已观察" : "无") + "；每周=" + (record.Weekly != null ? "已观察" : "无") + "；" + watch.ElapsedMilliseconds + " ms");
            }
            return 0;
        }
    }
}
