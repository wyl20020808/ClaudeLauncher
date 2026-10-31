#import "Projects.h"

// Read only the known path prefix of desktop records; never deserialize whole sessions.
NSDictionary *DiscoverProjects(NSArray<NSString *> *profiles, ProjectStore *validator, NSString *home);
BOOL ProjectNeedsManualImport(NSString *path, NSString *home);
// Returns appended records without saving; revalidates every selected path.
NSArray *PrepareProjectImport(ProjectStore *store, NSArray<NSString *> *paths, NSString *home, NSUInteger *skipped);
void TestProjectImport(void);

// Appends fresh, empty archived records only. guard is checked again before each atomic publication.
// Results include added/skipped/planned and an optional error; a partial success is never hidden.
NSDictionary *RegisterProjectDirectories(NSString *profile, NSArray<NSString *> *paths,
    ProjectStore *validator, NSString *home, BOOL apply, NSString *(^guard)(void));
void TestProjectRegistration(void);

// Publishes a cleaned session: transcript first, then the record that points at it. Never overwrites; a failed
// record removes only the transcript created here. guard is checked before each publication. Returns nil on success.
NSString *PublishSyncedSession(NSString *profile, NSString *configFolder, NSString *slug, NSString *session,
    NSData *transcript, NSData *record, NSString *(^guard)(void));
