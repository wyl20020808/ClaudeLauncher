#import <Foundation/Foundation.h>

// Caller validates the official main PID and profile before and after this read.
// Only copies existing AX attributes. Never prompts, activates, clicks or refreshes Claude.
NSDictionary *ReadUsageInterface(pid_t pid);
// Fingerprint only the non-login lastActiveOrg selector; no decryption, keychain or login rows.
NSData *UsageOrganizationMarker(NSString *profile);
// Optional AX notifications only schedule another read; unsupported notifications fall back to the timer.
id ObserveUsageInterface(pid_t pid, void (^changed)(void));
NSDictionary *MergeUsageInterface(NSDictionary *previous, NSDictionary *snapshot, NSArray *identity, NSDate *now);
NSString *UsageInterfaceTimeLabel(NSDictionary *metric, NSDate *now);
NSArray *StoredUsageInterface(NSDictionary *records);
NSDictionary *LoadedUsageInterface(id stored, NSDate *now);
void TestUsageInterface(void);
