using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using System.Web.Script.Serialization;
namespace ClaudeLauncher {
    internal static class Projects {
        internal static string LoadWarning;
        internal static string ValidatePath(string path) {
            if(String.IsNullOrWhiteSpace(path)||!Path.IsPathRooted(path)||path.StartsWith(@"\\")||path.IndexOf(':',2)>=0) throw new IOException("只支持绝对本地项目路径。");
            string full=Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            if(full.Length==2 && full[1]==':') full+="\\";
            if(!Directory.Exists(full)||new DriveInfo(Path.GetPathRoot(full)).DriveType==DriveType.Network) throw new IOException("项目目录不存在或不是本地目录。");
            for(var parent=new DirectoryInfo(full);parent!=null;parent=parent.Parent) if((parent.Attributes&FileAttributes.ReparsePoint)!=0) throw new IOException("项目路径含重解析点。");
            var protectedPaths=Enumerable.Range(1,10).SelectMany(n=>ProfileDeletion.Candidates(n,Core.Root,ProfileDeletion.Local)).Concat(new[]{LauncherSettings.DataDirectory,AppDomain.CurrentDomain.BaseDirectory,Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".claude")});
            if(full.Split('\\','/').Any(p=>p.Equals("ClaudeCodeConfig",StringComparison.OrdinalIgnoreCase)) || protectedPaths.Any(p=>Inside(full,p))) throw new IOException("不能添加账号、配置、启动器数据或安装目录。");
            return full;
        }
        internal static bool Inside(string path,string parent) { return String.Equals(path.TrimEnd('\\'),parent.TrimEnd('\\'),StringComparison.OrdinalIgnoreCase)||path.StartsWith(parent.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase); }
        internal static bool SensitiveRoot(string path) { return String.Equals(path.TrimEnd('\\'),Path.GetPathRoot(path).TrimEnd('\\'),StringComparison.OrdinalIgnoreCase)||String.Equals(path,Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),StringComparison.OrdinalIgnoreCase); }
        internal static List<LauncherUI.LocalProject> Load(string path) {
            LoadWarning=null;
            if(!File.Exists(path)) return new List<LauncherUI.LocalProject>();
            string text=LocalFiles.Read(path,1024*1024);
            try { return Parse(text); }
            catch(NotSupportedException) { throw; }
            catch(Exception error) {
                using(var guard=new LocalFiles(Path.GetDirectoryName(path),false)) { LocalFiles.Validate(path,1024*1024); File.Move(path,path+".corrupt-"+Guid.NewGuid().ToString("N")); }
                LoadWarning="损坏清单已改名保留："+error.Message;
                return new List<LauncherUI.LocalProject>();
            }
        }
        static List<LauncherUI.LocalProject> Parse(string text) {
            var root=new JavaScriptSerializer().DeserializeObject(text) as Dictionary<string,object>; object version,raw;
            if(root==null||!root.TryGetValue("version",out version)||!(version is int)) throw new IOException("项目清单格式无效。");
            if((int)version!=1) throw new NotSupportedException("项目清单版本未知，原文件保留，停止写入。");
            if(!root.TryGetValue("projects",out raw)||!(raw is object[])) throw new IOException("项目清单结构无效。");
            var result=new List<LauncherUI.LocalProject>();
            foreach(var row in (object[])raw) {
                var values=row as Dictionary<string,object>; object id,name,group,folder,last; Guid guid;
                if(values==null||!values.TryGetValue("id",out id)||!(id is string)||!Guid.TryParse((string)id,out guid)||!values.TryGetValue("name",out name)||!(name is string)||!values.TryGetValue("group",out group)||!(group is string)||!values.TryGetValue("path",out folder)||!(folder is string)||!values.TryGetValue("lastAccount",out last)||!(last is int)||(int)last<0||(int)last>10) throw new IOException("项目清单条目无效。");
                result.Add(new LauncherUI.LocalProject { Id=(string)id,Name=(string)name,Group=(string)group,Path=(string)folder,LastAccount=(int)last });
            }
            if(result.Count>500) throw new IOException("项目清单超过 500 项。");
            return result;
        }
        internal static void Save(string path,IEnumerable<LauncherUI.LocalProject> projects) {
            var rows=projects.Select(p=>new { id=p.Id,name=p.Name,group=p.Group,path=p.Path,lastAccount=p.LastAccount }).ToArray();
            if(rows.Length>500) throw new IOException("项目清单最多 500 项。");
            LocalFiles.Write(path,new JavaScriptSerializer().Serialize(new { version=1,projects=rows }));
        }
        sealed class Prefix {
            readonly Stream stream; int count;
            internal Prefix(Stream input) { stream=input; }
            int Read() { if(++count>32768) throw new IOException("前缀超限"); int c=stream.ReadByte(); if(c<0) throw new IOException("前缀不完整"); return c; }
            int Next() { int c; do { c=Read(); } while(c==32||c==13||c==10||c==9); return c; }
            string Quoted() {
                if(Next()!=34) throw new IOException("字段类型不支持");
                using(var bytes=new MemoryStream()) {
                    bytes.WriteByte(34); bool escape=false;
                    while(true) { int c=Read(); bytes.WriteByte((byte)c); if(c==34&&!escape) break; escape=c==92?!escape:false; }
                    return new JavaScriptSerializer().Deserialize<string>(new UTF8Encoding(false,true).GetString(bytes.ToArray()));
                }
            }
            internal string Parse() {
                if(Next()!=123||Quoted()!="sessionId"||Next()!=58) throw new IOException("记录格式不支持"); Quoted();
                if(Next()!=44) throw new IOException("记录格式不支持"); string key=Quoted();
                if(key=="cliSessionId") { if(Next()!=58) throw new IOException(); Quoted(); if(Next()!=44) throw new IOException(); key=Quoted(); }
                if(key!="cwd"||Next()!=58) throw new IOException("记录字段顺序不支持"); string cwd=Quoted();
                if(Next()!=44||Quoted()!="originCwd"||Next()!=58) throw new IOException("记录字段顺序不支持"); string origin=Quoted();
                return String.IsNullOrEmpty(origin)?cwd:origin;
            }
        }
        internal static string ReadPrefix(Stream stream) { return new Prefix(stream).Parse(); }
        internal sealed class Discovery { public List<string> Paths=new List<string>(); public int Records,Rejected; public bool Limited; }
        internal static Discovery Discover(IEnumerable<string> roots) {
            var result=new Discovery(); int entries=0;
            foreach(string root in roots.Distinct(StringComparer.OrdinalIgnoreCase)) {
                if(!Directory.Exists(root)) continue;
                try {
                    LocalFiles.RequirePrivateDirectory(root);
                    using(var guard=new LocalFiles(root,false)) foreach(string account in Directory.EnumerateFileSystemEntries(root)) {
                        if(++entries>20000) { result.Limited=true; return result; } Guid id;
                        if(!Directory.Exists(account)||!Guid.TryParse(Path.GetFileName(account),out id)) continue;
                        LocalFiles.RequirePrivateDirectory(account);
                        using(var a=new LocalFiles(account,false)) foreach(string org in Directory.EnumerateFileSystemEntries(account)) {
                            if(++entries>20000) { result.Limited=true; return result; }
                            if(!Directory.Exists(org)||!Guid.TryParse(Path.GetFileName(org),out id)) continue;
                            LocalFiles.RequirePrivateDirectory(org);
                            using(var b=new LocalFiles(org,false)) foreach(string file in Directory.EnumerateFileSystemEntries(org)) {
                                if(++entries>20000||result.Records>=5000) { result.Limited=true; return result; }
                                string name=Path.GetFileName(file); if(!name.StartsWith("local_")||!name.EndsWith(".json")||!Guid.TryParse(name.Substring(6,name.Length-11),out id)) continue;
                                result.Records++;
                                try {
                                    LocalFiles.Validate(file,Int64.MaxValue);
                                    // Buffer size 1: stop before any session body is read.
                                    using(var stream=LocalFiles.OpenRead(file,Int64.MaxValue)) {
                                        string path=ValidatePath(ReadPrefix(stream));
                                        if(SensitiveRoot(path)||Inside(path,Path.GetTempPath())) { result.Rejected++; continue; }
                                        if(!result.Paths.Contains(path,StringComparer.OrdinalIgnoreCase)) result.Paths.Add(path);
                                    }
                                } catch { result.Rejected++; }
                            }
                        }
                    }
                } catch { result.Rejected++; }
            }
            return result;
        }
    }
}
