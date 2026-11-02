#import "Projects.h"

// Read only the known path prefix of desktop records; never deserialize whole sessions.
NSDictionary *DiscoverProjects(NSArray<NSString *> *profiles, ProjectStore *validator, NSString *home);
BOOL ProjectNeedsManualImport(NSString *path, NSString *home);
// Returns appended records without saving; revalidates every selected path.
NSArray *PrepareProjectImport(ProjectStore *store, NSArray<NSString *> *paths, NSString *home, NSUInteger *skipped);
void TestProjectImport(void);

// Publishes a cleaned session: transcript first, then the record that points at it. Never overwrites; a failed
// record removes only the transcript created here. guard is checked before each publication. Returns nil on success.
NSString *PublishSyncedSession(NSString *profile, NSString *configFolder, NSString *slug, NSString *session,
    NSData *transcript, NSData *record, NSString *(^guard)(void));
// Replaces a linked session's transcript with a newer version. The old transcript goes through trash (nil: the Trash) and is put
// back if the new one cannot be published. The target's own record keeps its fields; only title and lastActivityAt change, or
// record is added when it has none. Refused when the target's file is newer than modified. Returns nil on success.
NSString *ReplaceSyncedSession(NSString *profile, NSString *configFolder, NSString *slug, NSString *session,
    NSData *transcript, NSData *record, NSDate *modified, NSURL *(^trash)(NSURL *path), NSString *(^guard)(void));
// Gives a published transcript the source's modification time, so both sides then read as the same version.
void SetSyncedSessionDate(NSString *configFolder, NSString *slug, NSString *session, NSDate *modified);
// True once the account has its one account/organization folder; a session cannot be published before that.
BOOL SyncTargetReady(NSString *profile);
