#import "UsageInterface.h"
#import <ApplicationServices/ApplicationServices.h>
#import <math.h>
#import <sqlite3.h>
#import <CommonCrypto/CommonDigest.h>
#import <sys/stat.h>
#import <fcntl.h>
#import <unistd.h>

static BOOL MarkerFile(struct stat st) { return S_ISREG(st.st_mode) && st.st_uid == getuid() && st.st_nlink == 1 && !(st.st_mode & 0022); }
static BOOL MarkerSame(struct stat a, struct stat b) {
    return a.st_dev == b.st_dev && a.st_ino == b.st_ino && a.st_size == b.st_size &&
        a.st_mtimespec.tv_sec == b.st_mtimespec.tv_sec && a.st_mtimespec.tv_nsec == b.st_mtimespec.tv_nsec &&
        a.st_ctimespec.tv_sec == b.st_ctimespec.tv_sec && a.st_ctimespec.tv_nsec == b.st_ctimespec.tv_nsec;
}
static BOOL MarkerQuiet(int directory) {
    // shortcut: immutable SQLite ignores WAL; reject WAL and active journals until a read-only WAL reader is supported.
    for (NSString *name in @[@"Cookies-journal",@"Cookies-wal",@"Cookies-shm"]) {
        struct stat st;
        if (fstatat(directory,name.fileSystemRepresentation,&st,AT_SYMLINK_NOFOLLOW)) { if (errno != ENOENT) return NO; }
        else if (!MarkerFile(st) || st.st_size) return NO;
    }
    return YES;
}
static int MarkerAuthorizer(void *context, int action, const char *table, const char *column, const char *database, const char *source) {
    if (source) return SQLITE_DENY;
    if (action == SQLITE_SELECT) return SQLITE_OK;
    if (action != SQLITE_READ || !table || !column || !database || strcmp(database,"main")) return SQLITE_DENY;
    if (!strcmp(table,"meta") && [@[@"key",@"value"] containsObject:@(column)]) return SQLITE_OK;
    if (!strcmp(table,"cookies") && [@[@"host_key",@"name",@"path",@"value",@"encrypted_value",@"has_expires",@"expires_utc",@"is_persistent",@"source_scheme",@"source_port",@"top_frame_site_key"] containsObject:@(column)]) return SQLITE_OK;
    return SQLITE_DENY;
}
static int MarkerBudget(void *context) { return --*(int *)context <= 0; }
NSData *UsageOrganizationMarker(NSString *profile) {
    if (!profile.isAbsolutePath || ![profile isEqual:profile.stringByStandardizingPath]) return nil;
    int directory = open("/",O_RDONLY|O_DIRECTORY|O_CLOEXEC);
    for (NSString *part in profile.pathComponents) {
        if ([part isEqual:@"/"]) continue;
        int next = openat(directory,part.fileSystemRepresentation,O_RDONLY|O_DIRECTORY|O_NOFOLLOW|O_CLOEXEC|O_NONBLOCK);
        if (directory >= 0) close(directory); directory = next;
        struct stat st;
        if (directory < 0) return nil;
        if (fstat(directory,&st) || (st.st_mode & 0022) || (st.st_uid != 0 && st.st_uid != getuid())) { close(directory); return nil; }
    }
    int file = openat(directory,"Cookies",O_RDONLY|O_NOFOLLOW|O_CLOEXEC|O_NONBLOCK);
    struct stat before,after,pathStat; uint8_t header[20];
    if (file < 0 || fstat(file,&before) || !MarkerFile(before) || before.st_size < 100 || before.st_size > 16*1024*1024 ||
        pread(file,header,sizeof(header),0) != sizeof(header) || memcmp(header,"SQLite format 3\0",16) ||
        header[18] != 1 || header[19] != 1 || !MarkerQuiet(directory)) {
        if (file >= 0) close(file); close(directory); return nil;
    }
    NSString *uri = [NSString stringWithFormat:@"file:/dev/fd/%d?mode=ro&immutable=1",file];
    sqlite3 *db = NULL; sqlite3_stmt *statement = NULL; NSData *marker = nil; int budget = 1000;
    if (sqlite3_open_v2(uri.UTF8String,&db,SQLITE_OPEN_READONLY|SQLITE_OPEN_URI|SQLITE_OPEN_NOMUTEX,NULL) == SQLITE_OK) {
        sqlite3_db_config(db,SQLITE_DBCONFIG_DEFENSIVE,1,NULL); sqlite3_db_config(db,SQLITE_DBCONFIG_TRUSTED_SCHEMA,0,NULL);
        sqlite3_limit(db,SQLITE_LIMIT_LENGTH,4096); sqlite3_set_authorizer(db,MarkerAuthorizer,NULL);
        sqlite3_progress_handler(db,1000,MarkerBudget,&budget);
        BOOL version = sqlite3_prepare_v2(db,"SELECT value FROM meta WHERE key='version' LIMIT 2",-1,&statement,NULL) == SQLITE_OK &&
            sqlite3_step(statement) == SQLITE_ROW && sqlite3_column_bytes(statement,0) == 2 &&
            !memcmp(sqlite3_column_text(statement,0),"24",2) && sqlite3_step(statement) == SQLITE_DONE;
        sqlite3_finalize(statement); statement = NULL;
        // This fixed query returns only the authorized non-login selector. No other cookie values are selected.
        const char *sql = "SELECT host_key,value,encrypted_value FROM cookies WHERE name='lastActiveOrg' "
            "AND host_key IN ('claude.ai','.claude.ai') AND path='/' AND top_frame_site_key='' "
            "AND source_scheme=2 AND source_port IN (-1,443) AND is_persistent=1 AND has_expires=1 AND expires_utc>? LIMIT 2";
        if (version && sqlite3_prepare_v2(db,sql,-1,&statement,NULL) == SQLITE_OK) {
            sqlite3_bind_int64(statement,1,(sqlite3_int64)((NSDate.date.timeIntervalSince1970+11644473600.0)*1000000));
            if (sqlite3_step(statement) == SQLITE_ROW && sqlite3_column_bytes(statement,1) == 0 && sqlite3_column_bytes(statement,2) == 83 &&
                !memcmp(sqlite3_column_blob(statement,2),"v10",3)) {
                CC_SHA256_CTX context; uint8_t digest[CC_SHA256_DIGEST_LENGTH]; CC_SHA256_Init(&context);
                CC_SHA256_Update(&context,sqlite3_column_text(statement,0),(CC_LONG)sqlite3_column_bytes(statement,0));
                CC_SHA256_Update(&context,sqlite3_column_blob(statement,2),83); CC_SHA256_Final(digest,&context);
                if (sqlite3_step(statement) == SQLITE_DONE) marker = [NSData dataWithBytes:digest length:sizeof(digest)];
            }
        }
    }
    sqlite3_finalize(statement); if (db) sqlite3_close(db);
    BOOL stable = MarkerQuiet(directory) && !fstat(file,&after) && MarkerSame(before,after) &&
        !fstatat(directory,"Cookies",&pathStat,AT_SYMLINK_NOFOLLOW) && MarkerSame(before,pathStat);
    close(file); close(directory); return stable ? marker : nil;
}

static NSString *String(id value) { return [value isKindOfClass:NSString.class] && [value length] <= 512 ? value : @""; }
static NSNumber *Percent(id value) {
    if (![value isKindOfClass:NSNumber.class] || CFGetTypeID((__bridge CFTypeRef)value) == CFBooleanGetTypeID()) return nil;
    double n = [value doubleValue]; return isfinite(n) && n >= 0 && n <= 100 ? value : nil;
}
static NSString *MetricKey(NSString *label) {
    if ([@[@"5 小时限额",@"5-hour limit"] containsObject:label]) return @"fh";
    if ([@[@"每周 · 所有模型",@"Weekly · all models"] containsObject:label]) return @"sd";
    return nil; // Model-specific weekly and context utilization are not the account's weekly limit.
}
// Before the first message of a new five-hour window, the official button shows the weekly limit instead.
static NSDictionary *ButtonMetric(NSString *label) {
    NSDictionary *patterns = @{@"^用量[：:]\\s*5 小时限额的 ([0-9]{1,3}(?:\\.[0-9]+)?)%(.*)$":@"fh",
                               @"^Usage: ([0-9]{1,3}(?:\\.[0-9]+)?)% of (?:the )?5-hour limit(.*)$":@"fh",
                               @"^用量[：:]\\s*每周 · 所有模型[：:]\\s*([0-9]{1,3}(?:\\.[0-9]+)?)%(.*)$":@"sd"};
    for (NSString *pattern in patterns) {
        NSTextCheckingResult *match = [[NSRegularExpression regularExpressionWithPattern:pattern options:0 error:nil] firstMatchInString:label options:0 range:NSMakeRange(0,label.length)];
        if (!match) continue;
        NSNumber *percent = Percent(@([[label substringWithRange:[match rangeAtIndex:1]] doubleValue]));
        if (!percent) return nil;
        NSString *reset = [[label substringWithRange:[match rangeAtIndex:2]] stringByTrimmingCharactersInSet:NSCharacterSet.whitespaceAndNewlineCharacterSet];
        return @{@"key":patterns[pattern],@"percent":percent,@"reset":reset};
    }
    return nil;
}
static BOOL CodeURL(NSString *value) {
    if (![value isKindOfClass:NSString.class]) return NO;
    NSURL *url = [NSURL URLWithString:value];
    return [url.scheme isEqual:@"https"] && [url.host isEqual:@"claude.ai"] && !url.user && !url.password && !url.port && !url.query && !url.fragment &&
        ([url.path isEqual:@"/epitaxy"] || ([url.path hasPrefix:@"/epitaxy/"] && url.path.length > 9)); // Code home or a conversation.
}
static BOOL UsageURL(NSString *value) {
    NSURL *url = [NSURL URLWithString:value];
    return [url.scheme isEqual:@"https"] && [url.host isEqual:@"claude.ai"] && [url.path isEqual:@"/settings/usage"] && !url.user && !url.password && !url.port;
}

// The same bounded traversal is tested with synthetic AX nodes; no test touches a real account.
@interface UsageAXReader : NSObject
@property (copy) id (^attribute)(id,NSString *);
@property NSUInteger count;
@property NSTimeInterval deadline;
@property BOOL limited, changed, unvisitedBranches;
@property id oversizedBranch;
@property NSMutableArray *matches;
@property NSHashTable *visited;
@property NSUInteger codeAreas;
@end
@implementation UsageAXReader
- (id)read:(id)node key:(NSString *)key {
    if (self.limited || ++self.count > 16000 || NSProcessInfo.processInfo.systemUptime > self.deadline) { self.limited = YES; return nil; }
    return self.attribute(node,key);
}
- (NSString *)name:(id)node {
    NSString *title = String([self read:node key:@"AXTitle"]);
    return title.length ? title : String([self read:node key:@"AXDescription"]);
}
- (NSArray *)children:(id)node {
    id children = [self read:node key:@"AXChildren"];
    if (!children) return @[];
    if (![children isKindOfClass:NSArray.class] || [children count] > 2000) { self.limited = YES; self.oversizedBranch = node; return @[]; }
    return children;
}
- (NSString *)url:(id)node {
    id value = [self read:node key:@"AXURL"];
    return [value isKindOfClass:NSURL.class] ? [value absoluteString] : String(value);
}
- (BOOL)hasUsageLink:(id)node depth:(NSUInteger)depth {
    if (depth > 12 || self.limited) return NO;
    NSString *role = String([self read:node key:@"AXRole"]);
    if ([role isEqual:@"AXLink"]) return UsageURL([self url:node]);
    if ([@[@"AXWebArea",@"AXTextArea",@"AXTextField",@"AXStaticText",@"AXImage",@"AXPopUpButton",@"AXButton"] containsObject:role]) return NO;
    for (id child in [self children:node]) if ([self hasUsageLink:child depth:depth+1]) return YES;
    return NO;
}
- (BOOL)panel:(id)node metrics:(NSMutableDictionary *)metrics reset:(NSString **)reset linked:(BOOL *)linked depth:(NSUInteger)depth {
    if (depth > 12) { self.limited = YES; return NO; }
    NSString *role = String([self read:node key:@"AXRole"]);
    if ([role isEqual:@"AXWebArea"] || [role isEqual:@"AXTextArea"]) return NO;
    if ([role isEqual:@"AXLink"]) { if (UsageURL([self url:node])) *linked = YES; return YES; }
    if ([role isEqual:@"AXStaticText"]) {
        NSString *text = String([self read:node key:@"AXValue"]);
        if (MetricKey(text)) *reset = nil;
        else if ([text containsString:@"重置"] || [text hasPrefix:@"Resets "] || [text containsString:@"第一条消息"]) *reset = text;
        return YES;
    }
    if ([role isEqual:@"AXLevelIndicator"] || [role isEqual:@"AXProgressIndicator"]) {
        NSString *key = MetricKey([self name:node]); if (!key) return YES;
        NSNumber *percent = Percent([self read:node key:@"AXValue"]);
        if (!percent || metrics[key]) return NO;
        metrics[key] = @{@"percent":percent,@"reset":*reset ?: @""}; *reset = nil;
        return YES;
    }
    for (id child in [self children:node]) if (![self panel:child metrics:metrics reset:reset linked:linked depth:depth+1]) return NO;
    return !self.limited;
}
- (void)walk:(id)node parent:(id)parent page:(NSString *)page depth:(NSUInteger)depth {
    if (self.limited) { if (node != self.oversizedBranch) self.unvisitedBranches = YES; return; }
    // shortcut: only quota layouts within 40 levels are supported; validate deeper layouts before extending this scope.
    if (depth > 40 || [self.visited containsObject:node]) return;
    [self.visited addObject:node];
    NSString *role = String([self read:node key:@"AXRole"]);
    BOOL codeArea = [role isEqual:@"AXWebArea"];
    if (codeArea) {
        if (page) return; // Never enter Code's embedded browser or other nested web content.
        NSString *url = [self url:node];
        if (CodeURL(url)) { page = url; self.codeAreas++; }
        else return; // The native wrapper is a sibling of Code, not its parent in Claude 4.29.0.
    }
    if (page && [role isEqual:@"AXPopUpButton"]) {
        NSDictionary *shown = ButtonMetric([self name:node]);
        if (!shown) return;
        NSString *key = shown[@"key"];
        NSMutableDictionary *result = [@{@"status":@"界面已读取",@"page":page,key:@{@"percent":shown[@"percent"],@"reset":shown[@"reset"]}} mutableCopy];
        NSUInteger panels = 0;
        for (id sibling in [self children:parent]) {
            // Chromium's generic container role/name varies. Verify the dedicated settings link before reading text.
            if (sibling == node || ![self hasUsageLink:sibling depth:0]) continue;
            NSMutableDictionary *metrics = [NSMutableDictionary dictionary]; NSString *reset = nil; BOOL linked = NO;
            if (![self panel:sibling metrics:metrics reset:&reset linked:&linked depth:0] || !linked || !metrics[key] ||
                ![metrics[key][@"percent"] isEqual:shown[@"percent"]]) { result = nil; break; }
            [result addEntriesFromDictionary:metrics]; panels++;
        }
        if (!result || panels > 1) [self.matches addObject:@{@"status":@"界面数据待核对"}];
        else [self.matches addObject:result];
        return;
    }
    if ([@[@"AXStaticText",@"AXTextArea",@"AXTextField",@"AXImage",@"AXLink",@"AXHeading",@"AXTable",@"AXOutline",@"AXList"] containsObject:role]) return;
    // No text/values are read from conversations. Only structural attributes and recognized quota controls.
    // Newest-first: the composer and its quota button sit after long transcripts, so reach them before the node budget runs out.
    for (id child in [[self children:node] reverseObjectEnumerator]) [self walk:child parent:node page:page depth:depth+1];
    if (codeArea) {
        // One direct read even after the budget ran out, so an unfinished walk still detects navigation.
        id value = self.attribute(node,@"AXURL"); NSString *now = [value isKindOfClass:NSURL.class] ? [value absoluteString] : String(value);
        if (![now isEqual:page]) self.limited = self.changed = YES;
    }
}
- (NSDictionary *)result:(NSArray *)windows {
    self.matches = [NSMutableArray array]; self.visited = [NSHashTable hashTableWithOptions:NSPointerFunctionsStrongMemory];
    for (id window in windows) [self walk:window parent:nil page:nil depth:0];
    // One fully verified control in this PID belongs to this account; an unfinished walk elsewhere cannot change its value.
    // This exception only covers the final oversized branch; unvisited siblings may contain another quota control.
    if (self.limited && (windows.count != 1 || self.codeAreas != 1 || self.changed || self.unvisitedBranches ||
        !(self.matches.count == 1 && [self.matches.firstObject[@"status"] isEqual:@"界面已读取"]))) return @{@"status":@"界面读取未完成"};
    if (self.matches.count > 1) return @{@"status":@"多个用量界面，待核对"};
    return self.matches.firstObject ?: @{@"status":self.codeAreas ? @"未观察到 Code 用量控件" : @"未观察到 Code 用量页面"};
}
@end

static NSDictionary *ReadUsageInterfaceAttempt(pid_t pid, BOOL allowInitialization, NSTimeInterval deadline) {
    if (pid <= 0) return @{@"status":@"进程待核对"};
    if (!AXIsProcessTrusted()) return @{@"status":@"需辅助功能权限"};
    AXUIElementRef app = AXUIElementCreateApplication(pid);
    AXUIElementSetMessagingTimeout(app,0.1f); // Local IPC timeout only; does not set any attribute in Claude.
    CFTypeRef manual = NULL;
    AXError manualStatus = AXUIElementCopyAttributeValue(app,CFSTR("AXManualAccessibility"),&manual);
    BOOL initialized = NO;
    if (allowInitialization && manualStatus == kAXErrorSuccess && manual == kCFBooleanFalse) {
        // Electron's documented local AX-tree switch, not a permission grant, click, navigation or usage refresh.
        initialized = AXUIElementSetAttributeValue(app,CFSTR("AXManualAccessibility"),kCFBooleanTrue) == kAXErrorSuccess;
    }
    if (manual) CFRelease(manual);
    UsageAXReader *reader = [UsageAXReader new]; reader.deadline = MIN(deadline,NSProcessInfo.processInfo.systemUptime+1.5);
    __weak UsageAXReader *weakReader = reader;
    NSMapTable *structure = [NSMapTable strongToStrongObjectsMapTable];
    reader.attribute = ^id(id node, NSString *key) {
        if (!node || CFGetTypeID((__bridge CFTypeRef)node) != AXUIElementGetTypeID()) return nil;
        AXUIElementSetMessagingTimeout((__bridge AXUIElementRef)node,0.1f); // Per element; keeps one call within the read deadline.
        if ([key isEqual:@"AXRole"] || [key isEqual:@"AXChildren"]) {
            NSDictionary *cached = [structure objectForKey:node];
            if (!cached) {
                CFArrayRef values = NULL;
                AXError error = AXUIElementCopyMultipleAttributeValues((__bridge AXUIElementRef)node,(__bridge CFArrayRef)@[@"AXRole",@"AXChildren"],0,&values);
                if (error == kAXErrorCannotComplete || error == kAXErrorInvalidUIElement || error == kAXErrorAPIDisabled) { weakReader.limited = YES; }
                NSArray *items = CFBridgingRelease(values);
                // Copy structural attributes together. Never batch titles or values from conversation nodes.
                cached = @{@"AXRole":items.count == 2 ? String(items[0]) : @"",
                           @"AXChildren":items.count == 2 && [items[1] isKindOfClass:NSArray.class] ? items[1] : @[]};
                [structure setObject:cached forKey:node];
            }
            return cached[key];
        }
        CFTypeRef value = NULL;
        AXError error = AXUIElementCopyAttributeValue((__bridge AXUIElementRef)node,(__bridge CFStringRef)key,&value);
        if (error == kAXErrorCannotComplete || error == kAXErrorInvalidUIElement || error == kAXErrorAPIDisabled) { weakReader.limited = YES; }
        return CFBridgingRelease(value);
    };
    id windows = [reader read:(__bridge id)app key:@"AXWindows"];
    NSDictionary *result = [reader result:[windows isKindOfClass:NSArray.class] ? windows : @[]];
    reader.attribute = nil; CFRelease(app);
    while (initialized && !result[@"fh"] && [result[@"status"] hasPrefix:@"未观察到 Code 用量"] && NSProcessInfo.processInfo.systemUptime+0.15 < deadline) {
        // Initial tree construction is asynchronous; preparation stays inside this one bounded viewing action.
        usleep(150000); result = ReadUsageInterfaceAttempt(pid,NO,deadline);
    }
    return result;
}
NSDictionary *ReadUsageInterface(pid_t pid) { return ReadUsageInterfaceAttempt(pid,YES,NSProcessInfo.processInfo.systemUptime+2.5); }

static BOOL UsagePopoverOpens(NSString *role, NSString *label, id expanded) {
    return [role isEqual:@"AXPopUpButton"] && ButtonMetric(label) && expanded == (__bridge id)kCFBooleanTrue;
}
@interface UsagePopoverObserver : NSObject
@property AXObserverRef observer;
@property (copy) void (^opened)(void);
@property id openButton;
- (BOOL)acceptButton:(id)button role:(NSString *)role title:(NSString *)title expanded:(id)expanded;
@end
@implementation UsagePopoverObserver
- (BOOL)acceptButton:(id)button role:(NSString *)role title:(NSString *)title expanded:(id)expanded {
    if ([role isEqual:@"AXPopUpButton"] && ButtonMetric(title) && expanded == (__bridge id)kCFBooleanFalse) {
        if ([self.openButton isEqual:button]) self.openButton = nil; return NO;
    }
    if (!UsagePopoverOpens(role,title,expanded) || [self.openButton isEqual:button]) return NO;
    self.openButton = button; return YES; // Repeated notifications for the same opening do not recapture.
}
- (void)dealloc {
    if (_observer) { CFRunLoopRemoveSource(CFRunLoopGetMain(),AXObserverGetRunLoopSource(_observer),kCFRunLoopCommonModes); CFRelease(_observer); }
}
@end
static id PopoverAttribute(AXUIElementRef element, CFStringRef key) {
    // Runs on the main thread: a hung Claude must not stall the launcher for the ~6s default timeout.
    AXUIElementSetMessagingTimeout(element,0.1f);
    CFTypeRef value = NULL; AXUIElementCopyAttributeValue(element,key,&value); return CFBridgingRelease(value);
}
static void UsagePopoverChanged(AXObserverRef observer, AXUIElementRef element, CFStringRef notification, void *context) {
    if (!CFEqual(notification,CFSTR("AXExpandedChanged"))) return;
    NSString *role = String(PopoverAttribute(element,kAXRoleAttribute));
    if (![role isEqual:@"AXPopUpButton"]) return;
    NSString *title = String(PopoverAttribute(element,kAXTitleAttribute));
    if (!title.length) title = String(PopoverAttribute(element,kAXDescriptionAttribute));
    UsagePopoverObserver *target = (__bridge UsagePopoverObserver *)context;
    if (![target acceptButton:(__bridge id)element role:role title:title expanded:PopoverAttribute(element,kAXExpandedAttribute)]) return;
    // Only the nearest, non-embedded official Code web area can authorize a quota snapshot.
    AXUIElementRef ancestor = element; CFRetain(ancestor); BOOL code = NO;
    for (NSUInteger depth=0;depth<40;depth++) {
        id parent = PopoverAttribute(ancestor,kAXParentAttribute); CFRelease(ancestor); ancestor = NULL;
        if (!parent || CFGetTypeID((__bridge CFTypeRef)parent) != AXUIElementGetTypeID()) break;
        ancestor = (AXUIElementRef)CFRetain((__bridge CFTypeRef)parent);
        if ([String(PopoverAttribute(ancestor,kAXRoleAttribute)) isEqual:@"AXWebArea"]) {
            id url = PopoverAttribute(ancestor,kAXURLAttribute);
            code = CodeURL([url isKindOfClass:NSURL.class] ? [url absoluteString] : String(url)); break;
        }
    }
    if (ancestor) CFRelease(ancestor);
    if (code && target.opened) target.opened();
}
id ObserveUsagePopover(pid_t pid, void (^opened)(void)) {
    if (pid <= 0 || !AXIsProcessTrusted()) return nil;
    AXObserverRef observer = NULL;
    if (AXObserverCreate(pid,UsagePopoverChanged,&observer) != kAXErrorSuccess) return nil;
    UsagePopoverObserver *target = [UsagePopoverObserver new]; target.observer = observer; target.opened = opened;
    AXUIElementRef app = AXUIElementCreateApplication(pid); AXUIElementSetMessagingTimeout(app,0.1f);
    AXError result = AXObserverAddNotification(observer,app,CFSTR("AXExpandedChanged"),(__bridge void *)target); CFRelease(app);
    if (result != kAXErrorSuccess) return nil;
    CFRunLoopAddSource(CFRunLoopGetMain(),AXObserverGetRunLoopSource(observer),kCFRunLoopCommonModes); return target;
}

NSDictionary *MergeUsageInterface(NSDictionary *previous, NSDictionary *snapshot, NSArray *identity, NSDate *now) {
    if (![snapshot[@"status"] isEqual:@"界面已读取"] || !identity || !CodeURL(snapshot[@"page"])) {
        BOOL unavailable = [snapshot[@"status"] hasPrefix:@"未观察到 Code 用量"] || [snapshot[@"status"] hasPrefix:@"界面读取未完成"];
        if (unavailable && [identity isEqual:previous[@"identity"]]) {
            NSMutableDictionary *last = [previous mutableCopy];
            for (NSString *key in @[@"fh",@"sd"]) if (last[key]) { NSMutableDictionary *metric = [last[key] mutableCopy]; metric[@"visible"] = @NO; last[key] = metric; }
            return last;
        }
        return snapshot ?: @{@"status":@"未观察到 Code 用量"};
    }
    NSMutableDictionary *result = [snapshot mutableCopy]; result[@"identity"] = identity;
    BOOL same = [identity isEqual:previous[@"identity"]]; // Quotas belong to the account, not an individual Code conversation.
    for (NSString *key in @[@"fh",@"sd"]) {
        NSDictionary *metric = snapshot[key];
        if (metric && Percent(metric[@"percent"])) {
            NSMutableDictionary *observed = [metric mutableCopy]; observed[@"observedAt"] = now; observed[@"visible"] = @YES; result[key] = observed;
        } else if (same && previous[key] && [previous[key][@"observedAt"] isKindOfClass:NSDate.class] && [now timeIntervalSinceDate:previous[key][@"observedAt"]] >= 0) {
            NSMutableDictionary *cached = [previous[key] mutableCopy]; cached[@"visible"] = @NO; result[key] = cached;
        } else [result removeObjectForKey:key];
    }
    return result;
}
NSString *UsageInterfaceTimeLabel(NSDictionary *metric, NSDate *now) {
    NSDate *date = metric[@"observedAt"];
    if (![date isKindOfClass:NSDate.class] || [now timeIntervalSinceDate:date] < 0) return @"尚未观察到";
    NSDateFormatter *format = [NSDateFormatter new]; format.dateFormat = @"HH:mm:ss";
    if (![NSCalendar.currentCalendar isDate:date inSameDayAsDate:now]) format.dateFormat = @"MM-dd HH:mm";
    return [format stringFromDate:date]; // Time only; tooltips explain it is a local observation time.
}

NSArray *StoredUsageInterface(NSDictionary *records) {
    NSMutableArray *items = [NSMutableArray array];
    for (NSNumber *account in [[records allKeys] sortedArrayUsingSelector:@selector(compare:)]) {
        NSDictionary *record = records[account];
        if (![record[@"status"] isEqual:@"界面已读取"] || !record[@"identity"] || !(record[@"fh"] || record[@"sd"])) continue;
        NSMutableDictionary *item = [@{@"identity":record[@"identity"]} mutableCopy];
        for (NSString *key in @[@"fh",@"sd"]) if (record[key]) item[key] = [record[key] dictionaryWithValuesForKeys:@[@"percent",@"reset",@"observedAt"]];
        [items addObject:item]; // No page/session identifier, conversation content or credentials are persisted.
    }
    return items;
}
NSDictionary *LoadedUsageInterface(id stored, NSDate *now) {
    if (![stored isKindOfClass:NSArray.class] || [stored count] > 10) return @{};
    NSMutableDictionary *records = [NSMutableDictionary dictionary];
    NSMutableSet *duplicates = [NSMutableSet set];
    for (id item in stored) {
        if (![item isKindOfClass:NSDictionary.class]) return @{};
        id identity = item[@"identity"];
        if (![identity isKindOfClass:NSArray.class] || [identity count] != 6) continue;
        BOOL valid = YES;
        for (NSUInteger i=0;i<6;i++) {
            id component = identity[i];
            if (i < 4) { if (![component isKindOfClass:NSNumber.class] || [component longLongValue] < 0) valid = NO; }
            else if (i == 5) { if (![component isKindOfClass:NSData.class] || [component length] != CC_SHA256_DIGEST_LENGTH) valid = NO; }
            else if ([component isKindOfClass:NSArray.class] && [component count] == 2) {
                for (id value in component) if (![value isKindOfClass:NSNumber.class] || !isfinite([value doubleValue]) || [value doubleValue] < 0) valid = NO;
            } else valid = NO;
        }
        NSInteger account = [identity[0] integerValue];
        if (!valid || account < 1 || account > 10 || [identity[1] intValue] <= 0 || [identity[2] longLongValue] <= 0) continue;
        if (records[@(account)]) { [duplicates addObject:@(account)]; [records removeObjectForKey:@(account)]; }
        if ([duplicates containsObject:@(account)]) continue;
        NSMutableDictionary *record = [@{@"status":@"界面已读取",@"identity":identity} mutableCopy];
        for (NSString *key in @[@"fh",@"sd"]) {
            id metric = item[key]; if (!metric) continue;
            if (![metric isKindOfClass:NSDictionary.class] || !Percent(metric[@"percent"]) ||
                ![metric[@"reset"] isKindOfClass:NSString.class] || [metric[@"reset"] length] > 512 ||
                ![metric[@"observedAt"] isKindOfClass:NSDate.class] || !isfinite([now timeIntervalSinceDate:metric[@"observedAt"]]) || [now timeIntervalSinceDate:metric[@"observedAt"]] < 0) continue;
            record[key] = @{@"percent":metric[@"percent"],@"reset":metric[@"reset"],@"observedAt":metric[@"observedAt"],@"visible":@NO};
        }
        if (record[@"fh"] || record[@"sd"]) records[@(account)] = record;
    }
    return records;
}

void TestUsageInterface(void) {
    NSCAssert(UsagePopoverOpens(@"AXPopUpButton",@"用量：5 小时限额的 52%",@YES),@"manual quota expansion can trigger one observation");
    NSCAssert(!UsagePopoverOpens(@"AXPopUpButton",@"用量：5 小时限额的 52%",@NO) &&
        !UsagePopoverOpens(@"AXPopUpButton",@"用量：5 小时限额的 52%",@1) &&
        !UsagePopoverOpens(@"AXStaticText",@"用量：5 小时限额的 52%",@YES) &&
        !UsagePopoverOpens(@"AXPopUpButton",@"其他菜单",@YES),@"closing, malformed state, text and unrelated menus cannot trigger quota reads");
    UsagePopoverObserver *events = [UsagePopoverObserver new]; NSObject *eventButton = [NSObject new];
    NSCAssert([events acceptButton:eventButton role:@"AXPopUpButton" title:@"用量：5 小时限额的 52%" expanded:@YES] &&
        ![events acceptButton:eventButton role:@"AXPopUpButton" title:@"用量：5 小时限额的 52%" expanded:@YES],@"one opening produces one observation despite duplicate notifications");
    NSCAssert(![events acceptButton:eventButton role:@"AXPopUpButton" title:@"用量：5 小时限额的 52%" expanded:@NO] &&
        [events acceptButton:eventButton role:@"AXPopUpButton" title:@"用量：5 小时限额的 53%" expanded:@YES],@"closing rearms the next manual opening without observing on close");
    NSDictionary *(^node)(NSString *,NSDictionary *,NSArray *) = ^NSDictionary *(NSString *role,NSDictionary *attrs,NSArray *children) {
        NSMutableDictionary *n = [attrs mutableCopy]; n[@"AXRole"] = role; n[@"AXChildren"] = children; n[@"testNodeID"] = NSUUID.UUID.UUIDString; return n;
    };
    NSString *url = @"https://claude.ai/epitaxy/local_synthetic";
    NSDictionary *button = node(@"AXPopUpButton",@{@"AXDescription":@"用量：5 小时限额的 52%周四01:10 重置"},@[]);
    NSDictionary *panel = node(@"AXGroup",@{@"AXDescription":@"用量"},@[
        node(@"AXLink",@{@"AXURL":@"https://claude.ai/settings/usage"},@[]),
        node(@"AXStaticText",@{@"AXValue":@"周四01:10 重置"},@[]),
        node(@"AXLevelIndicator",@{@"AXDescription":@"5 小时限额",@"AXValue":@52},@[]),
        node(@"AXStaticText",@{@"AXValue":@"周三19:00 重置"},@[]),
        node(@"AXLevelIndicator",@{@"AXDescription":@"每周 · 所有模型",@"AXValue":@7},@[]),
        node(@"AXLevelIndicator",@{@"AXDescription":@"Weekly · Sonnet",@"AXValue":@99},@[])]);
    NSDictionary *(^read)(NSArray *) = ^NSDictionary *(NSArray *children) {
        UsageAXReader *r = [UsageAXReader new]; r.deadline = NSProcessInfo.processInfo.systemUptime+5;
        r.attribute = ^id(id n,NSString *key) { return n[key]; };
        return [r result:@[node(@"AXWindow",@{},children)]];
    };
    NSDictionary *(^page)(NSArray *) = ^NSDictionary *(NSArray *children) { return node(@"AXWebArea",@{@"AXURL":url},@[node(@"AXGroup",@{},children)]); };
    NSDictionary *open = read(@[page(@[button,panel])]);
    NSCAssert([open[@"fh"][@"percent"] isEqual:@52] && [open[@"sd"][@"percent"] isEqual:@7] && [open[@"sd"][@"reset"] isEqual:@"周三19:00 重置"],@"both account limits and reset text, not model-specific quota");
    NSDictionary *closed = read(@[page(@[button])]);
    NSCAssert(closed[@"fh"] && !closed[@"sd"],@"closed popover exposes only five-hour value");
    NSDictionary *secondButton = node(@"AXPopUpButton",@{@"AXDescription":@"用量：5 小时限额的 52%周四01:10 重置"},@[]);
    NSCAssert(!read(@[page(@[button]),page(@[secondButton])])[@"fh"],@"multiple pages fail closed");
    NSCAssert(!read(@[node(@"AXWebArea",@{@"AXURL":@"https://evil.test/epitaxy/local_synthetic"},@[button])])[@"fh"],@"nonofficial page rejected");
    NSCAssert(!read(@[page(@[node(@"AXWebArea",@{@"AXURL":url},@[button])])])[@"fh"],@"embedded web content rejected");
    NSCAssert(!read(@[page(@[node(@"AXStaticText",@{@"AXValue":@"用量：5 小时限额的 52%"},@[])])])[@"fh"],@"chat text cannot supply quota");
    NSMutableDictionary *bad = [panel mutableCopy]; bad[@"AXChildren"] = @[];
    NSCAssert(!read(@[page(@[button,bad])])[@"sd"],@"unverified panel cannot supply weekly quota");
    NSMutableDictionary *unnamedPanel = [panel mutableCopy]; [unnamedPanel removeObjectForKey:@"AXDescription"]; unnamedPanel[@"AXRole"] = @"AXGenericElement";
    NSCAssert([read(@[page(@[button,unnamedPanel])])[@"sd"][@"percent"] isEqual:@7],@"verified usage panel does not require a particular generic container role/name");
    NSCAssert(!ButtonMetric(@"用量：5 小时限额的 101%") && !ButtonMetric(@"上下文 52%") && !Percent(@YES) && !Percent(@(NAN)),@"invalid percentages and context excluded");
    NSCAssert([ButtonMetric(@"Usage: 0% of 5-hour limit Resets Thu 01:10")[@"percent"] isEqual:@0],@"English, zero and reset supported");
    NSDate *now = [NSDate dateWithTimeIntervalSince1970:1800000000];
    NSDictionary *first = MergeUsageInterface(nil,open,@[@1,@123,@456],now);
    NSDictionary *second = MergeUsageInterface(first,closed,@[@1,@123,@456],[now dateByAddingTimeInterval:20]);
    NSCAssert([second[@"sd"][@"observedAt"] isEqual:now] && ![second[@"sd"][@"visible"] boolValue] && [second[@"fh"][@"observedAt"] isEqual:[now dateByAddingTimeInterval:20]],@"weekly observation time never refreshed by five-hour read");
    NSCAssert(!MergeUsageInterface(first,closed,@[@2,@123,@456],now)[@"sd"],@"other account cannot inherit weekly value");
    NSCAssert(!MergeUsageInterface(first,closed,@[@1,@124,@456],now)[@"sd"],@"new process cannot inherit weekly value");
    NSCAssert(!MergeUsageInterface(first,closed,@[@1,@123,@457],now)[@"sd"],@"identity-store changes cannot inherit weekly value");
    NSMutableDictionary *otherPage = [closed mutableCopy]; otherPage[@"page"] = [url stringByAppendingString:@"2"];
    NSCAssert(MergeUsageInterface(first,otherPage,@[@1,@123,@456],now)[@"sd"],@"same validated account retains quota across Code conversations");
    NSCAssert(!MergeUsageInterface(first,@{@"status":@"需辅助功能权限"},@[@1,@123,@456],now)[@"fh"],@"permission loss clears values");
    NSCAssert([UsageInterfaceTimeLabel(nil,now) isEqual:@"尚未观察到"] && [UsageInterfaceTimeLabel(second[@"sd"],now) rangeOfString:@"^[0-9]{2}:[0-9]{2}:[0-9]{2}$|^[0-9]{2}-[0-9]{2} [0-9]{2}:[0-9]{2}$" options:NSRegularExpressionSearch].location != NSNotFound,@"missing observation is explicit; observed values show time only");
    NSMutableDictionary *wrongWeek = [panel mutableCopy];
    wrongWeek[@"AXChildren"] = [panel[@"AXChildren"] arrayByAddingObject:node(@"AXLevelIndicator",@{@"AXDescription":@"每周 · 所有模型",@"AXValue":@8},@[])];
    NSCAssert(!read(@[page(@[button,wrongWeek])])[@"fh"],@"conflicting weekly values fail closed");
    NSDictionary *otherButton = node(@"AXPopUpButton",@{@"AXDescription":@"用量：5 小时限额的 53%周四01:10 重置"},@[]);
    NSCAssert(!read(@[page(@[otherButton,panel])])[@"fh"],@"changing popup/button values fail closed");
    UsageAXReader *limited = [UsageAXReader new]; limited.deadline = 0;
    limited.attribute = ^id(id n,NSString *key) { NSCAssert(NO,@"deadline prevents more IPC"); return nil; };
    NSCAssert(![limited result:@[page(@[button])]][@"fh"],@"deadline never returns partial values");
    UsageAXReader *privacy = [UsageAXReader new]; privacy.deadline = NSProcessInfo.processInfo.systemUptime+5;
    NSDictionary *conversation = node(@"AXTextArea",@{@"AXValue":@"not quota"},@[]);
    privacy.attribute = ^id(id n,NSString *key) {
        NSCAssert(!(n == conversation && ![key isEqual:@"AXRole"]),@"conversation values never read"); return n[key];
    };
    NSCAssert([privacy result:@[page(@[conversation,button])]][@"fh"],@"quota still readable with conversation skipped");
    NSMutableArray *transcript = [NSMutableArray array];
    for (NSUInteger i=0;i<2001;i++) [transcript addObject:node(@"AXGroup",@{},@[])];
    NSDictionary *longChat = node(@"AXGroup",@{},transcript);
    NSCAssert([read(@[page(@[longChat,button])])[@"fh"][@"percent"] isEqual:@52],@"long transcripts cannot hide a verified quota control");
    NSDictionary *differentButton = node(@"AXPopUpButton",@{@"AXDescription":@"用量：5 小时限额的 75%"},@[]);
    NSDictionary *firstGroup = node(@"AXGroup",@{},@[button]), *otherGroup = node(@"AXGroup",@{},@[differentButton]);
    NSCAssert(!read(@[page(@[otherGroup,longChat,firstGroup])])[@"fh"],@"unfinished same-page traversal cannot hide a conflicting quota button");
    UsageAXReader *twoWindows = [UsageAXReader new]; twoWindows.deadline = NSProcessInfo.processInfo.systemUptime+5;
    twoWindows.attribute = ^id(id n,NSString *key) { return n[key]; };
    NSDictionary *multipleWindows = [twoWindows result:@[node(@"AXWindow",@{},@[page(@[longChat,button])]),node(@"AXWindow",@{},@[page(@[differentButton])])]];
    NSCAssert(!multipleWindows[@"fh"],@"first window exhausting budget cannot hide a second window");
    NSCAssert(!read(@[page(@[longChat])])[@"fh"] && [read(@[page(@[longChat])])[@"status"] isEqual:@"界面读取未完成"],@"unfinished walk without a control stays unfinished");
    UsageAXReader *moving = [UsageAXReader new]; moving.deadline = NSProcessInfo.processInfo.systemUptime+5;
    __block NSUInteger urlReads = 0; NSDictionary *movingPage = page(@[button]);
    moving.attribute = ^id(id n,NSString *key) { if (n == movingPage && [key isEqual:@"AXURL"] && ++urlReads > 1) return [NSURL URLWithString:@"https://claude.ai/epitaxy/other"]; return n[key]; };
    NSCAssert(![moving result:@[movingPage]][@"fh"],@"page change during read discards the match");
    NSDictionary *deep = node(@"AXGroup",@{},@[secondButton]);
    for (NSUInteger i=0;i<45;i++) deep = node(@"AXGroup",@{},@[deep]);
    NSCAssert(read(@[page(@[deep,button])])[@"fh"] && !read(@[page(@[deep])])[@"fh"],@"deep unrelated branches cannot block supported quota controls or become quota candidates");
    NSDictionary *weeklyButton = node(@"AXPopUpButton",@{@"AXDescription":@"用量：每周 · 所有模型：7%周三19:00 重置"},@[]);
    NSDictionary *idle = read(@[node(@"AXWebArea",@{@"AXURL":@"https://claude.ai/epitaxy"},@[node(@"AXGroup",@{},@[weeklyButton])])]);
    NSCAssert(!idle[@"fh"] && [idle[@"sd"][@"percent"] isEqual:@7] && [idle[@"sd"][@"reset"] isEqual:@"周三19:00 重置"],@"idle five-hour window: Code home weekly button supplies only weekly");
    NSDictionary *idlePanel = node(@"AXGroup",@{},@[
        node(@"AXLink",@{@"AXURL":@"https://claude.ai/settings/usage"},@[]),
        node(@"AXStaticText",@{@"AXValue":@"发送第一条消息后开始计时"},@[]),
        node(@"AXLevelIndicator",@{@"AXDescription":@"5 小时限额",@"AXValue":@0},@[]),
        node(@"AXLevelIndicator",@{@"AXDescription":@"每周 · 所有模型",@"AXValue":@7},@[])]);
    NSDictionary *idleOpen = read(@[page(@[weeklyButton,idlePanel])]);
    NSCAssert([idleOpen[@"fh"][@"percent"] isEqual:@0] && [idleOpen[@"fh"][@"reset"] isEqual:@"发送第一条消息后开始计时"] && [idleOpen[@"sd"][@"percent"] isEqual:@7],@"expanded weekly button refreshes reset five-hour value");
    NSCAssert(!read(@[page(@[node(@"AXPopUpButton",@{@"AXDescription":@"用量：每周 · 所有模型：8%"},@[]),idlePanel])])[@"sd"],@"weekly button must match its panel");
    NSCAssert(UsagePopoverOpens(@"AXPopUpButton",@"用量：每周 · 所有模型：7%周三19:00 重置",@YES),@"manual expansion of weekly button triggers one observation");
    NSDictionary *idleMerged = MergeUsageInterface(first,idle,@[@1,@123,@456],[now dateByAddingTimeInterval:30]);
    NSCAssert([idleMerged[@"fh"][@"observedAt"] isEqual:now] && ![idleMerged[@"fh"][@"visible"] boolValue] && [idleMerged[@"sd"][@"observedAt"] isEqual:[now dateByAddingTimeInterval:30]],@"weekly-only read keeps five-hour as last observation");
    NSCAssert(!read(@[node(@"AXWebArea",@{@"AXURL":@"https://claude.ai/epitaxyx"},@[button])])[@"fh"],@"only Code home or conversation pages accepted");
    NSData *syntheticMarker = [NSMutableData dataWithLength:CC_SHA256_DIGEST_LENGTH];
    NSArray *identity = @[@1,@123,@456,@0,@[@1,@2],syntheticMarker];
    NSDictionary *persisted = MergeUsageInterface(nil,open,identity,now);
    NSArray *stored = StoredUsageInterface(@{@1:persisted});
    NSCAssert(!stored[0][@"page"] && !stored[0][@"fh"][@"visible"],@"only quota fields and matching metadata persist");
    NSDictionary *restored = LoadedUsageInterface(stored,now);
    NSCAssert([restored[@1][@"sd"][@"percent"] isEqual:@7] && ![restored[@1][@"sd"][@"visible"] boolValue],@"restart restores last observation, never a fresh reading");
    NSCAssert(!LoadedUsageInterface(stored,[now dateByAddingTimeInterval:-1])[@1],@"future observation rejected");
    NSCAssert(!LoadedUsageInterface(@[@{@"identity":@[@1]}],now)[@1],@"malformed persisted identity rejected");
    NSCAssert(!LoadedUsageInterface([stored arrayByAddingObjectsFromArray:stored],now)[@1],@"ambiguous persisted account rejected");
    NSData *plist = [NSPropertyListSerialization dataWithPropertyList:stored format:NSPropertyListBinaryFormat_v1_0 options:0 error:nil];
    NSCAssert([LoadedUsageInterface([NSPropertyListSerialization propertyListWithData:plist options:0 format:nil error:nil],now) isEqual:restored],@"persistent property-list dates and identity survive round trip");
    NSDictionary *hidden = MergeUsageInterface(restored[@1],@{@"status":@"未观察到 Code 用量"},identity,[now dateByAddingTimeInterval:60]);
    NSCAssert([hidden[@"sd"][@"percent"] isEqual:@7] && [hidden[@"sd"][@"observedAt"] isEqual:now] && ![hidden[@"sd"][@"visible"] boolValue],@"closed interface retains last observation without fabricating freshness");
    NSCAssert(!MergeUsageInterface(restored[@1],closed,@[@2,@123,@456,@0,@[@1,@2],syntheticMarker],now)[@"sd"],@"persisted quota never crosses accounts");
    NSDictionary *weeklyOnly = LoadedUsageInterface(StoredUsageInterface(@{@1:MergeUsageInterface(nil,idle,identity,now)}),now);
    NSCAssert([weeklyOnly[@1][@"sd"][@"percent"] isEqual:@7] && !weeklyOnly[@1][@"fh"],@"weekly-only observation persists without inventing five-hour value");
    // Production rejects shared writable ancestors; keep fixtures under the owned home directory.
    NSString *folder = [NSHomeDirectory() stringByAppendingPathComponent:[@".claude-launcher-marker-test-" stringByAppendingString:NSUUID.UUID.UUIDString]];
    NSCAssert([NSFileManager.defaultManager createDirectoryAtPath:folder withIntermediateDirectories:NO attributes:@{NSFilePosixPermissions:@0700} error:nil],@"marker fixture directory");
    sqlite3 *fixture = NULL; NSString *path = [folder stringByAppendingPathComponent:@"Cookies"];
    NSCAssert(sqlite3_open(path.fileSystemRepresentation,&fixture) == SQLITE_OK,@"marker fixture database"); chmod(path.fileSystemRepresentation,0600);
    const char *schema = "CREATE TABLE meta(key TEXT,value TEXT); INSERT INTO meta VALUES('version','24');"
        "CREATE TABLE cookies(host_key TEXT,name TEXT,path TEXT,value TEXT,encrypted_value BLOB,has_expires INTEGER,expires_utc INTEGER,is_persistent INTEGER,source_scheme INTEGER,source_port INTEGER,top_frame_site_key TEXT);"
        "INSERT INTO cookies VALUES('.claude.ai','lastActiveOrg','/','',NULL,1,20000000000000000,1,2,443,'');"
        "INSERT INTO cookies VALUES('.claude.ai','unrelated','/','synthetic-only',NULL,1,20000000000000000,1,2,443,'');";
    NSCAssert(sqlite3_exec(fixture,schema,NULL,NULL,NULL) == SQLITE_OK,@"marker schema");
    NSMutableData *encrypted = [NSMutableData dataWithLength:83]; memcpy(encrypted.mutableBytes,"v10",3);
    void (^setMarker)(void) = ^{
        sqlite3_stmt *stmt = NULL;
        NSCAssert(sqlite3_prepare_v2(fixture,"UPDATE cookies SET encrypted_value=? WHERE name='lastActiveOrg'",-1,&stmt,NULL) == SQLITE_OK,@"marker update");
        sqlite3_bind_blob(stmt,1,encrypted.bytes,(int)encrypted.length,SQLITE_TRANSIENT);
        NSCAssert(sqlite3_step(stmt) == SQLITE_DONE,@"marker write"); sqlite3_finalize(stmt);
    };
    setMarker(); NSData *firstMarker = UsageOrganizationMarker(folder);
    NSCAssert(firstMarker.length == 32,@"only a selector fingerprint leaves local reader");
    NSCAssert(sqlite3_exec(fixture,"UPDATE cookies SET value='different synthetic value' WHERE name='unrelated'",NULL,NULL,NULL) == SQLITE_OK,@"unrelated cookie write");
    NSCAssert([UsageOrganizationMarker(folder) isEqual:firstMarker],@"ordinary cookie writes do not invalidate observations");
    ((uint8_t *)encrypted.mutableBytes)[20] = 1; setMarker();
    NSCAssert(![UsageOrganizationMarker(folder) isEqual:firstMarker],@"changed organization selector invalidates observations");
    sqlite3_exec(fixture,"BEGIN IMMEDIATE; UPDATE cookies SET value='active' WHERE name='unrelated'",NULL,NULL,NULL);
    NSCAssert(!UsageOrganizationMarker(folder),@"active journal hides observations pending stable identity");
    sqlite3_exec(fixture,"ROLLBACK",NULL,NULL,NULL);
    NSCAssert(UsageOrganizationMarker(folder),@"stable identity recovers after temporary write");
    sqlite3_exec(fixture,"INSERT INTO cookies SELECT * FROM cookies WHERE name='lastActiveOrg'",NULL,NULL,NULL);
    NSCAssert(!UsageOrganizationMarker(folder),@"ambiguous selector rejected");
    sqlite3_exec(fixture,"DELETE FROM cookies WHERE rowid=(SELECT MAX(rowid) FROM cookies); UPDATE meta SET value='25'",NULL,NULL,NULL);
    NSCAssert(!UsageOrganizationMarker(folder),@"unknown cookie schema rejected");
    sqlite3_exec(fixture,"UPDATE meta SET value='24'",NULL,NULL,NULL); sqlite3_close(fixture);
    NSString *wal = [folder stringByAppendingPathComponent:@"Cookies-wal"];
    [@"synthetic WAL" writeToFile:wal atomically:NO encoding:NSUTF8StringEncoding error:nil];
    NSCAssert(!UsageOrganizationMarker(folder),@"WAL never ignored as current identity"); [NSFileManager.defaultManager removeItemAtPath:wal error:nil];
    NSString *hard = [folder stringByAppendingPathComponent:@"hard-link"];
    NSCAssert(link(path.fileSystemRepresentation,hard.fileSystemRepresentation) == 0 && !UsageOrganizationMarker(folder),@"hard-linked store rejected");
    [NSFileManager.defaultManager removeItemAtPath:hard error:nil];
    NSString *sym = [folder stringByAppendingPathComponent:@"profile-link"];
    NSCAssert(symlink(folder.fileSystemRepresentation,sym.fileSystemRepresentation) == 0 && !UsageOrganizationMarker(sym),@"linked profile rejected");
    NSCAssert(UsageOrganizationMarker(folder),@"valid local selector still readable");
    [NSFileManager.defaultManager removeItemAtPath:folder error:nil];
    puts("PASS: passive Code AX traversal, five-hour/weekly/reset fields, closed popover, ambiguous pages, embedded content, invalid values, account/PID cache isolation, separate observation times and persisted observation validation");
}
