#import "ConfigSync.h"
#import "Localization.h"
#include <sys/stat.h>

static NSArray *SyncCategories(void) { return @[@"skills",@"agents",@"commands",@"output-styles"]; }
static const unsigned long long SyncMaxBytes = 50ull*1024*1024;
static const NSUInteger SyncMaxFiles = 5000;

static BOOL Lstat(NSString *path, struct stat *info) { return path.length && lstat(path.fileSystemRepresentation,info) == 0; }
static BOOL RealDirectory(NSString *path) { struct stat info; return Lstat(path,&info) && S_ISDIR(info.st_mode); }

// settings.json may carry API keys or auth helpers; those belong to one account and are never copied.
static BOOL SettingsHaveCredentials(NSDictionary *settings) {
    for (NSString *key in @[@"apiKeyHelper",@"awsAuthRefresh",@"awsCredentialExport",@"otelHeadersHelper"]) if (settings[key]) return YES;
    id env = settings[@"env"];
    if (env && ![env isKindOfClass:NSDictionary.class]) return YES;
    NSRegularExpression *secret = [NSRegularExpression regularExpressionWithPattern:@"TOKEN|KEY|SECRET|PASSWORD|PASSWD|CREDENTIAL|AUTH|COOKIE" options:NSRegularExpressionCaseInsensitive error:nil];
    for (NSString *name in env) if (![name isKindOfClass:NSString.class] || [secret firstMatchInString:name options:0 range:NSMakeRange(0,name.length)]) return YES;
    return NO;
}
static NSString *SourceState(NSString *path, NSString *name, NSString **kind) {
    struct stat info;
    if (!Lstat(path,&info)) return @"不安全";
    if (S_ISLNK(info.st_mode)) { *kind = @"link"; return nil; }
    if (S_ISREG(info.st_mode)) {
        *kind = @"file";
        if (info.st_nlink != 1 || info.st_size > (off_t)SyncMaxBytes) return @"不安全";
        if (![name isEqual:@"settings.json"]) return nil;
        if (info.st_size > 1024*1024) return @"不安全";
        id settings = [NSJSONSerialization JSONObjectWithData:[NSData dataWithContentsOfFile:path] ?: [NSData data] options:0 error:nil];
        if (![settings isKindOfClass:NSDictionary.class]) return @"不安全";
        return SettingsHaveCredentials(settings) ? @"含凭据" : nil;
    }
    if (!S_ISDIR(info.st_mode)) return @"不安全";
    *kind = @"dir";
    NSDirectoryEnumerator *walk = [NSFileManager.defaultManager enumeratorAtURL:[NSURL fileURLWithPath:path]
        includingPropertiesForKeys:@[NSURLIsRegularFileKey,NSURLFileSizeKey] options:0 errorHandler:^BOOL(NSURL *url, NSError *error) { return NO; }];
    unsigned long long bytes = 0; NSUInteger files = 0;
    for (NSURL *url in walk) { // Enumerators do not descend into symlinked folders.
        NSNumber *regular = nil, *size = nil;
        [url getResourceValue:&regular forKey:NSURLIsRegularFileKey error:nil]; [url getResourceValue:&size forKey:NSURLFileSizeKey error:nil];
        if (++files > SyncMaxFiles || (bytes += regular.boolValue ? size.unsignedLongLongValue : 0) > SyncMaxBytes) return @"不安全";
    }
    return nil;
}
static BOOL SameItem(NSString *a, NSString *b, NSString *kind) {
    struct stat info;
    if (!Lstat(b,&info)) return NO;
    BOOL link = S_ISLNK(info.st_mode);
    if ([kind isEqual:@"link"] != link) return NO;
    if (link) return [[NSFileManager.defaultManager destinationOfSymbolicLinkAtPath:a error:nil] isEqual:[NSFileManager.defaultManager destinationOfSymbolicLinkAtPath:b error:nil]];
    if ([kind isEqual:@"dir"] != (BOOL)S_ISDIR(info.st_mode)) return NO;
    return [NSFileManager.defaultManager contentsEqualAtPath:a andPath:b];
}

BOOL ConfigSyncEntryAllowed(NSString *relative) {
    NSArray *parts = relative.pathComponents;
    if (![parts.lastObject length] || [parts.lastObject hasPrefix:@"."]) return NO;
    if (parts.count == 1) return [relative isEqual:@"settings.json"] || [relative.pathExtension.lowercaseString isEqual:@"md"];
    return parts.count == 2 && [SyncCategories() containsObject:parts[0]];
}
NSArray<NSDictionary *> *ConfigSyncItems(NSString *source, NSString *target) {
    if (!RealDirectory(source) || !RealDirectory(target) || [source.stringByStandardizingPath isEqual:target.stringByStandardizingPath]) return @[];
    NSMutableArray *names = [NSMutableArray array];
    for (NSString *name in [[NSFileManager.defaultManager contentsOfDirectoryAtPath:source error:nil] sortedArrayUsingSelector:@selector(compare:)]) {
        if ([name hasPrefix:@"."]) continue;
        if ([name isEqual:@"settings.json"] || [name.pathExtension.lowercaseString isEqual:@"md"]) [names addObject:name];
    }
    for (NSString *category in SyncCategories()) {
        NSString *folder = [source stringByAppendingPathComponent:category];
        if (!RealDirectory(folder)) continue;
        for (NSString *name in [[NSFileManager.defaultManager contentsOfDirectoryAtPath:folder error:nil] sortedArrayUsingSelector:@selector(compare:)])
            if (![name hasPrefix:@"."]) [names addObject:[category stringByAppendingPathComponent:name]];
    }
    NSMutableArray *items = [NSMutableArray array];
    for (NSString *name in names) {
        NSString *kind = @"file", *from = [source stringByAppendingPathComponent:name], *to = [target stringByAppendingPathComponent:name];
        NSString *state = SourceState(from,name,&kind);
        NSString *parent = to.stringByDeletingLastPathComponent; struct stat info;
        if (!state && ![parent isEqual:target] && Lstat(parent,&info) && !S_ISDIR(info.st_mode)) state = @"不安全";
        // A link to the source item, or a copied link with the same destination, already follows every change.
        BOOL linked = !state && Lstat(to,&info) && S_ISLNK(info.st_mode) &&
            ([kind isEqual:@"link"] ? SameItem(from,to,kind) : [[NSFileManager.defaultManager destinationOfSymbolicLinkAtPath:to error:nil] isEqual:from]);
        if (!state) state = linked ? @"已链接" : !Lstat(to,&info) ? @"缺少" : SameItem(from,to,kind) ? @"相同" : @"不同";
        [items addObject:@{@"name":name,@"kind":kind,@"state":state}];
    }
    return items;
}

NSDictionary *ApplyConfigSync(NSString *source, NSString *target, NSArray<NSString *> *names, BOOL overwrite, BOOL link) {
    NSUInteger added = 0, replaced = 0, skipped = 0; NSString *failure = nil;
    NSMutableDictionary *states = [NSMutableDictionary dictionary], *kinds = [NSMutableDictionary dictionary];
    for (NSDictionary *item in ConfigSyncItems(source,target)) { states[item[@"name"]] = item[@"state"]; kinds[item[@"name"]] = item[@"kind"]; }
    if (!states.count && names.count) failure = L(@"来源或目标配置目录不存在、是链接或相同，没有写入。");
    NSFileManager *files = NSFileManager.defaultManager;
    for (NSString *name in names) {
        if (failure) break;
        NSString *state = states[name];
        // Source items that are links stay links to the same place; everything else becomes a link to the source in link mode.
        BOOL linkable = link && ![kinds[name] isEqual:@"link"];
        BOOL replace = overwrite && ([state isEqual:@"不同"] || ([state isEqual:@"相同"] && linkable));
        if (!([state isEqual:@"缺少"] || replace)) { skipped++; continue; }
        if (!RealDirectory(target)) { failure = L(@"目标配置目录在同步期间变化，已停止。"); break; }
        NSString *from = [source stringByAppendingPathComponent:name], *to = [target stringByAppendingPathComponent:name];
        NSString *parent = to.stringByDeletingLastPathComponent;
        if (![parent isEqual:target] && !RealDirectory(parent) &&
            ![files createDirectoryAtPath:parent withIntermediateDirectories:NO attributes:@{NSFilePosixPermissions:@0755} error:nil]) {
            failure = [NSString stringWithFormat:L(@"无法创建目录 %@，已停止。"),parent.lastPathComponent]; break;
        }
        NSString *temporary = [parent stringByAppendingPathComponent:[@".claude-launcher-sync-" stringByAppendingString:NSUUID.UUID.UUIDString]];
        BOOL made = linkable ? [files createSymbolicLinkAtPath:temporary withDestinationPath:from error:nil] : [files copyItemAtPath:from toPath:temporary error:nil];
        if (!made) { [files removeItemAtPath:temporary error:nil]; failure = [NSString stringWithFormat:L(@"复制 %@ 失败，已停止。"),name]; break; }
        if (replace && ![files trashItemAtURL:[NSURL fileURLWithPath:to] resultingItemURL:nil error:nil]) {
            [files removeItemAtPath:temporary error:nil]; failure = [NSString stringWithFormat:L(@"无法把旧的 %@ 移到废纸篓，已停止。"),name]; break;
        }
        // moveItem refuses an existing destination, so a file created meanwhile is never overwritten.
        if (![files moveItemAtPath:temporary toPath:to error:nil]) { [files removeItemAtPath:temporary error:nil]; failure = [NSString stringWithFormat:L(@"%@ 在同步期间出现，未覆盖，已停止。"),name]; break; }
        if (replace) replaced++; else added++;
    }
    NSMutableDictionary *result = [@{@"added":@(added),@"replaced":@(replaced),@"skipped":@(skipped)} mutableCopy];
    if (failure) result[@"error"] = failure;
    return result;
}

void TestConfigSync(void) {
    NSFileManager *files = NSFileManager.defaultManager;
    NSString *root = [NSTemporaryDirectory() stringByAppendingPathComponent:[@"config-sync-" stringByAppendingString:NSUUID.UUID.UUIDString]];
    NSString *source = [root stringByAppendingPathComponent:@"a"], *target = [root stringByAppendingPathComponent:@"b"];
    void (^write)(NSString *, NSString *) = ^(NSString *path, NSString *text) {
        [files createDirectoryAtPath:path.stringByDeletingLastPathComponent withIntermediateDirectories:YES attributes:nil error:nil];
        [[text dataUsingEncoding:NSUTF8StringEncoding] writeToFile:path atomically:NO];
    };
    write([source stringByAppendingPathComponent:@"CLAUDE.md"],@"rules");
    write([source stringByAppendingPathComponent:@"RTK.md"],@"rtk");
    write([source stringByAppendingPathComponent:@"settings.json"],@"{\"env\":{\"PATH\":\"/bin\"},\"hooks\":{}}");
    write([source stringByAppendingPathComponent:@".claude.json"],@"{\"oauthAccount\":{}}");
    write([source stringByAppendingPathComponent:@"history.jsonl"],@"secret chat");
    write([source stringByAppendingPathComponent:@"projects/x/session.jsonl"],@"chat");
    write([source stringByAppendingPathComponent:@"skills/own/SKILL.md"],@"skill");
    write([source stringByAppendingPathComponent:@"agents/reviewer.md"],@"agent");
    [files createSymbolicLinkAtPath:[source stringByAppendingPathComponent:@"skills/linked"] withDestinationPath:@"/nonexistent/skill" error:nil];
    write([target stringByAppendingPathComponent:@"RTK.md"],@"rtk");
    write([target stringByAppendingPathComponent:@"agents/reviewer.md"],@"local agent");
    NSMutableDictionary *states = [NSMutableDictionary dictionary];
    for (NSDictionary *item in ConfigSyncItems(source,target)) states[item[@"name"]] = item[@"state"];
    NSDictionary *expected = @{@"CLAUDE.md":@"缺少",@"RTK.md":@"相同",@"settings.json":@"缺少",@"skills/own":@"缺少",@"skills/linked":@"缺少",@"agents/reviewer.md":@"不同"};
    NSCAssert([states isEqual:expected],@"only allowlisted config is listed; .claude.json, history and projects never appear");
    NSDictionary *result = ApplyConfigSync(source,target,states.allKeys,NO,NO);
    NSCAssert([result[@"added"] isEqual:@4] && [result[@"replaced"] isEqual:@0] && [result[@"skipped"] isEqual:@2] && !result[@"error"],@"missing items copied, existing kept");
    NSCAssert([[NSString stringWithContentsOfFile:[target stringByAppendingPathComponent:@"agents/reviewer.md"] encoding:NSUTF8StringEncoding error:nil] isEqual:@"local agent"],@"differing item untouched without overwrite");
    NSCAssert([[files destinationOfSymbolicLinkAtPath:[target stringByAppendingPathComponent:@"skills/linked"] error:nil] isEqual:@"/nonexistent/skill"],@"symlinks copied as links");
    NSCAssert(![files fileExistsAtPath:[target stringByAppendingPathComponent:@".claude.json"]] && ![files fileExistsAtPath:[target stringByAppendingPathComponent:@"history.jsonl"]],@"account state never copied");
    NSCAssert([ApplyConfigSync(source,target,states.allKeys,NO,NO)[@"added"] isEqual:@0],@"second run adds nothing");
    write([source stringByAppendingPathComponent:@"settings.json"],@"{\"env\":{\"ANTHROPIC_AUTH_TOKEN\":\"x\"}}");
    write([source stringByAppendingPathComponent:@"evil.md"],@"x");
    [files removeItemAtPath:[target stringByAppendingPathComponent:@"settings.json"] error:nil];
    [files createSymbolicLinkAtPath:[target stringByAppendingPathComponent:@"commands"] withDestinationPath:root error:nil];
    write([source stringByAppendingPathComponent:@"commands/run.md"],@"cmd");
    states = [NSMutableDictionary dictionary];
    for (NSDictionary *item in ConfigSyncItems(source,target)) states[item[@"name"]] = item[@"state"];
    NSCAssert([states[@"settings.json"] isEqual:@"含凭据"] && [states[@"commands/run.md"] isEqual:@"不安全"],@"credentials and linked target folders refused");
    result = ApplyConfigSync(source,target,@[@"settings.json",@"commands/run.md"],YES,NO);
    NSCAssert([result[@"added"] isEqual:@0] && ![files fileExistsAtPath:[target stringByAppendingPathComponent:@"settings.json"]] &&
              ![files fileExistsAtPath:[root stringByAppendingPathComponent:@"run.md"]],@"refused items never written");
    // Link mode: new items become links, identical copies become links with overwrite, and links follow later edits.
    NSString *linkTarget = [root stringByAppendingPathComponent:@"c"];
    write([linkTarget stringByAppendingPathComponent:@"CLAUDE.md"],@"rules");
    result = ApplyConfigSync(source,linkTarget,@[@"CLAUDE.md",@"RTK.md",@"skills/linked",@"skills/own"],NO,YES);
    NSCAssert([result[@"added"] isEqual:@3] && [result[@"skipped"] isEqual:@1],@"identical copy kept without overwrite");
    result = ApplyConfigSync(source,linkTarget,@[@"CLAUDE.md"],YES,YES);
    NSString *linkedRules = [linkTarget stringByAppendingPathComponent:@"CLAUDE.md"];
    NSCAssert([result[@"replaced"] isEqual:@1] && [[files destinationOfSymbolicLinkAtPath:linkedRules error:nil] isEqual:[source stringByAppendingPathComponent:@"CLAUDE.md"]],@"identical copy converted to a link with overwrite");
    NSCAssert([[files destinationOfSymbolicLinkAtPath:[linkTarget stringByAppendingPathComponent:@"skills/own"] error:nil] isEqual:[source stringByAppendingPathComponent:@"skills/own"]] &&
              [[files destinationOfSymbolicLinkAtPath:[linkTarget stringByAppendingPathComponent:@"skills/linked"] error:nil] isEqual:@"/nonexistent/skill"],@"folders linked to source; source links keep their own destination");
    write([source stringByAppendingPathComponent:@"CLAUDE.md"],@"rules v2");
    NSCAssert([[NSString stringWithContentsOfFile:linkedRules encoding:NSUTF8StringEncoding error:nil] isEqual:@"rules v2"],@"linked target follows later source edits");
    states = [NSMutableDictionary dictionary];
    for (NSDictionary *item in ConfigSyncItems(source,linkTarget)) states[item[@"name"]] = item[@"state"];
    NSCAssert([states[@"CLAUDE.md"] isEqual:@"已链接"] && [states[@"skills/own"] isEqual:@"已链接"] && [states[@"skills/linked"] isEqual:@"已链接"],@"launcher links and same-destination links reported as linked");
    NSCAssert([ApplyConfigSync(source,linkTarget,@[@"CLAUDE.md",@"skills/own"],YES,YES)[@"replaced"] isEqual:@0],@"linked items never replaced");
    NSCAssert(!ConfigSyncItems(source,source).count && !ConfigSyncItems(source,[root stringByAppendingPathComponent:@"missing"]).count,@"same or missing target refused");
    [files removeItemAtPath:root error:nil];
    puts("PASS: config sync allowlist (settings/md/skills/agents/commands/output-styles), add-only default, links kept as links, link mode follows source edits, credentials and linked targets refused; temporary fixtures only");
}
