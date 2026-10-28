#import "ProjectImport.h"
#import <sys/stat.h>
#import <fcntl.h>
#import <unistd.h>
#import <dirent.h>
#import <errno.h>

// No buffered reads: reading ahead could consume prompt/configuration fields after the prefix.
typedef struct { int fd; NSUInteger bytes; BOOL failed; } PrefixReader;
static int Next(PrefixReader *r) {
    if (r->bytes >= 32768) { r->failed = YES; return -1; }
    unsigned char c; ssize_t n;
    do { n = read(r->fd,&c,1); } while (n < 0 && errno == EINTR);
    if (n != 1) { r->failed = YES; return -1; }
    r->bytes++; return c;
}
static int Nonspace(PrefixReader *r) {
    int c; do { c = Next(r); } while (c == ' ' || c == '\n' || c == '\r' || c == '\t'); return c;
}
static NSString *JSONString(PrefixReader *r, NSUInteger limit) {
    if (Nonspace(r) != '"') return nil;
    NSMutableData *data = [NSMutableData dataWithBytes:"\"" length:1]; BOOL escaped = NO;
    for (NSUInteger i = 0; i < limit; i++) {
        int value = Next(r); if (value < 0) return nil;
        unsigned char c = value; [data appendBytes:&c length:1];
        if (c == '"' && !escaped) {
            id text = [NSJSONSerialization JSONObjectWithData:data options:NSJSONReadingFragmentsAllowed error:nil];
            return [text isKindOfClass:NSString.class] ? text : nil;
        }
        if (c < 32) return nil;
        escaped = c == '\\' && !escaped;
    }
    return nil;
}
static BOOL UUIDString(NSString *text) { return text.length == 36 && [[NSUUID alloc] initWithUUIDString:text] != nil; }
static BOOL SessionName(NSString *text) {
    return text.length == 42 && [text hasPrefix:@"local_"] && UUIDString([text substringFromIndex:6]);
}
static NSString *ReadPathPrefix(int fd, NSString *session) {
    PrefixReader r = {fd,0,NO};
    if (Nonspace(&r) != '{' || ![JSONString(&r,64) isEqual:@"sessionId"] || Nonspace(&r) != ':') return nil;
    if (![JSONString(&r,64) isEqual:session] || Nonspace(&r) != ',') return nil;
    NSString *key = JSONString(&r,64);
    if ([key isEqual:@"cliSessionId"]) {
        if (Nonspace(&r) != ':' || !UUIDString(JSONString(&r,64)) || Nonspace(&r) != ',') return nil;
        key = JSONString(&r,64);
    }
    if (![key isEqual:@"cwd"] || Nonspace(&r) != ':') return nil;
    NSString *cwd = JSONString(&r,24578);
    if (!cwd || cwd.length > 4096 || Nonspace(&r) != ',' || ![JSONString(&r,64) isEqual:@"originCwd"] || Nonspace(&r) != ':') return nil;
    NSString *origin = JSONString(&r,24578);
    if (!origin || origin.length > 4096) return nil;
    int delimiter = Nonspace(&r);
    if (r.failed || (delimiter != ',' && delimiter != '}')) return nil;
    // Stop here, even if additional fields might contain more folders. Never inspect their values.
    return origin.length ? origin : cwd;
}
static BOOL SafeDirectory(int fd, BOOL owned) {
    struct stat s;
    return fd >= 0 && fstat(fd,&s) == 0 && S_ISDIR(s.st_mode) && (!owned || (s.st_uid == getuid() && !(s.st_mode & 0022)));
}
static int ChildDirectory(int parent, NSString *name) {
    int fd = openat(parent,name.fileSystemRepresentation,O_RDONLY|O_DIRECTORY|O_NOFOLLOW|O_CLOEXEC|O_NONBLOCK);
    if (fd >= 0 && !SafeDirectory(fd,YES)) { close(fd); errno = EPERM; return -1; }
    return fd;
}
static int ProfileDirectory(NSString *path) {
    if (!path.isAbsolutePath || ![path isEqual:path.stringByStandardizingPath]) { errno = EINVAL; return -1; }
    int fd = open("/",O_RDONLY|O_DIRECTORY|O_CLOEXEC);
    for (NSString *part in path.pathComponents) {
        if ([part isEqual:@"/"]) continue;
        int next = openat(fd,part.fileSystemRepresentation,O_RDONLY|O_DIRECTORY|O_NOFOLLOW|O_CLOEXEC|O_NONBLOCK);
        int problem = errno; close(fd); fd = next; errno = problem;
        if (fd < 0) return -1;
        struct stat s;
        // Shared writable ancestors could redirect the read even when not symlinks.
        if (fstat(fd,&s) != 0 || !S_ISDIR(s.st_mode) || (s.st_mode & 0022)) { close(fd); errno = EPERM; return -1; }
    }
    if (!SafeDirectory(fd,YES)) { if (fd >= 0) close(fd); errno = EPERM; return -1; }
    return fd;
}
static NSString *Identity(NSString *path) {
    struct stat s; if (stat(path.fileSystemRepresentation,&s) != 0) return path;
    return [NSString stringWithFormat:@"%llu:%llu",(unsigned long long)s.st_dev,(unsigned long long)s.st_ino];
}
BOOL ProjectNeedsManualImport(NSString *path, NSString *home) {
    if ([path isEqual:@"/"] || [path isEqual:home.stringByResolvingSymlinksInPath]) return YES;
    for (NSString *root in @[@"/tmp",@"/private/tmp",@"/var/tmp",@"/private/var/tmp",@"/var/folders",@"/private/var/folders",NSTemporaryDirectory().stringByResolvingSymlinksInPath]) {
        NSString *normal = [root hasSuffix:@"/"] ? [root substringToIndex:root.length-1] : root;
        if ([path isEqual:normal] || [path hasPrefix:[normal stringByAppendingString:@"/"]]) return YES;
    }
    return NO;
}

typedef struct { NSUInteger entries, records, matched, format, unreadable, rejected, missing, denied, protected, manual; BOOL limited; } ScanCounts;
static void ScanLevel(int directory, NSUInteger level, NSInteger account, ProjectStore *validator, NSString *home,
                      NSMutableDictionary *candidates, ScanCounts *counts) {
    int copy = dup(directory);
    DIR *stream = copy < 0 ? NULL : fdopendir(copy);
    if (!stream) { if (copy >= 0) close(copy); counts->unreadable++; return; }
    struct dirent *entry;
    while (YES) {
        errno = 0; entry = readdir(stream);
        if (!entry) { if (errno) counts->unreadable++; break; }
        NSString *name = [NSString stringWithUTF8String:entry->d_name];
        if (!name || [name isEqual:@"."] || [name isEqual:@".."]) continue;
        if (++counts->entries > 20000 || counts->records >= 5000) { counts->limited = YES; break; }
        if (level < 2) {
            if (!UUIDString(name)) continue;
            int child = ChildDirectory(directory,name);
            if (child < 0) { counts->unreadable++; continue; }
            ScanLevel(child,level+1,account,validator,home,candidates,counts); close(child);
            if (counts->limited) break;
            continue;
        }
        if (![name.pathExtension isEqual:@"json"] || !SessionName(name.stringByDeletingPathExtension)) continue;
        counts->records++;
        int file = openat(directory,name.fileSystemRepresentation,O_RDONLY|O_NOFOLLOW|O_CLOEXEC|O_NONBLOCK);
        struct stat before, after;
        if (file < 0 || fstat(file,&before) != 0 || !S_ISREG(before.st_mode) || before.st_uid != getuid() || before.st_nlink != 1 || (before.st_mode & 0022)) {
            if (file >= 0) close(file); counts->unreadable++; continue;
        }
        NSString *path = ReadPathPrefix(file,name.stringByDeletingPathExtension);
        BOOL stable = fstat(file,&after) == 0 && before.st_size == after.st_size &&
            before.st_mtimespec.tv_sec == after.st_mtimespec.tv_sec && before.st_mtimespec.tv_nsec == after.st_mtimespec.tv_nsec;
        close(file);
        if (!stable) { counts->unreadable++; continue; }
        if (!path) { counts->format++; continue; }
        NSString *failure = nil; path = [validator normalizeDirectory:path error:&failure];
        if (!path || ProjectNeedsManualImport(path,home)) {
            counts->rejected++;
            if (path) counts->manual++;
            else if ([failure isEqual:@"目录不存在"]) counts->missing++;
            else if ([failure isEqual:@"目录无权限"]) counts->denied++;
            else counts->protected++;
            continue;
        }
        counts->matched++;
        NSString *identity = Identity(path);
        NSMutableDictionary *candidate = candidates[identity];
        if (!candidate) { candidate = [@{@"path":path,@"accounts":[NSMutableSet set]} mutableCopy]; candidates[identity] = candidate; }
        [candidate[@"accounts"] addObject:@(account)];
    }
    closedir(stream);
}
NSDictionary *DiscoverProjects(NSArray<NSString *> *profiles, ProjectStore *validator, NSString *home) {
    NSMutableDictionary *candidates = [NSMutableDictionary dictionary]; NSMutableArray *summaries = [NSMutableArray array];
    ScanCounts counts = {0}; NSUInteger created = 0;
    for (NSUInteger i = 0; i < MIN(profiles.count,10); i++) {
        NSString *label = [NSString stringWithFormat:@"账号 %02lu",(unsigned long)i+1];
        int profile = ProfileDirectory(profiles[i]);
        if (profile < 0) { if (errno != ENOENT) [summaries addObject:[label stringByAppendingString:@"：目录无法安全访问，未识别"]]; continue; }
        created++;
        if (counts.limited) { close(profile); [summaries addObject:[label stringByAppendingString:@"：已达扫描上限，未识别"]]; continue; }
        int root = ChildDirectory(profile,@"claude-code-sessions"); int problem = errno; close(profile);
        if (root < 0) { [summaries addObject:[label stringByAppendingString:problem == ENOENT ? @"：未找到支持的本地记录，不代表没有项目" : @"：记录目录不可安全访问，未识别"]]; continue; }
        ScanCounts previous = counts;
        ScanLevel(root,0,i+1,validator,home,candidates,&counts); close(root);
        [summaries addObject:[NSString stringWithFormat:@"%@：路径 %lu · 格式跳过 %lu · 读取失败 %lu · 失效/受限 %lu%@",label,
            (unsigned long)(counts.matched-previous.matched),(unsigned long)(counts.format-previous.format),
            (unsigned long)(counts.unreadable-previous.unreadable),(unsigned long)(counts.rejected-previous.rejected),counts.limited ? @" · 达到上限" : @""]];
        if (counts.rejected > previous.rejected) [summaries addObject:[NSString stringWithFormat:@"  跳过详情：不存在 %lu · 无权限 %lu · 路径受限 %lu · 需手动添加 %lu",
            (unsigned long)(counts.missing-previous.missing),(unsigned long)(counts.denied-previous.denied),
            (unsigned long)(counts.protected-previous.protected),(unsigned long)(counts.manual-previous.manual)]];
    }
    if (!summaries.count) [summaries addObject:@"未找到已创建的账号目录。"];
    NSMutableArray *rows = [NSMutableArray array];
    for (NSDictionary *candidate in candidates.allValues) [rows addObject:@{@"path":candidate[@"path"],@"accounts":[[candidate[@"accounts"] allObjects] sortedArrayUsingSelector:@selector(compare:)]}];
    [rows sortUsingComparator:^NSComparisonResult(NSDictionary *a, NSDictionary *b) { return [a[@"path"] compare:b[@"path"]]; }];
    return @{@"candidates":rows,@"summaries":summaries,@"created":@(created),@"limited":@(counts.limited)};
}
NSArray *PrepareProjectImport(ProjectStore *store, NSArray<NSString *> *paths, NSString *home, NSUInteger *skipped) {
    NSMutableArray *rows = store.projects.mutableCopy; NSMutableSet *seen = [NSMutableSet set]; NSUInteger ignored = 0;
    for (NSDictionary *row in rows) [seen addObject:Identity(row[@"path"])];
    for (NSString *path in paths) {
        NSString *normal = [store normalizeDirectory:path error:nil];
        if (!normal || ![normal isEqual:path] || ProjectNeedsManualImport(normal,home) || [seen containsObject:Identity(normal)]) { ignored++; continue; }
        [seen addObject:Identity(normal)];
        NSString *name = normal.lastPathComponent;
        if (name.length > 200) {
            NSRange last = [name rangeOfComposedCharacterSequenceAtIndex:199];
            name = [name substringToIndex:NSMaxRange(last) <= 200 ? 200 : last.location];
        }
        [rows addObject:@{@"id":NSUUID.UUID.UUIDString,@"name":name,@"group":@"",@"path":normal,@"lastAccount":@0}];
    }
    if (skipped) *skipped = ignored;
    return rows;
}

void TestProjectImport(void) {
    NSFileManager *fm = NSFileManager.defaultManager;
    NSString *root = [fm.currentDirectoryPath stringByAppendingPathComponent:[@".claude-import-tests-" stringByAppendingString:NSUUID.UUID.UUIDString]];
    NSString *home = [root stringByAppendingPathComponent:@"home"], *support = [home stringByAppendingPathComponent:@"Library/Application Support"];
    NSMutableArray *profiles = [NSMutableArray array];
    for (NSInteger n = 1; n <= 10; n++) [profiles addObject:[support stringByAppendingPathComponent:n == 1 ? @"Claude" : n == 2 ? @"Claude-Second" : [NSString stringWithFormat:@"Claude-Profile-%02ld",(long)n]]];
    NSString *project = [root stringByAppendingPathComponent:@"项目-quoted\"-folder"], *second = [root stringByAppendingPathComponent:@"Second"];
    NSString *storePath = [support stringByAppendingPathComponent:@"Claude Launcher"];
    void (^mkdir)(NSString *) = ^(NSString *path) { NSCAssert([fm createDirectoryAtPath:path withIntermediateDirectories:YES attributes:nil error:nil],@"mkdir synthetic"); };
    mkdir(project); mkdir(second); mkdir(profiles[2]);
    ProjectStore *store = [[ProjectStore alloc] initWithDirectory:storePath home:home applicationPaths:@[]]; [store load];
    NSString *session = [@"local_" stringByAppendingString:NSUUID.UUID.UUIDString];
    NSString *(^quoted)(NSString *) = ^NSString *(NSString *value) {
        return [[NSString alloc] initWithData:[NSJSONSerialization dataWithJSONObject:value options:NSJSONWritingFragmentsAllowed error:nil] encoding:NSUTF8StringEncoding];
    };
    NSString *(^prefix)(NSString *) = ^NSString *(NSString *path) {
        return [NSString stringWithFormat:@"{\"sessionId\":%@,\"cliSessionId\":%@,\"cwd\":%@,\"originCwd\":%@,",quoted(session),quoted(NSUUID.UUID.UUIDString),quoted(second),quoted(path)];
    };
    NSMutableArray *sources = [NSMutableArray array];
    for (NSInteger i = 0; i < 2; i++) {
        NSString *directory = [profiles[i] stringByAppendingPathComponent:[NSString stringWithFormat:@"claude-code-sessions/%@/%@",NSUUID.UUID.UUIDString,NSUUID.UUID.UUIDString]];
        mkdir(directory); NSString *file = [directory stringByAppendingPathComponent:[session stringByAppendingString:@".json"]];
        NSString *header = prefix(project);
        NSString *body = [header stringByAppendingString:@"\"prompt\":\"SYNTHETIC MUST NOT BE READ\",\"isArchived\":true}"];
        NSCAssert([body writeToFile:file atomically:NO encoding:NSUTF8StringEncoding error:nil],@"write fixture");
        [sources addObject:file];
        int fd = open(file.fileSystemRepresentation,O_RDONLY);
        NSCAssert([ReadPathPrefix(fd,session) isEqual:project],@"prefer origin; Unicode and escaped path");
        NSCAssert(lseek(fd,0,SEEK_CUR) == (off_t)[header lengthOfBytesUsingEncoding:NSUTF8StringEncoding],@"no single byte of following field is read"); close(fd);
    }
    NSDictionary *found = DiscoverProjects(profiles,store,home);
    NSArray *candidates = found[@"candidates"];
    NSCAssert(([found[@"created"] integerValue] == 3 && candidates.count == 1 && [candidates[0][@"accounts"] isEqual:@[@1,@2]]),@"all created accounts, no running process required, cross-account dedup");
    NSCAssert([found[@"summaries"] count] == 3,@"missing index reported separately");
    NSString *why = nil; NSUInteger skipped = 0;
    NSArray *rows = PrepareProjectImport(store,@[project,project,home,@"/",profiles[0],@"relative"],home,&skipped);
    NSCAssert(rows.count == 1 && skipped == 5 && [rows[0][@"lastAccount"] integerValue] == 0,@"path guards and no account binding");
    NSCAssert(![fm fileExistsAtPath:[storePath stringByAppendingPathComponent:@"projects.json"]],@"discovery/prepare/cancel do not save");
    NSMutableDictionary *custom = [rows[0] mutableCopy]; custom[@"name"] = @"Custom"; custom[@"group"] = @"Keep"; custom[@"lastAccount"] = @7;
    NSCAssert([store save:@[custom] error:&why],@"seed list");
    rows = PrepareProjectImport(store,@[project,second],home,&skipped);
    NSCAssert(rows.count == 2 && skipped == 1 && [rows[0] isEqual:custom],@"preserve name/group/order/last account");
    NSCAssert([store save:rows error:&why],@"atomic batch save"); [store load];
    NSCAssert(store.projects.count == 2 && [PrepareProjectImport(store,@[project,second],home,NULL) isEqual:store.projects],@"round trip idempotent");
    NSCAssert([store save:@[] error:&why] && [fm fileExistsAtPath:project],@"remove imported record leaves files");
    NSString *file = sources[0];
    // Unknown fields must stop before their values; a compatibility change never enables a broader reader.
    NSString *lead = [NSString stringWithFormat:@"{\"sessionId\":%@,\"prompt\"",quoted(session)];
    NSCAssert([[lead stringByAppendingString:@":\"PRIVATE SYNTHETIC TEXT\"}"] writeToFile:file atomically:NO encoding:NSUTF8StringEncoding error:nil],@"unknown field fixture");
    int fd = open(file.fileSystemRepresentation,O_RDONLY);
    NSCAssert(!ReadPathPrefix(fd,session) && lseek(fd,0,SEEK_CUR) == (off_t)[lead lengthOfBytesUsingEncoding:NSUTF8StringEncoding],@"unknown value never read"); close(fd);
    NSArray *invalid = @[@"{}",@"{\"sessionId\":null}",@"{\"sessionId\":\"wrong\"}",[@"{\"sessionId\":\"" stringByPaddingToLength:40000 withString:@"x" startingAtIndex:0]];
    for (NSString *text in invalid) {
        NSCAssert([text writeToFile:file atomically:NO encoding:NSUTF8StringEncoding error:nil],@"invalid fixture"); fd = open(file.fileSystemRepresentation,O_RDONLY);
        NSCAssert(!ReadPathPrefix(fd,session) && lseek(fd,0,SEEK_CUR) <= 32768,@"invalid/bounded header"); close(fd);
    }
    NSCAssert([fm removeItemAtPath:file error:nil] && [fm createSymbolicLinkAtPath:file withDestinationPath:sources[1] error:nil],@"symlink source");
    found = DiscoverProjects(profiles,store,home);
    NSCAssert([found[@"candidates"][0][@"accounts"] isEqual:@[@2]],@"linked source not read");
    NSCAssert([fm removeItemAtPath:file error:nil] && link([sources[1] fileSystemRepresentation],file.fileSystemRepresentation) == 0,@"hardlink source");
    NSCAssert([DiscoverProjects(profiles,store,home)[@"candidates"] count] == 0,@"both multiply linked sources refused");
    NSCAssert([fm removeItemAtPath:file error:nil],@"restore link count");
    NSString *alias = [root stringByAppendingPathComponent:@"alias"];
    NSCAssert([fm createSymbolicLinkAtPath:alias withDestinationPath:profiles[1] error:nil],@"profile alias");
    NSCAssert([DiscoverProjects(@[alias],store,home)[@"candidates"] count] == 0,@"profile symlink refused");
    NSString *gone = [root stringByAppendingPathComponent:@"Gone"]; mkdir(gone); NSCAssert([fm removeItemAtPath:gone error:nil],@"delete synthetic candidate");
    NSCAssert(PrepareProjectImport(store,@[gone],home,&skipped).count == 0 && skipped == 1,@"revalidate disappeared path at commit");
    NSCAssert(ProjectNeedsManualImport(@"/private/tmp/test",home) && ProjectNeedsManualImport(home,home),@"broad and transient directories require manual flow");
    int directory = ProfileDirectory([sources[1] stringByDeletingLastPathComponent]);
    ScanCounts capped = {.entries=20000}; NSMutableDictionary *none = [NSMutableDictionary dictionary];
    ScanLevel(directory,2,2,store,home,none,&capped); close(directory);
    NSCAssert(capped.limited && capped.records == 0 && !none.count,@"enumeration cap reports incomplete without reading records");
    NSString *normalHeader = [NSString stringWithFormat:@"{\"sessionId\":%@,\"cwd\":%@,\"originCwd\":\"\",",quoted(session),quoted(second)];
    NSCAssert([[normalHeader stringByAppendingString:@"\"prompt\":\"PRIVATE SYNTHETIC TEXT\"}"] writeToFile:file atomically:NO encoding:NSUTF8StringEncoding error:nil],@"optional CLI id fixture");
    fd = open(file.fileSystemRepresentation,O_RDONLY);
    NSCAssert([ReadPathPrefix(fd,session) isEqual:second] && lseek(fd,0,SEEK_CUR) == (off_t)[normalHeader lengthOfBytesUsingEncoding:NSUTF8StringEncoding],@"missing cli id and empty origin supported without suffix read"); close(fd);
    NSCAssert(chmod(file.fileSystemRepresentation,0666) == 0,@"synthetic writable source");
    found = DiscoverProjects(profiles,store,home);
    NSCAssert([found[@"candidates"] count] == 1 && [found[@"candidates"][0][@"accounts"] isEqual:@[@2]],@"other-writable record refused");
    NSCAssert(chmod(file.fileSystemRepresentation,0600) == 0,@"restore synthetic permission");
    NSCAssert([fm removeItemAtPath:root error:nil],@"clean import fixtures");
    puts("PASS: project import prefixes stop before bodies, unknown fields refused, all created/offline accounts, dedup, revalidation, cancel/no-write, batch round trip, links and non-destructive removal; synthetic fixtures only");
}
