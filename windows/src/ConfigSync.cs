using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Security.Cryptography;
namespace ClaudeLauncher {
    internal sealed class SyncItem { public string Relative, Source, Target, State; public bool Selected; public int TargetAccount; }
    internal static class ConfigSync {
        static readonly string[] Categories = { "skills", "agents", "commands", "output-styles" };
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] [return: MarshalAs(UnmanagedType.U1)] static extern bool CreateSymbolicLink(string link,string target,uint flags);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name,uint access,uint share,IntPtr security,uint disposition,uint flags,IntPtr template);
        [DllImport("kernel32.dll",SetLastError=true)] static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle handle,uint code,IntPtr input,int inputSize,byte[] output,int outputSize,out int returned,IntPtr overlapped);
        internal static string LinkTarget(string path) {
            using(var handle=CreateFile(path,0,7,IntPtr.Zero,3,0x02200000,IntPtr.Zero)) {
                byte[] data=new byte[16384]; int returned;
                if(handle.IsInvalid||!DeviceIoControl(handle,0x900A8,IntPtr.Zero,0,data,data.Length,out returned,IntPtr.Zero)||returned<20||BitConverter.ToUInt32(data,0)!=0xA000000C) throw new IOException("仅支持普通符号链接。");
                int offset=BitConverter.ToUInt16(data,8),length=BitConverter.ToUInt16(data,10);
                if(20+offset+length>returned) throw new IOException("链接格式无效。");
                string target=System.Text.Encoding.Unicode.GetString(data,20+offset,length);
                if(target.StartsWith(@"\??\")) target=target.Substring(4);
                target=Path.GetFullPath(Path.IsPathRooted(target)?target:Path.Combine(Path.GetDirectoryName(path),target));
                if(new[]{".claude.json",".credentials.json","credentials.json"}.Contains(Path.GetFileName(target),StringComparer.OrdinalIgnoreCase)) throw new IOException("链接指向身份或凭据文件，拒绝同步。");
                return target;
            }
        }
        internal static bool LinkAvailable(string directory) {
            string probe=Path.Combine(directory,".launcher-link-test-"+Guid.NewGuid().ToString("N"));
            try { return CreateSymbolicLink(probe,Path.Combine(directory,".missing-link-target"),2); }
            finally { try { File.Delete(probe); } catch { } }
        }
        internal static bool Allowed(string relative) {
            string[] parts=relative.Split(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
            if(parts[parts.Length-1].StartsWith(".")) return false;
            return parts.Length==1 ? relative=="settings.json" || relative.EndsWith(".md",StringComparison.OrdinalIgnoreCase) : parts.Length==2 && Categories.Contains(parts[0]) && parts[1].Length>0;
        }
        static bool Exists(string path) { try { File.GetAttributes(path); return true; } catch(FileNotFoundException) { return false; } catch(DirectoryNotFoundException) { return false; } }
        static void SafeTree(string path, ref int count, ref long size) {
            var attrs=File.GetAttributes(path);
            if((attrs & FileAttributes.ReparsePoint)!=0) { LinkTarget(path); count++; if(count>5000) throw new IOException("文件数超过上限。"); return; }
            if((attrs & FileAttributes.Directory)!=0) {
                if(!Storage.OwnedByMe((System.Security.Principal.SecurityIdentifier)Directory.GetAccessControl(path).GetOwner(typeof(System.Security.Principal.SecurityIdentifier)))) throw new IOException("配置目录不属于当前用户。");
                foreach(var child in Directory.EnumerateFileSystemEntries(path)) SafeTree(child,ref count,ref size);
            } else {
                LocalFiles.Validate(path,50L*1024*1024); count++; size+=new FileInfo(path).Length;
                if(count>5000 || size>50L*1024*1024) throw new IOException("不安全：超过 50 MB / 5000 文件。");
            }
        }
        internal static bool SharedLinkAllowed(string root,string path) {
            if(!String.Equals(Path.GetFileName(root),"ClaudeCodeConfig",StringComparison.OrdinalIgnoreCase)) return false;
            string relative=path.Substring(root.Length).TrimStart('\\','/');
            if(!Allowed(relative)) return false;
            string target=LinkTarget(path);
            if(!TargetAllowed(target,Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),LauncherSettings.DataDirectory,Enumerable.Range(1,10).SelectMany(n=>ProfileDeletion.Candidates(n,Core.Root,ProfileDeletion.Local)))) return false;
            using(var guard=new LocalFiles(Path.GetDirectoryName(target),false)) { int count=0;long size=0; SafeTree(target,ref count,ref size); }
            return true;
        }
        // 目标不得进入账号数据、启动器数据、~/.claude.json 或 ~/.claude 白名单之外，也不得是它们的上级（SPEC 存储校验）。
        internal static bool TargetAllowed(string target,string home,string launcher,IEnumerable<string> accounts) {
            if(Projects.Inside(target,Path.Combine(home,".claude.json"))||Projects.Inside(target,launcher)||Projects.Inside(launcher,target)) return false;
            string claude=Path.Combine(home,".claude");
            foreach(var pair in accounts.Select(a=>Tuple.Create(a,Path.Combine(a,"ClaudeCodeConfig"))).Concat(new[]{Tuple.Create(claude,claude)})) {
                if(Projects.Inside(pair.Item1,target)) return false;
                if(!Projects.Inside(target,pair.Item1)) continue;
                if(!Projects.Inside(target,pair.Item2)||!Allowed(target.Substring(pair.Item2.Length).TrimStart('\\'))) return false;
            }
            return true;
        }
        static void ValidateEntry(string source,string relative) {
            if(!Allowed(relative)) throw new IOException("不在配置白名单中。");
            int count=0; long size=0; SafeTree(source,ref count,ref size);
            if(relative=="settings.json") {
                if((File.GetAttributes(source)&FileAttributes.ReparsePoint)!=0) throw new IOException("不安全：settings.json 链接不能核验凭据字段。");
                var root=new JavaScriptSerializer().DeserializeObject(LocalFiles.Read(source,1024*1024)) as Dictionary<string,object>;
                if(root==null) throw new IOException("settings.json 格式无效。");
                foreach(string key in new[]{"apiKeyHelper","awsAuthRefresh","awsCredentialExport","otelHeadersHelper"}) if(root.ContainsKey(key)) throw new IOException("含凭据，拒绝同步。");
                object raw; if(root.TryGetValue("env",out raw)) {
                    var env=raw as Dictionary<string,object>; if(env==null) throw new IOException("env 格式无效。");
                    if(env.Keys.Any(k=>new[]{"TOKEN","KEY","SECRET","PASSWORD","PASSWD","CREDENTIAL","AUTH","COOKIE"}.Any(k.ToUpperInvariant().Contains))) throw new IOException("含凭据，拒绝同步。");
                }
            }
        }
        static string Hash(string file) { using(var sha=SHA256.Create()) using(var stream=File.OpenRead(file)) return Convert.ToBase64String(sha.ComputeHash(stream)); }
        static bool Same(string a,string b) {
            bool al=(File.GetAttributes(a)&FileAttributes.ReparsePoint)!=0,bl=(File.GetAttributes(b)&FileAttributes.ReparsePoint)!=0;
            if(al||bl) return al&&bl&&String.Equals(LinkTarget(a),LinkTarget(b),StringComparison.OrdinalIgnoreCase);
            bool directory=Directory.Exists(a); if(directory!=Directory.Exists(b)) return false;
            if(!directory) return new FileInfo(a).Length==new FileInfo(b).Length && Hash(a)==Hash(b);
            var left=Directory.GetFileSystemEntries(a).Select(Path.GetFileName).OrderBy(x=>x).ToArray();
            var right=Directory.GetFileSystemEntries(b).Select(Path.GetFileName).OrderBy(x=>x).ToArray();
            return left.SequenceEqual(right) && left.All(n=>Same(Path.Combine(a,n),Path.Combine(b,n)));
        }
        internal static List<SyncItem> Preview(string source,string target) {
            if(!Directory.Exists(source)||!Directory.Exists(target)||String.Equals(Path.GetFullPath(source),Path.GetFullPath(target),StringComparison.OrdinalIgnoreCase)) throw new IOException("来源和目标必须是不同的已有配置目录。");
            using(var a=new LocalFiles(source,false)) using(var b=new LocalFiles(target,false)) {
                var relatives=Directory.GetFiles(source,"*.md").Select(Path.GetFileName).ToList();
                if(Exists(Path.Combine(source,"settings.json"))) relatives.Add("settings.json");
                foreach(var category in Categories) {
                    string path=Path.Combine(source,category); if(!Directory.Exists(path)) continue;
                    if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0) throw new IOException("来源分类目录是链接，未跟随。");
                    relatives.AddRange(Directory.GetFileSystemEntries(path).Select(p=>Path.Combine(category,Path.GetFileName(p))));
                }
                var rows=new List<SyncItem>();
                foreach(var relative in relatives.Where(r=>!Path.GetFileName(r).StartsWith("."))) {
                    var row=new SyncItem { Relative=relative,Source=Path.Combine(source,relative),Target=Path.Combine(target,relative) };
                    try {
                        ValidateEntry(row.Source,relative);
                        string parent=Path.GetDirectoryName(row.Target);
                        if(Exists(parent) && (File.GetAttributes(parent)&FileAttributes.ReparsePoint)!=0) throw new IOException("目标分类目录是链接。");
                        if(!Exists(row.Target)) row.State="缺少";
                        else if((File.GetAttributes(row.Target)&FileAttributes.ReparsePoint)!=0 && String.Equals(LinkTarget(row.Target),Path.GetFullPath(row.Source),StringComparison.OrdinalIgnoreCase)) row.State="已链接";
                        else { int count=0; long size=0; SafeTree(row.Target,ref count,ref size); row.State=Same(row.Source,row.Target)?"相同":"不同"; }
                    } catch(Exception error) { row.State=error.Message.Contains("含凭据")?"含凭据":"不安全"; }
                    row.Selected=row.State=="缺少"; rows.Add(row);
                }
                return rows;
            }
        }
        static void Copy(string source,string target,ref int count,ref long total) {
            if(++count>5000) throw new IOException("配置超过 5000 个条目。");
            if((File.GetAttributes(source)&FileAttributes.ReparsePoint)!=0) { if(!CreateSymbolicLink(target,LinkTarget(source),(Directory.Exists(source)?1u:0u)|2u)) throw new IOException("系统不能复制来源符号链接，已停止；未跟随读取。"); return; }
            if(Directory.Exists(source)) {
                using(var guard=new LocalFiles(source,false)) { Directory.CreateDirectory(target); foreach(var child in Directory.GetFileSystemEntries(source)) Copy(child,Path.Combine(target,Path.GetFileName(child)),ref count,ref total); }
            } else {
                using(var input=LocalFiles.OpenRead(source,50L*1024*1024)) using(var output=new FileStream(target,FileMode.CreateNew,FileAccess.Write,FileShare.None)) {
                    byte[] bytes=new byte[65536]; int read;
                    while((read=input.Read(bytes,0,bytes.Length))>0) { total+=read; if(total>50L*1024*1024) throw new IOException("配置超过 50 MB。"); output.Write(bytes,0,read); } output.Flush(true);
                }
            }
        }
        internal static void Apply(SyncItem item,bool overwrite,bool link) {
            ValidateEntry(item.Source,item.Relative);
            string parent=Path.GetDirectoryName(item.Target);
            using(var guard=new LocalFiles(parent)) {
                bool exists=Exists(item.Target);
                if(exists && !overwrite) return;
                if(exists) { int count=0; long size=0; SafeTree(item.Target,ref count,ref size); }
                string temp=Path.Combine(parent,".launcher-sync-"+Guid.NewGuid().ToString("N"));
                bool directory=Directory.Exists(item.Source);
                try {
                    if(link) { string target=(File.GetAttributes(item.Source)&FileAttributes.ReparsePoint)!=0?LinkTarget(item.Source):Path.GetFullPath(item.Source); if(!CreateSymbolicLink(temp,target,(directory?1u:0u)|2u)) throw new IOException("无法建立符号链接；请选择拷贝模式。"); }
                    else { int count=0;long total=0;Copy(item.Source,temp,ref count,ref total); ValidateEntry(temp,item.Relative); }
                    if(exists) {
                        RecycleBin.Move(item.Target);
                    }
                    if(directory) Directory.Move(temp,item.Target); else File.Move(temp,item.Target);
                } finally { if(Directory.Exists(temp)) Directory.Delete(temp,true); else if(Exists(temp)) File.Delete(temp); }
            }
        }
    }
}
