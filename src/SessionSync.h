#import <Foundation/Foundation.h>

// One-way cleaned copy of a local Code session for another account. Everything is rebuilt from a whitelist;
// nothing here starts Claude, sends a request, or reads credentials.

// Claude Code's project folder name for a working directory; nil if it would need Claude's hashed long form.
NSString *SessionProjectSlug(NSString *cwd);
// Sessions of one project in a config folder, newest first: {path, title, modified}. Reads only each file's tail.
NSArray<NSDictionary *> *SessionSyncList(NSString *configFolder, NSString *project);
// On failure: {error}. On success: {sessionId, slug, cwd, title, jsonl, record, preview, report, messages, bytes}.
NSDictionary *CleanSession(NSString *sourcePath, NSString *project, NSString *home);
void TestSessionSync(void);
