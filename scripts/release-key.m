// macOS 发布签名密钥工具（只在 Mac 发布机使用，不进入应用或 DMG）。
// 私钥只存在登录钥匙串，创建时设为不可导出；本工具从不输出私钥。
//   release-key create                      创建 RSA-3072 密钥（已存在则拒绝），输出公钥 XML
//   release-key public                      输出公钥 XML（.NET RSAKeyValue，Modulus/Exponent 为 Base64）
//   release-key sign <原文文件>              输出 Base64 签名（RSA-SHA256，PKCS#1 v1.5）
//   release-key verify <公钥XML> <原文文件> <Base64签名>   验签，通过返回 0
#import <Foundation/Foundation.h>
#import <Security/Security.h>

static NSData *Tag(void) { return [@"local.claude.profile-launcher.release.macos" dataUsingEncoding:NSUTF8StringEncoding]; }
static SecKeyRef FindPrivate(void) CF_RETURNS_RETAINED {
    NSDictionary *query = @{(id)kSecClass:(id)kSecClassKey,(id)kSecAttrApplicationTag:Tag(),(id)kSecAttrKeyType:(id)kSecAttrKeyTypeRSA,(id)kSecAttrKeyClass:(id)kSecAttrKeyClassPrivate,(id)kSecReturnRef:@YES};
    CFTypeRef result = NULL;
    return SecItemCopyMatching((__bridge CFDictionaryRef)query,&result) == errSecSuccess ? (SecKeyRef)result : NULL;
}
static NSData *ReadInteger(const uint8_t **p, const uint8_t *end) {
    if (*p >= end || **p != 0x02) return nil;
    (*p)++; NSUInteger length = *(*p)++;
    if (length & 0x80) { NSUInteger count = length & 0x7f; length = 0; while (count--) length = (length << 8) | *(*p)++; }
    if (*p + length > end) return nil;
    NSData *value = [NSData dataWithBytes:*p length:length]; *p += length;
    while (value.length > 1 && ((const uint8_t *)value.bytes)[0] == 0) value = [value subdataWithRange:NSMakeRange(1,value.length-1)];
    return value;
}
static NSString *PublicXML(SecKeyRef publicKey) {
    NSData *der = CFBridgingRelease(SecKeyCopyExternalRepresentation(publicKey,NULL));
    const uint8_t *p = der.bytes, *end = p + der.length;
    if (!der || *p++ != 0x30) return nil;
    p += (*p & 0x80) ? 1 + (*p & 0x7f) : 1;
    NSData *modulus = ReadInteger(&p,end), *exponent = ReadInteger(&p,end);
    if (!modulus || !exponent) return nil;
    return [NSString stringWithFormat:@"<RSAKeyValue><Modulus>%@</Modulus><Exponent>%@</Exponent></RSAKeyValue>",[modulus base64EncodedStringWithOptions:0],[exponent base64EncodedStringWithOptions:0]];
}
static NSData *DERWrap(uint8_t tag, NSData *content) {
    NSMutableData *out = [NSMutableData dataWithBytes:&tag length:1]; NSUInteger length = content.length;
    if (length < 128) { uint8_t b = (uint8_t)length; [out appendBytes:&b length:1]; }
    else { uint8_t bytes[8]; int n = 0; for (NSUInteger v = length; v; v >>= 8) bytes[n++] = v & 0xff; uint8_t first = 0x80|(uint8_t)n; [out appendBytes:&first length:1]; while (n) [out appendBytes:&bytes[--n] length:1]; }
    [out appendData:content]; return out;
}
static NSData *DERInteger(NSData *value) {
    NSMutableData *bytes = [value mutableCopy];
    if (((const uint8_t *)bytes.bytes)[0] & 0x80) { uint8_t zero = 0; [bytes replaceBytesInRange:NSMakeRange(0,0) withBytes:&zero length:1]; }
    return DERWrap(0x02,bytes);
}
static SecKeyRef KeyFromXML(NSString *xml) CF_RETURNS_RETAINED {
    NSData *(^field)(NSString *) = ^NSData *(NSString *name) {
        NSRange open = [xml rangeOfString:[NSString stringWithFormat:@"<%@>",name]], close = [xml rangeOfString:[NSString stringWithFormat:@"</%@>",name]];
        if (open.location == NSNotFound || close.location == NSNotFound || NSMaxRange(open) > close.location) return nil;
        return [[NSData alloc] initWithBase64EncodedString:[xml substringWithRange:NSMakeRange(NSMaxRange(open),close.location-NSMaxRange(open))] options:0];
    };
    NSData *modulus = field(@"Modulus"), *exponent = field(@"Exponent");
    if (modulus.length < 256 || !exponent.length) return NULL;
    NSMutableData *sequence = [DERInteger(modulus) mutableCopy]; [sequence appendData:DERInteger(exponent)];
    return SecKeyCreateWithData((__bridge CFDataRef)DERWrap(0x30,sequence),(__bridge CFDictionaryRef)@{(id)kSecAttrKeyType:(id)kSecAttrKeyTypeRSA,(id)kSecAttrKeyClass:(id)kSecAttrKeyClassPublic},NULL);
}
static int Fail(NSString *message) { fprintf(stderr,"%s\n",message.UTF8String); return 1; }

int main(int argc, const char *argv[]) { @autoreleasepool {
    NSString *command = argc > 1 ? @(argv[1]) : @"";
    if ([command isEqual:@"create"]) {
        SecKeyRef existing = FindPrivate();
        if (existing) { CFRelease(existing); return Fail(@"钥匙串中已有 macOS 发布密钥，拒绝覆盖。"); }
        // 不可导出必须写在顶层参数：只放在 kSecPrivateKeyAttrs 里会被登录钥匙串忽略（实测仍可导出）。
        NSDictionary *attributes = @{(id)kSecAttrKeyType:(id)kSecAttrKeyTypeRSA,(id)kSecAttrKeySizeInBits:@3072,(id)kSecAttrIsExtractable:@NO,(id)kSecAttrIsSensitive:@YES,
            (id)kSecPrivateKeyAttrs:@{(id)kSecAttrIsPermanent:@YES,(id)kSecAttrApplicationTag:Tag(),(id)kSecAttrLabel:@"Claude 启动器 macOS 发布签名密钥",(id)kSecAttrIsExtractable:@NO,(id)kSecAttrIsSensitive:@YES}};
        CFErrorRef error = NULL;
        SecKeyRef key = SecKeyCreateRandomKey((__bridge CFDictionaryRef)attributes,&error);
        if (!key) return Fail([@"创建失败：" stringByAppendingString:((__bridge NSError *)error).localizedDescription ?: @"未知错误"]);
        CFDataRef exported = SecKeyCopyExternalRepresentation(key,NULL);
        if (exported) {   // 自检：私钥必须无法导出，否则立即删除并失败
            CFRelease(exported); CFRelease(key);
            SecItemDelete((__bridge CFDictionaryRef)@{(id)kSecClass:(id)kSecClassKey,(id)kSecAttrApplicationTag:Tag()});
            return Fail(@"私钥可以导出，已删除，未创建。");
        }
        CFRelease(key); command = @"public";
    }
    if ([command isEqual:@"public"]) {
        SecKeyRef key = FindPrivate(); if (!key) return Fail(@"钥匙串中没有 macOS 发布密钥。");
        SecKeyRef publicKey = SecKeyCopyPublicKey(key); CFRelease(key);
        NSString *xml = publicKey ? PublicXML(publicKey) : nil; if (publicKey) CFRelease(publicKey);
        if (!xml) return Fail(@"无法读取公钥。");
        printf("%s\n",xml.UTF8String); return 0;
    }
    if ([command isEqual:@"sign"] && argc == 3) {
        NSData *payload = [NSData dataWithContentsOfFile:@(argv[2])]; if (!payload.length) return Fail(@"签名原文为空。");
        SecKeyRef key = FindPrivate(); if (!key) return Fail(@"钥匙串中没有 macOS 发布密钥。");
        NSData *signature = CFBridgingRelease(SecKeyCreateSignature(key,kSecKeyAlgorithmRSASignatureMessagePKCS1v15SHA256,(__bridge CFDataRef)payload,NULL));
        CFRelease(key); if (!signature) return Fail(@"签名失败（钥匙串可能拒绝了访问）。");
        printf("%s\n",[signature base64EncodedStringWithOptions:0].UTF8String); return 0;
    }
    if ([command isEqual:@"verify"] && argc == 5) {
        NSString *xml = [NSString stringWithContentsOfFile:@(argv[2]) encoding:NSUTF8StringEncoding error:nil];
        SecKeyRef key = xml ? KeyFromXML(xml) : NULL; if (!key) return Fail(@"公钥无效。");
        NSData *payload = [NSData dataWithContentsOfFile:@(argv[3])], *signature = [[NSData alloc] initWithBase64EncodedString:@(argv[4]) options:0];
        BOOL ok = payload && signature && SecKeyVerifySignature(key,kSecKeyAlgorithmRSASignatureMessagePKCS1v15SHA256,(__bridge CFDataRef)payload,(__bridge CFDataRef)signature,NULL);
        CFRelease(key); return ok ? 0 : Fail(@"验签失败。");
    }
    return Fail(@"用法：release-key create | public | sign <原文> | verify <公钥XML> <原文> <签名>");
} }
