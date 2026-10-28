#import <Foundation/Foundation.h>

NSDictionary *DefaultSettings(void);
NSDictionary *ValidatedSettings(id input);
NSString *ValidatedAccountName(id input);
NSArray *FilteredLogs(NSArray *entries, NSString *query, NSString *instance, NSString *level, NSDate *since);
BOOL AllowedUpdateURL(NSURL *url, BOOL redirect);
void TestSettings(void);
