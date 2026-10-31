#import <Foundation/Foundation.h>

// Local copy of Claude Code configuration between account config folders (01: ~/.claude, 02-10: ClaudeCodeConfig).
// Only settings.json, top-level *.md and entries of skills/agents/commands/output-styles; never .claude.json,
// credentials, history, projects, plugins or caches. Symlinks are copied as links, never followed.
// Item: {name, kind, state}; state is 缺少 / 相同 / 不同 / 已链接 / 含凭据 / 不安全.
NSArray<NSDictionary *> *ConfigSyncItems(NSString *source, NSString *target);
// Adds missing items; differing items are replaced only with overwrite (old copy goes to the Trash).
// link: place a symlink to the source item instead of a copy, so later source edits apply to the target too;
// with overwrite an identical copy also becomes a link. Returns {added, replaced, skipped, error?}.
NSDictionary *ApplyConfigSync(NSString *source, NSString *target, NSArray<NSString *> *names, BOOL overwrite, BOOL link);
// Whether a path relative to a config folder is an entry config sync may place.
BOOL ConfigSyncEntryAllowed(NSString *relative);
void TestConfigSync(void);
