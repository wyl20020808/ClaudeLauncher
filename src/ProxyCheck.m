#import "ProxyCheck.h"
#import "Localization.h"
#import <SystemConfiguration/SystemConfiguration.h>
#import <sys/socket.h>
#import <netinet/in.h>
#import <arpa/inet.h>
#import <fcntl.h>
#import <poll.h>
#import <unistd.h>

static BOOL IsLoopbackHost(NSString *host) {
    NSString *h = host.lowercaseString;
    return [h isEqualToString:@"localhost"] || [h hasPrefix:@"127."] || [h isEqualToString:@"::1"];
}

static BOOL IsTunnelInterface(NSString *name) {
    for (NSString *prefix in @[@"utun",@"tun",@"tap",@"ppp",@"ipsec"]) if ([name hasPrefix:prefix]) return YES;
    return NO;
}

// Pure: turns SCDynamicStoreCopyProxies-style keys and the primary interface name into findings.
NSArray<NSDictionary *> *ProxyFindings(NSDictionary *proxies, NSString *primaryInterface) {
    NSMutableArray *findings = [NSMutableArray array];
    NSArray *kinds = @[@[@"HTTP",@"HTTPEnable",@"HTTPProxy",@"HTTPPort"],
                       @[@"HTTPS",@"HTTPSEnable",@"HTTPSProxy",@"HTTPSPort"],
                       @[@"SOCKS",@"SOCKSEnable",@"SOCKSProxy",@"SOCKSPort"]];
    for (NSArray *k in kinds) {
        id enabled = proxies[k[1]], host = proxies[k[2]], port = proxies[k[3]];
        if (![enabled respondsToSelector:@selector(boolValue)] || ![enabled boolValue]) continue;
        if (![host isKindOfClass:NSString.class] || ![host length]) continue;
        NSInteger p = [port respondsToSelector:@selector(integerValue)] ? [port integerValue] : 0;
        if (p < 1 || p > 65535) continue;
        [findings addObject:@{@"kind":k[0],@"host":host,@"port":@(p)}];
    }
    id pac = proxies[@"ProxyAutoConfigEnable"], pacURL = proxies[@"ProxyAutoConfigURLString"];
    if ([pac respondsToSelector:@selector(boolValue)] && [pac boolValue] && [pacURL isKindOfClass:NSString.class] && [pacURL length])
        [findings addObject:@{@"kind":@"PAC",@"url":pacURL}];
    if (primaryInterface.length && IsTunnelInterface(primaryInterface))
        [findings addObject:@{@"kind":@"TUN",@"interface":primaryInterface}];
    return findings;
}

// Connects only to loopback addresses, so nothing leaves this Mac.
static BOOL LoopbackPortOpen(NSString *host, NSInteger port) {
    BOOL v6 = [host isEqualToString:@"::1"];
    int fd = socket(v6 ? AF_INET6 : AF_INET, SOCK_STREAM, 0);
    if (fd < 0) return NO;
    fcntl(fd, F_SETFL, fcntl(fd, F_GETFL, 0) | O_NONBLOCK);
    int rc;
    if (v6) {
        struct sockaddr_in6 a = {0}; a.sin6_len = sizeof(a); a.sin6_family = AF_INET6; a.sin6_port = htons((uint16_t)port); a.sin6_addr = in6addr_loopback;
        rc = connect(fd, (struct sockaddr *)&a, sizeof(a));
    } else {
        struct sockaddr_in a = {0}; a.sin_len = sizeof(a); a.sin_family = AF_INET; a.sin_port = htons((uint16_t)port);
        if ([host.lowercaseString isEqualToString:@"localhost"] || inet_pton(AF_INET, host.UTF8String, &a.sin_addr) != 1) a.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
        rc = connect(fd, (struct sockaddr *)&a, sizeof(a));
    }
    BOOL open = rc == 0;
    if (!open && errno == EINPROGRESS) {
        struct pollfd p = {fd, POLLOUT, 0};
        int err = 0; socklen_t len = sizeof(err);
        open = poll(&p, 1, 800) == 1 && getsockopt(fd, SOL_SOCKET, SO_ERROR, &err, &len) == 0 && err == 0;
    }
    close(fd);
    return open;
}

NSArray<NSString *> *KnownProxyApps(NSArray<NSDictionary *> *apps) {
    // Bundle identifier prefixes first; display-name keywords catch renamed or less common builds.
    NSDictionary *bundles = @{@"io.github.clash-verge-rev":@"Clash Verge",@"com.west2online.ClashX":@"ClashX",@"com.nssurge.surge-mac":@"Surge",
                              @"com.liguangming.Shadowrocket":@"Shadowrocket",@"net.yanue.V2rayU":@"V2rayU",@"ws.stash.app":@"Stash",
                              @"com.crossutility.quantumult-x":@"Quantumult X",@"com.follow.clash":@"FlClash",@"party.mihomo":@"Mihomo Party",
                              @"io.nekohasekai.sfavt":@"sing-box",@"app.hiddify":@"Hiddify",@"com.loon0x00.LoonLite":@"Loon"};
    NSArray *keywords = @[@"Clash",@"Surge",@"Shadowrocket",@"V2ray",@"Stash",@"Quantumult",@"Mihomo",@"sing-box",@"Hiddify",@"Loon",@"Shadowsocks",@"Trojan",@"NekoRay"];
    NSMutableOrderedSet *found = [NSMutableOrderedSet orderedSet];
    for (NSDictionary *app in apps) {
        NSString *bundle = [app[@"bundle"] isKindOfClass:NSString.class] ? app[@"bundle"] : @"", *name = [app[@"name"] isKindOfClass:NSString.class] ? app[@"name"] : @"";
        NSString *match = nil;
        for (NSString *prefix in bundles) if ([bundle hasPrefix:prefix]) { match = [bundle hasPrefix:@"com.west2online.ClashXPro"] ? @"ClashX Pro" : bundles[prefix]; break; }
        if (!match) for (NSString *word in keywords) if ([name rangeOfString:word options:NSCaseInsensitiveSearch].location != NSNotFound) { match = name; break; }
        if (match) [found addObject:match];
    }
    return found.array;
}
NSDictionary *ProxyEnvironmentReport(NSArray<NSDictionary *> *apps) {
    NSDictionary *proxies = CFBridgingRelease(SCDynamicStoreCopyProxies(NULL)) ?: @{};
    NSString *primary = nil;
    SCDynamicStoreRef store = SCDynamicStoreCreate(NULL, CFSTR("ClaudeLauncherProxyCheck"), NULL, NULL);
    if (store) {
        NSDictionary *ipv4 = CFBridgingRelease(SCDynamicStoreCopyValue(store, CFSTR("State:/Network/Global/IPv4")));
        if ([ipv4[@"PrimaryInterface"] isKindOfClass:NSString.class]) primary = ipv4[@"PrimaryInterface"];
        CFRelease(store);
    }
    BOOL ok = NO;
    NSMutableArray *lines = [NSMutableArray array];
    for (NSDictionary *f in ProxyFindings(proxies, primary)) {
        NSString *kind = f[@"kind"];
        if ([kind isEqualToString:@"TUN"]) { ok = YES; [lines addObject:[NSString stringWithFormat:L(@"✓ TUN／VPN 模式：主网络接口为 %@"),f[@"interface"]]]; continue; }
        if ([kind isEqualToString:@"PAC"]) { ok = YES; [lines addObject:[NSString stringWithFormat:L(@"✓ 自动代理配置（PAC）：%@"),f[@"url"]]]; continue; }
        NSString *address = [NSString stringWithFormat:@"%@:%@",f[@"host"],f[@"port"]];
        if (!IsLoopbackHost(f[@"host"])) { ok = YES; [lines addObject:[NSString stringWithFormat:L(@"✓ %@ 代理：%@（非本机地址，未探测）"),kind,address]]; continue; }
        if (LoopbackPortOpen(f[@"host"], [f[@"port"] integerValue])) { ok = YES; [lines addObject:[NSString stringWithFormat:L(@"✓ %@ 代理：%@（本机端口可连接）"),kind,address]]; }
        else [lines addObject:[NSString stringWithFormat:L(@"✗ %@ 代理：%@（本机端口无响应，代理软件可能未运行）"),kind,address]];
    }
    if (!lines.count) [lines addObject:L(@"✗ 未检测到系统代理或 TUN／VPN 模式")];
    NSArray *clients = KnownProxyApps(apps);
    [lines addObject:clients.count ? [L(@"• 正在运行的代理软件：") stringByAppendingString:[clients componentsJoinedByString:@"、"]] : L(@"• 未发现常见代理软件在运行")];
    return @{@"ok":@(ok), @"lines":lines, @"clients":clients};
}

void TestProxyCheck(void) {
    NSCAssert(ProxyFindings(@{}, @"en0").count == 0, @"no proxy");
    NSArray *f = ProxyFindings(@{@"HTTPEnable":@1,@"HTTPProxy":@"127.0.0.1",@"HTTPPort":@7890,
                                 @"HTTPSEnable":@0,@"HTTPSProxy":@"127.0.0.1",@"HTTPSPort":@7890,
                                 @"SOCKSEnable":@1,@"SOCKSProxy":@"",@"SOCKSPort":@7891}, @"en0");
    NSCAssert(f.count == 1 && [f[0][@"kind"] isEqualToString:@"HTTP"] && [f[0][@"port"] integerValue] == 7890, @"only enabled, complete proxies");
    NSCAssert(ProxyFindings(@{@"HTTPEnable":@1,@"HTTPProxy":@"127.0.0.1",@"HTTPPort":@0}, nil).count == 0, @"invalid port");
    NSCAssert(ProxyFindings(@{@"HTTPEnable":@"x",@"HTTPProxy":@3,@"HTTPPort":@"bad"}, nil).count == 0, @"bad types");
    f = ProxyFindings(@{@"ProxyAutoConfigEnable":@1,@"ProxyAutoConfigURLString":@"http://127.0.0.1:1/pac"}, @"utun4");
    NSCAssert(f.count == 2 && [f[0][@"kind"] isEqualToString:@"PAC"] && [f[1][@"kind"] isEqualToString:@"TUN"], @"pac and tun");
    NSCAssert(ProxyFindings(@{}, @"en0").count == 0 && ProxyFindings(@{}, @"bridge0").count == 0, @"physical interfaces are not tunnels");
    NSCAssert(IsLoopbackHost(@"LocalHost") && IsLoopbackHost(@"127.0.0.1") && IsLoopbackHost(@"::1") && !IsLoopbackHost(@"10.0.0.2"), @"loopback");
    // Port 1 on loopback is not expected to listen; probe must fail fast without leaving the machine.
    NSCAssert(!LoopbackPortOpen(@"127.0.0.1", 1), @"closed loopback port");
    NSArray *apps = @[@{@"bundle":@"io.github.clash-verge-rev.clash-verge-rev",@"name":@"Clash Verge"},@{@"bundle":@"com.west2online.ClashXPro",@"name":@"ClashX Pro"},
                      @{@"bundle":@"com.example.mystash",@"name":@"My Shadowsocks"},@{@"bundle":@"com.apple.Safari",@"name":@"Safari"},@{@"bundle":@3,@"name":NSNull.null}];
    NSArray *clients = KnownProxyApps(apps);
    NSCAssert(clients.count == 3 && [clients[0] isEqual:@"Clash Verge"] && [clients[1] isEqual:@"ClashX Pro"] && [clients[2] isEqual:@"My Shadowsocks"], @"known proxy clients");
    NSCAssert(KnownProxyApps(@[]).count == 0, @"no clients");
    puts("PASS: proxy check findings (HTTP/HTTPS/SOCKS/PAC/TUN), type and port validation, loopback-only probe; no external network");
}
