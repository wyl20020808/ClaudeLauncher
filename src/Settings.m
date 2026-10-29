#import "Settings.h"

NSDictionary *DefaultSettings(void) {
    return @{@"closeToBackground":@YES, @"showMenuBar":@YES, @"rememberPage":@NO,
             @"startPage":@0, @"restoreMinimized":@YES, @"refreshInterval":@1,
             @"showArtwork":@YES, @"showUsageHistory":@YES, @"logLimit":@500,
             @"localNotifications":@YES, @"notificationSound":@NO, @"allowUpdates":@NO};
}
NSDictionary *ValidatedSettings(id input) {
    NSMutableDictionary *result = DefaultSettings().mutableCopy;
    if (![input isKindOfClass:NSDictionary.class]) return result;
    NSDictionary *choices = @{@"startPage":@[@0,@1,@2,@3,@4], @"refreshInterval":@[@1,@3,@5], @"logLimit":@[@100,@500,@1000]};
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
    NSCAssert([ValidatedAccountName(@"  工作账号  ") isEqual:@"工作账号"] && !ValidatedAccountName(@"a\nb") && !ValidatedAccountName([@"长" stringByPaddingToLength:25 withString:@"长" startingAtIndex:0]), @"bounded local alias");
    NSDate *now = NSDate.date;
    NSArray *logs = @[@{@"time":@"now",@"date":now,@"instance":@"系统",@"level":@"信息",@"text":@"Hello"},@{@"time":@"old",@"date":[now dateByAddingTimeInterval:-600],@"instance":@"Claude 2",@"level":@"错误",@"text":@"失败"}];
    NSCAssert(FilteredLogs(logs,@"hello",nil,nil,nil).count == 1 && FilteredLogs(logs,nil,@"Claude 2",@"错误",nil).count == 1 && FilteredLogs(logs,nil,nil,nil,[now dateByAddingTimeInterval:-300]).count == 1 && !FilteredLogs(logs,@"missing",nil,nil,nil).count, @"combined log filters and empty state");
    NSCAssert(AllowedUpdateURL([NSURL URLWithString:@"https://github.com/wyl20020808/ClaudeLauncher/releases/latest/download/latest-macos.json"],NO), @"allow project update manifest");
    for (NSString *address in @[@"https://claude.ai/api",@"https://api.anthropic.com/v1/messages",@"https://github.com.evil.test/wyl20020808/ClaudeLauncher/releases/latest/download/latest-macos.json",@"https://github.com/other/repo/releases/latest/download/latest-macos.json",@"http://github.com/wyl20020808/ClaudeLauncher/releases/latest/download/latest-macos.json",@"https://user@github.com/wyl20020808/ClaudeLauncher/releases/latest/download/latest-macos.json",@"https://github.com:444/wyl20020808/ClaudeLauncher/releases/latest/download/latest-macos.json",@"file:///tmp/x"]) {
        NSCAssert(!AllowedUpdateURL([NSURL URLWithString:address],NO) && !AllowedUpdateURL([NSURL URLWithString:address],YES), @"block non-update network target: %@",address);
    }
    puts("PASS: local settings validation, aliases, log filters, strict update URL allowlist; no network or user data");
}
