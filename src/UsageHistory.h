#import <Foundation/Foundation.h>

// Matches history to the locally stored lastActiveOrg selector.
// Uses only Claude Safe Storage to decrypt that selector; no login tokens, network or client control.
NSDictionary *ReadUsageHistory(NSString *profile, NSDate *now);
// Metadata is a scheduling hint only; ReadUsageHistory still validates all paths and identity.
NSArray *UsageHistoryFileState(NSString *profile);
NSString *UsageHistorySourceLabel(NSDate *date, NSDate *now);
// The target account's current organization, for choosing where a synced session goes. Touches the keychain, so call it only from a user action.
NSString *ReadLastActiveOrganization(NSString *profile, NSString **failure);
void TestUsageHistory(void);
