#import <Foundation/Foundation.h>

// Reads only Claude's dedicated history file. No network, credentials, or client control.
NSDictionary *ReadUsageHistory(NSString *profile, NSDate *now);
void TestUsageHistory(void);
