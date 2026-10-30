#import "Handoff.h"
#import "Localization.h"
#import <fcntl.h>
#import <sys/stat.h>
#import <unistd.h>

static NSString * const RuleStart = @"<!-- claude-launcher:handoff -->";

NSString *HandoffRuleBlock(void) {
    return [@[RuleStart,
              L(@"## 交接文档（由 Claude 启动器添加）"),
              @"",
              L(@"本项目可能由多个 Claude 账号轮流继续。请在项目根目录维护 `HANDOFF.md`："),
              L(@"- 每完成一个阶段、做出重要决定，或预计会话即将结束时，立即更新它；"),
              L(@"- 内容包括：目标、已完成、进行中、下一步（按顺序）、关键文件与命令、已知问题与注意事项；"),
              L(@"- 写到让没有上下文的新会话能直接接手；"),
              L(@"- 新会话开始时，如果存在 `HANDOFF.md`，先阅读再继续。"),
              @"<!-- /claude-launcher:handoff -->",@""] componentsJoinedByString:@"\n"];
}

// Appends only: existing CLAUDE.md content is never rewritten. Links, non-regular files and other owners are refused.
NSString *AppendHandoffRule(NSString *directory, BOOL *alreadyPresent) {
    if (alreadyPresent) *alreadyPresent = NO;
    NSString *path = [directory stringByAppendingPathComponent:@"CLAUDE.md"];
    struct stat info; BOOL exists = lstat(path.fileSystemRepresentation, &info) == 0;
    if (!exists && errno != ENOENT) return L(@"无法读取 CLAUDE.md 的状态，未写入。");
    if (exists && !S_ISREG(info.st_mode)) return L(@"CLAUDE.md 是链接或不是普通文件，未写入。");
    if (exists && info.st_uid != getuid()) return L(@"CLAUDE.md 不属于当前用户，未写入。");
    if (exists && info.st_nlink != 1) return L(@"CLAUDE.md 有硬链接，追加会同时改动其他位置的文件，未写入。");
    if (exists && info.st_size > 1024*1024) return L(@"CLAUDE.md 超过 1 MB，未写入。");
    NSData *existing = exists ? [NSData dataWithContentsOfFile:path] : [NSData data];
    if (!existing) return L(@"无法读取 CLAUDE.md，未写入。");
    NSString *text = [[NSString alloc] initWithData:existing encoding:NSUTF8StringEncoding];
    if (!text) return L(@"CLAUDE.md 不是 UTF-8 文本，未写入。");
    if ([text containsString:RuleStart]) { if (alreadyPresent) *alreadyPresent = YES; return nil; }
    int fd = open(path.fileSystemRepresentation, O_WRONLY|O_APPEND|O_CREAT|O_NOFOLLOW|O_CLOEXEC, 0644);
    if (fd < 0) return [NSString stringWithFormat:L(@"无法打开 CLAUDE.md：%s。未写入。"),strerror(errno)];
    struct stat opened;
    if (fstat(fd, &opened) != 0 || !S_ISREG(opened.st_mode) || opened.st_uid != getuid() || opened.st_nlink != 1 || (exists && (opened.st_ino != info.st_ino || opened.st_size != info.st_size))) {
        close(fd); return L(@"CLAUDE.md 在检查期间发生变化，未写入。");
    }
    NSString *block = HandoffRuleBlock();
    if (text.length) block = [([text hasSuffix:@"\n"] ? @"\n" : @"\n\n") stringByAppendingString:block];
    NSData *data = [block dataUsingEncoding:NSUTF8StringEncoding];
    ssize_t written = write(fd, data.bytes, data.length);
    BOOL ok = written == (ssize_t)data.length && fsync(fd) == 0;
    close(fd);
    return ok ? nil : L(@"写入 CLAUDE.md 未完成，请检查文件末尾。");
}

NSDate *HandoffModified(NSString *directory) {
    struct stat info;
    if (lstat([directory stringByAppendingPathComponent:@"HANDOFF.md"].fileSystemRepresentation, &info) != 0 || !S_ISREG(info.st_mode)) return nil;
    return [NSDate dateWithTimeIntervalSince1970:info.st_mtimespec.tv_sec];
}

NSString *HandoffAge(NSDate *modified, NSDate *now) {
    if (!modified) return nil;
    NSTimeInterval seconds = MAX(0, [now timeIntervalSinceDate:modified]);
    if (seconds < 60) return L(@"刚刚");
    if (seconds < 3600) return [NSString stringWithFormat:L(@"%.0f 分钟前"),floor(seconds/60)];
    if (seconds < 86400) return [NSString stringWithFormat:L(@"%.0f 小时前"),floor(seconds/3600)];
    return [NSString stringWithFormat:L(@"%.0f 天前"),floor(seconds/86400)];
}

NSString *HandoffWritePrompt(void) {
    return L(@"请把当前工作整理成交接文档，写入项目根目录的 HANDOFF.md（已有则更新）：目标、已完成、进行中、下一步（按顺序）、关键文件与命令、已知问题与注意事项。写到让没有上下文的新会话能直接接手。写完后告诉我。");
}

NSString *HandoffContinuePrompt(NSString *directory) {
    return [NSString stringWithFormat:L(@"这是从另一个会话交接过来的工作。项目目录：%@\n请先阅读项目根目录的 HANDOFF.md，用几句话复述目标和下一步，然后继续完成剩余工作；过程中按 CLAUDE.md 的要求持续更新 HANDOFF.md。"),directory];
}

void TestHandoff(void) {
    NSString *dir = [NSTemporaryDirectory() stringByAppendingPathComponent:[NSUUID UUID].UUIDString];
    [NSFileManager.defaultManager createDirectoryAtPath:dir withIntermediateDirectories:YES attributes:nil error:nil];
    NSString *claude = [dir stringByAppendingPathComponent:@"CLAUDE.md"];
    BOOL present = YES;
    NSCAssert(!AppendHandoffRule(dir, &present) && !present, @"creates CLAUDE.md");
    NSCAssert([[NSString stringWithContentsOfFile:claude encoding:NSUTF8StringEncoding error:nil] isEqual:HandoffRuleBlock()], @"new file holds only the rule");
    NSCAssert(!AppendHandoffRule(dir, &present) && present, @"rule is not duplicated");
    [@"# 项目\n已有内容" writeToFile:claude atomically:NO encoding:NSUTF8StringEncoding error:nil];
    NSCAssert(!AppendHandoffRule(dir, &present) && !present, @"appends to existing file");
    NSString *after = [NSString stringWithContentsOfFile:claude encoding:NSUTF8StringEncoding error:nil];
    NSCAssert([after hasPrefix:@"# 项目\n已有内容\n\n<!-- claude-launcher:handoff -->"], @"existing content kept, rule appended after a blank line");
    [NSFileManager.defaultManager removeItemAtPath:claude error:nil];
    NSString *target = [dir stringByAppendingPathComponent:@"other.md"]; [@"x" writeToFile:target atomically:NO encoding:NSUTF8StringEncoding error:nil];
    [NSFileManager.defaultManager createSymbolicLinkAtPath:claude withDestinationPath:target error:nil];
    NSCAssert(AppendHandoffRule(dir, &present) && [[NSString stringWithContentsOfFile:target encoding:NSUTF8StringEncoding error:nil] isEqual:@"x"], @"symlinked CLAUDE.md refused");
    [NSFileManager.defaultManager removeItemAtPath:claude error:nil];
    [NSFileManager.defaultManager linkItemAtPath:target toPath:claude error:nil];
    NSCAssert(AppendHandoffRule(dir, &present) && [[NSString stringWithContentsOfFile:target encoding:NSUTF8StringEncoding error:nil] isEqual:@"x"], @"hard-linked CLAUDE.md refused");
    [NSFileManager.defaultManager removeItemAtPath:claude error:nil];
    [[NSData dataWithBytes:"\xff\xfe" length:2] writeToFile:claude atomically:NO];
    NSCAssert(AppendHandoffRule(dir, &present), @"non-UTF-8 CLAUDE.md refused");
    NSCAssert(!HandoffModified(dir), @"missing handoff");
    [@"进度" writeToFile:[dir stringByAppendingPathComponent:@"HANDOFF.md"] atomically:NO encoding:NSUTF8StringEncoding error:nil];
    NSCAssert(HandoffModified(dir), @"handoff modification time");
    NSDate *now = NSDate.date;
    NSCAssert([HandoffAge([now dateByAddingTimeInterval:-30], now) isEqual:@"刚刚"] && [HandoffAge([now dateByAddingTimeInterval:-720], now) isEqual:@"12 分钟前"] && [HandoffAge([now dateByAddingTimeInterval:-7300], now) isEqual:@"2 小时前"] && !HandoffAge(nil, now), @"handoff age");
    NSCAssert([HandoffContinuePrompt(@"/tmp/p") containsString:@"/tmp/p"] && [HandoffContinuePrompt(@"/tmp/p") containsString:@"HANDOFF.md"], @"continue prompt names project and file");
    [NSFileManager.defaultManager removeItemAtPath:dir error:nil];
    puts("PASS: handoff rule append-only (create, dedupe, keep content, refuse symlinks, hard links and non-UTF-8), HANDOFF.md metadata and age, prompts; temporary fixtures only");
}
