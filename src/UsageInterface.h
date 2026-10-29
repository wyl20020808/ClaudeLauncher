#import <Foundation/Foundation.h>

// Caller validates the official main PID and profile before and after this read.
// Initializes Electron's local AX tree when disabled, then copies existing quota attributes.
// Never grants permissions, activates, clicks or refreshes Claude.
NSDictionary *ReadUsageInterface(pid_t pid);
// Fingerprint only the non-login lastActiveOrg selector; no decryption, keychain or login rows.
NSData *UsageOrganizationMarker(NSString *profile);
// Only a recognized quota button changing to expanded triggers the callback; no polling or clicks.
id ObserveUsagePopover(pid_t pid, void (^opened)(void));
NSDictionary *MergeUsageInterface(NSDictionary *previous, NSDictionary *snapshot, NSArray *identity, NSDate *now);
NSString *UsageInterfaceTimeLabel(NSDictionary *metric, NSDate *now);
NSArray *StoredUsageInterface(NSDictionary *records);
NSDictionary *LoadedUsageInterface(id stored, NSDate *now);
void TestUsageInterface(void);
