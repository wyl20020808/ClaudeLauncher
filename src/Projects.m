#import "Projects.h"
#import <sys/stat.h>
#import <fcntl.h>
#import <unistd.h>
#import <errno.h>
#import <stdio.h>

static BOOL Under(NSString *path, NSString *root) {
    return [path caseInsensitiveCompare:root] == NSOrderedSame || [path.lowercaseString hasPrefix:[root.lowercaseString stringByAppendingString:@"/"]];
}
static BOOL AbsoluteStandardPath(id path) {
    return [path isKindOfClass:NSString.class] && [path length] <= 4096 && [path isAbsolutePath] &&
        [path rangeOfString:@"\0"].location == NSNotFound && [path isEqual:[path stringByStandardizingPath]];
}
static BOOL IntegerBetween(id value, NSInteger low, NSInteger high) {
    return [value isKindOfClass:NSNumber.class] && CFGetTypeID((__bridge CFTypeRef)value) != CFBooleanGetTypeID() &&
        [value doubleValue] == [value integerValue] && [value integerValue] >= low && [value integerValue] <= high;
}
static NSString *DirectoryStatus(NSString *path) {
    struct stat info;
    if (stat(path.fileSystemRepresentation,&info) != 0)
        return errno == EACCES || errno == EPERM ? @"无权限" : @"不存在";
    if (!S_ISDIR(info.st_mode)) return @"不存在";
    return access(path.fileSystemRepresentation,R_OK | X_OK) == 0 ? @"正常" : @"无权限";
}

@interface ProjectStore ()
@property(nonatomic) NSString *directory, *home;
@property(nonatomic) NSArray<NSString *> *applications;
@property(nonatomic, readwrite) NSArray<NSDictionary *> *projects;
@property(nonatomic, readwrite) NSString *notice;
@property(nonatomic, readwrite) BOOL writable;
@end

@implementation ProjectStore
- (instancetype)initWithDirectory:(NSString *)directory home:(NSString *)home applicationPaths:(NSArray<NSString *> *)applications {
    if ((self = [super init])) {
        _directory = [directory stringByStandardizingPath]; _home = [home stringByStandardizingPath];
        _applications = [applications copy]; _projects = @[];
    }
    return self;
}
- (NSString *)file { return [self.directory stringByAppendingPathComponent:@"projects.json"]; }
- (BOOL)forbiddenSpelling:(NSString *)path {
    for (NSString *component in path.pathComponents) if ([component caseInsensitiveCompare:@"ClaudeCodeConfig"] == NSOrderedSame) return YES;
    if (Under(path,self.directory)) return YES;
    for (NSString *app in self.applications) if (Under(path,app)) return YES;
    NSString *support = [self.home stringByAppendingPathComponent:@"Library/Application Support"];
    if (Under(path,[support stringByAppendingPathComponent:@"Claude"]) || Under(path,[support stringByAppendingPathComponent:@"Claude-Second"])) return YES;
    if ([path.lowercaseString hasPrefix:[support.lowercaseString stringByAppendingString:@"/"]]) {
        NSString *component = [[path substringFromIndex:support.length+1] componentsSeparatedByString:@"/"].firstObject;
        if ([component.lowercaseString hasPrefix:@"claude-profile-"] && component.length == 17) {
            NSString *digits = [component substringFromIndex:15];
            if ([digits rangeOfCharacterFromSet:NSCharacterSet.decimalDigitCharacterSet.invertedSet].location == NSNotFound) return YES;
        }
    }
    return NO;
}
- (BOOL)forbidden:(NSString *)path {
    if ([self forbiddenSpelling:path]) return YES;
    NSString *support = [self.home stringByAppendingPathComponent:@"Library/Application Support"];
    // APFS firmlinks can give one directory two absolute spellings without being symlinks.
    NSMutableArray *protected = [NSMutableArray arrayWithArray:self.applications];
    [protected addObject:self.directory];
    [protected addObject:[support stringByAppendingPathComponent:@"Claude"]];
    [protected addObject:[support stringByAppendingPathComponent:@"Claude-Second"]];
    for (NSInteger n = 3; n <= 10; n++) [protected addObject:[support stringByAppendingPathComponent:[NSString stringWithFormat:@"Claude-Profile-%02ld",(long)n]]];
    NSMutableSet *identities = [NSMutableSet set]; struct stat info;
    for (NSString *root in protected) if (stat(root.fileSystemRepresentation,&info) == 0 && S_ISDIR(info.st_mode))
        [identities addObject:[NSString stringWithFormat:@"%llu:%llu",(unsigned long long)info.st_dev,(unsigned long long)info.st_ino]];
    for (NSString *ancestor = path; ancestor.length > 1; ancestor = ancestor.stringByDeletingLastPathComponent) {
        if (stat(ancestor.fileSystemRepresentation,&info) == 0 &&
            [identities containsObject:[NSString stringWithFormat:@"%llu:%llu",(unsigned long long)info.st_dev,(unsigned long long)info.st_ino]]) return YES;
    }
    return NO;
}
- (NSString *)normalizeDirectory:(NSString *)path error:(NSString **)failure {
    if (failure) *failure = nil;
    if (![path isKindOfClass:NSString.class] || !path.isAbsolutePath || [path rangeOfString:@"\0"].location != NSNotFound) {
        if (failure) *failure = @"请选择绝对路径的已有文件夹。"; return nil;
    }
    NSString *real = path.stringByStandardizingPath.stringByResolvingSymlinksInPath;
    if (!AbsoluteStandardPath(real) || [self forbidden:path.stringByStandardizingPath] || [self forbidden:real]) {
        if (failure) *failure = @"不能添加账号数据目录、ClaudeCodeConfig 或启动器自己的目录。"; return nil;
    }
    NSString *status = DirectoryStatus(real);
    if (![status isEqual:@"正常"]) { if (failure) *failure = [@"目录" stringByAppendingString:status]; return nil; }
    return real;
}
- (NSString *)statusForPath:(NSString *)path {
    if (!AbsoluteStandardPath(path) || [self forbidden:path]) return @"无权限";
    NSString *status = DirectoryStatus(path);
    if (![status isEqual:@"正常"]) return status;
    // A replaced symlink must not silently redirect a saved project to another directory.
    if (![path isEqual:path.stringByResolvingSymlinksInPath]) return @"不存在";
    return @"正常";
}
- (BOOL)containsDirectory:(NSString *)path {
    struct stat candidate; BOOL exists = stat(path.fileSystemRepresentation,&candidate) == 0;
    for (NSDictionary *project in self.projects) {
        NSString *saved = project[@"path"]; struct stat other;
        if ([saved isEqual:path] || (exists && stat(saved.fileSystemRepresentation,&other) == 0 &&
            candidate.st_dev == other.st_dev && candidate.st_ino == other.st_ino)) return YES;
    }
    return NO;
}
- (BOOL)validProjects:(id)projects {
    if (![projects isKindOfClass:NSArray.class] || [projects count] > 1000) return NO;
    NSMutableSet *ids = [NSMutableSet set], *paths = [NSMutableSet set];
    NSSet *keys = [NSSet setWithArray:@[@"id",@"name",@"group",@"path",@"lastAccount"]];
    for (id row in projects) {
        if (![row isKindOfClass:NSDictionary.class] || ![[NSSet setWithArray:[row allKeys]] isEqual:keys]) return NO;
        NSString *identifier = row[@"id"], *name = row[@"name"], *group = row[@"group"], *path = row[@"path"];
        if (![identifier isKindOfClass:NSString.class] || ![[NSUUID alloc] initWithUUIDString:identifier] ||
            ![name isKindOfClass:NSString.class] || ![name stringByTrimmingCharactersInSet:NSCharacterSet.whitespaceAndNewlineCharacterSet].length || name.length > 200 ||
            ![group isKindOfClass:NSString.class] || group.length > 200 || !AbsoluteStandardPath(path) || [self forbiddenSpelling:path] ||
            !IntegerBetween(row[@"lastAccount"],0,10) || [ids containsObject:identifier] || [paths containsObject:path]) return NO;
        [ids addObject:identifier]; [paths addObject:path];
    }
    return YES;
}
// Check every ancestor before accessing our own JSON. Do not follow a launcher-store symlink into account data.
- (NSString *)storageProblem {
    if (!AbsoluteStandardPath(self.directory)) return @"项目清单目录不是标准绝对路径。";
    NSString *prefix = @"/";
    for (NSString *part in self.directory.pathComponents) {
        if ([part isEqual:@"/"]) continue;
        prefix = [prefix stringByAppendingPathComponent:part]; struct stat info;
        if (lstat(prefix.fileSystemRepresentation,&info) != 0) {
            if (errno == ENOENT) return nil;
            return @"无法检查项目清单目录权限。";
        }
        if (!S_ISDIR(info.st_mode)) return @"项目清单目录或上级是链接／非目录，已停止访问。";
        if ([prefix isEqual:self.directory] && info.st_uid != getuid()) return @"项目清单目录不属于当前用户。";
    }
    return nil;
}
- (BOOL)safeFile:(struct stat)info {
    return S_ISREG(info.st_mode) && info.st_uid == getuid() && info.st_nlink == 1 && !(info.st_mode & 0077);
}
- (void)load {
    self.projects = @[]; self.notice = [self storageProblem]; self.writable = self.notice == nil;
    if (!self.writable) return;
    int fd = open(self.file.fileSystemRepresentation,O_RDONLY | O_NOFOLLOW | O_NONBLOCK);
    if (fd < 0) {
        if (errno == ENOENT) return;
        self.notice = @"无法安全读取 projects.json，原文件未改动。"; self.writable = NO; return;
    }
    struct stat info;
    if (fstat(fd,&info) != 0 || ![self safeFile:info]) {
        close(fd); self.notice = @"projects.json 必须是当前用户独有的普通文件（0600），不能是链接。原文件未改动。"; self.writable = NO; return;
    }
    // Bound the read of this launcher-owned file; never read project contents.
    NSMutableData *data = [NSMutableData data]; BOOL readError = NO;
    if (info.st_size <= 1024*1024) {
        unsigned char buffer[8192]; ssize_t count;
        while ((count = read(fd,buffer,sizeof(buffer))) != 0) {
            if (count < 0) { if (errno == EINTR) continue; readError = YES; break; }
            [data appendBytes:buffer length:(NSUInteger)count]; if (data.length > 1024*1024) break;
        }
    }
    close(fd);
    if (readError) { self.notice = @"读取项目清单失败，原文件未改动。"; self.writable = NO; return; }
    id json = data.length <= 1024*1024 ? [NSJSONSerialization JSONObjectWithData:data options:0 error:nil] : nil;
    if ([json isKindOfClass:NSDictionary.class] && IntegerBetween(json[@"version"],2,NSIntegerMax)) {
        self.notice = @"项目清单来自较新的版本，已停止写入并保留原文件。"; self.writable = NO; return;
    }
    if ([json isKindOfClass:NSDictionary.class] && IntegerBetween(json[@"version"],1,1) &&
        [json count] == 2 && [self validProjects:json[@"projects"]]) { self.projects = json[@"projects"]; return; }
    NSString *backup = [self.file stringByAppendingFormat:@".corrupt-%@",NSUUID.UUID.UUIDString];
    struct stat now;
    if (lstat(self.file.fileSystemRepresentation,&now) != 0 || now.st_dev != info.st_dev || now.st_ino != info.st_ino ||
        renamex_np(self.file.fileSystemRepresentation,backup.fileSystemRepresentation,RENAME_EXCL) != 0) {
        self.notice = @"项目清单损坏，但无法安全保留原文件。已停止写入，请检查目录权限。"; self.writable = NO; return;
    }
    self.notice = [NSString stringWithFormat:@"项目清单损坏，原文件已改名保留：%@。当前显示空清单，可重新添加目录。",backup];
}
- (BOOL)save:(NSArray<NSDictionary *> *)projects error:(NSString **)failure {
    if (failure) *failure = nil;
    NSString *problem = !self.writable ? self.notice ?: @"项目清单尚未加载。" : [self storageProblem];
    if (!problem && ![self validProjects:projects]) problem = @"项目记录格式无效（最多 1000 项，名称和分组最多 200 字）。";
    struct stat existing;
    if (!problem && lstat(self.file.fileSystemRepresentation,&existing) == 0 && ![self safeFile:existing]) problem = @"项目清单文件不安全，未覆盖。";
    if (problem) { if (failure) *failure = problem; return NO; }
    NSError *error = nil;
    NSData *data = [NSJSONSerialization dataWithJSONObject:@{@"version":@1,@"projects":projects} options:NSJSONWritingPrettyPrinted error:&error];
    if (!data || data.length > 1024*1024) { if (failure) *failure = @"项目清单过大或无法编码。"; return NO; }
    if (![NSFileManager.defaultManager createDirectoryAtPath:self.directory withIntermediateDirectories:YES attributes:@{NSFilePosixPermissions:@0700} error:&error] || [self storageProblem]) {
        if (failure) *failure = @"无法创建或安全访问项目清单目录。"; return NO;
    }
    NSString *temporary = [self.directory stringByAppendingPathComponent:[@".projects-" stringByAppendingString:NSUUID.UUID.UUIDString]];
    int fd = open(temporary.fileSystemRepresentation,O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW,0600);
    BOOL ok = fd >= 0; NSUInteger offset = 0;
    while (ok && offset < data.length) {
        ssize_t count = write(fd,(const char *)data.bytes+offset,data.length-offset);
        if (count < 0 && errno == EINTR) continue;
        if (count <= 0) ok = NO; else offset += (NSUInteger)count;
    }
    if (ok) ok = fsync(fd) == 0;
    if (fd >= 0 && close(fd) != 0) ok = NO;
    if (ok) ok = rename(temporary.fileSystemRepresentation,self.file.fileSystemRepresentation) == 0;
    if (!ok) { unlink(temporary.fileSystemRepresentation); if (failure) *failure = @"保存项目清单失败，原清单保持不变。"; return NO; }
    // Round trip to immutable records so later UI edits cannot mutate an unsaved model.
    self.projects = [NSJSONSerialization JSONObjectWithData:data options:0 error:nil][@"projects"];
    return YES;
}
@end

void TestProjects(void) {
    NSFileManager *fm = NSFileManager.defaultManager;
    NSString *root = [fm.currentDirectoryPath.stringByResolvingSymlinksInPath stringByAppendingPathComponent:[@".claude-project-tests-" stringByAppendingString:NSUUID.UUID.UUIDString]];
    NSString *home = [root stringByAppendingPathComponent:@"home"], *directory = [home stringByAppendingPathComponent:@"Library/Application Support/Claude Launcher"];
    NSString *project = [home stringByAppendingPathComponent:@"work/示例项目"], *app = [root stringByAppendingPathComponent:@"Launcher.app"];
    NSCAssert([fm createDirectoryAtPath:project withIntermediateDirectories:YES attributes:nil error:nil],@"project fixture");
    ProjectStore *store = [[ProjectStore alloc] initWithDirectory:directory home:home applicationPaths:@[app]]; [store load];
    NSCAssert(store.writable && !store.notice && !store.projects.count,@"missing list stays empty");
    NSString *why = nil;
    NSCAssert([[store normalizeDirectory:project error:&why] isEqual:project] && !why,@"normal directory accepted");
    NSCAssert(![store normalizeDirectory:@"relative" error:&why],@"relative rejected");
    for (NSString *path in @[[home stringByAppendingPathComponent:@"Library/Application Support/Claude"],
                              [home stringByAppendingPathComponent:@"Library/Application Support/Claude-Second/sub"],
                              [home stringByAppendingPathComponent:@"Library/Application Support/Claude-Profile-03/sub"],
                              [home stringByAppendingPathComponent:@"work/ClaudeCodeConfig/sub"],directory,app]) {
        NSCAssert([fm createDirectoryAtPath:path withIntermediateDirectories:YES attributes:nil error:nil],@"blocked fixture");
        NSCAssert(![store normalizeDirectory:path error:&why],@"protected directory rejected: %@",path);
    }
    NSString *alias = [root stringByAppendingPathComponent:@"alias"];
    NSCAssert([fm createSymbolicLinkAtPath:alias withDestinationPath:directory error:nil],@"link fixture");
    NSCAssert([store forbidden:alias],@"directory identity rejects alternate spelling before canonicalization");
    NSCAssert(![store normalizeDirectory:alias error:&why],@"protected alias rejected");
    NSCAssert([fm removeItemAtPath:alias error:nil] && [fm createSymbolicLinkAtPath:alias withDestinationPath:project error:nil],@"project alias fixture");
    NSCAssert([[store normalizeDirectory:alias error:&why] isEqual:project],@"normal alias resolves to real path");
    NSDictionary *row = @{@"id":NSUUID.UUID.UUIDString,@"name":@"示例",@"group":@"工作",@"path":project,@"lastAccount":@2};
    NSCAssert([store save:@[row] error:&why],@"save: %@",why);
    NSString *file = [directory stringByAppendingPathComponent:@"projects.json"]; struct stat metadata;
    NSCAssert(stat(file.fileSystemRepresentation,&metadata) == 0 && (metadata.st_mode & 0777) == 0600,@"private mode");
    [store load]; NSCAssert([store.projects isEqual:@[row]],@"round trip");
    NSCAssert([store containsDirectory:project] && [store containsDirectory:alias],@"duplicate directory including aliases");
    NSCAssert((![store save:@[row,row] error:&why] && [store.projects isEqual:@[row]]),@"invalid save preserves list");
    NSMutableDictionary *second = row.mutableCopy; second[@"id"] = NSUUID.UUID.UUIDString; second[@"path"] = [root stringByAppendingPathComponent:@"not-created"]; second[@"lastAccount"] = @0;
    NSMutableDictionary *edited = row.mutableCopy; edited[@"name"] = @"改名"; edited[@"group"] = @"另一组"; edited[@"lastAccount"] = @10;
    NSCAssert(([store save:@[second,edited] error:&why]),@"edited and reordered list saves, missing directory stays listed");
    [store load]; NSCAssert(([store.projects isEqual:@[second,edited]] && ![fm fileExistsAtPath:second[@"path"]]),@"order, names and last account persist without creating project paths");
    NSData *beforeFailedSave = [NSData dataWithContentsOfFile:file];
    NSCAssert(chmod(directory.fileSystemRepresentation,0500) == 0,@"deny writes to synthetic list directory");
    NSCAssert(![store save:@[] error:&why] && [[NSData dataWithContentsOfFile:file] isEqual:beforeFailedSave] && store.projects.count == 2,@"failed atomic write preserves disk and model");
    NSCAssert(chmod(directory.fileSystemRepresentation,0700) == 0,@"restore synthetic list directory");
    NSString *marker = [project stringByAppendingPathComponent:@"synthetic.txt"];
    NSCAssert([@"synthetic" writeToFile:marker atomically:YES encoding:NSUTF8StringEncoding error:nil],@"fixture contents");
    NSCAssert([store save:@[] error:&why] && [fm fileExistsAtPath:marker],@"removing record never deletes files");
    NSData *broken = [@"{not JSON" dataUsingEncoding:NSUTF8StringEncoding];
    NSCAssert([broken writeToFile:file options:0 error:nil],@"corrupt fixture"); [store load];
    NSCAssert(store.writable && store.notice && !store.projects.count && ![fm fileExistsAtPath:file],@"corrupt list preserved by rename");
    NSArray *names = [fm contentsOfDirectoryAtPath:directory error:nil];
    NSCAssert(names.count == 1 && [names[0] hasPrefix:@"projects.json.corrupt-"] &&
        [[NSData dataWithContentsOfFile:[directory stringByAppendingPathComponent:names[0]]] isEqual:broken],@"original bytes preserved");
    NSCAssert([store save:@[row] error:&why],@"new list after recovery");
    NSData *badShape = [@"{\"version\":1,\"projects\":[{\"path\":null}]}" dataUsingEncoding:NSUTF8StringEncoding];
    NSCAssert([badShape writeToFile:file options:0 error:nil],@"invalid schema fixture"); [store load];
    NSCAssert(store.writable && store.notice && ![fm fileExistsAtPath:file],@"invalid schema retained, not interpreted as a path");
    NSCAssert([store save:@[] error:&why],@"recover schema fixture");
    NSData *future = [@"{\"version\":2,\"projects\":[]}" dataUsingEncoding:NSUTF8StringEncoding];
    NSCAssert([future writeToFile:file options:0 error:nil],@"future fixture"); [store load];
    NSCAssert(!store.writable && ![store save:@[] error:&why] && [[NSData dataWithContentsOfFile:file] isEqual:future],@"future version retained");
    NSCAssert([[store statusForPath:[root stringByAppendingPathComponent:@"missing"]] isEqual:@"不存在"],@"missing directory");
    NSCAssert([[store statusForPath:alias] isEqual:@"不存在"],@"saved path never redirects");
    NSCAssert(chmod(project.fileSystemRepresentation,0000) == 0,@"deny synthetic directory only");
    NSCAssert([[store statusForPath:project] isEqual:@"无权限"],@"inaccessible directory");
    NSCAssert(chmod(project.fileSystemRepresentation,0700) == 0,@"restore synthetic directory");
    NSCAssert([fm removeItemAtPath:file error:nil] && [fm createSymbolicLinkAtPath:file withDestinationPath:marker error:nil],@"store symlink fixture");
    [store load]; NSCAssert(!store.writable && ![store save:@[] error:&why],@"never read or overwrite linked store");
    NSCAssert([fm removeItemAtPath:file error:nil] && link(marker.fileSystemRepresentation,file.fileSystemRepresentation) == 0,@"hardlink fixture");
    [store load]; NSCAssert(!store.writable && ![store save:@[] error:&why],@"never read or overwrite hardlinked store");
    NSCAssert([fm removeItemAtPath:file error:nil],@"remove synthetic hardlink");
    ProjectStore *linkedStore = [[ProjectStore alloc] initWithDirectory:[alias stringByAppendingPathComponent:@"storage"] home:home applicationPaths:@[]];
    [linkedStore load]; NSCAssert(!linkedStore.writable,@"symlink ancestor rejected before storage access");
    NSCAssert([store.projects isEqual:@[]],@"unsafe load does not expose records");
    NSCAssert([fm removeItemAtPath:root error:nil],@"clean synthetic project fixtures");
    puts("PASS: project path guards, aliases and duplicates, JSON round trip and 0600, corrupt/future preservation, non-destructive removal, missing/denied paths; synthetic fixtures only");
}
