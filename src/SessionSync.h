#import <Foundation/Foundation.h>

// One-way cleaned copy of a local Code session for another account. Everything is rebuilt from a whitelist;
// nothing here starts Claude, sends a request, or reads credentials.

// Claude Code's project folder name for a working directory; nil if it would need Claude's hashed long form.
NSString *SessionProjectSlug(NSString *cwd);
// Sessions of one project in a config folder, newest first: {path, title, modified}. Reads only each file's tail.
NSArray<NSDictionary *> *SessionSyncList(NSString *configFolder, NSString *project);
// Whether a title carries the suffix of an earlier sync.
BOOL SessionIsSyncCopy(NSString *title);
// On failure: {error}. On success: {sessionId, slug, cwd, title, jsonl, record, preview, report, messages, bytes, modified}.
// sessionId is targetId when replacing a linked copy, otherwise new.
NSDictionary *CleanSession(NSString *sourcePath, NSString *project, NSString *home, NSString *targetId);
// One row per session, keeping the newest of each linked group and adding versions {account: {id, modified}}. sessions: newest first.
NSArray<NSDictionary *> *MergeLinkedSessions(NSArray<NSDictionary *> *sessions, NSArray<NSDictionary *> *links);
// Links are groups {account: session id} of the same conversation; returns them with this pair joined.
NSArray<NSDictionary *> *LinkSessions(NSArray<NSDictionary *> *links, NSInteger from, NSString *fromId, NSInteger to, NSString *toId);
void TestSessionSync(void);
