#import "UsageHistory.h"
#import <sys/stat.h>
#import <fcntl.h>
#import <unistd.h>
#import <errno.h>
#import <math.h>

static NSDictionary *Unavailable(NSString *status) { return @{@"status":status}; }
static BOOL Number(id value) {
    return [value isKindOfClass:NSNumber.class] && CFGetTypeID((__bridge CFTypeRef)value) != CFBooleanGetTypeID() && isfinite([value doubleValue]);
}
static BOOL Keys(NSDictionary *value, NSArray *allowed) {
    for (id key in value) if (![allowed containsObject:key]) return NO;
    return YES;
}
static NSDictionary *ParseHistory(NSData *data, NSDate *now) {
    id root = [NSJSONSerialization JSONObjectWithData:data options:0 error:nil];
    if (![root isKindOfClass:NSDictionary.class] || !Keys(root,@[@"version",@"samples"]) || !Number(root[@"version"]) ||
        [root[@"version"] doubleValue] != 2 || ![root[@"samples"] isKindOfClass:NSArray.class]) return Unavailable(@"格式不支持");
    NSArray *samples = root[@"samples"];
    if (samples.count > 20000) return Unavailable(@"记录过大");
    NSDictionary *latest = nil;
    NSMutableSet *organizations = [NSMutableSet set]; BOOL unidentified = NO;
    for (id sample in samples) {
        if (![sample isKindOfClass:NSDictionary.class] || !Keys(sample,@[@"t",@"org",@"u"]) || !Number(sample[@"t"]) ||
            [sample[@"t"] doubleValue] <= 0 || [sample[@"t"] doubleValue]/1000 > now.timeIntervalSince1970 ||
            ![sample[@"u"] isKindOfClass:NSDictionary.class] ||
            !Keys(sample[@"u"],@[@"fh",@"sd",@"so",@"oa",@"cw",@"om",@"op",@"sn",@"xu"])) return Unavailable(@"格式不支持");
        id organization = sample[@"org"];
        if (organization == NSNull.null) unidentified = YES;
        else if (![organization isKindOfClass:NSString.class] || ![[NSUUID alloc] initWithUUIDString:organization]) return Unavailable(@"格式不支持");
        else [organizations addObject:[organization lowercaseString]];
        for (NSString *key in @[@"fh",@"sd"]) {
            id value = sample[@"u"][key];
            if (value && (!Number(value) || [value doubleValue] < 0 || [value doubleValue] > 100)) return Unavailable(@"格式不支持");
        }
        if (!latest || [sample[@"t"] doubleValue] >= [latest[@"t"] doubleValue]) latest = sample;
    }
    if (!latest) return Unavailable(@"暂无本地记录");
    // History can survive logout or organization switches. Never infer the current identity from it.
    if (unidentified || organizations.count != 1) return Unavailable(@"身份待核对");
    NSDate *date = [NSDate dateWithTimeIntervalSince1970:[latest[@"t"] doubleValue]/1000];
    if ([now timeIntervalSinceDate:date] > 15*60) return @{@"status":@"记录已过期",@"date":date};
    NSMutableDictionary *result = [@{@"status":@"本地记录",@"date":date} mutableCopy];
    for (NSString *key in @[@"fh",@"sd"]) if (latest[@"u"][key]) result[key] = latest[@"u"][key];
    return result;
}

NSDictionary *ReadUsageHistory(NSString *profile, NSDate *now) {
    if (!profile.isAbsolutePath || ![profile isEqual:profile.stringByStandardizingPath]) return Unavailable(@"路径不安全");
    int fd = open("/",O_RDONLY|O_DIRECTORY|O_CLOEXEC);
    for (NSString *part in profile.pathComponents) {
        if ([part isEqual:@"/"]) continue;
        int next = openat(fd,part.fileSystemRepresentation,O_RDONLY|O_DIRECTORY|O_NOFOLLOW|O_CLOEXEC|O_NONBLOCK);
        int problem = errno; if (fd >= 0) close(fd); fd = next;
        if (fd < 0) return Unavailable(problem == ENOENT ? @"暂无本地记录" : @"路径不可读取");
        struct stat directory;
        if (fstat(fd,&directory) || (directory.st_mode & 0022) || (directory.st_uid != 0 && directory.st_uid != getuid())) {
            close(fd); return Unavailable(@"路径不安全");
        }
    }
    struct stat directory;
    if (fd < 0 || fstat(fd,&directory) || directory.st_uid != getuid()) {
        if (fd >= 0) close(fd); return Unavailable(@"路径不安全");
    }
    int file = openat(fd,"plan-usage-history.json",O_RDONLY|O_NOFOLLOW|O_CLOEXEC|O_NONBLOCK);
    int problem = errno; close(fd);
    if (file < 0) return Unavailable(problem == ENOENT ? @"暂无本地记录" : @"记录不可读取");
    struct stat before, after;
    if (fstat(file,&before) || !S_ISREG(before.st_mode) || before.st_uid != getuid() || before.st_nlink != 1 || (before.st_mode & 0022)) {
        close(file); return Unavailable(@"记录不安全");
    }
    if (before.st_size <= 0 || before.st_size > 2*1024*1024) { close(file); return Unavailable(@"记录大小异常"); }
    NSMutableData *data = [NSMutableData dataWithLength:(NSUInteger)before.st_size];
    NSUInteger offset = 0;
    while (offset < data.length) {
        ssize_t count = read(file,(char *)data.mutableBytes+offset,data.length-offset);
        if (count < 0 && errno == EINTR) continue;
        if (count <= 0) break;
        offset += (NSUInteger)count;
    }
    BOOL stable = offset == data.length && !fstat(file,&after) && before.st_size == after.st_size &&
        before.st_mtimespec.tv_sec == after.st_mtimespec.tv_sec && before.st_mtimespec.tv_nsec == after.st_mtimespec.tv_nsec &&
        before.st_ctimespec.tv_sec == after.st_ctimespec.tv_sec && before.st_ctimespec.tv_nsec == after.st_ctimespec.tv_nsec;
    close(file);
    return stable ? ParseHistory(data,now) : Unavailable(@"记录正在更新");
}

void TestUsageHistory(void) {
    NSDate *now = [NSDate dateWithTimeIntervalSince1970:2000000000];
    NSString *org = @"11111111-1111-4111-8111-111111111111";
    NSMutableDictionary *sample = [@{@"t":@1999999990000,@"org":org,@"u":@{@"fh":@62,@"sd":@0}} mutableCopy];
    NSMutableDictionary *root = [@{@"version":@2,@"samples":@[sample]} mutableCopy];
    NSData *(^encode)(void) = ^{ return [NSJSONSerialization dataWithJSONObject:root options:0 error:nil]; };
    NSDictionary *result = ParseHistory(encode(),now);
    NSCAssert([result[@"fh"] isEqual:@62] && [result[@"sd"] isEqual:@0] && !result[@"org"],@"only usage and time leave parser");
    sample[@"u"] = @{@"fh":@100}; result = ParseHistory(encode(),now);
    NSCAssert([result[@"fh"] isEqual:@100] && !result[@"sd"],@"missing weekly is not zero");
    for (id bad in @[@YES,@(-1),@101,@"62"]) {
        sample[@"u"] = @{@"fh":bad}; NSCAssert(!ParseHistory(encode(),now)[@"fh"],@"reject invalid percentages");
    }
    sample[@"u"] = @{@"fh":@62,@"sd":@70};
    sample[@"t"] = @1999999000000;
    NSCAssert([ParseHistory(encode(),now)[@"status"] isEqual:@"记录已过期"],@"old numbers hidden");
    sample[@"t"] = @2000000001000;
    NSCAssert(!ParseHistory(encode(),now)[@"fh"],@"future time rejected");
    sample[@"t"] = @1999999990000;
    root[@"samples"] = @[sample,@{@"t":@1999999980000,@"org":@"22222222-2222-4222-8222-222222222222",@"u":@{}}];
    NSCAssert([ParseHistory(encode(),now)[@"status"] isEqual:@"身份待核对"],@"multiple organizations never assigned to current account");
    root[@"samples"] = @[sample]; sample[@"org"] = NSNull.null;
    NSCAssert(!ParseHistory(encode(),now)[@"fh"],@"unknown identity"); sample[@"org"] = org;
    root[@"version"] = @3; NSCAssert(!ParseHistory(encode(),now)[@"fh"],@"unknown version"); root[@"version"] = @2;
    sample[@"unknown"] = @1; NSCAssert(!ParseHistory(encode(),now)[@"fh"],@"unknown schema"); [sample removeObjectForKey:@"unknown"];
    NSCAssert(!ParseHistory([@"{" dataUsingEncoding:NSUTF8StringEncoding],now)[@"fh"],@"corrupt JSON");
    // Use an owned directory under home: the production reader rejects shared writable ancestors.
    NSString *folder = [NSHomeDirectory() stringByAppendingPathComponent:[@".claude-launcher-usage-test-" stringByAppendingString:NSUUID.UUID.UUIDString]];
    NSFileManager *fm = NSFileManager.defaultManager;
    NSCAssert([fm createDirectoryAtPath:folder withIntermediateDirectories:NO attributes:@{NSFilePosixPermissions:@0700} error:nil],@"fixture");
    NSString *path = [folder stringByAppendingPathComponent:@"plan-usage-history.json"];
    NSCAssert(!ReadUsageHistory(folder,now)[@"fh"],@"missing file");
    NSCAssert([encode() writeToFile:path atomically:YES],@"write fixture"); chmod(path.fileSystemRepresentation,0600);
    NSCAssert([ReadUsageHistory(folder,now)[@"fh"] isEqual:@62],@"read safe fixture");
    NSString *alias = [folder stringByAppendingString:@"-alias"];
    NSCAssert(symlink(folder.fileSystemRepresentation,alias.fileSystemRepresentation)==0,@"directory alias");
    NSCAssert(!ReadUsageHistory(alias,now)[@"fh"],@"reject linked ancestor"); unlink(alias.fileSystemRepresentation);
    NSString *other = [folder stringByAppendingPathComponent:@"other"];
    NSCAssert(link(path.fileSystemRepresentation,other.fileSystemRepresentation)==0,@"hardlink fixture");
    NSCAssert(!ReadUsageHistory(folder,now)[@"fh"],@"hardlink rejected"); unlink(other.fileSystemRepresentation);
    chmod(path.fileSystemRepresentation,0666); NSCAssert(!ReadUsageHistory(folder,now)[@"fh"],@"writable file rejected");
    unlink(path.fileSystemRepresentation);
    NSCAssert(symlink("missing",path.fileSystemRepresentation)==0,@"file symlink");
    NSCAssert(!ReadUsageHistory(folder,now)[@"fh"],@"file symlink rejected"); unlink(path.fileSystemRepresentation);
    NSCAssert(mkfifo(path.fileSystemRepresentation,0600)==0,@"FIFO fixture");
    NSCAssert(!ReadUsageHistory(folder,now)[@"fh"],@"FIFO rejected without blocking"); unlink(path.fileSystemRepresentation);
    int large = open(path.fileSystemRepresentation,O_CREAT|O_WRONLY,0600); NSCAssert(large>=0,@"large fixture");
    NSCAssert(ftruncate(large,2*1024*1024+1)==0,@"size fixture"); close(large);
    NSCAssert(!ReadUsageHistory(folder,now)[@"fh"],@"bounded read");
    NSCAssert([fm removeItemAtPath:folder error:nil],@"remove only synthetic fixture");
    puts("PASS: local usage history schema, missing/zero/invalid values, stale/future records, identity ambiguity, bounded reads and unsafe paths");
}
