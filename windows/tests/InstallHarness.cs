using System;
using System.IO;
using ClaudeLauncher;
public static class InstallHarness {
    [STAThread]
    public static int Main(string[] args) {
#if OLD
        return 0;
#else
        if (args.Length == 1 && args[0] == "--apply-update") return Updates.Install(false);
        if (args.Length == 2 && args[0] == "--update-ready") {
            if (File.Exists(Path.Combine(Path.GetDirectoryName(args[1]), "simulate-crash"))) return 42;
            File.WriteAllText(args[1], "fixture-ready"); return 0;
        }
        return 0;
#endif
    }
}
