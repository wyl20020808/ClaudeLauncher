using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ClaudeLauncher;

public static class DeletionTests {
    public static void Run(Action<bool, string> check) {
        string fixture = Path.Combine(Path.GetTempPath(), "claude-delete-test-" + Guid.NewGuid().ToString("N"));
        string roaming = Path.Combine(fixture, "Roaming"), local = Path.Combine(fixture, "Local");
        Directory.CreateDirectory(roaming); Directory.CreateDirectory(local);
        try {
            string[] candidates = ProfileDeletion.Candidates(3, roaming, local);
            check(candidates.Length == 2, "Expected normal and MSIX directories");
            check(ProfileDeletion.Candidates(1, roaming, local).Length == 3, "Default account must include known default locations");
            string first = candidates[0], second = candidates[1];
            string neighbour = Core.Folder(4, roaming);
            Directory.CreateDirectory(first); Directory.CreateDirectory(neighbour);
            File.WriteAllText(Path.Combine(first, "test-login.txt"), "fixture-only");
            File.WriteAllText(Path.Combine(neighbour, "keep.txt"), "other-account-must-stay");
            string[] approved = ProfileDeletion.Prepare(3, roaming, local);
            check(approved.Length == 1 && approved[0] == first, "Incorrect deletion preview");
            check(ProfileDeletion.BlocksDeletion(3, "Claude.exe --type=renderer --user-data-dir=\"" + first + "\"", candidates), "Helper process must block deletion");
            check(ProfileDeletion.BlocksDeletion(3, "Claude.exe --user-data-dir=\"" + second + "\"", candidates), "Virtualized process must block deletion");
            check(!ProfileDeletion.BlocksDeletion(3, "Claude.exe --user-data-dir=\"" + neighbour + "\"", candidates), "Other account must not block");
            check(ProfileDeletion.BlocksDeletion(1, "Claude.exe", ProfileDeletion.Candidates(1, roaming, local)), "Default main process must block");
            check(ProfileDeletion.BlocksDeletion(3, "Claude.exe --type=utility", candidates), "Unassignable helper must block deletion");
            check(!DeletionJournal.IsPending(3, local), "Fresh profile has stale deletion marker");
            DeletionJournal.Begin(3, local);
            check(DeletionJournal.IsPending(3, local), "Pending deletion must survive a new state read");
            check(!DeletionJournal.IsPending(4, local), "Deletion marker leaked into neighboring profile");
            DeletionJournal.Clear(3, local);
            check(!DeletionJournal.IsPending(3, local), "Completed deletion marker not cleared");
            bool rejected = false;
            try { ProfileDeletion.ValidateTarget(3, roaming, local, roaming); } catch (IOException) { rejected = true; }
            check(rejected, "Parent directory must be rejected");
            rejected = false;
            try { ProfileDeletion.ValidateTarget(3, roaming, local, neighbour); } catch (IOException) { rejected = true; }
            check(rejected, "Other profile must be rejected");
            int calls = 0;
            rejected = false;
            try { ProfileDeletion.Execute(3, roaming, local, approved, () => { throw new IOException("running"); }, p => calls++); } catch (IOException) { rejected = true; }
            check(rejected && calls == 0 && Directory.Exists(first), "Running state must prevent all mutation");
            Directory.CreateDirectory(second);
            rejected = false;
            try { ProfileDeletion.Execute(3, roaming, local, approved, () => { }, p => calls++); } catch (IOException) { rejected = true; }
            check(rejected && calls == 0, "Changed preview must require confirmation again");
            approved = ProfileDeletion.Prepare(3, roaming, local);
            rejected = false;
            try { ProfileDeletion.Execute(3, roaming, local, approved, () => { }, p => { throw new IOException("recycle failure"); }); } catch (IOException) { rejected = true; }
            check(rejected && File.Exists(Path.Combine(first, "test-login.txt")), "Recycle failure must preserve data");

            // 只在随机临时夹内创建测试目录联接，不触碰用户真实账号。
            string link = Path.Combine(first, "linked");
            check(Path.GetFullPath(link).StartsWith(Path.GetFullPath(fixture) + Path.DirectorySeparatorChar), "Fixture link escaped test root");
            var info = new ProcessStartInfo("cmd.exe", "/d /c mklink /J \"" + link + "\" \"" + neighbour + "\"");
            info.UseShellExecute = false; info.CreateNoWindow = true; info.RedirectStandardOutput = true; info.RedirectStandardError = true;
            using (var process = Process.Start(info)) {
                process.WaitForExit();
                check(process.ExitCode == 0, "Could not create junction test fixture");
            }
            try {
                rejected = false;
                try { ProfileDeletion.Prepare(3, roaming, local); } catch (IOException) { rejected = true; }
                check(rejected && File.Exists(Path.Combine(neighbour, "keep.txt")), "Junction must block deletion");
            } finally { Directory.Delete(link, false); }

            calls = 0; rejected = false;
            try {
                ProfileDeletion.Execute(3, roaming, local, approved, () => { }, p => {
                    calls++; if (calls == 2) throw new IOException("second directory failed");
                    RecycleBin.Move(p);
                });
            } catch (IOException error) { rejected = error.Message.Contains("1/2"); }
            check(rejected && !Directory.Exists(first) && Directory.Exists(second), "Partial failure must be explicit");
            check(File.ReadAllText(Path.Combine(neighbour, "keep.txt")) == "other-account-must-stay", "Other account changed");
            approved = ProfileDeletion.Prepare(3, roaming, local);
            int completed = ProfileDeletion.Execute(3, roaming, local, approved, () => { }, RecycleBin.Move);
            check(completed == 1 && ProfileDeletion.Prepare(3, roaming, local).Length == 0, "Remaining profile directory was not recycled");
            Directory.CreateDirectory(Core.Folder(3, roaming));
            check(!Directory.EnumerateFileSystemEntries(first).Any(), "Recreated profile must be empty");
            check(File.Exists(Path.Combine(neighbour, "keep.txt")), "Neighbour lost after recreation");

            // 长路径（超过 260 字符）：删除前检查不能因路径过长失败，且能整体移入回收站。
            string deep = Path.Combine(Core.Folder(5, roaming), "Service Worker", new string('s', 120), new string('t', 120), new string('u', 120));
            Directory.CreateDirectory(Storage.Extended(deep));
            File.WriteAllText(Storage.Extended(Path.Combine(deep, "index-dir-entry")), "fixture-only");
            check(Path.Combine(deep, "index-dir-entry").Length > 300, "Long-path fixture too short");
            string[] longApproved = ProfileDeletion.Prepare(5, roaming, local);
            check(longApproved.Length == 1, "Long-path profile not prepared");
            check(ProfileDeletion.Execute(5, roaming, local, longApproved, () => { }, RecycleBin.Move) == 1 && !Directory.Exists(Core.Folder(5, roaming)), "Long-path profile was not recycled");
        } finally {
            string full = Path.GetFullPath(fixture);
            if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("claude-delete-test-"))
                throw new IOException("Refusing cleanup outside test fixture");
            RecycleBin.Move(full);
        }
    }
}
