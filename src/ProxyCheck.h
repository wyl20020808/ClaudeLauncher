#import <Foundation/Foundation.h>

// Local-only proxy environment check shown before launching a new Claude instance.
// Reads system proxy settings and the primary network interface; may probe loopback ports only.
NSArray<NSDictionary *> *ProxyFindings(NSDictionary *proxies, NSString *primaryInterface);
// Names of known proxy clients among running apps ({bundle, name} pairs gathered on the main thread).
NSArray<NSString *> *KnownProxyApps(NSArray<NSDictionary *> *apps);
NSDictionary *ProxyEnvironmentReport(NSArray<NSDictionary *> *apps); // Blocking (loopback probes up to ~0.8 s each); call off the main thread.
void TestProxyCheck(void);
