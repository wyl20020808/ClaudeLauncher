using System;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
namespace ClaudeLauncher {
    // Unrelated values are skipped lexically, never deserialized or retained.
    internal sealed class DisplayNameReader {
        readonly Stream stream; int next=-2; long count;
        DisplayNameReader(Stream value) { stream=value; }
        int Peek() { if(next==-2) { if(++count>CodeUserName.SizeLimit) throw new IOException("显示名文件超过限制。"); next=stream.ReadByte(); } return next; }
        int Take() { int value=Peek(); next=-2; if(value<0) throw new IOException("JSON 不完整。"); return value; }
        void Space() { while(Peek()==32||Peek()==9||Peek()==10||Peek()==13) Take(); }
        void Expect(int value) { Space(); if(Take()!=value) throw new IOException("JSON 格式无效。"); }
        string StringValue(bool keep) {
            Expect(34); using(var bytes=keep?new MemoryStream():null) {
                if(keep) bytes.WriteByte(34); bool escape=false;
                while(true) {
                    int c=Take(); if(keep) { if(bytes.Length>4096) throw new IOException("字段过长。"); bytes.WriteByte((byte)c); }
                    if(c==34&&!escape) break; escape=c==92?!escape:false;
                }
                return keep?new JavaScriptSerializer().Deserialize<string>(new UTF8Encoding(false,true).GetString(bytes.ToArray())):null;
            }
        }
        void Skip(int depth) {
            if(depth>64) throw new IOException("JSON 层级过深。"); Space(); int c=Peek();
            if(c==34) { StringValue(false);return; }
            if(c==123||c==91) {
                Take(); int end=c==123?125:93; Space(); if(Peek()==end) { Take();return; }
                while(true) { if(c==123) { StringValue(false);Expect(58); } Skip(depth+1);Space();int token=Take();if(token==end)return;if(token!=44)throw new IOException("JSON 格式无效。"); }
            }
            bool any=false; while(Peek()>=0&&Peek()!=44&&Peek()!=125&&Peek()!=93&&Peek()!=32&&Peek()!=9&&Peek()!=10&&Peek()!=13) { Take();any=true; }
            if(!any)throw new IOException("JSON 值无效。");
        }
        string ObjectValue(bool account) {
            Expect(123); Space(); if(Peek()==125) { Take();return null; }
            while(true) {
                string key=StringValue(true);Expect(58);Space();
                if(account&&key=="displayName") return Peek()==34?StringValue(true):null;
                if(!account&&key=="oauthAccount") return Peek()==123?ObjectValue(true):null;
                Skip(0);Space();int token=Take();if(token==125)return null;if(token!=44)throw new IOException("JSON 格式无效。");
            }
        }
        internal static string Read(Stream stream) { return new DisplayNameReader(stream).ObjectValue(false); }
    }
}
