using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Cache;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

[assembly: AssemblyVersion("1.7.2.0")]
[assembly: AssemblyFileVersion("1.7.2.0")]
// csc 直接编译时没有目标框架声明，运行时会退回旧路径规则（拒绝超过 260 字符的路径）；Claude 的缓存目录常超过此长度。
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]
namespace ClaudeLauncher {
    // SPEC 更新与安装：latest-<平台>.json。
    public sealed class Release {
        public string platform, version, file, sha256, minimumOS, signature;
        public long size;
    }
    // 1.2.0 客户端的旧清单 latest.json（单个 exe 原地替换）。仅用于继续签发兼容清单和 --apply-update。
    public sealed class LegacyRelease {
        public string version, file, sha256, signature;
        public long size;
    }
    public sealed class UpdateSource { public string Name, Base; public bool GitHub; }

    public static class Updates {
        public const string Version = "1.7.2.0";
        public const string DisplayVersion = "1.7.2";
        public const string BaseUrl = "https://www.u4139693.nyat.app:53569/claude-launcher/";
        public const string GitHubBase = "https://github.com/wyl20020808/ClaudeLauncher/releases/latest/download/";
        public const string ReleasesPage = "https://github.com/wyl20020808/ClaudeLauncher/releases";
        public const long SizeLimit = 50L * 1024 * 1024;
        static readonly string[] GitHubHosts = { "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com" };
        public static readonly string Cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Claude-MultiLauncher", "updates");
        static string CheckedFile { get { return Path.Combine(Path.GetDirectoryName(Cache), "last-update-check"); } }
        public static string PublicKey() {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ClaudeLauncher.UpdateKey"))
            using (var reader = new StreamReader(stream)) return reader.ReadToEnd();
        }

        public static string FileName(string platform, string version) {
            if (platform == "windows") return "ClaudeLauncherSetup-" + version + ".exe";
            // 只用 ASCII：GitHub 会改写附件名中的非 ASCII 字符。
            if (platform == "macos") return "ClaudeLauncher-" + version + ".dmg";
            throw new ArgumentException("未知平台：" + platform);
        }
        // 签名原文：platform、version、file、size、sha256，各行以 \n 结尾。
        public static byte[] Payload(Release r) {
            return Encoding.UTF8.GetBytes(r.platform + "\n" + r.version + "\n" + r.file + "\n" + r.size.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n" + r.sha256 + "\n");
        }
        public static Release Verify(string json, string key) { return Verify(json, key, "windows"); }
        public static Release Verify(string json, string key, string platform) {
            if (String.IsNullOrEmpty(json) || Encoding.UTF8.GetByteCount(json) > 16384) throw new IOException("更新清单过大或为空。");
            Release r;
            try { r = new JavaScriptSerializer().Deserialize<Release>(json); } catch (Exception) { throw new IOException("更新清单格式无效。"); }
            if (r == null || r.platform != platform || r.version == null || !Regex.IsMatch(r.version, @"^[0-9]{1,4}\.[0-9]{1,4}\.[0-9]{1,4}$") ||
                r.file != FileName(platform, r.version) || r.size < 1024 || r.size > SizeLimit ||
                r.sha256 == null || !Regex.IsMatch(r.sha256, "^[a-f0-9]{64}$") ||
                r.minimumOS == null || !Regex.IsMatch(r.minimumOS, @"^[0-9]{1,5}(\.[0-9]{1,5}){0,2}$") || String.IsNullOrEmpty(r.signature))
                throw new IOException("更新清单格式无效。");
            byte[] signature;
            try { signature = Convert.FromBase64String(r.signature); } catch (FormatException) { throw new IOException("更新清单格式无效。"); }
            using (var rsa = new RSACryptoServiceProvider()) {
                rsa.PersistKeyInCsp = false; rsa.FromXmlString(key);
                if (!rsa.VerifyData(Payload(r), CryptoConfig.MapNameToOID("SHA256"), signature)) throw new IOException("更新清单签名无效，已拒绝。");
            }
            return r;
        }
        // 按数字逐段比较，缺少的段视为 0。
        public static int Compare(string a, string b) {
            int[] x = a.Split('.').Select(Int32.Parse).ToArray(), y = b.Split('.').Select(Int32.Parse).ToArray();
            for (int i = 0; i < Math.Max(x.Length, y.Length); i++) {
                int left = i < x.Length ? x[i] : 0, right = i < y.Length ? y[i] : 0;
                if (left != right) return left.CompareTo(right);
            }
            return 0;
        }
        public static bool IsNewer(Release r) { return Compare(r.version, DisplayVersion) > 0; }
        public static bool OsSupported(string minimum, System.Version os) { return Compare(os.Major + "." + os.Minor + "." + os.Build, minimum) >= 0; }
        // 自建服务不允许跳转；GitHub 只允许跳到自身下载域名，且只走 HTTPS 默认端口。
        public static bool RedirectAllowed(Uri uri, bool github) {
            return github && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && GitHubHosts.Contains(uri.Host.ToLowerInvariant());
        }
        public static string Hash(string path) {
            using (var sha = SHA256.Create()) using (var file = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
        }
        public static void VerifyFile(string path, long size, string sha256) {
            if (new FileInfo(path).Length != size || Hash(path) != sha256) throw new IOException("安装包大小或 SHA-256 不匹配，已拒绝。");
        }

        static void Fetch(Uri uri, bool github, long maximum, int seconds, Stream output) { Fetch(uri, github, maximum, seconds, output, null, null); }
        // progress 回报已下载字节数（在下载线程调用）；cancelled 返回 true 时中止并抛出 OperationCanceledException。
        static void Fetch(Uri uri, bool github, long maximum, int seconds, Stream output, Action<long> progress, Func<bool> cancelled) {
            if (uri.Scheme != Uri.UriSchemeHttps) throw new IOException("更新地址必须使用 HTTPS。");
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var time = Stopwatch.StartNew();
            for (int hop = 0; ; hop++) {
                var request = (HttpWebRequest)WebRequest.Create(uri);
                request.Timeout = 15000; request.ReadWriteTimeout = 15000; request.AllowAutoRedirect = false;
                request.CachePolicy = new RequestCachePolicy(RequestCacheLevel.NoCacheNoStore);
                request.UserAgent = "ClaudeProfileLauncher/" + DisplayVersion;
                HttpWebResponse response;
                try { response = (HttpWebResponse)request.GetResponse(); }
                catch (WebException error) {
                    var failed = error.Response as HttpWebResponse;
                    if (failed == null) throw new IOException(error.Message);
                    using (failed) throw new IOException("服务器返回 " + (int)failed.StatusCode + "。");
                }
                using (response) {
                    int status = (int)response.StatusCode;
                    if (status == 301 || status == 302 || status == 303 || status == 307 || status == 308) {
                        Uri next;
                        string location = response.Headers["Location"];
                        if (location == null || !Uri.TryCreate(uri, location, out next) || !RedirectAllowed(next, github)) throw new IOException("更新地址跳转到未允许的位置，已拒绝。");
                        if (hop >= 5) throw new IOException("更新地址跳转次数过多。");
                        uri = next; continue;
                    }
                    if (status != 200) throw new IOException("服务器返回 " + status + "。");
                    if (response.ContentLength > maximum) throw new IOException("下载内容超出上限。");
                    using (var stream = response.GetResponseStream()) {
                        byte[] buffer = new byte[65536]; int count; long total = 0;
                        while ((count = stream.Read(buffer, 0, buffer.Length)) > 0) {
                            if (cancelled != null && cancelled()) throw new OperationCanceledException("已取消更新。");
                            total += count;
                            if (total > maximum) throw new IOException("下载内容超出上限。");
                            if (time.Elapsed.TotalSeconds > seconds) throw new IOException("下载超时。");
                            output.Write(buffer, 0, count);
                            if (progress != null) progress(total);
                        }
                    }
                    return;
                }
            }
        }
        public static UpdateSource[] Sources() {
            var list = new List<UpdateSource>();
            if (!String.IsNullOrEmpty(BaseUrl)) list.Add(new UpdateSource { Name = "自建服务", Base = BaseUrl, GitHub = false });
            list.Add(new UpdateSource { Name = "GitHub", Base = GitHubBase, GitHub = true });
            return list.ToArray();
        }
        public static Release CheckSource(UpdateSource source) {
            using (var buffer = new MemoryStream()) {
                Fetch(new Uri(source.Base + "latest-windows.json"), source.GitHub, 16384, 30, buffer);
                Release release = Verify(Encoding.UTF8.GetString(buffer.ToArray()), PublicKey());
                Diagnostics.Write("update_check", "source=" + source.Name + "; available=" + release.version);
                return release;
            }
        }
        // 启动后自动检查：距上次成功检查超过 24 小时才进行。
        public static bool CheckDue() {
            try {
                long ticks;
                return !Int64.TryParse(File.ReadAllText(CheckedFile).Trim(), out ticks) || DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) > TimeSpan.FromHours(24);
            } catch (IOException) { return true; } catch (UnauthorizedAccessException) { return true; }
        }
        public static void MarkChecked() {
            try { Directory.CreateDirectory(Path.GetDirectoryName(CheckedFile)); File.WriteAllText(CheckedFile, DateTime.UtcNow.Ticks.ToString()); }
            catch (Exception error) { Diagnostics.Error("update_mark_error", error); }
        }
        // 下载到本用户更新缓存；大小或哈希不符时删除并抛出，由调用方换下一个来源。
        public static string Download(Release r, UpdateSource source) { return Download(r, source, null, null); }
        public static string Download(Release r, UpdateSource source, Action<long> progress, Func<bool> cancelled) {
            Verify(new JavaScriptSerializer().Serialize(r), PublicKey());
            string directory = Path.Combine(Cache, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, r.file);
            try {
                using (var file = File.Create(path)) Fetch(new Uri(source.Base + Uri.EscapeDataString(r.file)), source.GitHub, r.size, 300, file, progress, cancelled);
                VerifyFile(path, r.size, r.sha256);
            } catch {
                try { File.Delete(path); Directory.Delete(directory); } catch (Exception cleanup) { Diagnostics.Error("update_cleanup_error", cleanup); }
                throw;
            }
            return path;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct TrustFile { public int Size; [MarshalAs(UnmanagedType.LPWStr)] public string Path; public IntPtr File, KnownSubject; }
        [StructLayout(LayoutKind.Sequential)]
        struct TrustData {
            public int Size; public IntPtr PolicyCallback, SipClient; public int UiChoice, Revocation, UnionChoice; public IntPtr FileInfo;
            public int StateAction; public IntPtr State, Url; public int ProviderFlags, UiContext; public IntPtr Settings;
        }
        [DllImport("wintrust.dll", CharSet = CharSet.Unicode)] static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);
        // 返回 Authenticode 签名证书指纹；未签名返回 null；有签名但无效时抛出。
        public static string Signer(string path) {
            X509Certificate certificate;
            try { certificate = X509Certificate.CreateFromSignedFile(path); } catch (CryptographicException) { return null; }
            var file = new TrustFile { Size = Marshal.SizeOf(typeof(TrustFile)), Path = path };
            IntPtr pointer = Marshal.AllocHGlobal(file.Size);
            try {
                Marshal.StructureToPtr(file, pointer, false);
                var data = new TrustData { Size = Marshal.SizeOf(typeof(TrustData)), UiChoice = 2, UnionChoice = 1, FileInfo = pointer };
                var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
                if (WinVerifyTrust(IntPtr.Zero, ref action, ref data) != 0) throw new IOException("安装包的 Authenticode 签名无效，已拒绝。");
            } finally { Marshal.DestroyStructure(pointer, typeof(TrustFile)); Marshal.FreeHGlobal(pointer); }
            return certificate.GetCertHashString();
        }
        // 当前程序已签名时，新安装包必须由同一证书签名；当前程序未签名时无法比对（SPEC Windows 差异），只依赖清单签名与 SHA-256。
        public static string SignerProblem(string current, string candidate) {
            if (current == null) return null;
            if (candidate == null) return "新版安装包没有签名，而当前程序已签名，已拒绝更新。";
            return String.Equals(current, candidate, StringComparison.OrdinalIgnoreCase) ? null : "新版安装包的签名者与当前程序不一致，已拒绝更新。";
        }
        // Setup 只显示进度窗口（不显示向导页、不弹确认框），完成后由 Setup 重新打开启动器。
        public const string SetupArguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH=1";
        // 运行前再次核对哈希与签名者。
        public static void StartSetup(string setup, Release r) {
            VerifyFile(setup, r.size, r.sha256);
            string problem = SignerProblem(Signer(Assembly.GetExecutingAssembly().Location), Signer(setup));
            if (problem != null) throw new IOException(problem);
            Diagnostics.Write("update_setup_start", "version=" + r.version);
            Process.Start(new ProcessStartInfo(setup, SetupArguments) { UseShellExecute = false });
        }

        // ---- 1.2.0 兼容：旧清单与单 exe 原地替换 ----
        public static byte[] LegacyPayload(LegacyRelease r) { return Encoding.UTF8.GetBytes(r.version + "\n" + r.file + "\n" + r.size.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n" + r.sha256 + "\n"); }
        public static LegacyRelease VerifyLegacy(string json, string key) {
            if (json == null || json.Length > 16384) throw new IOException("更新清单过大或为空。");
            LegacyRelease r = new JavaScriptSerializer().Deserialize<LegacyRelease>(json);
            System.Version parsed;
            if (r == null || !System.Version.TryParse(r.version, out parsed) || parsed.ToString() != r.version ||
                r.file != "ClaudeLauncher-" + r.version + ".exe" || r.size < 1024 || r.size > 20 * 1024 * 1024 ||
                r.sha256 == null || !Regex.IsMatch(r.sha256, "^[a-f0-9]{64}$")) throw new IOException("更新清单格式无效。");
            using (var rsa = new RSACryptoServiceProvider()) {
                rsa.PersistKeyInCsp = false; rsa.FromXmlString(key);
                if (String.IsNullOrEmpty(r.signature) || !rsa.VerifyData(LegacyPayload(r), CryptoConfig.MapNameToOID("SHA256"), Convert.FromBase64String(r.signature))) throw new IOException("更新签名不匹配，已拒绝更新。");
            }
            return r;
        }
        public sealed class InstallPlan { public string target, originalHash; public int pid; public long started; }
        // 由 1.2.0 客户端下载 ClaudeLauncher-x.x.x.x.exe 后以 --apply-update 调用。
        public static int Install(bool showError = true) {
            string self = Assembly.GetExecutingAssembly().Location, directory = Path.GetDirectoryName(self);
            string backup = null, target = null; bool replaced = false;
            try {
                LegacyRelease release = VerifyLegacy(File.ReadAllText(Path.Combine(directory, "release.json")), PublicKey()); VerifyFile(self, release.size, release.sha256);
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
                File.Copy(self, sibling); VerifyFile(sibling, release.size, release.sha256);
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
