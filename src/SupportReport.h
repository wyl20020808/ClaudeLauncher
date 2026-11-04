#import <Foundation/Foundation.h>

// Read-only diagnostics for session sync (SPEC 问题诊断). The report holds counts, reasons and masked paths only: no
// conversation text, session titles, credentials or organization identifiers. Saving, copying and submitting are the user's call.
@interface SupportInput : NSObject
@property NSString *version, *system;
@property BOOL updatesEnabled, projectsUnreadable;
@property NSArray<NSDictionary *> *projects;                  // {name, path}
@property NSString *(^projectProblem)(NSString *path);         // why the sync window leaves it out; nil when usable
@property NSArray<NSNumber *> *accounts;
@property NSString *(^config)(NSInteger account);              // Claude Code config folder
@property NSString *(^profile)(NSInteger account);             // account data folder
@property NSString *(^targetGuard)(NSInteger account);         // why it cannot be a sync target now; nil when it can
@property NSArray<NSDictionary *> *links;                      // sessionSyncLinks: [{account: session id}]
@property NSArray<NSDictionary *> *logs;                       // launcher log entries, newest first: {date, level, text}
@property NSString *home;
@property NSDate *now;
@end

extern NSString *const SupportIssuesNew;
// {text, summary, problems, notices}
NSDictionary *BuildSupportReport(SupportInput *input);
// "~/…/name" or "/…/name": only the last folder name stays.
NSString *SupportMask(NSString *path, NSString *home);
// The last 15 launcher errors, oldest first, consecutive repeats merged; the home folder is replaced by ~.
NSArray<NSString *> *SupportRecentErrors(NSArray<NSDictionary *> *logs, NSString *home);
// Prefilled GitHub "new issue" page; complete is NO when only the conclusions fit (the caller copies the full report).
NSString *SupportIssueURL(NSDictionary *result, BOOL *complete);
void TestSupportReport(void);
