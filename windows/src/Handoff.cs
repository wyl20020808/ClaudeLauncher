using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
namespace ClaudeLauncher {
    internal static class Handoff {
        internal static string Rule { get { return Appearance.Text("<!-- claude-launcher:handoff -->\n## 交接文档（由 Claude 启动器添加）\n\n本项目可能由多个 Claude 账号轮流继续。请在项目根目录维护 `HANDOFF.md`：\n- 每完成一个阶段、做出重要决定，或预计会话即将结束时，立即更新它；\n- 内容包括：目标、已完成、进行中、下一步（按顺序）、关键文件与命令、已知问题与注意事项；\n- 写到让没有上下文的新会话能直接接手；\n- 新会话开始时，如果存在 `HANDOFF.md`，先阅读再继续。\n<!-- /claude-launcher:handoff -->\n"); } }
        internal static string WritePrompt { get { return Appearance.Text("请把当前工作整理成交接文档，写入项目根目录的 HANDOFF.md（已有则更新）：目标、已完成、进行中、下一步（按顺序）、关键文件与命令、已知问题与注意事项。写到让没有上下文的新会话能直接接手。写完后告诉我。"); } }
        internal static string ContinuePrompt(string directory) { return string.Format(Appearance.Text("这是从另一个会话交接过来的工作。项目目录：{0}\n请先阅读项目根目录的 HANDOFF.md，用几句话复述目标和下一步，然后继续完成剩余工作；过程中按 CLAUDE.md 的要求持续更新 HANDOFF.md。"), directory); }
        [StructLayout(LayoutKind.Sequential)] struct Info {
            public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Modified;
            public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern SafeFileHandle CreateFile(string name,uint access,uint share,IntPtr security,uint disposition,uint flags,IntPtr template);
        [DllImport("kernel32.dll", SetLastError=true)] static extern bool GetFileInformationByHandle(SafeFileHandle file,out Info info);
        static Info Inspect(SafeFileHandle handle) {
            Info info;
            if (!GetFileInformationByHandle(handle,out info)) throw new IOException("无法检查文件句柄");
            return info;
        }
        internal static bool Append(string directory) {
            string root=Path.GetFullPath(directory), path=Path.Combine(root,"CLAUDE.md");
            if (root.StartsWith(@"\\") || new DriveInfo(Path.GetPathRoot(root)).DriveType == DriveType.Network) throw new IOException("只支持本地项目目录");
            var parents=new List<SafeFileHandle>();
            try {
                for (var parent=new DirectoryInfo(root);parent!=null;parent=parent.Parent) {
                    var h=CreateFile(parent.FullName,0x80,3,IntPtr.Zero,3,0x02200000,IntPtr.Zero);
                    parents.Add(h);
                    if(h.IsInvalid || (Inspect(h).Attributes & 0x400)!=0) throw new IOException("项目路径含重解析点或无法锁定，未写入");
                }
                bool exists=File.Exists(path);
                using(var handle=CreateFile(path,0xC0020000,0,IntPtr.Zero,exists?3u:1u,0x00200000,IntPtr.Zero)) {
                    if(handle.IsInvalid) throw new IOException("无法独占打开 CLAUDE.md，未写入");
                    Info before=Inspect(handle);
                    if((before.Attributes & (0x400|0x10|0x40))!=0 || before.Links!=1 || before.SizeHigh!=0 || before.SizeLow>1048576) throw new IOException("CLAUDE.md 不是单链接普通文件或超过 1 MB，未写入");
                    using(var stream=new FileStream(handle,FileAccess.ReadWrite)) {
                        var owner=(SecurityIdentifier)stream.GetAccessControl().GetOwner(typeof(SecurityIdentifier));
                        if(!Storage.OwnedByMe(owner)) throw new IOException("CLAUDE.md 不属于当前用户，未写入");
                        byte[] bytes=new byte[before.SizeLow]; int offset=0;
                        while(offset<bytes.Length) { int read=stream.Read(bytes,offset,bytes.Length-offset); if(read==0) throw new IOException("文件在检查期间发生变化"); offset+=read; }
                        string text=new UTF8Encoding(false,true).GetString(bytes);
                        if(text.Contains("<!-- claude-launcher:handoff -->")) return false;
                        Info after=Inspect(handle);
                        if(before.SizeLow!=after.SizeLow || before.IndexLow!=after.IndexLow || before.IndexHigh!=after.IndexHigh || before.Modified.dwHighDateTime!=after.Modified.dwHighDateTime || before.Modified.dwLowDateTime!=after.Modified.dwLowDateTime || after.Links!=1) throw new IOException("文件在检查期间发生变化，未写入");
                        byte[] append=Encoding.UTF8.GetBytes((text.Length==0?"":text.EndsWith("\n")?"\n":"\n\n")+Rule);
                        stream.Seek(0,SeekOrigin.End); stream.Write(append,0,append.Length); stream.Flush(true);
                    }
                }
                return true;
            } finally { foreach(var handle in parents) handle.Dispose(); }
        }
        internal static string Age(string directory) {
            try { var info=new FileInfo(Path.Combine(directory,"HANDOFF.md")); if(!info.Exists || (info.Attributes & (FileAttributes.ReparsePoint|FileAttributes.Directory))!=0) return null; return "交接 " + Math.Max(0,(int)(DateTime.UtcNow-info.LastWriteTimeUtc).TotalMinutes) + " 分钟前"; } catch { return null; }
        }
    }
}
