using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace ClaudeLauncher {
    // 启动器自己的偏好（SPEC 网络边界 [Windows]）：%LOCALAPPDATA%\Claude-MultiLauncher\settings.json，不写 Claude 数据。
    public sealed class LauncherSettings {
        public bool AllowUpdates;
        // 读取失败的原因（已回落默认）；由调用方决定是否记录。
        public string LoadError;
        public static readonly string DefaultPath = Path.Combine(ProfileDeletion.Local, "Claude-MultiLauncher", "settings.json");

        // 缺失、损坏或类型不符时回落默认（更新关闭），不抛出。
        public static LauncherSettings Load(string path) {
            var settings = new LauncherSettings();
            try {
                if (!File.Exists(path)) return settings;
                var values = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object>;
                object version, allow;
                if (values == null || !values.TryGetValue("version", out version) || !(version is int) || (int)version != 1) throw new InvalidDataException("设置文件格式无效");
                if (values.TryGetValue("allowUpdates", out allow) && allow is bool) settings.AllowUpdates = (bool)allow;
            } catch (Exception error) { return new LauncherSettings { LoadError = error.Message }; }
            return settings;
        }
        // 同目录临时文件写完后替换；失败抛出，原文件保持不变。
        public void Save(string path) {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try {
                File.WriteAllText(temp, new JavaScriptSerializer().Serialize(new Dictionary<string, object> { { "version", 1 }, { "allowUpdates", AllowUpdates } }));
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            } finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }
}
