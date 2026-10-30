#import "ProjectImport.h"
#import "Localization.h"
#import <sys/stat.h>
#import <fcntl.h>
#import <unistd.h>
#import <dirent.h>
#import <errno.h>
#import <sys/file.h>
#import <stdio.h>

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
        NSString *label = [NSString stringWithFormat:L(@"账号 %02lu"),(unsigned long)i+1];
        int profile = ProfileDirectory(profiles[i]);
        if (profile < 0) { if (errno != ENOENT) [summaries addObject:[label stringByAppendingString:L(@"：目录无法安全访问，未识别")]]; continue; }
        created++;
        if (counts.limited) { close(profile); [summaries addObject:[label stringByAppendingString:L(@"：已达扫描上限，未识别")]]; continue; }
        int root = ChildDirectory(profile,@"claude-code-sessions"); int problem = errno; close(profile);
        if (root < 0) { [summaries addObject:[label stringByAppendingString:problem == ENOENT ? L(@"：未找到支持的本地记录，不代表没有项目") : L(@"：记录目录不可安全访问，未识别")]]; continue; }
        ScanCounts previous = counts;
        ScanLevel(root,0,i+1,validator,home,candidates,&counts); close(root);
        [summaries addObject:[NSString stringWithFormat:L(@"%@：路径 %lu · 格式跳过 %lu · 读取失败 %lu · 失效/受限 %lu%@"),label,
            (unsigned long)(counts.matched-previous.matched),(unsigned long)(counts.format-previous.format),
            (unsigned long)(counts.unreadable-previous.unreadable),(unsigned long)(counts.rejected-previous.rejected),counts.limited ? L(@" · 达到上限") : @""]];
        if (counts.rejected > previous.rejected) [summaries addObject:[NSString stringWithFormat:L(@"  跳过详情：不存在 %lu · 无权限 %lu · 路径受限 %lu · 需手动添加 %lu"),
            (unsigned long)(counts.missing-previous.missing),(unsigned long)(counts.denied-previous.denied),
            (unsigned long)(counts.protected-previous.protected),(unsigned long)(counts.manual-previous.manual)]];
    }
    if (!summaries.count) [summaries addObject:L(@"未找到已创建的账号目录。")];
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

static int UniqueRegistrationChild(int parent, NSString **name) {
    int copy = dup(parent); DIR *stream = copy < 0 ? NULL : fdopendir(copy);
    if (!stream) { if (copy >= 0) close(copy); return -1; }
    int found = -1; BOOL failed = NO; NSUInteger entries = 0;
    struct dirent *entry;
    while (YES) {
        errno = 0; entry = readdir(stream);
        if (!entry) { if (errno) failed = YES; break; }
        NSString *part = [NSString stringWithUTF8String:entry->d_name];
        if ([part isEqual:@"."] || [part isEqual:@".."]) continue;
        struct stat info;
        if (!part || ++entries > 20000 || fstatat(parent,entry->d_name,&info,AT_SYMLINK_NOFOLLOW) != 0) { failed = YES; break; }
        if (!UUIDString(part)) {
            if (S_ISDIR(info.st_mode) || S_ISLNK(info.st_mode)) { failed = YES; break; }
            continue;
        }
        if (found >= 0) { failed = YES; break; }
        found = ChildDirectory(parent,part);
        if (found < 0) { failed = YES; break; }
        if (name) *name = part;
    }
    closedir(stream);
    if (failed && found >= 0) { close(found); found = -1; }
    return found;
}
static int RegistrationDirectory(NSString *profile) {
    int fd = ProfileDirectory(profile); if (fd < 0) return -1;
    int next = ChildDirectory(fd,@"claude-code-sessions"); close(fd); fd = next;
    for (NSUInteger level = 0; fd >= 0 && level < 2; level++) {
        next = UniqueRegistrationChild(fd,NULL); close(fd); fd = next;
    }
    return fd;
}
static BOOL SameFile(struct stat a, struct stat b) {
    return a.st_dev == b.st_dev && a.st_ino == b.st_ino && a.st_size == b.st_size &&
        a.st_mtimespec.tv_sec == b.st_mtimespec.tv_sec && a.st_mtimespec.tv_nsec == b.st_mtimespec.tv_nsec &&
        a.st_ctimespec.tv_sec == b.st_ctimespec.tv_sec && a.st_ctimespec.tv_nsec == b.st_ctimespec.tv_nsec;
}
static BOOL RegistrationPaths(int directory, NSMutableSet *seen) {
    int copy = dup(directory); DIR *stream = copy < 0 ? NULL : fdopendir(copy);
    if (!stream) { if (copy >= 0) close(copy); return NO; }
    BOOL ok = YES; NSUInteger entries = 0, records = 0; struct dirent *entry;
    struct stat initial, final;
    if (fstat(directory,&initial) != 0) { closedir(stream); return NO; }
    while (YES) {
        errno = 0; entry = readdir(stream);
        if (!entry) { if (errno) ok = NO; break; }
        NSString *name = [NSString stringWithUTF8String:entry->d_name];
        if (!name || ++entries > 20000) { ok = NO; break; }
        if (![name hasPrefix:@"local_"] || ![name hasSuffix:@".json"]) continue;
        if (!SessionName(name.stringByDeletingPathExtension) || ++records > 5000) { ok = NO; break; }
        int file = openat(directory,entry->d_name,O_RDONLY|O_NOFOLLOW|O_NONBLOCK|O_CLOEXEC);
        struct stat before, after, current;
        if (file < 0 || fstat(file,&before) != 0 || !S_ISREG(before.st_mode) || before.st_uid != getuid() ||
            before.st_nlink != 1 || (before.st_mode & 0022)) { if (file >= 0) close(file); ok = NO; break; }
        NSString *path = ReadPathPrefix(file,name.stringByDeletingPathExtension);
        ok = path && fstat(file,&after) == 0 && SameFile(before,after) &&
            fstatat(directory,entry->d_name,&current,AT_SYMLINK_NOFOLLOW) == 0 && SameFile(after,current);
        close(file);
        if (!ok) break;
        if (path.isAbsolutePath) [seen addObject:Identity(path.stringByStandardizingPath.stringByResolvingSymlinksInPath)];
    }
    ok = ok && fstat(directory,&final) == 0 && SameFile(initial,final);
    closedir(stream); return ok;
}
static NSData *RegistrationData(NSString *identifier, NSString *path) {
    NSNumber *now = @((long long)(NSDate.date.timeIntervalSince1970 * 1000));
    NSArray *keys = @[@"sessionId",@"cwd",@"originCwd",@"title",@"createdAt",@"lastActivityAt",@"isArchived"];
    NSArray *values = @[identifier,path,path,@"项目目录入口（无对话）",now,now,@YES];
    NSMutableArray *fields = [NSMutableArray array];
    // Keep the directory prefix readable without ever inspecting later fields on a future scan.
    for (NSUInteger i = 0; i < keys.count; i++) {
        NSData *json = [NSJSONSerialization dataWithJSONObject:values[i] options:NSJSONWritingFragmentsAllowed error:nil];
        if (!json) return nil;
        [fields addObject:[NSString stringWithFormat:@"\"%@\":%@",keys[i],[[NSString alloc] initWithData:json encoding:NSUTF8StringEncoding]]];
    }
    return [[NSString stringWithFormat:@"{%@}",[fields componentsJoinedByString:@","]] dataUsingEncoding:NSUTF8StringEncoding];
}
NSDictionary *RegisterProjectDirectories(NSString *profile, NSArray<NSString *> *paths, ProjectStore *validator,
                                        NSString *home, BOOL apply, NSString *(^guard)(void)) {
    NSUInteger added = 0, skipped = 0, planned = 0; int directory = -1;
    NSString *failure = guard ? guard() : L(@"缺少进程安全检查，未登记。");
    NSMutableArray *selected = [NSMutableArray array]; NSMutableSet *seen = [NSMutableSet set];
    if (!failure && paths.count > 1000) failure = L(@"一次最多登记 1000 个目录。");
    if (!failure) {
        directory = RegistrationDirectory(profile);
        if (directory < 0) failure = L(@"目标没有唯一且安全的账号／组织记录目录。请先在该账号正常创建一次本地会话；多位置时不猜测身份。");
        else if (flock(directory,LOCK_EX|LOCK_NB) != 0) failure = L(@"另一个登记操作正在使用目标目录，请稍后重试。");
        else if (!RegistrationPaths(directory,seen)) failure = L(@"目标记录无法完整、安全地读取目录前缀（格式、权限或扫描上限），未登记。");
    }
    if (!failure) for (NSString *path in paths) {
        NSString *normal = [validator normalizeDirectory:path error:nil];
        if (!normal || ![normal isEqual:path] || ProjectNeedsManualImport(normal,home)) { failure = L(@"包含失效、受保护、非真实路径或范围过大的目录，请刷新后重新选择。"); break; }
        NSString *identity = Identity(normal);
        if ([seen containsObject:identity]) { skipped++; continue; }
        [seen addObject:identity]; [selected addObject:@{@"path":normal,@"identity":identity}];
    }
    if (!failure) planned = selected.count;
    if (!failure && apply) for (NSDictionary *selection in selected) {
        NSString *path = selection[@"path"];
        NSString *identifier = [@"local_" stringByAppendingString:NSUUID.UUID.UUIDString.lowercaseString];
        NSString *name = [identifier stringByAppendingString:@".json"];
        NSString *temporary = [@".claude-launcher-project-" stringByAppendingString:NSUUID.UUID.UUIDString];
        NSData *data = RegistrationData(identifier,path);
        if (!data) { failure = L(@"无法生成空记录。"); break; }
        failure = guard(); if (failure) break;
        int file = openat(directory,temporary.fileSystemRepresentation,O_WRONLY|O_CREAT|O_EXCL|O_NOFOLLOW|O_CLOEXEC,0600);
        if (file < 0) { failure = L(@"无法独占创建空记录临时文件。"); break; }
        const char *bytes = data.bytes; NSUInteger written = 0;
        while (written < data.length) {
            ssize_t n = write(file,bytes+written,data.length-written);
            if (n < 0 && errno == EINTR) continue;
            if (n <= 0) break;
            written += (NSUInteger)n;
        }
        BOOL ready = written == data.length && fsync(file) == 0; close(file);
        if (!ready) failure = L(@"空记录写入失败，原有文件未覆盖。");
        if (!failure) failure = guard();
        int current = RegistrationDirectory(profile); struct stat held, fresh;
        BOOL same = current >= 0 && fstat(directory,&held) == 0 && fstat(current,&fresh) == 0 &&
            held.st_dev == fresh.st_dev && held.st_ino == fresh.st_ino;
        if (current >= 0) close(current);
        NSString *normal = [validator normalizeDirectory:path error:nil];
        if (!failure && (!same || ![normal isEqual:path] || ![Identity(path) isEqual:selection[@"identity"]]))
            failure = L(@"目标存储或项目目录在检查期间变化，已停止后续登记。");
        if (!failure && renameatx_np(directory,temporary.fileSystemRepresentation,directory,name.fileSystemRepresentation,RENAME_EXCL) != 0)
            failure = L(@"无法原子发布新记录，未覆盖任何同名文件。");
        unlinkat(directory,temporary.fileSystemRepresentation,0);
        if (failure) break;
        added++; fsync(directory);
    }
    if (directory >= 0) close(directory);
    NSMutableDictionary *result = [@{@"added":@(added),@"skipped":@(skipped),@"planned":@(planned)} mutableCopy];
    if (failure) result[@"error"] = failure;
    return result;
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

void TestProjectRegistration(void) {
    NSFileManager *fm = NSFileManager.defaultManager;
    NSString *root = [fm.currentDirectoryPath stringByAppendingPathComponent:[@".claude-registration-tests-" stringByAppendingString:NSUUID.UUID.UUIDString]];
    NSString *home = [root stringByAppendingPathComponent:@"home"], *support = [home stringByAppendingPathComponent:@"Library/Application Support"];
    NSString *profile = [support stringByAppendingPathComponent:@"Claude-Second"];
    NSString *account = [[profile stringByAppendingPathComponent:@"claude-code-sessions"] stringByAppendingPathComponent:NSUUID.UUID.UUIDString];
    NSString *directory = [account stringByAppendingPathComponent:NSUUID.UUID.UUIDString];
    NSString *project = [root stringByAppendingPathComponent:@"项目-\"-目录"], *second = [root stringByAppendingPathComponent:@"Second"];
    void (^mkdir)(NSString *) = ^(NSString *p) { NSCAssert([fm createDirectoryAtPath:p withIntermediateDirectories:YES attributes:@{NSFilePosixPermissions:@0700} error:nil],@"synthetic directory"); };
    mkdir(directory); mkdir(project); mkdir(second);
    ProjectStore *validator = [[ProjectStore alloc] initWithDirectory:[support stringByAppendingPathComponent:@"Claude Launcher"] home:home applicationPaths:@[]];
    NSString *(^safe)(void) = ^NSString *{ return nil; };
    NSString *marker = [profile stringByAppendingPathComponent:@"Cookies"];
    NSData *sentinel = [@"SYNTHETIC LOGIN MARKER NEVER COPIED" dataUsingEncoding:NSUTF8StringEncoding];
    NSCAssert([sentinel writeToFile:marker atomically:NO],@"synthetic marker");
    NSDictionary *r = RegisterProjectDirectories(profile,@[project,second],validator,home,NO,safe);
    NSCAssert(!r[@"error"] && [r[@"planned"] integerValue] == 2 && [fm contentsOfDirectoryAtPath:directory error:nil].count == 0,@"preview never writes");
    r = RegisterProjectDirectories(profile,@[project],validator,home,YES,^NSString *{ return @"running"; });
    NSCAssert(r[@"error"] && [fm contentsOfDirectoryAtPath:directory error:nil].count == 0,@"running target refused");
    r = RegisterProjectDirectories(profile,@[project,profile],validator,home,YES,safe);
    NSCAssert(r[@"error"] && [r[@"added"] integerValue] == 0,@"invalid path aborts before any writes");
    __block NSUInteger calls = 0;
    r = RegisterProjectDirectories(profile,@[project,second],validator,home,YES,^NSString *{ return ++calls == 4 ? @"started during batch" : nil; });
    NSCAssert(r[@"error"] && [r[@"added"] integerValue] == 1,@"partial results survive and are reported");
    r = RegisterProjectDirectories(profile,@[project,second,second],validator,home,YES,safe);
    NSCAssert(!r[@"error"] && [r[@"added"] integerValue] == 1 && [r[@"skipped"] integerValue] == 2,@"retry and input duplicates are idempotent");
    NSArray *files = [fm contentsOfDirectoryAtPath:directory error:nil];
    NSCAssert(files.count == 2,@"only two final records, no temp files");
    NSSet *allowed = [NSSet setWithArray:@[@"sessionId",@"cwd",@"originCwd",@"title",@"createdAt",@"lastActivityAt",@"isArchived"]];
    NSMutableSet *ids = [NSMutableSet set];
    for (NSString *file in files) {
        NSString *full = [directory stringByAppendingPathComponent:file];
        NSDictionary *row = [NSJSONSerialization JSONObjectWithData:[NSData dataWithContentsOfFile:full] options:0 error:nil];
        struct stat info; NSCAssert(lstat(full.fileSystemRepresentation,&info) == 0 && (info.st_mode & 0777) == 0600 && info.st_nlink == 1,@"private single-link record");
        NSCAssert([[NSSet setWithArray:row.allKeys] isEqual:allowed] && [row[@"isArchived"] boolValue] &&
            [row[@"title"] isEqual:@"项目目录入口（无对话）"] && [row[@"cwd"] isEqual:row[@"originCwd"]],@"exact allowlist and fixed empty title");
        NSCAssert(SessionName(row[@"sessionId"]) && ![ids containsObject:row[@"sessionId"]],@"independent fresh identities"); [ids addObject:row[@"sessionId"]];
        int fd = open(full.fileSystemRepresentation,O_RDONLY);
        NSCAssert([ReadPathPrefix(fd,file.stringByDeletingPathExtension) isEqual:row[@"cwd"]],@"generated prefix is compatible with bounded discovery"); close(fd);
    }
    NSCAssert([[NSData dataWithContentsOfFile:marker] isEqual:sentinel],@"synthetic login data remains unchanged");
    NSString *extra = [account stringByAppendingPathComponent:NSUUID.UUID.UUIDString]; mkdir(extra);
    r = RegisterProjectDirectories(profile,@[project],validator,home,YES,safe);
    NSCAssert(r[@"error"],@"multiple organizations never guessed"); [fm removeItemAtPath:extra error:nil];
    NSString *bad = [directory stringByAppendingPathComponent:[NSString stringWithFormat:@"local_%@.json",NSUUID.UUID.UUIDString]];
    NSCAssert([@"{\"unknown\":\"SYNTHETIC SECRET\"}" writeToFile:bad atomically:NO encoding:NSUTF8StringEncoding error:nil],@"unknown synthetic format");
    r = RegisterProjectDirectories(profile,@[project],validator,home,YES,safe);
    NSCAssert(r[@"error"],@"unknown prefix refuses target without reading its value"); [fm removeItemAtPath:bad error:nil];
    NSCAssert([fm createSymbolicLinkAtPath:bad withDestinationPath:[directory stringByAppendingPathComponent:files[0]] error:nil],@"synthetic linked record");
    r = RegisterProjectDirectories(profile,@[project],validator,home,YES,safe); NSCAssert(r[@"error"],@"symlink refused"); [fm removeItemAtPath:bad error:nil];
    NSCAssert(link([directory stringByAppendingPathComponent:files[0]].fileSystemRepresentation,bad.fileSystemRepresentation) == 0,@"synthetic hardlink");
    r = RegisterProjectDirectories(profile,@[project],validator,home,YES,safe); NSCAssert(r[@"error"],@"hardlink refused"); unlink(bad.fileSystemRepresentation);
    int locked = RegistrationDirectory(profile); NSCAssert(locked >= 0 && flock(locked,LOCK_EX|LOCK_NB) == 0,@"hold fixture directory lock");
    r = RegisterProjectDirectories(profile,@[project],validator,home,YES,safe); NSCAssert(r[@"error"],@"concurrent writer refused"); close(locked);
    NSString *third = [root stringByAppendingPathComponent:@"Third"]; mkdir(third); calls = 0;
    r = RegisterProjectDirectories(profile,@[third],validator,home,YES,^NSString *{
        if (++calls == 3) [fm removeItemAtPath:third error:nil]; return nil;
    });
    NSCAssert(r[@"error"] && [r[@"added"] integerValue] == 0 && [fm contentsOfDirectoryAtPath:directory error:nil].count == 2,@"path change before publish leaves no final or temporary record");
    NSCAssert([fm removeItemAtPath:root error:nil],@"clean synthetic registration fixtures");
    puts("PASS: native project registration allowlist, fresh IDs, 0600 atomic writes, preview, duplicate retry, partial progress, process guard, path changes, ambiguous targets and linked records; synthetic fixtures only");
}
