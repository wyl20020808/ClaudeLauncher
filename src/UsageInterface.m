#import "UsageInterface.h"
#import <ApplicationServices/ApplicationServices.h>
#import <math.h>

static NSString *String(id value) { return [value isKindOfClass:NSString.class] && [value length] <= 512 ? value : @""; }
static NSNumber *Percent(id value) {
    if (![value isKindOfClass:NSNumber.class] || CFGetTypeID((__bridge CFTypeRef)value) == CFBooleanGetTypeID()) return nil;
    double n = [value doubleValue]; return isfinite(n) && n >= 0 && n <= 100 ? value : nil;
}
static NSString *MetricKey(NSString *label) {
    if ([@[@"5 小时限额",@"5-hour limit"] containsObject:label]) return @"fh";
    if ([@[@"每周 · 所有模型",@"Weekly · all models"] containsObject:label]) return @"sd";
    return nil; // Model-specific weekly and context utilization are not the account's weekly limit.
}
static NSDictionary *ButtonMetric(NSString *label) {
    for (NSString *pattern in @[@"^用量[：:]\\s*5 小时限额的 ([0-9]{1,3}(?:\\.[0-9]+)?)%(.*)$",
                                @"^Usage: ([0-9]{1,3}(?:\\.[0-9]+)?)% of (?:the )?5-hour limit(.*)$"]) {
        NSTextCheckingResult *match = [[NSRegularExpression regularExpressionWithPattern:pattern options:0 error:nil] firstMatchInString:label options:0 range:NSMakeRange(0,label.length)];
        if (!match) continue;
        NSNumber *percent = Percent(@([[label substringWithRange:[match rangeAtIndex:1]] doubleValue]));
        if (!percent) return nil;
        NSString *reset = [[label substringWithRange:[match rangeAtIndex:2]] stringByTrimmingCharactersInSet:NSCharacterSet.whitespaceAndNewlineCharacterSet];
        return @{@"percent":percent,@"reset":reset};
    }
    return nil;
}
static BOOL CodeURL(NSString *value) {
    if (![value isKindOfClass:NSString.class]) return NO;
    NSURL *url = [NSURL URLWithString:value];
    return [url.scheme isEqual:@"https"] && [url.host isEqual:@"claude.ai"] && !url.user && !url.password && !url.port && !url.query && !url.fragment &&
        [url.path hasPrefix:@"/epitaxy/"] && url.path.length > 9;
}
static BOOL UsageURL(NSString *value) {
    NSURL *url = [NSURL URLWithString:value];
    return [url.scheme isEqual:@"https"] && [url.host isEqual:@"claude.ai"] && [url.path isEqual:@"/settings/usage"] && !url.user && !url.password && !url.port;
}

// The same bounded traversal is tested with synthetic AX nodes; no test touches a real account.
@interface UsageAXReader : NSObject
@property (copy) id (^attribute)(id,NSString *);
@property NSUInteger count;
@property NSTimeInterval deadline;
@property BOOL limited;
@property NSMutableArray *matches;
@property NSHashTable *visited;
@end
@implementation UsageAXReader
- (id)read:(id)node key:(NSString *)key {
    if (self.limited || ++self.count > 16000 || NSProcessInfo.processInfo.systemUptime > self.deadline) { self.limited = YES; return nil; }
    return self.attribute(node,key);
}
- (NSString *)name:(id)node {
    NSString *title = String([self read:node key:@"AXTitle"]);
    return title.length ? title : String([self read:node key:@"AXDescription"]);
}
- (NSArray *)children:(id)node {
    id children = [self read:node key:@"AXChildren"];
    if (!children) return @[];
    if (![children isKindOfClass:NSArray.class] || [children count] > 2000) { self.limited = YES; return @[]; }
    return children;
}
- (NSString *)url:(id)node {
    id value = [self read:node key:@"AXURL"];
    return [value isKindOfClass:NSURL.class] ? [value absoluteString] : String(value);
}
- (BOOL)hasUsageLink:(id)node depth:(NSUInteger)depth {
    if (depth > 12 || self.limited) return NO;
    NSString *role = String([self read:node key:@"AXRole"]);
    if ([role isEqual:@"AXLink"]) return UsageURL([self url:node]);
    if ([@[@"AXWebArea",@"AXTextArea",@"AXTextField",@"AXStaticText",@"AXImage",@"AXPopUpButton",@"AXButton"] containsObject:role]) return NO;
    for (id child in [self children:node]) if ([self hasUsageLink:child depth:depth+1]) return YES;
    return NO;
}
- (BOOL)panel:(id)node metrics:(NSMutableDictionary *)metrics reset:(NSString **)reset linked:(BOOL *)linked depth:(NSUInteger)depth {
    if (depth > 12) { self.limited = YES; return NO; }
    NSString *role = String([self read:node key:@"AXRole"]);
    if ([role isEqual:@"AXWebArea"] || [role isEqual:@"AXTextArea"]) return NO;
    if ([role isEqual:@"AXLink"]) { if (UsageURL([self url:node])) *linked = YES; return YES; }
    if ([role isEqual:@"AXStaticText"]) {
        NSString *text = String([self read:node key:@"AXValue"]);
        if (MetricKey(text)) *reset = nil;
        else if ([text containsString:@"重置"] || [text hasPrefix:@"Resets "]) *reset = text;
        return YES;
    }
    if ([role isEqual:@"AXLevelIndicator"] || [role isEqual:@"AXProgressIndicator"]) {
        NSString *key = MetricKey([self name:node]); if (!key) return YES;
        NSNumber *percent = Percent([self read:node key:@"AXValue"]);
        if (!percent || metrics[key]) return NO;
        metrics[key] = @{@"percent":percent,@"reset":*reset ?: @""}; *reset = nil;
        return YES;
    }
    for (id child in [self children:node]) if (![self panel:child metrics:metrics reset:reset linked:linked depth:depth+1]) return NO;
    return !self.limited;
}
- (void)walk:(id)node parent:(id)parent page:(NSString *)page depth:(NSUInteger)depth {
    if (self.limited) return;
    // shortcut: only quota layouts within 40 levels are supported; validate deeper layouts before extending this scope.
    if (depth > 40 || [self.visited containsObject:node]) return;
    [self.visited addObject:node];
    NSString *role = String([self read:node key:@"AXRole"]);
    BOOL codeArea = [role isEqual:@"AXWebArea"];
    if (codeArea) {
        if (page) return; // Never enter Code's embedded browser or other nested web content.
        NSString *url = [self url:node];
        if (CodeURL(url)) page = url;
        else return; // The native wrapper is a sibling of Code, not its parent in Claude 4.29.0.
    }
    if (page && [role isEqual:@"AXPopUpButton"]) {
        NSDictionary *five = ButtonMetric([self name:node]);
        if (!five) return;
        NSMutableDictionary *result = [@{@"status":@"界面已读取",@"page":page,@"fh":five} mutableCopy];
        NSUInteger panels = 0;
        for (id sibling in [self children:parent]) {
            // Chromium's generic container role/name varies. Verify the dedicated settings link before reading text.
            if (sibling == node || ![self hasUsageLink:sibling depth:0]) continue;
            NSMutableDictionary *metrics = [NSMutableDictionary dictionary]; NSString *reset = nil; BOOL linked = NO;
            if (![self panel:sibling metrics:metrics reset:&reset linked:&linked depth:0] || !linked || !metrics[@"fh"] ||
                ![metrics[@"fh"][@"percent"] isEqual:five[@"percent"]]) { result = nil; break; }
            [result addEntriesFromDictionary:metrics]; panels++;
        }
        if (!result || panels > 1) [self.matches addObject:@{@"status":@"界面数据待核对"}];
        else [self.matches addObject:result];
        return;
    }
    if ([@[@"AXStaticText",@"AXTextArea",@"AXTextField",@"AXImage",@"AXLink",@"AXHeading",@"AXTable",@"AXOutline",@"AXList"] containsObject:role]) return;
    // No text/values are read from conversations. Only structural attributes and recognized quota controls.
    for (id child in [self children:node]) [self walk:child parent:node page:page depth:depth+1];
    if (codeArea && ![[self url:node] isEqual:page]) { self.limited = YES; }
}
- (NSDictionary *)result:(NSArray *)windows {
    self.matches = [NSMutableArray array]; self.visited = [NSHashTable hashTableWithOptions:NSPointerFunctionsStrongMemory];
    for (id window in windows) [self walk:window parent:nil page:nil depth:0];
    if (self.limited) return @{@"status":@"界面读取未完成"};
    if (self.matches.count > 1) return @{@"status":@"多个用量界面，待核对"};
    return self.matches.firstObject ?: @{@"status":@"未观察到 Code 用量"};
}
@end

NSDictionary *ReadUsageInterface(pid_t pid) {
    if (pid <= 0) return @{@"status":@"进程待核对"};
    if (!AXIsProcessTrusted()) return @{@"status":@"需辅助功能权限"};
    AXUIElementRef app = AXUIElementCreateApplication(pid);
    AXUIElementSetMessagingTimeout(app,0.1f); // Local IPC timeout only; does not set any attribute in Claude.
    UsageAXReader *reader = [UsageAXReader new]; reader.deadline = NSProcessInfo.processInfo.systemUptime+1.5;
    __weak UsageAXReader *weakReader = reader;
    NSMapTable *structure = [NSMapTable strongToStrongObjectsMapTable];
    reader.attribute = ^id(id node, NSString *key) {
        if (!node || CFGetTypeID((__bridge CFTypeRef)node) != AXUIElementGetTypeID()) return nil;
        if ([key isEqual:@"AXRole"] || [key isEqual:@"AXChildren"]) {
            NSDictionary *cached = [structure objectForKey:node];
            if (!cached) {
                CFArrayRef values = NULL;
                AXError error = AXUIElementCopyMultipleAttributeValues((__bridge AXUIElementRef)node,(__bridge CFArrayRef)@[@"AXRole",@"AXChildren"],0,&values);
                if (error == kAXErrorCannotComplete || error == kAXErrorInvalidUIElement || error == kAXErrorAPIDisabled) { weakReader.limited = YES; }
                NSArray *items = CFBridgingRelease(values);
                // Copy structural attributes together. Never batch titles or values from conversation nodes.
                cached = @{@"AXRole":items.count == 2 ? String(items[0]) : @"",
                           @"AXChildren":items.count == 2 && [items[1] isKindOfClass:NSArray.class] ? items[1] : @[]};
                [structure setObject:cached forKey:node];
            }
            return cached[key];
        }
        CFTypeRef value = NULL;
        AXError error = AXUIElementCopyAttributeValue((__bridge AXUIElementRef)node,(__bridge CFStringRef)key,&value);
        if (error == kAXErrorCannotComplete || error == kAXErrorInvalidUIElement || error == kAXErrorAPIDisabled) { weakReader.limited = YES; }
        return CFBridgingRelease(value);
    };
    id windows = [reader read:(__bridge id)app key:@"AXWindows"];
    NSDictionary *result = [reader result:[windows isKindOfClass:NSArray.class] ? windows : @[]];
    reader.attribute = nil; CFRelease(app); return result;
}

@interface UsageAXObservation : NSObject
@property AXObserverRef observer;
@property (copy) void (^changed)(void);
@end
@implementation UsageAXObservation
- (void)dealloc {
    if (_observer) { CFRunLoopRemoveSource(CFRunLoopGetMain(),AXObserverGetRunLoopSource(_observer),kCFRunLoopCommonModes); CFRelease(_observer); }
}
@end
static void UsageAXChanged(AXObserverRef observer, AXUIElementRef element, CFStringRef notification, void *context) {
    UsageAXObservation *observation = (__bridge UsageAXObservation *)context;
    if (observation.changed) observation.changed();
}
id ObserveUsageInterface(pid_t pid, void (^changed)(void)) {
    if (!AXIsProcessTrusted() || pid <= 0) return nil;
    UsageAXObservation *observation = [UsageAXObservation new]; AXObserverRef observer = NULL;
    if (AXObserverCreate(pid,UsageAXChanged,&observer) != kAXErrorSuccess) return nil;
    observation.observer = observer; observation.changed = changed;
    AXUIElementRef app = AXUIElementCreateApplication(pid); AXUIElementSetMessagingTimeout(app,0.1f);
    for (NSString *name in @[@"AXFocusedUIElementChanged",@"AXLayoutChanged",@"AXValueChanged",@"AXWindowCreated"])
        AXObserverAddNotification(observer,app,(__bridge CFStringRef)name,(__bridge void *)observation);
    CFRelease(app); CFRunLoopAddSource(CFRunLoopGetMain(),AXObserverGetRunLoopSource(observer),kCFRunLoopCommonModes);
    return observation;
}

NSDictionary *MergeUsageInterface(NSDictionary *previous, NSDictionary *snapshot, NSArray *identity, NSDate *now) {
    if (![snapshot[@"status"] isEqual:@"界面已读取"] || !identity || !CodeURL(snapshot[@"page"])) {
        BOOL unavailable = [snapshot[@"status"] hasPrefix:@"未观察到 Code 用量"] || [snapshot[@"status"] hasPrefix:@"界面读取未完成"];
        if (unavailable && [identity isEqual:previous[@"identity"]]) {
            NSMutableDictionary *last = [previous mutableCopy];
            for (NSString *key in @[@"fh",@"sd"]) if (last[key]) { NSMutableDictionary *metric = [last[key] mutableCopy]; metric[@"visible"] = @NO; last[key] = metric; }
            return last;
        }
        return snapshot ?: @{@"status":@"未观察到 Code 用量"};
    }
    NSMutableDictionary *result = [snapshot mutableCopy]; result[@"identity"] = identity;
    BOOL same = [identity isEqual:previous[@"identity"]]; // Quotas belong to the account, not an individual Code conversation.
    for (NSString *key in @[@"fh",@"sd"]) {
        NSDictionary *metric = snapshot[key];
        if (metric && Percent(metric[@"percent"])) {
            NSMutableDictionary *observed = [metric mutableCopy]; observed[@"observedAt"] = now; observed[@"visible"] = @YES; result[key] = observed;
        } else if (same && previous[key] && [previous[key][@"observedAt"] isKindOfClass:NSDate.class] && [now timeIntervalSinceDate:previous[key][@"observedAt"]] >= 0) {
            NSMutableDictionary *cached = [previous[key] mutableCopy]; cached[@"visible"] = @NO; result[key] = cached;
        } else [result removeObjectForKey:key];
    }
    return result;
}
NSString *UsageInterfaceTimeLabel(NSDictionary *metric, NSDate *now) {
    NSDate *date = metric[@"observedAt"];
    if (![date isKindOfClass:NSDate.class] || [now timeIntervalSinceDate:date] < 0) return @"尚未观察到";
    NSDateFormatter *format = [NSDateFormatter new]; format.dateFormat = @"HH:mm:ss";
    if (![NSCalendar.currentCalendar isDate:date inSameDayAsDate:now]) format.dateFormat = @"MM-dd HH:mm";
    return [NSString stringWithFormat:@"%@ %@",[metric[@"visible"] boolValue] ? @"读取于" : @"上次显示",[format stringFromDate:date]];
}

NSArray *StoredUsageInterface(NSDictionary *records) {
    NSMutableArray *items = [NSMutableArray array];
    for (NSNumber *account in [[records allKeys] sortedArrayUsingSelector:@selector(compare:)]) {
        NSDictionary *record = records[account];
        if (![record[@"status"] isEqual:@"界面已读取"] || !record[@"identity"] || !record[@"fh"]) continue;
        NSMutableDictionary *item = [@{@"identity":record[@"identity"]} mutableCopy];
        for (NSString *key in @[@"fh",@"sd"]) if (record[key]) item[key] = [record[key] dictionaryWithValuesForKeys:@[@"percent",@"reset",@"observedAt"]];
        [items addObject:item]; // No page/session identifier, conversation content or credentials are persisted.
    }
    return items;
}
NSDictionary *LoadedUsageInterface(id stored, NSDate *now) {
    if (![stored isKindOfClass:NSArray.class] || [stored count] > 10) return @{};
    NSMutableDictionary *records = [NSMutableDictionary dictionary];
    NSMutableSet *duplicates = [NSMutableSet set];
    for (id item in stored) {
        if (![item isKindOfClass:NSDictionary.class]) return @{};
        id identity = item[@"identity"];
        if (![identity isKindOfClass:NSArray.class] || [identity count] != 9) continue;
        BOOL valid = YES;
        for (NSUInteger i=0;i<9;i++) {
            id component = identity[i];
            if (i < 4) { if (![component isKindOfClass:NSNumber.class] || [component longLongValue] < 0) valid = NO; }
            else if ([component isKindOfClass:NSNumber.class]) { if (i == 4 || [component longLongValue] != 0) valid = NO; }
            else if ([component isKindOfClass:NSArray.class] && [component count] == (i == 4 ? 2u : 7u)) {
                for (id value in component) if (![value isKindOfClass:NSNumber.class] || !isfinite([value doubleValue]) || [value doubleValue] < 0) valid = NO;
            } else valid = NO;
        }
        NSInteger account = [identity[0] integerValue];
        if (!valid || account < 1 || account > 10 || [identity[1] intValue] <= 0 || [identity[2] longLongValue] <= 0) continue;
        if (records[@(account)]) { [duplicates addObject:@(account)]; [records removeObjectForKey:@(account)]; }
        if ([duplicates containsObject:@(account)]) continue;
        NSMutableDictionary *record = [@{@"status":@"界面已读取",@"identity":identity} mutableCopy];
        for (NSString *key in @[@"fh",@"sd"]) {
            id metric = item[key]; if (!metric) continue;
            if (![metric isKindOfClass:NSDictionary.class] || !Percent(metric[@"percent"]) ||
                ![metric[@"reset"] isKindOfClass:NSString.class] || [metric[@"reset"] length] > 512 ||
                ![metric[@"observedAt"] isKindOfClass:NSDate.class] || !isfinite([now timeIntervalSinceDate:metric[@"observedAt"]]) || [now timeIntervalSinceDate:metric[@"observedAt"]] < 0) continue;
            record[key] = @{@"percent":metric[@"percent"],@"reset":metric[@"reset"],@"observedAt":metric[@"observedAt"],@"visible":@NO};
        }
        if (record[@"fh"]) records[@(account)] = record;
    }
    return records;
}

void TestUsageInterface(void) {
    NSDictionary *(^node)(NSString *,NSDictionary *,NSArray *) = ^NSDictionary *(NSString *role,NSDictionary *attrs,NSArray *children) {
        NSMutableDictionary *n = [attrs mutableCopy]; n[@"AXRole"] = role; n[@"AXChildren"] = children; n[@"testNodeID"] = NSUUID.UUID.UUIDString; return n;
    };
    NSString *url = @"https://claude.ai/epitaxy/local_synthetic";
    NSDictionary *button = node(@"AXPopUpButton",@{@"AXDescription":@"用量：5 小时限额的 52%周四01:10 重置"},@[]);
    NSDictionary *panel = node(@"AXGroup",@{@"AXDescription":@"用量"},@[
        node(@"AXLink",@{@"AXURL":@"https://claude.ai/settings/usage"},@[]),
        node(@"AXStaticText",@{@"AXValue":@"周四01:10 重置"},@[]),
        node(@"AXLevelIndicator",@{@"AXDescription":@"5 小时限额",@"AXValue":@52},@[]),
        node(@"AXStaticText",@{@"AXValue":@"周三19:00 重置"},@[]),
        node(@"AXLevelIndicator",@{@"AXDescription":@"每周 · 所有模型",@"AXValue":@7},@[]),
        node(@"AXLevelIndicator",@{@"AXDescription":@"Weekly · Sonnet",@"AXValue":@99},@[])]);
    NSDictionary *(^read)(NSArray *) = ^NSDictionary *(NSArray *children) {
        UsageAXReader *r = [UsageAXReader new]; r.deadline = NSProcessInfo.processInfo.systemUptime+5;
        r.attribute = ^id(id n,NSString *key) { return n[key]; };
        return [r result:@[node(@"AXWindow",@{},children)]];
    };
    NSDictionary *(^page)(NSArray *) = ^NSDictionary *(NSArray *children) { return node(@"AXWebArea",@{@"AXURL":url},@[node(@"AXGroup",@{},children)]); };
    NSDictionary *open = read(@[page(@[button,panel])]);
    NSCAssert([open[@"fh"][@"percent"] isEqual:@52] && [open[@"sd"][@"percent"] isEqual:@7] && [open[@"sd"][@"reset"] isEqual:@"周三19:00 重置"],@"both account limits and reset text, not model-specific quota");
    NSDictionary *closed = read(@[page(@[button])]);
    NSCAssert(closed[@"fh"] && !closed[@"sd"],@"closed popover exposes only five-hour value");
    NSDictionary *secondButton = node(@"AXPopUpButton",@{@"AXDescription":@"用量：5 小时限额的 52%周四01:10 重置"},@[]);
    NSCAssert(!read(@[page(@[button]),page(@[secondButton])])[@"fh"],@"multiple pages fail closed");
    NSCAssert(!read(@[node(@"AXWebArea",@{@"AXURL":@"https://evil.test/epitaxy/local_synthetic"},@[button])])[@"fh"],@"nonofficial page rejected");
    NSCAssert(!read(@[page(@[node(@"AXWebArea",@{@"AXURL":url},@[button])])])[@"fh"],@"embedded web content rejected");
    NSCAssert(!read(@[page(@[node(@"AXStaticText",@{@"AXValue":@"用量：5 小时限额的 52%"},@[])])])[@"fh"],@"chat text cannot supply quota");
    NSMutableDictionary *bad = [panel mutableCopy]; bad[@"AXChildren"] = @[];
    NSCAssert(!read(@[page(@[button,bad])])[@"sd"],@"unverified panel cannot supply weekly quota");
    NSMutableDictionary *unnamedPanel = [panel mutableCopy]; [unnamedPanel removeObjectForKey:@"AXDescription"]; unnamedPanel[@"AXRole"] = @"AXGenericElement";
    NSCAssert([read(@[page(@[button,unnamedPanel])])[@"sd"][@"percent"] isEqual:@7],@"verified usage panel does not require a particular generic container role/name");
    NSCAssert(!ButtonMetric(@"用量：5 小时限额的 101%") && !ButtonMetric(@"上下文 52%") && !Percent(@YES) && !Percent(@(NAN)),@"invalid percentages and context excluded");
    NSCAssert([ButtonMetric(@"Usage: 0% of 5-hour limit Resets Thu 01:10")[@"percent"] isEqual:@0],@"English, zero and reset supported");
    NSDate *now = [NSDate dateWithTimeIntervalSince1970:1800000000];
    NSDictionary *first = MergeUsageInterface(nil,open,@[@1,@123,@456],now);
    NSDictionary *second = MergeUsageInterface(first,closed,@[@1,@123,@456],[now dateByAddingTimeInterval:20]);
    NSCAssert([second[@"sd"][@"observedAt"] isEqual:now] && ![second[@"sd"][@"visible"] boolValue] && [second[@"fh"][@"observedAt"] isEqual:[now dateByAddingTimeInterval:20]],@"weekly observation time never refreshed by five-hour read");
    NSCAssert(!MergeUsageInterface(first,closed,@[@2,@123,@456],now)[@"sd"],@"other account cannot inherit weekly value");
    NSCAssert(!MergeUsageInterface(first,closed,@[@1,@124,@456],now)[@"sd"],@"new process cannot inherit weekly value");
    NSCAssert(!MergeUsageInterface(first,closed,@[@1,@123,@457],now)[@"sd"],@"identity-store changes cannot inherit weekly value");
    NSMutableDictionary *otherPage = [closed mutableCopy]; otherPage[@"page"] = [url stringByAppendingString:@"2"];
    NSCAssert(MergeUsageInterface(first,otherPage,@[@1,@123,@456],now)[@"sd"],@"same validated account retains quota across Code conversations");
    NSCAssert(!MergeUsageInterface(first,@{@"status":@"需辅助功能权限"},@[@1,@123,@456],now)[@"fh"],@"permission loss clears values");
    NSCAssert([UsageInterfaceTimeLabel(nil,now) isEqual:@"尚未观察到"] && [UsageInterfaceTimeLabel(second[@"sd"],now) hasPrefix:@"上次显示"],@"missing and last observation remain explicit");
    NSMutableDictionary *wrongWeek = [panel mutableCopy];
    wrongWeek[@"AXChildren"] = [panel[@"AXChildren"] arrayByAddingObject:node(@"AXLevelIndicator",@{@"AXDescription":@"每周 · 所有模型",@"AXValue":@8},@[])];
    NSCAssert(!read(@[page(@[button,wrongWeek])])[@"fh"],@"conflicting weekly values fail closed");
    NSDictionary *otherButton = node(@"AXPopUpButton",@{@"AXDescription":@"用量：5 小时限额的 53%周四01:10 重置"},@[]);
    NSCAssert(!read(@[page(@[otherButton,panel])])[@"fh"],@"changing popup/button values fail closed");
    UsageAXReader *limited = [UsageAXReader new]; limited.deadline = 0;
    limited.attribute = ^id(id n,NSString *key) { NSCAssert(NO,@"deadline prevents more IPC"); return nil; };
    NSCAssert(![limited result:@[page(@[button])]][@"fh"],@"deadline never returns partial values");
    UsageAXReader *privacy = [UsageAXReader new]; privacy.deadline = NSProcessInfo.processInfo.systemUptime+5;
    NSDictionary *conversation = node(@"AXTextArea",@{@"AXValue":@"not quota"},@[]);
    privacy.attribute = ^id(id n,NSString *key) {
        NSCAssert(!(n == conversation && ![key isEqual:@"AXRole"]),@"conversation values never read"); return n[key];
    };
    NSCAssert([privacy result:@[page(@[conversation,button])]][@"fh"],@"quota still readable with conversation skipped");
    NSDictionary *deep = node(@"AXGroup",@{},@[secondButton]);
    for (NSUInteger i=0;i<45;i++) deep = node(@"AXGroup",@{},@[deep]);
    NSCAssert(read(@[page(@[deep,button])])[@"fh"] && !read(@[page(@[deep])])[@"fh"],@"deep unrelated branches cannot block supported quota controls or become quota candidates");
    NSArray *identity = @[@1,@123,@456,@0,@[@1,@2],@0,@0,@0,@0];
    NSDictionary *persisted = MergeUsageInterface(nil,open,identity,now);
    NSArray *stored = StoredUsageInterface(@{@1:persisted});
    NSCAssert(!stored[0][@"page"] && !stored[0][@"fh"][@"visible"],@"only quota fields and matching metadata persist");
    NSDictionary *restored = LoadedUsageInterface(stored,now);
    NSCAssert([restored[@1][@"sd"][@"percent"] isEqual:@7] && ![restored[@1][@"sd"][@"visible"] boolValue],@"restart restores last observation, never a fresh reading");
    NSCAssert(!LoadedUsageInterface(stored,[now dateByAddingTimeInterval:-1])[@1],@"future observation rejected");
    NSCAssert(!LoadedUsageInterface(@[@{@"identity":@[@1]}],now)[@1],@"malformed persisted identity rejected");
    NSCAssert(!LoadedUsageInterface([stored arrayByAddingObjectsFromArray:stored],now)[@1],@"ambiguous persisted account rejected");
    NSData *plist = [NSPropertyListSerialization dataWithPropertyList:stored format:NSPropertyListBinaryFormat_v1_0 options:0 error:nil];
    NSCAssert([LoadedUsageInterface([NSPropertyListSerialization propertyListWithData:plist options:0 format:nil error:nil],now) isEqual:restored],@"persistent property-list dates and identity survive round trip");
    NSDictionary *hidden = MergeUsageInterface(restored[@1],@{@"status":@"未观察到 Code 用量"},identity,[now dateByAddingTimeInterval:60]);
    NSCAssert([hidden[@"sd"][@"percent"] isEqual:@7] && [hidden[@"sd"][@"observedAt"] isEqual:now] && ![hidden[@"sd"][@"visible"] boolValue],@"closed interface retains last observation without fabricating freshness");
    NSCAssert(!MergeUsageInterface(restored[@1],closed,@[@2,@123,@456,@0,@[@1,@2],@0,@0,@0,@0],now)[@"sd"],@"persisted quota never crosses accounts");
    puts("PASS: passive Code AX traversal, five-hour/weekly/reset fields, closed popover, ambiguous pages, embedded content, invalid values, account/PID cache isolation, separate observation times and persisted observation validation");
}
