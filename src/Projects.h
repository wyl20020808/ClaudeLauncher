#import <Foundation/Foundation.h>

// Only launcher-owned metadata is serialized. Project contents are never opened.
@interface ProjectStore : NSObject
@property(nonatomic, readonly) NSArray<NSDictionary *> *projects;
@property(nonatomic, readonly) NSString *notice;
@property(nonatomic, readonly) BOOL writable;
- (instancetype)initWithDirectory:(NSString *)directory home:(NSString *)home applicationPaths:(NSArray<NSString *> *)applications;
- (void)load;
- (BOOL)save:(NSArray<NSDictionary *> *)projects error:(NSString **)failure;
- (NSString *)normalizeDirectory:(NSString *)path error:(NSString **)failure;
- (NSString *)statusForPath:(NSString *)path;
- (BOOL)containsDirectory:(NSString *)path;
@end

void TestProjects(void);
