using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

[assembly: AssemblyVersion("1.6.0.0")]
[assembly: AssemblyFileVersion("1.6.0.0")]
namespace ClaudeLauncher {
    public sealed class Release {
        public string version, file, sha256, signature;
        public long size;
    }
    public static class Updates {
        public const string Version = "1.6.0.0";
        public const string DisplayVersion = "1.6";
        public const string BaseUrl = "https://www.u4139693.nyat.app:53569/claude-launcher/";
        public static readonly string Cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Claude-MultiLauncher", "updates");
        public static string PublicKey() {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ClaudeLauncher.UpdateKey"))
            using (var reader = new StreamReader(stream)) return reader.ReadToEnd();
        }
        public static byte[] Payload(Release r) { return Encoding.UTF8.GetBytes(r.version + "\n" + r.file + "\n" + r.size.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n" + r.sha256 + "\n"); }
        public static Release Verify(string json, string key) {
            if (json == null || json.Length > 16384) throw new IOException("更新清单过大或为空。");
            Release r = new JavaScriptSerializer().Deserialize<Release>(json);
            System.Version parsed;
            if (r == null || !System.Version.TryParse(r.version, out parsed) || parsed.ToString() != r.version ||
                r.file != "ClaudeLauncher-" + r.version + ".exe" || r.size < 1024 || r.size > 20 * 1024 * 1024 ||
                r.sha256 == null || !Regex.IsMatch(r.sha256, "^[a-f0-9]{64}$")) throw new IOException("更新清单格式无效。");
            using (var rsa = new RSACryptoServiceProvider()) {
                rsa.PersistKeyInCsp = false; rsa.FromXmlString(key);
                if (String.IsNullOrEmpty(r.signature) || !rsa.VerifyData(Payload(r), CryptoConfig.MapNameToOID("SHA256"), Convert.FromBase64String(r.signature))) throw new IOException("更新签名不匹配，已拒绝更新。");
            }
            return r;
        }
        public static bool IsNewer(Release r) { return new System.Version(r.version) > new System.Version(Version); }
        public static string Hash(string path) {
            using (var sha = SHA256.Create()) using (var file = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
        }
        public static void VerifyFile(string path, Release r) {
            if (new FileInfo(path).Length != r.size || Hash(path) != r.sha256) throw new IOException("更新文件大小或 SHA256 不匹配，已拒绝运行。");
        }
        static byte[] Fetch(Uri uri, int maximum) {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var request = (HttpWebRequest)WebRequest.Create(uri);
            request.Timeout = 10000; request.ReadWriteTimeout = 10000; request.AllowAutoRedirect = false;
            request.UserAgent = "ClaudeProfileLauncher/" + Version;
            using (var response = (HttpWebResponse)request.GetResponse()) {
                if (response.StatusCode != HttpStatusCode.OK || response.ContentLength > maximum) throw new IOException("更新服务返回异常或文件过大。");
                using (var stream = response.GetResponseStream()) using (var result = new MemoryStream()) {
                    byte[] buffer = new byte[8192]; int count; var time = Stopwatch.StartNew();
                    while ((count = stream.Read(buffer, 0, buffer.Length)) > 0) {
                        if (result.Length + count > maximum || time.Elapsed.TotalSeconds > 45) throw new IOException("更新下载超限或超时。");
                        result.Write(buffer, 0, count);
                    }
                    return result.ToArray();
                }
            }
        }
        public static Release Check() {
            Release release = Verify(Encoding.UTF8.GetString(Fetch(new Uri(BaseUrl + "latest.json"), 16384)), PublicKey());
            Diagnostics.Write("update_check", "available=" + release.version);
            return release;
        }
        public static string Download(Release r) {
            // 再次验证内存中的清单，下载目录只用于本用户的更新缓存。
            Verify(new JavaScriptSerializer().Serialize(r), PublicKey());
            string directory = Path.Combine(Cache, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "update.exe");
            byte[] bytes = Fetch(new Uri(BaseUrl + r.file), checked((int)r.size));
            File.WriteAllBytes(path, bytes); VerifyFile(path, r);
            File.WriteAllText(Path.Combine(directory, "release.json"), new JavaScriptSerializer().Serialize(r), Encoding.UTF8);
            return path;
        }
        public static void StartInstaller(string staged) {
            string target = Assembly.GetExecutingAssembly().Location;
            var current = Process.GetCurrentProcess();
            var plan = new InstallPlan { target = target, originalHash = Hash(target), pid = current.Id, started = current.StartTime.ToUniversalTime().Ticks };
            string directory = Path.GetDirectoryName(staged);
            File.WriteAllText(Path.Combine(directory, "plan.json"), new JavaScriptSerializer().Serialize(plan), Encoding.UTF8);
            Process.Start(new ProcessStartInfo(staged, "--apply-update") { UseShellExecute = false, CreateNoWindow = true });
        }
        public sealed class InstallPlan { public string target, originalHash; public int pid; public long started; }
        public static int Install(bool showError = true) {
            string self = Assembly.GetExecutingAssembly().Location, directory = Path.GetDirectoryName(self);
            string backup = null, target = null; bool replaced = false;
            try {
                Release release = Verify(File.ReadAllText(Path.Combine(directory, "release.json")), PublicKey()); VerifyFile(self, release);
                var plan = new JavaScriptSerializer().Deserialize<InstallPlan>(File.ReadAllText(Path.Combine(directory, "plan.json")));
                target = Path.GetFullPath(plan.target);
                if (target == self || Path.GetExtension(target).ToLowerInvariant() != ".exe" || Hash(target) != plan.originalHash ||
                    AssemblyName.GetAssemblyName(target).Name != "ClaudeLauncher") throw new IOException("更新目标发生变化，已停止。");
                try {
                    using (var old = Process.GetProcessById(plan.pid)) {
                        if (old.StartTime.ToUniversalTime().Ticks == plan.started && !old.WaitForExit(30000)) throw new IOException("启动器尚未退出，未替换文件。");
                    }
                } catch (ArgumentException) { }
                if (Hash(target) != plan.originalHash) throw new IOException("更新目标已变化。");
                string sibling = target + ".new-" + Guid.NewGuid().ToString("N"); backup = target + ".previous-" + Guid.NewGuid().ToString("N") + ".exe";
                File.Copy(self, sibling); VerifyFile(sibling, release);
                File.Replace(sibling, target, backup); replaced = true;
                string ready = Path.Combine(directory, "ready");
                using (var next = Process.Start(new ProcessStartInfo(target, "--update-ready \"" + ready + "\"") { UseShellExecute = false })) {
                    for (int i = 0; i < 100; i++) {
                        if (File.Exists(ready)) { Diagnostics.Write("update_success", "version=" + release.version + "; backup=" + backup); return 0; }
                        if (next.HasExited) throw new IOException("新版启动失败，准备恢复旧版。");
                        Thread.Sleep(200);
                    }
                    // 活进程可能只是启动缓慢；不杀进程、不在它运行时回退文件。
                    Diagnostics.Write("update_ready_timeout", "备份保留：" + backup); return 2;
                }
            } catch (Exception error) {
                Diagnostics.Error("update_install_error", error);
                if (replaced) {
                    try {
                        File.Replace(backup, target, target + ".failed-" + Guid.NewGuid().ToString("N") + ".exe");
                        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                        Diagnostics.Write("update_rollback", "已恢复旧版启动器");
                    } catch (Exception rollback) { Diagnostics.Error("update_rollback_error", rollback); }
                }
                if (showError) System.Windows.MessageBox.Show("更新未完成，账号数据未改动。\n" + error.Message + "\n日志：" + Diagnostics.DirectoryPath, "Claude 启动器");
                return 1;
            }
        }
        public static void Ready(string path) {
            if (String.IsNullOrEmpty(path)) return;
            string full = Path.GetFullPath(path);
            if (!full.StartsWith(Path.GetFullPath(Cache) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(full) != "ready") return;
            File.WriteAllText(full, Version);
        }
    }
}
