#import <Foundation/Foundation.h>

// Interface language, fixed for one launcher run (set at launch from the saved setting).
extern BOOL UIEnglish;
// Returns the English text for a Chinese interface string when English is active; otherwise (or if untranslated) the input.
NSString *L(NSString *text) NS_FORMAT_ARGUMENT(1);
void TestLocalization(void);
