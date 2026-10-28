#import "UsageHistory.h"
#import <sys/stat.h>
#import <fcntl.h>
#import <unistd.h>
#import <errno.h>
#import <math.h>
#import <sqlite3.h>
#import <Security/Security.h>
#import <CommonCrypto/CommonCrypto.h>

static NSDictionary *Unavailable(NSString *status) { return @{@"status":status}; }
static BOOL Number(id value) {
    return [value isKindOfClass:NSNumber.class] && CFGetTypeID((__bridge CFTypeRef)value) != CFBooleanGetTypeID() && isfinite([value doubleValue]);
}
static BOOL Keys(NSDictionary *value, NSArray *allowed) {
    for (id key in value) if (![allowed containsObject:key]) return NO;
    return YES;
}
static NSDictionary *ParseHistory(NSData *data, NSDate *now, NSString *currentOrganization) {
    id root = [NSJSONSerialization JSONObjectWithData:data options:0 error:nil];
    if (![root isKindOfClass:NSDictionary.class] || !Keys(root,@[@"version",@"samples"]) || !Number(root[@"version"]) ||
        [root[@"version"] doubleValue] != 2 || ![root[@"samples"] isKindOfClass:NSArray.class]) return Unavailable(@"格式不支持");
    NSArray *samples = root[@"samples"];
    if (samples.count > 20000) return Unavailable(@"记录过大");
    NSDictionary *latest = nil;
    if (![currentOrganization isKindOfClass:NSString.class] || ![[NSUUID alloc] initWithUUIDString:currentOrganization]) return Unavailable(@"未找到当前组织");
    for (id sample in samples) {
        if (![sample isKindOfClass:NSDictionary.class] || !Keys(sample,@[@"t",@"org",@"u"]) || !Number(sample[@"t"]) ||
            [sample[@"t"] doubleValue] <= 0 || [sample[@"t"] doubleValue]/1000 > now.timeIntervalSince1970 ||
            ![sample[@"u"] isKindOfClass:NSDictionary.class] ||
            !Keys(sample[@"u"],@[@"fh",@"sd",@"so",@"oa",@"cw",@"om",@"op",@"sn",@"xu"])) return Unavailable(@"格式不支持");
        id organization = sample[@"org"];
        if (organization == NSNull.null) {}
        else if (![organization isKindOfClass:NSString.class] || ![[NSUUID alloc] initWithUUIDString:organization]) return Unavailable(@"格式不支持");
        for (NSString *key in @[@"fh",@"sd"]) {
            id value = sample[@"u"][key];
            if (value && (!Number(value) || [value doubleValue] < 0 || [value doubleValue] > 100)) return Unavailable(@"格式不支持");
        }
        if ([organization isKindOfClass:NSString.class] && [organization caseInsensitiveCompare:currentOrganization] == NSOrderedSame &&
            (!latest || [sample[@"t"] doubleValue] >= [latest[@"t"] doubleValue])) latest = sample;
    }
    if (!latest) return Unavailable(@"无对应组织记录");
    NSDate *date = [NSDate dateWithTimeIntervalSince1970:[latest[@"t"] doubleValue]/1000];
    if ([now timeIntervalSinceDate:date] > 15*60) return @{@"status":@"记录已过期",@"date":date};
    NSMutableDictionary *result = [@{@"status":@"本地记录",@"date":date} mutableCopy];
    for (NSString *key in @[@"fh",@"sd"]) if (latest[@"u"][key]) result[key] = latest[@"u"][key];
    return result;
}

static void ClearBytes(void *memory, size_t length) {
    volatile uint8_t *bytes = memory;
    while (length--) *bytes++ = 0;
}

typedef BOOL (^UsageKeyProvider)(uint8_t *key);

static BOOL SafeFile(struct stat st) {
    return S_ISREG(st.st_mode) && st.st_uid == getuid() && st.st_nlink == 1 && !(st.st_mode & 0022);
}
static BOOL SameFile(struct stat a, struct stat b) {
    return a.st_dev == b.st_dev && a.st_ino == b.st_ino && a.st_size == b.st_size &&
        a.st_mtimespec.tv_sec == b.st_mtimespec.tv_sec && a.st_mtimespec.tv_nsec == b.st_mtimespec.tv_nsec &&
        a.st_ctimespec.tv_sec == b.st_ctimespec.tv_sec && a.st_ctimespec.tv_nsec == b.st_ctimespec.tv_nsec;
}
static BOOL QuietCookieStore(int directory) {
    // shortcut: immutable SQLite ignores WAL; reject WAL and active journals until a read-only WAL reader is supported.
    for (NSString *name in @[@"Cookies-journal",@"Cookies-wal",@"Cookies-shm"]) {
        struct stat st;
        if (fstatat(directory,name.fileSystemRepresentation,&st,AT_SYMLINK_NOFOLLOW)) {
            if (errno != ENOENT) return NO;
        } else if (!SafeFile(st) || st.st_size != 0) return NO;
    }
    return YES;
}
static BOOL DeriveUsageKey(const void *password, size_t length, uint8_t *key) {
    return length > 0 && length <= 1024 && CCKeyDerivationPBKDF(kCCPBKDF2,password,length,
        (const uint8_t *)"saltysalt",9,kCCPRFHmacAlgSHA1,1003,key,kCCKeySizeAES128) == kCCSuccess;
}
static BOOL LoadUsageKey(uint8_t *key) {
    // Never enumerate the keychain, create a key, or change access controls. A denial lasts until restart.
    static BOOL denied = NO;
    @synchronized(NSProcessInfo.processInfo) {
        if (denied) return NO;
        UInt32 length = 0; void *password = NULL;
#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wdeprecated-declarations"
        OSStatus status = SecKeychainFindGenericPassword(NULL,19,"Claude Safe Storage",10,"Claude Key",&length,&password,NULL);
        BOOL ok = status == errSecSuccess && DeriveUsageKey(password,length,key);
        if (password) { ClearBytes(password,length); SecKeychainItemFreeContent(NULL,password); }
#pragma clang diagnostic pop
        if (!ok) denied = YES;
        return ok;
    }
}
static NSString *DecryptOrganization(NSData *encrypted, NSString *host, const uint8_t *key) {
    if (encrypted.length != 83 || memcmp(encrypted.bytes,"v10",3)) return nil;
    uint8_t plain[80] = {0}, hash[CC_SHA256_DIGEST_LENGTH]; size_t count = 0;
    NSData *domain = [host dataUsingEncoding:NSUTF8StringEncoding];
    CC_SHA256(domain.bytes,(CC_LONG)domain.length,hash);
    CCCryptorStatus status = CCCrypt(kCCDecrypt,kCCAlgorithmAES,kCCOptionPKCS7Padding,key,kCCKeySizeAES128,
        "                ",(const uint8_t *)encrypted.bytes+3,80,plain,sizeof(plain),&count);
    NSString *org = nil;
    if (status == kCCSuccess && count == 68 && !memcmp(plain,hash,sizeof(hash))) {
        NSString *value = [[NSString alloc] initWithBytes:plain+sizeof(hash) length:36 encoding:NSUTF8StringEncoding];
        if (value && [[NSUUID alloc] initWithUUIDString:value]) org = value.lowercaseString;
    }
    ClearBytes(plain,sizeof(plain));
    return org;
}
static int UsageSQLAuthorizer(void *context, int action, const char *table, const char *column, const char *database, const char *source) {
    if (source) return SQLITE_DENY; // Reject views/triggers, not just unexpected column names.
    if (action == SQLITE_SELECT) return SQLITE_OK;
    if (action != SQLITE_READ || !table || !column || !database || strcmp(database,"main")) return SQLITE_DENY;
    NSString *name = @(column);
    if (!strcmp(table,"meta") && [@[@"key",@"value"] containsObject:name]) return SQLITE_OK;
    if (!strcmp(table,"cookies") && [@[@"host_key",@"name",@"path",@"value",@"encrypted_value",@"has_expires",@"expires_utc",
        @"is_persistent",@"source_scheme",@"source_port",@"top_frame_site_key"] containsObject:name]) return SQLITE_OK;
    return SQLITE_DENY;
}
static int UsageSQLBudget(void *context) { return --*(int *)context <= 0; }
static NSString *ReadOrganization(int directory, NSDate *now, UsageKeyProvider provider, NSString **failure) {
    *failure = @"组织记录不可读";
    int file = openat(directory,"Cookies",O_RDONLY|O_NOFOLLOW|O_CLOEXEC|O_NONBLOCK);
    if (file < 0) { if (errno == ENOENT) *failure = @"未找到当前组织"; return nil; }
    struct stat before,after,pathStat; uint8_t header[20];
    BOOL safe = !fstat(file,&before) && SafeFile(before) && before.st_size >= 100 && before.st_size <= 16*1024*1024;
    if (!safe || pread(file,header,sizeof(header),0) != sizeof(header) || memcmp(header,"SQLite format 3\0",16)) {
        close(file); return nil;
    }
    if (header[18] != 1 || header[19] != 1 || !QuietCookieStore(directory)) {
        close(file); *failure = @"组织记录正在更新"; return nil;
    }
    // The already validated fd pins the inode. Immutable mode never creates journals, locks, SHM or copies.
    NSString *uri = [NSString stringWithFormat:@"file:/dev/fd/%d?mode=ro&immutable=1",file];
    sqlite3 *db = NULL; sqlite3_stmt *statement = NULL; NSString *org = nil;
    int budget = 1000;
    if (sqlite3_open_v2(uri.UTF8String,&db,SQLITE_OPEN_READONLY|SQLITE_OPEN_URI|SQLITE_OPEN_NOMUTEX,NULL) == SQLITE_OK) {
        sqlite3_db_config(db,SQLITE_DBCONFIG_DEFENSIVE,1,NULL);
        sqlite3_db_config(db,SQLITE_DBCONFIG_TRUSTED_SCHEMA,0,NULL);
        sqlite3_limit(db,SQLITE_LIMIT_LENGTH,4096);
        sqlite3_set_authorizer(db,UsageSQLAuthorizer,NULL);
        sqlite3_progress_handler(db,1000,UsageSQLBudget,&budget);
        BOOL versionOK = sqlite3_prepare_v2(db,"SELECT value FROM meta WHERE key='version' LIMIT 2",-1,&statement,NULL) == SQLITE_OK &&
            sqlite3_step(statement) == SQLITE_ROW && sqlite3_column_bytes(statement,0) == 2 &&
            !memcmp(sqlite3_column_text(statement,0),"24",2) && sqlite3_step(statement) == SQLITE_DONE;
        sqlite3_finalize(statement); statement = NULL;
        // Only the non-authentication selector for the root Claude URL is selected; no login rows are returned.
        const char *sql = "SELECT host_key,value,encrypted_value FROM cookies WHERE name='lastActiveOrg' "
            "AND host_key IN ('claude.ai','.claude.ai') AND path='/' AND top_frame_site_key='' "
            "AND source_scheme=2 AND source_port IN (-1,443) AND is_persistent=1 AND has_expires=1 AND expires_utc>? LIMIT 17";
        if (!versionOK) *failure = @"组织格式不支持";
        else if (sqlite3_prepare_v2(db,sql,-1,&statement,NULL) == SQLITE_OK) {
            sqlite3_bind_int64(statement,1,(sqlite3_int64)((now.timeIntervalSince1970+11644473600.0)*1000000));
            NSMutableSet *values = [NSMutableSet set]; BOOL valid = YES; int rows = 0,step;
            while ((step = sqlite3_step(statement)) == SQLITE_ROW) {
                if (++rows > 16) { valid = NO; break; }
                NSString *host = [NSString stringWithUTF8String:(const char *)sqlite3_column_text(statement,0)];
                int plainLength = sqlite3_column_bytes(statement,1), encryptedLength = sqlite3_column_bytes(statement,2);
                NSString *value = nil;
                if (plainLength == 0 && encryptedLength == 83 && !memcmp(sqlite3_column_blob(statement,2),"v10",3)) {
                    uint8_t key[kCCKeySizeAES128] = {0};
                    if (provider(key)) value = DecryptOrganization([NSData dataWithBytes:sqlite3_column_blob(statement,2) length:83],host,key);
                    else *failure = @"组织解密未授权";
                    ClearBytes(key,sizeof(key));
                }
                if (!value) { valid = NO; break; }
                [values addObject:value];
            }
            if (valid && step == SQLITE_DONE && values.count == 1) org = values.anyObject;
            else if (valid && step == SQLITE_DONE) *failure = values.count ? @"当前组织有歧义" : @"未找到当前组织";
        }
    }
    sqlite3_finalize(statement); if (db) sqlite3_close(db);
    BOOL stable = QuietCookieStore(directory) && !fstat(file,&after) && SameFile(before,after) &&
        !fstatat(directory,"Cookies",&pathStat,AT_SYMLINK_NOFOLLOW) && SameFile(before,pathStat);
    close(file);
    if (!stable) { *failure = @"组织记录正在更新"; return nil; }
    return org;
}

static NSDictionary *ReadUsageHistoryWithKey(NSString *profile, NSDate *now, UsageKeyProvider provider) {
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
    NSString *failure = nil;
    NSString *org = ReadOrganization(fd,now,provider,&failure);
    if (!org) { close(fd); return Unavailable(failure); }
    int file = openat(fd,"plan-usage-history.json",O_RDONLY|O_NOFOLLOW|O_CLOEXEC|O_NONBLOCK);
    int problem = errno;
    if (file < 0) { close(fd); return Unavailable(problem == ENOENT ? @"暂无本地记录" : @"记录不可读取"); }
    struct stat before, after;
    if (fstat(file,&before) || !S_ISREG(before.st_mode) || before.st_uid != getuid() || before.st_nlink != 1 || (before.st_mode & 0022)) {
        close(file); close(fd); return Unavailable(@"记录不安全");
    }
    if (before.st_size <= 0 || before.st_size > 2*1024*1024) { close(file); close(fd); return Unavailable(@"记录大小异常"); }
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
    NSString *confirmed = ReadOrganization(fd,now,provider,&failure); close(fd);
    if (!confirmed || ![confirmed isEqual:org]) return Unavailable(confirmed ? @"当前组织已切换" : failure);
    return stable ? ParseHistory(data,now,org) : Unavailable(@"记录正在更新");
}

NSDictionary *ReadUsageHistory(NSString *profile, NSDate *now) {
    uint8_t key[kCCKeySizeAES128] = {0}; uint8_t *keyPointer = key;
    __block BOOL attempted = NO, loaded = NO;
    NSDictionary *result = ReadUsageHistoryWithKey(profile,now,^BOOL(uint8_t *output) {
        if (!attempted) { attempted = YES; loaded = LoadUsageKey(keyPointer); }
        if (loaded) memcpy(output,keyPointer,kCCKeySizeAES128);
        return loaded;
    });
    ClearBytes(key,sizeof(key));
    return result;
}

void TestUsageHistory(void) {
    NSDate *now = [NSDate dateWithTimeIntervalSince1970:2000000000];
    NSString *org = @"11111111-1111-4111-8111-111111111111";
    NSMutableDictionary *sample = [@{@"t":@1999999990000,@"org":org,@"u":@{@"fh":@62,@"sd":@0}} mutableCopy];
    NSMutableDictionary *root = [@{@"version":@2,@"samples":@[sample]} mutableCopy];
    NSData *(^encode)(void) = ^{ return [NSJSONSerialization dataWithJSONObject:root options:0 error:nil]; };
    NSDictionary *result = ParseHistory(encode(),now,org);
    NSCAssert([result[@"fh"] isEqual:@62] && [result[@"sd"] isEqual:@0] && !result[@"org"],@"only usage and time leave parser");
    sample[@"u"] = @{@"fh":@100}; result = ParseHistory(encode(),now,org);
    NSCAssert([result[@"fh"] isEqual:@100] && !result[@"sd"],@"missing weekly is not zero");
    for (id bad in @[@YES,@(-1),@101,@"62"]) {
        sample[@"u"] = @{@"fh":bad}; NSCAssert(!ParseHistory(encode(),now,org)[@"fh"],@"reject invalid percentages");
    }
    sample[@"u"] = @{@"fh":@62,@"sd":@70};
    sample[@"t"] = @1999999000000;
    NSCAssert([ParseHistory(encode(),now,org)[@"status"] isEqual:@"记录已过期"],@"old numbers hidden");
    sample[@"t"] = @2000000001000;
    NSCAssert(!ParseHistory(encode(),now,org)[@"fh"],@"future time rejected");
    sample[@"t"] = @1999999990000;
    root[@"samples"] = @[sample,@{@"t":@1999999980000,@"org":@"22222222-2222-4222-8222-222222222222",@"u":@{}}];
    NSCAssert([ParseHistory(encode(),now,org)[@"fh"] isEqual:@62],@"match current organization among unrelated history");
    root[@"samples"] = @[sample,@{@"t":@1999999999000,@"org":@"22222222-2222-4222-8222-222222222222",@"u":@{@"fh":@99}}];
    NSCAssert([ParseHistory(encode(),now,org)[@"fh"] isEqual:@62],@"newer other organization must never win");
    NSCAssert(!ParseHistory(encode(),now,@"33333333-3333-4333-8333-333333333333")[@"fh"],@"missing match never falls back");
    root[@"samples"] = @[sample]; sample[@"org"] = NSNull.null;
    NSCAssert(!ParseHistory(encode(),now,org)[@"fh"],@"unknown identity"); sample[@"org"] = org;
    root[@"version"] = @3; NSCAssert(!ParseHistory(encode(),now,org)[@"fh"],@"unknown version"); root[@"version"] = @2;
    sample[@"unknown"] = @1; NSCAssert(!ParseHistory(encode(),now,org)[@"fh"],@"unknown schema"); [sample removeObjectForKey:@"unknown"];
    NSCAssert(!ParseHistory([@"{" dataUsingEncoding:NSUTF8StringEncoding],now,org)[@"fh"],@"corrupt JSON");
    // Use an owned directory under home: the production reader rejects shared writable ancestors.
    NSString *folder = [NSHomeDirectory() stringByAppendingPathComponent:[@".claude-launcher-usage-test-" stringByAppendingString:NSUUID.UUID.UUIDString]];
    NSFileManager *fm = NSFileManager.defaultManager;
    NSCAssert([fm createDirectoryAtPath:folder withIntermediateDirectories:NO attributes:@{NSFilePosixPermissions:@0700} error:nil],@"fixture");
    // Independent vector: Python hashlib PBKDF2 + SHA256, OpenSSL AES-CBC; contains only synthetic data.
    NSString *hex = @"7631304c25945a6704bedbeeadd980e7a18a8b0a6a02bf974aad9fe08ebb81be3acd88f16b49619ed4eb62d7666cfdf4812556019937e5d9ebb575003548e9fdd406c8938ca3e773c38e5a335f772f6bbc8f56";
    NSMutableData *cipher = [NSMutableData data];
    for (NSUInteger i=0;i<hex.length;i+=2) { unsigned byte = 0; sscanf([[hex substringWithRange:NSMakeRange(i,2)] UTF8String],"%02x",&byte); uint8_t b=byte; [cipher appendBytes:&b length:1]; }
    UsageKeyProvider testKey = ^BOOL(uint8_t *key) { return DeriveUsageKey("usage-test-only",15,key); };
    uint8_t vectorKey[16]; NSCAssert(testKey(vectorKey),@"synthetic key");
    NSCAssert([DecryptOrganization(cipher,@".claude.ai",vectorKey) isEqual:org],@"independent Chromium v24 crypto vector");
    NSCAssert(!DecryptOrganization(cipher,@"claude.ai",vectorKey),@"domain hash binding");
    vectorKey[0] ^= 1; NSCAssert(!DecryptOrganization(cipher,@".claude.ai",vectorKey),@"wrong key rejected");
    ClearBytes(vectorKey,sizeof(vectorKey));
    NSMutableData *wrongCipher = [cipher mutableCopy]; ((uint8_t *)wrongCipher.mutableBytes)[0] = 'x';
    NSCAssert(!DecryptOrganization(wrongCipher,@".claude.ai",vectorKey),@"unknown encryption rejected");
    NSString *cookiePath = [folder stringByAppendingPathComponent:@"Cookies"];
    sqlite3 *fixture = NULL;
    NSCAssert(sqlite3_open(cookiePath.fileSystemRepresentation,&fixture) == SQLITE_OK,@"synthetic cookie store");
    const char *schema = "CREATE TABLE meta(key TEXT,value TEXT); INSERT INTO meta VALUES('version','24');"
        "CREATE TABLE cookies(host_key TEXT,name TEXT,path TEXT,value TEXT,encrypted_value BLOB,has_expires INTEGER,"
        "expires_utc INTEGER,is_persistent INTEGER,source_scheme INTEGER,source_port INTEGER,top_frame_site_key TEXT);"
        "INSERT INTO cookies VALUES('.claude.ai','sessionKey','/','DO-NOT-READ',x'00',1,20000000000000000,1,2,443,'');"
        "INSERT INTO cookies VALUES('unrelated.invalid','lastActiveOrg','/','',x'00',1,20000000000000000,1,2,443,'');"
        "INSERT INTO cookies VALUES('.claude.ai','lastActiveOrg','/','',NULL,1,20000000000000000,1,2,443,'');";
    NSCAssert(sqlite3_exec(fixture,schema,NULL,NULL,NULL) == SQLITE_OK,@"synthetic schema");
    sqlite3_stmt *insert = NULL;
    NSCAssert(sqlite3_prepare_v2(fixture,"UPDATE cookies SET encrypted_value=? WHERE name='lastActiveOrg' AND host_key='.claude.ai'",-1,&insert,NULL) == SQLITE_OK,@"fixture statement");
    sqlite3_bind_blob(insert,1,cipher.bytes,(int)cipher.length,SQLITE_TRANSIENT);
    NSCAssert(sqlite3_step(insert) == SQLITE_DONE,@"fixture encryption"); sqlite3_finalize(insert); sqlite3_close(fixture);
    chmod(cookiePath.fileSystemRepresentation,0600);
    NSDictionary *(^readFixture)(NSString *) = ^(NSString *location) { return ReadUsageHistoryWithKey(location,now,testKey); };
    NSString *path = [folder stringByAppendingPathComponent:@"plan-usage-history.json"];
    NSCAssert(!readFixture(folder)[@"fh"],@"missing file");
    NSCAssert([encode() writeToFile:path atomically:YES],@"write fixture"); chmod(path.fileSystemRepresentation,0600);
    NSCAssert([readFixture(folder)[@"fh"] isEqual:@62],@"read safe fixture");
    __block NSUInteger selectedReads = 0;
    NSCAssert([ReadUsageHistoryWithKey(folder,now,^BOOL(uint8_t *key) { selectedReads++; return testKey(key); })[@"fh"] isEqual:@62] && selectedReads == 2,@"only selector is decrypted, before and after history");
    __block NSUInteger changingReads = 0;
    NSDictionary *changing = ReadUsageHistoryWithKey(folder,now,^BOOL(uint8_t *key) {
        if (++changingReads == 2) {
            sqlite3 *writer = NULL;
            NSCAssert(sqlite3_open(cookiePath.fileSystemRepresentation,&writer)==SQLITE_OK,@"synthetic concurrent writer");
            NSCAssert(sqlite3_exec(writer,"UPDATE cookies SET source_port=-1 WHERE name='lastActiveOrg'",NULL,NULL,NULL)==SQLITE_OK,@"synthetic concurrent change");
            sqlite3_close(writer);
        }
        return testKey(key);
    });
    NSCAssert(!changing[@"fh"] && [changing[@"status"] isEqual:@"组织记录正在更新"],@"discard organization changing during read");
    NSCAssert([ReadUsageHistoryWithKey(folder,now,^BOOL(uint8_t *key) { return NO; })[@"status"] isEqual:@"组织解密未授权"],@"no fallback on key denial");
    int dir = open(folder.fileSystemRepresentation,O_RDONLY|O_DIRECTORY); NSString *failure = nil;
    NSCAssert([ReadOrganization(dir,now,testKey,&failure) isEqual:org],@"only matching selector decrypted");
    NSCAssert(!ReadOrganization(dir,[NSDate dateWithTimeIntervalSince1970:10000000000],testKey,&failure),@"expired selector rejected");
    NSString *journal = [folder stringByAppendingPathComponent:@"Cookies-journal"];
    NSCAssert([[@"busy" dataUsingEncoding:NSUTF8StringEncoding] writeToFile:journal atomically:NO],@"active journal fixture");
    NSCAssert(!ReadOrganization(dir,now,testKey,&failure),@"active journal never read as current identity"); unlink(journal.fileSystemRepresentation);
    int cookieFD = open(cookiePath.fileSystemRepresentation,O_RDWR); uint8_t walHeader[2] = {2,2}, rollbackHeader[2] = {1,1};
    NSCAssert(pwrite(cookieFD,walHeader,2,18)==2,@"WAL fixture");
    NSCAssert(!ReadOrganization(dir,now,testKey,&failure),@"WAL mode must not use an obsolete main database");
    NSCAssert(pwrite(cookieFD,rollbackHeader,2,18)==2,@"restore synthetic header"); close(cookieFD);
    NSString *cookieAlias = [folder stringByAppendingPathComponent:@"cookie-link"];
    NSCAssert(link(cookiePath.fileSystemRepresentation,cookieAlias.fileSystemRepresentation)==0,@"cookie hardlink");
    NSCAssert(!ReadOrganization(dir,now,testKey,&failure),@"cookie hardlinks rejected"); unlink(cookieAlias.fileSystemRepresentation);
    // Authorizer rejects unrelated columns, writes and arbitrary schema-backed code.
    sqlite3 *restricted = NULL; NSCAssert(sqlite3_open(cookiePath.fileSystemRepresentation,&restricted)==SQLITE_OK,@"authorizer fixture");
    sqlite3_set_authorizer(restricted,UsageSQLAuthorizer,NULL);
    NSCAssert(sqlite3_exec(restricted,"DELETE FROM cookies",NULL,NULL,NULL)==SQLITE_AUTH,@"writes denied");
    NSCAssert(sqlite3_exec(restricted,"SELECT sql FROM sqlite_master",NULL,NULL,NULL)==SQLITE_AUTH,@"unrelated tables denied");
    sqlite3_close(restricted); close(dir);
    NSString *alias = [folder stringByAppendingString:@"-alias"];
    NSCAssert(symlink(folder.fileSystemRepresentation,alias.fileSystemRepresentation)==0,@"directory alias");
    NSCAssert(!readFixture(alias)[@"fh"],@"reject linked ancestor"); unlink(alias.fileSystemRepresentation);
    NSString *other = [folder stringByAppendingPathComponent:@"other"];
    NSCAssert(link(path.fileSystemRepresentation,other.fileSystemRepresentation)==0,@"hardlink fixture");
    NSCAssert(!readFixture(folder)[@"fh"],@"hardlink rejected"); unlink(other.fileSystemRepresentation);
    chmod(path.fileSystemRepresentation,0666); NSCAssert(!readFixture(folder)[@"fh"],@"writable file rejected");
    unlink(path.fileSystemRepresentation);
    NSCAssert(symlink("missing",path.fileSystemRepresentation)==0,@"file symlink");
    NSCAssert(!readFixture(folder)[@"fh"],@"file symlink rejected"); unlink(path.fileSystemRepresentation);
    NSCAssert(mkfifo(path.fileSystemRepresentation,0600)==0,@"FIFO fixture");
    NSCAssert(!readFixture(folder)[@"fh"],@"FIFO rejected without blocking"); unlink(path.fileSystemRepresentation);
    int large = open(path.fileSystemRepresentation,O_CREAT|O_WRONLY,0600); NSCAssert(large>=0,@"large fixture");
    NSCAssert(ftruncate(large,2*1024*1024+1)==0,@"size fixture"); close(large);
    NSCAssert(!readFixture(folder)[@"fh"],@"bounded read");
    NSCAssert([fm removeItemAtPath:folder error:nil],@"remove only synthetic fixture");
    puts("PASS: local usage history schema, missing/zero/invalid values, stale/future records, exact organization match, independent decryption vector, cookie filters, key denial, journal/WAL rejection, bounded reads and unsafe paths");
}
