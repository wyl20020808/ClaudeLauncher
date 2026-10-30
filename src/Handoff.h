#import <Foundation/Foundation.h>

// Local handoff between accounts working on the same project. The launcher never sends messages:
// it writes a marked rule into the project's CLAUDE.md on request, reads HANDOFF.md metadata, and prepares prompts to paste.
NSString *HandoffRuleBlock(void);
NSString *AppendHandoffRule(NSString *directory, BOOL *alreadyPresent); // nil on success, otherwise the reason nothing was written.
NSDate *HandoffModified(NSString *directory);                           // HANDOFF.md modification time; nil if missing or not a regular file.
NSString *HandoffAge(NSDate *modified, NSDate *now);
NSString *HandoffWritePrompt(void);
NSString *HandoffContinuePrompt(NSString *directory);
void TestHandoff(void);
