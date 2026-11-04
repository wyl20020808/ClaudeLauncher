#import <Foundation/Foundation.h>

// One-way cleaned copy of a local Code session for another account. Everything is rebuilt from a whitelist;
// nothing here starts Claude, sends a request, or reads credentials.

// Claude Code's project folder name for a working directory; nil if it would need Claude's hashed long form.
NSString *SessionProjectSlug(NSString *cwd);
// Every non-empty session file in one project's folder, newest first: {path, id, title, modified, stamp (nanoseconds)}. Reads only each
// file's tail. The sync list no longer uses it (records decide, see SessionSyncRows); diagnostics counts these files.
NSArray<NSDictionary *> *SessionSyncList(NSString *configFolder, NSString *project);
// What a listing skipped and why, for diagnostics. IDs and working directories only, never titles or text.
@interface SessionSyncScan : NSObject
@property NSMutableArray<NSString *> *problems;
@property NSUInteger records, unreadable, invalid, unstarted;   // unstarted: records without cliSessionId (no conversation yet)
@property NSMutableDictionary<NSString *,NSString *> *active;   // cliSessionId -> cwd, unarchived
@property NSMutableArray<NSArray<NSString *> *> *archived;       // [cliSessionId, cwd]
@property NSMutableArray<NSDictionary *> *skipped;              // {id, cwd, reason: outside|slug|missing|link|empty, project?}
@property BOOL noProjectsFolder;
@end
// The sync list follows the source's official records (SPEC 同步会话): only unarchived records, whose transcript is
// projects/<cwd folder>/<cliSessionId>.jsonl, each under the deepest listed project holding its cwd (worktrees included).
// projects: [{path, name, ...}]. Rows, newest first: {path, id, title, modified, stamp, project, group?, groupRank?}. scan may be nil.
NSArray<NSDictionary *> *SessionSyncRows(NSString *configFolder, NSString *profile, NSArray<NSDictionary *> *projects, SessionSyncScan *scan);
// The deepest project holding cwd (real paths, so links and worktrees count); nil when outside all of them.
NSDictionary *SessionProjectFor(NSString *cwd, NSArray<NSDictionary *> *projects);
// Source sidebar groups (read only) of one record scope in <profile>/claude_desktop_config.json:
// {byKey: {"code:<record sessionId>": group name}, order: [names in sidebar order]}. Empty when unreadable.
NSDictionary *SidebarGroupsRead(NSString *profile, NSString *scope);
// Grouping instruction for the target's own Claude: fixed text plus {"groups":[{"name","sessions":[sidebar IDs]}]}.
// pairs: [group name, sidebar ID]; groups keep first-seen order, IDs are deduplicated, invalid ones dropped. nil when none is left.
NSString *SidebarGroupInstruction(NSArray<NSArray<NSString *> *> *pairs);
// claude://code/new?q=…&folder=…, which only fills in a new Code session; nil when the prompt is too long, empty or starts with /.
NSString *SidebarNewSessionLink(NSString *prompt, NSString *folder);
// Only links in the form SidebarNewSessionLink makes: escaped, printable ASCII, no quotes or spaces.
BOOL SidebarLinkValid(NSString *link);
// 【group】title, not doubled when the title already starts with it.
NSString *SessionGroupTitle(NSString *title, NSString *group);
// Whether a title carries the suffix of an earlier sync.
BOOL SessionIsSyncCopy(NSString *title);
// On failure: {error}. On success: {sessionId, slug, cwd, title, jsonl, record, preview, report, messages, bytes, stamp}.
// sessionId is targetId when replacing a linked copy, otherwise new. group (may be nil) prefixes the copy's title.
NSDictionary *CleanSession(NSString *sourcePath, NSString *project, NSString *home, NSString *targetId, NSString *group);
// One row per session, keeping the newest of each linked group and adding versions {account: {id, stamp}} and linked. sessions: newest first.
NSArray<NSDictionary *> *MergeLinkedSessions(NSArray<NSDictionary *> *sessions, NSArray<NSDictionary *> *links);
// Links are groups {account: session id} of the same conversation; returns them with this pair joined.
NSArray<NSDictionary *> *LinkSessions(NSArray<NSDictionary *> *links, NSInteger from, NSString *fromId, NSInteger to, NSString *toId);
void TestSessionSync(void);
