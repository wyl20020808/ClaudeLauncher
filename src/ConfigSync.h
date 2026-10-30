#import <Foundation/Foundation.h>

// Local copy of Claude Code configuration between account config folders (01: ~/.claude, 02-10: ClaudeCodeConfig).
// Only settings.json, top-level *.md and entries of skills/agents/commands/output-styles; never .claude.json,
// credentials, history, projects, plugins or caches. Symlinks are copied as links, never followed.
// Item: {name, kind, state}; state is 缺少 / 相同 / 不同 / 含凭据 / 不安全.
NSArray<NSDictionary *> *ConfigSyncItems(NSString *source, NSString *target);
// Copies missing items; differing items are replaced only with overwrite (old copy goes to the Trash).
// Returns {added, replaced, skipped, error?}.
NSDictionary *ApplyConfigSync(NSString *source, NSString *target, NSArray<NSString *> *names, BOOL overwrite);
void TestConfigSync(void);
