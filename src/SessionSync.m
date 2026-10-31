#import "SessionSync.h"
#import "Localization.h"
#import "ProjectImport.h"
#import <sys/stat.h>
#import <fcntl.h>
#import <unistd.h>

static const unsigned long long MaxSourceBytes = 512ull*1024*1024;
static const NSUInteger TailBytes = 256*1024, MaxSessions = 2000, MaxSlug = 200;

NSString *SessionProjectSlug(NSString *cwd) {
    // Matches Claude's replace(/[^a-zA-Z0-9]/g,"-"), which works on UTF-16 units.
    NSMutableString *slug = [NSMutableString stringWithCapacity:cwd.length];
    for (NSUInteger i = 0; i < cwd.length; i++) {
        unichar c = [cwd characterAtIndex:i];
        BOOL keep = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
        [slug appendString:keep ? [NSString stringWithCharacters:&c length:1] : @"-"];
    }
    return slug.length && slug.length <= MaxSlug ? slug : nil;
}
static BOOL SessionFileName(NSString *name) {
    return [name hasSuffix:@".jsonl"] && name.length == 42 && [[NSUUID alloc] initWithUUIDString:[name substringToIndex:36]] != nil;
}
static int OpenSource(NSString *path, struct stat *info) {
    int fd = open(path.fileSystemRepresentation,O_RDONLY|O_NOFOLLOW|O_NONBLOCK|O_CLOEXEC);
    if (fd >= 0 && (fstat(fd,info) != 0 || !S_ISREG(info->st_mode) || info->st_uid != getuid() || info->st_nlink != 1)) { close(fd); return -1; }
    return fd;
}
static NSString *TailTitle(NSString *path) {
    struct stat info; int fd = OpenSource(path,&info); if (fd < 0) return nil;
    off_t start = info.st_size > (off_t)TailBytes ? info.st_size-(off_t)TailBytes : 0;
    NSMutableData *data = [NSMutableData dataWithLength:(NSUInteger)(info.st_size-start)];
    ssize_t count = pread(fd,data.mutableBytes,data.length,start); close(fd);
    if (count <= 0) return nil;
    data.length = (NSUInteger)count;
    NSData *newline = [NSData dataWithBytes:"\n" length:1];
    NSString *title = nil; NSRange search = NSMakeRange(0,data.length);
    if (start > 0) {
        NSRange first = [data rangeOfData:newline options:0 range:search]; if (first.location == NSNotFound) return nil;
        search = NSMakeRange(NSMaxRange(first),data.length-NSMaxRange(first));
    }
    NSData *marker = [@"\"custom-title\"" dataUsingEncoding:NSUTF8StringEncoding];
    while (search.length) {
        NSRange end = [data rangeOfData:newline options:0 range:search]; if (end.location == NSNotFound) break;
        NSRange line = NSMakeRange(search.location,end.location-search.location);
        if ([data rangeOfData:marker options:0 range:line].location != NSNotFound) {
            NSDictionary *row = [NSJSONSerialization JSONObjectWithData:[data subdataWithRange:line] options:0 error:nil];
            if ([row isKindOfClass:NSDictionary.class] && [row[@"customTitle"] isKindOfClass:NSString.class]) title = row[@"customTitle"];
        }
        search = NSMakeRange(NSMaxRange(end),NSMaxRange(search)-NSMaxRange(end));
    }
    return title;
}
NSArray<NSDictionary *> *SessionSyncList(NSString *configFolder, NSString *project) {
    NSString *slug = SessionProjectSlug(project); if (!slug) return @[];
    NSString *directory = [[configFolder stringByAppendingPathComponent:@"projects"] stringByAppendingPathComponent:slug];
    struct stat info;
    if (lstat(directory.fileSystemRepresentation,&info) != 0 || !S_ISDIR(info.st_mode)) return @[];
    NSMutableArray *rows = [NSMutableArray array];
    for (NSString *name in [NSFileManager.defaultManager contentsOfDirectoryAtPath:directory error:nil]) {
        if (!SessionFileName(name) || rows.count >= MaxSessions) continue;
        NSString *path = [directory stringByAppendingPathComponent:name];
        if (lstat(path.fileSystemRepresentation,&info) != 0 || !S_ISREG(info.st_mode) || info.st_size == 0) continue;
        [rows addObject:@{@"path":path,@"title":TailTitle(path) ?: L(@"未命名会话"),
                          @"modified":[NSDate dateWithTimeIntervalSince1970:info.st_mtimespec.tv_sec]}];
    }
    [rows sortUsingDescriptors:@[[NSSortDescriptor sortDescriptorWithKey:@"modified" ascending:NO]]];
    return rows;
}

static NSString *Within(NSString *path, NSString *root) {
    // Returns the path relative to root, or nil when outside; compares whole components so /a/bc is not inside /a/b.
    NSArray *parts = path.stringByStandardizingPath.pathComponents, *base = root.stringByStandardizingPath.pathComponents;
    if (parts.count < base.count || ![[parts subarrayWithRange:NSMakeRange(0,base.count)] isEqual:base]) return nil;
    NSArray *rest = [parts subarrayWithRange:NSMakeRange(base.count,parts.count-base.count)];
    return rest.count ? [NSString pathWithComponents:rest] : @"";
}
static NSString *CleanText(NSString *text, NSString *root, NSString *home, NSMutableDictionary<NSString *,NSNumber *> *counts) {
    static NSRegularExpression *tags, *email, *key, *bearer;
    static dispatch_once_t once;
    dispatch_once(&once, ^{
        tags = [NSRegularExpression regularExpressionWithPattern:@"<(system-reminder|command-name|command-message|command-args|local-command-stdout|local-command-stderr|local-command-caveat|task-notification|ide_selection|ide_opened_file)\\b[^>]*>.*?</\\1>" options:NSRegularExpressionDotMatchesLineSeparators error:nil];
        email = [NSRegularExpression regularExpressionWithPattern:@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\\.[A-Za-z]{2,}" options:0 error:nil];
        key = [NSRegularExpression regularExpressionWithPattern:@"\\b(?:sk-[A-Za-z0-9_-]{16,}|gh[pousr]_[A-Za-z0-9]{20,}|AKIA[0-9A-Z]{16}|xox[baprs]-[A-Za-z0-9-]{10,})\\b" options:0 error:nil];
        bearer = [NSRegularExpression regularExpressionWithPattern:@"\\bBearer\\s+[A-Za-z0-9._~+/=-]{16,}" options:0 error:nil];
    });
    NSString *(^replace)(NSRegularExpression *,NSString *,NSString *,NSString *) = ^NSString *(NSRegularExpression *rx, NSString *input, NSString *with, NSString *name) {
        NSRange all = NSMakeRange(0,input.length); NSUInteger hits = [rx numberOfMatchesInString:input options:0 range:all];
        if (!hits) return input;
        counts[name] = @(counts[name].unsignedIntegerValue+hits);
        return [rx stringByReplacingMatchesInString:input options:0 range:all withTemplate:with];
    };
    text = replace(tags,text,@"",@"tags");
    text = replace(email,text,@"[已移除:邮箱]",@"redacted");
    text = replace(key,text,@"[已移除:密钥]",@"redacted");
    text = replace(bearer,text,@"[已移除:令牌]",@"redacted");
    NSRegularExpression *paths = [NSRegularExpression regularExpressionWithPattern:[[NSRegularExpression escapedPatternForString:home] stringByAppendingString:@"/[^\\s`'\")\\]]*"] options:0 error:nil];
    NSMutableString *result = text.mutableCopy;
    for (NSTextCheckingResult *match in [[paths matchesInString:text options:0 range:NSMakeRange(0,text.length)] reverseObjectEnumerator]) {
        NSString *relative = Within([text substringWithRange:match.range],root);
        if (!relative) counts[@"outsidePaths"] = @(counts[@"outsidePaths"].unsignedIntegerValue+1);
        [result replaceCharactersInRange:match.range withString:!relative ? @"[项目外路径已移除]" : relative.length ? relative : @"."];
    }
    return [result stringByTrimmingCharactersInSet:NSCharacterSet.whitespaceAndNewlineCharacterSet];
}
static NSString *ToolNote(NSDictionary *block, NSString *rowCwd, NSString *root) {
    NSString *name = [block[@"name"] isKindOfClass:NSString.class] ? block[@"name"] : @"unknown";
    NSDictionary *input = [block[@"input"] isKindOfClass:NSDictionary.class] ? block[@"input"] : @{};
    for (NSString *key in @[@"file_path",@"notebook_path"]) {
        NSString *path = input[key]; if (![path isKindOfClass:NSString.class] || !path.length) continue;
        NSString *relative = Within(path.isAbsolutePath ? path : [rowCwd stringByAppendingPathComponent:path],root);
        return relative ? [NSString stringWithFormat:@"[历史工具请求：%@，%@]",name,relative.length ? relative : @"."] : [NSString stringWithFormat:@"[历史工具请求：%@，项目外路径已省略]",name];
    }
    return [NSString stringWithFormat:@"[历史工具请求：%@]",name];
}
// Reads one snapshot; a line without its newline is an in-progress write and is ignored.
static NSString *ReadRows(NSString *path, NSMutableArray<NSMutableDictionary *> *rows, NSString * __strong *title, NSUInteger *sidechain) {
    struct stat before, after; int fd = OpenSource(path,&before);
    if (fd < 0) return L(@"无法安全打开来源会话文件。");
    if ((unsigned long long)before.st_size > MaxSourceBytes) { close(fd); return L(@"来源会话文件超过 512 MiB，未处理。"); }
    NSData *data = [[[NSFileHandle alloc] initWithFileDescriptor:fd closeOnDealloc:NO] readDataToEndOfFile];
    BOOL same = fstat(fd,&after) == 0 && after.st_size == before.st_size && after.st_mtimespec.tv_sec == before.st_mtimespec.tv_sec && after.st_mtimespec.tv_nsec == before.st_mtimespec.tv_nsec;
    close(fd);
    if (!same || data.length != (NSUInteger)before.st_size) return L(@"来源会话正在写入，请等它结束后重试。");
    NSData *newline = [NSData dataWithBytes:"\n" length:1]; NSRange search = NSMakeRange(0,data.length);
    while (search.length) {
        NSRange end = [data rangeOfData:newline options:0 range:search]; if (end.location == NSNotFound) break;
        @autoreleasepool {
            NSMutableDictionary *row = [NSJSONSerialization JSONObjectWithData:[data subdataWithRange:NSMakeRange(search.location,end.location-search.location)] options:NSJSONReadingMutableContainers error:nil];
            if (![row isKindOfClass:NSDictionary.class]) return L(@"来源会话含无法识别的行，未处理。");
            if ([row[@"type"] isEqual:@"custom-title"] && [row[@"customTitle"] isKindOfClass:NSString.class]) *title = row[@"customTitle"];
            if ([row[@"isSidechain"] boolValue]) (*sidechain)++;
            else if ([row[@"uuid"] isKindOfClass:NSString.class]) {
                // Keep only the shape of bulky blocks: the chain walk needs types, and they are dropped anyway.
                [row removeObjectForKey:@"toolUseResult"];
                NSMutableArray *content = [row[@"message"] isKindOfClass:NSDictionary.class] ? row[@"message"][@"content"] : nil;
                if ([content isKindOfClass:NSArray.class]) for (NSUInteger i = 0; i < content.count; i++) {
                    NSDictionary *block = content[i]; NSString *type = [block isKindOfClass:NSDictionary.class] ? block[@"type"] : nil;
                    if (type && ![type isEqual:@"text"] && ![type isEqual:@"tool_use"]) content[i] = @{@"type":type};
                }
                [rows addObject:row];
            }
        }
        search = NSMakeRange(NSMaxRange(end),NSMaxRange(search)-NSMaxRange(end));
    }
    return nil;
}
// Syncing a copy again replaces its old suffix instead of stacking another one.
static NSString *SessionCopyTitle(NSString *title, NSDate *date) {
    static NSRegularExpression *suffix; static NSDateFormatter *format; static dispatch_once_t once;
    dispatch_once(&once, ^{
        suffix = [NSRegularExpression regularExpressionWithPattern:@"(?:（清理副本）| \\(cleaned copy\\)|（副本 \\d\\d/\\d\\d \\d\\d:\\d\\d）| \\(Copy \\d\\d/\\d\\d \\d\\d:\\d\\d\\))$" options:0 error:nil];
        format = [NSDateFormatter new]; format.locale = [NSLocale localeWithLocaleIdentifier:@"en_US_POSIX"]; format.dateFormat = @"MM/dd HH:mm";
    });
    NSString *base = [suffix stringByReplacingMatchesInString:title options:0 range:NSMakeRange(0,title.length) withTemplate:@""];
    return [NSString stringWithFormat:L(@"%@（副本 %@）"),base,[format stringFromDate:date]];
}
NSDictionary *CleanSession(NSString *sourcePath, NSString *project, NSString *home) {
    NSMutableArray<NSMutableDictionary *> *rows = [NSMutableArray array]; NSString *title = nil; NSUInteger sidechain = 0;
    NSString *failure = ReadRows(sourcePath,rows,&title,&sidechain);
    if (failure) return @{@"error":failure};
    NSMutableDictionary *byId = [NSMutableDictionary dictionaryWithCapacity:rows.count]; NSMutableDictionary *leaf = nil;
    for (NSMutableDictionary *row in rows) {
        byId[row[@"uuid"]] = row;
        if ([row[@"type"] isEqual:@"user"] || [row[@"type"] isEqual:@"assistant"]) leaf = row;
    }
    if (!leaf) return @{@"error":L(@"来源会话没有可同步的对话。")};
    NSMutableArray *chain = [NSMutableArray array]; NSMutableSet *seen = [NSMutableSet set];
    for (NSDictionary *row = leaf; row && ![seen containsObject:row[@"uuid"]]; ) {
        [seen addObject:row[@"uuid"]]; [chain insertObject:row atIndex:0];
        id parent = row[@"parentUuid"];
        if (![parent isKindOfClass:NSString.class]) parent = row[@"logicalParentUuid"];
        row = [parent isKindOfClass:NSString.class] ? byId[parent] : nil;
    }
    // Where the session ended up decides its project: it may have started elsewhere (Claude moves scratch sessions into a folder).
    NSString *root = project.stringByStandardizingPath, *lastCwd = nil;
    for (NSDictionary *row in chain) if ([row[@"cwd"] isKindOfClass:NSString.class] && [row[@"cwd"] length]) lastCwd = row[@"cwd"];
    NSString *resolvedRoot = root.stringByResolvingSymlinksInPath;
    if (!lastCwd.isAbsolutePath || !Within(lastCwd.stringByStandardizingPath.stringByResolvingSymlinksInPath,resolvedRoot))
        return @{@"error":L(@"来源会话的工作目录与所选项目不一致，未处理。")};
    NSString *slug = SessionProjectSlug(project); if (!slug) return @{@"error":L(@"项目路径过长，暂不支持同步会话。")};

    NSMutableArray<NSMutableDictionary *> *turns = [NSMutableArray array];
    NSMutableDictionary<NSString *,NSNumber *> *counts = [NSMutableDictionary dictionary];
    void (^add)(NSString *,NSString *,id) = ^(NSString *role, NSString *text, id stamp) {
        if (!text.length) return;
        if ([turns.lastObject[@"role"] isEqual:role]) turns.lastObject[@"text"] = [NSString stringWithFormat:@"%@\n\n%@",turns.lastObject[@"text"],text];
        else [turns addObject:[@{@"role":role,@"text":text,@"timestamp":stamp ?: NSNull.null} mutableCopy]];
    };
    NSUInteger toolNotes = 0, toolResults = 0, thinking = 0, images = 0; __block NSString *currentCwd = root;
    // Later relative paths in the text only make sense against the directory the session was in.
    NSString *(^cwdNote)(NSString *,NSString *) = ^NSString *(NSString *rowCwd, NSString *relative) {
        if ([rowCwd.stringByStandardizingPath isEqual:currentCwd.stringByStandardizingPath]) return @"";
        currentCwd = rowCwd;
        return [NSString stringWithFormat:@"[工作目录：%@]\n",!relative ? @"项目外" : relative.length ? relative : @"项目根目录"];
    };
    for (NSDictionary *row in chain) {
        NSString *type = row[@"type"]; id stamp = row[@"timestamp"];
        NSString *rowCwd = [row[@"cwd"] isKindOfClass:NSString.class] ? row[@"cwd"] : currentCwd;
        // A link inside the project can point elsewhere, so a directory that exists is judged by its real path.
        BOOL exists = [NSFileManager.defaultManager fileExistsAtPath:rowCwd];
        NSString *relative = Within(exists ? rowCwd.stringByResolvingSymlinksInPath : rowCwd,exists ? resolvedRoot : root);
        NSDictionary *attachment = row[@"attachment"];
        if ([type isEqual:@"attachment"]) {
            if ([attachment isKindOfClass:NSDictionary.class] && [attachment[@"type"] isEqual:@"queued_command"] && [attachment[@"prompt"] isKindOfClass:NSString.class] &&
                (!attachment[@"commandMode"] || [attachment[@"commandMode"] isEqual:@"prompt"]))
                add(@"user",[cwdNote(rowCwd,relative) stringByAppendingString:CleanText(attachment[@"prompt"],root,home,counts)],stamp);
            continue;
        }
        if ((![type isEqual:@"user"] && ![type isEqual:@"assistant"]) || [row[@"isMeta"] boolValue] || [row[@"isApiErrorMessage"] boolValue] ||
            [row[@"isVisibleInTranscriptOnly"] boolValue] || [row[@"isCompactSummary"] boolValue]) continue;
        id content = [row[@"message"] isKindOfClass:NSDictionary.class] ? row[@"message"][@"content"] : nil;
        NSMutableArray *parts = [NSMutableArray array];
        if ([content isKindOfClass:NSString.class]) [parts addObject:CleanText(content,root,home,counts)];
        else if ([content isKindOfClass:NSArray.class]) for (NSDictionary *block in content) {
            NSString *kind = [block isKindOfClass:NSDictionary.class] ? block[@"type"] : nil;
            if ([kind isEqual:@"text"] && [block[@"text"] isKindOfClass:NSString.class]) {
                if (![block[@"text"] hasPrefix:@"[Request interrupted"]) [parts addObject:CleanText(block[@"text"],root,home,counts)];
            } else if ([kind isEqual:@"tool_use"]) { [parts addObject:ToolNote(block,rowCwd,root)]; toolNotes++; }
            else if ([kind isEqual:@"tool_result"]) toolResults++;
            else if ([kind isEqual:@"thinking"]) thinking++;
            else if ([kind isEqual:@"image"]) images++;
        }
        [parts filterUsingPredicate:[NSPredicate predicateWithFormat:@"length > 0"]];
        if (!parts.count) continue;
        add(type,[cwdNote(rowCwd,relative) stringByAppendingString:[parts componentsJoinedByString:@"\n"]],stamp);
    }
    while (turns.count && ![turns[0][@"role"] isEqual:@"user"]) [turns removeObjectAtIndex:0];
    if (!turns.count) return @{@"error":L(@"来源会话没有可同步的对话文字。")};
    turns[0][@"text"] = [@"[这是从其他账号同步的清理副本：只保留对话文字和工具请求说明，工具结果、思考内容和图片都已移除。历史中说已完成的改动，请以磁盘上的文件和 git 状态为准。]\n\n" stringByAppendingString:turns[0][@"text"]];
    if (![turns.lastObject[@"role"] isEqual:@"assistant"]) add(@"assistant",@"（同步的历史到此结束，上一条请求没有留下回复。）",turns.lastObject[@"timestamp"]);

    NSString *session = NSUUID.UUID.UUIDString.lowercaseString, *parent = nil;
    NSString *clean = SessionCopyTitle(title.length ? title : L(@"未命名会话"),NSDate.date);
    NSMutableData *jsonl = [NSMutableData data]; NSMutableString *body = [NSMutableString string];
    BOOL (^append)(NSDictionary *) = ^BOOL(NSDictionary *line) {
        NSData *json = [NSJSONSerialization dataWithJSONObject:line options:NSJSONWritingSortedKeys|NSJSONWritingWithoutEscapingSlashes error:nil];
        if (!json) return NO;
        [jsonl appendData:json]; [jsonl appendBytes:"\n" length:1]; return YES;
    };
    for (NSDictionary *turn in turns) {
        NSString *role = turn[@"role"], *identifier = NSUUID.UUID.UUIDString.lowercaseString;
        NSDictionary *message = [role isEqual:@"user"] ? @{@"role":role,@"content":turn[@"text"]} : @{@"role":role,@"type":@"message",@"content":@[@{@"type":@"text",@"text":turn[@"text"]}]};
        if (!append(@{@"type":role,@"uuid":identifier,@"parentUuid":parent ?: NSNull.null,@"isSidechain":@NO,@"sessionId":session,@"cwd":project,@"timestamp":turn[@"timestamp"],@"message":message}))
            return @{@"error":L(@"无法生成清理副本。")};
        parent = identifier;
        [body appendFormat:@"【%@】\n%@\n\n",[role isEqual:@"user"] ? L(@"用户") : L(@"助手"),turn[@"text"]];
    }
    if (!append(@{@"type":@"custom-title",@"customTitle":clean,@"sessionId":session})) return @{@"error":L(@"无法生成清理副本。")};
    NSNumber *now = @((long long)(NSDate.date.timeIntervalSince1970*1000));
    // Same leading fields as other records, so the directory prefix stays readable on a later scan.
    NSMutableArray *fields = [NSMutableArray array]; NSArray *keys = @[@"sessionId",@"cliSessionId",@"cwd",@"originCwd",@"title",@"createdAt",@"lastActivityAt",@"isArchived"];
    NSArray *values = @[[@"local_" stringByAppendingString:session],session,project,project,clean,now,now,@NO];
    for (NSUInteger i = 0; i < keys.count; i++) {
        NSData *json = [NSJSONSerialization dataWithJSONObject:values[i] options:NSJSONWritingFragmentsAllowed error:nil];
        if (!json) return @{@"error":L(@"无法生成清理副本。")};
        [fields addObject:[NSString stringWithFormat:@"\"%@\":%@",keys[i],[[NSString alloc] initWithData:json encoding:NSUTF8StringEncoding]]];
    }
    NSData *record = [[NSString stringWithFormat:@"{%@}",[fields componentsJoinedByString:@","]] dataUsingEncoding:NSUTF8StringEncoding];
    NSString *report = [NSString stringWithFormat:L(@"保留 %lu 条消息，%lu 条工具说明。\n丢弃：%lu 个工具结果、%lu 个思考块、%lu 张图片、%lu 行子代理记录、%lu 行非主链记录。\n替换：%lu 处邮箱／密钥／令牌，%lu 个项目外路径。"),
        (unsigned long)turns.count,(unsigned long)toolNotes,(unsigned long)toolResults,(unsigned long)thinking,(unsigned long)images,(unsigned long)sidechain,(unsigned long)(rows.count-chain.count),
        (unsigned long)counts[@"redacted"].unsignedIntegerValue,(unsigned long)counts[@"outsidePaths"].unsignedIntegerValue];
    return @{@"sessionId":session,@"slug":slug,@"cwd":project,@"title":clean,@"jsonl":jsonl,@"record":record,@"preview":body,@"report":report,@"messages":@(turns.count),@"bytes":@(jsonl.length),@"images":@(images)};
}

void TestSessionSync(void) {
    NSFileManager *fm = NSFileManager.defaultManager;
    NSString *root = [fm.currentDirectoryPath stringByAppendingPathComponent:[@".claude-session-sync-tests-" stringByAppendingString:NSUUID.UUID.UUIDString]];
    NSString *home = [root stringByAppendingPathComponent:@"home"], *project = [home stringByAppendingPathComponent:@"Project"], *sub = [project stringByAppendingPathComponent:@"windows"];
    NSString *profile = [home stringByAppendingPathComponent:@"Profile"], *config = [profile stringByAppendingPathComponent:@"ClaudeCodeConfig"];
    NSString *slug = SessionProjectSlug(project);
    NSCAssert([SessionProjectSlug(@"/Users/a b/中.x") isEqual:@"-Users-a-b---x"] && !SessionProjectSlug([@"/" stringByPaddingToLength:201 withString:@"a" startingAtIndex:0]),@"slug rule and length limit");
    NSString *folder = [[config stringByAppendingPathComponent:@"projects"] stringByAppendingPathComponent:slug];
    NSString *records = [[[profile stringByAppendingPathComponent:@"claude-code-sessions"] stringByAppendingPathComponent:NSUUID.UUID.UUIDString] stringByAppendingPathComponent:NSUUID.UUID.UUIDString];
    @try {
        NSCAssert([fm createDirectoryAtPath:sub withIntermediateDirectories:YES attributes:@{NSFilePosixPermissions:@0700} error:nil] && [fm createDirectoryAtPath:folder withIntermediateDirectories:YES attributes:@{NSFilePosixPermissions:@0700} error:nil] && [fm createDirectoryAtPath:records withIntermediateDirectories:YES attributes:@{NSFilePosixPermissions:@0700} error:nil],@"fixture directories");
        NSString *outside = [home stringByAppendingPathComponent:@"Secret/plan.txt"], *inside = [project stringByAppendingPathComponent:@"src/a.m"];
        NSMutableArray *lines = [NSMutableArray array];
        void (^row)(NSDictionary *) = ^(NSDictionary *value) { [lines addObject:[[NSString alloc] initWithData:[NSJSONSerialization dataWithJSONObject:value options:0 error:nil] encoding:NSUTF8StringEncoding]]; };
        NSDictionary *(^message)(NSString *,NSString *,NSString *,NSString *,id) = ^NSDictionary *(NSString *type, NSString *uuid, NSString *parent, NSString *cwd, id content) {
            return @{@"type":type,@"uuid":uuid,@"parentUuid":parent ?: NSNull.null,@"isSidechain":@NO,@"cwd":cwd,@"timestamp":@"2026-10-30T10:00:00.000Z",@"sessionId":@"SOURCESESSIONID",@"entrypoint":@"claude-desktop",@"requestId":@"SOURCEREQUEST",@"permissionMode":@"bypassPermissions",
                     @"message":@{@"role":type,@"model":@"SOURCEMODEL",@"id":@"SOURCEMSG",@"usage":@{@"input_tokens":@1},@"content":content}};
        };
        row(@{@"type":@"custom-title",@"customTitle":@"原标题",@"sessionId":@"SOURCESESSIONID"});
        row(@{@"type":@"api-request",@"apiKey":@"SOURCEAPIROW"});
        row(message(@"user",@"u1",nil,project,[NSString stringWithFormat:@"<system-reminder>INJECTED</system-reminder>第一问 me@example.com sk-abcdefghijklmnopqrstu %@ %@",outside,inside]));
        row(message(@"assistant",@"a1",@"u1",project,@[@{@"type":@"thinking",@"thinking":@"PRIVATETHOUGHT",@"signature":@"SOURCESIGNATURE"},@{@"type":@"text",@"text":@"答一"},
            @{@"type":@"tool_use",@"id":@"t1",@"name":@"Edit",@"input":@{@"file_path":inside,@"old_string":@"TOOLINPUT"}},@{@"type":@"tool_use",@"id":@"t2",@"name":@"Read",@"input":@{@"file_path":outside}},@{@"type":@"tool_use",@"id":@"t3",@"name":@"Bash",@"input":@{@"command":@"SECRETCOMMAND"}}]));
        row(message(@"user",@"r1",@"a1",sub,@[@{@"type":@"tool_result",@"tool_use_id":@"t1",@"content":@"TOOLOUTPUT"},@{@"type":@"image",@"source":@{@"data":@"IMAGEDATA"}}]));
        row(message(@"assistant",@"dead",@"r1",sub,@[@{@"type":@"text",@"text":@"ABANDONEDBRANCH"}]));
        [lines addObject:[[NSString alloc] initWithData:[NSJSONSerialization dataWithJSONObject:@{@"type":@"assistant",@"uuid":@"side",@"parentUuid":@"r1",@"isSidechain":@YES,@"cwd":project,@"message":@{@"role":@"assistant",@"content":@"SIDECHAINTEXT"}} options:0 error:nil] encoding:NSUTF8StringEncoding]];
        row(@{@"type":@"attachment",@"uuid":@"q1",@"parentUuid":@"r1",@"cwd":sub,@"timestamp":@"2026-10-30T10:01:00.000Z",@"attachment":@{@"type":@"queued_command",@"prompt":@"排队指令",@"organizationUuid":@"SOURCEORG"}});
        row(@{@"type":@"system",@"subtype":@"compact_boundary",@"uuid":@"b1",@"parentUuid":NSNull.null,@"logicalParentUuid":@"q1",@"cwd":sub});
        NSMutableDictionary *summary = [message(@"user",@"s1",@"b1",sub,@"COMPACTSUMMARY") mutableCopy]; summary[@"isCompactSummary"] = @YES; row(summary);
        row(message(@"assistant",@"a2",@"s1",project,@[@{@"type":@"text",@"text":@"答二"}]));
        NSString *name = [NSUUID.UUID.UUIDString.lowercaseString stringByAppendingString:@".jsonl"], *source = [folder stringByAppendingPathComponent:name];
        NSString *content = [[lines componentsJoinedByString:@"\n"] stringByAppendingString:@"\n{\"type\":\"user\",\"uuid\":\"partial"];
        NSCAssert([content writeToFile:source atomically:NO encoding:NSUTF8StringEncoding error:nil],@"fixture session");
        NSArray *listed = SessionSyncList(config,project);
        NSCAssert(listed.count == 1 && [listed[0][@"title"] isEqual:@"原标题"] && [listed[0][@"path"] isEqual:source],@"lists session with tail title");
        NSDictionary *clean = CleanSession(source,project,home);
        NSCAssert(!clean[@"error"],@"clean succeeds: %@",clean[@"error"]);
        NSString *text = [[NSString alloc] initWithData:clean[@"jsonl"] encoding:NSUTF8StringEncoding];
        for (NSString *forbidden in @[@"SOURCE",@"PRIVATETHOUGHT",@"TOOLINPUT",@"TOOLOUTPUT",@"SECRETCOMMAND",@"IMAGEDATA",@"ABANDONEDBRANCH",@"SIDECHAINTEXT",@"COMPACTSUMMARY",@"INJECTED",@"me@example.com",@"sk-abc",@"Secret/plan",@"partial"])
            NSCAssert(![text containsString:forbidden] && ![clean[@"preview"] containsString:forbidden],@"not carried: %@",forbidden);
        for (NSString *kept in @[@"第一问",@"答一",@"答二",@"排队指令",@"src/a.m",@"[项目外路径已移除]",@"[已移除:邮箱]",@"[已移除:密钥]",@"[历史工具请求：Edit，src/a.m]",@"[历史工具请求：Read，项目外路径已省略]",@"[历史工具请求：Bash]",@"[工作目录：windows]",@"[工作目录：项目根目录]",@"原标题（副本 "])
            NSCAssert([text containsString:kept],@"kept: %@",kept);
        NSArray *out = [[text stringByTrimmingCharactersInSet:NSCharacterSet.newlineCharacterSet] componentsSeparatedByString:@"\n"];
        NSCAssert(out.count == [clean[@"messages"] unsignedIntegerValue]+1,@"one row per merged message plus title");
        NSSet *allowed = [NSSet setWithArray:@[@"type",@"uuid",@"parentUuid",@"isSidechain",@"sessionId",@"cwd",@"timestamp",@"message"]];
        NSString *previous = nil, *role = nil;
        for (NSString *line in [out subarrayWithRange:NSMakeRange(0,out.count-1)]) {
            NSDictionary *value = [NSJSONSerialization JSONObjectWithData:[line dataUsingEncoding:NSUTF8StringEncoding] options:0 error:nil];
            NSCAssert([[NSSet setWithArray:value.allKeys] isEqual:allowed] && [value[@"sessionId"] isEqual:clean[@"sessionId"]] && [value[@"cwd"] isEqual:project],@"whitelisted row");
            NSCAssert(previous ? [value[@"parentUuid"] isEqual:previous] : value[@"parentUuid"] == NSNull.null,@"rebuilt linear chain");
            NSCAssert(![value[@"type"] isEqual:role],@"roles alternate"); previous = value[@"uuid"]; role = value[@"type"];
        }
        NSCAssert([role isEqual:@"assistant"],@"ends with an assistant message");
        NSDictionary *record = [NSJSONSerialization JSONObjectWithData:clean[@"record"] options:0 error:nil];
        NSCAssert(record.count == 8 && [record[@"cliSessionId"] isEqual:clean[@"sessionId"]] && [record[@"sessionId"] isEqual:[@"local_" stringByAppendingString:clean[@"sessionId"]]] && [record[@"isArchived"] isEqual:@NO],@"eight-field record");

        NSDate *fixed = [NSDate dateWithTimeIntervalSince1970:1800000000];
        NSCAssert([[SessionCopyTitle(SessionCopyTitle(@"甲",fixed),fixed) componentsSeparatedByString:@"副本"] count] == 2 && [SessionCopyTitle(@"甲（清理副本）",fixed) hasPrefix:@"甲（副本 "],@"copy suffix is replaced, not stacked");
        NSCAssert(CleanSession(source,sub,home)[@"error"] != nil,@"root must be the selected project");
        NSString *wrong = [folder stringByAppendingPathComponent:[NSUUID.UUID.UUIDString.lowercaseString stringByAppendingString:@".jsonl"]];
        [[@"{\"type\":\"user\",\"uuid\":\"u\",\"parentUuid\":null,\"cwd\":\"" stringByAppendingFormat:@"%@\",\"message\":{\"role\":\"user\",\"content\":\"a\"}}\n{\"type\":\"assistant\",\"uuid\":\"v\",\"parentUuid\":\"u\",\"cwd\":\"%@\",\"message\":{\"role\":\"assistant\",\"content\":\"b\"}}\n",project,home] writeToFile:wrong atomically:NO encoding:NSUTF8StringEncoding error:nil];
        NSCAssert(CleanSession(wrong,project,home)[@"error"] != nil,@"a session that ended outside the project is refused");
        NSString *scratch = [home stringByAppendingPathComponent:@"scratch-gone"];
        [[NSString stringWithFormat:@"{\"type\":\"user\",\"uuid\":\"u\",\"parentUuid\":null,\"cwd\":\"%@\",\"message\":{\"role\":\"user\",\"content\":\"a\"}}\n{\"type\":\"assistant\",\"uuid\":\"v\",\"parentUuid\":\"u\",\"cwd\":\"%@\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"b\"}]}}\n",scratch,project] writeToFile:wrong atomically:NO encoding:NSUTF8StringEncoding error:nil];
        NSDictionary *moved = CleanSession(wrong,project,home);
        NSCAssert(!moved[@"error"] && [moved[@"preview"] containsString:@"[工作目录：项目外]"] && ![moved[@"preview"] containsString:@"scratch-gone"],@"a session moved into the project is accepted without its old path");
        [@"not json\n" writeToFile:wrong atomically:NO encoding:NSUTF8StringEncoding error:nil];
        NSCAssert(CleanSession(wrong,project,home)[@"error"] != nil,@"unknown line is refused");

        NSString *targetFolder = [[config stringByAppendingPathComponent:@"projects"] stringByAppendingPathComponent:@"-new-project-slug"];
        NSString *targetFile = [targetFolder stringByAppendingPathComponent:[clean[@"sessionId"] stringByAppendingString:@".jsonl"]];
        NSString *recordFile = [records stringByAppendingPathComponent:[NSString stringWithFormat:@"local_%@.json",clean[@"sessionId"]]];
        NSString *(^safe)(void) = ^NSString *{ return nil; };
        NSCAssert(PublishSyncedSession(profile,config,@"-new-project-slug",clean[@"sessionId"],clean[@"jsonl"],clean[@"record"],^NSString *{ return @"running"; }) != nil && ![fm fileExistsAtPath:targetFile],@"running target refused");
        __block NSUInteger calls = 0;
        NSCAssert(PublishSyncedSession(profile,config,@"-new-project-slug",clean[@"sessionId"],clean[@"jsonl"],clean[@"record"],^NSString *{ return ++calls >= 2 ? @"started" : nil; }) != nil,@"late guard failure refused");
        NSCAssert(![fm fileExistsAtPath:targetFile] && ![fm fileExistsAtPath:recordFile],@"failure leaves neither file");
        NSCAssert(!PublishSyncedSession(profile,config,@"-new-project-slug",clean[@"sessionId"],clean[@"jsonl"],clean[@"record"],safe),@"publish succeeds");
        NSCAssert([[NSData dataWithContentsOfFile:targetFile] isEqual:clean[@"jsonl"]] && [[NSData dataWithContentsOfFile:recordFile] isEqual:clean[@"record"]],@"exact bytes published");
        for (NSString *path in @[targetFile,recordFile]) { struct stat info; NSCAssert(lstat(path.fileSystemRepresentation,&info) == 0 && (info.st_mode & 0777) == 0600,@"private files"); }
        NSCAssert(PublishSyncedSession(profile,config,@"-new-project-slug",clean[@"sessionId"],[@"other" dataUsingEncoding:NSUTF8StringEncoding],clean[@"record"],safe) != nil && [[NSData dataWithContentsOfFile:targetFile] isEqual:clean[@"jsonl"]],@"never overwrites");
        NSCAssert([fm contentsOfDirectoryAtPath:targetFolder error:nil].count == 1 && [fm contentsOfDirectoryAtPath:records error:nil].count == 1,@"no temporary files left");
        NSString *second = [[profile stringByAppendingPathComponent:@"claude-code-sessions"] stringByAppendingPathComponent:NSUUID.UUID.UUIDString];
        [fm createDirectoryAtPath:second withIntermediateDirectories:YES attributes:@{NSFilePosixPermissions:@0700} error:nil];
        NSCAssert(PublishSyncedSession(profile,config,@"-new-project-slug",NSUUID.UUID.UUIDString.lowercaseString,clean[@"jsonl"],clean[@"record"],safe) != nil,@"ambiguous account location refused");
        puts("PASS: session sync rebuilds from a whitelist, drops account data and publishes without overwriting");
    } @finally { [fm removeItemAtPath:root error:nil]; }
}
