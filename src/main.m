#import <Cocoa/Cocoa.h>
#import <ApplicationServices/ApplicationServices.h>
#import <libproc.h>
#import <sys/sysctl.h>
#import <sys/file.h>
#import <unistd.h>
#import <errno.h>
#import <sys/stat.h>
#import <Security/Security.h>
#import <CommonCrypto/CommonDigest.h>
#import "Projects.h"
#import "ProjectImport.h"
#import "UsageInterface.h"
#import "Settings.h"
#import <ServiceManagement/ServiceManagement.h>

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
static NSString *ValidateProfileRoot(NSString *path, NSString *expected) {
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
    return CheckStorageNode(path,NO);
}
static NSString *ValidateProfileAtPath(NSString *path, NSString *expected) {
    NSString *failure = ValidateProfileRoot(path,expected);
    if (failure) return failure;
    // Chromium's SingletonSocket/SingletonLock are expected links; they are not credential storage.
    for (NSString *relative in @[@"Cookies",@"Cookies-journal",@"Network",@"Local Storage",@"Session Storage",@"IndexedDB",@"Partitions",@"Local State",@"Preferences",@"config.json",@"Cache",@"Code Cache",@"Service Worker",@"ClaudeCodeConfig"]) {
        failure = CheckStorageNode([path stringByAppendingPathComponent:relative],YES);
        if (failure) return failure;
    }
    return nil;
}
static void ValidateProfileAsync(NSString *path, void (^completion)(NSString *)) {
    dispatch_async(dispatch_get_global_queue(QOS_CLASS_USER_INITIATED,0), ^{
        @autoreleasepool {
            NSString *failure = ValidateProfileAtPath(path,path);
            dispatch_async(dispatch_get_main_queue(), ^{ completion(failure); });
        }
    });
}
// Returns why an account cannot be deleted right now, or nil. 01 is the official default profile and is never deleted here.
static NSString *DeleteBlocker(NSInteger account, NSArray *pids, BOOL starting) {
    if (account == 1) return @"账号 01 是 Claude 默认目录，启动器不删除它。";
    if (account < 2 || account > 10) return @"账号编号无效。";
    if (starting || pids.count) return @"该账号正在运行或启动中。请先在 Claude 中正常退出（⌘Q），再删除。";
    return nil;
}
// ---- Updates (docs/SPEC.md「更新与安装」) ----
static NSString * const ReleasesPage = @"https://github.com/wyl20020808/ClaudeLauncher/releases";
static NSString * const GitHubDownloads = @"https://github.com/wyl20020808/ClaudeLauncher/releases/latest/download/";
static const long long UpdateSizeLimit = 50LL*1024*1024;
static NSData *DERWrap(uint8_t tag, NSData *content) {
    NSMutableData *out = [NSMutableData dataWithBytes:&tag length:1];
    NSUInteger length = content.length;
    if (length < 128) { uint8_t byte = (uint8_t)length; [out appendBytes:&byte length:1]; }
    else {
        uint8_t bytes[sizeof(NSUInteger)]; int count = 0;
        for (NSUInteger value = length; value; value >>= 8) bytes[count++] = value & 0xff;
        uint8_t first = 0x80 | (uint8_t)count; [out appendBytes:&first length:1];
        for (int i = count-1; i >= 0; i--) [out appendBytes:&bytes[i] length:1];
    }
    [out appendData:content]; return out;
}
static NSData *DERInteger(NSData *value) {
    NSMutableData *bytes = [value mutableCopy];
    while (bytes.length > 1 && ((const uint8_t *)bytes.bytes)[0] == 0) [bytes replaceBytesInRange:NSMakeRange(0,1) withBytes:NULL length:0];
    if (bytes.length && (((const uint8_t *)bytes.bytes)[0] & 0x80)) { uint8_t zero = 0; [bytes replaceBytesInRange:NSMakeRange(0,0) withBytes:&zero length:1]; }
    return DERWrap(0x02, bytes);
}
// The release key is published as .NET RSAKeyValue XML (Windows side). Build a PKCS#1 RSAPublicKey from it.
static SecKeyRef PublicKeyFromXML(NSString *xml) CF_RETURNS_RETAINED {
    NSString *(^field)(NSString *) = ^NSString *(NSString *name) {
        NSRange open = [xml rangeOfString:[NSString stringWithFormat:@"<%@>",name]], close = [xml rangeOfString:[NSString stringWithFormat:@"</%@>",name]];
        if (open.location == NSNotFound || close.location == NSNotFound || NSMaxRange(open) > close.location) return nil;
        return [xml substringWithRange:NSMakeRange(NSMaxRange(open),close.location-NSMaxRange(open))];
    };
    NSData *modulus = [[NSData alloc] initWithBase64EncodedString:field(@"Modulus") ?: @"" options:0];
    NSData *exponent = [[NSData alloc] initWithBase64EncodedString:field(@"Exponent") ?: @"" options:0];
    if (modulus.length < 256 || !exponent.length) return NULL;
    NSMutableData *sequence = [DERInteger(modulus) mutableCopy]; [sequence appendData:DERInteger(exponent)];
    return SecKeyCreateWithData((__bridge CFDataRef)DERWrap(0x30,sequence), (__bridge CFDictionaryRef)@{(id)kSecAttrKeyType:(id)kSecAttrKeyTypeRSA,(id)kSecAttrKeyClass:(id)kSecAttrKeyClassPublic}, NULL);
}
// latest-macos.json may be signed by the shared release key (Windows publisher) or the macOS publisher key; either is accepted.
static NSArray *ReleaseKeys(void) {
    static NSArray *keys; static dispatch_once_t once;
    dispatch_once(&once, ^{
        NSMutableArray *loaded = [NSMutableArray array];
        // Bundle name must not end in "-macos": NSBundle treats that as a platform variant and returns it for "update-public-key" too.
        for (NSString *name in @[@"update-public-key",@"macos-publisher-key"]) {
            NSString *xml = [NSString stringWithContentsOfFile:[NSBundle.mainBundle pathForResource:name ofType:@"xml"] encoding:NSUTF8StringEncoding error:nil];
            SecKeyRef key = xml ? PublicKeyFromXML(xml) : NULL;
            if (key) [loaded addObject:CFBridgingRelease(key)];
        }
        keys = loaded;
    });
    return keys;
}
static NSComparisonResult CompareVersions(NSString *a, NSString *b) {
    NSArray *x = [a componentsSeparatedByString:@"."], *y = [b componentsSeparatedByString:@"."];
    for (NSUInteger i = 0; i < MAX(x.count,y.count); i++) {
        NSInteger p = i < x.count ? [x[i] integerValue] : 0, q = i < y.count ? [y[i] integerValue] : 0;
        if (p != q) return p < q ? NSOrderedAscending : NSOrderedDescending;
    }
    return NSOrderedSame;
}
// Keep the offers carrying the highest version, in source order (self-hosted first, then GitHub).
static NSArray *BestOffers(NSArray<NSDictionary *> *offers) {
    NSString *best = nil;
    for (NSDictionary *offer in offers) if (!best || CompareVersions(offer[@"manifest"][@"version"],best) == NSOrderedDescending) best = offer[@"manifest"][@"version"];
    NSMutableArray *chosen = [NSMutableArray array];
    for (NSDictionary *offer in offers) if (best && CompareVersions(offer[@"manifest"][@"version"],best) == NSOrderedSame) [chosen addObject:offer];
    return chosen;
}
static BOOL Matches(id value, NSString *pattern) {
    return [value isKindOfClass:NSString.class] && [value rangeOfString:pattern options:NSRegularExpressionSearch].location != NSNotFound;
}
static NSString *SHA256Hex(NSData *data) {
    unsigned char digest[CC_SHA256_DIGEST_LENGTH]; CC_SHA256(data.bytes,(CC_LONG)data.length,digest);
    NSMutableString *hex = [NSMutableString string];
    for (int i = 0; i < CC_SHA256_DIGEST_LENGTH; i++) [hex appendFormat:@"%02x",digest[i]];
    return hex;
}
// Validates shape and RSA-SHA256 (PKCS#1 v1.5) signature of latest-macos.json. Returns the manifest or nil with *failure set.
static NSDictionary *VerifyManifest(NSData *data, NSArray *keys, NSString **failure) {
    *failure = @"更新清单格式无效。";
    if (!data.length || data.length > 16384) { *failure = @"更新清单过大或为空。"; return nil; }
    NSDictionary *m = [NSJSONSerialization JSONObjectWithData:data options:0 error:nil];
    if (![m isKindOfClass:NSDictionary.class]) return nil;
    NSString *version = m[@"version"]; NSNumber *size = m[@"size"];
    if (![m[@"platform"] isEqual:@"macos"] || !Matches(version,@"^[0-9]{1,4}\\.[0-9]{1,4}\\.[0-9]{1,4}$")) return nil;
    if (![m[@"file"] isEqual:[NSString stringWithFormat:@"ClaudeLauncher-%@.dmg",version]]) return nil;
    if (![size isKindOfClass:NSNumber.class] || size.doubleValue != size.longLongValue || size.longLongValue < 1024 || size.longLongValue > UpdateSizeLimit) return nil;
    if (!Matches(m[@"sha256"],@"^[a-f0-9]{64}$") || !Matches(m[@"minimumOS"],@"^[0-9]{1,3}(\\.[0-9]{1,3}){0,2}$") || ![m[@"signature"] isKindOfClass:NSString.class]) return nil;
    NSData *signature = [[NSData alloc] initWithBase64EncodedString:m[@"signature"] options:0];
    NSString *payload = [NSString stringWithFormat:@"macos\n%@\n%@\n%lld\n%@\n",version,m[@"file"],size.longLongValue,m[@"sha256"]];
    BOOL verified = NO;
    for (id key in keys) verified = verified || (signature && SecKeyVerifySignature((__bridge SecKeyRef)key, kSecKeyAlgorithmRSASignatureMessagePKCS1v15SHA256, (__bridge CFDataRef)[payload dataUsingEncoding:NSUTF8StringEncoding], (__bridge CFDataRef)signature, NULL));
    if (!verified) {
        *failure = @"更新清单签名无效，已拒绝。"; return nil;
    }
    *failure = nil; return m;
}
// Set only by the --update-test development mode: print each request and redirect decision.
static BOOL TraceNetwork;
static BOOL NetworkEnabled; // Only saved opt-in plus an explicit update action permits requests.
// Only this project release path and GitHub download hosts may receive update redirects.
static BOOL RedirectAllowed(NSURL *url, BOOL github) {
    return github && AllowedUpdateURL(url,YES);
}
// The running app may replace itself only from a normal, writable .app location (not App Translocation or a mounted image).
static NSString *InstallBlocker(NSString *bundlePath) {
    if (![bundlePath.pathExtension isEqual:@"app"] || [bundlePath containsString:@"/AppTranslocation/"] || [bundlePath hasPrefix:@"/Volumes/"])
        return @"请先把启动器拖到「应用程序」文件夹并从那里打开，再检查更新。";
    if (![NSFileManager.defaultManager isWritableFileAtPath:bundlePath.stringByDeletingLastPathComponent]) return @"没有写入应用所在文件夹的权限，无法自动安装。";
    return nil;
}
// New app must satisfy the running app's designated requirement (same bundle id and signing certificate).
static NSString *SameSigner(NSString *candidate) {
    SecCodeRef me = NULL; SecRequirementRef requirement = NULL; SecStaticCodeRef other = NULL; NSString *failure = nil;
    if (SecCodeCopySelf(kSecCSDefaultFlags,&me) || SecCodeCopyDesignatedRequirement(me,kSecCSDefaultFlags,&requirement)) failure = @"无法读取当前应用的签名要求。";
    else if (SecStaticCodeCreateWithPath((__bridge CFURLRef)[NSURL fileURLWithPath:candidate],kSecCSDefaultFlags,&other)) failure = @"无法读取新版本的签名。";
    else if (SecStaticCodeCheckValidity(other,kSecCSCheckAllArchitectures|kSecCSStrictValidate|kSecCSCheckNestedCode,requirement)) failure = @"新版本与当前版本的签名证书不一致或签名无效，已拒绝更新。";
    if (me) CFRelease(me); if (requirement) CFRelease(requirement); if (other) CFRelease(other);
    return failure;
}
// Session delegate: enforces the redirect allowlist, collects the body under a size limit and reports progress.
@interface RedirectGuard : NSObject <NSURLSessionDataDelegate>
@property BOOL github;
@property long long limit;
@property NSURL *url;
@property NSMutableData *body;
@property NSString *overLimit;
@property (copy) void (^progress)(long long received, long long expected);
@property (copy) void (^done)(NSData *data, NSString *failure);
@end
@implementation RedirectGuard
- (void)URLSession:(NSURLSession *)session task:(NSURLSessionTask *)task willPerformHTTPRedirection:(NSHTTPURLResponse *)response newRequest:(NSURLRequest *)request completionHandler:(void (^)(NSURLRequest *))completionHandler {
    BOOL allowed = NetworkEnabled && RedirectAllowed(request.URL,self.github);
    if (TraceNetwork) printf("[network] redirect -> %s %s\n",request.URL.host.UTF8String,allowed ? "allowed" : "blocked");
    completionHandler(allowed ? request : nil);
}
- (void)URLSession:(NSURLSession *)session dataTask:(NSURLSessionDataTask *)task didReceiveResponse:(NSURLResponse *)response completionHandler:(void (^)(NSURLSessionResponseDisposition))completionHandler {
    if (response.expectedContentLength > self.limit) { self.overLimit = @"下载内容超出上限。"; completionHandler(NSURLSessionResponseCancel); return; }
    completionHandler(NSURLSessionResponseAllow);
}
- (void)URLSession:(NSURLSession *)session dataTask:(NSURLSessionDataTask *)task didReceiveData:(NSData *)data {
    [self.body appendData:data];
    if ((long long)self.body.length > self.limit) { self.overLimit = @"下载内容超出上限。"; [task cancel]; return; }
    if (self.progress) {
        long long received = self.body.length, expected = task.response.expectedContentLength;
        void (^progress)(long long, long long) = self.progress;
        dispatch_async(dispatch_get_main_queue(), ^{ progress(received,expected); });
    }
}
- (void)URLSession:(NSURLSession *)session task:(NSURLSessionTask *)task didCompleteWithError:(NSError *)error {
    NSInteger status = [task.response isKindOfClass:NSHTTPURLResponse.class] ? ((NSHTTPURLResponse *)task.response).statusCode : 0;
    if (TraceNetwork) printf("[network] %s -> HTTP %ld, %lu bytes\n",self.url.absoluteString.UTF8String,(long)status,(unsigned long)self.body.length);
    NSString *failure = self.overLimit ?: error.code == NSURLErrorCancelled ? @"已取消。" : error ? error.localizedDescription : status != 200 ? [NSString stringWithFormat:@"服务器返回 %ld。",(long)status] : nil;
    NSData *data = failure ? nil : [self.body copy];
    void (^done)(NSData *, NSString *) = self.done;
    [session finishTasksAndInvalidate];
    dispatch_async(dispatch_get_main_queue(), ^{ done(data, failure); });
}
@end
// Returns the task so a caller can cancel it; progress (optional) runs on the main queue.
static NSURLSessionTask *Fetch(NSURL *url, BOOL github, long long limit, NSTimeInterval timeout, void (^progress)(long long received, long long expected), void (^done)(NSData *data, NSString *failure)) {
    if (!NetworkEnabled || !github || !AllowedUpdateURL(url,NO)) { done(nil,@"本地安全策略已阻止此请求；仅允许主动检查本项目的 GitHub 更新。"); return nil; }
    RedirectGuard *guard = [RedirectGuard new]; guard.github = github;
    guard.limit = limit; guard.url = url; guard.body = [NSMutableData data]; guard.progress = progress; guard.done = done;
    NSURLSessionConfiguration *config = NSURLSessionConfiguration.ephemeralSessionConfiguration;
    config.timeoutIntervalForRequest = 15; config.timeoutIntervalForResource = timeout; config.requestCachePolicy = NSURLRequestReloadIgnoringLocalCacheData;
    config.HTTPCookieStorage = nil; config.URLCredentialStorage = nil; config.URLCache = nil;
    config.HTTPShouldSetCookies = NO;
    config.HTTPAdditionalHeaders = @{@"User-Agent":[@"ClaudeProfileLauncher/" stringByAppendingString:[NSBundle.mainBundle objectForInfoDictionaryKey:@"CFBundleShortVersionString"] ?: @"0"]};
    NSURLSession *session = [NSURLSession sessionWithConfiguration:config delegate:guard delegateQueue:nil];
    NSURLSessionTask *task = [session dataTaskWithURL:url];
    [task resume];
    return task;
}
static NSString *RunTool(NSString *path, NSArray<NSString *> *arguments, NSData **output) {
    NSTask *task = [NSTask new]; task.executableURL = [NSURL fileURLWithPath:path]; task.arguments = arguments;
    NSPipe *pipe = [NSPipe pipe]; task.standardOutput = pipe; task.standardError = [NSFileHandle fileHandleWithNullDevice];
    NSError *error = nil;
    if (![task launchAndReturnError:&error]) return error.localizedDescription;
    NSData *data = [pipe.fileHandleForReading readDataToEndOfFile]; [task waitUntilExit];
    if (output) *output = data;
    return task.terminationStatus == 0 ? nil : [NSString stringWithFormat:@"%@ 执行失败（%d）。",path.lastPathComponent,task.terminationStatus];
}

static BOOL LoginConflict(NSDictionary<NSNumber *,NSArray *> *processes, NSInteger account) {
    for (NSNumber *key in processes) if (key.integerValue != account && [processes[key] count]) return YES;
    return NO;
}
static NSString *LoginBlocker(NSDictionary<NSNumber *,NSArray *> *processes, NSArray<NSNumber *> *busy, NSInteger account) {
    if (LoginConflict(processes,account)) return @"请先保存工作并正常退出其他 Claude 实例（包括未管理的实例），再进入登录保护。启动器不会替你退出或登出账号。";
    if ([processes[@(account)] count] > 1) return @"该账号有多个主进程，不能进入登录保护。";
    for (NSNumber *key in busy) if (key.integerValue != account) return @"其他账号正在检查或启动，请等待完成后重试。";
    return nil;
}
static NSString *CodeStatus(NSInteger account, BOOL running, BOOL configured) {
    if (account == 1) return @"Code 默认";
    if (!running) return @"Code 待启动";
    return configured ? @"Code 已指定" : @"Code 未确认";
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
static NSArray *UsageIdentity(NSInteger account, NSArray *pids) {
    if (account < 1 || account > 10 || pids.count != 1) return nil;
    pid_t pid = [pids.firstObject intValue]; char executable[PROC_PIDPATHINFO_MAXSIZE] = {0};
    if (proc_pidpath(pid,executable,sizeof(executable)) <= 0 || !IsMainExecutable(@(executable))) return nil;
    NSArray *arguments = Arguments(pid);
    if (!arguments.count || AccountForArguments(arguments) != account) return nil;
    struct proc_bsdinfo process = {0};
    if (proc_pidinfo(pid,PROC_PIDTBSDINFO,0,&process,sizeof(process)) != sizeof(process)) return nil;
    NSString *profile = ProfilePath(account);
    if (ValidateProfileRoot(profile,profile)) return nil;
    NSMutableArray *identity = [NSMutableArray arrayWithArray:@[@(account),@(pid),@(process.pbi_start_tvsec),@(process.pbi_start_tvusec)]];
    // Metadata triggers selector revalidation; ordinary cookie writes are not an account change.
    for (NSString *name in @[@"",@"Cookies",@"Cookies-journal",@"Cookies-wal",@"Cookies-shm"]) {
        struct stat st; NSString *path = name.length ? [profile stringByAppendingPathComponent:name] : profile;
        if (lstat(path.fileSystemRepresentation,&st)) { if (errno != ENOENT || !name.length) return nil; [identity addObject:@0]; continue; }
        if (st.st_uid != getuid() || (st.st_mode & 0022) || (name.length ? (!S_ISREG(st.st_mode) || st.st_nlink != 1) : !S_ISDIR(st.st_mode))) return nil;
        [identity addObject:name.length ? @[@(st.st_dev),@(st.st_ino),@(st.st_size),@(st.st_mtimespec.tv_sec),@(st.st_mtimespec.tv_nsec),@(st.st_ctimespec.tv_sec),@(st.st_ctimespec.tv_nsec)] : @[@(st.st_dev),@(st.st_ino)]];
    }
    static NSMutableDictionary *markers;
    @synchronized(NSProcessInfo.processInfo) {
        if (!markers) markers = [NSMutableDictionary dictionary];
        NSDictionary *cached = markers[@(account)]; NSData *marker = nil;
        if ([cached[@"state"] isEqual:identity] && [cached[@"until"] doubleValue] > NSProcessInfo.processInfo.systemUptime) marker = cached[@"marker"];
        else {
            marker = UsageOrganizationMarker(profile);
            if (marker) markers[@(account)] = @{@"state":identity,@"marker":marker,@"until":@(NSProcessInfo.processInfo.systemUptime+60)};
            else [markers removeObjectForKey:@(account)];
        }
        if (!marker) return nil;
        return @[@(account),@(pid),@(process.pbi_start_tvsec),@(process.pbi_start_tvusec),identity[4],marker];
    }
}
static BOOL VisibleWindow(pid_t pid) {
    NSArray *windows = CFBridgingRelease(CGWindowListCopyWindowInfo(kCGWindowListOptionOnScreenOnly | kCGWindowListExcludeDesktopElements, kCGNullWindowID));
    for (NSDictionary *window in windows) {
        if ([window[(id)kCGWindowOwnerPID] intValue] == pid && [window[(id)kCGWindowLayer] intValue] == 0) return YES;
    }
    return NO;
}

static ProjectStore *RegistrationValidator(void) {
    return [[ProjectStore alloc] initWithDirectory:[NSHomeDirectory() stringByAppendingPathComponent:@"Library/Application Support/Claude Launcher"]
        home:NSHomeDirectory() applicationPaths:@[NSBundle.mainBundle.bundlePath,@"/Applications/Claude 启动器.app",[NSHomeDirectory() stringByAppendingPathComponent:@"Applications/Claude 启动器.app"]]];
}
static NSString *RegistrationGuard(NSInteger account) {
    if (account < 1 || account > 10) return @"目标账号编号无效。";
    if ([NSUserDefaults.standardUserDefaults integerForKey:@"loginProtectionAccount"]) return @"登录保护期间不能登记项目，请先完成登录并结束保护。";
    NSString *version = nil;
    for (NSString *path in @[@"/Applications/Claude.app",[NSHomeDirectory() stringByAppendingPathComponent:@"Applications/Claude.app"]]) {
        if (![NSFileManager.defaultManager isExecutableFileAtPath:[path stringByAppendingPathComponent:@"Contents/MacOS/Claude"]]) continue;
        version = [NSDictionary dictionaryWithContentsOfFile:[path stringByAppendingPathComponent:@"Contents/Info.plist"]][@"CFBundleShortVersionString"]; break;
    }
    if (![version isEqual:@"4.29.0"]) return @"此功能只验证过 Claude 4.29.0；当前版本不匹配，已停止写入。";
    NSString *failure = nil; NSDictionary *processes = Scan(&failure);
    if (failure) return failure;
    if ([processes[@(account)] count]) return @"请保存工作并正常退出目标账号后重试；登记期间不要从其他入口启动它。启动器不会替你退出。";
    return ValidateProfileRoot(ProfilePath(account),ProfilePath(account));
}
static NSDictionary *RegisterForAccount(NSInteger account, NSArray *paths, BOOL apply) {
    NSString *failure = RegistrationGuard(account) ?: ValidateProfileAtPath(ProfilePath(account),ProfilePath(account));
    if (failure) return @{@"added":@0,@"skipped":@0,@"planned":@0,@"error":failure};
    return RegisterProjectDirectories(ProfilePath(account),paths,RegistrationValidator(),NSHomeDirectory(),apply,^NSString *{
        return RegistrationGuard(account);
    });
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

static NSImage *Tinted(NSString *symbol, CGFloat size, NSColor *color) {
    NSImage *image = [[NSImage imageWithSystemSymbolName:symbol accessibilityDescription:nil] imageWithSymbolConfiguration:[NSImageSymbolConfiguration configurationWithPointSize:size weight:NSFontWeightRegular]];
    if (!image) return nil;
    return [NSImage imageWithSize:image.size flipped:NO drawingHandler:^BOOL(NSRect rect) {
        [image drawInRect:rect]; [color set]; NSRectFillUsingOperation(rect, NSCompositingOperationSourceAtop); return YES;
    }];
}
static NSFont *TitleFont(CGFloat size) {
    return [NSFont fontWithName:@"Songti SC Bold" size:size] ?: [NSFont systemFontOfSize:size weight:NSFontWeightBold];
}
// Rounded translucent panel used by tables, stat cards and settings groups; lets the page background show through.
static void DrawPanel(NSRect rect, CGFloat radius, CGFloat alpha) {
    NSBezierPath *path = [NSBezierPath bezierPathWithRoundedRect:rect xRadius:radius yRadius:radius];
    [[Hex(0xFFFDFA) colorWithAlphaComponent:alpha] setFill]; [path fill];
    [Hex(0xEDE4D8) setStroke]; path.lineWidth = 1; [path stroke];
}
// Native action button with the existing ink-and-paper treatment.
@interface ActionButton : NSButton
@property NSString *symbol;   // Optional leading SF Symbol.
@property CGFloat fontSize;   // 0 keeps the card-button default.
@property BOOL primary;       // Filled brown style regardless of title.
@end
@implementation ActionButton
- (void)drawRect:(NSRect)dirtyRect {
    NSRect rect = NSInsetRect(self.bounds,1,1);
    BOOL pill = [self.title hasPrefix:@"+"], filled = self.primary || pill || [self.title isEqualToString:@"打开"];
    CGFloat radius = pill ? NSHeight(rect)/2 : MIN(12,NSHeight(rect)/3);
    NSBezierPath *shape = [NSBezierPath bezierPathWithRoundedRect:rect xRadius:radius yRadius:radius];
    NSColor *fill = filled ? Hex(0xA8765A) : Hex(0xFFFFFF);
    NSColor *text = filled ? NSColor.whiteColor : [self.title isEqualToString:@"创建"] ? Hex(0x9A6447) : Hex(0x2B2622);
    if (!self.enabled) { fill = Hex(0xEFE9E1); text = Hex(0xA59B90); }
    if (self.highlighted && self.enabled) fill = [fill blendedColorWithFraction:0.12 ofColor:NSColor.blackColor];
    [fill setFill]; [shape fill];
    if (!filled) { [Hex(0xE3D9CD) setStroke]; shape.lineWidth = 1; [shape stroke]; }
    NSDictionary *attributes = @{NSFontAttributeName:[NSFont systemFontOfSize:self.fontSize ?: (pill ? 16 : 17) weight:NSFontWeightMedium],NSForegroundColorAttributeName:text};
    if (self.symbol) {
        NSImage *icon = Tinted(self.symbol, (self.fontSize ?: 14)-1, text);
        NSSize size = [self.title sizeWithAttributes:attributes];
        CGFloat x = NSMidX(self.bounds)-(icon.size.width+8+size.width)/2;
        [icon drawInRect:NSMakeRect(x,NSMidY(self.bounds)-icon.size.height/2,icon.size.width,icon.size.height) fromRect:NSZeroRect operation:NSCompositingOperationSourceOver fraction:1 respectFlipped:YES hints:nil];
        [self.title drawAtPoint:NSMakePoint(x+icon.size.width+8,NSMidY(self.bounds)-size.height/2) withAttributes:attributes];
    } else DrawCentered(self.title, self.bounds, attributes);
    if (self.window.firstResponder == self && NSApp.active) {
        [NSColor.keyboardFocusIndicatorColor setStroke]; shape.lineWidth = 3; [shape stroke];
    }
}
@end

static const unsigned BadgePalette[4][2] = {{0xFCE7D4,0xC0743A},{0xE9E3FA,0x6A55C8},{0xDDF0E6,0x3C8A68},{0xFCE3DD,0xC75F47}};
static const unsigned CardFill = 0xFFFCF7;

// Account rows paint the badge and status; native subviews handle selection and actions.
@interface AccountRow : NSView
@property NSInteger number;
@property NSColor *dot;
@property NSTextField *status, *recent, *nameLabel;
@property NSButton *selection;
@end
@implementation AccountRow
- (BOOL)isFlipped { return YES; }
- (void)drawRect:(NSRect)dirtyRect {
    [Hex(0xEFE7DC) setFill]; NSRectFill(NSMakeRect(0,NSHeight(self.bounds)-1,NSWidth(self.bounds),1));
    const unsigned *palette = BadgePalette[(self.number-1)%4];
    NSRect badge = NSMakeRect(64,12,28,28);
    [Hex(palette[0]) setFill]; [[NSBezierPath bezierPathWithRoundedRect:badge xRadius:8 yRadius:8] fill];
    DrawCentered([NSString stringWithFormat:@"%02ld",(long)self.number], badge, @{NSFontAttributeName:[NSFont systemFontOfSize:12 weight:NSFontWeightSemibold],NSForegroundColorAttributeName:Hex(palette[1])});
    [self.dot setFill]; [[NSBezierPath bezierPathWithOvalInRect:NSMakeRect(478,22,8,8)] fill];

}
@end
@interface AccountCard : NSView
@property NSInteger number;
@property NSImage *art;
@property NSArray<NSString *> *chips;
@property NSColor *dot;
@property NSTextField *status, *nameLabel;
@property ActionButton *button;
@property NSArray<NSTextField *> *usageLabels;
@property NSArray<NSTextField *> *usageTimes;
@property NSTextField *usageSource;
@property NSArray *usageValues;
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
    // Missing or ambiguous interface observations leave empty tracks; never invent a current quota.
    for (NSInteger row = 0; row < 2; row++) {
        NSBezierPath *track = [NSBezierPath bezierPathWithRoundedRect:NSMakeRect(436,44+row*46,150,6) xRadius:3 yRadius:3];
        [Hex(0xEEE8E0) setFill]; [track fill];
        id value = self.usageValues.count > (NSUInteger)row ? self.usageValues[row] : nil;
        if ([value isKindOfClass:NSNumber.class] && [value doubleValue] > 0) {
            [Hex(0xA99A87) setFill];
            [[NSBezierPath bezierPathWithRoundedRect:NSMakeRect(436,44+row*46,150*[value doubleValue]/100,6) xRadius:3 yRadius:3] fill];
        }
    }
}
@end

@interface Launcher : NSObject <NSApplicationDelegate, NSWindowDelegate, NSMenuDelegate, NSSearchFieldDelegate, NSTableViewDataSource, NSTableViewDelegate>
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
@property NSMutableArray<NSDictionary *> *logEntries; // Newest first: time, level, instance, text.
@property NSTableView *logTable;
@property NSSearchField *logSearch, *accountSearch;
@property NSPopUpButton *logInstance, *logLevel, *logPeriod, *accountFilter;
@property NSTextField *logSummary, *settingsFeedback, *toast;
@property NSButton *previousLogs, *nextLogs, *selectAllAccounts, *selectedPathsButton;
@property NSArray *visibleLogs;
@property NSInteger logPage, settingsSection;
@property NSUInteger toastGeneration;
@property NSMutableSet<NSNumber *> *selectedAccounts;
@property NSMutableDictionary *accountNames, *settingsDraft;
@property NSDictionary *settings;
@property NSUserDefaults *preferences;
@property NSView *settingsContent, *mainCanvas;
@property NSArray<NSButton *> *settingsTabs;
@property ActionButton *saveSettingsButton;
@property NSMutableArray<AccountRow *> *rows;
@property NSView *rowsView;
@property NSArray<NSTextField *> *stats;
@property NSTextField *tableFooter;
@property ProjectStore *projectStore;
@property NSView *projectRowsView;
@property NSTextField *projectFooter, *projectFeedback;
@property NSButton *addProjectButton, *importProjectsButton, *registerProjectsButton;
@property BOOL discoveringProjects, registeringProjects;
@property NSArray<NSDictionary *> *snapshotProjects;
@property BOOL snapshotMode;
@property BOOL updating;
@property BOOL updateCancelled;
@property NSURLSessionTask *updateTask;
@property NSWindow *updateWindow;
@property NSArray<NSTextField *> *updateSteps;
@property NSProgressIndicator *updateBar;
@property NSTextField *updateDetail;
@property NSButton *updateCancel;
@property NSInteger updateStage;
@property BOOL headless;         // --update-test: no dialogs, accept the offered update, print results.
@property BOOL testCancel;       // --update-test --allow-updates --cancel: cancel as soon as download progress arrives.
@property NSStatusItem *statusItem;
@property NSString *lastLogged;
@property NSTextField *accessibilityStatus;
@property NSTextField *message;
@property NSMutableDictionary<NSNumber *, NSDate *> *starting;
@property NSMutableSet<NSNumber *> *pendingLaunches;
@property NSMutableSet<NSNumber *> *checking;
// Only retain this launcher's successful launches; never infer configuration from a directory or read process environments.
@property NSMutableDictionary<NSNumber *, NSRunningApplication *> *configuredApps;
@property NSMutableDictionary<NSNumber *, NSString *> *errors;
@property NSDictionary<NSNumber *, NSArray<NSNumber *> *> *processes;
@property NSString *scanError;
@property NSTimer *timer;
@property int lockFD;
@property NSInteger loginAccount;
@property NSButton *endLoginButton;
@property NSTextField *summaryLabel;
@property NSDictionary<NSNumber *,NSDictionary *> *usageInterface;
@property NSDictionary<NSNumber *,NSDictionary *> *savedUsageInterface;
@property BOOL readingUsageInterface;
@property NSTimeInterval lastUsageRead;
@property NSUInteger usageGeneration;
@property id usageClickMonitor;
@end
@implementation Launcher
- (void)dealloc { if (_usageClickMonitor) [NSEvent removeMonitor:_usageClickMonitor]; }
- (instancetype)init {
    if ((self = [super init])) {
        self.preferences = NSUserDefaults.standardUserDefaults;
        self.settings = ValidatedSettings([self.preferences objectForKey:@"localSettings"]);
        self.settingsDraft = self.settings.mutableCopy;
        self.accountNames = [NSMutableDictionary dictionary];
        id savedNames = [self.preferences objectForKey:@"accountDisplayNames"];
        if ([savedNames isKindOfClass:NSDictionary.class]) for (NSInteger n = 1; n <= 10; n++) {
            NSString *key = @(n).stringValue, *name = ValidatedAccountName(savedNames[key]);
            if (name.length) self.accountNames[key] = name;
        }
        self.selectedAccounts = [NSMutableSet set];
        self.starting = [NSMutableDictionary dictionary]; self.errors = [NSMutableDictionary dictionary];
        self.pendingLaunches = [NSMutableSet set];
        self.checking = [NSMutableSet set]; self.configuredApps = [NSMutableDictionary dictionary];
    }
    return self;
}
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
    NSInteger savedLogin = [NSUserDefaults.standardUserDefaults integerForKey:@"loginProtectionAccount"];
    self.loginAccount = savedLogin >= 1 && savedLogin <= 10 ? savedLogin : 0;
    self.projectStore = [[ProjectStore alloc] initWithDirectory:support home:NSHomeDirectory() applicationPaths:@[NSBundle.mainBundle.bundlePath,@"/Applications/Claude 启动器.app",[NSHomeDirectory() stringByAppendingPathComponent:@"Applications/Claude 启动器.app"]]];
    [self.projectStore load];
    self.page = [self.settings[@"rememberPage"] boolValue] ? [self.preferences integerForKey:@"lastLauncherPage"] : [self.settings[@"startPage"] integerValue];
    [self makeWindow]; [self applySettings];
    if ([self.settings[@"showUsageHistory"] boolValue]) self.savedUsageInterface = LoadedUsageInterface([self.preferences objectForKey:@"usageInterfaceObservations"],NSDate.date);
    [self refresh]; [self show:nil];
    __weak Launcher *weak = self;
    self.usageClickMonitor = [NSEvent addLocalMonitorForEventsMatchingMask:NSEventMaskLeftMouseUp handler:^NSEvent *(NSEvent *event) {
        if (event.window == weak.window) dispatch_async(dispatch_get_main_queue(),^{ [weak readLocalUsage]; });
        return event; // Observe clicks in this launcher only; never consume or synthesize input.
    }];
    if (self.projectStore.notice) [self alert:@"项目清单提示" detail:self.projectStore.notice];
    [self finishPendingUpdate];
    // One check per launch (only when the user enabled updates); no timers or repeated background checks.
    if (NetworkEnabled) dispatch_after(dispatch_time(DISPATCH_TIME_NOW,(int64_t)(3*NSEC_PER_SEC)), dispatch_get_main_queue(), ^{ [self runUpdateCheck:NO]; });
}
- (void)alert:(NSString *)title detail:(NSString *)detail {
    if (self.headless) { printf("[result] %s：%s\n",title.UTF8String,detail.UTF8String); exit(3); }
    NSAlert *alert = [NSAlert new]; alert.messageText = title; alert.informativeText = detail;
    [alert addButtonWithTitle:@"知道了"]; [alert runModal];
}
- (AccountCard *)cardFor:(NSInteger)n frame:(NSRect)frame art:(NSImage *)art {
    NSColor *ink = Hex(0x2B2622), *muted = Hex(0x6E655C);
    AccountCard *card = [[AccountCard alloc] initWithFrame:frame];
    card.number = n; card.art = art; card.dot = Hex(0xB9B1A7);
    card.chips = @[(n == 1 ? @"默认目录" : [ProfilePath(n).lastPathComponent stringByReplacingOccurrencesOfString:@"Claude-" withString:@""]),CodeStatus(n,NO,NO)];
    card.nameLabel = Text([self accountName:n], NSMakeRect(84,18,270,24), [NSFont systemFontOfSize:18 weight:NSFontWeightSemibold], ink); [card addSubview:card.nameLabel];
    card.status = Text(@"读取状态…", NSMakeRect(102,44,160,20), [NSFont systemFontOfSize:13], muted); [card addSubview:card.status];
    NSArray *rows = @[@[@"clock",@"5小时限额"],@[@"calendar",@"周限额"]];
    NSMutableArray *usageLabels = [NSMutableArray array], *usageTimes = [NSMutableArray array];
    for (NSInteger row = 0; row < 2; row++) {
        CGFloat y = 18+row*46;
        [card addSubview:Symbol(rows[row][0], NSMakeRect(406,y+1,18,18), muted)];
        [card addSubview:Text(rows[row][1], NSMakeRect(436,y,90,20), [NSFont systemFontOfSize:14 weight:NSFontWeightMedium], ink)];
        NSTextField *pending = Text(@"—", NSMakeRect(588,y+19,48,16), [NSFont systemFontOfSize:12], Hex(0xA59B90)); pending.alignment = NSTextAlignmentRight;
        [card addSubview:pending];
        [usageLabels addObject:pending];
        NSTextField *time = Text(@"尚未观察到",NSMakeRect(525,y+2,111,16),[NSFont systemFontOfSize:10],muted); time.alignment = NSTextAlignmentRight;
        [card addSubview:time]; [usageTimes addObject:time];
    }
    card.usageLabels = usageLabels; card.usageTimes = usageTimes;
    card.usageSource = Text(@"界面观察 · 待读取",NSMakeRect(436,100,200,16),[NSFont systemFontOfSize:11],muted);
    [card addSubview:card.usageSource];
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
    NSButton *hit = [NSButton buttonWithTitle:title target:self action:@selector(navigate:)];
    hit.frame = item.bounds; hit.tag = index; hit.bordered = NO; hit.title = @""; hit.focusRingType = NSFocusRingTypeExterior; hit.accessibilityLabel = title; [item addSubview:hit];
    item.accessibilityElement = NO;
    return item;
}
- (Canvas *)pageTitled:(NSString *)title subtitle:(NSString *)subtitle frame:(NSRect)frame {
    Canvas *page = [[Canvas alloc] initWithFrame:frame];
    [page addSubview:Text(title, NSMakeRect(36,52,420,46), TitleFont(32), Hex(0x2B2622))];
    [page addSubview:Text(subtitle, NSMakeRect(37,104,600,20), [NSFont systemFontOfSize:13], Hex(0x6E655C))];
    return page;
}
- (ActionButton *)toolButton:(NSString *)title symbol:(NSString *)symbol frame:(NSRect)frame action:(SEL)action {
    ActionButton *button = [ActionButton buttonWithTitle:title target:self action:action];
    button.frame = frame; button.bordered = NO; button.symbol = symbol; button.fontSize = 14;
    return button;
}
- (NSView *)accountsPage:(NSRect)frame {
    NSColor *ink = Hex(0x2B2622), *muted = Hex(0x6E655C);
    Canvas *page = [self pageTitled:@"账号管理" subtitle:@"统一管理本机 Claude 账号空间，查看对应实例、运行状态与最近使用记录。" frame:frame];
    page.paint = ^(NSRect bounds) {
        for (NSInteger i = 0; i < 3; i++) DrawPanel(NSMakeRect(36+i*248,140,240,84), 16, 0.86);
        DrawPanel(NSMakeRect(36,292,736,404), 16, 0.86);
        [Hex(0xEFE7DC) setFill]; NSRectFill(NSMakeRect(37,336,734,1)); NSRectFill(NSMakeRect(37,655,734,1));
        NSDictionary *head = @{NSFontAttributeName:[NSFont systemFontOfSize:13 weight:NSFontWeightSemibold],NSForegroundColorAttributeName:Hex(0x7A6F64)};
        NSArray *columns = @[@[@"账号名称",@100],@[@"登录身份",@250],@[@"对应实例",@416],@[@"状态",@478],@[@"最近使用",@566],@[@"操作",@660]];
        for (NSArray *column in columns) [column[0] drawAtPoint:NSMakePoint(36+[column[1] doubleValue],305) withAttributes:head];
    };
    ActionButton *add = [ActionButton buttonWithTitle:@"+  添加账号" target:self action:@selector(newInstance:)];
    add.frame = NSMakeRect(612,56,160,44); add.bordered = NO; [page addSubview:add];
    [page addSubview:[self toolButton:@"刷新状态" symbol:@"arrow.clockwise" frame:NSMakeRect(660,240,112,38) action:@selector(refresh)]];
    NSArray *statInfo = @[@[@"账号总数",@"本机已有账号空间",@0x2B2622],@[@"运行中",@"主进程正在运行",@0x3C8A68],@[@"需要处理",@"启动失败或检查未通过",@0xC7704A]];
    NSMutableArray *stats = [NSMutableArray array];
    for (NSInteger i = 0; i < 3; i++) {
        CGFloat x = 36+i*248;
        [page addSubview:Text(statInfo[i][0], NSMakeRect(x+20,154,200,18), [NSFont systemFontOfSize:12], muted)];
        NSTextField *value = Text(@"00", NSMakeRect(x+18,176,60,38), [NSFont systemFontOfSize:30 weight:NSFontWeightMedium], Hex([statInfo[i][2] unsignedIntValue]));
        [page addSubview:value]; [stats addObject:value];
        [page addSubview:Text(statInfo[i][1], NSMakeRect(x+80,190,150,16), [NSFont systemFontOfSize:11], muted)];
    }
    self.stats = stats;
    self.accountSearch = [[NSSearchField alloc] initWithFrame:NSMakeRect(36,242,268,32)]; self.accountSearch.placeholderString = @"搜索账号名称或目录…"; self.accountSearch.accessibilityLabel = @"搜索账号"; self.accountSearch.delegate = self; [page addSubview:self.accountSearch];
    self.accountFilter = [self filter:@[@"全部状态",@"运行中",@"未运行",@"处理中",@"异常"] frame:NSMakeRect(316,242,130,32) label:@"账号状态" action:@selector(accountFilterChanged:)]; [page addSubview:self.accountFilter];
    self.selectedPathsButton = [self toolButton:@"复制选中路径" symbol:nil frame:NSMakeRect(470,240,176,38) action:@selector(copySelectedAccounts:)]; [page addSubview:self.selectedPathsButton];
    self.selectAllAccounts = [NSButton checkboxWithTitle:@"" target:self action:@selector(selectVisibleAccounts:)]; self.selectAllAccounts.frame = NSMakeRect(58,302,24,24); self.selectAllAccounts.accessibilityLabel = @"全选当前筛选账号"; self.selectAllAccounts.allowsMixedState = YES; [page addSubview:self.selectAllAccounts];
    NSScrollView *scroll = [[NSScrollView alloc] initWithFrame:NSMakeRect(37,337,734,318)];
    scroll.drawsBackground = NO; scroll.contentView.drawsBackground = NO; scroll.hasVerticalScroller = YES; scroll.autohidesScrollers = YES; scroll.scrollerStyle = NSScrollerStyleOverlay;
    Canvas *rowsView = [[Canvas alloc] initWithFrame:NSMakeRect(0,0,734,10*52)];
    self.rows = [NSMutableArray array];
    for (NSInteger n = 1; n <= 10; n++) {
        AccountRow *row = [[AccountRow alloc] initWithFrame:NSMakeRect(0,(n-1)*52,734,52)];
        row.number = n; row.dot = Hex(0xB9B1A7);
        row.nameLabel = Text([self accountName:n], NSMakeRect(100,16,140,20), [NSFont systemFontOfSize:14 weight:NSFontWeightMedium], ink); [row addSubview:row.nameLabel];
        row.selection = [NSButton checkboxWithTitle:@"" target:self action:@selector(selectAccountRow:)]; row.selection.frame = NSMakeRect(22,14,24,24); row.selection.tag = n; row.selection.accessibilityLabel = [NSString stringWithFormat:@"选择账号 %02ld",(long)n]; [row addSubview:row.selection];
        NSButton *edit = [NSButton buttonWithTitle:@"" image:[NSImage imageWithSystemSymbolName:@"pencil" accessibilityDescription:@"编辑本地别名"] target:self action:@selector(editAccountName:)]; edit.frame = NSMakeRect(652,11,30,30); edit.tag = n; edit.bezelStyle = NSBezelStyleRounded; [row addSubview:edit];
        NSTextField *email = Text(@"不读取", NSMakeRect(250,17,150,18), [NSFont systemFontOfSize:13], Hex(0xA59B90));
        email.toolTip = @"启动器不读取登录凭据或账号邮箱。"; [row addSubview:email];
        [row addSubview:Text([NSString stringWithFormat:@"Claude %ld",(long)n], NSMakeRect(416,17,90,18), [NSFont systemFontOfSize:13], muted)];
        row.status = Text(@"未运行", NSMakeRect(492,16,80,20), [NSFont systemFontOfSize:13 weight:NSFontWeightSemibold], muted); [row addSubview:row.status];
        row.recent = Text(@"—", NSMakeRect(566,17,80,18), [NSFont systemFontOfSize:13], muted); [row addSubview:row.recent];
        NSButton *more = [NSButton buttonWithTitle:@"" image:[NSImage imageWithSystemSymbolName:@"ellipsis" accessibilityDescription:@"更多"] target:self action:@selector(showMore:)];
        more.frame = NSMakeRect(690,11,30,30); more.bordered = NO; more.tag = n; more.contentTintColor = Hex(0xA59B90);
        [row addSubview:more];
        [rowsView addSubview:row]; [self.rows addObject:row];
    }
    scroll.documentView = rowsView; [page addSubview:scroll]; self.rowsView = rowsView;
    self.tableFooter = Text(@"", NSMakeRect(60,667,300,18), [NSFont systemFontOfSize:12], muted); [page addSubview:self.tableFooter];
    NSTextField *note = Text(@"登录信息由各实例独立管理", NSMakeRect(500,667,248,18), [NSFont systemFontOfSize:12], muted);
    note.alignment = NSTextAlignmentRight; [page addSubview:note];
    return page;
}
- (NSArray<NSDictionary *> *)projectRecords { return self.snapshotProjects ?: self.projectStore.projects ?: @[]; }
- (void)projectNotice:(NSString *)text {
    self.projectFeedback.stringValue = text; self.projectFeedback.toolTip = text;
}
- (NSView *)projectsPage:(NSRect)frame {
    Canvas *page = [self pageTitled:@"项目管理" subtitle:@"所有账号共用一份本机项目入口；各自的登录与会话保持独立。" frame:frame];
    page.paint = ^(NSRect bounds) {
        DrawPanel(NSMakeRect(36,140,736,72),14,0.86);
        DrawPanel(NSMakeRect(36,274,736,366),16,0.86);
        [Hex(0xEFE7DC) setFill]; NSRectFill(NSMakeRect(37,318,734,1)); NSRectFill(NSMakeRect(37,600,734,1));
        NSDictionary *head = @{NSFontAttributeName:[NSFont systemFontOfSize:12 weight:NSFontWeightSemibold],NSForegroundColorAttributeName:Hex(0x7A6F64)};
        for (NSArray *column in @[@[@"名称",@18],@[@"分组",@140],@[@"路径",@224],@[@"状态",@442],@[@"上次账号",@508],@[@"操作",@610]])
            [column[0] drawAtPoint:NSMakePoint(36+[column[1] doubleValue],289) withAttributes:head];
    };
    [page addSubview:Text(@"共享目录中的代码、CLAUDE.md、项目设置与 MCP 配置会互相影响。",NSMakeRect(54,154,694,20),[NSFont systemFontOfSize:13 weight:NSFontWeightMedium],Hex(0x795539))];
    [page addSubview:Text(@"同一项目请轮流使用；启动器无法检测同时编辑，不复制、同步或合并文件。",NSMakeRect(54,182,694,18),[NSFont systemFontOfSize:12],Hex(0x6E655C))];
    self.addProjectButton = [self toolButton:@"添加目录" symbol:@"folder.badge.plus" frame:NSMakeRect(640,56,132,44) action:@selector(addProject:)];
    [page addSubview:self.addProjectButton];
    self.importProjectsButton = [self toolButton:@"识别并导入" symbol:@"square.and.arrow.down" frame:NSMakeRect(484,56,146,44) action:@selector(importProjects:)];
    [page addSubview:self.importProjectsButton];
    [page addSubview:Text(@"本机目录入口 · 移除不删除文件",NSMakeRect(38,238,340,20),[NSFont systemFontOfSize:13],Hex(0x6E655C))];
    self.registerProjectsButton = [self toolButton:@"登记到账号…" symbol:@"sidebar.left" frame:NSMakeRect(492,228,156,38) action:@selector(registerProjects:)];
    [page addSubview:self.registerProjectsButton];
    [page addSubview:[self toolButton:@"刷新状态" symbol:@"arrow.clockwise" frame:NSMakeRect(660,228,112,38) action:@selector(refreshProjects:)]];
    NSScrollView *scroll = [[NSScrollView alloc] initWithFrame:NSMakeRect(37,319,734,281)];
    scroll.drawsBackground = NO; scroll.contentView.drawsBackground = NO;
    scroll.hasVerticalScroller = YES; scroll.autohidesScrollers = YES; scroll.scrollerStyle = NSScrollerStyleOverlay;
    self.projectRowsView = [[Canvas alloc] initWithFrame:NSMakeRect(0,0,734,281)]; scroll.documentView = self.projectRowsView;
    [page addSubview:scroll];
    self.projectFooter = Text(@"",NSMakeRect(54,612,690,18),[NSFont systemFontOfSize:12],Hex(0x6E655C)); [page addSubview:self.projectFooter];
    self.projectFeedback = Text(@"选择账号后会复制路径并显示 Finder，请在 Claude 中选择此文件夹。",NSMakeRect(38,656,734,42),[NSFont systemFontOfSize:12],Hex(0x795539));
    self.projectFeedback.maximumNumberOfLines = 2; [page addSubview:self.projectFeedback];
    [self refreshProjects:nil];
    if (self.projectStore.notice) [self projectNotice:self.projectStore.notice];
    return page;
}
- (void)refreshProjects:(id)sender {
    for (NSView *view in self.projectRowsView.subviews.copy) [view removeFromSuperview];
    NSArray *projects = [self projectRecords];
    self.addProjectButton.enabled = self.snapshotMode || self.projectStore.writable;
    self.importProjectsButton.enabled = !self.discoveringProjects && (self.snapshotMode || self.projectStore.writable);
    self.registerProjectsButton.enabled = !self.registeringProjects && !self.discoveringProjects && projects.count > 0;
    [self.projectRowsView setFrameSize:NSMakeSize(734,MAX(281,projects.count*64))];
    if (!projects.count) {
        [self.projectRowsView addSubview:Text(@"还没有项目",NSMakeRect(270,86,200,28),[NSFont systemFontOfSize:20 weight:NSFontWeightMedium],Hex(0x6E655C))];
        [self.projectRowsView addSubview:Text(@"添加一个已有文件夹，所有账号都可从这里找到它。",NSMakeRect(156,124,440,22),[NSFont systemFontOfSize:13],Hex(0x8A7F74))];
    }
    [projects enumerateObjectsUsingBlock:^(NSDictionary *project, NSUInteger index, BOOL *stop) {
        Canvas *row = [[Canvas alloc] initWithFrame:NSMakeRect(0,index*64,734,64)];
        row.paint = ^(NSRect bounds) { [Hex(0xEFE7DC) setFill]; NSRectFill(NSMakeRect(0,63,734,1)); };
        NSString *path = project[@"path"];
        NSString *status = self.snapshotProjects ? project[@"previewStatus"] : [self.projectStore statusForPath:path];
        NSInteger last = [project[@"lastAccount"] integerValue];
        NSArray *values = @[project[@"name"], [project[@"group"] length] ? project[@"group"] : @"未分组",path,status,last ? [NSString stringWithFormat:@"账号 %02ld",(long)last] : @"未使用"];
        CGFloat xs[] = {18,140,224,442,508}, widths[] = {112,74,204,58,88};
        for (NSUInteger i = 0; i < values.count; i++) {
            NSColor *color = i == 3 ? ([status isEqual:@"正常"] ? Hex(0x3C8A68) : Hex(0xB46A45)) : Hex(0x6E655C);
            NSTextField *field = Text(values[i],NSMakeRect(xs[i],22,widths[i],20),[NSFont systemFontOfSize:i == 0 ? 13 : 12],color);
            field.lineBreakMode = i == 2 ? NSLineBreakByTruncatingMiddle : NSLineBreakByTruncatingTail;
            field.toolTip = values[i]; field.accessibilityLabel = values[i]; [row addSubview:field];
        }
        ActionButton *openProject = [self toolButton:@"打开" symbol:nil frame:NSMakeRect(608,16,62,32) action:@selector(openProject:)];
        openProject.fontSize = 12; openProject.tag = (NSInteger)index;
        openProject.enabled = (self.snapshotMode || self.projectStore.writable) && [status isEqual:@"正常"];
        openProject.accessibilityLabel = [@"选择账号打开项目：" stringByAppendingString:project[@"name"]];
        [row addSubview:openProject];
        NSButton *more = [NSButton buttonWithTitle:@"" image:[NSImage imageWithSystemSymbolName:@"ellipsis" accessibilityDescription:@"项目操作"] target:self action:@selector(projectMenu:)];
        more.frame = NSMakeRect(678,16,32,32); more.bezelStyle = NSBezelStyleRounded; more.tag = (NSInteger)index;
        more.enabled = self.snapshotMode || self.projectStore != nil; more.accessibilityLabel = [@"更多项目操作：" stringByAppendingString:project[@"name"]]; [row addSubview:more];
        [self.projectRowsView addSubview:row];
    }];
    self.projectFooter.stringValue = [NSString stringWithFormat:@"共 %lu 个项目 · 上次账号仅用于默认选择，不绑定项目",(unsigned long)projects.count];
}
- (NSDictionary *)projectForSender:(id)sender {
    NSInteger index = [sender tag]; NSArray *projects = [self projectRecords];
    return index >= 0 && index < (NSInteger)projects.count ? projects[index] : nil;
}
- (BOOL)saveProjects:(NSArray *)projects {
    NSString *failure = nil;
    if (![self.projectStore save:projects error:&failure]) { [self alert:@"项目清单未保存" detail:failure]; return NO; }
    [self refreshProjects:nil]; return YES;
}
- (NSDictionary *)editProjectRecord:(NSDictionary *)project {
    NSAlert *editor = [NSAlert new]; editor.messageText = @"项目名称与分组";
    editor.informativeText = @"只修改清单中的显示信息，不改目录名称或位置。名称和分组最多 200 字。";
    [editor addButtonWithTitle:@"保存"]; [editor addButtonWithTitle:@"取消"];
    Canvas *form = [[Canvas alloc] initWithFrame:NSMakeRect(0,0,360,96)];
    [form addSubview:Text(@"名称",NSMakeRect(0,8,52,24),[NSFont systemFontOfSize:13],Hex(0x6E655C))];
    [form addSubview:Text(@"分组",NSMakeRect(0,54,52,24),[NSFont systemFontOfSize:13],Hex(0x6E655C))];
    NSTextField *name = [[NSTextField alloc] initWithFrame:NSMakeRect(56,4,300,28)]; name.stringValue = project[@"name"]; name.accessibilityLabel = @"项目名称";
    NSTextField *group = [[NSTextField alloc] initWithFrame:NSMakeRect(56,50,300,28)]; group.stringValue = project[@"group"]; group.placeholderString = @"可留空"; group.accessibilityLabel = @"项目分组";
    [form addSubview:name]; [form addSubview:group]; editor.accessoryView = form; editor.window.initialFirstResponder = name;
    if ([editor runModal] != NSAlertFirstButtonReturn) return nil;
    NSString *title = [name.stringValue stringByTrimmingCharactersInSet:NSCharacterSet.whitespaceAndNewlineCharacterSet];
    if (!title.length || title.length > 200 || group.stringValue.length > 200) { [self alert:@"名称或分组无效" detail:@"名称不能为空；名称和分组最多 200 字。请重新编辑。"] ; return nil; }
    NSMutableDictionary *edited = project.mutableCopy; edited[@"name"] = title; edited[@"group"] = [group.stringValue stringByTrimmingCharactersInSet:NSCharacterSet.whitespaceAndNewlineCharacterSet]; return edited;
}
- (NSAlert *)projectImportAlert:(NSDictionary *)result checks:(NSMutableArray<NSButton *> *)checks {
    NSAlert *alert = [NSAlert new]; alert.messageText = @"确认要加入公共清单的目录";
    alert.informativeText = @"识别范围：全部已创建账号（含未运行账号）的本机历史记录。\n只添加目录入口，不同步会话或文件，也不修改 Claude 的项目列表。\n历史识别可能不完整；记录可能来自远程会话，请核对本机目录是否正确。";
    [alert addButtonWithTitle:@"导入所选"]; [alert addButtonWithTitle:@"取消"];
    NSArray *candidates = result[@"candidates"], *summaries = result[@"summaries"];
    NSScrollView *scroll = [[NSScrollView alloc] initWithFrame:NSMakeRect(0,0,660,360)];
    scroll.hasVerticalScroller = YES; scroll.autohidesScrollers = YES; scroll.borderType = NSBezelBorder;
    Canvas *content = [[Canvas alloc] initWithFrame:NSMakeRect(0,0,640,MAX(360,summaries.count*24+48+candidates.count*58))];
    CGFloat y = 10;
    for (NSString *summary in summaries) {
        NSTextField *line = Text(summary,NSMakeRect(10,y,620,20),[NSFont systemFontOfSize:11],Hex(0x6E655C));
        line.toolTip = summary; [content addSubview:line]; y += 24;
    }
    [content addSubview:Text([NSString stringWithFormat:@"识别到 %lu 个目录候选 · 已在清单中的项目不重复添加",(unsigned long)candidates.count],NSMakeRect(10,y+2,620,22),[NSFont systemFontOfSize:12 weight:NSFontWeightMedium],Hex(0x795539))]; y += 34;
    for (NSDictionary *candidate in candidates) {
        NSString *path = candidate[@"path"]; BOOL exists = [self.projectStore containsDirectory:path];
        NSMutableArray *accounts = [NSMutableArray array];
        for (NSNumber *account in candidate[@"accounts"]) [accounts addObject:[NSString stringWithFormat:@"%02ld",(long)account.integerValue]];
        NSButton *check = [NSButton checkboxWithTitle:[NSString stringWithFormat:@"%@  ·  来源账号 %@%@",path.lastPathComponent,[accounts componentsJoinedByString:@"、"],exists ? @" · 已在清单" : @""] target:nil action:nil];
        check.frame = NSMakeRect(10,y,620,24); check.state = exists ? NSControlStateValueOff : NSControlStateValueOn;
        check.enabled = !exists; check.toolTip = path; check.accessibilityLabel = [@"导入目录：" stringByAppendingString:path];
        [checks addObject:check]; [content addSubview:check];
        NSTextField *label = Text(path,NSMakeRect(30,y+26,600,20),[NSFont systemFontOfSize:12],Hex(0x6E655C));
        label.lineBreakMode = NSLineBreakByTruncatingMiddle; label.toolTip = path; label.selectable = YES; [content addSubview:label]; y += 58;
    }
    scroll.documentView = content; alert.accessoryView = scroll;
    alert.buttons.firstObject.enabled = candidates.count > 0;
    return alert;
}
- (void)importProjects:(id)sender {
    if (!self.projectStore.writable || self.discoveringProjects) return;
    self.discoveringProjects = YES; self.importProjectsButton.title = @"正在识别…"; [self refreshProjects:nil];
    [self projectNotice:@"正在只读检查所有已创建账号的目录记录，包含未运行账号；完成后可选择要导入的目录。"];
    NSMutableArray *profiles = [NSMutableArray array];
    for (NSInteger n = 1; n <= 10; n++) [profiles addObject:ProfilePath(n)];
    // A separate immutable validator avoids reading the UI's mutable store from the worker queue.
    ProjectStore *validator = [[ProjectStore alloc] initWithDirectory:[NSHomeDirectory() stringByAppendingPathComponent:@"Library/Application Support/Claude Launcher"] home:NSHomeDirectory() applicationPaths:@[NSBundle.mainBundle.bundlePath,@"/Applications/Claude 启动器.app",[NSHomeDirectory() stringByAppendingPathComponent:@"Applications/Claude 启动器.app"]]];
    dispatch_async(dispatch_get_global_queue(QOS_CLASS_USER_INITIATED,0), ^{
        NSDictionary *result = DiscoverProjects(profiles,validator,NSHomeDirectory());
        dispatch_async(dispatch_get_main_queue(), ^{
            NSMutableArray<NSButton *> *checks = [NSMutableArray array];
            NSAlert *preview = [self projectImportAlert:result checks:checks];
            NSModalResponse response = [preview runModal];
            self.discoveringProjects = NO; self.importProjectsButton.title = @"识别并导入"; [self refreshProjects:nil];
            if (response != NSAlertFirstButtonReturn) { [self projectNotice:@"已取消导入，清单和客户端数据未改动。"] ; return; }
            NSMutableArray *selected = [NSMutableArray array]; NSArray *candidates = result[@"candidates"];
            for (NSUInteger i = 0; i < checks.count; i++) if (checks[i].enabled && checks[i].state == NSControlStateValueOn) [selected addObject:candidates[i][@"path"]];
            NSUInteger skipped = 0; NSArray *rows = PrepareProjectImport(self.projectStore,selected,NSHomeDirectory(),&skipped);
            NSUInteger added = rows.count-self.projectStore.projects.count;
            if (rows.count > 1000) { [self alert:@"项目清单容量不足" detail:@"最多保存 1000 项，本次没有写入。请减少选择或移除不需要的记录。"] ; return; }
            if (added && ![self saveProjects:rows]) return;
            [self projectNotice:[NSString stringWithFormat:@"已导入 %lu 个目录；跳过 %lu 个重复或状态已变化的目录。仅更新公共清单，客户端数据未改动。",(unsigned long)added,(unsigned long)skipped]];
        });
    });
}
- (void)registerProjects:(id)sender {
    if (self.snapshotMode || self.registeringProjects || self.discoveringProjects) return;
    NSArray *projects = [self projectRecords].copy; if (!projects.count) return;
    NSAlert *choice = [NSAlert new]; choice.messageText = @"登记目录到 Claude 侧栏";
    choice.informativeText = @"目标账号必须已正常退出。只新建目录路径和独立 ID 的空归档记录，不复制聊天、登录或权限配置；已有目录会跳过。\n完成后启动目标账号，在 Claude 的“分组和排序 → 显示”开启“空分组”。这些空记录需保留，且可能在归档列表中可见。";
    [choice addButtonWithTitle:@"登记所选目录"]; [choice addButtonWithTitle:@"取消"];
    Canvas *form = [[Canvas alloc] initWithFrame:NSMakeRect(0,0,620,340)];
    NSPopUpButton *accounts = [[NSPopUpButton alloc] initWithFrame:NSMakeRect(0,0,620,30) pullsDown:NO]; accounts.accessibilityLabel = @"登记目标账号";
    [self refresh]; NSInteger preferred = -1;
    for (NSInteger n = 1; n <= 10; n++) {
        BOOL directory = NO;
        if (![NSFileManager.defaultManager fileExistsAtPath:ProfilePath(n) isDirectory:&directory] || !directory) continue;
        [accounts addItemWithTitle:[NSString stringWithFormat:@"账号 %02ld · %@",(long)n,self.processes[@(n)].count ? @"运行中，请先正常退出" : @"未运行"]];
        accounts.lastItem.tag = n;
        if (preferred < 0 && !self.processes[@(n)].count) preferred = accounts.numberOfItems-1;
    }
    if (preferred >= 0) [accounts selectItemAtIndex:preferred]; [form addSubview:accounts];
    NSScrollView *scroll = [[NSScrollView alloc] initWithFrame:NSMakeRect(0,42,620,298)]; scroll.hasVerticalScroller = YES; scroll.borderType = NSBezelBorder;
    Canvas *rows = [[Canvas alloc] initWithFrame:NSMakeRect(0,0,598,MAX(298,projects.count*52))];
    NSMutableArray<NSButton *> *checks = [NSMutableArray array];
    for (NSUInteger i = 0; i < projects.count; i++) {
        NSDictionary *project = projects[i]; NSString *path = project[@"path"];
        NSButton *check = [NSButton checkboxWithTitle:project[@"name"] target:nil action:nil];
        check.frame = NSMakeRect(10,i*52+4,578,24); check.toolTip = path; check.accessibilityLabel = [@"登记目录：" stringByAppendingString:path];
        check.enabled = [[self.projectStore statusForPath:path] isEqual:@"正常"] && !ProjectNeedsManualImport(path,NSHomeDirectory());
        check.state = check.enabled ? NSControlStateValueOn : NSControlStateValueOff; [checks addObject:check]; [rows addSubview:check];
        NSTextField *label = Text(path,NSMakeRect(30,i*52+28,558,20),[NSFont systemFontOfSize:11],Hex(0x6E655C));
        label.lineBreakMode = NSLineBreakByTruncatingMiddle; label.toolTip = path; [rows addSubview:label];
    }
    scroll.documentView = rows; [form addSubview:scroll]; choice.accessoryView = form;
    choice.buttons.firstObject.enabled = accounts.numberOfItems > 0;
    if ([choice runModal] != NSAlertFirstButtonReturn) return;
    NSInteger account = accounts.selectedItem.tag; NSNumber *key = @(account);
    if (self.loginAccount || self.starting[key] || [self.checking containsObject:key]) { [self alert:@"账号正在处理中" detail:@"请先结束登录保护或等待检查／启动完成。"] ; return; }
    NSMutableArray *paths = [NSMutableArray array];
    for (NSUInteger i = 0; i < checks.count; i++) if (checks[i].enabled && checks[i].state == NSControlStateValueOn) [paths addObject:projects[i][@"path"]];
    if (!paths.count) { [self projectNotice:@"未选择可登记的目录，没有写入。"] ; return; }
    self.registeringProjects = YES; [self.checking addObject:key]; [self refresh]; [self refreshProjects:nil];
    [self projectNotice:@"正在校验目标账号并登记目录；请勿从 Dock 或终端启动目标账号。"];
    dispatch_async(dispatch_get_global_queue(QOS_CLASS_USER_INITIATED,0), ^{
        NSDictionary *result = RegisterForAccount(account,paths,YES);
        dispatch_async(dispatch_get_main_queue(), ^{
            self.registeringProjects = NO; [self.checking removeObject:key]; [self refresh]; [self refreshProjects:nil];
            NSString *summary = [NSString stringWithFormat:@"账号 %02ld：已登记 %@ 个目录，跳过 %@ 个已有目录。%@",(long)account,result[@"added"],result[@"skipped"],result[@"error"] ?: @"请启动该账号，并在 Claude 中开启“显示空分组”。"];
            [self projectNotice:summary]; [self alert:result[@"error"] ? @"登记已停止" : @"目录登记完成" detail:summary];
        });
    });
}
- (void)addProject:(id)sender {
    if (!self.projectStore.writable) return;
    NSOpenPanel *panel = [NSOpenPanel openPanel]; panel.title = @"添加已有项目目录";
    panel.canChooseDirectories = YES; panel.canChooseFiles = NO; panel.allowsMultipleSelection = NO; panel.canCreateDirectories = NO; panel.prompt = @"选择目录";
    if ([panel runModal] != NSModalResponseOK) return;
    NSString *failure = nil, *path = [self.projectStore normalizeDirectory:panel.URL.path error:&failure];
    if (!path) { [self alert:@"无法添加项目" detail:failure]; return; }
    if ([self.projectStore containsDirectory:path]) { [self alert:@"项目已在清单中" detail:@"同一个真实目录只需添加一次，所有账号都可以使用。"] ; return; }
    if ([path isEqual:@"/"] || [path isEqual:NSHomeDirectory().stringByResolvingSymlinksInPath]) {
        NSAlert *confirm = [NSAlert new]; confirm.messageText = @"添加整个主目录或根目录？";
        confirm.informativeText = @"这不是普通项目目录，范围很大。启动器只记录路径；之后交给 Claude 时，请自行确认文件访问范围。";
        [confirm addButtonWithTitle:@"取消"]; [confirm addButtonWithTitle:@"仍然添加"];
        if ([confirm runModal] != NSAlertSecondButtonReturn) return;
    }
    NSDictionary *record = [self editProjectRecord:@{@"id":NSUUID.UUID.UUIDString,@"name":path.lastPathComponent,@"group":@"",@"path":path,@"lastAccount":@0}];
    if (!record) return;
    if (![[self.projectStore statusForPath:path] isEqual:@"正常"]) { [self alert:@"目录状态已变化" detail:@"未添加，请重新选择已有目录。"] ; return; }
    if ([self saveProjects:[self.projectStore.projects arrayByAddingObject:record]]) [self projectNotice:@"已添加项目入口，没有复制或读取目录中的文件。"];
}
- (void)projectMenu:(NSButton *)sender {
    if (![self projectForSender:sender]) return;
    NSMenu *menu = [NSMenu new]; menu.autoenablesItems = NO;
    NSArray *titles = @[@"修改名称与分组…",@"上移",@"下移",@"在 Finder 中显示",@"复制路径",@"从清单移除…"];
    NSArray *actions = @[NSStringFromSelector(@selector(editProject:)),NSStringFromSelector(@selector(moveProjectUp:)),NSStringFromSelector(@selector(moveProjectDown:)),NSStringFromSelector(@selector(revealProject:)),NSStringFromSelector(@selector(copyProject:)),NSStringFromSelector(@selector(removeProject:))];
    for (NSUInteger i = 0; i < titles.count; i++) {
        if (i == 3 || i == 5) [menu addItem:NSMenuItem.separatorItem];
        NSMenuItem *item = [menu addItemWithTitle:titles[i] action:NSSelectorFromString(actions[i]) keyEquivalent:@""]; item.target = self; item.tag = sender.tag;
        item.enabled = i == 3 || i == 4 || self.projectStore.writable;
        if (i == 1 && sender.tag == 0) item.enabled = NO;
        if (i == 2 && sender.tag+1 == (NSInteger)self.projectStore.projects.count) item.enabled = NO;
    }
    [menu popUpMenuPositioningItem:nil atLocation:NSMakePoint(0,NSHeight(sender.bounds)) inView:sender];
}
- (void)editProject:(id)sender {
    NSDictionary *record = [self projectForSender:sender]; if (!record) return;
    NSDictionary *edited = [self editProjectRecord:record]; if (!edited) return;
    NSMutableArray *projects = self.projectStore.projects.mutableCopy;
    NSUInteger index = [projects indexOfObjectPassingTest:^BOOL(NSDictionary *row, NSUInteger i, BOOL *stop) { return [row[@"id"] isEqual:record[@"id"]]; }];
    if (index == NSNotFound) return;
    // An account check may have finished while the editor's modal loop was open. Keep its lastAccount.
    NSMutableDictionary *latest = [projects[index] mutableCopy]; latest[@"name"] = edited[@"name"]; latest[@"group"] = edited[@"group"]; projects[index] = latest;
    if ([self saveProjects:projects]) [self projectNotice:@"已修改显示信息，实际目录未改动。"];
}
- (void)moveProject:(id)sender offset:(NSInteger)offset {
    NSInteger index = [sender tag], target = index+offset;
    NSMutableArray *projects = self.projectStore.projects.mutableCopy;
    if (index < 0 || target < 0 || index >= (NSInteger)projects.count || target >= (NSInteger)projects.count) return;
    [projects exchangeObjectAtIndex:index withObjectAtIndex:target]; [self saveProjects:projects];
}
- (void)moveProjectUp:(id)sender { [self moveProject:sender offset:-1]; }
- (void)moveProjectDown:(id)sender { [self moveProject:sender offset:1]; }
- (void)copyProject:(id)sender {
    NSDictionary *record = [self projectForSender:sender]; if (!record) return;
    [NSPasteboard.generalPasteboard clearContents]; [NSPasteboard.generalPasteboard setString:record[@"path"] forType:NSPasteboardTypeString];
    [self projectNotice:@"项目路径已复制。"];
}
- (void)revealProject:(id)sender {
    NSDictionary *record = [self projectForSender:sender]; if (!record) return;
    if (![[self.projectStore statusForPath:record[@"path"]] isEqual:@"正常"]) { [self refreshProjects:nil]; [self alert:@"无法显示目录" detail:@"目录不存在、已改变或无权限；未创建或替换路径。"] ; return; }
    [NSWorkspace.sharedWorkspace activateFileViewerSelectingURLs:@[[NSURL fileURLWithPath:record[@"path"]]]];
}
- (void)removeProject:(id)sender {
    NSDictionary *record = [self projectForSender:sender]; if (!record) return;
    NSAlert *confirm = [NSAlert new]; confirm.messageText = @"从项目清单移除？";
    confirm.informativeText = [NSString stringWithFormat:@"只移除“%@”的入口，不删除或移动任何文件。",record[@"name"]];
    [confirm addButtonWithTitle:@"取消"]; [confirm addButtonWithTitle:@"移除记录"];
    if ([confirm runModal] != NSAlertSecondButtonReturn) return;
    NSMutableArray *projects = self.projectStore.projects.mutableCopy;
    NSIndexSet *removed = [projects indexesOfObjectsPassingTest:^BOOL(NSDictionary *row, NSUInteger i, BOOL *stop) { return [row[@"id"] isEqual:record[@"id"]]; }];
    [projects removeObjectsAtIndexes:removed];
    if ([self saveProjects:projects]) [self projectNotice:@"记录已移除，项目目录与文件保持原样。"];
}
- (void)openProject:(id)sender {
    NSDictionary *record = [self projectForSender:sender]; if (!record || !self.projectStore.writable) return;
    if (![[self.projectStore statusForPath:record[@"path"]] isEqual:@"正常"]) { [self refreshProjects:nil]; [self alert:@"项目目录不可用" detail:@"请检查目录是否存在、位置是否改变以及访问权限。"] ; return; }
    NSAlert *choice = [NSAlert new]; choice.messageText = @"选择打开项目的账号";
    choice.informativeText = @"通过账号检查后，路径会复制到剪贴板，并在 Finder 中显示。请在 Claude 中选择此文件夹；不会自动加载或同步会话。";
    NSPopUpButton *accounts = [[NSPopUpButton alloc] initWithFrame:NSMakeRect(0,0,320,30) pullsDown:NO];
    [self refresh];
    for (NSInteger n = 1; n <= 10; n++) [accounts addItemWithTitle:[NSString stringWithFormat:@"账号 %02ld · %@",(long)n,self.buttons[n-1].title]];
    NSInteger last = [record[@"lastAccount"] integerValue]; [accounts selectItemAtIndex:last ? last-1 : 0]; accounts.accessibilityLabel = @"目标账号";
    choice.accessoryView = accounts; [choice addButtonWithTitle:@"用此账号打开"]; [choice addButtonWithTitle:@"取消"];
    if ([choice runModal] != NSAlertFirstButtonReturn) return;
    NSInteger account = accounts.indexOfSelectedItem+1;
    if (self.starting[@(account)] || [self.checking containsObject:@(account)]) { [self alert:@"账号正在处理中" detail:@"请等待本次检查或启动完成，再打开项目。"] ; return; }
    NSString *identifier = record[@"id"];
    [self selectAccount:self.buttons[account-1] prepare:^BOOL{
        NSMutableArray *projects = self.projectStore.projects.mutableCopy;
        NSUInteger index = [projects indexOfObjectPassingTest:^BOOL(NSDictionary *row, NSUInteger i, BOOL *stop) { return [row[@"id"] isEqual:identifier]; }];
        if (index == NSNotFound || ![[self.projectStore statusForPath:projects[index][@"path"]] isEqual:@"正常"]) {
            [self alert:@"项目状态已变化" detail:@"记录已移除或目录不可访问，未交接项目。"] ; [self refreshProjects:nil]; return NO;
        }
        NSMutableDictionary *updated = [projects[index] mutableCopy]; updated[@"lastAccount"] = @(account); projects[index] = updated;
        if (![self saveProjects:projects]) return NO;
        [NSPasteboard.generalPasteboard clearContents]; [NSPasteboard.generalPasteboard setString:updated[@"path"] forType:NSPasteboardTypeString];
        [NSWorkspace.sharedWorkspace activateFileViewerSelectingURLs:@[[NSURL fileURLWithPath:updated[@"path"]]]];
        [self projectNotice:[NSString stringWithFormat:@"已为账号 %02ld 复制路径并显示 Finder。请在 Claude 中选择此文件夹。",(long)account]];
        return YES;
    }];
}
- (NSPopUpButton *)filter:(NSArray *)titles frame:(NSRect)frame label:(NSString *)label action:(SEL)action {
    NSPopUpButton *button = [[NSPopUpButton alloc] initWithFrame:frame pullsDown:NO]; [button addItemsWithTitles:titles];
    button.target = self; button.action = action; button.accessibilityLabel = label; return button;
}
- (NSView *)logsPage:(NSRect)frame {
    NSView *page = [self pageTitled:@"日志记录" subtitle:@"仅记录启动器本次运行；搜索、筛选后可导出当前结果。" frame:frame];
    [page addSubview:[self toolButton:@"导出结果…" symbol:@"square.and.arrow.down" frame:NSMakeRect(502,56,128,44) action:@selector(exportLogs:)]];
    [page addSubview:[self toolButton:@"清空日志…" symbol:@"trash" frame:NSMakeRect(640,56,132,44) action:@selector(clearLogs:)]];
    self.logPeriod = [self filter:@[@"本次运行",@"最近 5 分钟",@"最近 1 小时"] frame:NSMakeRect(36,142,150,32) label:@"日志时间范围" action:@selector(logFilterChanged:)]; [page addSubview:self.logPeriod];
    NSMutableArray *instances = [@[@"全部实例",@"系统"] mutableCopy]; for (NSInteger n=1;n<=10;n++) [instances addObject:[NSString stringWithFormat:@"Claude %ld",(long)n]];
    self.logInstance = [self filter:instances frame:NSMakeRect(196,142,136,32) label:@"日志实例" action:@selector(logFilterChanged:)]; [page addSubview:self.logInstance];
    self.logLevel = [self filter:@[@"全部级别",@"信息",@"警告",@"错误"] frame:NSMakeRect(342,142,126,32) label:@"日志级别" action:@selector(logFilterChanged:)]; [page addSubview:self.logLevel];
    self.logSearch = [[NSSearchField alloc] initWithFrame:NSMakeRect(482,142,290,32)]; self.logSearch.placeholderString = @"搜索日志内容…"; self.logSearch.accessibilityLabel = @"搜索日志"; self.logSearch.delegate = self; [page addSubview:self.logSearch];
    NSScrollView *scroll = [[NSScrollView alloc] initWithFrame:NSMakeRect(36,196,736,434)]; scroll.hasVerticalScroller = YES; scroll.hasHorizontalScroller = NO; scroll.borderType = NSBezelBorder;
    self.logTable = [[NSTableView alloc] initWithFrame:scroll.bounds]; self.logTable.rowHeight = 36; self.logTable.usesAlternatingRowBackgroundColors = YES;
    self.logTable.dataSource = self; self.logTable.delegate = self;
    NSArray *columns = @[@[@"time",@"时间",@158],@[@"level",@"级别",@64],@[@"instance",@"实例",@88],@[@"text",@"事件内容",@398]];
    for (NSArray *column in columns) { NSTableColumn *c = [[NSTableColumn alloc] initWithIdentifier:column[0]]; c.title = column[1]; c.width = [column[2] doubleValue]; [self.logTable addTableColumn:c]; }
    scroll.documentView = self.logTable; [page addSubview:scroll];
    self.previousLogs = [self toolButton:@"上一页" symbol:nil frame:NSMakeRect(36,650,90,34) action:@selector(previousLogPage:)]; [page addSubview:self.previousLogs];
    self.nextLogs = [self toolButton:@"下一页" symbol:nil frame:NSMakeRect(136,650,90,34) action:@selector(nextLogPage:)]; [page addSubview:self.nextLogs];
    self.logSummary = Text(@"暂无日志",NSMakeRect(246,656,526,24),[NSFont systemFontOfSize:13],Hex(0x6E655C)); [page addSubview:self.logSummary];
    [self reloadLogs]; return page;
}
- (NSArray *)filteredLogEntries {
    NSInteger period = self.logPeriod.indexOfSelectedItem;
    NSDate *since = period > 0 ? [NSDate dateWithTimeIntervalSinceNow:period == 1 ? -300 : -3600] : nil;
    return FilteredLogs(self.logEntries,self.logSearch.stringValue,self.logInstance.indexOfSelectedItem > 0 ? self.logInstance.titleOfSelectedItem : nil,self.logLevel.indexOfSelectedItem > 0 ? self.logLevel.titleOfSelectedItem : nil,since);
}
- (void)trimLogs {
    NSUInteger limit = [self.settings[@"logLimit"] unsignedIntegerValue];
    if (self.logEntries.count > limit) [self.logEntries removeObjectsInRange:NSMakeRange(limit,self.logEntries.count-limit)];
}
- (void)reloadLogs {
    NSArray *filtered = [self filteredLogEntries]; NSInteger pages = MAX(1,(filtered.count+9)/10);
    self.logPage = MAX(0,MIN(self.logPage,pages-1)); NSUInteger start = self.logPage*10;
    self.visibleLogs = [filtered subarrayWithRange:NSMakeRange(start,MIN(10,filtered.count-start))]; [self.logTable reloadData];
    self.previousLogs.enabled = self.logPage > 0; self.nextLogs.enabled = self.logPage+1 < pages;
    self.logSummary.stringValue = filtered.count ? [NSString stringWithFormat:@"第 %ld / %ld 页 · 匹配 %lu 条 / 共 %lu 条",(long)self.logPage+1,(long)pages,(unsigned long)filtered.count,(unsigned long)self.logEntries.count] : @"没有匹配的日志，可清除搜索或切换筛选条件";
}
- (NSInteger)numberOfRowsInTableView:(NSTableView *)tableView { return self.visibleLogs.count; }
- (NSView *)tableView:(NSTableView *)tableView viewForTableColumn:(NSTableColumn *)column row:(NSInteger)row {
    NSString *text = self.visibleLogs[row][column.identifier];
    NSTextField *field = Text(text,NSMakeRect(0,0,column.width,24),[NSFont systemFontOfSize:12],Hex(0x3A332D)); field.selectable = YES; field.toolTip = text; return field;
}
- (void)logFilterChanged:(id)sender { self.logPage = 0; [self reloadLogs]; }
- (void)previousLogPage:(id)sender { self.logPage--; [self reloadLogs]; }
- (void)nextLogPage:(id)sender { self.logPage++; [self reloadLogs]; }
- (void)controlTextDidChange:(NSNotification *)note {
    if (note.object == self.logSearch) [self logFilterChanged:nil];
    if (note.object == self.accountSearch) [self accountFilterChanged:nil];
}
- (NSView *)settingsPage:(NSRect)frame {
    Canvas *page = [self pageTitled:@"系统设置" subtitle:@"只管理本地启动器。更改不会修改 Claude 登录、会话或账号配置。" frame:frame];
    page.paint = ^(NSRect bounds) {
        DrawPanel(NSMakeRect(36,140,736,556),16,0.9);
        [Hex(0xEDE4D8) setFill]; NSRectFill(NSMakeRect(206,154,1,524));
    };
    NSArray *titles = @[@"常规设置",@"启动行为",@"外观设置",@"数据与存储",@"消息通知",@"关于软件"];
    NSMutableArray *tabs = [NSMutableArray array];
    for (NSInteger i=0;i<(NSInteger)titles.count;i++) {
        ActionButton *tab = [self toolButton:titles[i] symbol:nil frame:NSMakeRect(48,156+i*48,146,38) action:@selector(settingsTabChanged:)];
        tab.tag = i; [page addSubview:tab]; [tabs addObject:tab];
    }
    self.settingsTabs = tabs;
    self.settingsContent = [[Canvas alloc] initWithFrame:NSMakeRect(230,158,518,448)]; [page addSubview:self.settingsContent];
    self.settingsFeedback = Text(@"更改后点击保存设置",NSMakeRect(230,609,518,22),[NSFont systemFontOfSize:12],Hex(0x6E655C)); [page addSubview:self.settingsFeedback];
    [page addSubview:[self toolButton:@"恢复默认…" symbol:nil frame:NSMakeRect(424,646,104,34) action:@selector(resetSettings:)]];
    [page addSubview:[self toolButton:@"撤销更改" symbol:nil frame:NSMakeRect(536,646,104,34) action:@selector(discardSettings:)]];
    self.saveSettingsButton = [self toolButton:@"保存设置" symbol:nil frame:NSMakeRect(648,646,108,34) action:@selector(saveSettings:)];
    self.saveSettingsButton.primary = YES; [page addSubview:self.saveSettingsButton];
    [self renderSettings]; return page;
}
- (void)settingsTabChanged:(NSButton *)sender { self.settingsSection = sender.tag; [self renderSettings]; }
- (void)settingsText:(NSString *)text y:(CGFloat)y height:(CGFloat)height bold:(BOOL)bold {
    NSTextField *label = Text(text,NSMakeRect(0,y,518,height),[NSFont systemFontOfSize:bold ? 17 : 13 weight:bold ? NSFontWeightSemibold : NSFontWeightRegular],Hex(bold ? 0x2B2622 : 0x6E655C));
    label.maximumNumberOfLines = 0; label.lineBreakMode = NSLineBreakByWordWrapping; [self.settingsContent addSubview:label];
}
- (void)settingToggle:(NSString *)key title:(NSString *)title detail:(NSString *)detail y:(CGFloat)y {
    NSButton *check = [NSButton checkboxWithTitle:title target:self action:@selector(settingChanged:)];
    check.identifier = key; check.state = [self.settingsDraft[key] boolValue] ? NSControlStateValueOn : NSControlStateValueOff;
    check.frame = NSMakeRect(0,y,518,28); check.font = [NSFont systemFontOfSize:14]; [self.settingsContent addSubview:check];
    [self settingsText:detail y:y+30 height:38 bold:NO];
}
- (void)settingChoice:(NSString *)key title:(NSString *)title choices:(NSArray *)titles values:(NSArray *)values y:(CGFloat)y {
    [self settingsText:title y:y+5 height:24 bold:NO];
    NSPopUpButton *popup = [[NSPopUpButton alloc] initWithFrame:NSMakeRect(270,y,248,30) pullsDown:NO];
    for (NSUInteger i=0;i<titles.count;i++) { [popup addItemWithTitle:titles[i]]; popup.lastItem.representedObject = values[i]; }
    NSUInteger index = [values indexOfObject:self.settingsDraft[key]]; [popup selectItemAtIndex:index == NSNotFound ? 0 : index];
    popup.enabled = ![key isEqual:@"startPage"] || ![self.settingsDraft[@"rememberPage"] boolValue];
    popup.identifier = key; popup.accessibilityLabel = title; popup.target = self; popup.action = @selector(settingChanged:); [self.settingsContent addSubview:popup];
}
- (void)settingsAction:(NSString *)title action:(SEL)action y:(CGFloat)y {
    [self.settingsContent addSubview:[self toolButton:title symbol:nil frame:NSMakeRect(0,y,250,34) action:action]];
}
- (void)renderSettings {
    for (NSView *view in self.settingsContent.subviews.copy) [view removeFromSuperview];
    self.accessibilityStatus = nil;
    for (ActionButton *tab in self.settingsTabs) { tab.primary = tab.tag == self.settingsSection; tab.needsDisplay = YES; }
    switch (self.settingsSection) {
        case 0:
            [self settingsText:@"常规设置" y:0 height:28 bold:YES];
            [self settingToggle:@"closeToBackground" title:@"关闭窗口后继续运行" detail:@"关闭窗口后仍可从 Dock 重新打开；退出启动器不退出 Claude。" y:48];
            [self settingToggle:@"showMenuBar" title:@"显示菜单栏图标" detail:@"从 macOS 菜单栏查看账号状态、显示启动器窗口。" y:134];
            [self settingsText:@"辅助功能权限" y:232 height:26 bold:YES];
            self.accessibilityStatus = Text(AXIsProcessTrusted() ? @"已授权：可恢复窗口、只读界面用量。" : @"未授权：仍可激活账号，无法恢复窗口或读取界面用量。",NSMakeRect(0,266,518,24),[NSFont systemFontOfSize:12],Hex(0x6E655C));
            [self.settingsContent addSubview:self.accessibilityStatus];
            [self settingsAction:@"打开辅助功能设置" action:@selector(openAccessibility:) y:304];
            [self settingsText:@"权限由你在系统设置中管理，启动器不会自动授权。" y:352 height:48 bold:NO]; break;
        case 1:
            [self settingsText:@"启动行为" y:0 height:28 bold:YES];
            [self settingsAction:@"管理登录启动…" action:@selector(manageLoginItem:) y:42];
            [self settingsText:[self loginItemDescription] y:84 height:42 bold:NO];
            [self settingToggle:@"rememberPage" title:@"恢复启动器上次页面" detail:@"只恢复本管理器的页面，不自动启动或激活任何 Claude。" y:136];
            [self settingChoice:@"startPage" title:@"未恢复时的启动页面" choices:@[@"实例管理",@"账号管理",@"项目管理",@"日志记录",@"系统设置"] values:@[@0,@1,@2,@3,@4] y:218];
            [self settingChoice:@"refreshInterval" title:@"本地进程状态刷新间隔" choices:@[@"1 秒",@"3 秒",@"5 秒"] values:@[@1,@3,@5] y:270];
            [self settingToggle:@"restoreMinimized" title:@"点击账号时恢复最小化窗口" detail:@"只处理经过核对的目标 PID；需辅助功能权限。" y:324]; break;
        case 2:
            [self settingsText:@"外观设置" y:0 height:28 bold:YES];
            [self settingToggle:@"showArtwork" title:@"显示水墨插画" detail:@"关闭后保留浅色纸张背景和原有文字布局。" y:48];
            [self settingToggle:@"showUsageHistory" title:@"显示本地界面用量" detail:@"只读 Claude 已有控件；不点击、不查询，不读历史文件。" y:144];
            [self settingsText:@"界面语言：简体中文\n主题：浅色水墨\n\n这两项为当前版本的固定样式。Claude 自身的外观、浏览器和登录方式由官方客户端管理。" y:252 height:152 bold:NO]; break;
        case 3:
            [self settingsText:@"数据与存储" y:0 height:28 bold:YES];
            [self settingsText:@"项目清单与运行锁\n~/Library/Application Support/Claude Launcher\n\n偏好与账号别名保存在本机用户偏好中。账号目录固定，不迁移、不合并。" y:42 height:108 bold:NO];
            [self settingsAction:@"在 Finder 中显示管理器数据" action:@selector(revealSupport:) y:164];
            [self settingsAction:@"复制管理器数据路径" action:@selector(copySupportPath:) y:208];
            [self settingsAction:@"导出已保存的设置…" action:@selector(exportSettings:) y:252];
            [self settingChoice:@"logLimit" title:@"本次运行日志保留上限" choices:@[@"100 条",@"500 条",@"1000 条"] values:@[@100,@500,@1000] y:304];
            [self settingsText:@"日志只留在内存；退出即清空，导出由你选择位置。恢复默认只重置启动器偏好，保留项目、别名和登录保护。" y:352 height:76 bold:NO]; break;
        case 4:
            [self settingsText:@"消息通知" y:0 height:28 bold:YES];
            [self settingToggle:@"localNotifications" title:@"显示窗口内提示" detail:@"在启动器中显示操作警告；不推送、不上传、不读取 Claude 消息。" y:48];
            [self settingToggle:@"notificationSound" title:@"警告时播放提示音" detail:@"仅在窗口内提示开启时生效，使用系统提示音。" y:144];
            [self settingsAction:@"测试已保存的通知设置" action:@selector(testNotification:) y:254];
            [self settingsText:@"安全确认和错误弹窗始终保留，不受通知开关影响。" y:308 height:66 bold:NO]; break;
        default:
            [self settingsText:[NSString stringWithFormat:@"Claude 启动器 %@",[NSBundle.mainBundle objectForInfoDictionaryKey:@"CFBundleShortVersionString"] ?: @"开发版"] y:0 height:28 bold:YES];
            [self settingsText:@"非官方本地管理工具，与 Anthropic 无关联。\n不向 Claude / Anthropic 发请求，不读取凭据，不修改官方应用。" y:42 height:70 bold:NO];
            [self settingToggle:@"allowUpdates" title:@"允许检查 GitHub 更新" detail:@"默认关闭。开启后每次打开启动器检查一次，也可手动检查；仅访问本项目 GitHub 下载地址。" y:124];
            [self settingsAction:@"检查启动器更新…" action:@selector(checkForUpdates:) y:214];
            [self settingsAction:@"查看本地使用帮助" action:@selector(localHelp:) y:258];
            [self settingsAction:@"复制基础诊断信息" action:@selector(copyDiagnostics:) y:302];
            [self settingsText:@"不会自动联网检查。诊断仅包含版本、系统版本和权限状态，不包含账号、路径、日志或凭据。" y:354 height:70 bold:NO]; break;
    }
    self.saveSettingsButton.enabled = ![self.settingsDraft isEqual:self.settings];
}
- (void)settingChanged:(NSControl *)sender {
    id value = [sender isKindOfClass:NSPopUpButton.class] ? [(NSPopUpButton *)sender selectedItem].representedObject : @([(NSButton *)sender state] == NSControlStateValueOn);
    self.settingsDraft[sender.identifier] = value;
    if ([sender.identifier isEqual:@"rememberPage"]) for (NSView *view in self.settingsContent.subviews) if ([view.identifier isEqual:@"startPage"]) [(NSControl *)view setEnabled:![value boolValue]];
    self.saveSettingsButton.enabled = ![self.settingsDraft isEqual:self.settings];
    self.settingsFeedback.stringValue = self.saveSettingsButton.enabled ? @"有未保存的更改" : @"与已保存设置一致";
}
- (void)discardSettings:(id)sender {
    self.settingsDraft = self.settings.mutableCopy; [self renderSettings]; self.settingsFeedback.stringValue = @"已撤销未保存更改";
}
- (void)saveSettings:(id)sender {
    NSDictionary *next = ValidatedSettings(self.settingsDraft);
    if (self.updating && [next[@"allowUpdates"] boolValue] != [self.settings[@"allowUpdates"] boolValue]) {
        [self alert:@"更新正在进行" detail:@"请等待当前更新结束后再改变联网选项。其余更改仍保留在草稿中。"]; return;
    }
    NSDictionary *previous = [self.preferences objectForKey:@"localSettings"];
    [self.preferences setObject:next forKey:@"localSettings"];
    if (![self.preferences synchronize]) {
        if (previous) [self.preferences setObject:previous forKey:@"localSettings"]; else [self.preferences removeObjectForKey:@"localSettings"];
        [self alert:@"保存设置失败" detail:@"本机偏好未能写入，运行设置保持原样，请稍后重试。"]; return;
    }
    self.settings = next; self.settingsDraft = next.mutableCopy; [self applySettings]; [self renderSettings];
    self.settingsFeedback.stringValue = @"设置已保存并生效，仅作用于本地启动器"; [self addLog:@"本地设置已保存"];
}
- (void)resetSettings:(id)sender {
    NSAlert *alert = [NSAlert new]; alert.messageText = @"恢复启动器默认设置？";
    alert.informativeText = @"将默认值填入草稿，点击保存后生效。不会移除账号、项目、别名、登录保护或系统登录项。";
    [alert addButtonWithTitle:@"取消"]; [alert addButtonWithTitle:@"恢复到草稿"];
    if ([alert runModal] != NSAlertSecondButtonReturn) return;
    self.settingsDraft = DefaultSettings().mutableCopy; [self renderSettings]; self.settingsFeedback.stringValue = @"默认值已填入，请保存或撤销";
}
- (void)applySettings {
    if (!self.snapshotMode) {
        NetworkEnabled = [self.settings[@"allowUpdates"] boolValue];
        [self.timer invalidate];
        self.timer = [NSTimer scheduledTimerWithTimeInterval:[self.settings[@"refreshInterval"] doubleValue] target:self selector:@selector(refresh) userInfo:nil repeats:YES];
        if ([self.settings[@"showMenuBar"] boolValue] && !self.statusItem) [self makeStatusItem];
        if (![self.settings[@"showMenuBar"] boolValue] && self.statusItem) { [NSStatusBar.systemStatusBar removeStatusItem:self.statusItem]; self.statusItem = nil; }
    }
    for (AccountCard *card in self.cards) { card.art = [self.settings[@"showArtwork"] boolValue] ? Art([NSString stringWithFormat:@"card-%ld",(long)(card.number-1)%4+1]) : nil; card.needsDisplay = YES; }
    self.sidebar.needsDisplay = YES; self.mainCanvas.needsDisplay = YES;
    if (![self.settings[@"localNotifications"] boolValue]) self.toast.hidden = YES;
    self.usageGeneration++; self.usageInterface = nil;
    if (![self.settings[@"showUsageHistory"] boolValue]) { self.savedUsageInterface = nil; if (!self.snapshotMode) [self.preferences removeObjectForKey:@"usageInterfaceObservations"]; }
    [self updateUsageCards]; [self trimLogs]; [self reloadLogs];
}
- (NSString *)loginItemDescription {
    if (@available(macOS 13.0, *)) {
        switch (SMAppService.mainAppService.status) {
            case SMAppServiceStatusEnabled: return @"登录启动已启用，只打开启动器，不启动 Claude。";
            case SMAppServiceStatusRequiresApproval: return @"等待系统批准，请到登录项设置中允许启动器。";
            case SMAppServiceStatusNotFound: return @"系统未找到此应用的登录项，请使用已安装版本。";
            default: return @"登录启动未启用。此项由 macOS 单独保存，立即生效。";
        }
    }
    return @"macOS 12 请在系统「用户与群组 → 登录项」中手动管理启动器。";
}
- (void)manageLoginItem:(id)sender {
    if (@available(macOS 13.0, *)) {
        if (SMAppService.mainAppService.status == SMAppServiceStatusRequiresApproval) { [SMAppService openSystemSettingsLoginItems]; return; }
        NSString *path = NSBundle.mainBundle.bundlePath;
        if (![path isEqual:@"/Applications/Claude 启动器.app"] && ![path isEqual:[NSHomeDirectory() stringByAppendingPathComponent:@"Applications/Claude 启动器.app"]]) {
            [self alert:@"请使用已安装的启动器" detail:@"登录启动只为应用程序文件夹中的正式副本启用，避免登录时运行临时开发版本。请安装后再设置。"]; return;
        }
        BOOL enabled = SMAppService.mainAppService.status == SMAppServiceStatusEnabled;
        NSAlert *alert = [NSAlert new]; alert.messageText = enabled ? @"关闭启动器登录启动？" : @"开启启动器登录启动？";
        alert.informativeText = @"只更改本启动器在 macOS 中的登录项，立即生效。不自动启动任何 Claude 账号。";
        [alert addButtonWithTitle:@"取消"]; [alert addButtonWithTitle:enabled ? @"关闭登录启动" : @"开启登录启动"];
        if ([alert runModal] != NSAlertSecondButtonReturn) return;
        NSError *error = nil; BOOL ok = enabled ? [SMAppService.mainAppService unregisterAndReturnError:&error] : [SMAppService.mainAppService registerAndReturnError:&error];
        if (!ok) [self alert:@"登录启动设置失败" detail:error.localizedDescription ?: @"请到 macOS 登录项中核对授权状态。"];
        [self renderSettings]; self.settingsFeedback.stringValue = [self loginItemDescription];
    } else [NSWorkspace.sharedWorkspace openURL:[NSURL URLWithString:@"x-apple.systempreferences:com.apple.preferences.users"]];
}
- (void)copySupportPath:(id)sender {
    [NSPasteboard.generalPasteboard clearContents]; [NSPasteboard.generalPasteboard setString:[NSHomeDirectory() stringByAppendingPathComponent:@"Library/Application Support/Claude Launcher"] forType:NSPasteboardTypeString];
    self.settingsFeedback.stringValue = @"管理器数据路径已复制";
}
- (void)exportSettings:(id)sender {
    NSSavePanel *panel = NSSavePanel.savePanel; panel.nameFieldStringValue = @"启动器设置.json";
    if ([panel runModal] != NSModalResponseOK) return;
    NSError *error = nil; NSData *data = [NSJSONSerialization dataWithJSONObject:@{@"version":@1,@"settings":self.settings} options:NSJSONWritingPrettyPrinted|NSJSONWritingSortedKeys error:&error];
    if (!data || ![data writeToURL:panel.URL options:NSDataWritingAtomic error:&error]) [self alert:@"导出失败" detail:error.localizedDescription ?: @"无法写入目标文件。"];
    else self.settingsFeedback.stringValue = @"已导出保存的设置，不含账号或凭据";
}
- (void)localHelp:(id)sender {
    [self alert:@"本地使用帮助" detail:@"实例管理：手动启动或打开指定账号；更多菜单可复制路径及管理登录保护。\n账号管理：搜索、筛选、设置本地别名，勾选后复制路径。\n项目管理：维护公共目录入口；所有写入均需明确操作。\n日志记录：筛选、搜索、翻页、导出当前结果；仅保留本次运行。\n系统设置：编辑后保存，恢复默认不会处理账号数据。\n\n启动器不查询 Claude 服务、不读取凭据，不控制官方客户端自身正常联网。"];
}
- (void)copyDiagnostics:(id)sender {
    NSString *text = [NSString stringWithFormat:@"Claude Launcher %@ (%@)\nmacOS %@\nAccessibility: %@\nNetwork: manual GitHub updates %@\nClaude/Anthropic requests: prohibited",[NSBundle.mainBundle objectForInfoDictionaryKey:@"CFBundleShortVersionString"],[NSBundle.mainBundle objectForInfoDictionaryKey:@"CFBundleVersion"],NSProcessInfo.processInfo.operatingSystemVersionString,AXIsProcessTrusted() ? @"granted" : @"not granted",[self.settings[@"allowUpdates"] boolValue] ? @"enabled" : @"disabled"];
    [NSPasteboard.generalPasteboard clearContents]; [NSPasteboard.generalPasteboard setString:text forType:NSPasteboardTypeString]; self.settingsFeedback.stringValue = @"基础诊断已复制，不含账号、路径或日志";
}
- (void)showLocalNotice:(NSString *)text {
    if (![self.settings[@"localNotifications"] boolValue]) return;
    NSUInteger generation = ++self.toastGeneration;
    self.toast.stringValue = text; self.toast.toolTip = text; self.toast.hidden = NO;
    if ([self.settings[@"notificationSound"] boolValue] && !self.snapshotMode) NSBeep();
    dispatch_after(dispatch_time(DISPATCH_TIME_NOW,5*NSEC_PER_SEC),dispatch_get_main_queue(),^{ if (generation == self.toastGeneration) self.toast.hidden = YES; });
}
- (void)testNotification:(id)sender {
    [self showLocalNotice:@"本地通知测试：设置已生效，没有发送网络请求。"];
    self.settingsFeedback.stringValue = [self.settings[@"localNotifications"] boolValue] ? @"已按保存的设置显示提示" : @"窗口内提示已关闭；启用并保存后可测试";
}
- (void)addLog:(NSString *)text {
    if (self.headless) printf("[log] %s\n",text.UTF8String);
    if (!self.logEntries) self.logEntries = [NSMutableArray array];
    NSDateFormatter *clock = [NSDateFormatter new]; clock.dateFormat = @"yyyy-MM-dd HH:mm:ss";
    NSString *level = @"信息";
    for (NSString *word in @[@"失败",@"错误",@"未通过",@"无法"]) if ([text containsString:word]) level = @"错误";
    if ([level isEqual:@"信息"]) for (NSString *word in @[@"超时",@"保护",@"未确认",@"已变化",@"多个"]) if ([text containsString:word]) level = @"警告";
    NSString *instance = @"系统";
    NSRange match = [text rangeOfString:@"账号 [0-9]{2}" options:NSRegularExpressionSearch];
    if (match.location != NSNotFound) instance = [NSString stringWithFormat:@"Claude %ld",(long)[text substringWithRange:NSMakeRange(match.location+3,2)].integerValue];
    [self.logEntries insertObject:@{@"date":NSDate.date,@"time":[clock stringFromDate:[NSDate date]],@"level":level,@"instance":instance,@"text":text} atIndex:0];
    [self trimLogs]; [self reloadLogs];
    if (![level isEqual:@"信息"]) [self showLocalNotice:text];
}
- (void)clearLogs:(id)sender {
    NSAlert *alert = [NSAlert new]; alert.messageText = @"清空本次运行日志？"; alert.informativeText = @"仅清空启动器内存中的记录，不读取或删除 Claude 日志。";
    [alert addButtonWithTitle:@"取消"]; [alert addButtonWithTitle:@"清空"];
    if ([alert runModal] != NSAlertSecondButtonReturn) return;
    [self.logEntries removeAllObjects]; self.logPage = 0; [self reloadLogs];
}
- (void)exportLogs:(id)sender {
    NSSavePanel *panel = [NSSavePanel savePanel]; panel.nameFieldStringValue = @"Claude 启动器日志.txt";
    if ([panel runModal] != NSModalResponseOK) return;
    NSMutableString *out = [NSMutableString string];
    for (NSDictionary *entry in [self filteredLogEntries].reverseObjectEnumerator) [out appendFormat:@"%@\t%@\t%@\t%@\n",entry[@"time"],entry[@"level"],entry[@"instance"],entry[@"text"]];
    NSError *error = nil;
    if (![out writeToURL:panel.URL atomically:YES encoding:NSUTF8StringEncoding error:&error]) [self alert:@"导出失败" detail:error.localizedDescription];
}
// ---- Updates: explicit opt-in, fixed GitHub source, then manual download. ----
- (NSArray<NSDictionary *> *)updateSources {
    return @[@{@"name":@"GitHub",@"base":GitHubDownloads,@"github":@YES}];
}
- (void)checkForUpdates:(id)sender {
    if (![self.settingsDraft isEqual:self.settings]) { [self showPage:4]; [self alert:@"请先处理未保存设置" detail:@"保存或撤销设置后，再检查启动器更新。"]; return; }
    [self runUpdateCheck:YES];
}
- (void)runUpdateCheck:(BOOL)manual {
    if (!NetworkEnabled) {
        if (manual) [self alert:@"联网更新未启用" detail:@"如需检查启动器更新，请在系统设置 → 关于软件中启用 GitHub 更新并保存，然后再次点击检查更新。不会访问 Claude 或 Anthropic。"];
        return;
    }
    if (self.updating) { if (manual) [self alert:@"正在检查更新" detail:@"请稍候。"]; return; }
    self.updating = YES; self.updateCancelled = NO;
    [NSUserDefaults.standardUserDefaults setObject:[NSDate date] forKey:@"lastUpdateCheck"];
    [self checkAllSources:manual];
}
// Progress window (SPEC 更新过程界面): 下载 → 校验 → 安装. Cancel is allowed until installation starts.
- (void)showUpdateProgress:(NSString *)version {
    if (self.headless) return;
    [self closeUpdateProgress];
    NSWindow *window = [[NSWindow alloc] initWithContentRect:NSMakeRect(0,0,460,212) styleMask:NSWindowStyleMaskTitled | NSWindowStyleMaskClosable backing:NSBackingStoreBuffered defer:NO];
    window.title = [NSString stringWithFormat:@"更新到 %@",version]; window.releasedWhenClosed = NO; window.delegate = self;
    Canvas *content = [[Canvas alloc] initWithFrame:NSMakeRect(0,0,460,212)];
    content.paint = ^(NSRect bounds) { [Hex(0xFAF6F0) setFill]; NSRectFill(bounds); };
    window.contentView = content;
    [content addSubview:Text([NSString stringWithFormat:@"正在更新 Claude 启动器到 %@",version], NSMakeRect(24,20,412,24), [NSFont systemFontOfSize:16 weight:NSFontWeightSemibold], Hex(0x2B2622))];
    NSMutableArray *steps = [NSMutableArray array];
    NSArray *names = @[@"① 下载",@"② 校验",@"③ 安装"];
    for (NSUInteger i = 0; i < names.count; i++) {
        NSTextField *step = Text(names[i], NSMakeRect(24+i*140,58,130,20), [NSFont systemFontOfSize:13 weight:NSFontWeightMedium], Hex(0xA59B90));
        [content addSubview:step]; [steps addObject:step];
    }
    self.updateSteps = steps;
    self.updateBar = [[NSProgressIndicator alloc] initWithFrame:NSMakeRect(24,90,412,16)];
    self.updateBar.style = NSProgressIndicatorStyleBar; self.updateBar.minValue = 0; self.updateBar.maxValue = 1;
    [content addSubview:self.updateBar];
    self.updateDetail = Text(@"", NSMakeRect(24,116,412,40), [NSFont systemFontOfSize:12], Hex(0x6E655C));
    self.updateDetail.maximumNumberOfLines = 2; self.updateDetail.lineBreakMode = NSLineBreakByWordWrapping;
    [content addSubview:self.updateDetail];
    self.updateCancel = [NSButton buttonWithTitle:@"取消" target:self action:@selector(cancelUpdate:)];
    self.updateCancel.frame = NSMakeRect(356,164,80,30); [content addSubview:self.updateCancel];
    self.updateWindow = window; [window center];
    if (!self.snapshotMode) { [window makeKeyAndOrderFront:nil]; [NSApp activateIgnoringOtherApps:YES]; }
}
// progress: 0...1 determinate, negative = indeterminate.
- (void)updateStep:(NSInteger)stage detail:(NSString *)detail progress:(double)progress {
    if (self.headless || !self.updateWindow) return;
    self.updateStage = stage;
    for (NSInteger i = 0; i < (NSInteger)self.updateSteps.count; i++) {
        NSString *base = [self.updateSteps[i].stringValue stringByReplacingOccurrencesOfString:@" ✓" withString:@""];
        self.updateSteps[i].stringValue = i < stage ? [base stringByAppendingString:@" ✓"] : base;
        self.updateSteps[i].textColor = i < stage ? Hex(0x3C8A68) : i == stage ? Hex(0x9A6447) : Hex(0xA59B90);
    }
    if (progress < 0) { self.updateBar.indeterminate = YES; [self.updateBar startAnimation:nil]; }
    else { [self.updateBar stopAnimation:nil]; self.updateBar.indeterminate = NO; self.updateBar.doubleValue = MIN(1,progress); }
    self.updateDetail.stringValue = detail;
    self.updateCancel.enabled = stage < 2;
}
- (void)closeUpdateProgress {
    if (!self.updateWindow) return;
    NSWindow *window = self.updateWindow; self.updateWindow = nil;
    window.delegate = nil; [window orderOut:nil];
}
- (void)cancelUpdate:(id)sender {
    if (!self.updating || self.updateStage >= 2) return;
    self.updateCancelled = YES; self.updating = NO;
    [self.updateTask cancel]; self.updateTask = nil;
    [self closeUpdateProgress];
    [self addLog:@"已取消更新，未做任何改动。"];
}
- (BOOL)windowShouldClose:(NSWindow *)sender {
    if (sender != self.updateWindow) return YES;
    if (self.updateStage >= 2) return NO;   // installation already handed off
    [self cancelUpdate:nil]; return NO;
}
- (void)updateFailed:(NSArray<NSString *> *)failures manual:(BOOL)manual {
    self.updating = NO;
    [self closeUpdateProgress];
    NSString *detail = [failures componentsJoinedByString:@"\n"];
    [self addLog:[@"检查更新失败：" stringByAppendingString:[failures componentsJoinedByString:@"；"]]];
    if (self.headless) exit(1);
    if (!manual) return;
    NSAlert *alert = [NSAlert new]; alert.messageText = @"自动更新失败";
    alert.informativeText = [detail stringByAppendingString:@"\n\n可以前往 GitHub 手动下载最新版本。"];
    [alert addButtonWithTitle:@"前往 GitHub 下载"]; [alert addButtonWithTitle:@"关闭"];
    if ([alert runModal] == NSAlertFirstButtonReturn) [NSWorkspace.sharedWorkspace openURL:[NSURL URLWithString:ReleasesPage]];
}
// Ask every source at once and compare: a lagging self-hosted copy must not hide a newer release on GitHub.
- (void)checkAllSources:(BOOL)manual {
    NSArray *sources = [self updateSources];
    NSMutableArray *results = [NSMutableArray array];
    for (NSUInteger i = 0; i < sources.count; i++) [results addObject:NSNull.null];
    dispatch_group_t group = dispatch_group_create();
    for (NSUInteger i = 0; i < sources.count; i++) {
        NSDictionary *source = sources[i];
        dispatch_group_enter(group);
        Fetch([NSURL URLWithString:[source[@"base"] stringByAppendingString:@"latest-macos.json"]], [source[@"github"] boolValue], 16384, 30, nil, ^(NSData *data, NSString *failure) {
            NSString *invalid = failure;
            NSDictionary *manifest = data ? VerifyManifest(data, ReleaseKeys(), &invalid) : nil;
            results[i] = manifest ? @{@"source":source,@"manifest":manifest} : @{@"source":source,@"failure":invalid ?: @"未知错误"};
            dispatch_group_leave(group);
        });
    }
    dispatch_group_notify(group, dispatch_get_main_queue(), ^{ [self chooseUpdate:results manual:manual]; });
}
- (void)chooseUpdate:(NSArray<NSDictionary *> *)results manual:(BOOL)manual {
    NSMutableArray *offers = [NSMutableArray array], *failures = [NSMutableArray array];
    for (NSDictionary *result in results) {
        if (self.headless) printf("[source] %s: %s\n",[result[@"source"][@"name"] UTF8String],result[@"manifest"] ? [result[@"manifest"][@"version"] UTF8String] : [result[@"failure"] UTF8String]);
        if (result[@"manifest"]) [offers addObject:result];
        else [failures addObject:[NSString stringWithFormat:@"%@：%@",result[@"source"][@"name"],result[@"failure"]]];
    }
    if (!offers.count) { [self updateFailed:failures manual:manual]; return; }
    NSArray *best = BestOffers(offers);
    NSDictionary *manifest = best.firstObject[@"manifest"], *source = best.firstObject[@"source"];
    {
        NSString *current = [NSBundle.mainBundle objectForInfoDictionaryKey:@"CFBundleShortVersionString"] ?: @"0";
        if (CompareVersions(manifest[@"version"],current) != NSOrderedDescending) {
            self.updating = NO;
            if (manual) [self alert:@"已是最新版本" detail:[NSString stringWithFormat:@"当前版本 %@。",current]];
            return;
        }
        NSArray *minimum = [manifest[@"minimumOS"] componentsSeparatedByString:@"."];
        NSOperatingSystemVersion need = {[minimum[0] integerValue], minimum.count > 1 ? [minimum[1] integerValue] : 0, minimum.count > 2 ? [minimum[2] integerValue] : 0};
        if (![NSProcessInfo.processInfo isOperatingSystemAtLeastVersion:need]) {
            self.updating = NO;
            if (manual) [self alert:@"新版本需要更高的系统版本" detail:[NSString stringWithFormat:@"%@ 需要 macOS %@ 及以上。",manifest[@"version"],manifest[@"minimumOS"]]];
            return;
        }
        NSString *blocker = InstallBlocker(NSBundle.mainBundle.bundlePath);
        NSAlert *offer = [NSAlert new];
        offer.messageText = [NSString stringWithFormat:@"发现新版本 %@",manifest[@"version"]];
        offer.informativeText = blocker ?: [NSString stringWithFormat:@"当前版本 %@。更新会先校验安装包，再退出启动器、替换旧版并重新打开；Claude 实例和账号数据不受影响。",current];
        [offer addButtonWithTitle:blocker ? @"前往 GitHub 下载" : @"立即更新"]; [offer addButtonWithTitle:@"稍后"];
        if (self.headless) { if (blocker) { printf("[result] %s\n",blocker.UTF8String); exit(4); } printf("[offer] %s from %s, accepted\n",[manifest[@"version"] UTF8String],[source[@"name"] UTF8String]); }
        else if ([offer runModal] != NSAlertFirstButtonReturn) { self.updating = NO; return; }
        if (blocker) { self.updating = NO; [NSWorkspace.sharedWorkspace openURL:[NSURL URLWithString:ReleasesPage]]; return; }
        [self downloadOffer:best index:0 failures:failures];
    }
}
// Download from the sources that offer the chosen version, in source order; a failed download moves to the next one.
- (void)downloadOffer:(NSArray<NSDictionary *> *)offers index:(NSUInteger)index failures:(NSArray<NSString *> *)failures {
    if (self.updateCancelled) return;
    if (index >= offers.count) { [self updateFailed:failures manual:YES]; return; }
    NSDictionary *source = offers[index][@"source"], *manifest = offers[index][@"manifest"];
    [self addLog:[NSString stringWithFormat:@"正在从%@下载 %@",source[@"name"],manifest[@"file"]]];
    if (index == 0) [self showUpdateProgress:manifest[@"version"]];
    double total = [manifest[@"size"] doubleValue];
    [self updateStep:0 detail:[NSString stringWithFormat:@"从 %@ 下载 · 0 / %.1f MB",source[@"name"],total/1048576] progress:0];
    self.updateTask = Fetch([NSURL URLWithString:[source[@"base"] stringByAppendingString:[manifest[@"file"] stringByAddingPercentEncodingWithAllowedCharacters:NSCharacterSet.URLPathAllowedCharacterSet]]], [source[@"github"] boolValue], UpdateSizeLimit, 300, ^(long long received, long long expected) {
        if (self.updateCancelled) return;
        if (self.headless && self.testCancel) {
            [self cancelUpdate:nil]; printf("[result] cancelled at %lld bytes\n",received);
            dispatch_after(dispatch_time(DISPATCH_TIME_NOW,(int64_t)(2*NSEC_PER_SEC)), dispatch_get_main_queue(), ^{ exit(0); }); return;
        }
        double size = expected > 0 ? expected : total;
        [self updateStep:0 detail:[NSString stringWithFormat:@"从 %@ 下载 · %.1f / %.1f MB · %.0f%%",source[@"name"],received/1048576.0,size/1048576,size > 0 ? 100.0*received/size : 0] progress:size > 0 ? received/size : 0];
    }, ^(NSData *package, NSString *downloadFailure) {
        self.updateTask = nil;
        if (self.updateCancelled) return;
        NSString *problem = downloadFailure ?: (long long)package.length != [manifest[@"size"] longLongValue] || ![SHA256Hex(package) isEqual:manifest[@"sha256"]] ? @"安装包大小或 SHA-256 不匹配，已拒绝。" : nil;
        if (problem) { [self downloadOffer:offers index:index+1 failures:[failures arrayByAddingObject:[NSString stringWithFormat:@"%@：%@",source[@"name"],problem]]]; return; }
        [self installPackage:package version:manifest[@"version"]];
    });
}
- (void)installPackage:(NSData *)package version:(NSString *)version {
    NSString *current = NSBundle.mainBundle.bundlePath, *folder = current.stringByDeletingLastPathComponent;
    NSString *token = NSUUID.UUID.UUIDString;
    NSString *staged = [folder stringByAppendingPathComponent:[NSString stringWithFormat:@".%@-update-%@.app",current.lastPathComponent.stringByDeletingPathExtension,token]];
    NSString *backup = [folder stringByAppendingPathComponent:[NSString stringWithFormat:@".%@-backup-%@.app",current.lastPathComponent.stringByDeletingPathExtension,token]];
    NSString *helper = [NSBundle.mainBundle pathForResource:@"update-helper" ofType:@"sh"];
    [self updateStep:1 detail:@"正在校验安装包：签名、版本与证书…" progress:-1];
    dispatch_async(dispatch_get_global_queue(QOS_CLASS_USER_INITIATED,0), ^{
        NSFileManager *fm = NSFileManager.defaultManager;
        NSString *work = [NSTemporaryDirectory() stringByAppendingPathComponent:[@"claude-launcher-update-" stringByAppendingString:token]];
        NSString *dmg = [work stringByAppendingPathComponent:@"update.dmg"], *mount = [work stringByAppendingPathComponent:@"mount"], *script = [work stringByAppendingPathComponent:@"update-helper.sh"];
        NSString *failure = nil; BOOL mounted = NO;
        if (!helper || ![fm createDirectoryAtPath:mount withIntermediateDirectories:YES attributes:@{NSFilePosixPermissions:@0700} error:nil] || ![package writeToFile:dmg atomically:YES] || ![fm copyItemAtPath:helper toPath:script error:nil]) failure = @"无法准备临时文件。";
        if (!failure) { failure = RunTool(@"/usr/bin/hdiutil", @[@"attach",@"-nobrowse",@"-readonly",@"-noautoopen",@"-mountpoint",mount,dmg], NULL); mounted = !failure; }
        NSString *candidate = [mount stringByAppendingPathComponent:current.lastPathComponent];
        if (!failure) {
            NSDictionary *info = [NSDictionary dictionaryWithContentsOfFile:[candidate stringByAppendingPathComponent:@"Contents/Info.plist"]];
            if (![info[@"CFBundleIdentifier"] isEqual:BundleID] || ![info[@"CFBundleShortVersionString"] isEqual:version]) failure = @"安装包中的应用标识或版本与清单不符。";
        }
        if (!failure) failure = SameSigner(candidate);
        if (!failure) failure = RunTool(@"/usr/bin/ditto", @[candidate,staged], NULL);
        if (mounted) RunTool(@"/usr/bin/hdiutil", @[@"detach",@"-quiet",mount], NULL);
        if (!failure) failure = SameSigner(staged);
        if (failure) [fm removeItemAtPath:staged error:nil];
        dispatch_async(dispatch_get_main_queue(), ^{
            if (self.updateCancelled) { [fm removeItemAtPath:staged error:nil]; [fm removeItemAtPath:work error:nil]; return; }
            if (failure) { [fm removeItemAtPath:work error:nil]; [self updateFailed:@[failure] manual:YES]; return; }
            if (![self.settingsDraft isEqual:self.settings]) {
                [fm removeItemAtPath:staged error:nil]; [fm removeItemAtPath:work error:nil]; self.updating = NO; [self closeUpdateProgress];
                [self alert:@"更新安装已取消" detail:@"下载期间设置发生了更改。请先保存或撤销，再重新检查更新；当前应用保持原样。"]; return;
            }
            // Point of no return: cancel is disabled, the launcher quits and the helper replaces it.
            [self updateStep:2 detail:@"校验通过。启动器即将关闭，安装完成后会自动重新打开。Claude 不受影响。" progress:-1];
            dispatch_after(dispatch_time(DISPATCH_TIME_NOW,(int64_t)((self.headless ? 0 : 2)*NSEC_PER_SEC)), dispatch_get_main_queue(), ^{
                NSTask *task = [NSTask new]; task.executableURL = [NSURL fileURLWithPath:@"/bin/zsh"];
                task.arguments = @[script,[NSString stringWithFormat:@"%d",getpid()],current,staged,backup];
                NSError *error = nil;
                if (![task launchAndReturnError:&error]) { [fm removeItemAtPath:staged error:nil]; [self updateFailed:@[error.localizedDescription ?: @"无法启动安装步骤。"] manual:YES]; return; }
                [NSUserDefaults.standardUserDefaults setObject:backup forKey:@"updateBackupPath"];
                [NSUserDefaults.standardUserDefaults synchronize];
                [self addLog:[NSString stringWithFormat:@"更新包校验通过，退出并安装 %@",version]];
                [NSApp terminate:nil];
            });
        });
    });
}
// After a successful replacement the previous version waits as a hidden backup next to the app; move it to the Trash.
- (void)finishPendingUpdate {
    NSString *backup = [NSUserDefaults.standardUserDefaults stringForKey:@"updateBackupPath"];
    if (!backup) return;
    [NSUserDefaults.standardUserDefaults removeObjectForKey:@"updateBackupPath"];
    NSString *current = NSBundle.mainBundle.bundlePath;
    NSString *prefix = [NSString stringWithFormat:@".%@-backup-",current.lastPathComponent.stringByDeletingPathExtension];
    if (![backup.stringByDeletingLastPathComponent isEqual:current.stringByDeletingLastPathComponent] || ![backup.lastPathComponent hasPrefix:prefix] || ![backup.pathExtension isEqual:@"app"]) return;
    if ([NSFileManager.defaultManager fileExistsAtPath:backup] && [NSFileManager.defaultManager trashItemAtURL:[NSURL fileURLWithPath:backup] resultingItemURL:nil error:nil])
        [self addLog:[NSString stringWithFormat:@"已更新到 %@，旧版本已移到废纸篓",[NSBundle.mainBundle objectForInfoDictionaryKey:@"CFBundleShortVersionString"]]];
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
    NSImage *icon = [[NSImage alloc] initWithContentsOfFile:[NSBundle.mainBundle pathForResource:@"AppIcon" ofType:@"icns"]];
    NSApp.applicationIconImage = icon;

    __weak Launcher *weakSelf = self;
    NSImage *mountains = Art(@"sidebar");
    Canvas *sidebar = [[Canvas alloc] initWithFrame:NSMakeRect(0,0,272,720)];
    sidebar.paint = ^(NSRect bounds) {
        NSColor *base = Hex(0xF3ECE2);
        [base setFill]; NSRectFill(bounds);
        if (mountains && [weakSelf.settings[@"showArtwork"] boolValue]) {
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
    NSTextField *edition = Text(@"", NSMakeRect(101,124,170,14), [NSFont systemFontOfSize:9], muted);
    edition.attributedStringValue = [[NSAttributedString alloc] initWithString:[NSString stringWithFormat:@"DESKTOP · V%@",[NSBundle.mainBundle objectForInfoDictionaryKey:@"CFBundleShortVersionString"] ?: @"DEV"] attributes:@{NSKernAttributeName:@2,NSFontAttributeName:[NSFont systemFontOfSize:9],NSForegroundColorAttributeName:muted}];
    [sidebar addSubview:edition];
    NSArray *nav = @[@[@"实例管理",@"briefcase"],@[@"账号管理",@"person.2"],@[@"项目管理",@"folder"],@[@"日志记录",@"doc.text"],@[@"系统设置",@"gearshape"]];
    NSMutableArray *items = [NSMutableArray array];
    for (NSUInteger i = 0; i < nav.count; i++) { NSView *item = [self navItem:nav[i][0] symbol:nav[i][1] index:i]; [sidebar addSubview:item]; [items addObject:item]; }
    self.navItems = items;

    Canvas *main = [[Canvas alloc] initWithFrame:NSMakeRect(272,0,808,720)];
    NSImage *backdrop = Art(@"background");
    main.paint = ^(NSRect bounds) {
        [paper setFill]; NSRectFill(bounds);
        if ([weakSelf.settings[@"showArtwork"] boolValue]) DrawFill(backdrop, bounds, 1);
        // Wash the painting toward paper on the left so text stays readable; the right side keeps more colour.
        [[[NSGradient alloc] initWithStartingColor:[paper colorWithAlphaComponent:0.93] endingColor:[paper colorWithAlphaComponent:0.72]] drawInRect:bounds angle:0];
    };
    [root addSubview:main]; self.mainCanvas = main;

    Canvas *instances = [[Canvas alloc] initWithFrame:main.bounds];
    instances.paint = ^(NSRect bounds) {
        // Brush underline beneath the slogan.
        NSBezierPath *stroke = [NSBezierPath bezierPath];
        [stroke moveToPoint:NSMakePoint(196,104)]; [stroke curveToPoint:NSMakePoint(388,94) controlPoint1:NSMakePoint(260,100) controlPoint2:NSMakePoint(330,92)];
        stroke.lineWidth = 2.6; stroke.lineCapStyle = NSLineCapStyleRound;
        [[Hex(0xEE9A63) colorWithAlphaComponent:0.85] setStroke]; [stroke stroke];
        NSBezierPath *tail = [NSBezierPath bezierPath];
        [tail moveToPoint:NSMakePoint(232,108)]; [tail curveToPoint:NSMakePoint(360,101) controlPoint1:NSMakePoint(280,104) controlPoint2:NSMakePoint(320,101)];
        tail.lineWidth = 1.4; tail.lineCapStyle = NSLineCapStyleRound; [[Hex(0xEE9A63) colorWithAlphaComponent:0.5] setStroke]; [tail stroke];
    };
    [main addSubview:instances];
    NSTextField *slogan = Text(@"让多个 Claude，服务你的不同灵感", NSMakeRect(40,56,420,40), BrushFont(26), Hex(0x3A332D));
    slogan.frameCenterRotation = 1.5; [instances addSubview:slogan];
    self.summaryLabel = Text(@"正在读取状态", NSMakeRect(44,114,300,18), [NSFont systemFontOfSize:12], muted); [instances addSubview:self.summaryLabel];
    self.createButton = [ActionButton buttonWithTitle:@"+  新建实例" target:self action:@selector(newInstance:)];
    self.createButton.frame = NSMakeRect(612,52,160,48); self.createButton.bordered = NO; [instances addSubview:self.createButton];
    NSScrollView *list = [[NSScrollView alloc] initWithFrame:NSMakeRect(16,140,776,528)];
    list.drawsBackground = NO; list.contentView.drawsBackground = NO; list.hasVerticalScroller = YES; list.autohidesScrollers = YES; list.scrollerStyle = NSScrollerStyleOverlay;
    Canvas *cardsView = [[Canvas alloc] initWithFrame:NSMakeRect(0,0,776,10*136-12)];
    NSArray *arts = @[Art(@"card-1") ?: [NSNull null],Art(@"card-2") ?: [NSNull null],Art(@"card-3") ?: [NSNull null],Art(@"card-4") ?: [NSNull null]];
    self.cards = [NSMutableArray array]; self.buttons = [NSMutableArray array]; self.statuses = [NSMutableArray array];
    for (NSInteger n = 1; n <= 10; n++) {
        id art = arts[(n-1)%4];
        AccountCard *card = [self cardFor:n frame:NSMakeRect(0,(n-1)*136,776,124) art:art == [NSNull null] ? nil : art];
        [cardsView addSubview:card]; [self.cards addObject:card]; [self.buttons addObject:card.button]; [self.statuses addObject:card.status];
    }
    list.documentView = cardsView; [instances addSubview:list]; self.cardsView = cardsView;
    self.emptyHint = Text(@"本机还没有 Claude 账号目录。点击右上角「+ 新建实例」创建第一个。", NSMakeRect(40,180,700,22), [NSFont systemFontOfSize:15], muted);
    self.emptyHint.hidden = YES; [instances addSubview:self.emptyHint];
    self.message = Text(@"", NSMakeRect(24,678,580,32), [NSFont systemFontOfSize:12], muted);
    self.message.maximumNumberOfLines = 2; self.message.lineBreakMode = NSLineBreakByWordWrapping; [instances addSubview:self.message];
    self.endLoginButton = [NSButton buttonWithTitle:@"结束登录保护" target:self action:@selector(endLogin:)];
    self.endLoginButton.frame = NSMakeRect(640,680,140,28); self.endLoginButton.bezelStyle = NSBezelStyleRounded;
    [instances addSubview:self.endLoginButton];

    NSView *accounts = [self accountsPage:main.bounds];
    NSView *projects = [self projectsPage:main.bounds];
    NSView *logs = [self logsPage:main.bounds];
    NSView *settings = [self settingsPage:main.bounds];
    for (NSView *page in @[accounts,projects,logs,settings]) [main addSubview:page];
    self.pages = @[instances,accounts,projects,logs,settings];
    self.toast = Text(@"",NSMakeRect(308,20,736,26),[NSFont systemFontOfSize:12 weight:NSFontWeightMedium],Hex(0x795539));
    self.toast.hidden = YES; [root addSubview:self.toast];
    [self addLog:@"启动器已完成初始化"];
    self.message.accessibilityIdentifier = @"launcher-feedback";
    [self showPage:self.page];
    NSMenu *menu = [NSMenu new]; NSMenuItem *appItem = [NSMenuItem new]; [menu addItem:appItem];
    NSMenu *appMenu = [NSMenu new]; appItem.submenu = appMenu;
    NSMenuItem *show = [appMenu addItemWithTitle:@"显示账号窗口" action:@selector(show:) keyEquivalent:@"0"]; show.target = self;
    NSMenuItem *update = [appMenu addItemWithTitle:@"检查更新…" action:@selector(checkForUpdates:) keyEquivalent:@""]; update.target = self;
    [appMenu addItem:NSMenuItem.separatorItem];
    [appMenu addItemWithTitle:@"退出 Claude 启动器" action:@selector(terminate:) keyEquivalent:@"q"];
    NSApp.mainMenu = menu;
}
- (void)show:(id)sender { [self.window deminiaturize:nil]; [self.window makeKeyAndOrderFront:nil]; [NSApp activateIgnoringOtherApps:YES]; [self readLocalUsage]; }
- (void)applicationDidBecomeActive:(NSNotification *)note { [self readLocalUsage]; }
- (void)windowDidBecomeKey:(NSNotification *)note { if (note.object == self.window) [self readLocalUsage]; }
- (BOOL)canObserveUsage {
    if (![self.settings[@"showUsageHistory"] boolValue] || self.snapshotMode || self.loginAccount || self.scanError || !self.processes) return NO;
    // Callers are viewing actions only; accessibility clicks need not first activate the application.
    return self.window.visible && !self.window.miniaturized && self.page == 0;
}
- (void)readLocalUsage {
    if (![self canObserveUsage] || self.readingUsageInterface || NSProcessInfo.processInfo.systemUptime-self.lastUsageRead < 1) return;
    self.readingUsageInterface = YES; self.lastUsageRead = NSProcessInfo.processInfo.systemUptime;
    self.usageInterface = @{}; [self updateUsageCards]; // Revalidate cached ownership only on an explicit viewing action.
    NSUInteger generation = self.usageGeneration;
    NSDictionary *processes = self.processes;
    dispatch_async(dispatch_get_global_queue(QOS_CLASS_UTILITY,0), ^{
        @autoreleasepool {
            NSMutableDictionary *results = [NSMutableDictionary dictionary], *identities = [NSMutableDictionary dictionary], *observedTimes = [NSMutableDictionary dictionary];
            for (NSInteger n=1;n<=10;n++) {
                NSArray *pids = processes[@(n)];
                NSArray *before = UsageIdentity(n,pids);
                if (!before) { if (pids.count) results[@(n)] = @{@"status":@"账号状态待核对"}; continue; }
                NSDictionary *result = ReadUsageInterface([pids.firstObject intValue]); observedTimes[@(n)] = NSDate.date;
                NSString *scanFailure = nil; NSDictionary *afterProcesses = Scan(&scanFailure);
                NSArray *after = scanFailure ? nil : UsageIdentity(n,afterProcesses[@(n)]);
                if ([before isEqual:after]) { results[@(n)] = result; identities[@(n)] = after; }
                else results[@(n)] = @{@"status":@"账号状态变化，待读取"};
            }
            dispatch_async(dispatch_get_main_queue(), ^{
                self.readingUsageInterface = NO;
                if (generation != self.usageGeneration || ![self.settings[@"showUsageHistory"] boolValue] || self.loginAccount || self.scanError) return;
                NSMutableDictionary *combined = [self.usageInterface mutableCopy] ?: [NSMutableDictionary dictionary];
                NSMutableDictionary *saved = [self.savedUsageInterface mutableCopy] ?: [NSMutableDictionary dictionary];
                for (NSNumber *n in results) {
                    NSArray *current = UsageIdentity(n.integerValue,self.processes[n]);
                    if (![current isEqual:identities[n]]) { [combined removeObjectForKey:n]; if (current && ![current isEqual:saved[n][@"identity"]]) [saved removeObjectForKey:n]; continue; }
                    NSDictionary *previous = [combined[n][@"status"] isEqual:@"界面已读取"] ? combined[n] : saved[n];
                    combined[n] = MergeUsageInterface(previous,results[n],current,observedTimes[n]);
                    if ([combined[n][@"status"] isEqual:@"界面已读取"]) saved[n] = combined[n];
                }
                if (![saved isEqual:self.savedUsageInterface]) { self.savedUsageInterface = saved; [self.preferences setObject:StoredUsageInterface(saved) forKey:@"usageInterfaceObservations"]; }
                self.usageInterface = combined; [self updateUsageCards];
            });
        }
    });
}
- (void)updateUsageCards {
    NSDate *now = NSDate.date;
    for (AccountCard *card in self.cards) {
        NSDictionary *record = self.usageInterface[@(card.number)];
        NSString *status = record[@"status"] ?: @"未观察到 Code 用量";
        if (!self.snapshotMode && record[@"identity"]) {
            NSArray *identity = record[@"identity"]; struct proc_bsdinfo process = {0};
            pid_t pid = [self.processes[@(card.number)].firstObject intValue];
            if (pid != [identity[1] intValue] || proc_pidinfo(pid,PROC_PIDTBSDINFO,0,&process,sizeof(process)) != sizeof(process) ||
                process.pbi_start_tvsec != [identity[2] unsignedLongLongValue] || process.pbi_start_tvusec != [identity[3] unsignedLongLongValue]) status = @"账号进程变化，待读取";
        }
        if (!self.snapshotMode && !self.processes[@(card.number)].count) status = @"Claude 未运行";
        else if (!self.snapshotMode && !AXIsProcessTrusted()) status = @"需辅助功能权限";
        if (self.processes[@(card.number)].count > 1) status = @"多个主进程，待核对";
        if (self.loginAccount) status = @"登录保护中";
        if (self.scanError) status = @"进程待核对";
        if (![self.settings[@"showUsageHistory"] boolValue]) status = @"本地用量显示已关闭";
        BOOL available = [status isEqual:@"界面已读取"];
        card.usageValues = @[(available ? record[@"fh"][@"percent"] : nil) ?: NSNull.null,(available ? record[@"sd"][@"percent"] : nil) ?: NSNull.null];
        NSString *detail = @"仅在实例管理页重新获得焦点、返回该页或点击时，只读此账号官方主进程已有的 Code 用量控件；不定时读取，不监听 Claude，不点击、不请求、不读官方历史或登录信息。保留最近观察值，重启后核对归属再恢复。‘上次显示’不是当前服务器额度，两项时间独立；组织变化会使缓存失效，普通 Cookie 写入不会。从未观察到显示 —，不从其他账号补位。辅助功能权限由用户自行授权。";
        card.usageSource.stringValue = available ? @"Claude 界面 · 非服务器查询" : status;
        card.usageSource.toolTip = detail;
        for (NSUInteger row=0;row<2;row++) {
            NSString *key = row == 0 ? @"fh" : @"sd"; NSDictionary *metric = available ? record[key] : nil;
            id value = card.usageValues[row];
            card.usageLabels[row].stringValue = [value isKindOfClass:NSNumber.class] ? [NSString stringWithFormat:@"%.0f%%",[value doubleValue]] : @"—";
            card.usageTimes[row].stringValue = UsageInterfaceTimeLabel(metric,now);
            NSString *specific = metric ? [NSString stringWithFormat:@"%@\n观察时间：%@\n官方重置说明：%@",detail,[NSDateFormatter localizedStringFromDate:metric[@"observedAt"] dateStyle:NSDateFormatterMediumStyle timeStyle:NSDateFormatterMediumStyle],[metric[@"reset"] length] ? metric[@"reset"] : @"未提供"] : detail;
            card.usageLabels[row].toolTip = specific; card.usageTimes[row].toolTip = specific;
            card.usageLabels[row].accessibilityLabel = [NSString stringWithFormat:@"%@，%@，%@，%@",row == 0 ? @"5小时已使用" : @"每周已使用",card.usageLabels[row].stringValue,card.usageTimes[row].stringValue,card.usageSource.stringValue];
        }
        card.needsDisplay = YES;
    }
}
// Menu-bar item (SPEC 常驻图标). Uses the bundled resources/icon/menubar.png template, with an SF Symbol fallback.
- (void)makeStatusItem {
    self.statusItem = [NSStatusBar.systemStatusBar statusItemWithLength:NSVariableStatusItemLength];
    NSString *path = [NSBundle.mainBundle pathForResource:@"menubar" ofType:@"png"];
    NSImage *image = path ? [[NSImage alloc] initWithContentsOfFile:path] : [NSImage imageWithSystemSymbolName:@"square.stack.3d.up" accessibilityDescription:@"Claude 多开启动器"];
    image.size = NSMakeSize(18,18); image.template = YES;
    self.statusItem.button.image = image; self.statusItem.button.toolTip = @"Claude 多开启动器";
    NSMenu *menu = [NSMenu new]; menu.delegate = self; menu.autoenablesItems = NO;
    self.statusItem.menu = menu;
}
// Rebuilt on every open after a fresh scan, so the account states are never stale.
- (void)menuNeedsUpdate:(NSMenu *)menu {
    [menu removeAllItems];
    NSMenuItem *show = [menu addItemWithTitle:@"显示启动器窗口" action:@selector(show:) keyEquivalent:@""]; show.target = self;
    [menu addItem:NSMenuItem.separatorItem];
    [self refresh];
    if (self.scanError) [menu addItemWithTitle:self.scanError action:nil keyEquivalent:@""].enabled = NO;
    NSUInteger shown = 0;
    for (NSInteger n = 1; n <= 10; n++) {
        if (self.cards[n-1].hidden) continue;
        NSMenuItem *item = [menu addItemWithTitle:[NSString stringWithFormat:@"账号 %02ld · %@",(long)n,self.statuses[n-1].stringValue] action:@selector(openFromMenu:) keyEquivalent:@""];
        item.target = self; item.tag = n; item.enabled = self.buttons[n-1].enabled; shown++;
    }
    if (!shown && !self.scanError) [menu addItemWithTitle:@"本机还没有账号" action:nil keyEquivalent:@""].enabled = NO;
    [menu addItem:NSMenuItem.separatorItem];
    NSMenuItem *update = [menu addItemWithTitle:@"检查更新…" action:@selector(checkForUpdates:) keyEquivalent:@""]; update.target = self;
    [menu addItemWithTitle:@"退出启动器" action:@selector(terminate:) keyEquivalent:@""];
}
- (void)openFromMenu:(NSMenuItem *)item { [self selectAccount:self.buttons[item.tag-1]]; }
- (BOOL)applicationShouldHandleReopen:(NSApplication *)app hasVisibleWindows:(BOOL)visible { [self show:nil]; return YES; }
- (NSApplicationTerminateReply)applicationShouldTerminate:(NSApplication *)app {
    if ([self.settingsDraft isEqual:self.settings]) return NSTerminateNow;
    NSAlert *alert = [NSAlert new]; alert.messageText = @"设置尚未保存"; alert.informativeText = @"保存后退出，或放弃这次更改。Claude 实例会继续运行。";
    [alert addButtonWithTitle:@"返回设置"]; [alert addButtonWithTitle:@"放弃并退出"]; [alert addButtonWithTitle:@"保存并退出"];
    NSModalResponse response = [alert runModal];
    if (response == NSAlertSecondButtonReturn) return NSTerminateNow;
    if (response == NSAlertThirdButtonReturn) { [self saveSettings:nil]; return [self.settingsDraft isEqual:self.settings] ? NSTerminateNow : NSTerminateCancel; }
    [self showPage:4]; [self show:nil]; return NSTerminateCancel;
}
- (BOOL)applicationShouldTerminateAfterLastWindowClosed:(NSApplication *)app { return ![self.settings[@"closeToBackground"] boolValue]; }
- (void)refresh {
    NSString *failure = nil; NSDictionary *snapshot = Scan(&failure);
    self.scanError = failure; if (snapshot) self.processes = snapshot;
    self.endLoginButton.enabled = self.loginAccount != 0 && !self.checking.count && !self.starting.count;
    self.endLoginButton.hidden = self.loginAccount == 0;
    for (NSInteger n = 1; n <= 10; n++) {
        NSNumber *key = @(n); NSArray *pids = self.processes[key];
        BOOL checking = [self.checking containsObject:key];
        NSRunningApplication *configured = self.configuredApps[key];
        BOOL knownLaunch = !failure && pids.count == 1 && configured && !configured.terminated && [pids.firstObject intValue] == configured.processIdentifier;
        if (!failure && !knownLaunch) [self.configuredApps removeObjectForKey:key];
        self.cards[n-1].chips = @[(n == 1 ? @"默认目录" : [ProfilePath(n).lastPathComponent stringByReplacingOccurrencesOfString:@"Claude-" withString:@""]),CodeStatus(n,pids.count > 0,knownLaunch)];
        self.cards[n-1].toolTip = n == 1 ? @"01 沿用默认 Code 配置，与普通终端 CLI 共用。" : knownLaunch ? @"本次启动时已指定独立 Code 配置；尚未验证 Code 的实际写入位置。" : @"运行中实例的 Code 配置未确认。保存工作后正常退出该 Claude，再从这里启动；不会迁移或删除旧记录。";
        NSDate *since = self.starting[key];
        BOOL launchPending = [self.pendingLaunches containsObject:key];
        if (since && !launchPending && pids.count && VisibleWindow([pids.firstObject intValue])) { [self.starting removeObjectForKey:key]; since = nil; }
        if (since && -since.timeIntervalSinceNow > 35) {
            // A timed-out LaunchServices request can still start an app later. Keep its account reserved until it returns.
            if (!launchPending) { [self.starting removeObjectForKey:key]; since = nil; }
            self.errors[key] = launchPending ? @"启动请求超过 35 秒仍未返回，等待系统响应；暂不重复启动或进入其他账号的登录保护。" : pids.count ? @"进程已运行，但未发现可见窗口。请点击恢复。" : @"启动失败：35 秒内未发现主进程。请重试。";
            self.message.stringValue = [NSString stringWithFormat:@"账号 %02ld：%@", (long)n, self.errors[key]];
        }
        BOOL exists = [NSFileManager.defaultManager fileExistsAtPath:ProfilePath(n)];
        NSString *state = failure ? @"状态读取失败" : checking ? @"正在检查…" : since ? (self.errors[key] ? @"启动响应超时" : @"正在启动…") : pids.count > 1 ? @"运行中 · 多个实例，请检查" : pids.count ? @"运行中" : @"未运行";
        if (!failure && !checking && !since && !pids.count && self.errors[key]) state = @"未运行 · 操作失败";
        BOOL broken = failure || self.errors[key];
        self.statuses[n-1].stringValue = state;
        self.statuses[n-1].textColor = broken ? NSColor.systemRedColor : Hex(0x6E655C);
        self.statuses[n-1].toolTip = self.errors[key] ?: (pids.count ? [NSString stringWithFormat:@"主进程 PID：%@",[pids componentsJoinedByString:@"、"]] : ProfilePath(n));
        self.cards[n-1].dot = broken ? NSColor.systemRedColor : since ? Hex(0xE0A23A) : pids.count ? Hex(0x3BAA6E) : Hex(0xB9B1A7);
        [self.cards[n-1] setNeedsDisplay:YES];
        self.buttons[n-1].title = checking ? @"检查中" : since ? @"启动中" : pids.count ? @"打开" : exists ? @"启动" : @"创建";
        self.buttons[n-1].enabled = !failure && !since && !checking && (!self.loginAccount || self.loginAccount == n);
        self.buttons[n-1].accessibilityLabel = [NSString stringWithFormat:@"账号 %02ld，%@",(long)n,state];
    }
    // Show only accounts present on this Mac (data directory, running or starting), packed top to bottom.
    CGFloat y = 0; NSUInteger shown = 0, running = 0;
    for (NSInteger n = 1; n <= 10; n++) {
        NSArray *pids = self.processes[@(n)]; if (pids.count) running++;
        BOOL visible = pids.count || self.starting[@(n)] || [self.checking containsObject:@(n)] || [NSFileManager.defaultManager fileExistsAtPath:ProfilePath(n)];
        self.cards[n-1].hidden = !visible;
        self.cards[n-1].nameLabel.stringValue = [self accountName:n];
        if (visible) { [self.cards[n-1] setFrameOrigin:NSMakePoint(0,y)]; y += 136; shown++; }
    }
    [self.cardsView setFrameSize:NSMakeSize(776,MAX(y-12,0))];
    self.emptyHint.hidden = shown > 0 || self.page != 0;
    self.summaryLabel.stringValue = failure ? @"状态暂不可用" : [NSString stringWithFormat:@"本机 %lu 个账号 · %lu 个运行中",(unsigned long)shown,(unsigned long)running];
    self.createButton.enabled = !failure && !self.loginAccount;
    if (failure) self.message.stringValue = failure;
    else if (self.loginAccount) self.message.stringValue = [NSString stringWithFormat:@"账号 %02ld 登录保护中。%@",(long)self.loginAccount,LoginConflict(snapshot,self.loginAccount) ? @"检测到其他 Claude！请停止登录，并正常退出其他实例。" : @"完成登录并核对身份后，点「结束登录保护」恢复多开。"];
    self.accessibilityStatus.stringValue = AXIsProcessTrusted() ? @"已授权：可恢复 Claude 窗口、只读界面用量。" : @"未授权：无法恢复窗口或读取界面用量，不自动更改权限。";
    NSString *text = self.message.stringValue;
    if (text.length && ![text isEqual:self.lastLogged]) {
        self.lastLogged = text; [self addLog:text];
    }
    [self refreshAccountTable];
    if (self.page == 3) [self reloadLogs];
    [self updateUsageCards];
}
- (void)refreshAccountTable {
    NSUInteger shown = 0, total = 0, running = 0, attention = 0, selected = 0; CGFloat y = 0;
    NSDateFormatter *day = [NSDateFormatter new]; day.dateFormat = @"M 月 d 日";
    NSDateFormatter *clock = [NSDateFormatter new]; clock.dateFormat = @"HH:mm";
    for (NSInteger n = 1; n <= (NSInteger)self.rows.count; n++) {
        NSNumber *key = @(n); AccountRow *row = self.rows[n-1];
        BOOL exists = !self.cards[n-1].hidden;
        NSArray *pids = self.processes[key]; BOOL busy = self.starting[key] || [self.checking containsObject:key];
        NSString *status = self.scanError ? @"未知" : self.errors[key] ? @"异常" : busy ? @"处理中" : pids.count ? @"运行中" : @"未运行";
        if (exists) { total++; if (!self.scanError && pids.count) running++; if (self.errors[key] || self.scanError) attention++; }
        NSString *searchable = [NSString stringWithFormat:@"%@ 账号 %02ld %@",[self accountName:n],(long)n,ProfilePath(n)];
        BOOL matches = (!self.accountSearch.stringValue.length || [searchable localizedCaseInsensitiveContainsString:self.accountSearch.stringValue]) && (self.accountFilter.indexOfSelectedItem <= 0 || [status isEqual:self.accountFilter.titleOfSelectedItem]);
        row.hidden = !exists || !matches;
        if (row.hidden) { [self.selectedAccounts removeObject:key]; continue; }
        [row setFrameOrigin:NSMakePoint(0,y)]; y += 52; shown++;
        row.selection.state = [self.selectedAccounts containsObject:key] ? NSControlStateValueOn : NSControlStateValueOff; if (row.selection.state == NSControlStateValueOn) selected++;
        row.nameLabel.stringValue = [self accountName:n]; row.nameLabel.toolTip = row.nameLabel.stringValue;
        row.status.stringValue = status;
        row.dot = self.errors[key] || self.scanError ? NSColor.systemRedColor : busy ? Hex(0xE0A23A) : pids.count ? Hex(0x3BAA6E) : Hex(0xB9B1A7);
        row.status.textColor = self.errors[key] || self.scanError ? NSColor.systemRedColor : busy ? Hex(0xB7832A) : pids.count ? Hex(0x3C8A68) : Hex(0x6E655C);
        // Directory modification time is metadata only; it approximates last use without opening account files.
        NSDate *modified = self.snapshotMode ? nil : [NSFileManager.defaultManager attributesOfItemAtPath:ProfilePath(n) error:nil].fileModificationDate;
        NSCalendar *calendar = NSCalendar.currentCalendar;
        row.recent.stringValue = !modified ? @"—" : [calendar isDateInToday:modified] ? [@"今天 " stringByAppendingString:[clock stringFromDate:modified]] : [calendar isDateInYesterday:modified] ? [@"昨天 " stringByAppendingString:[clock stringFromDate:modified]] : [day stringFromDate:modified];
        row.needsDisplay = YES;
    }
    [self.rowsView setFrameSize:NSMakeSize(734,MAX(y,1))];
    NSArray *values = @[@(total),@(running),@(attention)];
    for (NSUInteger i = 0; i < self.stats.count; i++) self.stats[i].stringValue = [NSString stringWithFormat:@"%02lu",[values[i] unsignedLongValue]];
    self.tableFooter.stringValue = [NSString stringWithFormat:@"显示 %lu / %lu 个 · 选中 %lu 项",(unsigned long)shown,(unsigned long)total,(unsigned long)selected];
    self.selectedPathsButton.enabled = selected > 0; self.selectAllAccounts.enabled = shown > 0;
    self.selectAllAccounts.state = selected == 0 ? NSControlStateValueOff : selected == shown ? NSControlStateValueOn : NSControlStateValueMixed;
}
- (NSString *)accountName:(NSInteger)account {
    return self.accountNames[@(account).stringValue] ?: [NSString stringWithFormat:@"账号 %02ld",(long)account];
}
- (void)accountFilterChanged:(id)sender { [self refreshAccountTable]; }
- (void)selectAccountRow:(NSButton *)sender {
    if (sender.state == NSControlStateValueOn) [self.selectedAccounts addObject:@(sender.tag)]; else [self.selectedAccounts removeObject:@(sender.tag)];
    [self refreshAccountTable];
}
- (void)selectVisibleAccounts:(NSButton *)sender {
    for (AccountRow *row in self.rows) if (!row.hidden) {
        if (sender.state != NSControlStateValueOff) [self.selectedAccounts addObject:@(row.number)]; else [self.selectedAccounts removeObject:@(row.number)];
    }
    [self refreshAccountTable];
}
- (void)copySelectedAccounts:(id)sender {
    NSMutableArray *paths = [NSMutableArray array];
    for (AccountRow *row in self.rows) if (!row.hidden && [self.selectedAccounts containsObject:@(row.number)]) [paths addObject:ProfilePath(row.number)];
    if (!paths.count) return;
    [NSPasteboard.generalPasteboard clearContents]; [NSPasteboard.generalPasteboard setString:[paths componentsJoinedByString:@"\n"] forType:NSPasteboardTypeString];
    [self showLocalNotice:[NSString stringWithFormat:@"已复制 %lu 个账号的目录路径",(unsigned long)paths.count]];
}
- (void)editAccountName:(NSButton *)sender {
    NSInteger account = sender.tag; if (account < 1 || account > 10) return;
    NSAlert *alert = [NSAlert new]; alert.messageText = [NSString stringWithFormat:@"账号 %02ld 的本地别名",(long)account];
    alert.informativeText = @"只修改启动器内的显示名称，不改目录、邮箱或 Claude 身份。最多 24 字，留空恢复默认名称。";
    NSTextField *field = [[NSTextField alloc] initWithFrame:NSMakeRect(0,0,360,28)]; field.stringValue = self.accountNames[@(account).stringValue] ?: @""; field.accessibilityLabel = @"本地账号别名";
    alert.accessoryView = field; [alert addButtonWithTitle:@"保存别名"]; [alert addButtonWithTitle:@"取消"];
    [alert.window setInitialFirstResponder:field];
    while ([alert runModal] == NSAlertFirstButtonReturn) {
        NSString *name = ValidatedAccountName(field.stringValue);
        if (!name) { alert.informativeText = @"请输入不超过 24 字、不含换行或控制字符的别名；留空恢复默认。"; continue; }
        NSMutableDictionary *names = self.accountNames.mutableCopy;
        if (name.length) names[@(account).stringValue] = name; else [names removeObjectForKey:@(account).stringValue];
        [self.preferences setObject:names forKey:@"accountDisplayNames"];
        if (![self.preferences synchronize]) { [self.preferences setObject:self.accountNames forKey:@"accountDisplayNames"]; [self alert:@"别名保存失败" detail:@"本地偏好无法写入，原名称保持不变。"]; return; }
        self.accountNames = names;
        for (AccountCard *card in self.cards) card.nameLabel.stringValue = [self accountName:card.number];
        [self refreshAccountTable]; return;
    }
}
- (void)showPage:(NSInteger)page {
    if (page < 0 || page >= (NSInteger)self.pages.count) page = 0;
    self.page = page;
    if (!self.snapshotMode) [self.preferences setInteger:page forKey:@"lastLauncherPage"];
    if (page == 2) [self refreshProjects:nil];
    if (page == 3) [self reloadLogs];
    for (NSUInteger i = 0; i < self.pages.count; i++) self.pages[i].hidden = (NSInteger)i != page;
    self.createButton.hidden = page != 0;
    if (page != 0) self.emptyHint.hidden = YES;
    self.sidebar.needsDisplay = YES;
    for (NSView *item in self.navItems) item.needsDisplay = YES;
    if (page == 0) [self readLocalUsage];
}
- (void)navigate:(NSButton *)sender { [self showPage:sender.tag]; }
- (void)newInstance:(id)sender {
    [self refresh];
    if (self.scanError || self.loginAccount) return;
    for (NSInteger n = 1; n <= 10; n++) {
        if (!self.processes[@(n)].count && !self.starting[@(n)] && ![self.checking containsObject:@(n)] && ![NSFileManager.defaultManager fileExistsAtPath:ProfilePath(n)]) { [self selectAccount:self.buttons[n-1]]; return; }
    }
    [self alert:@"10 个账号空间都已创建" detail:@"请直接在列表中启动或打开对应账号。"];
}
- (void)showMore:(NSButton *)sender {
    NSMenu *menu = [NSMenu new]; menu.autoenablesItems = NO;
    NSMenuItem *reveal = [menu addItemWithTitle:@"在 Finder 中显示账号目录" action:@selector(revealProfile:) keyEquivalent:@""];
    reveal.target = self; reveal.tag = sender.tag; reveal.enabled = [NSFileManager.defaultManager fileExistsAtPath:ProfilePath(sender.tag)];
    NSMenuItem *copy = [menu addItemWithTitle:@"复制账号目录路径" action:@selector(copyProfilePath:) keyEquivalent:@""];
    copy.target = self; copy.tag = sender.tag;
    NSMenuItem *login = [menu addItemWithTitle:@"登录 / 重新登录保护…" action:@selector(beginLogin:) keyEquivalent:@""];
    login.target = self; login.tag = sender.tag;
    login.enabled = !self.loginAccount && !self.starting[@(sender.tag)] && ![self.checking containsObject:@(sender.tag)];
    [menu addItem:NSMenuItem.separatorItem];
    NSMenuItem *remove = [menu addItemWithTitle:@"删除该实例…" action:@selector(deleteProfile:) keyEquivalent:@""];
    remove.target = self; remove.tag = sender.tag;
    remove.attributedTitle = [[NSAttributedString alloc] initWithString:@"删除该实例…" attributes:@{NSForegroundColorAttributeName:NSColor.systemRedColor,NSFontAttributeName:[NSFont menuFontOfSize:0]}];
    remove.enabled = sender.tag != 1 && self.loginAccount != sender.tag && !self.starting[@(sender.tag)] && ![self.checking containsObject:@(sender.tag)] && !self.processes[@(sender.tag)].count && [NSFileManager.defaultManager fileExistsAtPath:ProfilePath(sender.tag)];
    if (sender.tag == 1) remove.toolTip = @"账号 01 是 Claude 默认目录，不能在启动器中删除。";
    [menu popUpMenuPositioningItem:nil atLocation:NSMakePoint(0,-4) inView:sender];
}
- (void)revealProfile:(NSMenuItem *)item { [NSWorkspace.sharedWorkspace activateFileViewerSelectingURLs:@[[NSURL fileURLWithPath:ProfilePath(item.tag)]]]; }
- (void)copyProfilePath:(NSMenuItem *)item {
    [NSPasteboard.generalPasteboard clearContents]; [NSPasteboard.generalPasteboard setString:ProfilePath(item.tag) forType:NSPasteboardTypeString];
}
- (void)deleteProfile:(NSMenuItem *)item {
    NSInteger account = item.tag; NSNumber *key = @(account);
    [self refresh];
    NSString *blocker = self.scanError ?: [self deletionBlocker:account];
    if (blocker) { [self alert:@"暂时不能删除" detail:blocker]; return; }
    NSAlert *confirm = [NSAlert new]; confirm.alertStyle = NSAlertStyleWarning;
    confirm.messageText = [NSString stringWithFormat:@"删除账号 %02ld？",(long)account];
    confirm.informativeText = [NSString stringWithFormat:@"将把以下目录移到废纸篓，其中的登录状态、聊天缓存和 Claude Code 配置会一起移走：\n%@\n\n清空废纸篓前可以恢复。",ProfilePath(account)];
    [confirm addButtonWithTitle:@"取消"];
    NSButton *destroy = [confirm addButtonWithTitle:@"移到废纸篓"]; destroy.hasDestructiveAction = YES;
    [self.checking addObject:key]; // Reserve this account while the modal dialog runs its nested event loop.
    NSModalResponse response = [confirm runModal];
    [self.checking removeObject:key];
    if (response != NSAlertSecondButtonReturn) { [self refresh]; return; }
    // State may have changed while the dialog was open; recheck identity, process state and path safety.
    [self refresh];
    blocker = self.scanError ?: [self deletionBlocker:account];
    if (blocker) { [self alert:@"已取消删除" detail:blocker]; return; }
    [self checkAccount:account completion:^{
        [self refresh];
        NSString *failure = self.scanError ?: [self deletionBlocker:account] ?: ValidateProfileRoot(ProfilePath(account),ProfilePath(account));
        if (failure) { [self alert:@"已取消删除" detail:failure]; return; }
        NSError *error = nil;
        if (![NSFileManager.defaultManager trashItemAtURL:[NSURL fileURLWithPath:ProfilePath(account) isDirectory:YES] resultingItemURL:nil error:&error]) {
            [self alert:@"删除失败" detail:error.localizedDescription ?: @"未移动任何数据。"]; return;
        }
        [self.errors removeObjectForKey:key]; [self.configuredApps removeObjectForKey:key];
        self.message.stringValue = [NSString stringWithFormat:@"账号 %02ld 已移到废纸篓。",(long)account];
        [self refresh];
    }];
}
- (NSString *)deletionBlocker:(NSInteger)account {
    if (self.loginAccount == account) return @"该账号处于登录保护中，请先结束保护。";
    return DeleteBlocker(account,self.processes[@(account)],self.starting[@(account)] != nil || [self.checking containsObject:@(account)]);
}
- (void)checkAccount:(NSInteger)account completion:(void (^)(void))completion {
    NSNumber *key = @(account);
    if (account < 1 || account > 10 || [self.checking containsObject:key] || self.starting[key]) return;
    [self.checking addObject:key]; [self.errors removeObjectForKey:key]; [self refresh];
    ValidateProfileAsync(ProfilePath(account), ^(NSString *failure) {
        [self.checking removeObject:key];
        if (failure) {
            self.errors[key] = failure;
            self.message.stringValue = [NSString stringWithFormat:@"账号 %02ld：%@",(long)account,failure];
            [self refresh]; [self alert:@"账号存储隔离检查未通过" detail:failure]; return;
        }
        completion();
        [self refresh];
    });
}
- (void)revealSupport:(id)sender {
    [NSWorkspace.sharedWorkspace activateFileViewerSelectingURLs:@[[NSURL fileURLWithPath:[NSHomeDirectory() stringByAppendingPathComponent:@"Library/Application Support/Claude Launcher"]]]];
}
- (void)openAccessibility:(id)sender {
    NSDictionary *options = @{(__bridge NSString *)kAXTrustedCheckOptionPrompt:@YES}; AXIsProcessTrustedWithOptions((__bridge CFDictionaryRef)options);
    [NSWorkspace.sharedWorkspace openURL:[NSURL URLWithString:@"x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility"]];
}
- (void)endLogin:(id)sender {
    if (!self.loginAccount || self.checking.count || self.starting.count) return;
    NSInteger account = self.loginAccount;
    NSAlert *confirm = [NSAlert new]; confirm.messageText = @"结束登录保护？";
    confirm.informativeText = @"请确认登录已完成并核对账号身份，或已取消浏览器中的登录流程。结束保护后可以打开其他账号；启动器无法验证登录结果或拦截浏览器回调。";
    [confirm addButtonWithTitle:@"继续保护"]; [confirm addButtonWithTitle:@"结束保护"];
    [self.checking addObject:@(account)];
    NSModalResponse response = [confirm runModal];
    [self.checking removeObject:@(account)];
    if (response != NSAlertSecondButtonReturn) { [self refresh]; return; }
    self.loginAccount = 0;
    [NSUserDefaults.standardUserDefaults removeObjectForKey:@"loginProtectionAccount"];
    self.message.stringValue = @"已结束登录保护。只有确认登录完成、身份正确后，才应同时打开其他账号。";
    [self refresh];
}
- (NSString *)loginBlocker:(NSInteger)account {
    return self.scanError ?: LoginBlocker(self.processes,[self.starting.allKeys arrayByAddingObjectsFromArray:self.checking.allObjects],account);
}
- (NSString *)openingBlocker:(NSInteger)account expectedPID:(pid_t)expectedPID {
    if (self.scanError) return self.scanError;
    NSArray *pids = self.processes[@(account)];
    if (expectedPID && (pids.count != 1 || [pids.firstObject intValue] != expectedPID)) return @"检查期间原实例已退出或被替换，未自动重启。请重新点击。";
    if (self.loginAccount && (self.loginAccount != account || [self loginBlocker:account])) return @"登录保护中：请先完成目标账号登录并核对身份。外部启动的其他 Claude 需要你正常退出。";
    if (pids.count > 1) return @"同一目录发现多个主进程，未激活或结束任何实例。请自行检查 Claude 窗口。";
    return nil;
}
- (void)beginLogin:(id)sender {
    NSInteger account = [sender tag]; NSNumber *key = @(account);
    if (account < 1 || account > 10 || self.loginAccount || self.starting[key] || [self.checking containsObject:key]) return;
    [self refresh];
    NSString *failure = [self loginBlocker:account];
    if (failure) { [self alert:@"暂时不能进入登录保护" detail:failure]; return; }
    NSAlert *confirm = [NSAlert new];
    confirm.messageText = [NSString stringWithFormat:@"账号 %02ld：进入登录保护？",(long)account];
    confirm.informativeText = @"保护期间，启动器只允许打开这个账号。随后由你在 Claude 中登录或重新登录，并核对浏览器和 Claude 中的身份。\n\n不会退出、登出其他账号或改写登录数据。请勿从 Dock、终端等外部入口打开其他 Claude；启动器不能拦截系统登录回调。新账号会在保护中创建目录。";
    [confirm addButtonWithTitle:@"取消"]; [confirm addButtonWithTitle:@"保护并打开"];
    [self.checking addObject:key];
    NSModalResponse response = [confirm runModal];
    [self.checking removeObject:key];
    [self refresh];
    if (response != NSAlertSecondButtonReturn) return;
    [self protectAndOpen:account];
}
- (void)protectAndOpen:(NSInteger)account {
    [self protectAndOpen:account prepare:nil];
}
- (void)protectAndOpen:(NSInteger)account prepare:(BOOL (^)(void))prepare {
    NSString *failure = [self loginBlocker:account];
    if (failure) { [self alert:@"状态已变化，未进入保护" detail:failure]; return; }
    self.loginAccount = account;
    [NSUserDefaults.standardUserDefaults setInteger:account forKey:@"loginProtectionAccount"];
    [self selectAccount:self.buttons[account-1] prepare:prepare];
}
- (void)selectAccount:(NSButton *)sender {
    [self selectAccount:sender prepare:nil];
}
- (void)selectAccount:(NSButton *)sender prepare:(BOOL (^)(void))prepare {
    NSNumber *key = @(sender.tag);
    if (sender.tag < 1 || sender.tag > 10 || self.starting[key] || [self.checking containsObject:key]) return;
    [self refresh];
    NSString *failure = [self openingBlocker:sender.tag expectedPID:0];
    if (failure) { [self alert:@"暂时不能打开账号" detail:failure]; return; }
    NSInteger account = sender.tag;
    BOOL create = !self.loginAccount && !self.processes[key].count && ![NSFileManager.defaultManager fileExistsAtPath:ProfilePath(account)];
    if (create) {
        NSAlert *choice = [NSAlert new];
        choice.messageText = [NSString stringWithFormat:@"创建账号 %02ld？",(long)account];
        choice.informativeText = @"将创建独立的本地数据目录。\n\n直接创建：立即启动，其他 Claude 保持运行。登录时浏览器回调可能打开其他窗口，登录后请核对身份。\n\n进入登录保护：需先正常退出其他 Claude；保护期间只允许打开这个账号，登录更稳妥。";
        [choice addButtonWithTitle:@"直接创建"]; [choice addButtonWithTitle:@"进入登录保护"]; [choice addButtonWithTitle:@"取消"];
        [self.checking addObject:key]; // Reserve this account while the modal dialog runs its nested event loop.
        NSModalResponse response = [choice runModal];
        [self.checking removeObject:key];
        [self refresh];
        if (response == NSAlertSecondButtonReturn) { [self protectAndOpen:account prepare:prepare]; return; }
        if (response != NSAlertFirstButtonReturn) return;
        failure = [self openingBlocker:account expectedPID:0];
        if (failure || self.processes[key].count) { [self alert:@"状态已变化，未创建" detail:failure ?: @"该账号已在运行，请重新点击。"]; return; }
    }
    pid_t expectedPID = self.processes[key].count == 1 ? [self.processes[key].firstObject intValue] : 0;
    [self checkAccount:account completion:^{ [self openCheckedAccount:account expectedPID:expectedPID create:create prepare:prepare]; }];
}
- (void)openCheckedAccount:(NSInteger)account expectedPID:(pid_t)expectedPID create:(BOOL)create prepare:(BOOL (^)(void))prepare {
    NSNumber *key = @(account);
    [self refresh]; // The background check may outlive a process or a login-protection change.
    NSString *failure = [self openingBlocker:account expectedPID:expectedPID];
    if (failure) { [self alert:@"实例或保护状态已变化" detail:failure]; return; }
    NSString *storageFailure = ValidateProfileRoot(ProfilePath(account),ProfilePath(account));
    if (storageFailure) { [self alert:@"账号路径已变化" detail:storageFailure]; return; }
    NSArray *pids = self.processes[key];
    [self.errors removeObjectForKey:key];
    if (pids.count == 1) { [self activatePID:[pids.firstObject intValue] account:account prepare:prepare]; return; }
    NSString *bundlePath = nil;
    for (NSString *path in @[@"/Applications/Claude.app", [NSHomeDirectory() stringByAppendingPathComponent:@"Applications/Claude.app"]]) {
        if ([NSFileManager.defaultManager isExecutableFileAtPath:[path stringByAppendingPathComponent:@"Contents/MacOS/Claude"]]) { bundlePath = path; break; }
    }
    if (!bundlePath) { [self alert:@"未找到官方 Claude.app" detail:@"请将官方 Claude.app 安装到 /Applications 或 ~/Applications 后重试。"] ; return; }
    NSError *error = nil; BOOL directory = NO;
    BOOL exists = [NSFileManager.defaultManager fileExistsAtPath:ProfilePath(account) isDirectory:&directory];
    if (!exists && !self.loginAccount && !create) {
        self.message.stringValue = @"账号目录已变化，未创建新目录。请重新点击。"; return;
    }
    if ((exists && !directory) || (!exists && ![NSFileManager.defaultManager createDirectoryAtPath:ProfilePath(account) withIntermediateDirectories:YES attributes:@{NSFilePosixPermissions:@0700} error:&error])) {
        [self alert:@"无法使用账号目录" detail:error.localizedDescription ?: @"该路径已存在且不是目录。未覆盖任何数据。"] ; return;
    }
    NSString *codeConfig = CodeConfigPath(account);
    if (codeConfig && ![NSFileManager.defaultManager fileExistsAtPath:codeConfig] && ![NSFileManager.defaultManager createDirectoryAtPath:codeConfig withIntermediateDirectories:NO attributes:@{NSFilePosixPermissions:@0700} error:&error]) {
        [self alert:@"无法创建 Claude Code 独立配置目录" detail:error.localizedDescription]; return;
    }
    storageFailure = ValidateProfileRoot(ProfilePath(account),ProfilePath(account));
    if (!storageFailure && codeConfig) storageFailure = ValidateProfileRoot(codeConfig,codeConfig);
    if (storageFailure) { [self alert:@"创建后隔离检查未通过" detail:storageFailure]; return; }
    [self refresh];
    if ([self openingBlocker:account expectedPID:0] || self.processes[key].count) {
        self.message.stringValue = @"进程或登录保护状态已变化，未启动新实例，请重试。"; return;
    }
    if (prepare && !prepare()) return;
    self.starting[key] = [NSDate date]; [self refresh];
    self.message.stringValue = [NSString stringWithFormat:@"正在启动账号 %02ld…",(long)account];
    NSWorkspaceOpenConfiguration *config = [NSWorkspaceOpenConfiguration configuration];
    config.createsNewApplicationInstance = YES; config.activates = YES;
    config.arguments = account == 1 ? @[] : @[[ @"--user-data-dir=" stringByAppendingString:ProfilePath(account)]];
    if (codeConfig) config.environment = @{@"CLAUDE_CONFIG_DIR":codeConfig};
    NSDate *requested = [NSDate date];
    [self.pendingLaunches addObject:key];
    [NSWorkspace.sharedWorkspace openApplicationAtURL:[NSURL fileURLWithPath:bundlePath] configuration:config completionHandler:^(NSRunningApplication *app, NSError *launchError) {
        dispatch_async(dispatch_get_main_queue(), ^{
            [self.pendingLaunches removeObject:key];
            if (!launchError) [self.errors removeObjectForKey:key];
            if (launchError) { [self.starting removeObjectForKey:key]; self.errors[key] = launchError.localizedDescription; self.message.stringValue = [@"启动失败：" stringByAppendingString:launchError.localizedDescription]; }
            // A returned pre-existing app is not evidence that the requested environment took effect.
            if (!launchError && codeConfig && app.launchDate && [app.launchDate compare:requested] != NSOrderedAscending && !app.terminated) {
                NSArray *args = Arguments(app.processIdentifier); char path[PROC_PIDPATHINFO_MAXSIZE] = {0};
                if (args && AccountForArguments(args) == account && proc_pidpath(app.processIdentifier,path,sizeof(path)) > 0 && IsMainExecutable(@(path))) self.configuredApps[key] = app;
            }
            [self refresh];
        });
    }];
}
- (void)activatePID:(pid_t)pid account:(NSInteger)account prepare:(BOOL (^)(void))prepare {
    // Revalidate the identity immediately before activating; never fall back to a bundle-wide open.
    NSArray *args = Arguments(pid); char path[PROC_PIDPATHINFO_MAXSIZE] = {0};
    if (!args || AccountForArguments(args) != account || proc_pidpath(pid,path,sizeof(path)) <= 0 || !IsMainExecutable(@(path))) {
        [self alert:@"实例状态已改变" detail:@"无法核对该 PID。请稍后再次点击，未启动新实例。"] ; [self refresh]; return;
    }
    NSRunningApplication *app = [NSRunningApplication runningApplicationWithProcessIdentifier:pid];
    if (!app || app.terminated) { [self alert:@"实例已退出" detail:@"请再次点击启动。"] ; return; }
    if (prepare && !prepare()) return;
    [app unhide];
    if (@available(macOS 14.0, *)) {
        [NSApp yieldActivationToApplication:app];
        [app activateFromApplication:NSRunningApplication.currentApplication options:NSApplicationActivateAllWindows];
    } else {
        [app activateWithOptions:NSApplicationActivateAllWindows | NSApplicationActivateIgnoringOtherApps];
    }
    if (![self.settings[@"restoreMinimized"] boolValue]) {
        self.message.stringValue = [NSString stringWithFormat:@"已激活账号 %02ld；按设置保留窗口最小化状态。",(long)account]; return;
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
        TestSettings();
        TestProjects();
        TestProjectImport();
        TestProjectRegistration();
        TestUsageInterface();
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
        NSCAssert(LoginBlocker(@{@0:@[@123]},@[],2),@"unmanaged instance blocks protected login");
        NSCAssert(LoginBlocker(@{@2:@[@123,@124]},@[],2),@"duplicate target blocks protected login");
        NSCAssert(LoginBlocker(@{},@[@3],2),@"other in-flight operation blocks protected login even before a PID exists");
        NSCAssert(!LoginBlocker(@{@2:@[@123]},@[@2],2),@"protected target may complete its own check or launch");
        NSCAssert([CodeStatus(2,YES,NO) isEqual:@"Code 未确认"],@"existing or external instance is not proven isolated");
        NSCAssert([CodeStatus(2,YES,YES) isEqual:@"Code 已指定"],@"launch evidence means configured, not verified storage");
        NSCAssert([CodeStatus(2,NO,YES) isEqual:@"Code 待启动"],@"exited process loses running configuration claim");
        NSCAssert([CodeStatus(1,YES,YES) isEqual:@"Code 默认"],@"default remains shared with CLI");
        Launcher *state = [Launcher new];
        state.processes = @{}; [state.checking addObject:@2];
        NSCAssert([state deletionBlocker:2] && ![state deletionBlocker:3],@"in-flight check blocks deletion only for that account");
        state.loginAccount = 3;
        NSCAssert([state deletionBlocker:3],@"protected account cannot be deleted");
        [state.checking removeObject:@2];
        NSCAssert(![state deletionBlocker:2],@"other idle account is not blocked by protection");
        NSCAssert([state openingBlocker:2 expectedPID:0],@"protection changed during check blocks a different account");
        state.loginAccount = 0; state.processes = @{@2:@[@123]};
        NSCAssert(![state openingBlocker:2 expectedPID:123],@"same live PID remains eligible");
        state.processes = @{};
        NSCAssert([state openingBlocker:2 expectedPID:123],@"original PID exited during check: do not restart");
        state.processes = @{@2:@[@124]};
        NSCAssert([state openingBlocker:2 expectedPID:123],@"replacement PID during check: do not switch silently");
        state.processes = @{@2:@[@123,@124]};
        NSCAssert([state openingBlocker:2 expectedPID:0],@"duplicate PIDs still block opening after check");
        state.processes = @{}; state.scanError = @"synthetic scan failure";
        NSCAssert([state openingBlocker:2 expectedPID:0],@"unknown process state still blocks launching");
        NSCAssert(DeleteBlocker(1,@[],NO),@"default profile never deleted");
        NSCAssert(DeleteBlocker(3,@[@123],NO) && DeleteBlocker(3,@[],YES),@"running or starting profile not deleted");
        NSCAssert(DeleteBlocker(11,@[],NO),@"out-of-range account rejected");
        NSCAssert(!DeleteBlocker(3,@[],NO),@"idle profile deletable");
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
        // Exercise concurrent checks and main-queue completion without opening or launching any application.
        __block NSUInteger completed = 0;
        ValidateProfileAsync(a, ^(NSString *failure) {
            NSCAssert(NSThread.isMainThread && failure,@"unsafe account fails on main queue"); completed++;
        });
        ValidateProfileAsync(b, ^(NSString *failure) {
            NSCAssert(NSThread.isMainThread && !failure,@"other account passes independently on main queue"); completed++;
        });
        NSCAssert(completed == 0,@"storage validation does not synchronously block its caller");
        NSDate *deadline = [NSDate dateWithTimeIntervalSinceNow:10];
        while (completed < 2 && deadline.timeIntervalSinceNow > 0) [[NSRunLoop currentRunLoop] runUntilDate:[NSDate dateWithTimeIntervalSinceNow:0.01]];
        NSCAssert(completed == 2,@"both background checks complete");
        NSCAssert([fm removeItemAtPath:[a stringByAppendingPathComponent:@"Cookies"] error:nil],@"remove generated hardlink only");
        NSString *nested = [a stringByAppendingPathComponent:@"ClaudeCodeConfig/projects"];
        NSCAssert([fm createDirectoryAtPath:nested withIntermediateDirectories:YES attributes:nil error:nil],@"nested Code fixture");
        NSString *nestedLink = [nested stringByAppendingPathComponent:@"shared"];
        NSCAssert([fm createSymbolicLinkAtPath:nestedLink withDestinationPath:b error:nil],@"nested shared-storage fixture");
        NSCAssert(ValidateProfileAtPath(a,a),@"recursive background policy still rejects deeply nested links");
        NSCAssert([fm removeItemAtPath:nestedLink error:nil],@"remove generated nested link only");
        NSCAssert(!ValidateProfileAtPath(a,a),@"safe nested profile remains usable");
        NSCAssert([[NSString stringWithContentsOfFile:marker encoding:NSUTF8StringEncoding error:nil] isEqual:@"synthetic-test-only"],@"other fixture untouched");
        NSCAssert([fm removeItemAtPath:root error:nil],@"clean generated fixtures only");
        puts("PASS: login conflict guard, delete guards, duplicate overrides, exact paths, symlink and hardlink rejection; synthetic fixtures only");
        puts("PASS: profile mapping, helper/CLI filtering, malformed arguments, kernel argv");
        puts("PASS: login admission, per-account operation guards, conservative Code status, concurrent metadata checks with main-queue completion");
        // Update verification with a throwaway key: XML key round trip, signed manifest, tampering, versions, redirects, install paths.
        SecKeyRef privateKey = SecKeyCreateRandomKey((__bridge CFDictionaryRef)@{(id)kSecAttrKeyType:(id)kSecAttrKeyTypeRSA,(id)kSecAttrKeySizeInBits:@2048}, NULL);
        NSCAssert(privateKey,@"test key");
        SecKeyRef publicKey = SecKeyCopyPublicKey(privateKey);
        NSData *pkcs1 = CFBridgingRelease(SecKeyCopyExternalRepresentation(publicKey,NULL));
        // Test-only reader for SEQUENCE { INTEGER modulus, INTEGER exponent }, to produce the XML form the Windows side publishes.
        NSData *(^readInteger)(const uint8_t **) = ^NSData *(const uint8_t **p) {
            NSCAssert(**p == 0x02,@"integer tag"); (*p)++;
            NSUInteger length = *(*p)++;
            if (length & 0x80) { NSUInteger count = length & 0x7f; length = 0; while (count--) length = (length << 8) | *(*p)++; }
            NSData *value = [NSData dataWithBytes:*p length:length]; *p += length; return value;
        };
        const uint8_t *cursor = pkcs1.bytes; NSCAssert(*cursor == 0x30,@"sequence"); cursor++;
        cursor += (*cursor & 0x80) ? 1 + (*cursor & 0x7f) : 1;
        NSData *modulus = readInteger(&cursor), *exponent = readInteger(&cursor);
        NSString *xml = [NSString stringWithFormat:@"<RSAKeyValue><Modulus>%@</Modulus><Exponent>%@</Exponent></RSAKeyValue>",[modulus base64EncodedStringWithOptions:0],[exponent base64EncodedStringWithOptions:0]];
        SecKeyRef rebuilt = PublicKeyFromXML(xml);
        NSCAssert(rebuilt && [CFBridgingRelease(SecKeyCopyExternalRepresentation(rebuilt,NULL)) isEqual:pkcs1],@"XML key rebuilds identical PKCS#1");
        NSString *(^sign)(NSDictionary *) = ^NSString *(NSDictionary *m) {
            NSString *payload = [NSString stringWithFormat:@"%@\n%@\n%@\n%lld\n%@\n",m[@"platform"],m[@"version"],m[@"file"],[m[@"size"] longLongValue],m[@"sha256"]];
            return [CFBridgingRelease(SecKeyCreateSignature(privateKey,kSecKeyAlgorithmRSASignatureMessagePKCS1v15SHA256,(__bridge CFDataRef)[payload dataUsingEncoding:NSUTF8StringEncoding],NULL)) base64EncodedStringWithOptions:0];
        };
        NSData *(^json)(NSDictionary *) = ^NSData *(NSDictionary *m) { return [NSJSONSerialization dataWithJSONObject:m options:0 error:nil]; };
        NSMutableDictionary *good = [@{@"platform":@"macos",@"version":@"1.8.0",@"file":@"ClaudeLauncher-1.8.0.dmg",@"size":@2048,@"sha256":SHA256Hex([NSData data]),@"minimumOS":@"12.0"} mutableCopy];
        good[@"signature"] = sign(good);
        NSString *why = nil;
        NSCAssert(VerifyManifest(json(good),@[(__bridge id)rebuilt],&why) && !why,@"signed manifest accepted");
        NSMutableDictionary *tampered = [good mutableCopy]; tampered[@"sha256"] = SHA256Hex([@"x" dataUsingEncoding:NSUTF8StringEncoding]);
        NSCAssert(!VerifyManifest(json(tampered),@[(__bridge id)rebuilt],&why) && [why containsString:@"签名"],@"tampered hash rejected by signature");
        for (NSArray *change in @[@[@"platform",@"windows"],@[@"file",@"evil.dmg"],@[@"size",@(UpdateSizeLimit+1)],@[@"version",@"1.8"],@[@"sha256",@"ABC"]]) {
            NSMutableDictionary *bad = [good mutableCopy]; bad[change[0]] = change[1]; bad[@"signature"] = sign(bad);
            NSCAssert(!VerifyManifest(json(bad),@[(__bridge id)rebuilt],&why),@"signed but malformed manifest rejected: %@",change[0]);
        }
        NSCAssert(!VerifyManifest([NSMutableData dataWithLength:20000],@[(__bridge id)rebuilt],&why),@"oversized manifest rejected");
        // Two accepted keys (shared + macOS publisher): a manifest signed by either passes; a third key does not.
        SecKeyRef macPrivate = SecKeyCreateRandomKey((__bridge CFDictionaryRef)@{(id)kSecAttrKeyType:(id)kSecAttrKeyTypeRSA,(id)kSecAttrKeySizeInBits:@2048}, NULL);
        SecKeyRef otherPrivate = SecKeyCreateRandomKey((__bridge CFDictionaryRef)@{(id)kSecAttrKeyType:(id)kSecAttrKeyTypeRSA,(id)kSecAttrKeySizeInBits:@2048}, NULL);
        NSCAssert(macPrivate && otherPrivate,@"extra test keys");
        id macPublic = CFBridgingRelease(SecKeyCopyPublicKey(macPrivate));
        NSString *(^signWith)(SecKeyRef, NSDictionary *) = ^NSString *(SecKeyRef k, NSDictionary *m) {
            NSString *payload = [NSString stringWithFormat:@"%@\n%@\n%@\n%lld\n%@\n",m[@"platform"],m[@"version"],m[@"file"],[m[@"size"] longLongValue],m[@"sha256"]];
            return [CFBridgingRelease(SecKeyCreateSignature(k,kSecKeyAlgorithmRSASignatureMessagePKCS1v15SHA256,(__bridge CFDataRef)[payload dataUsingEncoding:NSUTF8StringEncoding],NULL)) base64EncodedStringWithOptions:0];
        };
        NSArray *both = @[(__bridge id)rebuilt,macPublic];
        NSMutableDictionary *byMac = [good mutableCopy]; byMac[@"signature"] = signWith(macPrivate,byMac);
        NSMutableDictionary *byOther = [good mutableCopy]; byOther[@"signature"] = signWith(otherPrivate,byOther);
        NSCAssert(VerifyManifest(json(good),both,&why),@"shared-key manifest accepted with two keys");
        NSCAssert(VerifyManifest(json(byMac),both,&why),@"macOS-key manifest accepted with two keys");
        NSCAssert(!VerifyManifest(json(byMac),@[(__bridge id)rebuilt],&why),@"macOS-key manifest rejected without macOS key");
        NSCAssert(!VerifyManifest(json(byOther),both,&why) && [why containsString:@"签名"],@"unknown key rejected");
        NSCAssert(!VerifyManifest(json(good),@[],&why),@"no keys, no update");
        CFRelease(macPrivate); CFRelease(otherPrivate);
        CFRelease(rebuilt); CFRelease(publicKey); CFRelease(privateKey);
        NSArray *(^offer)(NSString *, NSString *) = ^NSArray *(NSString *name, NSString *version) { return @[@{@"source":@{@"name":name},@"manifest":@{@"version":version}}]; };
        NSArray *lagging = [offer(@"自建",@"1.7.4") arrayByAddingObjectsFromArray:offer(@"GitHub",@"1.7.5")];
        NSCAssert([BestOffers(lagging).firstObject[@"source"][@"name"] isEqual:@"GitHub"] && BestOffers(lagging).count == 1,@"lagging self-hosted does not hide newer GitHub");
        NSArray *same = [offer(@"自建",@"1.7.5") arrayByAddingObjectsFromArray:offer(@"GitHub",@"1.7.5")];
        NSCAssert(BestOffers(same).count == 2 && [BestOffers(same).firstObject[@"source"][@"name"] isEqual:@"自建"],@"equal versions keep self-hosted first, GitHub as download fallback");
        NSCAssert(BestOffers(@[]).count == 0 && [BestOffers([offer(@"GitHub",@"1.10.0") arrayByAddingObjectsFromArray:offer(@"自建",@"1.9.9")]).firstObject[@"source"][@"name"] isEqual:@"GitHub"],@"numeric comparison, empty input");
        NSCAssert(CompareVersions(@"1.7.0",@"1.6") == NSOrderedDescending && CompareVersions(@"1.6.0",@"1.6") == NSOrderedSame && CompareVersions(@"1.10.0",@"1.9.9") == NSOrderedDescending,@"numeric version order");
        NSCAssert(RedirectAllowed([NSURL URLWithString:@"https://objects.githubusercontent.com/x"],YES) && !RedirectAllowed([NSURL URLWithString:@"https://evil.example/x"],YES) && !RedirectAllowed([NSURL URLWithString:@"https://github.com/x"],NO) && !RedirectAllowed([NSURL URLWithString:@"http://github.com/x"],YES),@"redirect allowlist");
        NSCAssert(InstallBlocker(@"/private/var/folders/x/AppTranslocation/y/Claude 启动器.app") && InstallBlocker(@"/Volumes/Claude/Claude 启动器.app") && InstallBlocker(@"/tmp/not-an-app"),@"unsafe install locations rejected");
        NSArray *bundled = ReleaseKeys();
        NSCAssert(bundled.count == 2,@"both bundled release keys load (shared + macOS)");
        for (id key in bundled) NSCAssert(SecKeyGetBlockSize((__bridge SecKeyRef)key) == 384,@"bundled release key is RSA-3072");
        NSCAssert(![CFBridgingRelease(SecKeyCopyExternalRepresentation((__bridge SecKeyRef)bundled[0],NULL)) isEqual:CFBridgingRelease(SecKeyCopyExternalRepresentation((__bridge SecKeyRef)bundled[1],NULL))],@"the two bundled keys differ: %@ vs %@",[CFBridgingRelease(SecKeyCopyExternalRepresentation((__bridge SecKeyRef)bundled[0],NULL)) subdataWithRange:NSMakeRange(0,24)],[CFBridgingRelease(SecKeyCopyExternalRepresentation((__bridge SecKeyRef)bundled[1],NULL)) subdataWithRange:NSMakeRange(0,24)]);
        puts("PASS: update source choice keeps the highest verified version (lagging self-hosted cannot hide GitHub)");
        puts("PASS: update manifest signature and tampering, two accepted keys (shared/macOS) and unknown-key rejection, XML key import, version order, redirect allowlist, install location, bundled RSA-3072 keys");
    } return 0;
}
static NSDictionary *UsageDemo(NSNumber *five, NSNumber *week) {
    NSDate *now = NSDate.date;
    NSMutableDictionary *record = [@{@"status":@"界面已读取",@"fh":@{@"percent":five,@"reset":@"周四 01:10 重置",@"observedAt":now,@"visible":@YES}} mutableCopy];
    if (week) record[@"sd"] = @{@"percent":week,@"reset":@"周三 19:00 重置",@"observedAt":[now dateByAddingTimeInterval:-120],@"visible":@NO};
    return record;
}
// Native target/action checks and offscreen renders use an isolated preference suite and synthetic accounts only.
static int UISettingsTest(NSString *output) {
    [NSApplication sharedApplication];
    NSString *suite = [@"local.claude.settings-test." stringByAppendingString:NSUUID.UUID.UUIDString];
    NSUserDefaults *preferences = [[NSUserDefaults alloc] initWithSuiteName:suite];
    Launcher *launcher = [Launcher new]; launcher.snapshotMode = YES; launcher.preferences = preferences;
    launcher.settings = DefaultSettings(); launcher.settingsDraft = launcher.settings.mutableCopy; launcher.accountNames = [NSMutableDictionary dictionary];
    @try {
        NSCAssert([NSFileManager.defaultManager createDirectoryAtPath:output withIntermediateDirectories:YES attributes:nil error:nil],@"snapshot folder");
        [launcher makeWindow]; [launcher applySettings];
        launcher.usageInterface = @{@1:UsageDemo(@62,@70),@2:UsageDemo(@73,nil)};
        [launcher updateUsageCards];
        NSCAssert([launcher.cards[0].usageLabels[0].stringValue isEqual:@"62%"] &&
            [launcher.cards[0].usageSource.stringValue isEqual:@"Claude 界面 · 非服务器查询"] &&
            [launcher.cards[0].usageTimes[1].stringValue hasPrefix:@"上次显示"] &&
            [launcher.cards[1].usageLabels[1].stringValue isEqual:@"—"],@"account-specific interface values, individual times and missing weekly reach native controls");
        NSCAssert([launcher.cards[1].usageLabels[0].stringValue isEqual:@"73%"],@"account two does not inherit account one's quota");
        launcher.loginAccount = 1; [launcher updateUsageCards];
        NSCAssert([launcher.cards[0].usageLabels[0].stringValue isEqual:@"—"],@"login protection hides interface observations");
        launcher.loginAccount = 0; launcher.usageInterface = nil; [launcher updateUsageCards];
        void (^capture)(NSString *) = ^(NSString *name) {
            NSView *view = launcher.window.contentView; [view layoutSubtreeIfNeeded];
            NSBitmapImageRep *bitmap = [view bitmapImageRepForCachingDisplayInRect:view.bounds]; [view cacheDisplayInRect:view.bounds toBitmapImageRep:bitmap];
            NSCAssert([[bitmap representationUsingType:NSBitmapImageFileTypePNG properties:@{}] writeToFile:[output stringByAppendingPathComponent:name] atomically:YES],@"snapshot saved");
        };
        [launcher showPage:4];
        for (NSInteger section=0;section<6;section++) {
            [launcher.settingsTabs[section] performClick:nil]; NSCAssert(launcher.settingsSection == section,@"settings tab action");
            capture([NSString stringWithFormat:@"settings-%ld.png",(long)section]);
        }
        [launcher.settingsTabs[0] performClick:nil];
        NSButton *toggle = nil;
        for (NSView *view in launcher.settingsContent.subviews) if ([view.identifier isEqual:@"closeToBackground"]) toggle = (NSButton *)view;
        NSCAssert(toggle && toggle.state == NSControlStateValueOn,@"real preference control");
        [toggle performClick:nil];
        NSCAssert(![launcher.settingsDraft[@"closeToBackground"] boolValue] && [launcher.settings[@"closeToBackground"] boolValue],@"draft does not apply before save");
        [launcher.saveSettingsButton performClick:nil];
        NSCAssert([launcher applicationShouldTerminateAfterLastWindowClosed:NSApp],@"close-window preference takes effect");
        NSUserDefaults *reopened = [[NSUserDefaults alloc] initWithSuiteName:suite];
        NSCAssert(![ValidatedSettings([reopened objectForKey:@"localSettings"])[@"closeToBackground"] boolValue],@"saved preference reload");
        [launcher.settingsTabs[2] performClick:nil];
        for (NSView *view in launcher.settingsContent.subviews) if ([view.identifier isEqual:@"showArtwork"]) [(NSButton *)view performClick:nil];
        [launcher discardSettings:nil]; NSCAssert([launcher.settingsDraft isEqual:launcher.settings],@"cancel unsaved edits");
        [preferences setInteger:3 forKey:@"loginProtectionAccount"]; [preferences setObject:@{@"2":@"本地别名"} forKey:@"accountDisplayNames"];
        launcher.settingsDraft = DefaultSettings().mutableCopy; [launcher saveSettings:nil];
        NSCAssert([preferences integerForKey:@"loginProtectionAccount"] == 3 && [[preferences objectForKey:@"accountDisplayNames"] count] == 1,@"reset settings preserves unrelated local state");
        launcher.settingsDraft[@"showArtwork"] = @NO; launcher.settingsDraft[@"showUsageHistory"] = @NO; launcher.settingsDraft[@"logLimit"] = @100;
        [launcher saveSettings:nil]; NSCAssert(!launcher.cards[0].art && !launcher.usageInterface,@"appearance and interface settings applied");
        launcher.settingsDraft[@"localNotifications"] = @NO; [launcher saveSettings:nil]; [launcher testNotification:nil]; NSCAssert(launcher.toast.hidden,@"notification off");
        launcher.settingsDraft[@"localNotifications"] = @YES; [launcher saveSettings:nil]; [launcher testNotification:nil]; NSCAssert(!launcher.toast.hidden,@"notification on"); launcher.toast.hidden = YES;
        [launcher.logEntries removeAllObjects];
        for (NSInteger i=0;i<125;i++) [launcher addLog:[NSString stringWithFormat:@"账号 02 本地测试 %ld",(long)i]];
        NSCAssert(launcher.logEntries.count == 100 && launcher.visibleLogs.count == 10,@"bounded logs and first page");
        [launcher.nextLogs performClick:nil]; NSCAssert(launcher.logPage == 1,@"next page action");
        [launcher.previousLogs performClick:nil]; NSCAssert(launcher.logPage == 0,@"previous page action");
        launcher.logSearch.stringValue = @"测试 124"; [launcher controlTextDidChange:[NSNotification notificationWithName:NSControlTextDidChangeNotification object:launcher.logSearch]];
        NSCAssert(launcher.visibleLogs.count == 1 && !launcher.nextLogs.enabled,@"search resets and clamps paging");
        [launcher.logLevel selectItemWithTitle:@"错误"]; [launcher logFilterChanged:nil]; NSCAssert(!launcher.visibleLogs.count,@"combined empty filter");
        [launcher.logLevel selectItemAtIndex:0]; launcher.logSearch.stringValue = @""; [launcher logFilterChanged:nil]; [launcher showPage:3]; capture(@"logs.png");
        for (NSInteger n=1;n<=10;n++) launcher.cards[n-1].hidden = n > 3;
        launcher.processes = @{@2:@[@123]}; launcher.accountNames[@"2"] = @"开发工作账号"; [launcher refreshAccountTable];
        [launcher.selectAllAccounts performClick:nil]; NSCAssert(launcher.selectedAccounts.count == 3,@"select visible accounts");
        [launcher.rows[0].selection performClick:nil]; NSCAssert(launcher.selectedAccounts.count == 2 && launcher.selectAllAccounts.state == NSControlStateValueMixed,@"row selection and mixed state");
        [launcher.accountFilter selectItemWithTitle:@"运行中"]; [launcher accountFilterChanged:nil];
        NSCAssert(launcher.rows[0].hidden && !launcher.rows[1].hidden && launcher.rows[2].hidden && launcher.selectedAccounts.count == 1,@"status filtering removes hidden selection");
        launcher.accountSearch.stringValue = @"missing"; [launcher controlTextDidChange:[NSNotification notificationWithName:NSControlTextDidChangeNotification object:launcher.accountSearch]];
        NSCAssert(launcher.rows[1].hidden && !launcher.selectedPathsButton.enabled,@"empty search disables batch copy");
        launcher.accountSearch.stringValue = @"开发"; [launcher accountFilterChanged:nil]; NSCAssert(!launcher.rows[1].hidden,@"local alias searchable");
        launcher.accountSearch.stringValue = @""; [launcher.accountFilter selectItemAtIndex:0]; [launcher refreshAccountTable]; [launcher showPage:1]; capture(@"accounts.png");
        NetworkEnabled = NO; __block BOOL refused = NO;
        Fetch([NSURL URLWithString:[GitHubDownloads stringByAppendingString:@"latest-macos.json"]],YES,100,1,nil,^(NSData *data,NSString *failure) { refused = !data && failure.length; });
        NSCAssert(refused,@"disabled network rejected before session creation");
        NetworkEnabled = YES; refused = NO;
        Fetch([NSURL URLWithString:@"https://api.anthropic.com/v1/messages"],YES,100,1,nil,^(NSData *data,NSString *failure) { refused = !data && failure.length; });
        NSCAssert(refused,@"Claude requests rejected even with update opt-in"); NetworkEnabled = NO;
        NSCAssert(!launcher.timer && !launcher.statusItem && !launcher.projectStore,@"UI checks do not start normal lifecycle or load projects");
        puts("PASS: native settings controls, isolated save/reload, draft cancellation, reset scope, artwork/interface-usage/notifications, log retention/filter/paging, account search/status/selection, network rejection before session creation; 8 offscreen renders");
    } @finally {
        NetworkEnabled = NO; [preferences removePersistentDomainForName:suite]; [preferences synchronize];
    }
    return 0;
}
int main(int argc, const char *argv[]) {
    @autoreleasepool {
        if (argc > 2 && strcmp(argv[1],"--ui-self-test") == 0) return UISettingsTest(@(argv[2]));
        if (argc > 1 && strcmp(argv[1],"--self-test") == 0) return SelfTest();
        if (argc > 1 && strcmp(argv[1],"--usage-history-check") == 0) {
            fputs("Historical usage reading is retired; use --usage-interface-check.\n",stderr); return 2;
        }
        if (argc > 1 && strcmp(argv[1],"--usage-interface-check") == 0) {
            NSString *failure = nil; NSDictionary *processes = Scan(&failure);
            if (failure) { puts("process validation failed"); return 1; }
            for (NSInteger n=1;n<=10;n++) {
                NSArray *identity = UsageIdentity(n,processes[@(n)]); if (!identity) continue;
                NSTimeInterval start = NSProcessInfo.processInfo.systemUptime;
                NSDictionary *result = ReadUsageInterface([[processes[@(n)] firstObject] intValue]);
                if (![identity isEqual:UsageIdentity(n,processes[@(n)])]) result = @{@"status":@"账号状态变化"};
                printf("account=%02ld status=%s five_hour=%s weekly=%s read_ms=%.3f\n",(long)n,[result[@"status"] UTF8String],result[@"fh"] ? "observed" : "absent",result[@"sd"] ? "observed" : "absent",(NSProcessInfo.processInfo.systemUptime-start)*1000);
            }
            return 0;
        }
        if (argc > 1 && strcmp(argv[1],"--diagnose") == 0) {
            NSString *failure=nil; NSDictionary *pids=Scan(&failure);
            if (failure) { puts(failure.UTF8String); return 1; }
            for (NSNumber *n in [[pids allKeys] sortedArrayUsingSelector:@selector(compare:)]) printf("account=%02d pids=%s\n",n.intValue,[pids[n] description].UTF8String);
            printf("accessibility=%s\n",AXIsProcessTrusted()?"yes":"no"); return 0;
        }
        if (argc > 1 && strcmp(argv[1],"--update-test") == 0) {
            // Development aid: run the real update path without dialogs (accepts the offer). Use on a test copy, not the daily app.
            if (argc < 3 || strcmp(argv[2],"--allow-updates") != 0) { fputs("Explicit --allow-updates required; no request sent.\n",stderr); return 2; }
            setvbuf(stdout,NULL,_IOLBF,0); TraceNetwork = YES; NetworkEnabled = YES;
            NSApplication *app = NSApplication.sharedApplication; app.activationPolicy = NSApplicationActivationPolicyProhibited;
            Launcher *launcher = [Launcher new]; launcher.headless = YES;
            launcher.testCancel = argc > 3 && strcmp(argv[3],"--cancel") == 0;
            printf("[test] %s %s\n",NSBundle.mainBundle.bundlePath.UTF8String,[[NSBundle.mainBundle objectForInfoDictionaryKey:@"CFBundleShortVersionString"] UTF8String]);
            dispatch_async(dispatch_get_main_queue(), ^{ [launcher runUpdateCheck:YES]; });
            [app run]; return 0;
        }
        if (argc > 2 && strcmp(argv[1],"--check-signer") == 0) {
            // Development aid: would this app accept the given bundle as an update (same designated requirement)?
            NSString *failure = SameSigner(@(argv[2]));
            puts(failure ? failure.UTF8String : "same signer"); return failure ? 1 : 0;
        }
        if (argc > 1 && strcmp(argv[1],"--register-projects-from") == 0) {
            NSInteger source = argc >= 4 ? [@(argv[2]) integerValue] : 0, target = argc >= 4 ? [@(argv[3]) integerValue] : 0;
            BOOL apply = argc == 5 && strcmp(argv[4],"--apply") == 0;
            if ((argc != 4 && !apply) || source < 1 || source > 10 || target < 1 || target > 10 || source == target ||
                ![@(argv[2]) isEqual:[NSString stringWithFormat:@"%ld",(long)source]] || ![@(argv[3]) isEqual:[NSString stringWithFormat:@"%ld",(long)target]]) {
                fputs("usage: --register-projects-from <1..10> <different target 1..10> [--apply]\n",stderr); return 2;
            }
            NSDictionary *found = DiscoverProjects(@[ProfilePath(source)],RegistrationValidator(),NSHomeDirectory());
            if ([found[@"limited"] boolValue]) { fputs("source discovery reached its limit; no write\n",stderr); return 1; }
            NSArray *paths = [found[@"candidates"] valueForKey:@"path"];
            NSDictionary *result = RegisterForAccount(target,paths,apply);
            printf("source=%02ld target=%02ld mode=%s candidates=%lu planned=%lu added=%lu skipped=%lu\n",(long)source,(long)target,apply ? "apply" : "preview",(unsigned long)paths.count,[result[@"planned"] unsignedLongValue],[result[@"added"] unsignedLongValue],[result[@"skipped"] unsignedLongValue]);
            if (result[@"error"]) { puts([result[@"error"] UTF8String]); return 1; }
            puts("Only directory paths and fresh empty archive records; enable Show empty groups in Claude."); return 0;
        }
        if (argc > 1 && strcmp(argv[1],"--project-import-check") == 0) {
            NSMutableArray *profiles = [NSMutableArray array];
            for (NSInteger n = 1; n <= 10; n++) [profiles addObject:ProfilePath(n)];
            ProjectStore *validator = [[ProjectStore alloc] initWithDirectory:[NSHomeDirectory() stringByAppendingPathComponent:@"Library/Application Support/Claude Launcher"] home:NSHomeDirectory() applicationPaths:@[NSBundle.mainBundle.bundlePath,@"/Applications/Claude 启动器.app",[NSHomeDirectory() stringByAppendingPathComponent:@"Applications/Claude 启动器.app"]]];
            NSDictionary *result = DiscoverProjects(profiles,validator,NSHomeDirectory());
            printf("created=%lu candidates=%lu limited=%s\n",[result[@"created"] unsignedLongValue],(unsigned long)[result[@"candidates"] count],[result[@"limited"] boolValue] ? "yes" : "no");
            for (NSString *line in result[@"summaries"]) puts(line.UTF8String);
            return 0;
        }
        if (argc > 2 && strcmp(argv[1],"--snapshot") == 0) {
            // Development aid: render the window offscreen to PNG without taking the instance lock or launching Claude.
            [NSApplication sharedApplication];
            Launcher *launcher = [Launcher new];
            launcher.snapshotMode = YES;
            if (argc > 3) launcher.page = atoi(argv[3]);
            // Purely visual fixtures: do not load or write the user's project list.
            if (argc > 4 && strcmp(argv[4],"--projects-demo") == 0) launcher.snapshotProjects = @[
                @{@"name":@"网站项目",@"group":@"工作",@"path":@"/Users/example/Projects/Website",@"lastAccount":@2,@"previewStatus":@"正常"},
                @{@"name":@"较长的项目名称用于检查表格截断",@"group":@"个人学习资料",@"path":@"/Users/example/Documents/一个很长的目录名称/研究项目/Notes",@"lastAccount":@1,@"previewStatus":@"不存在"},
                @{@"name":@"归档资料",@"group":@"",@"path":@"/Volumes/Archive/Research",@"lastAccount":@0,@"previewStatus":@"无权限"}];
            [launcher makeWindow]; [launcher applySettings]; [launcher refresh];
            if (argc > 4 && strcmp(argv[4],"--usage-demo") == 0) {
                launcher.usageInterface = @{@1:UsageDemo(@62,@70),@2:UsageDemo(@73,nil)};
                [launcher updateUsageCards];
            }
            NSView *view = launcher.window.contentView;
            NSAlert *importPreview = nil;
            if (argc > 4 && strcmp(argv[4],"--import-demo") == 0) {
                importPreview = [launcher projectImportAlert:@{@"summaries":@[@"账号 01：路径 5 · 格式跳过 1 · 读取失败 0 · 失效/受限 1",@"账号 02：路径 3 · 格式跳过 0 · 读取失败 0 · 失效/受限 0",@"账号 03：未找到支持的本地记录，不代表没有项目"],@"candidates":@[
                    @{@"path":@"/Users/example/Projects/Website",@"accounts":@[@1,@2]},
                    @{@"path":@"/Users/example/Documents/一个较长的本地项目目录/Notes",@"accounts":@[@2]}]} checks:[NSMutableArray array]];
                [importPreview layout]; view = importPreview.window.contentView;
            }
            if (argc > 4 && strcmp(argv[4],"--update-demo") == 0) {
                NSInteger stage = argc > 5 ? atoi(argv[5]) : 0;
                [launcher showUpdateProgress:@"1.7.8"];
                NSArray *details = @[@"从 GitHub 下载 · 1.2 / 1.7 MB · 70%",@"正在校验安装包：签名、版本与证书…",@"校验通过。启动器即将关闭，安装完成后会自动重新打开。Claude 不受影响。"];
                [launcher updateStep:stage detail:details[MIN(MAX(stage,0),2)] progress:stage == 0 ? 0.7 : -1];
                view = launcher.updateWindow.contentView;
            }
            [view layoutSubtreeIfNeeded];
            NSBitmapImageRep *bitmap = [view bitmapImageRepForCachingDisplayInRect:view.bounds];
            [view cacheDisplayInRect:view.bounds toBitmapImageRep:bitmap];
            return [[bitmap representationUsingType:NSBitmapImageFileTypePNG properties:@{}] writeToFile:@(argv[2]) atomically:YES] ? 0 : 1;
        }
        NSApplication *app = NSApplication.sharedApplication; app.activationPolicy=NSApplicationActivationPolicyRegular;
        Launcher *delegate=[Launcher new]; app.delegate=delegate; [app run];
    } return 0;
}
