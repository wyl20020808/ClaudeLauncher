using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Web.Script.Serialization;
using ClaudeLauncher;

public static class UpdateTests {
    public static void Run(Action<bool, string> check) {
        using (var rsa = new RSACryptoServiceProvider(2048)) {
            rsa.PersistKeyInCsp = false;
            // 1.2.0 兼容清单（--apply-update 使用）
            var release = new LegacyRelease { version="1.7.0.0", file="ClaudeLauncher-1.7.0.0.exe", size=2048, sha256=new string('a',64) };
            release.signature = Convert.ToBase64String(rsa.SignData(Updates.LegacyPayload(release), CryptoConfig.MapNameToOID("SHA256")));
            var serializer = new JavaScriptSerializer();
            string json = serializer.Serialize(release), key = rsa.ToXmlString(false);
            check(Updates.VerifyLegacy(json, key).version == "1.7.0.0", "Valid signature rejected");
            // 相对当前版本构造更高版本，版本号递增后无需改测试。
            int[] current = Updates.DisplayVersion.Split('.').Select(Int32.Parse).ToArray();
            check(Updates.IsNewer(new Release { version = current[0] + "." + current[1] + "." + (current[2] + 1) }), "New release not detected");
            check(!Updates.IsNewer(new Release { version=Updates.DisplayVersion }), "Same version offered as update");
            check(!Updates.IsNewer(new Release { version="1.6.9" }), "Downgrade offered");
            foreach (string bad in new[] { json.Replace("1.7.0.0", "9.2.0.0"), json.Replace(new string('a',64),new string('b',64)), json.Replace("2048", "4096"), json.Replace("ClaudeLauncher-1.7.0.0.exe", "../evil.exe"), "null", new string('x',16385) }) {
                bool rejected = false; try { Updates.VerifyLegacy(bad,key); } catch { rejected=true; }
                check(rejected, "Tampered manifest accepted");
            }
            using (var other = new RSACryptoServiceProvider(2048)) {
                other.PersistKeyInCsp = false;
                bool rejected = false; try { Updates.VerifyLegacy(json,other.ToXmlString(false)); } catch { rejected=true; }
                check(rejected, "Foreign signer accepted");
            }
            string file = Path.Combine(Path.GetTempPath(), "claude-update-test-" + Guid.NewGuid().ToString("N") + ".bin");
            try {
                File.WriteAllBytes(file,new byte[2048]); release.sha256=Updates.Hash(file);
                Updates.VerifyFile(file,release.size,release.sha256); check(true,"Valid file rejected");
                File.WriteAllText(file,"tampered");
                bool rejected=false; try { Updates.VerifyFile(file,release.size,release.sha256); } catch(IOException) { rejected=true; }
                check(rejected,"Corrupt download accepted");
            } finally { File.Delete(file); }
            string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            check(!Diagnostics.Redact(user+"\\sample").Contains(user), "Username not redacted");
        }
    }
}
