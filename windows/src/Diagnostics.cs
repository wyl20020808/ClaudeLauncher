using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace ClaudeLauncher {
    public sealed class ProcessWatch : IDisposable {
        readonly System.Collections.Generic.Dictionary<int, Process> processes = new System.Collections.Generic.Dictionary<int, Process>();
        public void Refresh(System.Collections.Generic.IEnumerable<Instance> latest) {
            var rows = latest.ToArray();
            foreach (int id in processes.Keys.Where(id => !rows.Any(row => row.ProcessId == id)).ToArray()) { processes[id].Dispose(); processes.Remove(id); }
            foreach (var row in rows.Where(row => !processes.ContainsKey(row.ProcessId))) {
                Process process = null;
                try {
                    int slot = row.Number, id = row.ProcessId;
                    process = Process.GetProcessById(id);
                    var observed = process;
                    process.Exited += delegate {
                        try { Diagnostics.Write("account_process_exit", "slot=" + slot + "; pid=" + id + "; exitCode=" + observed.ExitCode + "; exitCode 不单独证明退出原因"); }
                        catch (Exception error) { Diagnostics.Error("process_exit_read_error", error); }
                    };
                    process.EnableRaisingEvents = true; processes.Add(id, process);
                } catch { if (process != null) process.Dispose(); }
            }
        }
        public void Dispose() { foreach (var process in processes.Values) process.Dispose(); processes.Clear(); }
    }
    public static class Diagnostics {
        internal static string LastError;
        internal static bool Disabled;
        static readonly object Gate = new object();
        internal static string DirectoryOverride = null;
        public static string DirectoryPath { get { return DirectoryOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Claude-MultiLauncher", "logs"); } }
        static readonly string SessionFile = "launcher-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Process.GetCurrentProcess().Id + ".jsonl";
        internal static string CurrentFile { get { return Path.Combine(DirectoryPath, SessionFile); } }
        public static string Redact(string text) {
            string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return (text ?? "").Replace(user, "%USERPROFILE%");
        }
        public static void Write(string action, string detail) {
            if(Disabled) return;
            try {
                lock (Gate) {
                    using (var guard = new LocalFiles(DirectoryPath)) {
                    foreach (var old in new DirectoryInfo(DirectoryPath).GetFiles("launcher-*.jsonl").OrderByDescending(f => f.LastWriteTimeUtc).Skip(9)) { LocalFiles.Validate(old.FullName, 16 * 1024 * 1024); old.Delete(); }
                    string path = Path.Combine(DirectoryPath, SessionFile);
                    LocalFiles.Validate(path, 16 * 1024 * 1024);
                    if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024) File.WriteAllText(path, "", Encoding.UTF8);
                    string line = new JavaScriptSerializer().Serialize(new { utc = DateTime.UtcNow.ToString("o"), pid = Process.GetCurrentProcess().Id, version = Updates.Version, action = action, detail = Redact(detail) });
                    File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
                    }
                }
            } catch (Exception error) { LastError = error.Message; }
        }
        public static void Error(string action, Exception error) { Write(action, error == null ? "unknown" : error.ToString()); }
        public static void OpenFolder() {
            Directory.CreateDirectory(DirectoryPath);
            Process.Start(new ProcessStartInfo(DirectoryPath) { UseShellExecute = true });
        }
    }
}
