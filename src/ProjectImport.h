#import "Projects.h"

// Read only the known path prefix of desktop records; never deserialize whole sessions.
NSDictionary *DiscoverProjects(NSArray<NSString *> *profiles, ProjectStore *validator, NSString *home);
BOOL ProjectNeedsManualImport(NSString *path, NSString *home);
// Returns appended records without saving; revalidates every selected path.
NSArray *PrepareProjectImport(ProjectStore *store, NSArray<NSString *> *paths, NSString *home, NSUInteger *skipped);
void TestProjectImport(void);
