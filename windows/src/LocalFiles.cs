using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace ClaudeLauncher {
    // Keep directory handles open without delete sharing while accessing manager-owned files.
    internal sealed class LocalFiles : IDisposable {
        readonly List<SafeFileHandle> parents = new List<SafeFileHandle>();
        [StructLayout(LayoutKind.Sequential)] struct Info {
            public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Modified;
            public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern SafeFileHandle CreateFile(string name,uint access,uint share,IntPtr security,uint disposition,uint flags,IntPtr template);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool GetFileInformationByHandle(SafeFileHandle file,out Info info);
        internal LocalFiles(string directory, bool create = true) {
            try { LockDirectory(new DirectoryInfo(Path.GetFullPath(directory)), create); }
            catch { Dispose(); throw; }
        }
        void LockDirectory(DirectoryInfo directory, bool create) {
            if (directory.Parent != null) LockDirectory(directory.Parent, create);
            if (!directory.Exists) { if(!create) throw new DirectoryNotFoundException("本地目录不存在。"); Directory.CreateDirectory(directory.FullName); }
            var handle = CreateFile(directory.FullName, 0x80, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            parents.Add(handle); Info info;
            if (handle.IsInvalid || !GetFileInformationByHandle(handle,out info) || (info.Attributes & 0x400) != 0 || (info.Attributes & 0x10) == 0)
                throw new IOException("本地数据目录含重解析点或无法锁定。");
        }
        internal static void Validate(string path, long limit) {
            try { File.GetAttributes(path); } catch(FileNotFoundException) { return; } catch(DirectoryNotFoundException) { return; }
            using (var handle = CreateFile(path,0x80020000,1,IntPtr.Zero,3,0x00200000,IntPtr.Zero)) {
                Info info;
                if(handle.IsInvalid || !GetFileInformationByHandle(handle,out info) || (info.Attributes & (0x400|0x10|0x40)) != 0 || info.Links != 1 || (((long)info.SizeHigh << 32) + info.SizeLow) > limit)
                    throw new IOException("本地数据不是单链接普通文件，或超过大小限制。");
                using(var stream = new FileStream(handle,FileAccess.Read)) {
                    if(!Storage.OwnedByMe((SecurityIdentifier)stream.GetAccessControl().GetOwner(typeof(SecurityIdentifier)))) throw new IOException("本地数据不属于当前用户。");
                }
            }
        }
        internal static void RequirePrivateDirectory(string path) {
            var security=Directory.GetAccessControl(path);
            if(!Storage.OwnedByMe((SecurityIdentifier)security.GetOwner(typeof(SecurityIdentifier)))) throw new IOException("来源目录不属于当前用户。");
            foreach(System.Security.AccessControl.FileSystemAccessRule rule in security.GetAccessRules(true,true,typeof(SecurityIdentifier))) {
                string sid=rule.IdentityReference.Value;
                // S-1-15-3-* 是系统授予商店应用的能力标识（MSIX 版 Claude 的数据目录都带），不是其他用户。
                if(rule.AccessControlType!=System.Security.AccessControl.AccessControlType.Allow || Storage.OwnedByMe((SecurityIdentifier)rule.IdentityReference) || sid=="S-1-5-18" || sid=="S-1-5-32-544" || sid.StartsWith("S-1-15-3-")) continue;
                var write=System.Security.AccessControl.FileSystemRights.Write|System.Security.AccessControl.FileSystemRights.Delete|System.Security.AccessControl.FileSystemRights.ChangePermissions|System.Security.AccessControl.FileSystemRights.TakeOwnership;
                if((rule.FileSystemRights&write)!=0) throw new IOException("来源目录允许其他用户写入。");
            }
        }
        internal static string Read(string path, long limit) {
            using(var guard = new LocalFiles(Path.GetDirectoryName(path),false)) using(var stream=OpenRead(path,limit)) using(var reader=new StreamReader(stream,new UTF8Encoding(false,true))) return reader.ReadToEnd();
        }
        internal static FileStream OpenRead(string path,long limit) {
            var handle=CreateFile(path,0x80020000,1,IntPtr.Zero,3,0x00200000,IntPtr.Zero);
            try {
                Info info;
                if(handle.IsInvalid || !GetFileInformationByHandle(handle,out info) || (info.Attributes&(0x400|0x10|0x40))!=0 || info.Links!=1 || (((long)info.SizeHigh<<32)+info.SizeLow)>limit) throw new IOException("文件不是安全的单链接普通文件或超过限制。");
                var stream=new FileStream(handle,FileAccess.Read,1,false);
                if(!Storage.OwnedByMe((SecurityIdentifier)stream.GetAccessControl().GetOwner(typeof(SecurityIdentifier)))) { stream.Dispose(); throw new IOException("文件不属于当前用户。"); }
                return stream;
            } catch { handle.Dispose(); throw; }
        }
        internal static void Write(string path, string text) {
            const long limit = 16 * 1024 * 1024;
            if(Encoding.UTF8.GetByteCount(text) > limit) throw new IOException("本地数据超过 16 MiB。");
            using(var guard = new LocalFiles(Path.GetDirectoryName(path))) {
                Validate(path,limit);
                string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
                try {
                    using(var stream = new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)) {
                        byte[] bytes = Encoding.UTF8.GetBytes(text); stream.Write(bytes,0,bytes.Length); stream.Flush(true);
                    }
                    Validate(path,limit);
                    if(File.Exists(path)) File.Replace(temp,path,null); else File.Move(temp,path);
                } finally { if(File.Exists(temp)) File.Delete(temp); }
            }
        }
        public void Dispose() { foreach(var handle in parents) handle.Dispose(); parents.Clear(); }
    }
}
