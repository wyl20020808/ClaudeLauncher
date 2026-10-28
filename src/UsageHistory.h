#import <Foundation/Foundation.h>

// Matches history to the locally stored lastActiveOrg selector.
// Uses only Claude Safe Storage to decrypt that selector; no login tokens, network or client control.
NSDictionary *ReadUsageHistory(NSString *profile, NSDate *now);
void TestUsageHistory(void);
