#import <Cocoa/Cocoa.h>

// Launcher-owned history of quota values the launcher already observed; nothing new is read or queried.
// Point: {account, time (seconds since 1970), fh, sd} with percentages 0-100 or absent.
NSArray *AppendUsageTrend(NSArray *points, NSInteger account, NSNumber *fh, NSNumber *sd, NSDate *now);
NSArray *LoadedUsageTrend(id stored, NSDate *now);
// Hours until 100% at the rate since the latest reset; nil without a rising, recent span of at least 20 minutes.
NSNumber *UsageHoursLeft(NSArray *points, NSInteger account, NSString *key, NSDate *now);
// Lowest latest weekly use, ties broken by five-hour use; 0 when nothing was observed.
NSInteger UsageRotationAccount(NSArray *points, NSArray<NSNumber *> *accounts, NSInteger exclude);
NSView *UsageTrendChart(NSArray *points, NSInteger account, NSRect frame, NSDate *now);
void TestUsageTrend(void);
