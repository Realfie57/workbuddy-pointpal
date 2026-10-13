// ============================================================================
//  WorkBuddy PointPal - uninstaller.
//
//  Removes EVERY installation of WorkBuddy PointPal on this machine in one
//  go - no mode selection anywhere:
//    * the per-user install  (HKCU registration, user shortcuts, user data
//      optionally) - always handled by the invoking (non-elevated) process,
//      so the correct user profile is touched even if UAC would switch to a
//      different admin account;
//    * the per-machine install (HKLM registration, common shortcuts, system
//      folder) - handled by a runas-elevated copy of this exe placed in
//      %TEMP% (so no file-lock conflict with either install dir).
//
//  Elevation ordering: the interactive flow asks UAC BEFORE anything is
//  deleted - declining it leaves the machine untouched (all-or-nothing).
//  The elevated copy reports nothing itself; the original process shows the
//  final result. Silent mode (/S) never pops UAC: if a machine install is
//  present it must already run as administrator, otherwise it fails with a
//  clear message.
//
//  UTF-8 source: MUST be compiled with /codepage:65001 (build_setup.ps1).
//
//  Command line:
//    (none)             ask interactively, remove all installs
//    /S                 silent uninstall, keep user data
//    /DELETEDATA=1      also delete the data folder (credentials & settings)
//    /ELEVATED          internal: we are the runas-relaunched temp copy
//    /MACHINEDIR=<dir>  internal: per-machine install folder to remove
//    /CALLEREXE=<path>  internal: the invoking uninstaller's exe path
//    /CALLERPID=<pid>   internal: wait for this process before deleting
//                       CALLEREXE (it stays locked while the caller runs)
//    /DIRDELTEST=<dir>  hidden self-check for ScheduleDelete. Confined to
//                       %TEMP%; creates throwaway folders, runs the real
//                       deletion line and reports PASS/FAIL. Nothing outside
//                       the temp tree is ever touched.
// ============================================================================
using System;
using System.Text;
using System.Collections.Generic;
using System.Windows.Forms;
using System.IO;
using System.Diagnostics;
using System.ComponentModel;
using Microsoft.Win32;

public static class PointPalUninstall {
    const string Product = "WorkBuddy PointPal";
    // The dual launcher installed alongside the pet: a desktop shortcut of
    // this name (only when the option was ticked) plus its icon in the install
    // folder. Kept in sync with setup.cs by hand because the two exes are
    // compiled separately.
    const string BundleLnkName = "WorkBuddy & PointPal";
    // HISTORICAL name. Builds up to 1.2.16.zc dropped this .vbs next to the pet,
    // and 360 kept quarantining it (virus.vbs.powershell.a / virus.vbs.dropper.d).
    // Current builds run the same logic inside the pet exe, but an uninstall -
    // and an upgrade - must still sweep the old file off machines that came
    // from one of those builds.
    const string BundleFile    = "WorkBuddy & PointPal.vbs";
    const string BundleIcoFile = "WorkBuddy & PointPal.ico";
    const string RunKeyPath    = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string UninstKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\WorkBuddyPointPal";

    // Subfolder of the data dir that holds self-check screenshots. Must match
    // WbPet.ShotsSubdir in wb_pet.cs (the two exes are compiled separately, so
    // the string is duplicated on purpose - change both).
    const string ShotsSubdir = "shots";

    [STAThread]
    public static int Main(string[] args) {
        bool silent = false, delData = false, elevated = false;
        string machineDir = null, callerExe = null, dirDelTest = null;
        int callerPid = 0;
        foreach (string a in args) {
            string u = a.ToUpperInvariant();
            if (u == "/S") silent = true;
            else if (u == "/ELEVATED") elevated = true;
            else if (u.StartsWith("/DELETEDATA=")) delData = u.EndsWith("=1");
            else if (u.StartsWith("/MACHINEDIR=")) machineDir = a.Substring(12).Trim('"');
            else if (u.StartsWith("/CALLEREXE=")) callerExe = a.Substring(11).Trim('"');
            else if (u.StartsWith("/CALLERPID=")) int.TryParse(a.Substring(11), out callerPid);
            else if (u.StartsWith("/DIRDELTEST=")) dirDelTest = a.Substring(12).Trim('"');
        }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // Hidden self-check. Handled FIRST and it never installs or removes
        // anything: it can only ever touch a folder under %TEMP%, so running it
        // cannot harm an existing install.
        if (!string.IsNullOrEmpty(dirDelTest)) return RunDirDelTest(dirDelTest);

        // ------------------------------------------------------------------
        // Elevated temp copy: machine-level cleanup only, no UI. The caller
        // shows the result; failures go to a log file next to the temp exe.
        // ------------------------------------------------------------------
        if (elevated) {
            string errM;
            bool okM = MachineCleanup(machineDir, callerExe, callerPid, out errM);
            if (!okM) {
                try {
                    File.WriteAllText(
                        Path.Combine(Path.GetTempPath(), "pointpal_uninstall_elevated.log"),
                        DateTime.Now.ToString("s") + " " + errM);
                } catch { }
            }
            ScheduleDelete(Application.ExecutablePath, null);   // the temp copy itself
            return okM ? 0 : 1;
        }

        string dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Product);
        string userLoc = FindInstall(Registry.CurrentUser);
        string machLoc = FindInstall(Registry.LocalMachine);

        // Silent mode never elevates: with a machine install present the
        // caller must already be administrator.
        if (silent && machLoc != null && !IsAdmin()) {
            Console.Error.WriteLine(
                "uninstall failed: 检测到为所有用户安装的版本，卸载需要管理员权限 - " +
                "请右键「以管理员身份运行」。");
            return 1;
        }

        if (!silent) {
            string what = "确定要从你的电脑卸载 " + Product + " 吗？";
            if (userLoc != null && machLoc != null)
                what += "\r\n\r\n检测到本机有两处安装（当前用户 + 所有用户），将一并卸载。";
            else if (machLoc != null)
                what += "\r\n\r\n卸载时会请求一次管理员权限（UAC）。";
            DialogResult r = MessageBox.Show(
                what, "卸载 " + Product, MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2);
            if (r != DialogResult.Yes) return 0;
            if (Directory.Exists(dataDir)) {
                DialogResult r2 = MessageBox.Show(
                    "要同时删除你的数据吗？\r\n\r\n" + dataDir + "\r\n" +
                    "（里面是你的登录凭证与尺寸、角色等个性化设置）",
                    "卸载 " + Product, MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2);
                delData = (r2 == DialogResult.Yes);
            }
        }

        // Phase 0: launch the elevated temp copy BEFORE changing anything,
        // so a declined UAC leaves the machine completely untouched.
        // The copy works in the background; the caller-exe (if it lives in
        // the machine folder) is deleted by it once this process exits.
        if (machLoc != null && !IsAdmin()) {
            try {
                string tempCopy = Path.Combine(
                    Path.GetTempPath(), "PointPalUninstall_elevated.exe");
                File.Copy(Application.ExecutablePath, tempCopy, true);
                Process.Start(new ProcessStartInfo {
                    FileName = tempCopy,
                    Arguments = "/ELEVATED /MACHINEDIR=\"" + (machLoc ?? "") +
                                "\" /CALLEREXE=\"" + Application.ExecutablePath +
                                "\" /CALLERPID=" + Process.GetCurrentProcess().Id,
                    Verb = "runas",
                    UseShellExecute = true
                });
            } catch (Win32Exception) {
                // ERROR_CANCELLED: the user declined the UAC prompt.
                if (!silent)
                    MessageBox.Show("已取消管理员授权，未执行卸载。", "卸载 " + Product,
                                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 1;
            } catch (Exception ex) {
                if (!silent)
                    MessageBox.Show("无法请求管理员权限：\r\n" + ex.Message,
                                    "卸载 " + Product,
                                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                else Console.Error.WriteLine("uninstall failed: elevation: " + ex.Message);
                return 1;
            }
        }

        // Phase 1: invoking user's cleanup (always this process, always the
        // correct profile).
        string err1;
        bool ok1 = UserCleanup(delData, dataDir, userLoc, out err1);

        // Phase 2 (already-admin case): machine cleanup right here. The
        // non-admin case is being handled by the elevated copy in parallel.
        bool ok2 = true; string err2 = null;
        if (machLoc != null && IsAdmin())
            ok2 = MachineCleanup(machLoc, Application.ExecutablePath, 0, out err2);

        bool ok = ok1 && ok2;
        if (!silent) {
            if (ok) MessageBox.Show("卸载完成。", "卸载 " + Product,
                                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            else MessageBox.Show("卸载过程中出错：\r\n" + (err1 ?? err2),
                                 "卸载 " + Product,
                                 MessageBoxButtons.OK, MessageBoxIcon.Warning);
        } else if (!ok) {
            Console.Error.WriteLine("uninstall failed: " + (err1 ?? err2));
        }
        return ok ? 0 : 1;
    }

    // ---------------------------------------------------------------- finds

    // InstallLocation of the registered install in the given hive, or null
    // when this hive has no registration. Empty string = registered but the
    // folder is unknown (registry partially damaged).
    static string FindInstall(RegistryKey hive) {
        try {
            using (RegistryKey k = hive.OpenSubKey(UninstKeyPath, false)) {
                if (k == null) return null;
                string loc = k.GetValue("InstallLocation") as string;
                return loc ?? "";
            }
        } catch { return null; }
    }

    static bool IsAdmin() {
        try {
            System.Security.Principal.WindowsPrincipal p =
                new System.Security.Principal.WindowsPrincipal(
                    System.Security.Principal.WindowsIdentity.GetCurrent());
            return p.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        } catch { return false; }
    }

    static bool SameDir(string a, string b) {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        try {
            return string.Equals(
                Path.GetFullPath(a).TrimEnd('\\'),
                Path.GetFullPath(b).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase);
        } catch { return false; }
    }

    static void KillPets() {
        try {
            foreach (Process p in Process.GetProcessesByName("WorkBuddy PointPal")) {
                try { p.Kill(); } catch { }
            }
            System.Threading.Thread.Sleep(800);
        } catch { }
    }

    // ------------------------------------------------------------- phases

    // Everything that belongs to the invoking user's profile: shortcuts,
    // HKCU entries, data (optional) and the per-user install folder.
    static bool UserCleanup(bool delData, string dataDir, string userLoc, out string err) {
        err = null;
        try {
            KillPets();

            TryDelete(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Product + ".lnk"));
            // Dual launcher shortcut + its spare payload. The launcher name
            // carries a real ampersand, so it is spelled out here rather than
            // built from a constant that also serves as a resource key.
            TryDelete(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                BundleLnkName + ".lnk"));
            string sm = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Programs), Product);
            if (Directory.Exists(sm)) Directory.Delete(sm, true);
            TryDelete(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                Product + ".lnk"));

            using (RegistryKey run = Registry.CurrentUser.OpenSubKey(RunKeyPath, true)) {
                if (run != null) { try { run.DeleteValue(Product, false); } catch { } }
            }
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstKeyPath, false); } catch { }

            // Data folder. Two things live under it that must NOT share a fate:
            //   * shots\  - self-check PNGs. Never user data, always ours, and
            //               always removed (see below) so a leftover folder
            //               cannot survive an uninstall.
            //   * the rest - credentials, settings and the unpacked artwork.
            //               Removed only when the user asked for it.
            // Order matters: drop shots\ first (it is removed either way), then
            // the whole folder when delData is set.
            string shotsDir = Path.Combine(dataDir, ShotsSubdir);
            if (Directory.Exists(shotsDir)) {
                try { Directory.Delete(shotsDir, true); } catch { }
            }

            if (delData) {
                if (Directory.Exists(dataDir)) Directory.Delete(dataDir, true);
                // Legacy data dir from the app's old Chinese name. The pet's
                // first-run migration resurrects token/state from it, so a
                // "delete my data" uninstall must remove it too.
                string legacy = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WorkBuddy" + (char)0x79EF + (char)0x5206 + (char)0x684C + (char)0x5BA0);
                if (Directory.Exists(legacy)) Directory.Delete(legacy, true);
                // wb-path.txt is a pointer at the user's WorkBuddy install,
                // written by hand and read at boot. It is normally inside
                // dataDir and already gone, but the pet also accepts a copy
                // next to the exe, so sweep every location it looks in.
                foreach (string p in WbPathCandidates()) TryDelete(p);
            }

            // Per-user install folder. If WE are its uninstaller, our own exe
            // is deleted on a delay behind our exit (see ScheduleDelete, which
            // retries and backstops with rmdir /s so an empty folder is never
            // left behind). Otherwise nothing here is locked and we can drop
            // the folder directly - recursively, so leftovers from an older
            // version cannot keep it alive.
            if (!string.IsNullOrEmpty(userLoc) && Directory.Exists(userLoc)) {
                string self = Application.ExecutablePath;
                TryDelete(Path.Combine(userLoc, "WorkBuddy PointPal.exe"));
                // Legacy .vbs (see the BundleFile note) plus the launcher icon:
                // both are ours, and ours alone, so a "delete the folder"
                // uninstall must not leave them behind.
                TryDelete(Path.Combine(userLoc, BundleFile));
                TryDelete(Path.Combine(userLoc, BundleIcoFile));
                if (SameDir(self, Path.Combine(userLoc, "Uninstall.exe"))) {
                    ScheduleDelete(self, userLoc);
                } else {
                    TryDelete(Path.Combine(userLoc, "Uninstall.exe"));
                    try { Directory.Delete(userLoc, true); } catch { }
                }
            }
            return true;
        } catch (Exception ex) {
            err = ex.Message;
            return false;
        }
    }

    // Everything that belongs to the machine scope: common shortcuts, the
    // HKLM registration and the per-machine install folder. Runs either in
    // the elevated temp copy (callerPid > 0) or inline when the caller is
    // already administrator (callerPid = 0, caller may be our own exe).
    static bool MachineCleanup(string machDir, string callerExe, int callerPid,
                               out string err) {
        err = null;
        try {
            KillPets();

            TryDelete(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                Product + ".lnk"));
            TryDelete(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                BundleLnkName + ".lnk"));
            string sm = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), Product);
            if (Directory.Exists(sm)) Directory.Delete(sm, true);
            TryDelete(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
                Product + ".lnk"));

            try { Registry.LocalMachine.DeleteSubKeyTree(UninstKeyPath, false); } catch { }

            if (string.IsNullOrEmpty(machDir) || !Directory.Exists(machDir))
                return true;

            bool callerInside = SameDir(
                Path.GetDirectoryName(callerExe ?? ""), machDir);
            // Delete everything except a caller exe that lives inside: it is
            // still locked while the caller runs.
            foreach (string f in Directory.GetFiles(machDir)) {
                if (callerInside && SameDir(f, callerExe)) continue;
                TryDelete(f);
            }
            foreach (string d in Directory.GetDirectories(machDir)) {
                try { Directory.Delete(d, true); } catch { }
            }

            if (!callerInside) {
                // Nothing in here is locked by us; drop the whole folder so
                // leftovers from an older version cannot keep it alive.
                try { Directory.Delete(machDir, true); } catch { }
            } else if (callerPid > 0) {
                // Elevated temp copy: wait for the caller to exit (it shows
                // the result box first), then remove its exe and the folder.
                WaitForExit(callerPid, 300);
                TryDelete(callerExe);
                try { Directory.Delete(machDir, true); } catch { }
            } else {
                // We ARE the machine-folder uninstaller (already admin): our
                // token can delete the protected file, on a delay.
                ScheduleDelete(callerExe, machDir);
            }
            return true;
        } catch (Exception ex) {
            err = ex.Message;
            return false;
        }
    }

    static void WaitForExit(int pid, int maxSeconds) {
        for (int i = 0; i < maxSeconds * 2; i++) {
            try {
                Process p = Process.GetProcessById(pid);
                if (p.HasExited) return;
            } catch { return; }   // no such process: caller already gone
            System.Threading.Thread.Sleep(500);
        }
    }

    // Delete a file (and optionally its folder) behind a short delay, so the
    // calling process has time to exit and release the lock on itself.
    // The command line itself is built by ScheduleDeleteLine - one source of
    // truth, shared with the /DIRDELTEST self-check so the shipped line is the
    // tested line.
    static void ScheduleDelete(string exePath, string dir) {
        try {
            Process.Start(new ProcessStartInfo {
                FileName = "cmd.exe",
                Arguments = ScheduleDeleteLine(exePath, dir),
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        } catch { }
    }

    static void TryDelete(string path) {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    // ---------------------------------------------------------- self-check ---
    //
    // /DIRDELTEST=<dir>: run the REAL ScheduleDelete line against throwaway
    // folders instead of re-implementing it in a script. A re-implementation
    // can pass while the shipped line fails - which is exactly how the orphan
    // folder in %LOCALAPPDATA%\Programs survived a green test run.
    //
    // Confined to %TEMP%: anything else is refused, so this switch can never
    // touch an installed copy. The report goes to a FILE because this exe is
    // built /target:winexe, where Console.WriteLine has nowhere to go.
    static int RunDirDelTest(string root) {
        List<string> rep = new List<string>();
        int pass = 0, fail = 0;
        Action<string, bool> chk = delegate(string name, bool ok) {
            rep.Add((ok ? "  PASS  " : "  FAIL  ") + name);
            if (ok) pass++; else fail++;
        };
        string reportPath = Path.Combine(Path.GetTempPath(), "_pointpal_dirdeltest.log");
        string full = null;
        try {
            string temp = Path.GetFullPath(Path.GetTempPath());
            full = Path.GetFullPath(root);
            if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase)) {
                rep.Add("REFUSED: target must live under " + temp);
                rep.Add("          got: " + full);
                File.WriteAllLines(reportPath, rep.ToArray(), new UTF8Encoding(false));
                return 2;
            }
            rep.Add("dirdeltest: " + full);
            rep.Add("cmd line  : " + ScheduleDeleteLine(null, full));
            rep.Add("");

            Action<string> mk = delegate(string name) {
                string d = Path.Combine(full, name);
                try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
                Directory.CreateDirectory(d);
            };
            Func<string, bool> waitGone = delegate(string d) {
                DateTime dl = DateTime.UtcNow.AddSeconds(25);
                while (DateTime.UtcNow < dl) {
                    if (!Directory.Exists(d)) return true;
                    System.Threading.Thread.Sleep(200);
                }
                return false;
            };

            // 1. plain empty folder
            string d1 = Path.Combine(full, "empty");
            mk("empty");
            ScheduleDelete(null, d1);
            chk("empty folder removed", waitGone(d1));

            // 2. leftover file - the historical er.mp3 that made the old
            //    rmdir (no /s) fail silently
            string d2 = Path.Combine(full, "leftover");
            mk("leftover");
            File.WriteAllText(Path.Combine(d2, "er.mp3"), "legacy");
            ScheduleDelete(null, d2);
            chk("folder with a leftover file removed", waitGone(d2));

            // 3. realistic set + subdirectory, with the caller exe still inside
            string d3 = Path.Combine(full, "fullset");
            mk("fullset");
            File.WriteAllText(Path.Combine(d3, "WorkBuddy PointPal.exe"), "pet");
            File.WriteAllText(Path.Combine(d3, "WorkBuddy & PointPal.ico"), "ico");
            Directory.CreateDirectory(Path.Combine(d3, "sub"));
            File.WriteAllText(Path.Combine(d3, "sub", "deep.txt"), "deep");
            ScheduleDelete(Path.Combine(d3, "Uninstall.exe"), d3);
            chk("folder with files + subdir + exe removed", waitGone(d3));

            // 4. THE REGRESSION: a folder another process holds open as its
            //    current directory. Windows refuses to remove it until that
            //    process exits, so a single-shot delete (or 40 attempts crammed
            //    into 40 ms) loses the race and leaves an orphan for good.
            string d4 = Path.Combine(full, "locked");
            mk("locked");
            File.WriteAllText(Path.Combine(d4, "pet.log"), "log");
            Process holder = null;
            try {
                holder = Process.Start(new ProcessStartInfo {
                    FileName = "cmd.exe",
                    Arguments = "/c cd /d \"" + d4 + "\" & ping -n 7 127.0.0.1 >nul",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
            } catch { }
            System.Threading.Thread.Sleep(600);          // let it actually chdir
            DateTime t0 = DateTime.UtcNow;
            ScheduleDelete(null, d4);
            bool gone = waitGone(d4);
            chk("folder held open by another process removed", gone);
            rep.Add("          (took " + (DateTime.UtcNow - t0).TotalSeconds.ToString("0.0") +
                    "s while the holder lived ~6s)");
            try { if (holder != null && !holder.HasExited) holder.Kill(); } catch { }

            rep.Add("");
            rep.Add("dirdeltest: " + pass + " passed, " + fail + " failed");
        } catch (Exception ex) {
            rep.Add("EXCEPTION: " + ex);
            fail++;
        }
        try { File.WriteAllLines(reportPath, rep.ToArray(), new UTF8Encoding(false)); } catch { }
        try { if (full != null && Directory.Exists(full)) Directory.Delete(full, true); } catch { }
        return fail == 0 ? 0 : 1;
    }

    // The exact single line ScheduleDelete hands to cmd /c, exposed so the
    // self-check can print (and be diffed against) what actually ships.
    //
    // Why not a plain "del + rmdir": the process we are waiting on may still
    // hold the folder as its current directory, and rmdir refuses a busy or
    // non-empty folder - silently, because cmd reports nothing and this runs
    // detached. That left the user staring at an empty "WorkBuddy PointPal"
    // folder after an uninstall.
    //
    // So: wait longer (the exe handle is not released the instant the process
    // exits), step OUT of the folder first so our own working directory is not
    // the thing blocking it, then retry the removal. rmdir /s /q is the
    // backstop for leftovers from older versions (e.g. a stray er.mp3 the
    // previous installer used to drop): every file we recognise has already
    // been deleted explicitly by the caller, so whatever remains here is ours
    // too. A file the user put in there themselves would also go - but the
    // install folder is ours by definition, and leaving a ghost folder in
    // Program Files is the worse failure, so recursion wins.
    //
    // Deliberately no goto/labels: the whole thing has to be one command line
    // for cmd /c, and a label cannot live mid-line. A retry loop that repeats
    // the rmdir does the same job.
    //
    // The loop WAITS between attempts. The first version fired all 40 rmdir
    // calls back-to-back - tens of milliseconds end to end - so it was really
    // ONE attempt wearing a counter: if antivirus or Explorer happened to hold
    // a handle at that instant, the folder survived for good (this is exactly
    // how the orphan folder in %LOCALAPPDATA%\Programs was born). Each pass now
    // pauses ~1s, and only while the folder is still there, so a normal delete
    // finishes in a second or two while a transient lock gets a real chance to
    // clear. Worst case (folder never removable) is ~20s of a hidden cmd.exe.
    static string ScheduleDeleteLine(string exePath, string dir) {
        StringBuilder sb = new StringBuilder();
        sb.Append("/c ping 127.0.0.1 -n 4 > nul");
        if (!string.IsNullOrEmpty(exePath))
            sb.Append(" & del /f /q \"" + exePath + "\"");
        if (!string.IsNullOrEmpty(dir)) {
            sb.Append(" & cd /d \"" + Path.GetPathRoot(dir) + "\"");
            // InstallLocation may carry a trailing separator, which makes
            // `if exist "<dir>"` ambiguous. Drop it - but never when the path
            // IS a drive root, where trimming would change the meaning
            // ("C:\" -> "C:" is the drive's current directory).
            string d = dir;
            string root = Path.GetPathRoot(dir);
            if (d.Length > root.Length) d = d.TrimEnd('\\', '/');
            sb.Append(" & for /l %i in (1,1,20) do @(rmdir /s /q \""
                      + d + "\" 2>nul & if exist \"" + d
                      + "\" ping -n 2 127.0.0.1 >nul)");
        }
        return sb.ToString();
    }

    // Every place the pet looks for wb-path.txt (see WbFromOverride in
    // wb_pet.cs): userdata\, the data folder root, and next to the exe. The
    // first two vanish with the data folder, but "next to the exe" belongs to
    // the install folder and has to be named explicitly so a delete-my-data
    // uninstall leaves no pointer behind.
    static string[] WbPathCandidates() {
        string dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Product);
        return new string[] {
            Path.Combine(dataDir, "userdata", "wb-path.txt"),
            Path.Combine(dataDir, "wb-path.txt"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         Product, "wb-path.txt"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                         Product, "wb-path.txt")
        };
    }
}
