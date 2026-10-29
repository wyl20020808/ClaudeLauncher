using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace ClaudeLauncher {
    // 启动器自己的偏好（SPEC 系统设置 [Windows]）：%LOCALAPPDATA%\Claude-MultiLauncher\settings.json，不写 Claude 数据。
    public sealed class LauncherSettings {
        public bool CloseToBackground = true, ShowTray = true, RememberPage, RestoreMinimized = true, ShowArtwork = true, LocalNotifications = true, NotificationSound, AllowUpdates;
        public string StartPage = "instances";
        public int RefreshInterval = 3, LogLimit = 500;
        // 读取失败的原因（已回落默认）；由调用方决定是否记录。
        public string LoadError;

        public static readonly string[] Pages = { "instances", "accounts", "logs", "settings" };
        public static readonly int[] Intervals = { 1, 3, 5 }, LogLimits = { 100, 500, 1000 };
        public static readonly string DataDirectory = Path.Combine(ProfileDeletion.Local, "Claude-MultiLauncher");
        public static readonly string DefaultPath = Path.Combine(DataDirectory, "settings.json");
        public static readonly string LastPagePath = Path.Combine(DataDirectory, "last-page");

        public LauncherSettings Clone() { var copy = (LauncherSettings)MemberwiseClone(); copy.LoadError = null; return copy; }
        public bool SameAs(LauncherSettings other) { return Serialize(Values()) == Serialize(other.Values()); }

        // 固定键顺序的设置值，用于保存、导出与比较。
        public Dictionary<string, object> Values() {
            return new Dictionary<string, object> {
                { "closeToBackground", CloseToBackground }, { "showTray", ShowTray }, { "rememberPage", RememberPage }, { "startPage", StartPage },
                { "refreshInterval", RefreshInterval }, { "restoreMinimized", RestoreMinimized }, { "showArtwork", ShowArtwork }, { "logLimit", LogLimit },
                { "localNotifications", LocalNotifications }, { "notificationSound", NotificationSound }, { "allowUpdates", AllowUpdates } };
        }
        // 逐项按类型与可选值校验，异常项回落默认；不接受未知键以外的任何路径、命令或地址。
        public static LauncherSettings FromValues(IDictionary<string, object> values) {
            var d = new LauncherSettings();
            return new LauncherSettings {
                CloseToBackground = Flag(values, "closeToBackground", d.CloseToBackground), ShowTray = Flag(values, "showTray", d.ShowTray),
                RememberPage = Flag(values, "rememberPage", d.RememberPage), StartPage = Pick(values, "startPage", Pages, d.StartPage),
                RefreshInterval = Pick(values, "refreshInterval", Intervals, d.RefreshInterval), RestoreMinimized = Flag(values, "restoreMinimized", d.RestoreMinimized),
                ShowArtwork = Flag(values, "showArtwork", d.ShowArtwork), LogLimit = Pick(values, "logLimit", LogLimits, d.LogLimit),
                LocalNotifications = Flag(values, "localNotifications", d.LocalNotifications), NotificationSound = Flag(values, "notificationSound", d.NotificationSound),
                AllowUpdates = Flag(values, "allowUpdates", d.AllowUpdates) };
        }
        static bool Flag(IDictionary<string, object> values, string key, bool fallback) {
            object value; return values.TryGetValue(key, out value) && value is bool ? (bool)value : fallback;
        }
        static T Pick<T>(IDictionary<string, object> values, string key, T[] choices, T fallback) {
            object value; return values.TryGetValue(key, out value) && value is T && choices.Contains((T)value) ? (T)value : fallback;
        }
        static string Serialize(object value) { return new JavaScriptSerializer().Serialize(value); }

        // 缺失、损坏或版本不是 1 时全部回落默认，不抛出。
        public static LauncherSettings Load(string path) {
            try {
                if (!File.Exists(path)) return new LauncherSettings();
                var values = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object>;
                object version;
                if (values == null || !values.TryGetValue("version", out version) || !(version is int) || (int)version != 1) throw new InvalidDataException("设置文件格式无效");
                return FromValues(values);
            } catch (Exception error) { return new LauncherSettings { LoadError = error.Message }; }
        }
        // 同目录临时文件写完后替换；失败抛出，原文件保持不变。
        public void Save(string path) {
            var values = new Dictionary<string, object> { { "version", 1 } };
            foreach (var pair in Values()) values.Add(pair.Key, pair.Value);
            WriteAtomic(path, Serialize(values));
        }
        // 导出已保存的偏好：不含账号、别名、项目、凭据、登录保护或上次页面。
        public string ExportJson() { return Serialize(new Dictionary<string, object> { { "version", 1 }, { "settings", Values() } }); }

        public static string ReadLastPage(string path) {
            try { string page = File.Exists(path) ? File.ReadAllText(path).Trim() : null; return Pages.Contains(page) ? page : null; }
            catch (Exception) { return null; }
        }
        public static void WriteLastPage(string path, string page) {
            if (!Pages.Contains(page)) throw new InvalidOperationException("未知页面：" + page);
            WriteAtomic(path, page);
        }
        static void WriteAtomic(string path, string text) {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try {
                File.WriteAllText(temp, text);
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            } finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }

    // SPEC 启动行为 [Windows]：HKCU Run 中本启动器的开机启动项；只接受正式安装路径，只删除指向本启动器的值。
    public static class AutoStart {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", ValueName = "ClaudeMultiLauncher";
        public static string InstalledPath { get { return Path.Combine(ProfileDeletion.Local, "Programs", "Claude 多开启动器", "ClaudeLauncher.exe"); } }
        public static string Command(string exe) { return "\"" + exe + "\""; }
        public static bool IsInstalledCopy(string exe, string installed) {
            try { return String.Equals(Path.GetFullPath(exe), Path.GetFullPath(installed), StringComparison.OrdinalIgnoreCase); }
            catch (Exception) { return false; }
        }
        // null：未设置；否则为当前值。
        public static string Current() {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey)) return key == null ? null : key.GetValue(ValueName) as string;
        }
        public static bool PointsTo(string value, string exe) { return value != null && String.Equals(value.Trim(), Command(exe), StringComparison.OrdinalIgnoreCase); }
        public static void Enable(string exe) {
            if (!IsInstalledCopy(exe, InstalledPath)) throw new InvalidOperationException("开机启动只为正式安装的启动器开启：" + InstalledPath);
            using (var key = Registry.CurrentUser.CreateSubKey(RunKey)) key.SetValue(ValueName, Command(exe), RegistryValueKind.String);
        }
        public static void Disable(string exe) {
            string value = Current();
            if (value == null) return;
            if (!PointsTo(value, exe)) throw new InvalidOperationException("开机启动项指向其他位置，未修改：" + value);
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey, true)) if (key != null) key.DeleteValue(ValueName, false);
        }
    }
}
