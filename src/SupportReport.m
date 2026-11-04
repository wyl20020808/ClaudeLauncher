#import "SupportReport.h"
#import "SessionSync.h"
#import "ProjectImport.h"
#import "Localization.h"

NSString *const SupportIssuesNew = @"https://github.com/wyl20020808/ClaudeLauncher/issues/new";
static const NSUInteger MaxListed = 8, MaxURL = 7500;

@implementation SupportInput
@end

NSString *SupportMask(NSString *path, NSString *home) {
    if (!path.length) return @"（空）";
    NSString *normal = path.stringByStandardizingPath, *base = home.stringByStandardizingPath;
    if ([normal isEqual:@"/"]) return @"/";
    if ([normal isEqual:base]) return @"~";
    return [([normal hasPrefix:[base stringByAppendingString:@"/"]] ? @"~/…/" : @"/…/") stringByAppendingString:normal.lastPathComponent];
}
static NSString *Two(NSInteger n) { return [NSString stringWithFormat:@"%02ld",(long)n]; }
// Report text is Chinese only: it is written for the developer (SPEC 问题诊断).
static NSString *Join(NSArray *items, NSString *empty) { return items.count ? [items componentsJoinedByString:@"、"] : empty; }

NSArray<NSString *> *SupportRecentErrors(NSArray<NSDictionary *> *logs, NSString *home) {
    NSDateFormatter *format = [NSDateFormatter new]; format.locale = [NSLocale localeWithLocaleIdentifier:@"en_US_POSIX"]; format.dateFormat = @"MM-dd HH:mm";
    NSMutableArray *merged = [NSMutableArray array]; NSString *last = nil; NSUInteger repeat = 0;
    for (NSDictionary *entry in logs.reverseObjectEnumerator) {
        if (![entry isKindOfClass:NSDictionary.class] || ![entry[@"level"] isEqual:@"错误"] || ![entry[@"text"] isKindOfClass:NSString.class]) continue;
        NSString *text = [[entry[@"text"] componentsSeparatedByString:@"\n"].firstObject stringByTrimmingCharactersInSet:NSCharacterSet.whitespaceCharacterSet];
        if (home.length) text = [text stringByReplacingOccurrencesOfString:home withString:@"~"];
        if (text.length > 160) text = [[text substringToIndex:[text rangeOfComposedCharacterSequenceAtIndex:159].location] stringByAppendingString:@"…"];
        id date = entry[@"date"];
        NSString *time = [date isKindOfClass:NSDate.class] ? [format stringFromDate:date] : [date isKindOfClass:NSNumber.class] ? [format stringFromDate:[NSDate dateWithTimeIntervalSince1970:[date doubleValue]]] : @"";
        NSString *line = [NSString stringWithFormat:@"%@  %@",time,text];
        if ([text isEqual:last]) { repeat++; merged[merged.count-1] = [line stringByAppendingFormat:@"（连续 %lu 次）",(unsigned long)repeat]; continue; }
        last = text; repeat = 1; [merged addObject:line];
    }
    return merged.count > 15 ? [merged subarrayWithRange:NSMakeRange(merged.count-15,15)] : merged;
}

static BOOL FoundElsewhere(NSString *config, NSString *identifier) {
    NSString *projects = [config stringByAppendingPathComponent:@"projects"]; NSUInteger seen = 0;
    for (NSString *folder in [NSFileManager.defaultManager contentsOfDirectoryAtPath:projects error:nil]) {
        if (++seen > 2000) break;
        if ([NSFileManager.defaultManager fileExistsAtPath:[[projects stringByAppendingPathComponent:folder] stringByAppendingPathComponent:[identifier stringByAppendingString:@".jsonl"]]]) return YES;
    }
    return NO;
}

NSDictionary *BuildSupportReport(SupportInput *input) {
    NSMutableArray *problems = [NSMutableArray array], *notices = [NSMutableArray array];
    NSMutableString *body = [NSMutableString string]; NSString *home = input.home ?: NSHomeDirectory();
    NSFileManager *fm = NSFileManager.defaultManager;

    // ---- Projects ----
    NSMutableDictionary<NSString *,NSString *> *label = [NSMutableDictionary dictionary];
    NSMutableArray<NSDictionary *> *usable = [NSMutableArray array]; NSMutableSet *real = [NSMutableSet set];
    [body appendFormat:@"== 项目清单（%lu 个）==\n",(unsigned long)input.projects.count];
    if (input.projectsUnreadable) [problems addObject:@"项目清单文件读不了，同步窗口不会列出任何项目。请在项目页重新添加项目，或把诊断发给开发者。"];
    for (NSUInteger i = 0; i < input.projects.count; i++) {
        NSDictionary *project = input.projects[i]; NSString *path = project[@"path"];
        if (![path isKindOfClass:NSString.class] || !path.length) continue;
        NSString *name = [project[@"name"] length] ? project[@"name"] : path.lastPathComponent;
        NSString *tag = [NSString stringWithFormat:@"P%lu「%@」",(unsigned long)i+1,name];
        if (!label[path]) label[path] = tag;
        NSString *why = input.projectProblem ? input.projectProblem(path) : nil, *resolved = path.stringByResolvingSymlinksInPath;
        if (!why && [real containsObject:resolved]) why = @"和前面的项目重复";
        if (!why) { [usable addObject:project]; [real addObject:resolved]; }
        else if ([why isEqual:@"和前面的项目重复"]) [notices addObject:[tag stringByAppendingString:@" 和前面的项目是同一个目录，会合并显示。"]];
        else [problems addObject:[NSString stringWithFormat:@"%@ 在同步窗口里不会出现：%@。",tag,why]];
        [body appendFormat:@"  %@  %@%@\n",tag,SupportMask(path,home),why ? [NSString stringWithFormat:@"  [不可用：%@]",why] : @""];
    }
    NSMutableDictionary *nameOwners = [NSMutableDictionary dictionary];
    for (NSDictionary *project in usable) if (!nameOwners[[project[@"path"] lastPathComponent]]) nameOwners[[project[@"path"] lastPathComponent]] = project[@"path"];

    // ---- Accounts: the same listing code as the sync window, with skip reasons ----
    NSMutableArray<NSDictionary *> *results = [NSMutableArray array];
    for (NSNumber *account in input.accounts) {
        NSInteger n = account.integerValue; SessionSyncScan *scan = [SessionSyncScan new];
        NSString *config = input.config(n), *profile = input.profile(n);
        NSArray *rows = SessionSyncRows(config,profile,usable,scan);
        NSMutableDictionary<NSString *,NSMutableDictionary *> *counts = [NSMutableDictionary dictionary];
        for (NSDictionary *project in usable) counts[project[@"path"]] = [@{@"listed":@0,@"archived":@0,@"missing":@0,@"elsewhere":@0,@"other":@0,@"old":@(SessionSyncList(config,project[@"path"]).count)} mutableCopy];
        void (^bump)(NSString *,NSString *) = ^(NSString *path, NSString *key) { if (path && counts[path]) counts[path][key] = @([counts[path][key] integerValue]+1); };
        for (NSDictionary *row in rows) bump(row[@"project"][@"path"],@"listed");
        for (NSArray *pair in scan.archived) bump(SessionProjectFor(pair[1],usable)[@"path"],@"archived");
        for (NSDictionary *skip in scan.skipped) {
            NSString *path = skip[@"project"][@"path"]; if (!path) continue;
            if ([skip[@"reason"] isEqual:@"missing"]) { bump(path,@"missing"); if (FoundElsewhere(config,skip[@"id"])) bump(path,@"elsewhere"); }
            else bump(path,@"other");
        }
        NSString *target = input.targetGuard ? input.targetGuard(n) : nil;
        NSInteger format = SyncTargetRecordFormat(profile);
        [results addObject:@{@"number":account,@"scan":scan,@"counts":counts,@"target":target ?: @"",@"format":@(format),@"config":config ?: @""}];
    }
    NSInteger (^sum)(NSString *,NSString *) = ^NSInteger(NSString *path, NSString *key) { NSInteger total = 0; for (NSDictionary *r in results) total += [r[@"counts"][path][key] integerValue]; return total; };
    NSMutableArray *shown = [NSMutableArray array], *hidden = [NSMutableArray array];
    for (NSDictionary *project in usable) [(sum(project[@"path"],@"listed") > 0 ? shown : hidden) addObject:project[@"path"]];
    for (NSString *path in hidden) {
        NSInteger archived = sum(path,@"archived"), missing = sum(path,@"missing"), elsewhere = sum(path,@"elsewhere"), other = sum(path,@"other"), old = sum(path,@"old");
        NSMutableArray *reasons = [NSMutableArray array];
        if (archived) [reasons addObject:[NSString stringWithFormat:@"%ld 条会话在来源账号里是“已归档”（同步只列未归档的；Claude 的自动归档设置也会归档）",(long)archived]];
        if (elsewhere) [reasons addObject:[NSString stringWithFormat:@"%ld 条会话的正文不在记录指向的位置（启动器的问题，请提交诊断）",(long)elsewhere]];
        if (missing-elsewhere > 0) [reasons addObject:[NSString stringWithFormat:@"%ld 条会话找不到正文文件",(long)(missing-elsewhere)]];
        if (other) [reasons addObject:[NSString stringWithFormat:@"%ld 条会话的正文为空、是链接或路径太长",(long)other]];
        if (!reasons.count && old) [reasons addObject:[NSString stringWithFormat:@"项目目录下有 %ld 个旧会话文件，但没有对应的官方会话记录（多为改模型或回退留下的旧段、已删除的会话）",(long)old]];
        if (!reasons.count) [reasons addObject:@"各账号里都没有这个项目的会话记录"];
        [elsewhere ? problems : notices addObject:[NSString stringWithFormat:@"%@ 在同步窗口里不显示：%@。",label[path],[reasons componentsJoinedByString:@"；"]]];
    }
    for (NSDictionary *r in results) {
        NSString *who = [@"账号 " stringByAppendingString:Two([r[@"number"] integerValue])]; SessionSyncScan *scan = r[@"scan"];
        if ([r[@"format"] integerValue] < 0 && scan.records) [notices addObject:[who stringByAppendingString:@" 有多套账号／组织记录目录（登录过多个账号或组织），列表会合并读取；作为目标时启动器会按它当前的组织确定写入位置。"]];
        for (NSString *text in scan.problems) [problems addObject:[NSString stringWithFormat:@"%@：%@。这一部分的会话不会出现在同步窗口里。",who,text]];
        if (scan.unreadable) [notices addObject:[NSString stringWithFormat:@"%@ 有 %lu 条会话记录打不开（是链接、有多个硬链接、超过 8 MiB、他人可写或不属于当前用户），这些会话不会列出。",who,(unsigned long)scan.unreadable]];
        if (scan.invalid) [notices addObject:[NSString stringWithFormat:@"%@ 有 %lu 条会话记录格式不对（不是合法 JSON 或缺少会话 ID、工作目录），这些会话不会列出。",who,(unsigned long)scan.invalid]];
        if (scan.noProjectsFolder && scan.active.count) [problems addObject:[who stringByAppendingString:@" 有未归档的会话记录，但 Claude Code 配置目录里没有 projects 文件夹（或它是链接），这个账号的会话都不会列出。"]];
        NSMutableDictionary<NSString *,NSNumber *> *moved = [NSMutableDictionary dictionary];
        for (NSDictionary *skip in scan.skipped) if ([skip[@"reason"] isEqual:@"outside"] && nameOwners[[skip[@"cwd"] lastPathComponent]]) moved[skip[@"cwd"]] = @(moved[skip[@"cwd"]].integerValue+1);
        NSUInteger listed = 0;
        for (NSString *cwd in [moved.allKeys sortedArrayUsingSelector:@selector(compare:)]) {
            if (++listed > MaxListed) break;
            [notices addObject:[NSString stringWithFormat:@"%@ 有 %@ 条会话的工作目录是 %@，和项目 %@ 同名但路径不同（项目可能搬过家）。它们不会归到这个项目；把这个目录也加进项目清单就能看到。",who,moved[cwd],SupportMask(cwd,home),label[nameOwners[cwd.lastPathComponent]]]];
        }
        if ([r[@"target"] length]) [notices addObject:[NSString stringWithFormat:@"%@ 现在不能作为同步目标：%@",who,r[@"target"]]];
    }

    // ---- Body ----
    NSMutableArray *shownText = [NSMutableArray array], *hiddenText = [NSMutableArray array];
    for (NSString *path in shown) [shownText addObject:[NSString stringWithFormat:@"%@（%ld）",label[path],(long)sum(path,@"listed")]];
    for (NSString *path in hidden) [hiddenText addObject:label[path]];
    [body appendFormat:@"\n== 同步窗口 ==\n  会显示的项目（括号里是各来源账号合计的会话数）：%@\n  不会显示的项目：%@\n",Join(shownText,@"无"),Join(hiddenText,@"无")];
    for (NSDictionary *r in results) {
        NSInteger number = [r[@"number"] integerValue]; SessionSyncScan *scan = r[@"scan"]; NSInteger format = [r[@"format"] integerValue];
        [body appendFormat:@"\n== 账号 %@ ==\n  Claude Code 配置目录：%@\n",Two(number),[fm fileExistsAtPath:r[@"config"]] ? @"有" : @"没有"];
        [body appendFormat:@"  会话记录：共 %lu 条；未归档 %lu，已归档 %lu，还没开始对话 %lu，打不开 %lu，格式不对 %lu\n",(unsigned long)scan.records,(unsigned long)scan.active.count,(unsigned long)scan.archived.count,(unsigned long)scan.unstarted,(unsigned long)scan.unreadable,(unsigned long)scan.invalid];
        for (NSString *text in scan.problems) [body appendFormat:@"  [跳过] %@\n",text];
        [body appendString:@"  按项目（可同步／已归档／缺正文／其他原因跳过／项目目录下的会话文件数）：\n"];
        for (NSDictionary *project in usable) {
            NSDictionary *c = r[@"counts"][project[@"path"]];
            [body appendFormat:@"    %@：%@ ／ %@ ／ %@%@ ／ %@ ／ %@\n",label[project[@"path"]],c[@"listed"],c[@"archived"],c[@"missing"],[c[@"elsewhere"] integerValue] ? [NSString stringWithFormat:@"（其中 %@ 条正文在别的目录）",c[@"elsewhere"]] : @"",c[@"other"],c[@"old"]];
        }
        NSMutableDictionary<NSString *,NSNumber *> *outside = [NSMutableDictionary dictionary];
        for (NSDictionary *skip in scan.skipped) if ([skip[@"reason"] isEqual:@"outside"]) {
            NSString *where = [skip[@"cwd"] containsString:@"/scratch-workspaces/"] ? @"临时工作区（scratch-workspaces）" : SupportMask(skip[@"cwd"],home);
            outside[where] = @(outside[where].integerValue+1);
        }
        if (outside.count) {
            NSArray *keys = [outside keysSortedByValueUsingComparator:^NSComparisonResult(NSNumber *a, NSNumber *b) { return [b compare:a]; }];
            NSInteger total = 0; for (NSNumber *count in outside.allValues) total += count.integerValue;
            [body appendFormat:@"  不在清单项目里的未归档会话：%ld 条，分布在 %lu 个目录：\n",(long)total,(unsigned long)keys.count];
            for (NSUInteger i = 0; i < MIN(keys.count,MaxListed); i++) [body appendFormat:@"    %@ 条  %@\n",outside[keys[i]],keys[i]];
            if (keys.count > MaxListed) [body appendFormat:@"    ……另有 %lu 个目录\n",(unsigned long)(keys.count-MaxListed)];
        }
        [body appendFormat:@"  作为同步目标：%@；记录格式：%@\n",[r[@"target"] length] ? [NSString stringWithFormat:@"不可以（%@）",r[@"target"]] : @"可以",
            format > 0 ? @"已知格式" : format == 0 ? @"未知格式（启动器会拒绝写入，可能是 Claude 更新改了格式）" : @"没有唯一的记录目录（有多套账号／组织目录时，写入前按当前组织确定；也可能还没建过本地会话）"];
        NSMutableArray *ids = [NSMutableArray array];
        for (NSDictionary *link in input.links) { id value = [link isKindOfClass:NSDictionary.class] ? link[@(number).stringValue] : nil; if ([value isKindOfClass:NSString.class]) [ids addObject:value]; }
        if (ids.count) {
            NSMutableSet *archived = [NSMutableSet set]; NSUInteger active = 0, gone = 0;
            for (NSArray *pair in scan.archived) [archived addObject:pair[0]];
            for (NSString *identifier in ids) { if (scan.active[identifier.lowercaseString]) active++; else if ([archived containsObject:identifier.lowercaseString]) gone++; }
            [body appendFormat:@"  启动器记录的关联会话：%lu 条；其中未归档 %lu，已归档 %lu，已不存在 %lu\n",(unsigned long)ids.count,(unsigned long)active,(unsigned long)gone,(unsigned long)(ids.count-active-gone)];
        }
    }
    NSArray *errors = SupportRecentErrors(input.logs,home);
    [body appendString:@"\n== 最近的启动器错误 ==\n"];
    if (!errors.count) [body appendString:@"  无\n"];
    for (NSString *line in errors) [body appendFormat:@"  %@\n",line];
    for (NSString *line in errors) if ([line containsString:@"同步"]) { [notices addObject:@"最近有同步会话相关的错误记录，详见报告末尾。"]; break; }

    // ---- Summary ----
    NSDateFormatter *format = [NSDateFormatter new]; format.locale = [NSLocale localeWithLocaleIdentifier:@"en_US_POSIX"]; format.dateFormat = @"yyyy-MM-dd HH:mm";
    NSMutableString *report = [NSMutableString stringWithFormat:@"Claude 多开启动器 · 问题诊断\n版本 %@；macOS %@；联网更新 %@；生成于 %@\n只读检查。不含对话内容、会话标题和凭据；路径只保留最后一级目录名。\n\n== 结论 ==\n",
        input.version ?: @"",input.system ?: @"",input.updatesEnabled ? @"开" : @"关",[format stringFromDate:input.now ?: NSDate.date]];
    if (!problems.count && !notices.count) [report appendString:@"  没有发现已知问题。\n"];
    for (NSString *text in problems) [report appendFormat:@"  [问题] %@\n",text];
    for (NSString *text in notices) [report appendFormat:@"  [注意] %@\n",text];
    [report appendFormat:@"\n%@",body];
    NSString *text = home.length ? [report stringByReplacingOccurrencesOfString:home withString:@"~"] : report;
    NSString *summary = !problems.count && !notices.count ? @"没有发现已知问题" : [NSString stringWithFormat:@"发现 %lu 个问题、%lu 个注意事项",(unsigned long)problems.count,(unsigned long)notices.count];
    return @{@"text":text,@"summary":summary,@"problems":@(problems.count),@"notices":@(notices.count)};
}

static NSString *IssueLink(NSString *title, NSString *body) {
    NSCharacterSet *ascii = [NSCharacterSet characterSetWithCharactersInString:@"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-._~"];
    return [NSString stringWithFormat:@"%@?labels=bug&title=%@&body=%@",SupportIssuesNew,[title stringByAddingPercentEncodingWithAllowedCharacters:ascii] ?: @"",[body stringByAddingPercentEncodingWithAllowedCharacters:ascii] ?: @""];
}
NSString *SupportIssueURL(NSDictionary *result, BOOL *complete) {
    NSString *title = [@"[诊断] " stringByAppendingString:result[@"summary"]], *text = result[@"text"];
    NSString *full = IssueLink(title,[NSString stringWithFormat:@"```\n%@\n```",[text stringByTrimmingCharactersInSet:NSCharacterSet.whitespaceAndNewlineCharacterSet]]);
    *complete = full.length <= MaxURL;
    if (*complete) return full;
    NSString *head = text; NSRange conclusions = [text rangeOfString:@"== 结论 =="];
    NSRange next = conclusions.location == NSNotFound ? conclusions : [text rangeOfString:@"\n== " options:0 range:NSMakeRange(NSMaxRange(conclusions),text.length-NSMaxRange(conclusions))];
    if (next.location != NSNotFound) head = [text substringToIndex:next.location];
    NSString *note = @"完整报告太长，已复制到剪贴板：请把这段说明删掉，在这里按 ⌘V 粘贴完整报告。\n\n";
    for (NSUInteger length = head.length; length > 0; length = length*3/4) {
        NSString *part = [head substringToIndex:[head rangeOfComposedCharacterSequenceAtIndex:length-1].location];
        NSString *url = IssueLink(title,[NSString stringWithFormat:@"%@```\n%@%@\n```",note,[part stringByTrimmingCharactersInSet:NSCharacterSet.whitespaceAndNewlineCharacterSet],length < head.length ? @"\n……" : @""]);
        if (url.length <= MaxURL) return url;
    }
    return IssueLink(title,note);
}

void TestSupportReport(void) {
    NSFileManager *fm = NSFileManager.defaultManager;
    NSString *root = [fm.currentDirectoryPath stringByAppendingPathComponent:[@".claude-support-tests-" stringByAppendingString:NSUUID.UUID.UUIDString]];
    NSString *home = [root stringByAppendingPathComponent:@"home"], *profile = [home stringByAppendingPathComponent:@"Profile"], *config = [profile stringByAppendingPathComponent:@"ClaudeCodeConfig"];
    NSString *shown = [home stringByAppendingPathComponent:@"Shown"], *archivedOnly = [home stringByAppendingPathComponent:@"Archived"], *misplaced = [home stringByAppendingPathComponent:@"Misplaced"];
    NSString *moved = [home stringByAppendingPathComponent:@"Old/Shown"], *gone = [home stringByAppendingPathComponent:@"Gone"];
    NSString *records = [[[profile stringByAppendingPathComponent:@"claude-code-sessions"] stringByAppendingPathComponent:NSUUID.UUID.UUIDString] stringByAppendingPathComponent:NSUUID.UUID.UUIDString];
    NSDictionary *mode = @{NSFilePosixPermissions:@0700};
    void (^mkdir)(NSString *) = ^(NSString *path) { NSCAssert([fm createDirectoryAtPath:path withIntermediateDirectories:YES attributes:mode error:nil],@"support fixture dir"); };
    NSString *(^record)(NSString *, BOOL) = ^NSString *(NSString *cwd, BOOL archived) {
        NSString *cli = NSUUID.UUID.UUIDString.lowercaseString;
        NSData *json = [NSJSONSerialization dataWithJSONObject:@{@"sessionId":[@"local_" stringByAppendingString:cli],@"cliSessionId":cli,@"cwd":cwd,@"originCwd":cwd,@"title":@"SECRET TITLE",@"isArchived":@(archived)} options:0 error:nil];
        NSString *file = [records stringByAppendingPathComponent:[NSString stringWithFormat:@"local_%@.json",cli]];
        NSCAssert([json writeToFile:file atomically:NO],@"support record");
        return cli;
    };
    void (^transcript)(NSString *, NSString *, NSString *) = ^(NSString *cwd, NSString *slugOf, NSString *cli) {
        NSString *folder = [[config stringByAppendingPathComponent:@"projects"] stringByAppendingPathComponent:SessionProjectSlug(slugOf ?: cwd)]; mkdir(folder);
        NSCAssert([@"{\"type\":\"custom-title\",\"customTitle\":\"SECRET TITLE\"}\n" writeToFile:[folder stringByAppendingPathComponent:[cli stringByAppendingString:@".jsonl"]] atomically:NO encoding:NSUTF8StringEncoding error:nil],@"support transcript");
    };
    @try {
        for (NSString *path in @[records,shown,archivedOnly,misplaced,moved,[config stringByAppendingPathComponent:@"projects"]]) mkdir(path);
        transcript(shown,nil,record(shown,NO));
        record(archivedOnly,YES); record(archivedOnly,YES);
        transcript(misplaced,shown,record(misplaced,NO));             // transcript stored under another project's folder
        record(moved,NO);
        [@"not json" writeToFile:[records stringByAppendingPathComponent:@"local_bad.json"] atomically:NO encoding:NSUTF8StringEncoding error:nil];
        // A stray folder at the organization level makes that level unsafe; its name is reported, the rest still scanned.
        NSString *second = [[profile stringByAppendingPathComponent:@"claude-code-sessions"] stringByAppendingPathComponent:NSUUID.UUID.UUIDString];
        mkdir([second stringByAppendingPathComponent:@"stray-folder"]);
        SupportInput *input = [SupportInput new];
        input.version = @"test"; input.system = @"12"; input.home = home; input.now = [NSDate dateWithTimeIntervalSince1970:1800000000];
        input.projects = @[@{@"name":@"Shown",@"path":shown},@{@"name":@"Archived",@"path":archivedOnly},@{@"name":@"Misplaced",@"path":misplaced},@{@"name":@"Again",@"path":shown},@{@"name":@"Gone",@"path":gone}];
        input.projectProblem = ^NSString *(NSString *path) { return [fm fileExistsAtPath:path] ? nil : @"目录不存在"; };
        input.accounts = @[@1]; input.config = ^NSString *(NSInteger n) { return config; }; input.profile = ^NSString *(NSInteger n) { return profile; };
        input.targetGuard = ^NSString *(NSInteger n) { return @"请先退出"; };
        input.links = @[@{@"1":@"00000000-0000-0000-0000-000000000000"}];
        input.logs = @[@{@"date":@1800000300,@"level":@"错误",@"text":[NSString stringWithFormat:@"会话同步失败 %@/x",home]},@{@"date":@1800000200,@"level":@"错误",@"text":[NSString stringWithFormat:@"会话同步失败 %@/x",home]},@{@"date":@1800000100,@"level":@"信息",@"text":@"ok"}];
        NSDictionary *result = BuildSupportReport(input); NSString *text = result[@"text"];
        NSCAssert([text containsString:@"会显示的项目（括号里是各来源账号合计的会话数）：P1「Shown」（1）"],@"shown project and count: %@",text);
        NSCAssert([text containsString:@"P2「Archived」 在同步窗口里不显示：2 条会话在来源账号里是“已归档”"],@"archived reason");
        NSCAssert([text containsString:@"P3「Misplaced」 在同步窗口里不显示：1 条会话的正文不在记录指向的位置"] && [text containsString:@"[问题] P3「Misplaced」"],@"transcript elsewhere is a problem");
        NSCAssert([text containsString:@"P4「Again」 和前面的项目是同一个目录"] && [text containsString:@"P5「Gone」 在同步窗口里不会出现：目录不存在"],@"duplicate and unusable projects");
        NSCAssert([text containsString:@"和项目 P1「Shown」 同名但路径不同"],@"moved project hint");
        NSCAssert([text containsString:@"有不认识的文件夹「stray-folder」"] && [text containsString:@"格式不对 1"],@"unsafe level named, bad record counted");
        NSCAssert([text containsString:@"现在不能作为同步目标：请先退出"] && [text containsString:@"已不存在 1"],@"target and links");
        NSCAssert([text containsString:@"会话同步失败 ~/x（连续 2 次）"] && ![text containsString:@"ok"],@"errors merged, home masked");
        NSCAssert(![text containsString:@"SECRET TITLE"] && ![text containsString:home] && ![text containsString:records.lastPathComponent] && ![text containsString:records.stringByDeletingLastPathComponent.lastPathComponent],@"no titles, home or organization IDs");
        NSCAssert([SupportMask(@"/Users/x/a/b/Proj",@"/Users/x") isEqual:@"~/…/Proj"] && [SupportMask(@"/Volumes/D/Proj",@"/Users/x") isEqual:@"/…/Proj"] && [SupportMask(@"/Users/x",@"/Users/x") isEqual:@"~"],@"mask keeps the last name only");
        BOOL complete = NO; NSString *url = SupportIssueURL(result,&complete);
        NSCAssert([url hasPrefix:[SupportIssuesNew stringByAppendingString:@"?labels=bug&title="]] && url.length <= MaxURL,@"issue link within the limit");
        NSMutableDictionary *big = result.mutableCopy; big[@"text"] = [text stringByPaddingToLength:40000 withString:@"长" startingAtIndex:0];
        NSCAssert(SupportIssueURL(big,&complete).length <= MaxURL && !complete,@"a long report prefills the conclusions only");
        NSCAssert([[NSURL URLWithString:url].host isEqual:@"github.com"],@"opens the project's GitHub only");
        puts("PASS: diagnostics share the sync listing, explain hidden projects, name unsafe levels, keep titles, home and organization IDs out, and fit the issue link");
    } @finally { [fm removeItemAtPath:root error:nil]; }
}
