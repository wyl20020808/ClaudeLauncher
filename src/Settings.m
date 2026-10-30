#import "Settings.h"

NSDictionary *DefaultSettings(void) {
    return @{@"closeToBackground":@YES, @"showMenuBar":@YES, @"rememberPage":@NO,
             @"startPage":@0, @"restoreMinimized":@YES, @"refreshInterval":@1,
             @"showArtwork":@YES, @"showUsageHistory":@YES, @"logLimit":@500,
             @"localNotifications":@YES, @"notificationSound":@NO, @"allowUpdates":@NO,
             @"usageAlert":@80, @"globalHotkeys":@NO, @"theme":@1, @"language":@0};
}
// Each valid item from input replaces the base value; invalid or unknown items keep the base value.
static NSDictionary *MergedSettings(id input, NSDictionary *base) {
    NSMutableDictionary *result = ValidatedSettings(base).mutableCopy;
    if (![input isKindOfClass:NSDictionary.class]) return result;
    NSDictionary *choices = @{@"startPage":@[@0,@1,@2,@3,@4], @"refreshInterval":@[@1,@3,@5], @"logLimit":@[@100,@500,@1000],
                              @"usageAlert":@[@0,@70,@80,@90], @"theme":@[@0,@1,@2], @"language":@[@0,@1]};
    for (NSString *key in result.allKeys) {
        id value = input[key];
        if (![value isKindOfClass:NSNumber.class]) continue;
        if (choices[key] ? [choices[key] containsObject:value] : [@[@NO,@YES] containsObject:value]) result[key] = value;
    }
    return result;
}
NSDictionary *ImportedSettings(NSData *data, NSDictionary *current) {
    id root = data.length && data.length <= 65536 ? [NSJSONSerialization JSONObjectWithData:data options:0 error:nil] : nil;
    if (![root isKindOfClass:NSDictionary.class] || ![root[@"version"] isEqual:@1] || ![root[@"settings"] isKindOfClass:NSDictionary.class]) return nil;
    return MergedSettings(root[@"settings"], current);
}
NSDictionary *ValidatedSettings(id input) {
    NSMutableDictionary *result = DefaultSettings().mutableCopy;
    if (![input isKindOfClass:NSDictionary.class]) return result;
    NSDictionary *choices = @{@"startPage":@[@0,@1,@2,@3,@4], @"refreshInterval":@[@1,@3,@5], @"logLimit":@[@100,@500,@1000],
                              @"usageAlert":@[@0,@70,@80,@90], @"theme":@[@0,@1,@2], @"language":@[@0,@1]};
    for (NSString *key in result.allKeys) {
        id value = input[key];
        if (![value isKindOfClass:NSNumber.class]) continue;
        if (choices[key] ? [choices[key] containsObject:value] : [@[@NO,@YES] containsObject:value]) result[key] = value;
    }
    return result;
}
NSString *ValidatedAccountName(id input) {
    if (![input isKindOfClass:NSString.class]) return nil;
    NSString *name = [input stringByTrimmingCharactersInSet:NSCharacterSet.whitespaceAndNewlineCharacterSet];
    if (name.length > 24 || [name rangeOfCharacterFromSet:NSCharacterSet.controlCharacterSet].location != NSNotFound) return nil;
    return name;
}
NSArray *FilteredLogs(NSArray *entries, NSString *query, NSString *instance, NSString *level, NSDate *since) {
    NSMutableArray *result = [NSMutableArray array];
    for (NSDictionary *entry in entries) {
        if (instance.length && ![entry[@"instance"] isEqual:instance]) continue;
        if (level.length && ![entry[@"level"] isEqual:level]) continue;
        if (since && [entry[@"date"] compare:since] == NSOrderedAscending) continue;
        if (query.length && ![[NSString stringWithFormat:@"%@ %@ %@ %@",entry[@"time"],entry[@"instance"],entry[@"level"],entry[@"text"]] localizedCaseInsensitiveContainsString:query]) continue;
        [result addObject:entry];
    }
    return result;
}
// Launcher log file rows: {date: epoch seconds, level, instance, text}, newest first. Malformed rows are dropped.
NSMutableArray *LoadedLogs(NSData *data) {
    NSMutableArray *result = [NSMutableArray array];
    id rows = data ? [NSJSONSerialization JSONObjectWithData:data options:0 error:nil] : nil;
    if (![rows isKindOfClass:NSArray.class]) return result;
    NSDateFormatter *clock = [NSDateFormatter new]; clock.dateFormat = @"yyyy-MM-dd HH:mm:ss";
    for (id row in rows) {
        if (![row isKindOfClass:NSDictionary.class]) continue;
        id date = row[@"date"], level = row[@"level"], instance = row[@"instance"], text = row[@"text"];
        if (![date isKindOfClass:NSNumber.class] || ![@[@"信息",@"警告",@"错误"] containsObject:level] || ![instance isKindOfClass:NSString.class] || ![text isKindOfClass:NSString.class]) continue;
        NSDate *when = [NSDate dateWithTimeIntervalSince1970:[date doubleValue]];
        [result addObject:@{@"date":when,@"time":[clock stringFromDate:when],@"level":level,@"instance":instance,@"text":text}];
    }
    return result;
}
NSData *SerializedLogs(NSArray *entries) {
    NSMutableArray *rows = [NSMutableArray array];
    for (NSDictionary *entry in entries) [rows addObject:@{@"date":@([entry[@"date"] timeIntervalSince1970]),@"level":entry[@"level"],@"instance":entry[@"instance"],@"text":entry[@"text"]}];
    return [NSJSONSerialization dataWithJSONObject:rows options:0 error:nil];
}
BOOL AllowedUpdateURL(NSURL *url, BOOL redirect) {
    if (![url.scheme.lowercaseString isEqual:@"https"] || url.user || url.password || (url.port && url.port.integerValue != 443) || url.fragment) return NO;
    NSString *host = url.host.lowercaseString;
    if (redirect && [@[@"objects.githubusercontent.com",@"release-assets.githubusercontent.com"] containsObject:host]) return YES;
    if (![host isEqual:@"github.com"] || url.query) return NO;
    NSString *prefix = @"/wyl20020808/ClaudeLauncher/releases/";
    if (![url.path hasPrefix:prefix]) return NO;
    NSString *tail = [url.path substringFromIndex:prefix.length];
    NSString *pattern = redirect ? @"^(latest/download|download/[^/]+)/((latest-macos\\.json)|(ClaudeLauncher-[0-9]{1,4}\\.[0-9]{1,4}\\.[0-9]{1,4}\\.dmg))$" : @"^latest/download/((latest-macos\\.json)|(ClaudeLauncher-[0-9]{1,4}\\.[0-9]{1,4}\\.[0-9]{1,4}\\.dmg))$";
    return [tail rangeOfString:pattern options:NSRegularExpressionSearch].location != NSNotFound && ![tail containsString:@".."];
}
void TestSettings(void) {
    NSCAssert(![DefaultSettings()[@"allowUpdates"] boolValue], @"network off by default");
    NSCAssert([ValidatedSettings(@"bad") isEqual:DefaultSettings()], @"malformed settings use defaults");
    NSDictionary *safe = ValidatedSettings(@{@"allowUpdates":@"YES",@"refreshInterval":@0,@"startPage":@99,@"logLimit":@999999,@"unknown":@YES});
    NSCAssert([safe isEqual:DefaultSettings()], @"reject invalid types, ranges and unknown settings");
    NSDictionary *changed = ValidatedSettings(@{@"showArtwork":@NO,@"refreshInterval":@5,@"startPage":@4});
    NSData *json = [NSJSONSerialization dataWithJSONObject:changed options:0 error:nil];
    NSCAssert([ValidatedSettings([NSJSONSerialization JSONObjectWithData:json options:0 error:nil]) isEqual:changed], @"preferences round trip");
    NSDictionary *current = ValidatedSettings(@{@"showArtwork":@NO,@"theme":@2});
    NSData *file = [@"{\"version\":1,\"settings\":{\"usageAlert\":90,\"theme\":7,\"logLimit\":\"500\",\"unknown\":true,\"globalHotkeys\":true}}" dataUsingEncoding:NSUTF8StringEncoding];
    NSDictionary *imported = ImportedSettings(file, current);
    NSCAssert([imported[@"usageAlert"] isEqual:@90] && [imported[@"globalHotkeys"] isEqual:@YES] && [imported[@"theme"] isEqual:@2] && [imported[@"showArtwork"] isEqual:@NO] && !imported[@"unknown"], @"import keeps current values for invalid items");
    for (NSString *bad in @[@"{\"version\":2,\"settings\":{}}",@"{\"settings\":{}}",@"[]",@"{"]) NSCAssert(!ImportedSettings([bad dataUsingEncoding:NSUTF8StringEncoding], current), @"reject malformed import");
    NSCAssert(!ImportedSettings(nil, current) && !ImportedSettings([NSMutableData dataWithLength:65537], current), @"reject empty or oversized import");
    NSCAssert([ValidatedAccountName(@"  工作账号  ") isEqual:@"工作账号"] && !ValidatedAccountName(@"a\nb") && !ValidatedAccountName([@"长" stringByPaddingToLength:25 withString:@"长" startingAtIndex:0]), @"bounded local alias");
    NSDate *now = NSDate.date;
    NSArray *logs = @[@{@"time":@"now",@"date":now,@"instance":@"系统",@"level":@"信息",@"text":@"Hello"},@{@"time":@"old",@"date":[now dateByAddingTimeInterval:-600],@"instance":@"Claude 2",@"level":@"错误",@"text":@"失败"}];
    NSCAssert(FilteredLogs(logs,@"hello",nil,nil,nil).count == 1 && FilteredLogs(logs,nil,@"Claude 2",@"错误",nil).count == 1 && FilteredLogs(logs,nil,nil,nil,[now dateByAddingTimeInterval:-300]).count == 1 && !FilteredLogs(logs,@"missing",nil,nil,nil).count, @"combined log filters and empty state");
    NSArray *reloaded = LoadedLogs(SerializedLogs(logs));
    NSCAssert(reloaded.count == 2 && [reloaded[1][@"text"] isEqual:@"失败"] && fabs([reloaded[0][@"date"] timeIntervalSinceDate:now]) < 0.001, @"log file round trip keeps order and time");
    NSData *mixed = [@"[{\"date\":1,\"level\":\"信息\",\"instance\":\"系统\",\"text\":\"ok\"},{\"date\":\"x\",\"level\":\"信息\",\"instance\":\"系统\",\"text\":\"bad\"},{\"date\":2,\"level\":\"伪造\",\"instance\":\"系统\",\"text\":\"bad\"},3]" dataUsingEncoding:NSUTF8StringEncoding];
    NSCAssert(LoadedLogs(mixed).count == 1 && !LoadedLogs([@"{" dataUsingEncoding:NSUTF8StringEncoding]).count && !LoadedLogs(nil).count, @"malformed log rows and files are ignored");
    NSCAssert(AllowedUpdateURL([NSURL URLWithString:@"https://github.com/wyl20020808/ClaudeLauncher/releases/latest/download/latest-macos.json"],NO), @"allow project update manifest");
    for (NSString *address in @[@"https://claude.ai/api",@"https://api.anthropic.com/v1/messages",@"https://github.com.evil.test/wyl20020808/ClaudeLauncher/releases/latest/download/latest-macos.json",@"https://github.com/other/repo/releases/latest/download/latest-macos.json",@"http://github.com/wyl20020808/ClaudeLauncher/releases/latest/download/latest-macos.json",@"https://user@github.com/wyl20020808/ClaudeLauncher/releases/latest/download/latest-macos.json",@"https://github.com:444/wyl20020808/ClaudeLauncher/releases/latest/download/latest-macos.json",@"file:///tmp/x"]) {
        NSCAssert(!AllowedUpdateURL([NSURL URLWithString:address],NO) && !AllowedUpdateURL([NSURL URLWithString:address],YES), @"block non-update network target: %@",address);
    }
    puts("PASS: local settings validation, aliases, log filters and log file parsing, strict update URL allowlist; no network or user data");
}
