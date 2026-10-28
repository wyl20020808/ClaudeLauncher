#import <Cocoa/Cocoa.h>
#import <ApplicationServices/ApplicationServices.h>
#import <libproc.h>
#import <sys/sysctl.h>
#import <sys/file.h>
#import <unistd.h>
#import <errno.h>
#import <sys/stat.h>

static NSString * const BundleID = @"local.claude.profile-launcher";
static NSString * const WakeNotification = @"local.claude.profile-launcher.show";
static NSString *ProfilePath(NSInteger n) {
    NSString *name = n == 1 ? @"Claude" : n == 2 ? @"Claude-Second" : [NSString stringWithFormat:@"Claude-Profile-%02ld", (long)n];
    return [[NSHomeDirectory() stringByAppendingPathComponent:@"Library/Application Support"] stringByAppendingPathComponent:name];
}
// Claude Code (Code tab) defaults to the shared ~/.claude and ~/.claude.json; give 02-10 their own. 01 keeps the default.
static NSString *CodeConfigPath(NSInteger n) {
    return n == 1 ? nil : [ProfilePath(n) stringByAppendingPathComponent:@"ClaudeCodeConfig"];
}
static NSString *Canonical(NSString *path) {
    return [[path stringByStandardizingPath] stringByResolvingSymlinksInPath];
}
// Inspect metadata only. Never open cookies, credential stores, or account configuration contents.
static NSString *CheckStorageNode(NSString *path, BOOL recursive) {
    struct stat info;
    if (lstat(path.fileSystemRepresentation, &info) != 0)
        return errno == ENOENT ? nil : [NSString stringWithFormat:@"无法检查存储路径：%@", path];
    if (S_ISLNK(info.st_mode)) return [NSString stringWithFormat:@"隔离检查失败：路径是符号链接：%@", path];
    if (!S_ISDIR(info.st_mode) && !S_ISREG(info.st_mode)) return [NSString stringWithFormat:@"隔离检查失败：存储类型异常：%@", path];
    if (info.st_uid != getuid()) return [NSString stringWithFormat:@"隔离检查失败：目录或文件不属于当前用户：%@", path];
    if (S_ISREG(info.st_mode) && info.st_nlink > 1) return [NSString stringWithFormat:@"隔离检查失败：存储文件有硬链接：%@", path];
    if (recursive && S_ISDIR(info.st_mode)) {
        NSError *error = nil;
        NSArray *names = [NSFileManager.defaultManager contentsOfDirectoryAtPath:path error:&error];
        if (!names) return [NSString stringWithFormat:@"无法检查存储目录：%@（%@）",path,error.localizedDescription];
        for (NSString *name in names) {
            NSString *failure = CheckStorageNode([path stringByAppendingPathComponent:name], YES);
            if (failure) return failure;
        }
    }
    return nil;
}
static NSString *ValidateProfileAtPath(NSString *path, NSString *expected) {
    if (![path isEqual:expected] || !path.isAbsolutePath || ![path isEqual:path.stringByStandardizingPath]) return @"账号数据路径不符合固定目录约定。";
    NSString *prefix = @"/";
    for (NSString *component in path.pathComponents) {
        if ([component isEqual:@"/"]) continue;
        prefix = [prefix stringByAppendingPathComponent:component];
        struct stat info;
        if (lstat(prefix.fileSystemRepresentation,&info) != 0) {
            if (errno == ENOENT) break;
            return [NSString stringWithFormat:@"无法检查路径：%@",prefix];
        }
        if (S_ISLNK(info.st_mode) || !S_ISDIR(info.st_mode)) return [NSString stringWithFormat:@"账号目录或其上级是链接/非目录，已停止：%@",prefix];
    }
    NSString *failure = CheckStorageNode(path,NO);
    if (failure) return failure;
    // Chromium's SingletonSocket/SingletonLock are expected links; they are not credential storage.
    for (NSString *relative in @[@"Cookies",@"Cookies-journal",@"Network",@"Local Storage",@"Session Storage",@"IndexedDB",@"Partitions",@"Local State",@"Preferences",@"config.json",@"Cache",@"Code Cache",@"Service Worker",@"ClaudeCodeConfig"]) {
        failure = CheckStorageNode([path stringByAppendingPathComponent:relative],YES);
        if (failure) return failure;
    }
    return nil;
}
static NSString *ValidateProfile(NSInteger account) {
    if (account < 1 || account > 10) return @"账号编号无效。";
    return ValidateProfileAtPath(ProfilePath(account),ProfilePath(account));
}
static BOOL LoginConflict(NSDictionary<NSNumber *,NSArray *> *processes, NSInteger account) {
    for (NSNumber *key in processes) if (key.integerValue != account && [processes[key] count]) return YES;
    return NO;
}
static NSArray<NSString *> *Arguments(pid_t pid) {
    int mib[] = {CTL_KERN, KERN_PROCARGS2, pid};
    size_t length = 0;
    if (sysctl(mib, 3, NULL, &length, NULL, 0) != 0 || length < sizeof(int)) return nil;
    char *buffer = calloc(1, length);
    if (!buffer) return nil;
    if (sysctl(mib, 3, buffer, &length, NULL, 0) != 0) { free(buffer); return nil; }
    int argc = 0; memcpy(&argc, buffer, sizeof(argc));
    char *p = buffer + sizeof(argc), *end = buffer + length;
    while (p < end && *p) p++; // Skip the kernel's executable path.
    while (p < end && !*p) p++;
    NSMutableArray *args = [NSMutableArray array];
    for (int i = 0; i < argc && p < end; i++) {
        size_t count = strnlen(p, (size_t)(end-p));
        if (p + count == end) break;
        NSString *arg = [[NSString alloc] initWithBytes:p length:count encoding:NSUTF8StringEncoding];
        if (!arg) break;
        [args addObject:arg]; p += count + 1;
    }
    free(buffer); // Never parse or retain the environment following argv.
    return args.count == (NSUInteger)argc && argc > 0 ? args : nil;
}
static NSInteger AccountForArguments(NSArray<NSString *> *args) {
    NSString *path = nil;
    for (NSUInteger i = 1; i < args.count; i++) {
        NSString *arg = args[i];
        if ([arg hasPrefix:@"--type="] || [arg isEqual:@"--type"]) return 0;
        if ([arg hasPrefix:@"--user-data-dir="]) { if (path) return -1; path = [arg substringFromIndex:16]; }
        else if ([arg isEqual:@"--user-data-dir"]) {
            if (path) return -1;
            if (++i == args.count) return -1;
            path = args[i];
        }
    }
    if (!path) return 1;
    if (![path isAbsolutePath]) return -1;
    for (NSInteger n = 1; n <= 10; n++) if ([Canonical(path) isEqual:Canonical(ProfilePath(n))]) return n;
    return 0;
}
static BOOL IsMainExecutable(NSString *path) {
    for (NSString *root in @[@"/Applications/Claude.app", [NSHomeDirectory() stringByAppendingPathComponent:@"Applications/Claude.app"]]) {
        if ([Canonical(path) isEqual:Canonical([root stringByAppendingPathComponent:@"Contents/MacOS/Claude"])]) return YES;
    }
    return NO;
}
static NSDictionary<NSNumber *, NSArray<NSNumber *> *> *Scan(NSString **failure) {
    int capacity = proc_listallpids(NULL, 0);
    if (capacity <= 0) { *failure = @"无法读取进程列表。为避免重复启动，已暂停启动操作。"; return nil; }
    capacity += 256;
    pid_t *pids = calloc((size_t)capacity, sizeof(pid_t));
    if (!pids) { *failure = @"无法分配进程检查缓冲区，已停止启动。"; return nil; }
    int count = proc_listallpids(pids, capacity * (int)sizeof(pid_t));
    if (count <= 0 || count >= capacity) { free(pids); *failure = @"进程列表读取不完整，请稍后重试。"; return nil; }
    NSMutableDictionary *result = [NSMutableDictionary dictionary];
    NSMutableSet *seen = [NSMutableSet set];
    for (int i = 0; i < count; i++) {
        char path[PROC_PIDPATHINFO_MAXSIZE] = {0};
        if (proc_pidpath(pids[i], path, sizeof(path)) <= 0) continue;
        if (!IsMainExecutable(@(path))) continue;
        [seen addObject:@(pids[i])];
        NSArray *args = Arguments(pids[i]);
        if (!args) {
            if (kill(pids[i], 0) == -1 && errno == ESRCH) continue;
            *failure = [NSString stringWithFormat:@"无法读取 Claude 主进程 PID %d 的启动参数。已暂停启动，未结束任何进程。", pids[i]];
            free(pids); return nil;
        }
        NSInteger account = AccountForArguments(args);
        if (account < 0) { *failure = @"Claude 使用无法安全识别的数据目录参数。已暂停启动。"; free(pids); return nil; }
        // Keep unmanaged official instances under 0 so login protection also detects them.
        NSMutableArray *list = result[@(account)];
        if (!list) result[@(account)] = list = [NSMutableArray array];
        [list addObject:@(pids[i])];
    }
    free(pids);
    for (NSRunningApplication *app in NSWorkspace.sharedWorkspace.runningApplications) {
        if ([app.bundleIdentifier isEqual:@"com.anthropic.claudefordesktop"] && !app.terminated && ![seen containsObject:@(app.processIdentifier)]) {
            *failure = @"发现无法核对启动参数的 Claude 实例。请稍后重试；不会重复启动或结束它。";
            return nil;
        }
    }
    return result;
}
static BOOL VisibleWindow(pid_t pid) {
    NSArray *windows = CFBridgingRelease(CGWindowListCopyWindowInfo(kCGWindowListOptionOnScreenOnly | kCGWindowListExcludeDesktopElements, kCGNullWindowID));
    for (NSDictionary *window in windows) {
        if ([window[(id)kCGWindowOwnerPID] intValue] == pid && [window[(id)kCGWindowLayer] intValue] == 0) return YES;
    }
    return NO;
}

@interface ActionButton : NSButton
@end
@implementation ActionButton
- (void)drawRect:(NSRect)dirtyRect {
    NSRect rect = NSInsetRect(self.bounds,1,1);
    NSBezierPath *shape = [NSBezierPath bezierPathWithRoundedRect:rect xRadius:8 yRadius:8];
    NSColor *fill = [NSColor colorWithSRGBRed:0.66 green:0.30 blue:0.20 alpha:1];
    if ([self.title isEqualToString:@"创建"]) fill = [NSColor colorWithSRGBRed:0.16 green:0.37 blue:0.67 alpha:1];
    else if ([self.title isEqualToString:@"打开"]) fill = [NSColor colorWithSRGBRed:0.18 green:0.43 blue:0.32 alpha:1];
    if (!self.enabled) fill = NSColor.quaternaryLabelColor;
    if (self.highlighted && self.enabled) fill = [fill blendedColorWithFraction:0.18 ofColor:NSColor.blackColor];
    [fill setFill]; [shape fill];
    NSDictionary *attributes = @{NSFontAttributeName:[NSFont systemFontOfSize:13 weight:NSFontWeightSemibold],NSForegroundColorAttributeName:self.enabled ? NSColor.whiteColor : NSColor.secondaryLabelColor};
    NSSize size = [self.title sizeWithAttributes:attributes];
    [self.title drawAtPoint:NSMakePoint((NSWidth(rect)-size.width)/2+1,(NSHeight(rect)-size.height)/2+1) withAttributes:attributes];
    if (self.window.firstResponder == self && NSApp.active) {
        [NSColor.keyboardFocusIndicatorColor setStroke]; shape.lineWidth = 3; [shape stroke];
    }
}
@end

@interface Launcher : NSObject <NSApplicationDelegate, NSWindowDelegate>
@property NSWindow *window;
@property NSMutableArray<NSButton *> *buttons;
@property NSMutableArray<NSTextField *> *statuses;
@property NSTextField *message;
@property NSMutableDictionary<NSNumber *, NSDate *> *starting;
@property NSMutableDictionary<NSNumber *, NSString *> *errors;
@property NSDictionary<NSNumber *, NSArray<NSNumber *> *> *processes;
@property NSString *scanError;
@property NSTimer *timer;
@property int lockFD;
@property NSInteger loginAccount;
@property NSButton *endLoginButton;
@property NSTextField *summaryLabel;
@end
@implementation Launcher
- (void)applicationDidFinishLaunching:(NSNotification *)note {
    NSString *support = [NSHomeDirectory() stringByAppendingPathComponent:@"Library/Application Support/Claude Launcher"];
    NSError *error;
    if (![NSFileManager.defaultManager createDirectoryAtPath:support withIntermediateDirectories:YES attributes:nil error:&error]) {
        [self alert:@"无法打开启动器" detail:error.localizedDescription]; [NSApp terminate:nil]; return;
    }
    self.lockFD = open([[support stringByAppendingPathComponent:@"instance.lock"] fileSystemRepresentation], O_CREAT | O_RDWR | O_NOFOLLOW, 0600);
    if (self.lockFD < 0) { [self alert:@"无法建立启动器实例锁" detail:@"请检查 Claude Launcher 目录权限。未启动 Claude。"] ; [NSApp terminate:nil]; return; }
    if (flock(self.lockFD, LOCK_EX | LOCK_NB) != 0) {
        if (errno != EWOULDBLOCK) { [self alert:@"无法读取启动器实例锁" detail:@"未启动 Claude，请检查目录权限。"] ; [NSApp terminate:nil]; return; }
        [NSDistributedNotificationCenter.defaultCenter postNotificationName:WakeNotification object:nil userInfo:nil deliverImmediately:YES];
        for (NSRunningApplication *other in [NSRunningApplication runningApplicationsWithBundleIdentifier:BundleID])
            if (other.processIdentifier != getpid()) [other activateWithOptions:NSApplicationActivateIgnoringOtherApps];
        [NSApp terminate:nil]; return;
    }
    [NSDistributedNotificationCenter.defaultCenter addObserver:self selector:@selector(show:) name:WakeNotification object:nil];
    self.starting = [NSMutableDictionary dictionary]; self.errors = [NSMutableDictionary dictionary];
    NSInteger savedLogin = [NSUserDefaults.standardUserDefaults integerForKey:@"loginProtectionAccount"];
    self.loginAccount = savedLogin >= 1 && savedLogin <= 10 ? savedLogin : 0;
    [self makeWindow]; [self refresh]; [self show:nil];
    self.timer = [NSTimer scheduledTimerWithTimeInterval:1.0 target:self selector:@selector(refresh) userInfo:nil repeats:YES];
}
- (void)alert:(NSString *)title detail:(NSString *)detail {
    NSAlert *alert = [NSAlert new]; alert.messageText = title; alert.informativeText = detail;
    [alert addButtonWithTitle:@"知道了"]; [alert runModal];
}
- (NSTextField *)label:(NSString *)text frame:(NSRect)frame size:(CGFloat)size {
    NSTextField *label = [NSTextField wrappingLabelWithString:text]; label.frame = frame;
    label.font = [NSFont systemFontOfSize:size]; [self.window.contentView addSubview:label]; return label;
}
- (void)makeWindow {
    self.window = [[NSWindow alloc] initWithContentRect:NSMakeRect(0,0,760,520) styleMask:NSWindowStyleMaskTitled | NSWindowStyleMaskClosable | NSWindowStyleMaskMiniaturizable backing:NSBackingStoreBuffered defer:NO];
    self.window.title = @"Claude 启动器"; self.window.delegate = self; self.window.releasedWhenClosed = NO; [self.window center];
    NSImage *icon = [[NSImage alloc] initWithContentsOfFile:[NSBundle.mainBundle pathForResource:@"Claude" ofType:@"icns"]];
    NSApp.applicationIconImage = icon;
    NSImageView *image = [[NSImageView alloc] initWithFrame:NSMakeRect(28,444,42,42)]; image.image = icon; [self.window.contentView addSubview:image];
    NSTextField *title = [self label:@"Claude 启动器" frame:NSMakeRect(82,450,440,30) size:24]; title.font = [NSFont systemFontOfSize:24 weight:NSFontWeightSemibold];
    self.summaryLabel = [self label:@"正在读取状态" frame:NSMakeRect(526,454,206,24) size:12]; self.summaryLabel.alignment = NSTextAlignmentRight; self.summaryLabel.textColor = NSColor.secondaryLabelColor;
    for (NSInteger column=0; column<2; column++) {
        NSBox *group = [[NSBox alloc] initWithFrame:NSMakeRect(28+360*column,62,344,360)];
        group.boxType = NSBoxCustom; group.titlePosition = NSNoTitle; group.cornerRadius = 12;
        group.fillColor = NSColor.controlBackgroundColor; group.borderColor = NSColor.separatorColor; group.borderWidth = 0.5;
        [self.window.contentView addSubview:group];
        for (NSInteger row=1;row<5;row++) {
            NSBox *line = [[NSBox alloc] initWithFrame:NSMakeRect(44+360*column,62+row*72,312,1)]; line.boxType=NSBoxSeparator; [self.window.contentView addSubview:line];
        }
    }
    self.buttons = [NSMutableArray array]; self.statuses = [NSMutableArray array];
    for (NSInteger n = 1; n <= 10; n++) {
        CGFloat x = 28 + ((n-1)%2)*360, y = 350 - ((n-1)/2)*72;
        NSTextField *name = [self label:[NSString stringWithFormat:@"账号 %02ld",(long)n] frame:NSMakeRect(x+18,y+37,148,23) size:15]; name.font = [NSFont systemFontOfSize:15 weight:NSFontWeightSemibold];
        if(n==1) { NSTextField *badge=[self label:@"默认" frame:NSMakeRect(x+89,y+38,52,20) size:11]; badge.textColor=NSColor.secondaryLabelColor; }
        ActionButton *button = [ActionButton buttonWithTitle:@"启动" target:self action:@selector(selectAccount:)];
        button.tag = n; button.frame = NSMakeRect(x+246,y+20,80,32); button.bordered = NO; button.buttonType = NSButtonTypeMomentaryPushIn;
        button.toolTip = ProfilePath(n); [self.window.contentView addSubview:button]; [self.buttons addObject:button];
        NSTextField *status = [self label:@"读取状态…" frame:NSMakeRect(x+18,y+12,214,20) size:12]; [self.statuses addObject:status];
    }
    self.message = [self label:@"" frame:NSMakeRect(28,10,520,40) size:11];
    self.message.maximumNumberOfLines=3; self.message.lineBreakMode=NSLineBreakByWordWrapping; self.message.textColor=NSColor.labelColor;
    self.endLoginButton = [NSButton buttonWithTitle:@"结束登录保护" target:self action:@selector(endLogin:)];
    self.endLoginButton.frame = NSMakeRect(568,17,146,30); self.endLoginButton.bezelStyle = NSBezelStyleRounded;
    [self.window.contentView addSubview:self.endLoginButton];
    self.message.accessibilityIdentifier = @"launcher-feedback";
    NSMenu *menu = [NSMenu new]; NSMenuItem *root = [NSMenuItem new]; [menu addItem:root];
    NSMenu *appMenu = [NSMenu new]; root.submenu = appMenu;
    NSMenuItem *show = [appMenu addItemWithTitle:@"显示账号窗口" action:@selector(show:) keyEquivalent:@"0"]; show.target = self;
    [appMenu addItem:NSMenuItem.separatorItem];
    [appMenu addItemWithTitle:@"退出 Claude 启动器" action:@selector(terminate:) keyEquivalent:@"q"];
    NSApp.mainMenu = menu;
}
- (void)show:(id)sender { [self.window deminiaturize:nil]; [self.window makeKeyAndOrderFront:nil]; [NSApp activateIgnoringOtherApps:YES]; }
- (BOOL)applicationShouldHandleReopen:(NSApplication *)app hasVisibleWindows:(BOOL)visible { [self show:nil]; return YES; }
- (BOOL)applicationShouldTerminateAfterLastWindowClosed:(NSApplication *)app { return NO; }
- (void)refresh {
    NSString *failure = nil; NSDictionary *snapshot = Scan(&failure);
    self.scanError = failure; if (snapshot) self.processes = snapshot;
    self.endLoginButton.enabled = self.loginAccount != 0;
    self.endLoginButton.hidden = self.loginAccount == 0;
    NSUInteger running = 0;
    for (NSInteger i=1;i<=10;i++) if ([self.processes[@(i)] count]) running++;
    self.summaryLabel.stringValue = failure ? @"状态暂不可用" : [NSString stringWithFormat:@"%lu 个运行中",(unsigned long)running];
    for (NSInteger n = 1; n <= 10; n++) {
        NSNumber *key = @(n); NSArray *pids = self.processes[key];
        NSDate *since = self.starting[key];
        if (since && pids.count && VisibleWindow([pids.firstObject intValue])) { [self.starting removeObjectForKey:key]; since = nil; }
        if (since && -since.timeIntervalSinceNow > 35) {
            [self.starting removeObjectForKey:key]; since = nil;
            self.errors[key] = pids.count ? @"进程已运行，但未发现可见窗口。请点击恢复。" : @"启动失败：35 秒内未发现主进程。请重试。";
            self.message.stringValue = [NSString stringWithFormat:@"账号 %02ld：%@", (long)n, self.errors[key]];
        }
        BOOL exists = [NSFileManager.defaultManager fileExistsAtPath:ProfilePath(n)];
        NSString *state = failure ? @"状态读取失败" : since ? @"正在启动…" : pids.count > 1 ? @"运行中 · 多个实例，请检查" : pids.count ? @"运行中" : @"未运行";
        if (!failure && !since && !pids.count && self.errors[key]) state = @"未运行 · 启动失败";
        self.statuses[n-1].stringValue = state;
        self.statuses[n-1].textColor = failure || self.errors[key] ? NSColor.systemRedColor : pids.count ? NSColor.labelColor : NSColor.secondaryLabelColor;
        self.statuses[n-1].toolTip = pids.count ? [NSString stringWithFormat:@"主进程 PID：%@",pids] : ProfilePath(n);
        self.buttons[n-1].title = since ? @"启动中" : pids.count ? @"打开" : exists ? @"启动" : @"创建";
        self.buttons[n-1].enabled = !failure && !since;
        self.buttons[n-1].accessibilityLabel = [NSString stringWithFormat:@"账号 %02ld，%@",(long)n,state];
    }
    if (failure) self.message.stringValue = failure;
    else if (self.loginAccount) self.message.stringValue = [NSString stringWithFormat:@"账号 %02ld 登录保护中。%@",(long)self.loginAccount,LoginConflict(snapshot,self.loginAccount) ? @"检测到其他 Claude！请停止登录，并正常退出其他实例。" : @"完成登录并核对身份后，点「结束登录保护」恢复多开。"];
}
- (void)endLogin:(id)sender {
    self.loginAccount = 0;
    [NSUserDefaults.standardUserDefaults removeObjectForKey:@"loginProtectionAccount"];
    self.message.stringValue = @"已结束登录保护。只有确认登录完成、身份正确后，才应同时打开其他账号。";
    [self refresh];
}
- (void)selectAccount:(NSButton *)sender {
    NSNumber *key = @(sender.tag);
    if (self.starting[key]) return;
    [self refresh];
    if (self.scanError) { [self alert:@"暂时无法识别账号" detail:self.scanError]; return; }
    if (self.loginAccount && (self.loginAccount != sender.tag || LoginConflict(self.processes,sender.tag))) {
        [self alert:@"登录保护：已阻止切换或启动" detail:@"请先完成目标账号登录并核对身份，然后结束登录保护。外部启动的其他 Claude 需要你正常退出。"] ; return;
    }
    NSString *storageFailure = ValidateProfile(sender.tag);
    if (storageFailure) { [self alert:@"账号存储隔离检查未通过" detail:storageFailure]; return; }
    NSArray *pids = self.processes[key];
    if (pids.count > 1) { [self alert:@"同一目录发现多个主进程" detail:@"为避免切错账号，未激活或结束任何实例。请自行检查 Claude 窗口。"] ; return; }
    [self.errors removeObjectForKey:key];
    if (pids.count == 1) { [self activatePID:[pids.firstObject intValue] account:sender.tag]; return; }
    NSString *bundlePath = nil;
    for (NSString *path in @[@"/Applications/Claude.app", [NSHomeDirectory() stringByAppendingPathComponent:@"Applications/Claude.app"]]) {
        if ([NSFileManager.defaultManager isExecutableFileAtPath:[path stringByAppendingPathComponent:@"Contents/MacOS/Claude"]]) { bundlePath = path; break; }
    }
    if (!bundlePath) { [self alert:@"未找到官方 Claude.app" detail:@"请将官方 Claude.app 安装到 /Applications 或 ~/Applications 后重试。"] ; return; }
    NSError *error = nil; BOOL directory = NO;
    BOOL exists = [NSFileManager.defaultManager fileExistsAtPath:ProfilePath(sender.tag) isDirectory:&directory];
    if (!exists && !self.loginAccount) {
        NSAlert *notice = [NSAlert new]; notice.messageText = @"创建账号空间";
        notice.informativeText = @"将创建独立的本地数据目录。登录前请正常退出其他 Claude 实例，避免浏览器登录回调切错窗口。";
        [notice addButtonWithTitle:@"取消"]; [notice addButtonWithTitle:@"创建并打开"];
        if ([notice runModal] != NSAlertSecondButtonReturn) return;
        [self refresh];
        if (self.scanError || self.processes[key].count) { self.message.stringValue = @"状态已变化，未启动新实例，请重新点击。"; return; }
    }
    if ((exists && !directory) || (!exists && ![NSFileManager.defaultManager createDirectoryAtPath:ProfilePath(sender.tag) withIntermediateDirectories:YES attributes:@{NSFilePosixPermissions:@0700} error:&error])) {
        [self alert:@"无法使用账号目录" detail:error.localizedDescription ?: @"该路径已存在且不是目录。未覆盖任何数据。"] ; return;
    }
    NSString *codeConfig = CodeConfigPath(sender.tag);
    if (codeConfig && ![NSFileManager.defaultManager fileExistsAtPath:codeConfig] && ![NSFileManager.defaultManager createDirectoryAtPath:codeConfig withIntermediateDirectories:NO attributes:@{NSFilePosixPermissions:@0700} error:&error]) {
        [self alert:@"无法创建 Claude Code 独立配置目录" detail:error.localizedDescription]; return;
    }
    storageFailure = ValidateProfile(sender.tag);
    if (storageFailure) { [self alert:@"创建后隔离检查未通过" detail:storageFailure]; return; }
    self.starting[key] = [NSDate date]; [self refresh];
    self.message.stringValue = [NSString stringWithFormat:@"正在启动账号 %02ld…",(long)sender.tag];
    NSWorkspaceOpenConfiguration *config = [NSWorkspaceOpenConfiguration configuration];
    config.createsNewApplicationInstance = YES; config.activates = YES;
    config.arguments = sender.tag == 1 ? @[] : @[[ @"--user-data-dir=" stringByAppendingString:ProfilePath(sender.tag)]];
    if (codeConfig) config.environment = @{@"CLAUDE_CONFIG_DIR":codeConfig};
    [NSWorkspace.sharedWorkspace openApplicationAtURL:[NSURL fileURLWithPath:bundlePath] configuration:config completionHandler:^(NSRunningApplication *app, NSError *launchError) {
        dispatch_async(dispatch_get_main_queue(), ^{
            if (launchError) { [self.starting removeObjectForKey:key]; self.errors[key] = launchError.localizedDescription; self.message.stringValue = [@"启动失败：" stringByAppendingString:launchError.localizedDescription]; }
            [self refresh];
        });
    }];
}
- (void)activatePID:(pid_t)pid account:(NSInteger)account {
    // Revalidate the identity immediately before activating; never fall back to a bundle-wide open.
    NSArray *args = Arguments(pid); char path[PROC_PIDPATHINFO_MAXSIZE] = {0};
    if (!args || AccountForArguments(args) != account || proc_pidpath(pid,path,sizeof(path)) <= 0 || !IsMainExecutable(@(path))) {
        [self alert:@"实例状态已改变" detail:@"无法核对该 PID。请稍后再次点击，未启动新实例。"] ; [self refresh]; return;
    }
    NSRunningApplication *app = [NSRunningApplication runningApplicationWithProcessIdentifier:pid];
    if (!app || app.terminated) { [self alert:@"实例已退出" detail:@"请再次点击启动。"] ; return; }
    [app unhide];
    if (@available(macOS 14.0, *)) {
        [NSApp yieldActivationToApplication:app];
        [app activateFromApplication:NSRunningApplication.currentApplication options:NSApplicationActivateAllWindows];
    } else {
        [app activateWithOptions:NSApplicationActivateAllWindows | NSApplicationActivateIgnoringOtherApps];
    }
    if (AXIsProcessTrusted()) {
        AXUIElementRef target = AXUIElementCreateApplication(pid); CFTypeRef value = NULL;
        AXError read = AXUIElementCopyAttributeValue(target, kAXWindowsAttribute, &value);
        if (read == kAXErrorSuccess && value && CFGetTypeID(value) == CFArrayGetTypeID()) {
            NSArray *windows = (__bridge NSArray *)value; BOOL restored = NO;
            NSUInteger minimizedCount = 0, restoredCount = 0;
            for (id item in windows) {
                AXUIElementRef window = (__bridge AXUIElementRef)item;
                CFTypeRef before = NULL;
                if (AXUIElementCopyAttributeValue(window, kAXMinimizedAttribute, &before) == kAXErrorSuccess && before == kCFBooleanTrue) minimizedCount++;
                if (before) CFRelease(before);
                AXUIElementSetAttributeValue(window, kAXMinimizedAttribute, kCFBooleanFalse);
                if (AXUIElementPerformAction(window, kAXRaiseAction) == kAXErrorSuccess) restored = YES;
                CFTypeRef after = NULL;
                if (AXUIElementCopyAttributeValue(window, kAXMinimizedAttribute, &after) == kAXErrorSuccess && after == kCFBooleanFalse) restoredCount++;
                if (after) CFRelease(after);
            }
            AXUIElementSetAttributeValue(target, kAXFrontmostAttribute, kCFBooleanTrue);
            self.message.stringValue = [NSString stringWithFormat:@"账号 %02ld · PID %d：发现 %lu 个窗口，%lu 个原为最小化，%lu 个已展开。",(long)account,pid,(unsigned long)windows.count,(unsigned long)minimizedCount,(unsigned long)restoredCount];
            if (!windows.count) [self alert:@"Claude 在后台运行，但没有窗口" detail:@"已激活对应 PID。请使用 Claude 菜单尝试新建窗口；启动器不会强制结束或重复启动它。"];
            else if (!restored) [self alert:@"未能恢复窗口" detail:@"已激活对应 PID，但系统拒绝提升窗口。请检查辅助功能权限或稍后重试。"];
        } else [self alert:@"无法读取 Claude 窗口" detail:@"已激活对应 PID。请检查辅助功能权限，或稍后重试；未启动新实例。"];
        if (value) CFRelease(value); CFRelease(target);
    } else {
        dispatch_after(dispatch_time(DISPATCH_TIME_NOW, (int64_t)(0.7*NSEC_PER_SEC)), dispatch_get_main_queue(), ^{
            if (VisibleWindow(pid) && NSWorkspace.sharedWorkspace.frontmostApplication.processIdentifier == pid) return;
            [self show:nil];
            NSAlert *alert = [NSAlert new]; alert.messageText = @"该账号运行中，但窗口尚未恢复";
            alert.informativeText = @"窗口可能已最小化，或该进程没有窗口。恢复最小化窗口需要辅助功能权限，以读取并提升这个 PID 的窗口。请在系统设置 → 隐私与安全性 → 辅助功能中允许「Claude 启动器」，然后重新点击账号。不会修改系统权限或结束 Claude。";
            [alert addButtonWithTitle:@"打开辅助功能设置"]; [alert addButtonWithTitle:@"稍后"];
            if ([alert runModal] == NSAlertFirstButtonReturn) {
                NSDictionary *options = @{(__bridge NSString *)kAXTrustedCheckOptionPrompt:@YES}; AXIsProcessTrustedWithOptions((__bridge CFDictionaryRef)options);
                [NSWorkspace.sharedWorkspace openURL:[NSURL URLWithString:@"x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility"]];
            }
        });
    }
    if (!AXIsProcessTrusted()) self.message.stringValue = [NSString stringWithFormat:@"已请求激活账号 %02ld（PID %d）；窗口恢复权限尚未启用。",(long)account,pid];
}
@end

static int SelfTest(void) {
    @autoreleasepool {
        NSCAssert(AccountForArguments(@[@"Claude"]) == 1, @"default");
        for (NSInteger n=1;n<=10;n++) {
            NSCAssert(AccountForArguments(@[@"Claude",[@"--user-data-dir=" stringByAppendingString:ProfilePath(n)]]) == n,@"equals");
            NSCAssert(AccountForArguments(@[@"Claude",@"--user-data-dir",ProfilePath(n)]) == n,@"split");
        }
        NSCAssert(!CodeConfigPath(1),@"default keeps ~/.claude");
        NSMutableSet *configs = [NSMutableSet set];
        for (NSInteger n=2;n<=10;n++) { NSCAssert([CodeConfigPath(n) hasPrefix:[ProfilePath(n) stringByAppendingString:@"/"]],@"code config inside profile"); [configs addObject:CodeConfigPath(n)]; }
        NSCAssert(configs.count == 9,@"code config unique per account");
        NSCAssert(AccountForArguments(@[@"Claude",@"--type=renderer"]) == 0,@"renderer");
        NSCAssert(AccountForArguments(@[@"Claude",@"--type=gpu-process"]) == 0,@"gpu");
        NSCAssert(AccountForArguments(@[@"Claude",@"--user-data-dir"]) == -1,@"missing");
        NSCAssert(AccountForArguments(@[@"Claude",@"--user-data-dir=relative"]) == -1,@"relative");
        NSCAssert(AccountForArguments(@[@"Claude",@"--user-data-dir=/tmp/unmanaged"]) == 0,@"unmanaged");
        NSCAssert(!IsMainExecutable(@"/usr/local/bin/claude"),@"CLI excluded");
        NSCAssert(!IsMainExecutable(@"/Applications/Claude.app/Contents/Frameworks/Claude Helper.app/Contents/MacOS/Claude Helper"),@"helper excluded");
        NSCAssert(Arguments(getpid()).count > 0,@"kernel argv");
        NSCAssert(AccountForArguments(@[@"Claude",@"--user-data-dir=/tmp/a",@"--user-data-dir=/tmp/b"]) == -1,@"duplicate override rejected");
        NSCAssert(LoginConflict(@{@1:@[@123]},2),@"other account blocks login");
        NSCAssert(LoginConflict(@{@0:@[@123]},2),@"unmanaged official instance blocks login");
        NSCAssert(!LoginConflict(@{@2:@[@123]},2),@"own instance allowed");
        NSString *root = [NSFileManager.defaultManager.currentDirectoryPath stringByAppendingPathComponent:[@".claude-isolation-tests-" stringByAppendingString:NSUUID.UUID.UUIDString]];
        NSFileManager *fm = NSFileManager.defaultManager;
        NSString *a = [root stringByAppendingPathComponent:@"A"], *b = [root stringByAppendingPathComponent:@"B"];
        NSCAssert([fm createDirectoryAtPath:a withIntermediateDirectories:YES attributes:nil error:nil],@"test directory");
        NSCAssert([fm createDirectoryAtPath:b withIntermediateDirectories:YES attributes:nil error:nil],@"other test directory");
        NSCAssert(!ValidateProfileAtPath(a,a),@"separate real directory: %@ (%@)",a,ValidateProfileAtPath(a,a));
        NSCAssert(ValidateProfileAtPath(a,b),@"wrong path rejected");
        NSString *link = [root stringByAppendingPathComponent:@"alias"];
        NSCAssert([fm createSymbolicLinkAtPath:link withDestinationPath:a error:nil],@"root link fixture");
        NSCAssert(ValidateProfileAtPath(link,link),@"root symlink rejected");
        NSString *store = [a stringByAppendingPathComponent:@"Local Storage"];
        NSCAssert([fm createSymbolicLinkAtPath:store withDestinationPath:b error:nil],@"store link fixture");
        NSCAssert(ValidateProfileAtPath(a,a),@"nested storage symlink rejected");
        NSCAssert([fm removeItemAtPath:store error:nil],@"remove test link only");
        NSString *marker = [b stringByAppendingPathComponent:@"sentinel"];
        NSCAssert([@"synthetic-test-only" writeToFile:marker atomically:YES encoding:NSUTF8StringEncoding error:nil],@"synthetic marker");
        NSCAssert(linkat(AT_FDCWD,marker.fileSystemRepresentation,AT_FDCWD,[[a stringByAppendingPathComponent:@"Cookies"] fileSystemRepresentation],0)==0,@"hardlink fixture");
        NSCAssert(ValidateProfileAtPath(a,a),@"hardlinked cookie store rejected");
        NSCAssert([[NSString stringWithContentsOfFile:marker encoding:NSUTF8StringEncoding error:nil] isEqual:@"synthetic-test-only"],@"other fixture untouched");
        NSCAssert([fm removeItemAtPath:root error:nil],@"clean generated fixtures only");
        puts("PASS: login conflict guard, duplicate overrides, exact paths, symlink and hardlink rejection; synthetic fixtures only");
        puts("PASS: profile mapping, helper/CLI filtering, malformed arguments, kernel argv");
    } return 0;
}
int main(int argc, const char *argv[]) {
    @autoreleasepool {
        if (argc > 1 && strcmp(argv[1],"--self-test") == 0) return SelfTest();
        if (argc > 1 && strcmp(argv[1],"--diagnose") == 0) {
            NSString *failure=nil; NSDictionary *pids=Scan(&failure);
            if (failure) { puts(failure.UTF8String); return 1; }
            for (NSNumber *n in [[pids allKeys] sortedArrayUsingSelector:@selector(compare:)]) printf("account=%02d pids=%s\n",n.intValue,[pids[n] description].UTF8String);
            printf("accessibility=%s\n",AXIsProcessTrusted()?"yes":"no"); return 0;
        }
        NSApplication *app = NSApplication.sharedApplication; app.activationPolicy=NSApplicationActivationPolicyRegular;
        Launcher *delegate=[Launcher new]; app.delegate=delegate; [app run];
    } return 0;
}
