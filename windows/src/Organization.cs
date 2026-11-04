using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Web.Script.Serialization;

namespace ClaudeLauncher {
    // 会话同步定位（AGENTS.md Windows 例外，2026-11-04 用户授权）：只在用户点击同步后、目标账号已退出且有多套账号／组织目录时调用。
    // 只读、不加锁、不复制地查 Cookies 里唯一的 lastActiveOrg 一行，用当前用户 DPAPI 解出 Local State 的密钥，只解这一行。
    // 组织标识不输出、不记录、不持久化；密钥和明文用后清零。不联网，不启动任何程序。
    internal static class Organization {
        const int SqliteOk = 0, SqliteRow = 100, SqliteDone = 101, SqliteDeny = 1;
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int Authorizer(IntPtr context, int action, IntPtr a, IntPtr b, IntPtr database, IntPtr source);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int Progress(IntPtr context);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_open_v2(byte[] name, out IntPtr db, int flags, IntPtr vfs);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_close_v2(IntPtr db);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int length, out IntPtr statement, IntPtr tail);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_step(IntPtr statement);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_finalize(IntPtr statement);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_bind_int64(IntPtr statement, int index, long value);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] static extern IntPtr sqlite3_column_blob(IntPtr statement, int column);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_column_bytes(IntPtr statement, int column);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_limit(IntPtr db, int id, int value);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] static extern int sqlite3_set_authorizer(IntPtr db, Authorizer callback, IntPtr context);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)] static extern void sqlite3_progress_handler(IntPtr db, int steps, Progress callback, IntPtr context);
        static readonly Authorizer Allow = AllowColumns;
        static readonly string[] CookieColumns = { "host_key", "name", "path", "value", "encrypted_value", "has_expires", "expires_utc", "is_persistent", "source_scheme", "source_port", "top_frame_site_key" };
        static string Utf8(IntPtr text) { if (text == IntPtr.Zero) return null; int n = 0; while (Marshal.ReadByte(text, n) != 0) n++; var bytes = new byte[n]; Marshal.Copy(text, bytes, 0, n); return Encoding.UTF8.GetString(bytes); }
        // 只放行这两张表的固定列；视图和触发器（source 非空）一律拒绝。
        static int AllowColumns(IntPtr context, int action, IntPtr a, IntPtr b, IntPtr database, IntPtr source) {
            if (source != IntPtr.Zero) return SqliteDeny;
            if (action == 21) return SqliteOk; // SQLITE_SELECT
            if (action != 20 || Utf8(database) != "main") return SqliteDeny; // SQLITE_READ
            string table = Utf8(a), column = Utf8(b);
            if (table == "meta" && (column == "key" || column == "value")) return SqliteOk;
            return table == "cookies" && CookieColumns.Contains(column) ? SqliteOk : SqliteDeny;
        }

        // ---- 密钥：Local State 的 os_crypt.encrypted_key，"DPAPI" 前缀，当前用户解密，不弹界面 ----
        [StructLayout(LayoutKind.Sequential)] struct Blob { public int Size; public IntPtr Data; }
        [DllImport("crypt32.dll", SetLastError = true)] static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
        [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
        internal static void Clear(byte[] bytes) { if (bytes != null) Array.Clear(bytes, 0, bytes.Length); }
        internal static byte[] Unprotect(byte[] data) {
            var handle = GCHandle.Alloc(data, GCHandleType.Pinned); Blob output = new Blob();
            try {
                var input = new Blob { Size = data.Length, Data = handle.AddrOfPinnedObject() };
                if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0x1, out output)) return null;
                var result = new byte[output.Size]; Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            } finally {
                if (output.Data != IntPtr.Zero) { for (int i = 0; i < output.Size; i++) Marshal.WriteByte(output.Data, i, 0); LocalFree(output.Data); }
                handle.Free();
            }
        }
        internal static byte[] LoadKey(string profile) {
            string text;
            try { text = LocalFiles.Read(Path.Combine(profile, "Local State"), 1024 * 1024); } catch (IOException) { return null; } catch (UnauthorizedAccessException) { return null; } catch (DecoderFallbackException) { return null; }
            byte[] wrapped = null, key = null;
            try {
                var root = new JavaScriptSerializer().DeserializeObject(text) as Dictionary<string, object>; object crypt, value;
                var section = root != null && root.TryGetValue("os_crypt", out crypt) ? crypt as Dictionary<string, object> : null;
                string encoded = section != null && section.TryGetValue("encrypted_key", out value) ? value as string : null;
                if (encoded == null || encoded.Length > 4096) return null;
                wrapped = Convert.FromBase64String(encoded);
                if (wrapped.Length <= 5 || Encoding.ASCII.GetString(wrapped, 0, 5) != "DPAPI") return null;
                key = Unprotect(wrapped.Skip(5).ToArray());
                if (key == null || key.Length != 32) { Clear(key); return null; }
                return key;
            }
            catch (ArgumentException) { return null; } catch (InvalidOperationException) { return null; } catch (FormatException) { return null; }
            finally { Clear(wrapped); }
        }

        // ---- AES-256-GCM（系统 BCrypt，.NET Framework 没有 AesGcm） ----
        [StructLayout(LayoutKind.Sequential)] struct AuthInfo {
            public int Size, Version; public IntPtr Nonce; public int NonceSize; public IntPtr AuthData; public int AuthDataSize;
            public IntPtr Tag; public int TagSize; public IntPtr MacContext; public int MacContextSize, Aad; public long Data; public int Flags;
        }
        [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)] static extern int BCryptOpenAlgorithmProvider(out IntPtr algorithm, string id, string implementation, int flags);
        [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)] static extern int BCryptSetProperty(IntPtr handle, string property, byte[] input, int size, int flags);
        [DllImport("bcrypt.dll")] static extern int BCryptGenerateSymmetricKey(IntPtr algorithm, out IntPtr key, IntPtr keyObject, int objectSize, byte[] secret, int secretSize, int flags);
        [DllImport("bcrypt.dll")] static extern int BCryptDecrypt(IntPtr key, byte[] input, int inputSize, ref AuthInfo info, IntPtr iv, int ivSize, byte[] output, int outputSize, out int result, int flags);
        [DllImport("bcrypt.dll")] static extern int BCryptEncrypt(IntPtr key, byte[] input, int inputSize, ref AuthInfo info, IntPtr iv, int ivSize, byte[] output, int outputSize, out int result, int flags);
        [DllImport("bcrypt.dll")] static extern int BCryptDestroyKey(IntPtr key);
        [DllImport("bcrypt.dll")] static extern int BCryptCloseAlgorithmProvider(IntPtr algorithm, int flags);
        // 加密只给自检造合成数据用；解密失败（含标签不符）返回 null。
        internal static byte[] Gcm(byte[] key, byte[] nonce, byte[] input, byte[] tag, bool encrypt) {
            IntPtr algorithm = IntPtr.Zero, handle = IntPtr.Zero;
            GCHandle pinnedNonce = GCHandle.Alloc(nonce, GCHandleType.Pinned), pinnedTag = GCHandle.Alloc(tag, GCHandleType.Pinned);
            var output = new byte[input.Length];
            try {
                if (BCryptOpenAlgorithmProvider(out algorithm, "AES", null, 0) != 0) return null;
                if (BCryptSetProperty(algorithm, "ChainingMode", Encoding.Unicode.GetBytes("ChainingModeGCM\0"), 32, 0) != 0) return null;
                if (BCryptGenerateSymmetricKey(algorithm, out handle, IntPtr.Zero, 0, key, key.Length, 0) != 0) return null;
                var info = new AuthInfo { Size = Marshal.SizeOf(typeof(AuthInfo)), Version = 1, Nonce = pinnedNonce.AddrOfPinnedObject(), NonceSize = nonce.Length, Tag = pinnedTag.AddrOfPinnedObject(), TagSize = tag.Length };
                int written;
                int status = encrypt ? BCryptEncrypt(handle, input, input.Length, ref info, IntPtr.Zero, 0, output, output.Length, out written, 0)
                                     : BCryptDecrypt(handle, input, input.Length, ref info, IntPtr.Zero, 0, output, output.Length, out written, 0);
                if (status == 0 && written == output.Length) return output;
                Clear(output); return null;
            } finally {
                if (handle != IntPtr.Zero) BCryptDestroyKey(handle);
                if (algorithm != IntPtr.Zero) BCryptCloseAlgorithmProvider(algorithm, 0);
                pinnedNonce.Free(); pinnedTag.Free();
            }
        }
        // Windows Chromium v10：前缀 3 + 随机数 12 + 密文 68 + 标签 16；明文是 SHA-256(host_key) 32 字节加 36 字符 UUID（Cookie 库版本 24）。
        internal static string Decrypt(byte[] encrypted, string host, byte[] key) {
            if (encrypted == null || encrypted.Length != 99 || encrypted[0] != 'v' || encrypted[1] != '1' || encrypted[2] != '0' || key == null || key.Length != 32) return null;
            byte[] nonce = new byte[12], cipher = new byte[68], tag = new byte[16], plain = null;
            Buffer.BlockCopy(encrypted, 3, nonce, 0, 12); Buffer.BlockCopy(encrypted, 15, cipher, 0, 68); Buffer.BlockCopy(encrypted, 83, tag, 0, 16);
            try {
                plain = Gcm(key, nonce, cipher, tag, false);
                if (plain == null) return null;
                byte[] hash; using (var sha = SHA256.Create()) hash = sha.ComputeHash(Encoding.UTF8.GetBytes(host));
                if (!hash.SequenceEqual(plain.Take(32))) return null;
                string value = Encoding.ASCII.GetString(plain, 32, 36); Guid id;
                return Guid.TryParseExact(value, "D", out id) ? value.ToLowerInvariant() : null;
            } finally { Clear(plain); Clear(cipher); }
        }

        // ---- 读取 ----
        // 回滚日志模式且没有活动日志才读；immutable 读取不建锁、不建日志、不复制。
        static bool Quiet(string network) {
            foreach (string name in new[] { "Cookies-journal", "Cookies-wal", "Cookies-shm" }) {
                string path = Path.Combine(network, name);
                if (!File.Exists(path) && !Directory.Exists(path)) continue;
                if (LocalFiles.FileState(path) == null || new FileInfo(path).Length != 0) return false;
            }
            return true;
        }
        static string Uri(string path) {
            var parts = path.Replace('\\', '/').Split('/');
            return "file:///" + parts[0] + "/" + String.Join("/", parts.Skip(1).Select(System.Uri.EscapeDataString)) + "?mode=ro&immutable=1";
        }
        internal static string Read(string profile, out string why) {
            byte[] key = null; bool loaded = false;
            try { return Read(profile, DateTime.UtcNow, () => { if (!loaded) { loaded = true; key = LoadKey(profile); } return key; }, out why); }
            finally { Clear(key); }
        }
        // keyProvider 每次返回同一把 32 字节密钥，null 表示不可用；只在找到候选行后才调用。
        internal static string Read(string profile, DateTime nowUtc, Func<byte[]> keyProvider, out string why) {
            why = "路径不安全";
            if (profile == null || !Path.IsPathRooted(profile) || profile.Contains("?") || profile.Contains("#")) return null;
            string network = Path.Combine(profile, "Network"), cookies = Path.Combine(network, "Cookies");
            try {
                using (new LocalFiles(network, false)) {
                    why = "组织记录不可读";
                    string before = LocalFiles.FileState(cookies);
                    if (before == null) { if (!File.Exists(cookies)) why = "未找到当前组织"; return null; }
                    var header = new byte[20];
                    using (var stream = LocalFiles.OpenRead(cookies, 16 * 1024 * 1024, 7)) {
                        if (stream.Length < 100 || stream.Read(header, 0, 20) != 20 || Encoding.ASCII.GetString(header, 0, 16) != "SQLite format 3\0") return null;
                    }
                    if (header[18] != 1 || header[19] != 1 || !Quiet(network)) { why = "组织记录正在更新"; return null; }
                    string org = Query(cookies, nowUtc, keyProvider, ref why);
                    if (!Quiet(network) || LocalFiles.FileState(cookies) != before) { why = "组织记录正在更新"; return null; }
                    return org;
                }
            }
            catch (DirectoryNotFoundException) { why = "暂无本地记录"; return null; }
            catch (IOException) { why = "路径不可读取"; return null; }
            catch (UnauthorizedAccessException) { why = "路径不可读取"; return null; }
            catch (DllNotFoundException) { why = "组织格式不支持"; return null; }
            catch (EntryPointNotFoundException) { why = "组织格式不支持"; return null; }
        }
        static string Query(string cookies, DateTime nowUtc, Func<byte[]> keyProvider, ref string why) {
            IntPtr db = IntPtr.Zero, statement = IntPtr.Zero; int budget = 1000;
            Progress stop = context => --budget <= 0 ? 1 : 0;
            try {
                if (sqlite3_open_v2(Encoding.UTF8.GetBytes(Uri(cookies) + "\0"), out db, 0x1 | 0x40 | 0x8000, IntPtr.Zero) != SqliteOk) return null; // READONLY | URI | NOMUTEX
                sqlite3_limit(db, 0, 4096); // SQLITE_LIMIT_LENGTH
                sqlite3_set_authorizer(db, Allow, IntPtr.Zero);
                sqlite3_progress_handler(db, 1000, stop, IntPtr.Zero);
                bool version = sqlite3_prepare_v2(db, Encoding.UTF8.GetBytes("SELECT value FROM meta WHERE key='version' LIMIT 2\0"), -1, out statement, IntPtr.Zero) == SqliteOk &&
                    sqlite3_step(statement) == SqliteRow && Text(statement, 0) == "24" && sqlite3_step(statement) == SqliteDone;
                sqlite3_finalize(statement); statement = IntPtr.Zero;
                if (!version) { why = "组织格式不支持"; return null; }
                // 只选根地址的非登录选择器，不返回任何登录行。
                const string sql = "SELECT host_key,value,encrypted_value FROM cookies WHERE name='lastActiveOrg' AND host_key IN ('claude.ai','.claude.ai') AND path='/' AND top_frame_site_key='' " +
                    "AND source_scheme=2 AND source_port IN (-1,443) AND is_persistent=1 AND has_expires=1 AND expires_utc>? LIMIT 17";
                if (sqlite3_prepare_v2(db, Encoding.UTF8.GetBytes(sql + "\0"), -1, out statement, IntPtr.Zero) != SqliteOk) return null;
                sqlite3_bind_int64(statement, 1, (nowUtc.ToUniversalTime().Ticks - new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks) / 10);
                var values = new HashSet<string>(); int rows = 0, step;
                while ((step = sqlite3_step(statement)) == SqliteRow) {
                    if (++rows > 16) return null;
                    string host = Text(statement, 0); byte[] encrypted = Bytes(statement, 2), key = null;
                    if (sqlite3_column_bytes(statement, 1) != 0 || encrypted == null || encrypted.Length != 99) return null;
                    key = keyProvider();
                    if (key == null) { why = "组织密钥不可用"; return null; }
                    string value = Decrypt(encrypted, host, key);
                    if (value == null) return null;
                    values.Add(value);
                }
                if (step != SqliteDone) return null;
                if (values.Count == 1) return values.First();
                why = values.Count == 0 ? "未找到当前组织" : "当前组织有歧义";
                return null;
            } finally {
                if (statement != IntPtr.Zero) sqlite3_finalize(statement);
                if (db != IntPtr.Zero) sqlite3_close_v2(db);
                GC.KeepAlive(stop);
            }
        }
        static byte[] Bytes(IntPtr statement, int column) {
            IntPtr data = sqlite3_column_blob(statement, column); int length = sqlite3_column_bytes(statement, column);
            if (data == IntPtr.Zero || length <= 0 || length > 4096) return null;
            var bytes = new byte[length]; Marshal.Copy(data, bytes, 0, length); return bytes;
        }
        static string Text(IntPtr statement, int column) { var bytes = Bytes(statement, column); return bytes == null ? null : Encoding.UTF8.GetString(bytes); }
    }
}
