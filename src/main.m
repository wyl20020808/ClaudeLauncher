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

static NSColor *Hex(unsigned hex) {
    return [NSColor colorWithSRGBRed:((hex>>16)&255)/255.0 green:((hex>>8)&255)/255.0 blue:(hex&255)/255.0 alpha:1];
}
static NSImage *Art(NSString *name) {
    NSString *path = [NSBundle.mainBundle pathForResource:name ofType:@"webp"];
    return path ? [[NSImage alloc] initWithContentsOfFile:path] : nil;
}
static NSFont *BrushFont(CGFloat size) {
    for (NSString *name in @[@"STKaiti",@"Kaiti SC",@"KaitiSC-Regular",@"Kai"]) { NSFont *font = [NSFont fontWithName:name size:size]; if (font) return font; }
    return [NSFont systemFontOfSize:size];
}
static NSFont *SerifFont(CGFloat size) {
    NSFont *base = [NSFont systemFontOfSize:size weight:NSFontWeightRegular];
    NSFontDescriptor *serif = [base.fontDescriptor fontDescriptorWithDesign:NSFontDescriptorSystemDesignSerif];
    return serif ? [NSFont fontWithDescriptor:serif size:size] : base;
}
static NSTextField *Text(NSString *text, NSRect frame, NSFont *font, NSColor *color) {
    NSTextField *label = [NSTextField labelWithString:text]; label.frame = frame; label.font = font; label.textColor = color;
    label.lineBreakMode = NSLineBreakByTruncatingTail; return label;
}
static NSImageView *Symbol(NSString *name, NSRect frame, NSColor *tint) {
    NSImageView *view = [[NSImageView alloc] initWithFrame:frame];
    view.image = [NSImage imageWithSystemSymbolName:name accessibilityDescription:nil];
    view.symbolConfiguration = [NSImageSymbolConfiguration configurationWithPointSize:NSHeight(frame)-3 weight:NSFontWeightRegular];
    view.contentTintColor = tint; return view;
}
// Aspect-fill: scale to cover rect, crop overflow; alignX 0 = keep left edge, 1 = keep right edge.
static void DrawFill(NSImage *image, NSRect rect, CGFloat alignX) {
    if (!image) return;
    NSSize size = image.size; CGFloat scale = MAX(NSWidth(rect)/size.width, NSHeight(rect)/size.height);
    NSSize drawn = NSMakeSize(size.width*scale, size.height*scale);
    [NSGraphicsContext saveGraphicsState]; NSRectClip(rect);
    [image drawInRect:NSMakeRect(NSMinX(rect)+(NSWidth(rect)-drawn.width)*alignX, NSMidY(rect)-drawn.height/2, drawn.width, drawn.height) fromRect:NSZeroRect operation:NSCompositingOperationSourceOver fraction:1 respectFlipped:YES hints:nil];
    [NSGraphicsContext restoreGraphicsState];
}
static void DrawCentered(NSString *text, NSRect rect, NSDictionary *attributes) {
    NSSize size = [text sizeWithAttributes:attributes];
    [text drawAtPoint:NSMakePoint(NSMidX(rect)-size.width/2, NSMidY(rect)-size.height/2) withAttributes:attributes];
}

@interface Canvas : NSView
@property (copy) void (^paint)(NSRect bounds);
@end
@implementation Canvas
- (BOOL)isFlipped { return YES; }
- (void)drawRect:(NSRect)dirtyRect { if (self.paint) self.paint(self.bounds); }
@end

@interface ActionButton : NSButton
@end
@implementation ActionButton
- (void)drawRect:(NSRect)dirtyRect {
    NSRect rect = NSInsetRect(self.bounds,1,1);
    BOOL pill = [self.title hasPrefix:@"+"], filled = pill || [self.title isEqualToString:@"打开"];
    CGFloat radius = pill ? NSHeight(rect)/2 : 12;
    NSBezierPath *shape = [NSBezierPath bezierPathWithRoundedRect:rect xRadius:radius yRadius:radius];
    NSColor *fill = filled ? Hex(0xA8765A) : Hex(0xFFFFFF);
    NSColor *text = filled ? NSColor.whiteColor : [self.title isEqualToString:@"创建"] ? Hex(0x9A6447) : Hex(0x2B2622);
    if (!self.enabled) { fill = Hex(0xEFE9E1); text = Hex(0xA59B90); }
    if (self.highlighted && self.enabled) fill = [fill blendedColorWithFraction:0.12 ofColor:NSColor.blackColor];
    [fill setFill]; [shape fill];
    if (!filled) { [Hex(0xE3D9CD) setStroke]; shape.lineWidth = 1; [shape stroke]; }
    DrawCentered(self.title, self.bounds, @{NSFontAttributeName:[NSFont systemFontOfSize:pill ? 16 : 17 weight:NSFontWeightMedium],NSForegroundColorAttributeName:text});
    if (self.window.firstResponder == self && NSApp.active) {
        [NSColor.keyboardFocusIndicatorColor setStroke]; shape.lineWidth = 3; [shape stroke];
    }
}
@end

static const unsigned BadgePalette[4][2] = {{0xFCE7D4,0xC0743A},{0xE9E3FA,0x6A55C8},{0xDDF0E6,0x3C8A68},{0xFCE3DD,0xC75F47}};
static const unsigned CardFill = 0xFFFCF7;
@interface AccountCard : NSView
@property NSInteger number;
@property NSImage *art;
@property NSArray<NSString *> *chips;
@property NSColor *dot;
@property NSTextField *status;
@property ActionButton *button;
@end
@implementation AccountCard
- (BOOL)isFlipped { return YES; }
- (void)drawRect:(NSRect)dirtyRect {
    NSBezierPath *shape = [NSBezierPath bezierPathWithRoundedRect:NSInsetRect(self.bounds,0.5,0.5) xRadius:14 yRadius:14];
    [Hex(CardFill) setFill]; [shape fill]; [Hex(0xEDE4D8) setStroke]; [shape stroke];
    NSRect art = NSMakeRect(186,6,200,NSHeight(self.bounds)-12);
    [NSGraphicsContext saveGraphicsState];
    [[NSBezierPath bezierPathWithRoundedRect:art xRadius:12 yRadius:12] addClip];
    DrawFill(self.art, art, 0.75);
    [[[NSGradient alloc] initWithStartingColor:Hex(CardFill) endingColor:[Hex(CardFill) colorWithAlphaComponent:0]] drawInRect:NSMakeRect(NSMinX(art)-1,NSMinY(art),90,NSHeight(art)) angle:0];
    [NSGraphicsContext restoreGraphicsState];
    const unsigned *palette = BadgePalette[(self.number-1)%4];
    NSRect circle = NSMakeRect(22,NSMidY(self.bounds)-22,44,44);
    NSBezierPath *badge = [NSBezierPath bezierPathWithOvalInRect:circle];
    [Hex(palette[0]) setFill]; [badge fill]; [[Hex(palette[1]) colorWithAlphaComponent:0.25] setStroke]; [badge stroke];
    DrawCentered([NSString stringWithFormat:@"%ld",(long)self.number], circle, @{NSFontAttributeName:[NSFont systemFontOfSize:20 weight:NSFontWeightMedium],NSForegroundColorAttributeName:Hex(palette[1])});
    [self.dot setFill]; [[NSBezierPath bezierPathWithOvalInRect:NSMakeRect(86,51,9,9)] fill];
    NSDictionary *chipText = @{NSFontAttributeName:[NSFont systemFontOfSize:12],NSForegroundColorAttributeName:Hex(0x6E655C)};
    CGFloat x = 84;
    for (NSString *chip in self.chips) {
        NSRect rect = NSMakeRect(x,80,[chip sizeWithAttributes:chipText].width+16,24);
        NSBezierPath *path = [NSBezierPath bezierPathWithRoundedRect:rect xRadius:6 yRadius:6];
        [Hex(0xFAF6EF) setFill]; [path fill]; [Hex(0xE8DFD3) setStroke]; [path stroke];
        DrawCentered(chip, rect, chipText); x = NSMaxX(rect)+8;
    }
    // Quota rows are layout placeholders until a data source exists; empty tracks, no invented numbers.
    for (NSInteger row = 0; row < 2; row++) {
        NSBezierPath *track = [NSBezierPath bezierPathWithRoundedRect:NSMakeRect(436,44+row*46,150,6) xRadius:3 yRadius:3];
        [Hex(0xEEE8E0) setFill]; [track fill];
    }
}
@end

@interface Launcher : NSObject <NSApplicationDelegate, NSWindowDelegate>
@property NSWindow *window;
@property NSMutableArray<NSButton *> *buttons;
@property NSMutableArray<NSTextField *> *statuses;
@property NSMutableArray<AccountCard *> *cards;
@property NSInteger page;
@property NSArray<NSView *> *pages;
@property NSArray<NSView *> *navItems;
@property NSView *sidebar;
@property NSView *cardsView;
@property NSTextField *emptyHint;
@property NSButton *createButton;
@property NSTextView *logView;
@property NSString *lastLogged;
@property NSTextField *accessibilityStatus;
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
- (AccountCard *)cardFor:(NSInteger)n frame:(NSRect)frame art:(NSImage *)art {
    NSColor *ink = Hex(0x2B2622), *muted = Hex(0x6E655C);
    AccountCard *card = [[AccountCard alloc] initWithFrame:frame];
    card.number = n; card.art = art; card.dot = Hex(0xB9B1A7);
    card.chips = n == 1 ? @[@"默认目录",@"Code 默认"] : @[[ProfilePath(n).lastPathComponent stringByReplacingOccurrencesOfString:@"Claude-" withString:@""],@"Code 独立"];
    [card addSubview:Text([NSString stringWithFormat:@"账号 %02ld",(long)n], NSMakeRect(84,18,150,24), [NSFont systemFontOfSize:18 weight:NSFontWeightSemibold], ink)];
    card.status = Text(@"读取状态…", NSMakeRect(102,44,160,20), [NSFont systemFontOfSize:13], muted); [card addSubview:card.status];
    NSArray *rows = @[@[@"clock",@"5小时限额"],@[@"calendar",@"周限额"]];
    for (NSInteger row = 0; row < 2; row++) {
        CGFloat y = 18+row*46;
        [card addSubview:Symbol(rows[row][0], NSMakeRect(406,y+1,18,18), muted)];
        [card addSubview:Text(rows[row][1], NSMakeRect(436,y,120,20), [NSFont systemFontOfSize:14 weight:NSFontWeightMedium], ink)];
        NSTextField *pending = Text(@"未接入", NSMakeRect(592,y+19,44,16), [NSFont systemFontOfSize:12], Hex(0xA59B90)); pending.alignment = NSTextAlignmentRight;
        [card addSubview:pending];
    }
    card.button = [ActionButton buttonWithTitle:@"启动" target:self action:@selector(selectAccount:)];
    card.button.tag = n; card.button.frame = NSMakeRect(646,36,86,52); card.button.bordered = NO; card.button.buttonType = NSButtonTypeMomentaryPushIn;
    card.button.toolTip = ProfilePath(n); [card addSubview:card.button];
    NSButton *more = [NSButton buttonWithTitle:@"" image:[NSImage imageWithSystemSymbolName:@"ellipsis" accessibilityDescription:@"更多"] target:self action:@selector(showMore:)];
    more.frame = NSMakeRect(740,47,30,30); more.bordered = NO; more.tag = n; more.contentTintColor = Hex(0xA59B90);
    [card addSubview:more];
    return card;
}
- (NSView *)navItem:(NSString *)title symbol:(NSString *)symbol index:(NSInteger)index {
    __weak Launcher *weakSelf = self;
    Canvas *item = [[Canvas alloc] initWithFrame:NSMakeRect(20,176+index*56,232,48)];
    item.identifier = @(index).stringValue;
    item.paint = ^(NSRect bounds) {
        if (weakSelf.page != index) return;
        [Hex(0xEDDDC9) setFill]; [[NSBezierPath bezierPathWithRoundedRect:bounds xRadius:14 yRadius:14] fill];
    };
    [item addSubview:Symbol(symbol, NSMakeRect(20,14,21,21), Hex(0x3A332D))];
    [item addSubview:Text(title, NSMakeRect(56,13,160,22), [NSFont systemFontOfSize:16 weight:NSFontWeightMedium], Hex(0x2B2622))];
    [item addGestureRecognizer:[[NSClickGestureRecognizer alloc] initWithTarget:self action:@selector(navClicked:)]];
    item.accessibilityElement = YES; item.accessibilityRole = NSAccessibilityButtonRole; item.accessibilityLabel = title;
    return item;
}
- (void)makeWindow {
    NSApp.appearance = [NSAppearance appearanceNamed:NSAppearanceNameAqua]; // Artwork is light-only.
    self.window = [[NSWindow alloc] initWithContentRect:NSMakeRect(0,0,1080,720) styleMask:NSWindowStyleMaskTitled | NSWindowStyleMaskClosable | NSWindowStyleMaskMiniaturizable | NSWindowStyleMaskFullSizeContentView backing:NSBackingStoreBuffered defer:NO];
    self.window.title = @"Claude 多开启动器"; self.window.titlebarAppearsTransparent = YES; self.window.titleVisibility = NSWindowTitleHidden;
    self.window.movableByWindowBackground = YES; self.window.delegate = self; self.window.releasedWhenClosed = NO; [self.window center];
    NSColor *paper = Hex(0xFAF6F0), *ink = Hex(0x2B2622), *muted = Hex(0x6E655C);
    Canvas *root = [[Canvas alloc] initWithFrame:NSMakeRect(0,0,1080,720)];
    root.paint = ^(NSRect bounds) { [paper setFill]; NSRectFill(bounds); };
    self.window.contentView = root;
    NSImage *icon = [[NSImage alloc] initWithContentsOfFile:[NSBundle.mainBundle pathForResource:@"Claude" ofType:@"icns"]];
    NSApp.applicationIconImage = icon;

    __weak Launcher *weakSelf = self;
    NSImage *mountains = Art(@"sidebar");
    Canvas *sidebar = [[Canvas alloc] initWithFrame:NSMakeRect(0,0,272,720)];
    sidebar.paint = ^(NSRect bounds) {
        NSColor *base = Hex(0xF3ECE2);
        [base setFill]; NSRectFill(bounds);
        if (mountains) {
            CGFloat height = NSWidth(bounds)*mountains.size.height/mountains.size.width;
            NSRect art = NSMakeRect(0,NSHeight(bounds)-height,NSWidth(bounds),height);
            [mountains drawInRect:art fromRect:NSZeroRect operation:NSCompositingOperationSourceOver fraction:1 respectFlipped:YES hints:nil];
            [[[NSGradient alloc] initWithStartingColor:base endingColor:[base colorWithAlphaComponent:0]] drawInRect:NSMakeRect(0,NSMinY(art),NSWidth(bounds),180) angle:90];
        }
        [Hex(0xB9825F) setFill];
        [[NSBezierPath bezierPathWithRoundedRect:NSMakeRect(6,188+weakSelf.page*56,4,24) xRadius:2 yRadius:2] fill];
        [Hex(0xE6DCCF) setFill]; NSRectFill(NSMakeRect(NSWidth(bounds)-1,0,1,NSHeight(bounds)));
    };
    [root addSubview:sidebar]; self.sidebar = sidebar;
    NSImageView *logo = [[NSImageView alloc] initWithFrame:NSMakeRect(32,54,58,58)]; logo.image = icon; [sidebar addSubview:logo];
    [sidebar addSubview:Text(@"Claude", NSMakeRect(98,48,170,48), SerifFont(40), ink)];
    [sidebar addSubview:Text(@"多开启动器", NSMakeRect(100,96,160,24), [NSFont systemFontOfSize:17 weight:NSFontWeightMedium], ink)];
    NSArray *nav = @[@[@"实例管理",@"briefcase"],@[@"账号管理",@"person.2"],@[@"日志记录",@"doc.text"],@[@"系统设置",@"gearshape"]];
    NSMutableArray *items = [NSMutableArray array];
    for (NSUInteger i = 0; i < nav.count; i++) { NSView *item = [self navItem:nav[i][0] symbol:nav[i][1] index:i]; [sidebar addSubview:item]; [items addObject:item]; }
    self.navItems = items;

    Canvas *main = [[Canvas alloc] initWithFrame:NSMakeRect(272,0,808,720)];
    main.paint = ^(NSRect bounds) {
        [paper setFill]; NSRectFill(bounds);
        // Brush underline beneath the slogan.
        NSBezierPath *stroke = [NSBezierPath bezierPath];
        [stroke moveToPoint:NSMakePoint(196,104)]; [stroke curveToPoint:NSMakePoint(388,94) controlPoint1:NSMakePoint(260,100) controlPoint2:NSMakePoint(330,92)];
        stroke.lineWidth = 2.6; stroke.lineCapStyle = NSLineCapStyleRound;
        [[Hex(0xEE9A63) colorWithAlphaComponent:0.85] setStroke]; [stroke stroke];
        NSBezierPath *tail = [NSBezierPath bezierPath];
        [tail moveToPoint:NSMakePoint(232,108)]; [tail curveToPoint:NSMakePoint(360,101) controlPoint1:NSMakePoint(280,104) controlPoint2:NSMakePoint(320,101)];
        tail.lineWidth = 1.4; tail.lineCapStyle = NSLineCapStyleRound; [[Hex(0xEE9A63) colorWithAlphaComponent:0.5] setStroke]; [tail stroke];
    };
    [root addSubview:main];
    NSTextField *slogan = Text(@"让多个 Claude，服务你的不同灵感", NSMakeRect(40,56,420,40), BrushFont(26), Hex(0x3A332D));
    slogan.frameCenterRotation = 1.5; [main addSubview:slogan];
    self.summaryLabel = Text(@"正在读取状态", NSMakeRect(44,114,300,18), [NSFont systemFontOfSize:12], muted); [main addSubview:self.summaryLabel];
    self.createButton = [ActionButton buttonWithTitle:@"+  新建实例" target:self action:@selector(newInstance:)];
    self.createButton.frame = NSMakeRect(612,52,160,48); self.createButton.bordered = NO; [main addSubview:self.createButton];

    NSRect pageFrame = NSMakeRect(16,140,776,528);
    NSScrollView *list = [[NSScrollView alloc] initWithFrame:pageFrame];
    list.drawsBackground = NO; list.contentView.drawsBackground = NO; list.hasVerticalScroller = YES; list.autohidesScrollers = YES; list.scrollerStyle = NSScrollerStyleOverlay;
    Canvas *cardsView = [[Canvas alloc] initWithFrame:NSMakeRect(0,0,776,10*136-12)];
    NSArray *arts = @[Art(@"card-1") ?: [NSNull null],Art(@"card-2") ?: [NSNull null],Art(@"card-3") ?: [NSNull null],Art(@"card-4") ?: [NSNull null]];
    self.cards = [NSMutableArray array]; self.buttons = [NSMutableArray array]; self.statuses = [NSMutableArray array];
    for (NSInteger n = 1; n <= 10; n++) {
        id art = arts[(n-1)%4];
        AccountCard *card = [self cardFor:n frame:NSMakeRect(0,(n-1)*136,776,124) art:art == [NSNull null] ? nil : art];
        [cardsView addSubview:card]; [self.cards addObject:card]; [self.buttons addObject:card.button]; [self.statuses addObject:card.status];
    }
    list.documentView = cardsView; [main addSubview:list]; self.cardsView = cardsView;
    self.emptyHint = Text(@"本机还没有 Claude 账号目录。点击右上角「+ 新建实例」创建第一个。", NSMakeRect(40,180,700,22), [NSFont systemFontOfSize:15], muted);
    self.emptyHint.hidden = YES; [main addSubview:self.emptyHint];

    Canvas *accounts = [[Canvas alloc] initWithFrame:pageFrame];
    accounts.paint = ^(NSRect bounds) { [Hex(CardFill) setFill]; [[NSBezierPath bezierPathWithRoundedRect:bounds xRadius:14 yRadius:14] fill]; };
    [accounts addSubview:Text(@"账号管理", NSMakeRect(28,26,300,24), [NSFont systemFontOfSize:17 weight:NSFontWeightSemibold], ink)];
    NSTextField *soon = [NSTextField wrappingLabelWithString:@"后续在这里管理账号备注等本地信息。启动器不读取登录凭据或账号内容。"];
    soon.frame = NSMakeRect(28,60,720,40); soon.textColor = muted; [accounts addSubview:soon];

    NSScrollView *logScroll = [NSTextView scrollableTextView];
    logScroll.frame = pageFrame; logScroll.wantsLayer = YES; logScroll.layer.cornerRadius = 14; logScroll.borderType = NSNoBorder;
    self.logView = logScroll.documentView; self.logView.editable = NO; self.logView.backgroundColor = Hex(CardFill);
    self.logView.textContainerInset = NSMakeSize(18,18); self.logView.font = [NSFont systemFontOfSize:13]; self.logView.textColor = ink;
    [main addSubview:logScroll];

    Canvas *settings = [[Canvas alloc] initWithFrame:pageFrame];
    settings.paint = accounts.paint;
    NSString *version = [NSString stringWithFormat:@"%@（build %@）",[NSBundle.mainBundle objectForInfoDictionaryKey:@"CFBundleShortVersionString"] ?: @"开发版",[NSBundle.mainBundle objectForInfoDictionaryKey:@"CFBundleVersion"] ?: @"-"];
    NSArray *rows = @[@[@"辅助功能权限",@""],@[@"账号数据目录",@"~/Library/Application Support（01 为 Claude，02 为 Claude-Second，03～10 为 Claude-Profile-NN）"],@[@"隔离范围",@"02～10 的 Code 配置位于各自账号目录的 ClaudeCodeConfig。钥匙串、登录回调、日志目录仍共享；完整隔离请使用独立 macOS 用户。"],@[@"版本",version]];
    for (NSUInteger i = 0; i < rows.count; i++) {
        CGFloat y = 26+i*92;
        [settings addSubview:Text(rows[i][0], NSMakeRect(28,y,300,22), [NSFont systemFontOfSize:15 weight:NSFontWeightSemibold], ink)];
        NSTextField *detail = [NSTextField wrappingLabelWithString:rows[i][1]]; detail.frame = NSMakeRect(28,y+26,500,40); detail.textColor = muted;
        [settings addSubview:detail]; if (i == 0) self.accessibilityStatus = detail;
    }
    NSButton *axButton = [NSButton buttonWithTitle:@"打开辅助功能设置" target:self action:@selector(openAccessibility:)];
    axButton.frame = NSMakeRect(566,30,180,30); [settings addSubview:axButton];
    NSButton *revealButton = [NSButton buttonWithTitle:@"在 Finder 中显示" target:self action:@selector(revealSupport:)];
    revealButton.frame = NSMakeRect(566,122,180,30); [settings addSubview:revealButton];
    [main addSubview:settings];
    self.pages = @[list,accounts,logScroll,settings];

    self.message = Text(@"", NSMakeRect(24,678,580,32), [NSFont systemFontOfSize:12], muted);
    self.message.maximumNumberOfLines = 2; self.message.lineBreakMode = NSLineBreakByWordWrapping; [main addSubview:self.message];
    self.endLoginButton = [NSButton buttonWithTitle:@"结束登录保护" target:self action:@selector(endLogin:)];
    self.endLoginButton.frame = NSMakeRect(640,680,140,28); self.endLoginButton.bezelStyle = NSBezelStyleRounded;
    [main addSubview:self.endLoginButton];
    self.message.accessibilityIdentifier = @"launcher-feedback";
    [self showPage:0];
    NSMenu *menu = [NSMenu new]; NSMenuItem *appItem = [NSMenuItem new]; [menu addItem:appItem];
    NSMenu *appMenu = [NSMenu new]; appItem.submenu = appMenu;
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
        BOOL broken = failure || self.errors[key];
        self.statuses[n-1].stringValue = state;
        self.statuses[n-1].textColor = broken ? NSColor.systemRedColor : Hex(0x6E655C);
        self.statuses[n-1].toolTip = pids.count ? [NSString stringWithFormat:@"主进程 PID：%@",[pids componentsJoinedByString:@"、"]] : ProfilePath(n);
        self.cards[n-1].dot = broken ? NSColor.systemRedColor : since ? Hex(0xE0A23A) : pids.count ? Hex(0x3BAA6E) : Hex(0xB9B1A7);
        [self.cards[n-1] setNeedsDisplay:YES];
        self.buttons[n-1].title = since ? @"启动中" : pids.count ? @"打开" : exists ? @"启动" : @"创建";
        self.buttons[n-1].enabled = !failure && !since;
        self.buttons[n-1].accessibilityLabel = [NSString stringWithFormat:@"账号 %02ld，%@",(long)n,state];
    }
    // Show only accounts present on this Mac (data directory, running or starting), packed top to bottom.
    CGFloat y = 0; NSUInteger shown = 0, running = 0;
    for (NSInteger n = 1; n <= 10; n++) {
        NSArray *pids = self.processes[@(n)]; if (pids.count) running++;
        BOOL visible = pids.count || self.starting[@(n)] || [NSFileManager.defaultManager fileExistsAtPath:ProfilePath(n)];
        self.cards[n-1].hidden = !visible;
        if (visible) { [self.cards[n-1] setFrameOrigin:NSMakePoint(0,y)]; y += 136; shown++; }
    }
    [self.cardsView setFrameSize:NSMakeSize(776,MAX(y-12,0))];
    self.emptyHint.hidden = shown > 0 || self.page != 0;
    self.summaryLabel.stringValue = failure ? @"状态暂不可用" : [NSString stringWithFormat:@"本机 %lu 个账号 · %lu 个运行中",(unsigned long)shown,(unsigned long)running];
    if (failure) self.message.stringValue = failure;
    else if (self.loginAccount) self.message.stringValue = [NSString stringWithFormat:@"账号 %02ld 登录保护中。%@",(long)self.loginAccount,LoginConflict(snapshot,self.loginAccount) ? @"检测到其他 Claude！请停止登录，并正常退出其他实例。" : @"完成登录并核对身份后，点「结束登录保护」恢复多开。"];
    self.accessibilityStatus.stringValue = AXIsProcessTrusted() ? @"已授权：可恢复最小化的 Claude 窗口。" : @"未授权：无法恢复最小化窗口。授权不会修改账号数据。";
    NSString *text = self.message.stringValue;
    if (text.length && ![text isEqual:self.lastLogged]) {
        self.lastLogged = text;
        NSDateFormatter *clock = [NSDateFormatter new]; clock.dateFormat = @"HH:mm:ss";
        NSString *line = [NSString stringWithFormat:@"%@  %@\n",[clock stringFromDate:[NSDate date]],text];
        [self.logView.textStorage appendAttributedString:[[NSAttributedString alloc] initWithString:line attributes:@{NSFontAttributeName:[NSFont systemFontOfSize:13],NSForegroundColorAttributeName:Hex(0x2B2622)}]];
    }
}
- (void)showPage:(NSInteger)page {
    self.page = page;
    for (NSUInteger i = 0; i < self.pages.count; i++) self.pages[i].hidden = (NSInteger)i != page;
    self.createButton.hidden = page != 0;
    if (page != 0) self.emptyHint.hidden = YES;
    self.sidebar.needsDisplay = YES;
    for (NSView *item in self.navItems) item.needsDisplay = YES;
}
- (void)navClicked:(NSClickGestureRecognizer *)recognizer { [self showPage:recognizer.view.identifier.integerValue]; }
- (void)newInstance:(id)sender {
    for (NSInteger n = 1; n <= 10; n++) {
        if (![NSFileManager.defaultManager fileExistsAtPath:ProfilePath(n)]) { [self selectAccount:self.buttons[n-1]]; return; }
    }
    [self alert:@"10 个账号空间都已创建" detail:@"请直接在列表中启动或打开对应账号。"];
}
- (void)showMore:(NSButton *)sender {
    NSMenu *menu = [NSMenu new]; menu.autoenablesItems = NO;
    NSMenuItem *reveal = [menu addItemWithTitle:@"在 Finder 中显示账号目录" action:@selector(revealProfile:) keyEquivalent:@""];
    reveal.target = self; reveal.tag = sender.tag; reveal.enabled = [NSFileManager.defaultManager fileExistsAtPath:ProfilePath(sender.tag)];
    NSMenuItem *copy = [menu addItemWithTitle:@"复制账号目录路径" action:@selector(copyProfilePath:) keyEquivalent:@""];
    copy.target = self; copy.tag = sender.tag;
    [menu popUpMenuPositioningItem:nil atLocation:NSMakePoint(0,-4) inView:sender];
}
- (void)revealProfile:(NSMenuItem *)item { [NSWorkspace.sharedWorkspace activateFileViewerSelectingURLs:@[[NSURL fileURLWithPath:ProfilePath(item.tag)]]]; }
- (void)copyProfilePath:(NSMenuItem *)item {
    [NSPasteboard.generalPasteboard clearContents]; [NSPasteboard.generalPasteboard setString:ProfilePath(item.tag) forType:NSPasteboardTypeString];
}
- (void)revealSupport:(id)sender {
    [NSWorkspace.sharedWorkspace openURL:[NSURL fileURLWithPath:[NSHomeDirectory() stringByAppendingPathComponent:@"Library/Application Support"]]];
}
- (void)openAccessibility:(id)sender {
    NSDictionary *options = @{(__bridge NSString *)kAXTrustedCheckOptionPrompt:@YES}; AXIsProcessTrustedWithOptions((__bridge CFDictionaryRef)options);
    [NSWorkspace.sharedWorkspace openURL:[NSURL URLWithString:@"x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility"]];
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
            if ([alert runModal] == NSAlertFirstButtonReturn) [self openAccessibility:nil];
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
        if (argc > 2 && strcmp(argv[1],"--snapshot") == 0) {
            // Development aid: render the window offscreen to PNG without taking the instance lock or launching Claude.
            [NSApplication sharedApplication];
            Launcher *launcher = [Launcher new]; launcher.starting = [NSMutableDictionary dictionary]; launcher.errors = [NSMutableDictionary dictionary];
            [launcher makeWindow]; if (argc > 3) [launcher showPage:atoi(argv[3])]; [launcher refresh];
            NSView *view = launcher.window.contentView; [view layoutSubtreeIfNeeded];
            NSBitmapImageRep *bitmap = [view bitmapImageRepForCachingDisplayInRect:view.bounds];
            [view cacheDisplayInRect:view.bounds toBitmapImageRep:bitmap];
            return [[bitmap representationUsingType:NSBitmapImageFileTypePNG properties:@{}] writeToFile:@(argv[2]) atomically:YES] ? 0 : 1;
        }
        NSApplication *app = NSApplication.sharedApplication; app.activationPolicy=NSApplicationActivationPolicyRegular;
        Launcher *delegate=[Launcher new]; app.delegate=delegate; [app run];
    } return 0;
}
