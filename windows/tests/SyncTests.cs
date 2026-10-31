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
        Pump((Task)open.Invoke(ui,new object[]{9,0,true}));
        Check(risks.Count==1&&risks[0][1]=="取消"&&risks[0][2]=="仍然继续"&&riskCancel==0&&riskDefault==0,"risk warning defaults to cancel");
        Check(risks[0][0].Contains("fixture")&&risks[0][0].Contains("启动器不会修改这些文件"),"risk warning lists reason and impact");
        Check(launches.Count==0&&errors.ContainsKey(9),"cancel does not launch and keeps the error");
        riskAnswer=1;validations=0;risks.Clear();
        Pump((Task)open.Invoke(ui,new object[]{9,0,true}));
        Check(risks.Count==1&&validations==2,"accepted risk is not asked again after the network prompt");
        Check(launches.Count==1&&launches[0]&&!errors.ContainsKey(9),"continue launches with the accepted risk");
        ((Dictionary<int,DateTime>)typeof(LauncherUI).GetField("pending",flags).GetValue(ui)).Remove(9);
        risks.Clear();riskAnswer=0;launches.Clear();
        Pump((Task)open.Invoke(ui,new object[]{9,0,true}));
        Check(risks.Count==1&&launches.Count==0,"next click warns again");
        ui.ValidateStorage=n=>{ var source=new TaskCompletionSource<int>();source.SetException(new StorageNotFolderException("账号目录路径不是文件夹，已停止：fixture"));return source.Task; };
        risks.Clear();alerts.Clear();riskAnswer=1;
        Pump((Task)open.Invoke(ui,new object[]{9,0,true}));
        Check(risks.Count==0&&alerts.Count==1&&launches.Count==0,"non-folder path stays blocked");
        int rounds=0;ui.ValidateStorage=n=>{ if(++rounds==1) return Task.FromResult(0);var source=new TaskCompletionSource<int>();source.SetException(failure);return source.Task; };
        risks.Clear();riskAnswer=0;
        Pump((Task)open.Invoke(ui,new object[]{9,0,true}));
        Check(risks.Count==1&&launches.Count==0,"failure at the recheck also warns");
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
    // 1.7.18：趋势从实例页移到账号页表格下方。
    static void TrendSectionChecks(string root,BindingFlags flags) {
        var ui=new LauncherUI(new LauncherSettings(),Path.Combine(root,"trend-settings.json"));
        ui.CodeNameFiles=n=>new string[0];
        var state=new List<Instance>{new Instance{Number=1,ProcessId=111}};
        Check(!LauncherTests.Children(ui.Snapshot(0,state)).OfType<System.Windows.Controls.Button>().Any(b=>"用量趋势".Equals(b.Content)),"instances page has no trend entry");
        var section=(System.Windows.Controls.Canvas)typeof(LauncherUI).GetField("trendSection",flags).GetValue(ui);
        var content=(System.Windows.Controls.Canvas)typeof(LauncherUI).GetField("accountsContent",flags).GetValue(ui);
        ui.Snapshot(1,state);
        Func<IEnumerable<string>> texts=()=>LauncherTests.Children(section).OfType<System.Windows.Controls.TextBlock>().Select(t=>t.Text);
        Check(texts().Any(t=>t.StartsWith("还没有观察记录")),"empty trend state");
        double empty=content.Height;
        DateTime now=DateTime.UtcNow;
        ui.SetTrend(new[]{new TrendPoint{Account=1,At=now.AddHours(-3),Five=10,Week=20},new TrendPoint{Account=1,At=now.AddHours(-2),Five=30,Week=25},new TrendPoint{Account=1,At=now.AddHours(-1),Five=5,Week=30}});
        Check(texts().Any(t=>t=="建议下一个使用：账号 01")&&texts().Any(t=>t=="30%"),"suggestion and latest values shown");
        Check(texts().Any(t=>t.StartsWith("按当前速度")),"forecast shown");
        Check(LauncherTests.Children(section).OfType<System.Windows.Controls.Button>().Any(b=>((string)b.Content).EndsWith("账号 01")),"suggestion can open the account");
        Check(content.Height>empty-1&&content.Height>700,"page grows to scroll");
        typeof(LauncherUI).GetField("settings",flags).SetValue(ui,new LauncherSettings{ShowUsage=false});
        ui.SetTrend(new TrendPoint[0]);
        Check(texts().Any(t=>t.StartsWith("用量趋势未开启"))&&!LauncherTests.Children(section).OfType<System.Windows.Shapes.Polyline>().Any(),"trend off shows only the notice");
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
            second.SetResult(new List<Instance>());Pump(b); first.SetException(new IOException("stale failure"));Pump(a);Check(b.Result&&a.Result,"old caller adopts the new scan instead of failing");
            var third=new TaskCompletionSource<List<Instance>>();var fourth=new TaskCompletionSource<List<Instance>>();calls=0;ui.ScanProvider=()=>++calls==1?third.Task:fourth.Task;
            var c=(Task<bool>)scan.Invoke(ui,null);var d=(Task<bool>)scan.Invoke(ui,null);third.SetResult(new List<Instance>());Check(!c.IsCompleted,"superseded caller waits for the new scan");
            fourth.SetException(new IOException("newest failure"));Pump(d);Pump(c);Check(!c.Result&&!d.Result&&(string)typeof(LauncherUI).GetField("scanError",flags).GetValue(ui)=="newest failure","superseded caller reports the real reason");
            var fifth=new TaskCompletionSource<List<Instance>>();ui.ScanProvider=()=>fifth.Task;var e=(Task<bool>)scan.Invoke(ui,null);fifth.SetResult(new List<Instance>());Pump(e);Check(e.Result,"scan recovers health");
            Check((bool)typeof(LauncherUI).GetField("healthy",flags).GetValue(ui),"stale failure cannot clear health");
            var finalScan=new TaskCompletionSource<List<Instance>>();ui.ScanProvider=()=>finalScan.Task;int validations=0;ui.ValidateStorage=n=>{ validations++;return Task.FromResult(0); };
            var open=typeof(LauncherUI).GetMethod("CheckAndOpen",flags);var operation=(Task)open.Invoke(ui,new object[]{2,0,false});
            var checking=(HashSet<int>)typeof(LauncherUI).GetField("checking",flags).GetValue(ui);Check(checking.Contains(2),"account stays occupied during final scan");
            var duplicate=(Task)open.Invoke(ui,new object[]{2,0,false});Check(duplicate.IsCompleted&&validations==1,"duplicate account operation rejected");
            typeof(LauncherUI).GetField("closed",flags).SetValue(ui,true);finalScan.SetResult(new List<Instance>());Pump(operation);Check(!checking.Contains(2),"account occupation released in finally");
            StorageRiskChecks(root,flags);
            LinkTargetChecks();
            TrendSectionChecks(root,flags);
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
            foreach(string text in new[]{"名称","分组","路径","状态","上次账号","操作","页面项目","失效项目","未分组","工作","正常","不可用","账号 02","未使用","共 2 个项目 · 上次账号仅用于默认选择，不绑定项目"}) Check(pageTexts.Contains(text),"project page shows "+text);
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
            Check(panelReads==3&&popupUsage[9].FiveHour.Percent==21,"retries are bounded and keep the last five-hour value");
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
