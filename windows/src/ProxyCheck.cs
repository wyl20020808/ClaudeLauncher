using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Win32;

namespace ClaudeLauncher {
    // 启动新实例前的本机代理环境检测：只读当前用户的系统代理设置与网卡，仅探测本机回环端口，不访问外部网络。
    public sealed class ProxyFinding {
        public string Kind, Host, Detail;
        public int Port;
    }

    public static class ProxyCheck {
        static readonly string[] TunnelKeywords = { "wintun", "tap-windows", "tap adapter", "wireguard", "clash", "mihomo", "sing-box", "tun", "vpn" };

        public static bool IsLoopback(string host) {
            string h = (host ?? "").ToLowerInvariant();
            return h == "localhost" || h.StartsWith("127.") || h == "::1" || h == "[::1]";
        }

        // 解析 WinINET ProxyServer："127.0.0.1:7890" 或 "http=127.0.0.1:7890;https=...;socks=..."。
        public static List<ProxyFinding> Findings(int proxyEnable, string proxyServer, string autoConfigUrl, IEnumerable<string> tunnelAdapters) {
            var findings = new List<ProxyFinding>();
            if (proxyEnable == 1 && !string.IsNullOrWhiteSpace(proxyServer)) {
                foreach (string part in proxyServer.Split(';')) {
                    string entry = part.Trim(), kind = "HTTP/HTTPS";
                    int eq = entry.IndexOf('=');
                    if (eq >= 0) { kind = entry.Substring(0, eq).Trim().ToUpperInvariant(); entry = entry.Substring(eq + 1).Trim(); }
                    if (entry.Contains("://")) entry = entry.Substring(entry.IndexOf("://", StringComparison.Ordinal) + 3);
                    int colon = entry.LastIndexOf(':'), port;
                    if (colon <= 0 || !int.TryParse(entry.Substring(colon + 1), out port) || port < 1 || port > 65535) continue;
                    findings.Add(new ProxyFinding { Kind = kind, Host = entry.Substring(0, colon), Port = port });
                }
            }
            if (!string.IsNullOrWhiteSpace(autoConfigUrl)) findings.Add(new ProxyFinding { Kind = "PAC", Detail = autoConfigUrl.Trim() });
            foreach (string adapter in tunnelAdapters ?? Enumerable.Empty<string>()) findings.Add(new ProxyFinding { Kind = "TUN", Detail = adapter });
            return findings;
        }

        public static bool LooksLikeTunnel(string name, string description) {
            string text = ((name ?? "") + " " + (description ?? "")).ToLowerInvariant();
            return TunnelKeywords.Any(k => text.Contains(k));
        }

        static IEnumerable<string> TunnelAdapters() {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces()) {
                // Teredo/ISATAP 等系统隧道为 Tunnel 类型，名称含 “Tunneling”，排除以免误报。
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback || nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                if (!nic.GetIPProperties().UnicastAddresses.Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork)) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Ppp || LooksLikeTunnel(nic.Name, nic.Description)) yield return nic.Name;
            }
        }

        // 解析为实际配置的回环地址（如 127.0.0.2）；localhost 视为 127.0.0.1；非回环或无法解析返回 null。
        public static System.Net.IPAddress LoopbackAddress(string host) {
            string h = (host ?? "").Trim().Trim('[', ']');
            if (h.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return System.Net.IPAddress.Loopback;
            System.Net.IPAddress address;
            return System.Net.IPAddress.TryParse(h, out address) && System.Net.IPAddress.IsLoopback(address) ? address : null;
        }

        // 只连接回环地址，不离开本机。
        static bool LoopbackPortOpen(string host, int port) {
            var address = LoopbackAddress(host);
            if (address == null) return false;
            try {
                using (var client = new TcpClient(address.AddressFamily)) {
                    var task = client.ConnectAsync(address, port);
                    return task.Wait(800) && client.Connected;
                }
            } catch { return false; }
        }

        // 阻塞（回环探测每项最多约 0.8 秒），在后台线程调用。
        public static bool Report(out List<string> lines) {
            int enable = 0; string server = null, pac = null;
            using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings")) {
                if (key != null) {
                    object e = key.GetValue("ProxyEnable"); if (e is int) enable = (int)e;
                    server = key.GetValue("ProxyServer") as string;
                    pac = key.GetValue("AutoConfigURL") as string;
                }
            }
            List<string> adapters;
            try { adapters = TunnelAdapters().ToList(); } catch (NetworkInformationException) { adapters = new List<string>(); }
            bool ok = false;
            lines = new List<string>();
            foreach (var f in Findings(enable, server, pac, adapters)) {
                if (f.Kind == "TUN") { ok = true; lines.Add("✓ TUN／VPN 网卡：" + f.Detail); continue; }
                if (f.Kind == "PAC") { ok = true; lines.Add("✓ 自动代理配置（PAC）：" + f.Detail); continue; }
                string address = f.Host + ":" + f.Port;
                if (LoopbackAddress(f.Host) == null) { ok = true; lines.Add("✓ " + f.Kind + " 代理：" + address + "（非本机地址，未探测）"); }
                else if (LoopbackPortOpen(f.Host, f.Port)) { ok = true; lines.Add("✓ " + f.Kind + " 代理：" + address + "（本机端口可连接）"); }
                else lines.Add("✗ " + f.Kind + " 代理：" + address + "（本机端口无响应，代理软件可能未运行）");
            }
            if (lines.Count == 0) lines.Add("✗ 未检测到系统代理或 TUN／VPN 网卡");
            return ok;
        }
    }
}
