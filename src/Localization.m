#import "Localization.h"

BOOL UIEnglish;

static NSDictionary *EnglishTable(void) {
    static NSDictionary *table; static dispatch_once_t once;
    dispatch_once(&once, ^{
        NSString *text = [NSString stringWithContentsOfFile:[NSBundle.mainBundle pathForResource:@"english" ofType:@"strings"] encoding:NSUTF8StringEncoding error:nil];
        id parsed = nil;
        @try { parsed = text.propertyListFromStringsFileFormat; } @catch (NSException *e) { parsed = nil; }
        table = [parsed isKindOfClass:NSDictionary.class] ? parsed : @{};
    });
    return table;
}
NSString *L(NSString *text) {
    return UIEnglish && text ? EnglishTable()[text] ?: text : text;
}
static NSArray *FormatSpecifiers(NSString *text) {
    NSRegularExpression *pattern = [NSRegularExpression regularExpressionWithPattern:@"%(?:[0-9]+\\$)?[-+ #0]*[0-9]*(?:\\.[0-9]+)?(?:hh|h|ll|l|q|z|t|j|L)?[@dDuUxXoOfeEgGcCsSpaAF%]" options:0 error:nil];
    NSMutableArray *result = [NSMutableArray array];
    for (NSTextCheckingResult *match in [pattern matchesInString:text options:0 range:NSMakeRange(0,text.length)]) [result addObject:[text substringWithRange:match.range]];
    return result;
}
void TestLocalization(void) {
    NSDictionary *table = EnglishTable();
    NSCAssert(table.count > 500, @"English table loaded");
    for (NSString *key in table) NSCAssert([FormatSpecifiers(key) isEqual:FormatSpecifiers(table[key])], @"format specifiers match: %@", key);
    BOOL saved = UIEnglish;
    UIEnglish = NO; NSCAssert([L(@"取消") isEqual:@"取消"], @"Chinese passes through");
    UIEnglish = YES; NSCAssert([L(@"取消") isEqual:@"Cancel"] && [L(@"未收录") isEqual:@"未收录"] && [L(L(@"取消")) isEqual:@"Cancel"], @"English lookup, fallback and repeat");
    UIEnglish = saved;
    puts("PASS: English interface table loads, keeps format specifiers, falls back to the original text");
}
