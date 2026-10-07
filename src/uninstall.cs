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
// ============================================================================
using System;
using System.Windows.Forms;
using System.IO;
using System.Diagnostics;
using System.ComponentModel;
using Microsoft.Win32;

public static class PointPalUninstall {
    const string Product = "WorkBuddy PointPal";
    // The dual launcher installed alongside the pet: a desktop shortcut of
    // this name (only when the option was ticked) plus a spare .vbs and its
    // icon in the install folder (always). Kept in sync with setup.cs by hand
    // because the two exes are compiled separately.
    const string BundleLnkName = "WorkBuddy & PointPal";
    const string BundleFile    = "WorkBuddy & PointPal.vbs";
    const string BundleIcoFile = "WorkBuddy & PointPal.ico";
    // Easter-egg cue installed next to the pet (v1.2.16.zc). Named here so a
    // user-level uninstall does not leave an orphan; without it rmdir below
    // would just silently fail and the folder would survive.
    const string RunKeyPath    = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string UninstKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\WorkBuddyPointPal";

    [STAThread]
    public static int Main(string[] args) {
        bool silent = false, delData = false, elevated = false;
        string machineDir = null, callerExe = null;
        int callerPid = 0;
        foreach (string a in args) {
            string u = a.ToUpperInvariant();
            if (u == "/S") silent = true;
            else if (u == "/ELEVATED") elevated = true;
            else if (u.StartsWith("/DELETEDATA=")) delData = u.EndsWith("=1");
            else if (u.StartsWith("/MACHINEDIR=")) machineDir = a.Substring(12).Trim('"');
            else if (u.StartsWith("/CALLEREXE=")) callerExe = a.Substring(11).Trim('"');
            else if (u.StartsWith("/CALLERPID=")) int.TryParse(a.Substring(11), out callerPid);
        }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

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

            if (delData) {
                if (Directory.Exists(dataDir)) Directory.Delete(dataDir, true);
                // Legacy data dir from the app's old Chinese name. The pet's
                // first-run migration resurrects token/state from it, so a
                // "delete my data" uninstall must remove it too.
                string legacy = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "WorkBuddy" + (char)0x79EF + (char)0x5206 + (char)0x684C + (char)0x5BA0);
                if (Directory.Exists(legacy)) Directory.Delete(legacy, true);
            }

            // Per-user install folder. If WE are its uninstaller, our own exe
            // is deleted on a delay behind our exit; rmdir then only succeeds
            // when nothing else is left (never wipes files the user added).
            if (!string.IsNullOrEmpty(userLoc) && Directory.Exists(userLoc)) {
                string self = Application.ExecutablePath;
                TryDelete(Path.Combine(userLoc, "WorkBuddy PointPal.exe"));
                // Spare dual launcher + its icon: ours, and ours alone, so a
                // "delete the folder" uninstall must not leave them behind.
                // (rmdir below only succeeds once the folder is otherwise
                // empty, so these must go explicitly.)
                TryDelete(Path.Combine(userLoc, BundleFile));
                TryDelete(Path.Combine(userLoc, BundleIcoFile));
                if (SameDir(self, Path.Combine(userLoc, "Uninstall.exe"))) {
                    ScheduleDelete(self, userLoc);
                } else {
                    TryDelete(Path.Combine(userLoc, "Uninstall.exe"));
                    try { Directory.Delete(userLoc, false); } catch { }
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
                try { Directory.Delete(machDir, false); } catch { }
            } else if (callerPid > 0) {
                // Elevated temp copy: wait for the caller to exit (it shows
                // the result box first), then remove its exe and the folder.
                WaitForExit(callerPid, 300);
                TryDelete(callerExe);
                try { Directory.Delete(machDir, false); } catch { }
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
    // calling process has time to exit and release the lock. rmdir only
    // succeeds when the folder is empty, which protects user-added files.
    static void ScheduleDelete(string exePath, string dir) {
        try {
            string args = "/c ping 127.0.0.1 -n 3 > nul & del /f /q \"" + exePath + "\"";
            if (!string.IsNullOrEmpty(dir)) args += " & rmdir \"" + dir + "\"";
            Process.Start(new ProcessStartInfo {
                FileName = "cmd.exe",
                Arguments = args,
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        } catch { }
    }

    static void TryDelete(string path) {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
