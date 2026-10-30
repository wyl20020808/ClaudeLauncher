using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Reflection;
using System.Windows.Threading;
using ClaudeLauncher;
internal static class SyncTests {
    static int checks;
    static void Check(bool value,string name) { checks++; if(!value) throw new Exception("1.7.17: "+name); }
    static void Reject(Action action,string name) { bool rejected=false; try { action(); } catch { rejected=true; } Check(rejected,name); }
    [System.Runtime.InteropServices.DllImport("kernel32.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode)] static extern bool CreateHardLink(string link,string target,IntPtr security);
    static void Pump(Task task) {
        var frame=new DispatcherFrame(); var timer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(5) }; DateTime end=DateTime.UtcNow.AddSeconds(5);
        timer.Tick+=delegate { if(task.IsCompleted||DateTime.UtcNow>end) frame.Continue=false; }; timer.Start(); Dispatcher.PushFrame(frame); timer.Stop(); Check(task.IsCompleted,"asynchronous callback completed"); task.GetAwaiter().GetResult();
    }
    internal static void Run() {
        string root=Path.Combine(Path.GetTempPath(),"launcher-sync-tests-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try {
            string data=Path.Combine(root,"settings.json"); LocalFiles.Write(data,"original"); Check(LocalFiles.Read(data,30)=="original","safe read/write");
            Reject(()=>LocalFiles.Read(data,2),"read limit");
            string hard=Path.Combine(root,"hard"); Check(CreateHardLink(hard,data,IntPtr.Zero),"hard-link fixture"); Reject(()=>LocalFiles.Write(data,"changed"),"hard-link write refused"); Check(File.ReadAllText(data)=="original","hard-link original preserved"); File.Delete(hard);
            string neighbour=Path.Combine(root,"neighbour"),junction=Path.Combine(root,"junction");Directory.CreateDirectory(neighbour);File.WriteAllText(Path.Combine(neighbour,"keep"),"unchanged");
            typeof(SelfTest).GetMethod("Junction",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[]{junction,neighbour});
            Reject(()=>LocalFiles.Write(Path.Combine(junction,"keep"),"changed"),"parent junction write refused");Reject(()=>LocalFiles.Read(Path.Combine(junction,"keep"),100),"parent junction read refused");
            Check(File.ReadAllText(Path.Combine(neighbour,"keep"))=="unchanged","junction target untouched");Directory.Delete(junction);
            var settings=new LauncherSettings { Theme=2,Language=1 }; settings.Save(data); var loaded=LauncherSettings.Load(data); Check(loaded.Theme==2&&loaded.Language==1,"appearance roundtrip");
            Check(LauncherSettings.FromValues(new Dictionary<string,object>{{"theme",42},{"language",-1}}).Theme==1,"invalid theme fallback");
            string source=Path.Combine(root,"source"),target=Path.Combine(root,"target"); Directory.CreateDirectory(source);Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(source,"CLAUDE.md"),"rules"); File.WriteAllText(Path.Combine(source,"settings.json"),"{\"env\":{\"SAFE\":\"1\"}}"); File.WriteAllText(Path.Combine(source,".claude.json"),"must not copy");
            Directory.CreateDirectory(Path.Combine(source,"skills","example")); File.WriteAllText(Path.Combine(source,"skills","example","SKILL.md"),"skill");
            var preview=ConfigSync.Preview(source,target); Check(preview.Count==3&&preview.All(p=>p.State=="缺少"),"whitelist preview");
            foreach(var item in preview) ConfigSync.Apply(item,false,false);
            Check(!File.Exists(Path.Combine(target,".claude.json")),"excluded identity file"); Check(ConfigSync.Preview(source,target).All(p=>p.State=="相同"),"copy equality");
            File.WriteAllText(Path.Combine(source,"settings.json"),"{\"env\":{\"API_TOKEN\":\"secret-fixture\"}}"); Check(ConfigSync.Preview(source,target).Single(p=>p.Relative=="settings.json").State=="含凭据","credential env refused");
            foreach(string key in new[]{"apiKeyHelper","awsAuthRefresh","awsCredentialExport","otelHeadersHelper"}) { File.WriteAllText(Path.Combine(source,"settings.json"),"{\""+key+"\":\"fixture\"}"); Check(ConfigSync.Preview(source,target).Single(p=>p.Relative=="settings.json").State=="含凭据",key+" refused"); }
            Reject(()=>ConfigSync.Apply(new SyncItem { Relative=".claude.json",Source=Path.Combine(source,".claude.json"),Target=Path.Combine(target,"forbidden") },false,false),"apply whitelist recheck");
            string large=Path.Combine(source,"huge.md");using(var file=File.Create(large)) file.SetLength(50L*1024*1024+1);
            Check(ConfigSync.Preview(source,target).Single(p=>p.Relative=="huge.md").State=="不安全","oversize configuration refused");File.Delete(large);
            if(ConfigSync.LinkAvailable(target)) {
                string linked=Path.Combine(target,"linked.md");ConfigSync.Apply(new SyncItem { Relative="CLAUDE.md",Source=Path.Combine(source,"CLAUDE.md"),Target=linked },false,true);
                Check(ConfigSync.LinkTarget(linked)==Path.Combine(source,"CLAUDE.md"),"link points to source");
                File.WriteAllText(Path.Combine(source,"CLAUDE.md"),"new rules");Check(File.ReadAllText(linked)=="new rules","linked changes shared");
                Reject(()=>LocalFiles.Write(linked,"unsafe"),"manager writes reject symlink");
                Reject(()=>Projects.ValidatePath(Path.Combine(target,"skills","example","missing")),"missing project refused");
                File.Delete(linked);
            } else Console.WriteLine("SKIP: symbolic-link creation unavailable; copy fallback tested.");
            string project=Path.Combine(root,"project");Directory.CreateDirectory(project); Check(Projects.ValidatePath(project)==project,"project admission");
            Reject(()=>Projects.ValidatePath(LauncherSettings.DataDirectory),"manager directory refused"); Reject(()=>Projects.ValidatePath("relative"),"relative project refused");
            string list=Path.Combine(root,"projects.json"); var projects=new[]{new LauncherUI.LocalProject { Path=project,Name="Example",Group="Work",LastAccount=2 }}; Projects.Save(list,projects); Check(Projects.Load(list).Single().Group=="Work","project roundtrip");
            File.WriteAllText(list,"{\"version\":2,\"projects\":[]}"); Reject(()=>Projects.Load(list),"unknown project version"); Check(File.ReadAllText(list).Contains(":2"),"unknown version preserved");
            foreach(bool cli in new[]{false,true}) {
                string prefix="{\"sessionId\":\"local_fixture\","+(cli?"\"cliSessionId\":\"fixture\",":"")+"\"cwd\":\"C:\\\\work\",\"originCwd\":\"C:\\\\origin\"";
                using(var stream=new MemoryStream(Encoding.UTF8.GetBytes(prefix+",\"messages\":\"DO NOT READ\"}"))) { Check(Projects.ReadPrefix(stream)==@"C:\origin","prefix path"); Check(stream.Position==Encoding.UTF8.GetByteCount(prefix),"stops before body"); }
            }
            Reject(()=>Projects.ReadPrefix(new MemoryStream(Encoding.UTF8.GetBytes("{\"messages\":\"body\"}"))),"unknown order refused");
            string namePrefix="{\"ignored\":{\"nested\":[1,true,\"skip\"]},\"oauthAccount\":{\"displayName\":\"Example\"";
            using(var stream=new MemoryStream(Encoding.UTF8.GetBytes(namePrefix+",\"token\":\"never-read-fixture\"}}"))) { Check(DisplayNameReader.Read(stream)=="Example","targeted display name");Check(stream.Position==Encoding.UTF8.GetByteCount(namePrefix),"display-name reader stops immediately"); }
            DateTime now=DateTime.UtcNow; var points=new List<TrendPoint>();
            var record=new UsageRecord { FiveHour=new UsageMetric { Percent=20 },Weekly=new UsageMetric { Percent=30 } };
            UsageTrend.Observe(points,1,record,now); UsageTrend.Observe(points,1,record,now.AddSeconds(1)); Check(points.Count==1,"trend deduplication");
            points.Add(new TrendPoint { Account=1,At=now.AddMinutes(-30),Week=20 }); Check(UsageTrend.HoursLeft(points,true,now).HasValue,"rate estimate");
            points.Add(new TrendPoint { Account=1,At=now.AddMinutes(1),Week=5 }); Check(!UsageTrend.HoursLeft(points,true,now.AddMinutes(1)).HasValue,"rate reset boundary");
            points.Add(new TrendPoint { Account=11,At=now,Week=20 }); points.Add(new TrendPoint { Account=2,At=now.AddDays(-9),Week=20 }); Check(UsageTrend.Clean(points,now).Count==2,"trend retention and validation");
            points.Add(new TrendPoint { Account=2,At=now,Week=1 }); Check(UsageTrend.Recommend(points,new[]{1,2})==2,"next account recommendation");
            Appearance.Language=1;Check(Appearance.Text("实例管理")!="实例管理","shared English dictionary");Check(Appearance.Text("unknown fallback")=="unknown fallback","missing English fallback");Appearance.Language=0;
            Appearance.Theme=1;var brush=Appearance.Brush("#FAF6F0");var light=brush.Color;Appearance.Theme=2;Appearance.Refresh();Check(brush.Color!=light,"live dark palette");Appearance.Theme=1;Appearance.Refresh();
            File.Delete(list);
            File.WriteAllText(list,"{broken");Check(Projects.Load(list).Count==0&&!File.Exists(list)&&Directory.GetFiles(root,"projects.json.corrupt-*").Length==1,"corrupt project file retained");
            var ui=new LauncherUI(new LauncherSettings(),Path.Combine(root,"ui-settings.json"));
            var flags=BindingFlags.NonPublic|BindingFlags.Instance;
            var logs=(List<LogEntry>)typeof(LauncherUI).GetField("logs",flags).GetValue(ui);logs.Clear();logs.Add(new LogEntry { At=now,Time="fixture",Level="信息",Instance="系统",Text="fixture" });
            string log=Path.Combine(root,"launcher-log.json");File.WriteAllText(log,"[]");string logAlias=log+".hard";Check(CreateHardLink(logAlias,log,IntPtr.Zero),"log hard-link fixture");
            Check(!ui.ClearStoredLogs()&&logs.Count==1&&File.ReadAllText(log)=="[]","failed clear preserves memory and disk");File.Delete(logAlias);Check(ui.ClearStoredLogs()&&logs.Count==0,"successful clear commits disk first");
            var progressA=ui.BuildUpdateProgress("2.0.0");int generationA=ui.BeginUpdateTask();ui.CancelUpdateTask(generationA,progressA,"fixture");
            var progressB=ui.BuildUpdateProgress("2.0.0");int generationB=ui.BeginUpdateTask();ui.DownloadProgress(generationB,progressB,25,25,100);
            ui.DownloadProgress(generationA,progressB,99,99,100);ui.FinishUpdateTask(generationA,progressA,"stale");ui.CancelUpdateTask(generationA,progressA,"stale");
            Check(progressB.Bar.Value==25&&!progressB.Finished&&ui.UpdateCurrent(generationB),"stale progress/finally/cancel cannot affect retry");
            Check((bool)typeof(LauncherUI).GetField("updating",flags).GetValue(ui),"retry retains operation state");
            ui.FinishUpdateTask(generationB,progressB,"fixture");Check(progressB.Finished,"current update completes");
            var first=new TaskCompletionSource<List<Instance>>();var second=new TaskCompletionSource<List<Instance>>();int calls=0;ui.ScanProvider=()=>++calls==1?first.Task:second.Task;
            typeof(LauncherUI).GetField("executable",flags).SetValue(ui,Assembly.GetExecutingAssembly().Location);
            var scan=typeof(LauncherUI).GetMethod("ScanNow",flags);var a=(Task<bool>)scan.Invoke(ui,null);var b=(Task<bool>)scan.Invoke(ui,null);
            second.SetResult(new List<Instance>());Pump(b); first.SetException(new IOException("stale failure"));Pump(a);Check(b.Result&&!a.Result,"new scan wins over old failure");
            Check((bool)typeof(LauncherUI).GetField("healthy",flags).GetValue(ui),"stale failure cannot clear health");
            var finalScan=new TaskCompletionSource<List<Instance>>();ui.ScanProvider=()=>finalScan.Task;int validations=0;ui.ValidateStorage=n=>{ validations++;return Task.FromResult(0); };
            var open=typeof(LauncherUI).GetMethod("CheckAndOpen",flags);var operation=(Task)open.Invoke(ui,new object[]{2,0,false});
            var checking=(HashSet<int>)typeof(LauncherUI).GetField("checking",flags).GetValue(ui);Check(checking.Contains(2),"account stays occupied during final scan");
            var duplicate=(Task)open.Invoke(ui,new object[]{2,0,false});Check(duplicate.IsCompleted&&validations==1,"duplicate account operation rejected");
            typeof(LauncherUI).GetField("closed",flags).SetValue(ui,true);finalScan.SetResult(new List<Instance>());Pump(operation);Check(!checking.Contains(2),"account occupation released in finally");
        } finally { Directory.Delete(root,true); }
        Console.WriteLine("SYNC PASS: "+checks+" checks (temporary fixtures only)");
    }
}
