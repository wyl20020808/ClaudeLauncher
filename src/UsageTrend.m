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
@property NSDictionary<NSString *, NSColor *> *colors;
@end
@implementation UsageTrendView
- (BOOL)isFlipped { return NO; }
- (void)drawRect:(NSRect)dirty {
    // Transparent: the host card supplies the launcher palette, so light and dark themes both read correctly.
    NSRect plot = NSMakeRect(30,18,NSWidth(self.bounds)-38,NSHeight(self.bounds)-24);
    NSDictionary *axis = @{NSFontAttributeName:[NSFont systemFontOfSize:9],NSForegroundColorAttributeName:self.colors[@"axis"]};
    for (NSInteger p = 0; p <= 100; p += 50) {
        CGFloat y = round(NSMinY(plot)+NSHeight(plot)*p/100.0)+0.5;
        NSBezierPath *rule = [NSBezierPath bezierPath]; [rule moveToPoint:NSMakePoint(NSMinX(plot),y)]; [rule lineToPoint:NSMakePoint(NSMaxX(plot),y)];
        if (p) { CGFloat dash[] = {2,3}; [rule setLineDash:dash count:2 phase:0]; }
        rule.lineWidth = 1; [self.colors[@"grid"] setStroke]; [rule stroke];
        [[NSString stringWithFormat:@"%ld%%",(long)p] drawAtPoint:NSMakePoint(0,y-6) withAttributes:axis];
    }
    // Fit the axis to the observed span (24 hours to 7 days) so a short history is not squeezed into one edge.
    NSTimeInterval first = self.points.count ? [self.points.firstObject[@"time"] doubleValue] : self.end;
    NSTimeInterval span = MIN(7*86400,MAX(86400,(self.end-first)*1.08)), begin = self.end-span;
    CGFloat (^xAt)(NSTimeInterval) = ^CGFloat(NSTimeInterval t) { return NSMinX(plot)+NSWidth(plot)*(t-begin)/span; };
    BOOL hourly = span <= 2*86400; NSTimeInterval step = hourly ? 6*3600 : 86400;
    NSDateFormatter *format = [NSDateFormatter new]; format.dateFormat = hourly ? @"HH:mm" : @"M/d";
    NSDate *anchor = [NSCalendar.currentCalendar startOfDayForDate:[NSDate dateWithTimeIntervalSince1970:self.end]];
    NSTimeInterval tick = anchor.timeIntervalSince1970; while (tick+step <= self.end) tick += step;
    for (; tick >= begin; tick -= step) {
        NSString *label = [format stringFromDate:[NSDate dateWithTimeIntervalSince1970:tick]]; CGFloat x = xAt(tick);
        [label drawAtPoint:NSMakePoint(MAX(NSMinX(plot),MIN(x-[label sizeWithAttributes:axis].width/2,NSMaxX(plot)-26)),0) withAttributes:axis];
    }
    for (NSString *key in @[@"fh",@"sd"]) {
        // Split into runs that never decrease; a drop is a quota reset and must not be bridged by a line.
        NSMutableArray<NSMutableArray<NSValue *> *> *runs = [NSMutableArray array]; double previous = -1;
        for (NSDictionary *point in self.points) {
            if (!point[key] || [point[@"time"] doubleValue] < begin) continue;
            double value = [point[key] doubleValue];
            NSPoint at = NSMakePoint(xAt([point[@"time"] doubleValue]), NSMinY(plot)+NSHeight(plot)*value/100);
            if (!runs.count || value < previous) [runs addObject:[NSMutableArray array]];
            [runs.lastObject addObject:[NSValue valueWithPoint:at]]; previous = value;
        }
        NSColor *color = self.colors[key];
        for (NSArray<NSValue *> *run in runs) {
            NSBezierPath *line = [NSBezierPath bezierPath];
            for (NSValue *value in run) { if (line.isEmpty) [line moveToPoint:value.pointValue]; else [line lineToPoint:value.pointValue]; }
            if (run.count > 1 && [key isEqual:@"sd"]) {
                NSBezierPath *area = line.copy;
                [area lineToPoint:NSMakePoint(run.lastObject.pointValue.x,NSMinY(plot))]; [area lineToPoint:NSMakePoint(run.firstObject.pointValue.x,NSMinY(plot))]; [area closePath];
                [[color colorWithAlphaComponent:0.12] setFill]; [area fill];
            }
            // Five-hour use resets several times a day; keep it light so the weekly line stays readable.
            line.lineWidth = [key isEqual:@"sd"] ? 2.2 : 1; line.lineJoinStyle = NSLineJoinStyleRound; line.lineCapStyle = NSLineCapStyleRound;
            [([key isEqual:@"sd"] ? color : [color colorWithAlphaComponent:0.45]) setStroke]; [line stroke];
            if (run.count == 1) { [color setFill]; NSPoint at = run[0].pointValue; [[NSBezierPath bezierPathWithOvalInRect:NSMakeRect(at.x-2,at.y-2,4,4)] fill]; }
        }
        NSValue *last = runs.lastObject.lastObject;
        if (last) {
            NSPoint at = last.pointValue;
            [[color colorWithAlphaComponent:0.22] setFill]; [[NSBezierPath bezierPathWithOvalInRect:NSMakeRect(at.x-5,at.y-5,10,10)] fill];
            [color setFill]; [[NSBezierPath bezierPathWithOvalInRect:NSMakeRect(at.x-2.5,at.y-2.5,5,5)] fill];
        }
    }
}
@end

NSView *UsageTrendChart(NSArray *points, NSInteger account, NSRect frame, NSDate *now, NSDictionary<NSString *, NSColor *> *colors) {
    UsageTrendView *view = [[UsageTrendView alloc] initWithFrame:frame];
    view.points = AccountPoints(points,account,nil); view.end = now.timeIntervalSince1970; view.colors = colors;
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
