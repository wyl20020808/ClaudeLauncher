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
// Local countdown from the observation time and the official reset text: "3d 5h", "2h 30m", "0m"; "" when unknown.
NSString *UsageResetCountdown(NSDictionary *metric, NSDate *now);
// Ring colour: green below 60%, yellow up to 80%, red above.
unsigned UsageQuotaColor(double percent);
NSArray *StoredUsageInterface(NSDictionary *records);
NSDictionary *LoadedUsageInterface(id stored, NSDate *now);
void TestUsageInterface(void);
