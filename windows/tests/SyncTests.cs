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
    // 1.7.18：隔离检查未通过改为警告。用合成校验结果和替身启动，不碰真实账号。
    static void StorageRiskChecks(string root,BindingFlags flags) {
        System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var ui=new LauncherUI(new LauncherSettings(),Path.Combine(root,"risk-settings.json"));
        ui.ScanProvider=()=>Task.FromResult(new List<Instance>());
        typeof(LauncherUI).GetField("executable",flags).SetValue(ui,Assembly.GetExecutingAssembly().Location);
        var open=typeof(LauncherUI).GetMethod("CheckAndOpen",flags);
        var errors=(Dictionary<int,string>)typeof(LauncherUI).GetField("errors",flags).GetValue(ui);
        Exception failure=new IOException("存储项含链接或重定向，已停止：fixture");int validations=0;
        ui.ValidateStorage=n=>{ validations++;var source=new TaskCompletionSource<int>();source.SetException(failure);return source.Task; };
        var launches=new List<bool>();ui.LaunchProvider=(n,path,accepted)=>{ launches.Add(accepted);return 4242; };
        var risks=new List<string[]>();int riskAnswer=0,riskCancel=-1,riskDefault=-1;var alerts=new List<string>();
        ui.AskOverride=(title,detail,buttons,cancel,standard)=>{
            if(title=="账号存储隔离检查未通过"&&buttons.Length==2) { risks.Add(new[]{detail,buttons[0],buttons[1]});riskCancel=cancel;riskDefault=standard;return riskAnswer; }
            if(buttons.Contains("确认启动")) return Array.IndexOf(buttons,"确认启动");
            if(buttons.Contains("仍然启动")) return Array.IndexOf(buttons,"仍然启动");
            alerts.Add(title);return cancel;
        };
        Pump((Task)open.Invoke(ui,new object[]{9,0,true,null}));
        Check(risks.Count==1&&risks[0][1]=="取消"&&risks[0][2]=="仍然继续"&&riskCancel==0&&riskDefault==0,"risk warning defaults to cancel");
        Check(risks[0][0].Contains("fixture")&&risks[0][0].Contains("启动器不会修改这些文件"),"risk warning lists reason and impact");
        Check(launches.Count==0&&errors.ContainsKey(9),"cancel does not launch and keeps the error");
        riskAnswer=1;validations=0;risks.Clear();
        Pump((Task)open.Invoke(ui,new object[]{9,0,true,null}));
        Check(risks.Count==1&&validations==2,"accepted risk is not asked again after the network prompt");
        Check(launches.Count==1&&launches[0]&&!errors.ContainsKey(9),"continue launches with the accepted risk");
        ((Dictionary<int,DateTime>)typeof(LauncherUI).GetField("pending",flags).GetValue(ui)).Remove(9);
        risks.Clear();riskAnswer=0;launches.Clear();
        Pump((Task)open.Invoke(ui,new object[]{9,0,true,null}));
        Check(risks.Count==1&&launches.Count==0,"next click warns again");
        ui.ValidateStorage=n=>{ var source=new TaskCompletionSource<int>();source.SetException(new StorageNotFolderException("账号目录路径不是文件夹，已停止：fixture"));return source.Task; };
        risks.Clear();alerts.Clear();riskAnswer=1;
        Pump((Task)open.Invoke(ui,new object[]{9,0,true,null}));
        Check(risks.Count==0&&alerts.Count==1&&launches.Count==0,"non-folder path stays blocked");
        int rounds=0;ui.ValidateStorage=n=>{ if(++rounds==1) return Task.FromResult(0);var source=new TaskCompletionSource<int>();source.SetException(failure);return source.Task; };
        risks.Clear();riskAnswer=0;
        Pump((Task)open.Invoke(ui,new object[]{9,0,true,null}));
        Check(risks.Count==1&&launches.Count==0,"failure at the recheck also warns");
        ui.ValidateStorage=n=>Task.FromResult(0); launches.Clear();
        var copied=new List<string>(); ui.ClipboardWriter=text=>copied.Add(text);
        var linked=new List<string>(); ui.LinkLaunchProvider=(n,path,accepted,link)=>{ linked.Add(link);return 4243; };
        var promptField=typeof(LauncherUI).GetField("linkPrompt",flags); const string prefill="claude://code/new?q=x";
        promptField.SetValue(ui,"分组指令");
        Pump((Task)open.Invoke(ui,new object[]{9,0,true,prefill}));
        Check(linked.SequenceEqual(new[]{prefill})&&copied.SequenceEqual(new[]{"分组指令"}),"prefilled launch also copies the instruction");
        ((Dictionary<int,DateTime>)typeof(LauncherUI).GetField("pending",flags).GetValue(ui)).Remove(9);
        ui.ClipboardWriter=text=>{ throw new InvalidOperationException("clipboard busy"); };
        Pump((Task)open.Invoke(ui,new object[]{9,0,true,prefill}));
        Check(linked.Count==2,"clipboard failure does not stop the launch");
        ((Dictionary<int,DateTime>)typeof(LauncherUI).GetField("pending",flags).GetValue(ui)).Remove(9);
        copied.Clear(); ui.ClipboardWriter=text=>copied.Add(text);
        Pump((Task)open.Invoke(ui,new object[]{9,0,true,null}));
        Check(copied.Count==0&&launches.Count==1,"a plain launch copies nothing");
        ((Dictionary<int,DateTime>)typeof(LauncherUI).GetField("pending",flags).GetValue(ui)).Remove(9);
        promptField.SetValue(ui,null);
        string fake=Path.Combine(root,"recheck"),roaming=Path.Combine(fake,"Roaming"),local=Path.Combine(fake,"Local"),profile=Path.Combine(roaming,"Claude-Profile-09"),outside=Path.Combine(fake,"outside");
        Directory.CreateDirectory(profile);Directory.CreateDirectory(outside);Directory.CreateDirectory(local);
        typeof(SelfTest).GetMethod("Junction",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[]{Path.Combine(profile,"Cache"),outside});
        Reject(()=>Core.Recheck(9,roaming,local,false),"launch recheck stops without acceptance");
        Core.Recheck(9,roaming,local,true);checks++;
        Directory.Delete(Path.Combine(profile,"Cache"));Directory.Delete(profile);File.WriteAllText(profile,"not a folder");
        Reject(()=>Core.Recheck(9,roaming,local,true),"launch recheck stops on a non-folder path even when accepted");
    }
    // 对应 macOS SharedConfigLinkAllowed 的 allowed／rejected 两组用例；只判定路径，本机无符号链接权限也能运行。
    static void LinkTargetChecks() {
        const string home=@"C:\Users\fixture",launcher=@"C:\Users\fixture\AppData\Local\Claude-MultiLauncher";
        const string second=@"C:\Users\fixture\AppData\Roaming\Claude-Second",config=second+@"\ClaudeCodeConfig",first=@"C:\Users\fixture\AppData\Local\Claude-Data\claude_x";
        var accounts=new[]{first,second};
        foreach(string target in new[]{home+@"\.claude\settings.json",home+@"\.claude\CLAUDE.md",home+@"\.claude\skills\shared",config+@"\CLAUDE.md",config+@"\skills\shared",@"D:\shared\skills\example",home+@"\Documents\rules.md"})
            Check(ConfigSync.TargetAllowed(target,home,launcher,accounts),"link target allowed: "+target);
        foreach(string target in new[]{home+@"\.claude.json",home+@"\.claude\.credentials.json",home+@"\.claude\projects",home+@"\.claude\projects\x",home+@"\.claude\.hidden.md",home+@"\.claude",
            home,@"C:\Users",@"C:\",home+@"\AppData",home+@"\AppData\Roaming",second,second+@"\Cookies",first,first+@"\Network",config,config+@"\projects",config+@"\.hidden.md",launcher,launcher+@"\settings.json",home+@"\AppData\Local"})
            Check(!ConfigSync.TargetAllowed(target,home,launcher,accounts),"link target rejected: "+target);
        foreach(string entry in new[]{"settings.json","CLAUDE.md",@"skills\shared",@"output-styles\a.md"}) Check(ConfigSync.Allowed(entry),"entry allowed: "+entry);
        foreach(string entry in new[]{".hidden.md",@"skills\.hidden",@"projects\shared","notes.txt",@"skills\own\deep","agents",".claude.json"}) Check(!ConfigSync.Allowed(entry),"entry rejected: "+entry);
    }
    // 最近会话页（取代用量趋势页）：进入不读取、每页 10 条、显示全部、目标菜单。
    static void RecentPageChecks(string root,BindingFlags flags) {
        var ui=new LauncherUI(new LauncherSettings(),Path.Combine(root,"recent-settings.json"));
        ui.CodeNameFiles=n=>new string[0];
        ui.Snapshot(1,new List<Instance>{new Instance{Number=2,ProcessId=111}});
        Func<string,object> get=name=>typeof(LauncherUI).GetField(name,flags).GetValue(ui);
        Check(get("recentSessions")==null&&((System.Windows.Controls.Button)get("recentLoad")).Visibility==System.Windows.Visibility.Visible,"showing the page reads nothing and offers the load button");
        Pump(ui.LoadRecent()); Check(get("recentSessions")==null,"snapshots never read sessions");
        DateTime now=DateTime.Now; var rows=LauncherUI.RecentDemo(now);
        ui.SetRecent(rows,new[]{1,2,3});
        var list=(System.Windows.Controls.ListBox)get("recentList"); var pager=(System.Windows.Controls.StackPanel)get("recentPager");
        Check(list.Items.Count==10&&pager.Children.Count==5&&((System.Windows.Controls.Button)get("recentLoad")).Visibility!=System.Windows.Visibility.Visible,"ten rows a page with previous, three numbers and next");
        Check(rows.Zip(rows.Skip(1),(a,b)=>a.Stamp>=b.Stamp).All(x=>x),"newest change first");
        typeof(LauncherUI).GetField("recentPageIndex",flags).SetValue(ui,2); ui.UpdateRecent(); Check(list.Items.Count==3,"last page holds the rest");
        typeof(LauncherUI).GetField("recentAll",flags).SetValue(ui,true); ui.UpdateRecent(); Check(list.Items.Count==23&&pager.Children.Count==0,"show all lists every row without a pager");
        Check((string)((System.Windows.Controls.Button)get("recentAllButton")).Content=="分页显示","show all offers paging back");
        // 模板确实生成 50 高的行；“显示全部”只为看得见的行建容器（虚拟化）。
        var panel=(System.Windows.Controls.Border)get("recentPanel"); panel.Measure(new System.Windows.Size(736,502)); panel.Arrange(new System.Windows.Rect(0,0,736,502));
        Func<System.Windows.DependencyObject,IEnumerable<System.Windows.DependencyObject>> visuals=null;
        visuals=node=>new[]{node}.Concat(Enumerable.Range(0,System.Windows.Media.VisualTreeHelper.GetChildrenCount(node)).SelectMany(i=>visuals(System.Windows.Media.VisualTreeHelper.GetChild(node,i))));
        var first=list.ItemContainerGenerator.ContainerFromIndex(0);
        Check(first!=null&&visuals(first).OfType<System.Windows.Controls.Canvas>().Any(c=>c.Height==50&&c.Width==734),"row template builds a 50-pixel row");
        int realized=Enumerable.Range(0,list.Items.Count).Count(i=>list.ItemContainerGenerator.ContainerFromIndex(i)!=null);
        Check(realized>=10&&realized<23,"show all builds only the visible rows: "+realized);
        typeof(LauncherUI).GetField("recentAll",flags).SetValue(ui,false); typeof(LauncherUI).GetField("recentPageIndex",flags).SetValue(ui,0); ui.UpdateRecent();
        var whole=(System.Windows.FrameworkElement)ui.Snapshot(1,new List<Instance>{new Instance{Number=2,ProcessId=111}}); whole.Measure(new System.Windows.Size(1080,720)); whole.Arrange(new System.Windows.Rect(0,0,1080,720)); whole.UpdateLayout();
        var scroller=visuals(list).OfType<System.Windows.Controls.ScrollViewer>().First();
        Check(list.Items.Count==10&&scroller.ExtentHeight<=scroller.ViewportHeight&&scroller.ComputedVerticalScrollBarVisibility!=System.Windows.Visibility.Visible,"a page of ten rows fits without a scroll bar: "+scroller.ViewportHeight+"/"+scroller.ExtentHeight);
        // 每行的文字：来源账号、旧版本／已同步副本、来源分组。
        var rowOf=typeof(LauncherUI).GetMethod("RecentRow",flags);
        Func<SyncSessionRow,List<string>> strings=r=>LauncherTests.Children((System.Windows.DependencyObject)rowOf.Invoke(ui,new object[]{r})).SelectMany(d=>{
            var found=new List<string>{System.Windows.Automation.AutomationProperties.GetName(d)};
            var block=d as System.Windows.Controls.TextBlock; if(block!=null) found.Add(block.Text);
            var run=d as System.Windows.Documents.Run; if(run!=null) found.Add(run.Text);
            var element=d as System.Windows.FrameworkElement; if(element!=null) found.Add(element.ToolTip as string);
            return found; }).Where(s=>!String.IsNullOrEmpty(s)).ToList();
        var zh=strings(rows[0]); Check(zh.Contains("最新版本在账号 01")&&zh.Any(s=>s.EndsWith(" · 账号 02 是旧版本")),"row names the newest account and an older copy");
        Check(strings(rows[2]).Any(s=>s.EndsWith(" · 账号 02 已同步")),"an equal copy reads in sync");
        Check(strings(rows[3]).Any(s=>s.Contains(" · 发布")),"source sidebar group shown");
        typeof(LauncherUI).GetField("recentLoading",flags).SetValue(ui,true); ui.UpdateRecent();
        Check(((System.Windows.Controls.TextBlock)get("recentSummary")).Text=="正在刷新…"&&!((System.Windows.Controls.Button)get("recentRefresh")).IsEnabled
            &&!LauncherTests.Children((System.Windows.DependencyObject)rowOf.Invoke(ui,new object[]{rows[0]})).OfType<System.Windows.Controls.Button>().Single().IsEnabled,"refreshing keeps rows but disables sync");
        typeof(LauncherUI).GetField("recentLoading",flags).SetValue(ui,false); ui.UpdateRecent();
        // 英文界面：行和菜单不在窗口逻辑树里，不能留中文（会话标题和项目名是用户内容，这里用英文）。
        Appearance.Language=1;
        try {
            long stamp=now.AddMinutes(-3).Ticks;
            var english=new SyncSessionRow { Path="demo",Id="e",Title="Fix login",ProjectPath=@"C:\Users\example\Projects\Website",ProjectName="Website",Modified=now.AddMinutes(-3),Stamp=stamp,Account=1,Linked=true,
                Versions=new Dictionary<int,SyncVersion>{{1,new SyncVersion{Id="e",Stamp=stamp}},{2,new SyncVersion{Id="f",Stamp=stamp-1}},{3,new SyncVersion{Id="g",Stamp=stamp}}} };
            ui.UpdateRecent();
            var shown=strings(english);
            foreach(System.Windows.Controls.MenuItem item in ui.RecentTargetMenu(english).Items) { var header=item.Header as System.Windows.Controls.TextBlock; shown.Add(header!=null?header.Text:(string)item.Header); }
            shown.Add(((System.Windows.Controls.TextBlock)get("recentSummary")).Text); shown.Add((string)((System.Windows.Controls.Button)get("recentAllButton")).Content);
            var chinese=shown.Where(s=>System.Text.RegularExpressions.Regex.IsMatch(s,"[\u3000-\u303f\u4e00-\u9fff\uff00-\uffef]")).ToList();
            Check(chinese.Count==0,"English recent page has no Chinese: "+String.Join(" | ",chinese));
            Check(shown.Contains("Newest version is in Account 01")&&shown.Any(s=>s.EndsWith("account 02 has an older version, account 03 in sync"))&&shown.Contains("Account 02 · running, quit it first")&&shown.Contains("23 sessions · read just now"),"English row, menu and summary: "+String.Join(" | ",shown));
        } finally { Appearance.Language=0; }
        Func<int,List<System.Windows.Controls.MenuItem>> menu=i=>ui.RecentTargetMenu(rows[i]).Items.Cast<System.Windows.Controls.MenuItem>().ToList();
        var m=menu(0); Check(m.Count==4&&!m[0].IsEnabled&&!m[1].IsEnabled&&!m[2].IsEnabled&&m[3].IsEnabled&&(int)m[3].Tag==3,"target menu: source and running disabled, new enabled");
        m=menu(1); Check(m[1].IsEnabled&&!m[3].IsEnabled,"an older copy can be replaced; the source cannot");
        m=menu(2); Check(!m[2].IsEnabled,"an equal copy is already current");
        Check(SessionSync.VersionAt(rows[0],2)==1&&SessionSync.VersionAt(rows[0],1)==2&&SessionSync.VersionAt(rows[0],3)==0,"version held per account");
        Check(LauncherUI.RecentAge(now.AddSeconds(-5),now)=="刚刚"&&LauncherUI.RecentAge(now.AddMinutes(-3),now)=="3 分钟前"&&LauncherUI.RecentAge(now.AddDays(-2),now)==now.AddDays(-2).ToString("MM-dd HH:mm"),"relative times");
        ui.SetRecent(new List<SyncSessionRow>(),new[]{1,2,3});
        Check(((System.Windows.Controls.Button)get("recentDiagnose")).Visibility==System.Windows.Visibility.Visible,"no sessions offers diagnosis");
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
            Appearance.Language=1;Check(Appearance.Text("实例管理")!="实例管理","shared English dictionary");Check(Appearance.Text("unknown fallback")=="unknown fallback","missing English fallback");Appearance.Language=0;
            Appearance.Theme=1;var brush=Appearance.Brush("#FAF6F0");var light=brush.Color;Appearance.Theme=2;Appearance.Refresh();Check(brush.Color!=light,"live dark palette");Appearance.Theme=1;Appearance.Refresh();
            File.Delete(list);
            File.WriteAllText(list,"{broken");Check(Projects.Load(list).Count==0&&!File.Exists(list)&&Directory.GetFiles(root,"projects.json.corrupt-*").Length==1,"corrupt project file retained");
            var ui=new LauncherUI(new LauncherSettings(),Path.Combine(root,"ui-settings.json"));
            var flags=BindingFlags.NonPublic|BindingFlags.Instance;
            var logs=(List<LogEntry>)typeof(LauncherUI).GetField("logs",flags).GetValue(ui);logs.Clear();logs.Add(new LogEntry { At=DateTime.UtcNow,Time="fixture",Level="信息",Instance="系统",Text="fixture" });
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
            second.SetResult(new List<Instance>());Pump(b); first.SetException(new IOException("stale failure"));Pump(a);Check(b.Result&&a.Result,"old caller adopts the new scan instead of failing");
            var third=new TaskCompletionSource<List<Instance>>();var fourth=new TaskCompletionSource<List<Instance>>();calls=0;ui.ScanProvider=()=>++calls==1?third.Task:fourth.Task;
            var c=(Task<bool>)scan.Invoke(ui,null);var d=(Task<bool>)scan.Invoke(ui,null);third.SetResult(new List<Instance>());Check(!c.IsCompleted,"superseded caller waits for the new scan");
            fourth.SetException(new IOException("newest failure"));Pump(d);Pump(c);Check(!c.Result&&!d.Result&&(string)typeof(LauncherUI).GetField("scanError",flags).GetValue(ui)=="newest failure","superseded caller reports the real reason");
            var fifth=new TaskCompletionSource<List<Instance>>();ui.ScanProvider=()=>fifth.Task;var e=(Task<bool>)scan.Invoke(ui,null);fifth.SetResult(new List<Instance>());Pump(e);Check(e.Result,"scan recovers health");
            Check((bool)typeof(LauncherUI).GetField("healthy",flags).GetValue(ui),"stale failure cannot clear health");
            var finalScan=new TaskCompletionSource<List<Instance>>();ui.ScanProvider=()=>finalScan.Task;int validations=0;ui.ValidateStorage=n=>{ validations++;return Task.FromResult(0); };
            var open=typeof(LauncherUI).GetMethod("CheckAndOpen",flags);var operation=(Task)open.Invoke(ui,new object[]{2,0,false,null});
            var checking=(HashSet<int>)typeof(LauncherUI).GetField("checking",flags).GetValue(ui);Check(checking.Contains(2),"account stays occupied during final scan");
            var duplicate=(Task)open.Invoke(ui,new object[]{2,0,false,null});Check(duplicate.IsCompleted&&validations==1,"duplicate account operation rejected");
            typeof(LauncherUI).GetField("closed",flags).SetValue(ui,true);finalScan.SetResult(new List<Instance>());Pump(operation);Check(!checking.Contains(2),"account occupation released in finally");
            StorageRiskChecks(root,flags);
            LinkTargetChecks();
            RecentPageChecks(root,flags);
            // MSIX 版 Claude 的会话目录带应用能力标识：不算其他用户，能被扫描；真正的其他用户可写仍拒绝。
            string sessions=Path.Combine(root,"sessions"),sessionOrg=Path.Combine(sessions,Guid.NewGuid().ToString(),Guid.NewGuid().ToString());Directory.CreateDirectory(sessionOrg);string sessionProject=Path.Combine(root,"session-project");Directory.CreateDirectory(sessionProject);
            string sessionId=Guid.NewGuid().ToString();File.WriteAllText(Path.Combine(sessionOrg,"local_"+sessionId+".json"),"{\"sessionId\":\"local_"+sessionId+"\",\"cwd\":"+new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(sessionProject)+",\"originCwd\":\"\"}",new UTF8Encoding(false));
            Action<string> grant=sid=>{ var acl=Directory.GetAccessControl(sessions);acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(new System.Security.Principal.SecurityIdentifier(sid),System.Security.AccessControl.FileSystemRights.FullControl,System.Security.AccessControl.InheritanceFlags.ContainerInherit|System.Security.AccessControl.InheritanceFlags.ObjectInherit,System.Security.AccessControl.PropagationFlags.None,System.Security.AccessControl.AccessControlType.Allow));Directory.SetAccessControl(sessions,acl); };
            // 临时目录可能继承其他用户的写权限，断开继承后只留当前用户。
            var isolated=new System.Security.AccessControl.DirectorySecurity();isolated.SetAccessRuleProtection(true,false);isolated.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(System.Security.Principal.WindowsIdentity.GetCurrent().User,System.Security.AccessControl.FileSystemRights.FullControl,System.Security.AccessControl.InheritanceFlags.ContainerInherit|System.Security.AccessControl.InheritanceFlags.ObjectInherit,System.Security.AccessControl.PropagationFlags.None,System.Security.AccessControl.AccessControlType.Allow));Directory.SetAccessControl(sessions,isolated);
            Check(Projects.Discover(new[]{sessions}).Records==1,"private session folder is scanned");
            grant("S-1-15-3-3557520199-3666692283-3112367039-3524159787-2791857073-3163583606-3692855932");
            Check(Projects.Discover(new[]{sessions}).Records==1,"store-app capability does not block the scan");
            grant("S-1-1-0");Reject(()=>LocalFiles.RequirePrivateDirectory(sessions),"folder writable by everyone is refused");var exposed=Projects.Discover(new[]{sessions});Check(exposed.Records==0&&exposed.Rejected==1,"writable-by-others sessions are skipped");
            // 项目页表格：有效目录可打开，失效目录标为不可用。
            string pageRoot=Path.Combine(root,"project-page"),pageProject=Path.Combine(pageRoot,"页面项目");Directory.CreateDirectory(pageProject);
            Projects.Save(Path.Combine(pageRoot,"projects.json"),new[]{new LauncherUI.LocalProject{Path=pageProject,Name="页面项目",LastAccount=2},new LauncherUI.LocalProject{Path=Path.Combine(pageRoot,"missing"),Name="失效项目",Group="工作"}});
            var pageUi=new LauncherUI(new LauncherSettings(),Path.Combine(pageRoot,"settings.json"));var pageAll=LauncherTests.Children(pageUi.Snapshot(4,new List<Instance>())).ToList();var pageTexts=pageAll.OfType<System.Windows.Controls.TextBlock>().Select(t=>t.Text).ToList();
            foreach(string text in new[]{"名称","分组","路径","状态","上次账号","操作","页面项目","失效项目","未分组","工作","正常","不可用","账号 02","未使用","共 2 个项目"}) Check(pageTexts.Contains(text),"project page shows "+text);
            var openButtons=pageAll.OfType<System.Windows.Controls.Button>().Where(item=>"打开".Equals(item.Content)&&System.Windows.Automation.AutomationProperties.GetName(item).StartsWith("选择账号打开项目：")).ToList();
            Check(openButtons.Count==2&&openButtons.Count(item=>item.IsEnabled)==1,"only a usable project can be opened");
            // 手动展开用量弹窗：面板晚到时重读，读到两项即停；取整差异不算矛盾；漏掉关闭通知后仍能再次触发。
            var watch=new PopupWatch();var usageButton=new[]{1,2};Check(watch.Accept(usageButton,true)&&!watch.Accept(usageButton,true),"duplicate expansion ignored");watch.DuplicateWindow=TimeSpan.Zero;Check(watch.Accept(usageButton,true),"expansion after a missed close is read again");
            Func<double,UsageNode> usagePage=week=>new UsageNode{Type="Document",Url="https://claude.ai/epitaxy",Children={new UsageNode{Type="Button",Name="用量：每周 · 所有模型：53%周二18:00 重置",Expandable=true},new UsageNode{Type="Window",Children={new UsageNode{Type="Hyperlink",Url="https://claude.ai/settings/usage"},new UsageNode{Type="ProgressBar",Name="5 小时限额",Range=21},new UsageNode{Type="ProgressBar",Name="每周 · 所有模型",Range=week}}}}};
            var rounded=UsageInterface.ReadSynthetic(usagePage(52.6),5);Check(rounded.Status==UsageInterface.Observed&&rounded.FiveHour.Percent==21,"panel value rounded by the button is consistent");
            Check(UsageInterface.ReadSynthetic(usagePage(50),5).Status=="界面数据待核对","panel that disagrees is still refused");
            Directory.CreateDirectory(Path.Combine(root,"popup"));var popupUi=new LauncherUI(new LauncherSettings(),Path.Combine(root,"popup","settings.json"));
            var popupState=new List<Instance>{new Instance{Number=9,ProcessId=4321}};popupUi.ScanProvider=()=>Task.FromResult(popupState);
            typeof(LauncherUI).GetField("instances",flags).SetValue(popupUi,popupState);typeof(LauncherUI).GetField("healthy",flags).SetValue(popupUi,true);
            var weekOnly=new UsageRecord{Status=UsageInterface.Observed,Weekly=new UsageMetric{Percent=53,Reset=""}};var bothRead=new UsageRecord{Status=UsageInterface.Observed,FiveHour=new UsageMetric{Percent=21,Reset=""},Weekly=new UsageMetric{Percent=53,Reset=""}};
            int panelReads=0;popupUi.UsageReader=(pid,identity)=>++panelReads==1?weekOnly:bothRead;
            var expanded=typeof(LauncherUI).GetMethod("ReadExpandedUsage",flags);Pump((Task)expanded.Invoke(popupUi,new object[]{4321}));
            var popupUsage=(Dictionary<int,UsageRecord>)typeof(LauncherUI).GetField("usage",flags).GetValue(popupUi);
            Check(panelReads==2&&popupUsage[9].FiveHour.Percent==21,"late panel is read on retry");
            panelReads=0;popupUi.UsageReader=(pid,identity)=>{ panelReads++;return weekOnly; };Pump((Task)expanded.Invoke(popupUi,new object[]{4321}));
            Check(panelReads==5&&popupUsage[9].FiveHour.Percent==21,"retries are bounded and keep the last five-hour value");
            // 两个账号同时展开时，先成功的账号不能取消另一个账号的重试。
            popupState.Add(new Instance{Number=8,ProcessId=4322}); int firstReads=0,secondReads=0;
            popupUi.UsageReader=(pid,identity)=>pid==4321 ? (++firstReads==1?weekOnly:bothRead) : (++secondReads==1?bothRead:weekOnly);
            Pump(Task.WhenAll((Task)expanded.Invoke(popupUi,new object[]{4321}),(Task)expanded.Invoke(popupUi,new object[]{4322})));
            Check(firstReads==2&&secondReads==1&&popupUsage[9].FiveHour.Percent==21,"popup completion belongs to the requested PID only");
            // 挂起中的同一 PID 展开合并；排队期间不消耗重试次数。
            var gate=(System.Threading.SemaphoreSlim)typeof(LauncherUI).GetField("usageReadGate",flags).GetValue(popupUi);
            gate.Wait(); panelReads=0; popupUi.UsageReader=(pid,identity)=>++panelReads==1?weekOnly:bothRead;
            Task queued=(Task)expanded.Invoke(popupUi,new object[]{4321});
            Task duplicateExpansion=(Task)expanded.Invoke(popupUi,new object[]{4321});
            Pump(Task.Delay(1900)); Check(!queued.IsCompleted&&duplicateExpansion.IsCompleted&&panelReads==0,"queued reads await the gate and duplicate notifications coalesce");
            gate.Release(); Pump(queued); Check(panelReads==2,"a queued expansion retains its retries");
            gate.Wait(); panelReads=0; Task cancelled=(Task)expanded.Invoke(popupUi,new object[]{4321});
            typeof(LauncherUI).GetMethod("ClearUsage",flags).Invoke(popupUi,null); gate.Release(); Pump(cancelled);
            Check(panelReads==0,"clearing usage invalidates queued reads");
            string aliasRoaming=Path.Combine(root,"alias","Roaming"),aliasLocal=Path.Combine(root,"alias","Local");
            string[] aliasPaths=ProfileDeletion.Candidates(9,aliasRoaming,aliasLocal);foreach(string path in aliasPaths) Directory.CreateDirectory(path);
            int recycled=0;int done=ProfileDeletion.Execute(9,aliasRoaming,aliasLocal,aliasPaths,()=>{},path=>{ recycled++;foreach(string each in aliasPaths) Directory.Delete(each,true); });
            Check(done==2&&recycled==1,"second candidate that left with the first is not a failure");
            foreach(string path in aliasPaths) Directory.CreateDirectory(path);
            Reject(()=>ProfileDeletion.Execute(9,aliasRoaming,aliasLocal,aliasPaths,()=>{},path=>{ }),"folder that stays behind still fails");
        } finally { Directory.Delete(root,true); }
        Console.WriteLine("SYNC PASS: "+checks+" checks (temporary fixtures only)");
    }
}
