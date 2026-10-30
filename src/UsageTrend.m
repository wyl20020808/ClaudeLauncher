#import "UsageTrend.h"
#import "Localization.h"

static const NSTimeInterval TrendKeep = 8*86400, TrendRepeat = 15*60;
static const NSUInteger TrendCap = 4000;

static NSNumber *TrendPercent(id value) {
    if (![value isKindOfClass:NSNumber.class] || CFGetTypeID((__bridge CFTypeRef)value) == CFBooleanGetTypeID()) return nil;
    double v = [value doubleValue];
    return isfinite(v) && v >= 0 && v <= 100 ? @(v) : nil;
}
static NSArray *AccountPoints(NSArray *points, NSInteger account, NSString *key) {
    NSMutableArray *result = [NSMutableArray array];
    for (NSDictionary *point in points) if ([point[@"account"] integerValue] == account && (!key || point[key])) [result addObject:point];
    return result;
}

NSArray *AppendUsageTrend(NSArray *points, NSInteger account, NSNumber *fh, NSNumber *sd, NSDate *now) {
    fh = TrendPercent(fh); sd = TrendPercent(sd);
    NSTimeInterval time = now.timeIntervalSince1970;
    NSMutableArray *kept = [NSMutableArray array];
    for (NSDictionary *point in points ?: @[]) if ([point[@"time"] doubleValue] >= time-TrendKeep) [kept addObject:point];
    if (account < 1 || account > 10 || !(fh || sd)) return kept;
    NSDictionary *last = AccountPoints(kept,account,nil).lastObject;
    // Repeated identical readings only refresh the card; keep one point per quarter hour so the chart stays small.
    if (last && (last[@"fh"] == fh || [last[@"fh"] isEqual:fh]) && (last[@"sd"] == sd || [last[@"sd"] isEqual:sd]) &&
        time-[last[@"time"] doubleValue] < TrendRepeat) return kept;
    NSMutableDictionary *point = [@{@"account":@(account),@"time":@(time)} mutableCopy];
    if (fh) point[@"fh"] = fh;
    if (sd) point[@"sd"] = sd;
    [kept addObject:point];
    if (kept.count > TrendCap) [kept removeObjectsInRange:NSMakeRange(0,kept.count-TrendCap)];
    return kept;
}

NSArray *LoadedUsageTrend(id stored, NSDate *now) {
    if (![stored isKindOfClass:NSArray.class] || [stored count] > TrendCap) return @[];
    NSTimeInterval time = now.timeIntervalSince1970;
    NSMutableArray *points = [NSMutableArray array];
    for (id item in stored) {
        if (![item isKindOfClass:NSDictionary.class]) continue;
        id account = item[@"account"], at = item[@"time"];
        if (![account isKindOfClass:NSNumber.class] || [account integerValue] < 1 || [account integerValue] > 10) continue;
        if (![at isKindOfClass:NSNumber.class] || !isfinite([at doubleValue]) || [at doubleValue] < time-TrendKeep || [at doubleValue] > time+60) continue;
        NSNumber *fh = TrendPercent(item[@"fh"]), *sd = TrendPercent(item[@"sd"]);
        if ((item[@"fh"] && !fh) || (item[@"sd"] && !sd) || !(fh || sd)) continue;
        NSMutableDictionary *point = [@{@"account":@([account integerValue]),@"time":@([at doubleValue])} mutableCopy];
        if (fh) point[@"fh"] = fh;
        if (sd) point[@"sd"] = sd;
        [points addObject:point];
    }
    [points sortUsingComparator:^NSComparisonResult(NSDictionary *a, NSDictionary *b) { return [a[@"time"] compare:b[@"time"]]; }];
    return points;
}

NSNumber *UsageHoursLeft(NSArray *points, NSInteger account, NSString *key, NSDate *now) {
    NSArray *series = AccountPoints(points,account,key);
    NSDictionary *last = series.lastObject;
    if (!last || now.timeIntervalSince1970-[last[@"time"] doubleValue] > 6*3600) return nil;
    NSTimeInterval window = [key isEqual:@"fh"] ? 5*3600 : 7*86400;
    NSDictionary *start = last;
    // Walk back while use never decreased: a drop means the quota reset, so older points belong to another window.
    for (NSInteger i = (NSInteger)series.count-2; i >= 0; i--) {
        NSDictionary *point = series[i];
        if ([point[key] doubleValue] > [start[key] doubleValue] || [last[@"time"] doubleValue]-[point[@"time"] doubleValue] > window) break;
        start = point;
    }
    double span = [last[@"time"] doubleValue]-[start[@"time"] doubleValue], rise = [last[key] doubleValue]-[start[key] doubleValue];
    if (span < 1200 || rise <= 0) return nil;
    return @((100-[last[key] doubleValue])/(rise/(span/3600)));
}

NSInteger UsageRotationAccount(NSArray *points, NSArray<NSNumber *> *accounts, NSInteger exclude) {
    NSInteger best = 0; double bestWeek = INFINITY, bestFive = INFINITY;
    for (NSNumber *account in accounts) {
        if (account.integerValue == exclude) continue;
        // Weekly values are seen less often than five-hour ones, so take the latest of each separately.
        NSNumber *lastWeek = AccountPoints(points,account.integerValue,@"sd").lastObject[@"sd"], *lastFive = AccountPoints(points,account.integerValue,@"fh").lastObject[@"fh"];
        if (!lastWeek && !lastFive) continue;
        double week = (lastWeek ?: lastFive).doubleValue, five = (lastFive ?: lastWeek).doubleValue;
        if (week < bestWeek || (week == bestWeek && five < bestFive)) { best = account.integerValue; bestWeek = week; bestFive = five; }
    }
    return best;
}

@interface UsageTrendView : NSView
@property NSArray *points;
@property NSTimeInterval end;
@end
@implementation UsageTrendView
- (BOOL)isFlipped { return NO; }
- (void)drawRect:(NSRect)dirty {
    // Own light panel so the launcher palette reads the same inside light or dark system alerts.
    [[NSColor colorWithSRGBRed:0.99 green:0.98 blue:0.96 alpha:1] setFill]; [[NSBezierPath bezierPathWithRoundedRect:self.bounds xRadius:8 yRadius:8] fill];
    NSRect plot = NSInsetRect(self.bounds,8,10); plot.origin.x += 28; plot.size.width -= 36;
    NSDictionary *axis = @{NSFontAttributeName:[NSFont systemFontOfSize:9],NSForegroundColorAttributeName:[NSColor colorWithSRGBRed:0.48 green:0.44 blue:0.39 alpha:1]};
    for (NSInteger p = 0; p <= 100; p += 50) {
        CGFloat y = NSMinY(plot)+NSHeight(plot)*p/100.0;
        [[NSColor colorWithSRGBRed:0.94 green:0.91 blue:0.86 alpha:1] setFill]; NSRectFill(NSMakeRect(NSMinX(plot),y,NSWidth(plot),1));
        [[NSString stringWithFormat:@"%ld%%",(long)p] drawAtPoint:NSMakePoint(NSMinX(self.bounds)+2,y-6) withAttributes:axis];
    }
    NSTimeInterval begin = self.end-7*86400;
    for (NSString *key in @[@"sd",@"fh"]) {
        NSBezierPath *line = [NSBezierPath bezierPath], *dots = [NSBezierPath bezierPath]; BOOL started = NO; double previous = -1;
        for (NSDictionary *point in self.points) {
            if (!point[key] || [point[@"time"] doubleValue] < begin) continue;
            double value = [point[key] doubleValue];
            NSPoint at = NSMakePoint(NSMinX(plot)+NSWidth(plot)*([point[@"time"] doubleValue]-begin)/(7*86400), NSMinY(plot)+NSHeight(plot)*value/100);
            if (!started || value < previous) [line moveToPoint:at]; else [line lineToPoint:at]; // Never draw a line across a reset.
            started = YES; previous = value;
            [dots appendBezierPath:[NSBezierPath bezierPathWithOvalInRect:NSMakeRect(at.x-2.5,at.y-2.5,5,5)]];
        }
        line.lineWidth = [key isEqual:@"sd"] ? 2 : 1.4;
        NSColor *color = [key isEqual:@"sd"] ? [NSColor colorWithSRGBRed:0.55 green:0.36 blue:0.25 alpha:1] : [NSColor colorWithSRGBRed:0.93 green:0.55 blue:0.30 alpha:1];
        [color set]; [line stroke]; [dots fill];
    }
}
@end

NSView *UsageTrendChart(NSArray *points, NSInteger account, NSRect frame, NSDate *now) {
    UsageTrendView *view = [[UsageTrendView alloc] initWithFrame:frame];
    view.points = AccountPoints(points,account,nil); view.end = now.timeIntervalSince1970;
    view.accessibilityLabel = [NSString stringWithFormat:L(@"账号 %02ld 近 7 天用量趋势"),(long)account];
    return view;
}

void TestUsageTrend(void) {
    NSDate *t0 = [NSDate dateWithTimeIntervalSince1970:1800000000];
    NSDate *(^at)(double) = ^NSDate *(double minutes) { return [t0 dateByAddingTimeInterval:minutes*60]; };
    NSArray *points = AppendUsageTrend(nil,2,@10,@20,t0);
    NSCAssert(points.count == 1 && AppendUsageTrend(points,2,@10,@20,at(5)).count == 1,@"identical readings within 15 minutes are not repeated");
    NSCAssert(AppendUsageTrend(points,2,@11,@20,at(5)).count == 2 && AppendUsageTrend(points,2,@10,@20,at(16)).count == 2,@"changes and quarter-hour points are kept");
    NSCAssert(AppendUsageTrend(points,11,@10,@20,at(30)).count == 1 && AppendUsageTrend(points,3,nil,nil,at(30)).count == 1 &&
              AppendUsageTrend(points,3,@101,@(NAN),at(30)).count == 1 && AppendUsageTrend(points,3,(id)kCFBooleanTrue,nil,at(30)).count == 1,@"invalid accounts and values ignored");
    NSCAssert(AppendUsageTrend(points,2,@50,nil,[t0 dateByAddingTimeInterval:9*86400]).count == 1,@"points older than 8 days dropped");
    NSArray *stored = @[@{@"account":@2,@"time":@(1800000000),@"fh":@10},@{@"account":@0,@"time":@(1800000000),@"fh":@1},@"bad",
                        @{@"account":@2,@"time":@(1800000000+999999),@"fh":@1},@{@"account":@2,@"time":@(1800000000-60),@"sd":@200},
                        @{@"account":@3,@"time":@(1800000000-120),@"sd":@5}];
    NSArray *loaded = LoadedUsageTrend(stored,t0);
    NSCAssert(loaded.count == 2 && [loaded.firstObject[@"account"] isEqual:@3] && [LoadedUsageTrend(@{},t0) isEqual:@[]],@"stored trend validated and sorted");
    NSArray *rising = AppendUsageTrend(AppendUsageTrend(AppendUsageTrend(nil,2,@80,@10,at(-120)),2,@5,@20,at(-60)),2,@25,@30,at(0));
    NSCAssert(fabs([UsageHoursLeft(rising,2,@"fh",t0) doubleValue]-3.75) < 0.001,@"five-hour forecast starts after the reset drop");
    NSCAssert(fabs([UsageHoursLeft(rising,2,@"sd",t0) doubleValue]-7) < 0.001,@"weekly forecast uses the rising span");
    NSCAssert(!UsageHoursLeft(rising,2,@"fh",at(7*60)) && !UsageHoursLeft(AppendUsageTrend(nil,2,@5,nil,t0),2,@"fh",t0) &&
              !UsageHoursLeft(rising,3,@"fh",t0),@"stale, single-point and unknown accounts have no forecast");
    NSArray *several = AppendUsageTrend(AppendUsageTrend(AppendUsageTrend(nil,1,@90,@60,t0),2,@40,@20,t0),3,@10,@20,t0);
    NSCAssert(UsageRotationAccount(several,@[@1,@2,@3,@4],0) == 3 && UsageRotationAccount(several,@[@1,@2,@3],3) == 2 &&
              UsageRotationAccount(several,@[@4],0) == 0,@"rotation prefers lowest weekly then five-hour use");
    puts("PASS: usage trend keeps only observed values (8 days, deduplicated, validated), reset-aware forecasts, rotation choice; synthetic data only");
}
