// ============================================================================
//  WorkBuddy PointPal - wizard installer (Setup).
//
//  A classic installer: welcome -> choose folder -> choose tasks
//  (desktop / start menu shortcuts, run at logon) -> install -> finish.
//  The install scope is INFERRED from the chosen folder: a folder writable
//  by a standard user means a per-user install (no admin, no UAC); a
//  protected/system folder (e.g. Program Files) means a per-machine
//  install, and UAC is requested only when the Install button is clicked.
//
//  This file is UTF-8 (Chinese UI text as-is) and MUST be compiled with
//  /codepage:65001 - see build_setup.ps1. The pet itself (wb_pet.cs) stays
//  pure ASCII; only the installer sources use real UTF-8.
//
//  Command line:
//    (none)                 run the wizard
//    /S                     silent install with defaults
//    /D=<dir>               install folder (silent or wizard prefill)
//    /DESKTOP=0|1           desktop shortcut      (default 1)
//    /STARTMENU=0|1         start menu shortcuts  (default 1)
//    /AUTORUN=0|1           run at logon          (default 0)
//    /BUNDLE=0|1            "WorkBuddy & PointPal" desktop shortcut (default 0)
//    /LAUNCH=0|1            launch after install  (default: silent 0)
//    /MACHINE               force per-machine install (default: inferred
//                           from the folder; silent mode still needs an
//                           already-elevated process - never pops UAC)
//    /ELEVATED              internal: we are the runas-relaunched instance
//    /SHOTWIZARD=<dir>      dev helper: render every wizard page to PNG
// ============================================================================
using System;
using System.Drawing;
using System.Windows.Forms;
using System.IO;
using System.Reflection;
using System.Diagnostics;
using Microsoft.Win32;

public static class PointPalSetup {
    public const string Product   = "WorkBuddy PointPal";
    // Version is what people READ (Apps & features, file names). VersionCore is
    // the numeric form the assembly metadata and any numeric consumer needs -
    // "zc" is not a number and would make the assembly unloadable.
    public const string VersionCore = "1.2.16.1";
    public const string Version   = "1.2.16.zc";
    public const string Publisher = "Realfie";
    public const string PetExe    = "WorkBuddy PointPal.exe";
    public const string UninstExe = "Uninstall.exe";
    // The dual launcher ("WorkBuddy & PointPal") is always copied next to the
    // pet as a spare; the option below only decides whether it ALSO gets a
    // desktop shortcut. The payload's resource name is deliberately ASCII while
    // the on-disk names carry the real ampersand, so no codepage games are
    // needed to look it up inside the exe.
    public const string BundleRes     = "_payload_bundle_launcher.vbs";
    public const string BundleFile    = "WorkBuddy & PointPal.vbs";
    public const string BundleLnkName = "WorkBuddy & PointPal";
    // The launcher is a .vbs, which cannot carry an icon of its own, so the
    // shortcut must name one explicitly. We ship a WorkBuddy-branded icon
    // (distinct from the pet's own DaFeiYu.ico) and drop it next to the
    // launcher at install time.
    public const string BundleIcoRes  = "WorkBuddy_fake_icon.ico";
    public const string BundleIcoFile = "WorkBuddy & PointPal.ico";
    // The dual-launcher shortcut MUST target an executable, or Windows will
    // not offer "Pin to taskbar" for it. wscript.exe is the script host that
    // runs our .vbs; the .vbs path travels as its argument instead.
    // Resolved from the real System32 at run time (a hardcoded C:\Windows
    // would break on a Windows installed elsewhere).
    public static string WscriptExe {
        get {
            string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
            return Path.Combine(sys, "wscript.exe");
        }
    }
    public const string RunKeyPath    = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string UninstKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\WorkBuddyPointPal";

    public static bool Silent;
    public static bool MachineMode;          // install for all users (needs admin)
    static bool _elevatedReq;                // we are the runas-relaunched instance
    public static string InstallDir;
    public static bool OptDesktop = true, OptStartMenu = true, OptAutoRun, OptLaunch;
    public static bool OptBundle;            // desktop shortcut for the dual launcher
    static string _shotDir;
    static Icon _appIcon;

    [STAThread]
    public static int Main(string[] args) {
        // Test hook: report what the guard would do and exit without
        // touching the machine. Lets the regression suite exercise the
        // registry probe on any host, installed or not.
        foreach (string ta in args) {
            if (!ta.ToUpperInvariant().StartsWith("/CHECKONLY")) continue;
            string loc;
            bool found = FindInstalled(out loc);
            Console.WriteLine("checkonly installed=" + found +
                              " location=" + (loc ?? "(none)") +
                              " uninstallerPresent=" +
                              (!string.IsNullOrEmpty(loc) &&
                               File.Exists(Path.Combine(loc, UninstExe))));
            return found ? 0 : 1;
        }
        bool dirGiven = false;
        foreach (string a in args) {
            string u = a.ToUpperInvariant();
            if (u == "/S") Silent = true;
            else if (u == "/MACHINE") MachineMode = true;
            else if (u == "/ELEVATED") _elevatedReq = true;
            else if (u.StartsWith("/D=")) { InstallDir = a.Substring(3).Trim('"'); dirGiven = true; }
            else if (u.StartsWith("/DESKTOP=")) OptDesktop = u.EndsWith("=1");
            else if (u.StartsWith("/STARTMENU=")) OptStartMenu = u.EndsWith("=1");
            else if (u.StartsWith("/AUTORUN=")) OptAutoRun = u.EndsWith("=1");
            else if (u.StartsWith("/BUNDLE=")) OptBundle = u.EndsWith("=1");
            else if (u.StartsWith("/LAUNCH=")) OptLaunch = u.EndsWith("=1");
            else if (u.StartsWith("/SHOTWIZARD=")) _shotDir = a.Substring(12).Trim('"');
        }
        if (string.IsNullOrEmpty(InstallDir))
            InstallDir = MachineMode ? DefaultMachineDir() : DefaultUserDir();
        try { InstallDir = Path.GetFullPath(InstallDir); } catch { }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        if (_shotDir != null) { ShotWizard(_shotDir); return 0; }

        // DPI awareness only for the real wizard. ShotWizard renders at 96 DPI
        // on purpose: with AutoScaleMode.Dpi a truthful 96-dpi layout check is
        // enough (the real display scales boxes and fonts proportionally),
        // while DrawToBitmap on a DPI-aware process captures a broken hybrid
        // (unscaled boxes, scaled fonts -> text looks clipped in the bitmap).
        try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); }
        catch { try { Native.SetProcessDPIAware(); } catch { } }

        // The runas-relaunched instance does the machine install silently and
        // reports through a MessageBox (it has no console).
        if (_elevatedReq) {
            if (!IsAdmin()) {
                Console.Error.WriteLine("elevation did not grant admin rights");
                return 1;
            }
            Silent = true;
        }

        // Refuse to install a second copy: two PointPals on one machine
        // would fight over the same shortcuts, autostart link and
        // credential. A portable copy carries no registry entry and is
        // therefore never detected here. Cancelling stops the wizard.
        {
            string existing;
            if (FindInstalled(out existing)) {
                if (!ConfirmReplaceInstalled(existing)) {
                    MessageBox.Show(
                        "已取消安装。你可以先手动卸载原有的 " + Product +
                        "，再运行本安装程序。",
                        Product + " 安装向导",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 2;
                }
                if (!UninstallExisting(existing)) return 3;
            }
        }

        // Refuse to install a second copy: two PointPals on one machine
        // would fight over the same shortcuts, autostart link and
        // credential. A portable copy carries no registry entry and is
        // therefore never detected here. Cancelling stops the wizard.
        {
            string existing;
            if (FindInstalled(out existing)) {
                if (!ConfirmReplaceInstalled(existing)) {
                    MessageBox.Show(
                        "已取消安装。你可以先手动卸载原有的 " + Product +
                        "，再运行本安装程序。",
                        Product + " 安装向导",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 2;
                }
                if (!UninstallExisting(existing)) return 3;
            }
        }

        if (Silent) {
            InstallDir = NormalizeInstallDir(InstallDir);
            // Infer the scope from the target folder unless /MACHINE (or the
            // wizard via the runas relaunch) already decided: a folder that
            // a standard user cannot write, or a well-known system location,
            // means a per-machine install.
            string werr; bool accessDenied;
            bool writable = CheckWritable(InstallDir, out werr, out accessDenied);
            if (!MachineMode)
                MachineMode = IsSystemLocation(InstallDir) || (!writable && accessDenied);
            Console.WriteLine("options: dir=" + InstallDir +
                              " desktop=" + OptDesktop + " startmenu=" + OptStartMenu +
                              " autorun=" + OptAutoRun + " bundle=" + OptBundle +
                              " launch=" + OptLaunch + " machine=" + MachineMode);
            if (!writable && !MachineMode)
                return ReportDone(_elevatedReq, false, werr);
            if (MachineMode && !IsAdmin()) {
                Console.Error.WriteLine(
                    "install failed: 安装到 " + InstallDir + " 需要管理员权限 - " +
                    "请右键「以管理员身份运行」，或在管理员命令行中执行。");
                return 1;
            }
            string err;
            bool ok = Install(InstallDir, OptDesktop, OptStartMenu, OptAutoRun, OptBundle,
                              MachineMode, null, out err);
            if (!ok) return ReportDone(_elevatedReq, false, "安装过程中出错：\r\n" + err);
            Console.WriteLine("installed to " + InstallDir);
            // Never launch the pet from an elevated process: it would run as
            // admin (and possibly as a different account). Machine installs
            // are started from the shortcuts by the user themselves.
            if (OptLaunch && !_elevatedReq) TryLaunch();
            if (_elevatedReq)
                return ReportDone(true, true,
                    "安装位置：\r\n" + InstallDir +
                    "\r\n\r\n可从桌面或开始菜单启动 " + Product + "。");
            return 0;
        }

        // Same guard for the normal (wizard) path, before the wizard is
        // even shown, so the user never walks through pages only to be
        // stopped at the end.
        {
            string existing;
            if (FindInstalled(out existing)) {
                using (WizardForm guard = new WizardForm()) {
                    if (!ConfirmReplaceInstalled(existing, guard)) {
                        MessageBox.Show(
                            guard,
                            "已取消安装。你可以先手动卸载原有的 " + Product +
                            "，再运行本安装程序。",
                            Product + " 安装向导",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return 2;
                    }
                }
                if (!UninstallExisting(existing)) return 3;
            }
        }

        // Same guard for the normal (wizard) path, before the wizard is
        // even shown, so the user never walks through pages only to be
        // stopped at the end.
        {
            string existing;
            if (FindInstalled(out existing)) {
                using (WizardForm guard = new WizardForm()) {
                    if (!ConfirmReplaceInstalled(existing, guard)) {
                        MessageBox.Show(
                            guard,
                            "已取消安装。你可以先手动卸载原有的 " + Product +
                            "，再运行本安装程序。",
                            Product + " 安装向导",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return 2;
                    }
                }
                if (!UninstallExisting(existing)) return 3;
            }
        }

        Application.Run(new WizardForm());
        return 0;
    }

    // -------------------------------------------------- already installed

    // InstallLocation of a REGISTERED install, or null when none exists.
    // Both hives are checked: a machine-wide install is equally "already
    // there". Only the registry counts, by design - a portable copy that
    // was merely unzipped somewhere carries no registration and must not
    // block setup (the user asked for portable copies to be ignored).
    public static bool FindInstalled(out string location) {
        location = null;
        RegistryKey[] hives = new RegistryKey[] {
            Registry.CurrentUser, Registry.LocalMachine };
        foreach (RegistryKey hive in hives) {
            try {
                using (RegistryKey k = hive.OpenSubKey(UninstKeyPath, false)) {
                    if (k == null) continue;
                    location = k.GetValue("InstallLocation") as string;
                    if (string.IsNullOrEmpty(location)) location = "";
                    return true;
                }
            } catch { }
        }
        return false;
    }

    // "Install anyway?" prompt. Returns true when the user lets us remove
    // the existing copy and carry on, false to abort the whole setup.
    public static bool ConfirmReplaceInstalled(string location) {
        return ConfirmReplaceInstalled(location, null);
    }

    public static bool ConfirmReplaceInstalled(string location, IWin32Window owner) {
        string where = string.IsNullOrEmpty(location)
            ? "（注册表记录的安装位置无法读取）"
            : location;
        string msg =
            "检测到这台电脑上已经安装了 " + Product + "。\r\n\r\n" +
            "安装位置：\r\n" + where + "\r\n\r\n" +
            "同一台电脑不需要安装多个 " + Product + "。\r\n" +
            "点击「确定」将先卸载原有软件，卸载完成后自动继续安装；\r\n" +
            "点击「取消」将终止本次安装。";
        DialogResult r = owner == null
            ? MessageBox.Show(msg, Product + " 安装向导",
                              MessageBoxButtons.OKCancel, MessageBoxIcon.Warning)
            : MessageBox.Show(owner, msg, Product + " 安装向导",
                              MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
        return r == DialogResult.OK;
    }

    // Runs the registered uninstaller and waits for it to finish, so the
    // old files, shortcuts and registry entry are gone before we start
    // writing. Returns true when nothing is left to overwrite.
    //
    // WHY cmd.exe /c start /wait: the uninstaller is itself a GUI exe, and a
    // GUI child launched directly cannot be relied on for an exit code.
    // `start /wait` makes cmd.exe block until the child exits, and cmd then
    // propagates the child's own exit code - which is what ExitCode reads.
    public static bool UninstallExisting(string location) {
        string exe = string.IsNullOrEmpty(location)
            ? null : Path.Combine(location, UninstExe);
        if (exe == null || !File.Exists(exe)) {
            // Registry points at a folder with no uninstaller (hand-moved).
            // We cannot remove it for the user; say so plainly rather than
            // silently installing a second copy.
            MessageBox.Show(
                "注册表里记录的安装位置找不到卸载程序：\r\n\r\n" +
                (string.IsNullOrEmpty(location) ? "(未知)" : location) +
                "\r\n\r\n" +
                "请手动删除该文件夹，或从「应用和功能」中卸载 " + Product +
                "，然后重新运行本安装程序。",
                Product + " 安装向导",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        string cmd = Environment.GetEnvironmentVariable("COMSPEC");
        if (string.IsNullOrEmpty(cmd))
            cmd = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "cmd.exe");
        try {
            using (Process p = Process.Start(new ProcessStartInfo {
                FileName = cmd,
                Arguments = "/c start \"\" /wait \"" + exe + "\"",
                UseShellExecute = false,
                CreateNoWindow = true
            })) {
                p.WaitForExit();
                // Non-zero = the user cancelled the uninstall, or it
                // failed. Either way the old copy is still there, so
                // installing now would leave two of them - stop instead.
                if (p.ExitCode != 0) {
                    MessageBox.Show(
                        "原有的 " + Product + " 未能卸载完成（可能已取消），" +
                        "安装已终止。\r\n\r\n" +
                        "请卸载完成后再运行本安装程序。",
                        Product + " 安装向导",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
            }
        } catch (Exception ex) {
            MessageBox.Show(
                "无法启动原有的卸载程序：\r\n\r\n" + ex.Message + "\r\n\r\n" +
                "安装已终止。请手动卸载 " + Product + " 后重试。",
                Product + " 安装向导",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        return true;
    }

    static int ReportDone(bool showBox, bool ok, string detail) {
        if (showBox) {
            MessageBox.Show(detail,
                ok ? Product + " 安装向导" : "安装失败",
                MessageBoxButtons.OK,
                ok ? MessageBoxIcon.Information : MessageBoxIcon.Error);
        } else if (!ok) {
            Console.Error.WriteLine("install failed: " + detail);
        }
        return ok ? 0 : 1;
    }

    public static string DefaultUserDir() {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", Product);
    }

    public static string DefaultMachineDir() {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Product);
    }

    public static bool IsAdmin() {
        try {
            System.Security.Principal.WindowsPrincipal p =
                new System.Security.Principal.WindowsPrincipal(
                    System.Security.Principal.WindowsIdentity.GetCurrent());
            return p.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        } catch { return false; }
    }

    // Display DPI factor (1.0 at 96 dpi). Read through a throwaway form
    // because the wizard needs it before its own handle exists. Must be
    // called AFTER the process was made DPI-aware.
    public static float DpiScale() {
        try {
            using (Form f = new Form())
            using (Graphics g = f.CreateGraphics()) {
                return g.DpiX / 96f;
            }
        } catch { return 1f; }
    }

    // ------------------------------------------------------------------ core

    // The effective install folder: when the chosen folder is not already
    // named after the product, install into a product-named subfolder - the
    // standard installer behavior (C:\Program Files -> C:\Program Files\
    // WorkBuddy PointPal), instead of dropping the exe loose into the folder.
    public static string NormalizeInstallDir(string dir) {
        dir = (dir ?? "").Trim().TrimEnd('\\');
        if (dir.Length == 0) return dir;
        string leaf = Path.GetFileName(dir);
        if (string.Equals(leaf, Product, StringComparison.OrdinalIgnoreCase)) return dir;
        return Path.Combine(dir, Product);
    }

    // Probe create + write + delete in the target folder so an unwritable
    // destination (e.g. C:\Program Files without elevation) fails with a clear
    // Chinese explanation BEFORE anything is installed, not mid-install with a
    // raw "access denied".
    public static bool CheckWritable(string dir, out string err, out bool accessDenied) {
        err = null;
        accessDenied = false;
        string probe = null;
        try {
            Directory.CreateDirectory(dir);
            probe = Path.Combine(dir, "~pointpal-write-test.tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        } catch (Exception ex) {
            try { if (probe != null && File.Exists(probe)) File.Delete(probe); } catch { }
            if (ex is UnauthorizedAccessException || ex is System.Security.SecurityException) {
                accessDenied = true;
                err = "没有该文件夹的写入权限：\r\n" + dir;
            } else {
                err = "无法使用该文件夹：\r\n" + ex.Message;
            }
            return false;
        }
    }

    // Well-known system roots. A target under any of them is treated as a
    // per-machine install even when this process happens to be elevated
    // (for an admin the write probe would succeed and hide the distinction).
    public static bool IsSystemLocation(string dir) {
        try {
            string d = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
            string[] roots = new string[] {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                Environment.GetEnvironmentVariable("WINDIR")
            };
            foreach (string r in roots) {
                if (string.IsNullOrEmpty(r)) continue;
                string rr = Path.GetFullPath(r).TrimEnd('\\') + "\\";
                if (d.StartsWith(rr, StringComparison.OrdinalIgnoreCase)) return true;
            }
        } catch { }
        return false;
    }

    // WinForms treats "&" in Button/CheckBox/Label text as a mnemonic marker:
    // it disappears and underlines the following character. Names like
    // "WorkBuddy & PointPal" must therefore be escaped for DISPLAY ONLY - the
    // constant itself keeps the real ampersand, because it is also used to
    // name files on disk, where a doubled "&&" would be wrong.
    public static string Mn(string s) {
        return s == null ? null : s.Replace("&", "&&");
    }

    // Actual on-disk footprint of the install folder: the embedded payloads
    // we extract (pet exe + uninstaller + dual launcher). Shown in the folder
    // page hint. Resolved through the same bare-name matcher ExtractRes uses,
    // because the resource is recorded as "_payload_bundle_launcher.vbs" while
    // the const carries that exact name - a direct lookup would work, but the
    // matcher keeps this honest if the file is ever moved into a subfolder.
    public static string RequiredSpaceText() {
        long bytes = 0;
        foreach (string name in new string[] { PetExe, UninstExe, BundleRes, BundleIcoRes }) {
            try {
                using (Stream s = OpenBareResource(name)) {
                    if (s != null) bytes += s.Length;
                }
            } catch { }
        }
        double mb = bytes / 1048576.0;
        return mb.ToString("0.0") + " MB";
    }

    // Look an embedded resource up by BARE FILE NAME. The compiler names every
    // embedded resource after its file name, so any path the build used is
    // irrelevant at run time.
    static Stream OpenBareResource(string bareName) {
        Assembly asm = Assembly.GetExecutingAssembly();
        foreach (string n in asm.GetManifestResourceNames()) {
            string nn = n;
            int t = nn.LastIndexOf('/'); if (t >= 0) nn = nn.Substring(t + 1);
            t = nn.LastIndexOf('\\'); if (t >= 0) nn = nn.Substring(t + 1);
            if (string.Equals(nn, bareName, StringComparison.OrdinalIgnoreCase))
                return asm.GetManifestResourceStream(n);
        }
        return null;
    }

    // The actual installation. progress may be null (silent mode).
    // machine=true installs for all users: shared desktop / start menu /
    // startup folder and the HKLM uninstall entry (caller must be admin).
    // bundle=true additionally drops a "WorkBuddy & PointPal" desktop shortcut,
    // pointing at the spare launcher copy described below.
    public static bool Install(string dir, bool desktop, bool startMenu, bool autoRun,
                               bool bundle, bool machine,
                               Action<int, string> progress, out string err) {
        err = null;
        try {
            Report(progress, 8, "正在关闭正在运行的挂件…");
            // Stop any running pet first. Overwriting a live exe fails with a
            // sharing violation, and an upgrade is exactly when the pet IS
            // running (it autostarts at logon). The uninstaller always did this;
            // the installer did not, so upgrading while the pet was up could
            // silently leave the old binary in place - which then looked like
            // "the fix did not take".
            KillPets();

            Report(progress, 10, "正在复制程序文件…");
            Directory.CreateDirectory(dir);
            ExtractResRetry(PetExe, Path.Combine(dir, PetExe));
            ExtractResRetry(UninstExe, Path.Combine(dir, UninstExe));
            // Spare copy of the dual launcher, ALWAYS created - ticked or not.
            // Sitting next to the pet it resolves it through the registry just
            // as reliably as the shortcut's explicit %1, so it is a real
            // fallback rather than decoration.
            ExtractResRetry(BundleRes, Path.Combine(dir, BundleFile));
            // The launcher is a .vbs and cannot carry an icon, so its shortcut
            // needs an explicit one. Ship it alongside (always, for the same
            // reason as the launcher itself).
            ExtractResRetry(BundleIcoRes, Path.Combine(dir, BundleIcoFile));
            // NOTE: er.mp3 is deliberately NOT laid down here. The pet reads
            // every asset from its DATA folder (%LOCALAPPDATA%\WorkBuddy
            // PointPal\), which it unpacks from its own embedded copy on each
            // launch (see WbPet.ExtractResources). An earlier version dropped
            // a spare er.mp3 into the install folder on the theory that an
            // installed pet reads from there - it does not, so that file was
            // never opened and just sat in Program Files looking like junk.

            string deskDir = Environment.GetFolderPath(machine
                ? Environment.SpecialFolder.CommonDesktopDirectory
                : Environment.SpecialFolder.DesktopDirectory);
            string progsDir = Environment.GetFolderPath(machine
                ? Environment.SpecialFolder.CommonPrograms
                : Environment.SpecialFolder.Programs);
            string startupDir = Environment.GetFolderPath(machine
                ? Environment.SpecialFolder.CommonStartup
                : Environment.SpecialFolder.Startup);

            string exe = Path.Combine(dir, PetExe);
            if (desktop) {
                Report(progress, 40, "正在创建桌面快捷方式…");
                CreateShortcut(
                    Path.Combine(deskDir, Product + ".lnk"),
                    exe, dir, exe + ",0", Product);
            }
            if (startMenu) {
                Report(progress, 55, "正在创建开始菜单快捷方式…");
                string sm = Path.Combine(progsDir, Product);
                Directory.CreateDirectory(sm);
                CreateShortcut(Path.Combine(sm, Product + ".lnk"), exe, dir, exe + ",0", Product);
                CreateShortcut(Path.Combine(sm, "卸载 " + Product + ".lnk"),
                               Path.Combine(dir, UninstExe), dir, Path.Combine(dir, UninstExe) + ",0",
                               "卸载 " + Product);
            }

            // "WorkBuddy & PointPal": pet + WorkBuddy in one double-click.
            // The shortcut drives the spare copy in the install folder and
            // hands it that folder as an explicit argument, so it can never
            // drift onto a stale or second-hand copy of the pet.
            //
            // IMPORTANT: the target must be wscript.exe, NOT the .vbs itself.
            // Windows only lets you pin a shortcut whose target is an
            // executable; point it straight at a script and the taskbar greys
            // out "Pin to taskbar" and shows a denied cursor on drag. Shipping
            // the script path as an ARGUMENT keeps the same behaviour while
            // restoring pinnability (this matches the launcher the user pinned
            // before the feature moved into the installer).
            if (bundle) {
                Report(progress, 62, Mn("正在创建「" + BundleLnkName + "」快捷方式…"));
                string launcher = Path.Combine(dir, BundleFile);
                // Icon from the shipped .ico, not the pet exe: the launcher is
                // WorkBuddy-flavoured and should not look like the pet itself.
                CreateShortcut(Path.Combine(deskDir, BundleLnkName + ".lnk"),
                               WscriptExe, dir, Path.Combine(dir, BundleIcoFile) + ",0",
                               "同时启动 WorkBuddy 与桌宠",
                               "\"" + launcher + "\" \"" + dir + "\"");
            }

            Report(progress, 70, "正在写入注册表…");
            // Run at logon = a shortcut in the Startup folder (shared folder for
            // machine installs). We used the HKCU Run key first, but PC-manager
            // style guards strip new Run values within a second while leaving
            // Startup links alone (measured on the dev machine 2026-09-29).
            string startupLnk = Path.Combine(startupDir, Product + ".lnk");
            if (autoRun) CreateShortcut(startupLnk, exe, dir, exe + ",0", Product);
            else { try { if (File.Exists(startupLnk)) File.Delete(startupLnk); } catch { } }
            if (!machine) {
                // clean up a Run value a previous build may have written
                using (RegistryKey run = Registry.CurrentUser.OpenSubKey(RunKeyPath, true)) {
                    if (run != null) { try { run.DeleteValue(Product, false); } catch { } }
                }
            }
            RegistryKey hive = machine ? Registry.LocalMachine : Registry.CurrentUser;
            using (RegistryKey un = hive.CreateSubKey(UninstKeyPath)) {
                un.SetValue("DisplayName", Product);
                un.SetValue("DisplayVersion", Version);
                un.SetValue("Publisher", Publisher);
                un.SetValue("DisplayIcon", exe + ",0");
                un.SetValue("InstallLocation", dir);
                un.SetValue("UninstallString", "\"" + Path.Combine(dir, UninstExe) + "\"");
                un.SetValue("NoModify", 1, RegistryValueKind.DWord);
                un.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
            Report(progress, 100, "完成");
            return true;
        } catch (Exception ex) {
            err = ex.Message;
            return false;
        }
    }

    public static void TryLaunch() {
        try {
            Process.Start(new ProcessStartInfo {
                FileName = Path.Combine(InstallDir, PetExe),
                WorkingDirectory = InstallDir,
                UseShellExecute = true
            });
        } catch { }
    }

    static void Report(Action<int, string> cb, int pct, string text) {
        if (cb != null) { cb(pct, text); Application.DoEvents(); }
    }

    // Stop every running pet so its exe can be overwritten. Both copies matter:
    // the per-machine install AND any portable/workspace copy, because the lock
    // one of them holds would otherwise keep us from replacing the other (and a
    // stale binary on disk is exactly what makes "the fix did not take").
    static void KillPets() {
        try {
            foreach (Process p in Process.GetProcessesByName("WorkBuddy PointPal")) {
                try { p.Kill(); } catch { }
            }
            // Give Windows a moment to release the image file handles.
            System.Threading.Thread.Sleep(1200);
        } catch { }
    }

    static void ExtractRes(string bareName, string dst) {
        using (Stream s = OpenBareResource(bareName)) {
            if (s == null) throw new FileNotFoundException("embedded resource missing: " + bareName);
            using (FileStream f = File.Create(dst)) s.CopyTo(f);
        }
    }

    // Wrap ExtractRes with a retry: a just-killed process may still hold the
    // image handle for a moment (and an antivirus scanner can briefly lock the
    // freshly written exe), so give it a few short attempts before giving up.
    static void ExtractResRetry(string bareName, string dst) {
        Exception last = null;
        for (int attempt = 0; attempt < 5; attempt++) {
            try {
                ExtractRes(bareName, dst);
                return;
            } catch (Exception ex) {
                last = ex;
                System.Threading.Thread.Sleep(700);
            }
        }
        throw last;
    }

    public static Icon AppIcon() {
        if (_appIcon != null) return _appIcon;
        try {
            Assembly asm = Assembly.GetExecutingAssembly();
            foreach (string n in asm.GetManifestResourceNames()) {
                if (n.EndsWith("DaFeiYu.ico")) {
                    using (Stream s = asm.GetManifestResourceStream(n)) {
                        _appIcon = new Icon(s);
                    }
                    break;
                }
            }
        } catch { }
        return _appIcon;
    }

    static void CreateShortcut(string lnk, string target, string workDir, string icon, string desc) {
        CreateShortcut(lnk, target, workDir, icon, desc, null);
    }

    // args is optional and last, so every existing call site keeps its shape.
    static void CreateShortcut(string lnk, string target, string workDir, string icon,
                               string desc, string args) {
        Type t = Type.GetTypeFromProgID("WScript.Shell");
        dynamic sh = Activator.CreateInstance(t);
        dynamic sc = sh.CreateShortcut(lnk);
        sc.TargetPath = target;
        sc.WorkingDirectory = workDir;
        sc.IconLocation = icon;
        sc.Description = desc;
        if (!string.IsNullOrEmpty(args)) sc.Arguments = args;
        sc.Save();
    }

    static void ShotWizard(string outDir) {
        Directory.CreateDirectory(outDir);
        // Pass 1 (suffix ""): design space at 96 dpi.
        // Pass 2 (suffix "_150"): simulated 150% display - bounds x1.5 AND
        // fonts x1.5 rendered onto the 96-dpi bitmap, which is pixel-for-
        // pixel what a 144-dpi screen shows (points at 144 dpi = 1.5x
        // pixels). Lets us verify the hi-dpi layout without a hi-dpi screen.
        ShotPass(outDir, 1f, "");
        ShotPass(outDir, 1.5f, "_150");
    }

    static void ShotPass(string outDir, float scale, string suffix) {
        WizardForm f = new WizardForm(scale);
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(-10000, -10000);
        f.Show();
        for (int i = 0; i < f.PageCount; i++) {
            f.ShowPage(i);
            if (i == f.InstallPageIndex) f.FakeInstallProgress();
            Application.DoEvents();
            using (Bitmap b = new Bitmap(f.Width, f.Height)) {
                f.DrawToBitmap(b, new Rectangle(0, 0, f.Width, f.Height));
                b.Save(Path.Combine(outDir, "wizard_page" + i + suffix + ".png"),
                       System.Drawing.Imaging.ImageFormat.Png);
            }
        }
        // Extra render: folder page pointing at Program Files, showing the
        // live "this location needs admin rights" notice.
        f.ShowPage(1);
        f.SetDirForShot(DefaultMachineDir());
        Application.DoEvents();
        using (Bitmap b = new Bitmap(f.Width, f.Height)) {
            f.DrawToBitmap(b, new Rectangle(0, 0, f.Width, f.Height));
            b.Save(Path.Combine(outDir, "wizard_page1_system" + suffix + ".png"),
                   System.Drawing.Imaging.ImageFormat.Png);
        }
        f.Close();
    }
}

// ------------------------------------------------------------------ wizard UI

public class WizardForm : Form {
    Panel[] _pages;
    int _page;
    Button _back, _next, _cancel;
    TextBox _dirBox;
    Label _dirWarn, _dirHint;
    CheckBox _cbDesktop, _cbStartMenu, _cbAutoRun, _cbBundle, _cbLaunch;
    ProgressBar _bar;
    Label _installStatus, _finishBody;
    float _s = 1f;    // DPI scale applied to pixel bounds (fonts are in
                      // points and scale with the physical DPI by themselves)
    float _fs = 1f;   // font multiplier, only for simulated hi-dpi shots
    public int PageCount { get { return _pages.Length; } }
    public int InstallPageIndex { get { return 3; } }

    public WizardForm() : this(0f) { }

    // forceScale > 0 simulates that DPI factor (dev screenshots); 0 = system.
    public WizardForm(float forceScale) {
        if (forceScale > 0f) { _s = forceScale; _fs = forceScale; }
        else _s = PointPalSetup.DpiScale();
        Text = PointPalSetup.Product + " 安装向导";
        Icon ico = PointPalSetup.AppIcon();
        if (ico != null) Icon = ico;
        ClientSize = SZ(520, 400);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        // AutoScaleMode.Dpi is a no-op here: on an already DPI-aware process
        // WinForms assumes our pixel constants were authored for the current
        // DPI and never scales (boxes stay small while point-sized text
        // grows -> the clipped layout seen on 150% displays). We therefore
        // scale every pixel value ourselves via S()/RB()/SZ().
        AutoScaleMode = AutoScaleMode.None;
        Font = F(new Font("Segoe UI", 9F));

        // header
        Panel header = new Panel();
        header.Bounds = RB(0, 0, 520, 64);
        header.BackColor = Color.White;
        Controls.Add(header);
        Panel line = new Panel();
        line.Bounds = RB(0, 63, 520, 1);
        line.BackColor = Color.Gainsboro;
        header.Controls.Add(line);
        if (ico != null) {
            PictureBox pb = new PictureBox();
            pb.Image = ico.ToBitmap();
            pb.SizeMode = PictureBoxSizeMode.Zoom;
            pb.Bounds = RB(16, 16, 32, 32);
            pb.BackColor = Color.Transparent;
            header.Controls.Add(pb);
        }
        Label hTitle = MkLabel(PointPalSetup.Product + " 安装向导",
                               new Font("Segoe UI", 12F, FontStyle.Bold),
                               Color.Black, 58, 19, 440, 26);
        header.Controls.Add(hTitle);

        // footer buttons
        _back = MkBtn("< 上一步", 220, 354, delegate { ShowPage(_page - 1); });
        _next = MkBtn("下一步 >", 312, 354, delegate { Next(); });
        _cancel = MkBtn("取消", 412, 354, delegate { Cancel(); });
        Controls.Add(_back); Controls.Add(_next); Controls.Add(_cancel);

        BuildWelcome();
        BuildFolder();
        BuildOptions();
        BuildInstalling();
        BuildFinish();
        ShowPage(0);
    }

    // ------------------------------------------------------- scaling

    int S(int v) { return (int)Math.Round(v * _s); }
    Rectangle RB(int x, int y, int w, int h) { return new Rectangle(S(x), S(y), S(w), S(h)); }
    Size SZ(int w, int h) { return new Size(S(w), S(h)); }
    Font F(Font f) {
        if (_fs == 1f) return f;
        return new Font(f.FontFamily, f.Size * _fs, f.Style, f.Unit);
    }

    // ------------------------------------------------------------ pages

    void BuildWelcome() {
        Panel p = PagePanel();
        Label t = MkLabel("欢迎安装 " + PointPalSetup.Product,
                          new Font("Segoe UI", 14F, FontStyle.Bold),
                          Color.Black, 28, 28, 460, 32);
        p.Controls.Add(t);
        Label b = MkLabel(
            "一个住在桌面角落的积分小伙伴：\r\n" +
            "角色举着一块平板，实时显示你的 WorkBuddy 积分余额。\r\n\r\n" +
            "本向导将把它安装到你的电脑上。",
            new Font("Segoe UI", 10F), Color.FromArgb(40, 40, 40), 28, 78, 464, 240);
        p.Controls.Add(b);
    }

    void BuildFolder() {
        Panel p = PagePanel();
        p.Controls.Add(MkLabel("选择安装位置", new Font("Segoe UI", 12F, FontStyle.Bold),
                               Color.Black, 28, 24, 400, 28));
        p.Controls.Add(MkLabel("安装到以下文件夹：", Font, Color.FromArgb(40, 40, 40),
                               28, 66, 300, 20));
        _dirBox = new TextBox();
        _dirBox.Bounds = RB(28, 90, 382, 26);
        _dirBox.Text = PointPalSetup.InstallDir;
        _dirBox.TextChanged += delegate { RefreshDirWarn(); };
        p.Controls.Add(_dirBox);
        Button br = new Button();
        br.Text = "浏览…";
        br.Bounds = RB(418, 89, 80, 27);
        br.Click += delegate { Browse(); };
        p.Controls.Add(br);
        _dirHint = MkLabel("", new Font("Segoe UI", 8.5F), Color.DimGray, 28, 124, 464, 34);
        p.Controls.Add(_dirHint);
        _dirWarn = MkLabel("", new Font("Segoe UI", 8.5F), Color.DarkOrange, 28, 162, 464, 20);
        p.Controls.Add(_dirWarn);
    }

    void BuildOptions() {
        Panel p = PagePanel();
        p.Controls.Add(MkLabel("选择附加任务", new Font("Segoe UI", 12F, FontStyle.Bold),
                               Color.Black, 28, 24, 400, 28));
        _cbDesktop   = MkCheck("在桌面创建快捷方式", 28, 66, true);
        _cbStartMenu = MkCheck("在开始菜单创建快捷方式（含卸载入口）", 28, 96, true);
        _cbAutoRun   = MkCheck("开机时自动运行", 28, 126, PointPalSetup.OptAutoRun);
        p.Controls.Add(_cbDesktop); p.Controls.Add(_cbStartMenu); p.Controls.Add(_cbAutoRun);
        p.Controls.Add(MkLabel("登录 Windows 后自动启动；安装后也可随时在托盘菜单退出。",
                               new Font("Segoe UI", 8.5F), Color.DimGray, 48, 152, 440, 34));

        // Deliberately set apart from the three shortcut/startup tasks above:
        // a separator plus a blank line marks it as a different kind of extra,
        // and it still ends well above the bottom buttons.
        Panel rule = new Panel();
        rule.Bounds = RB(28, 190, 464, 1);
        rule.BackColor = Color.Gainsboro;
        p.Controls.Add(rule);

        _cbBundle = MkCheck(PointPalSetup.Mn("在桌面创建「" + PointPalSetup.BundleLnkName + "」快捷方式"),
                            28, 204, PointPalSetup.OptBundle);
        p.Controls.Add(_cbBundle);
        p.Controls.Add(MkLabel("一次双击同时启动 WorkBuddy 与桌宠；该启动器无论是否勾选，都会在安装目录留一份备用。",
                               new Font("Segoe UI", 8.5F), Color.DimGray, 48, 228, 440, 34));
    }

    void BuildInstalling() {
        Panel p = PagePanel();
        p.Controls.Add(MkLabel("正在安装", new Font("Segoe UI", 12F, FontStyle.Bold),
                               Color.Black, 28, 24, 400, 28));
        _bar = new ProgressBar();
        _bar.Bounds = RB(28, 66, 464, 24);
        _bar.Minimum = 0; _bar.Maximum = 100;
        p.Controls.Add(_bar);
        _installStatus = MkLabel("准备中…", Font, Color.FromArgb(40, 40, 40), 28, 100, 460, 22);
        p.Controls.Add(_installStatus);
    }

    void BuildFinish() {
        Panel p = PagePanel();
        p.Controls.Add(MkLabel("安装完成", new Font("Segoe UI", 14F, FontStyle.Bold),
                               Color.Black, 28, 28, 400, 32));
        // Height kept clear of the launch checkbox below (y=196): a body that
        // grew past it used to overprint the checkbox. The credential hint
        // that once caused that lives on the credential dialog instead, and
        // the box is sized so a 2-line body cannot reach the checkbox.
        _finishBody = MkLabel("", new Font("Segoe UI", 10F), Color.FromArgb(40, 40, 40),
                              28, 76, 464, 112);
        p.Controls.Add(_finishBody);
        _cbLaunch = MkCheck("立即运行 " + PointPalSetup.Product, 28, 196, true);
        p.Controls.Add(_cbLaunch);
    }

    // ------------------------------------------------------------ navigation

    public void ShowPage(int i) {
        _page = Math.Max(0, Math.Min(_pages.Length - 1, i));
        for (int k = 0; k < _pages.Length; k++) _pages[k].Visible = (k == _page);
        _back.Enabled = _page >= 1 && _page <= 2;
        _cancel.Enabled = _page != InstallPageIndex;
        if (_page == _pages.Length - 1) { _next.Text = "完成"; _back.Enabled = false; }
        else _next.Text = "下一步 >";
        _next.Enabled = _page != InstallPageIndex;
        if (_page == 1) RefreshDirWarn();
    }

    void Next() {
        if (_page == 1) {
            string dir = _dirBox.Text.Trim();
            if (dir.Length == 0) { Warn("请填写安装文件夹。"); return; }
            try { dir = Path.GetFullPath(dir); }
            catch { Warn("文件夹路径无效。"); return; }
            dir = PointPalSetup.NormalizeInstallDir(dir);
            _dirBox.Text = dir;                 // show the effective folder
            RefreshDirWarn();
            // Infer the install scope from the target folder: a folder that a
            // standard user cannot write (or a well-known system location)
            // means a per-machine install - UAC is requested when the user
            // clicks Install, not here.
            string werr; bool accessDenied;
            bool writable = PointPalSetup.CheckWritable(dir, out werr, out accessDenied);
            if (!writable && !accessDenied) {
                MessageBox.Show(this, werr, "无法安装到此位置",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            PointPalSetup.MachineMode =
                !writable || PointPalSetup.IsSystemLocation(dir);
            PointPalSetup.InstallDir = dir;
            ShowPage(2);
            return;
        }
        if (_page == 2) { RunInstall(); return; }
        if (_page == _pages.Length - 1) {
            if (_cbLaunch.Checked) PointPalSetup.TryLaunch();
            Close();
            return;
        }
        ShowPage(_page + 1);
    }

    void RunInstall() {
        PointPalSetup.OptDesktop = _cbDesktop.Checked;
        PointPalSetup.OptStartMenu = _cbStartMenu.Checked;
        PointPalSetup.OptAutoRun = _cbAutoRun.Checked;
        PointPalSetup.OptBundle = _cbBundle.Checked;

        // Per-machine install without admin rights: relaunch this Setup
        // elevated. The elevated copy installs silently and reports the
        // result through its own MessageBox (it has no wizard), so this
        // instance just closes.
        if (PointPalSetup.MachineMode && !PointPalSetup.IsAdmin()) {
            string relArgs = "/ELEVATED /MACHINE /D=\"" + PointPalSetup.InstallDir + "\"" +
                             " /DESKTOP=" + (PointPalSetup.OptDesktop ? "1" : "0") +
                             " /STARTMENU=" + (PointPalSetup.OptStartMenu ? "1" : "0") +
                             " /AUTORUN=" + (PointPalSetup.OptAutoRun ? "1" : "0") +
                             " /BUNDLE=" + (PointPalSetup.OptBundle ? "1" : "0");
            try {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo {
                        FileName = Application.ExecutablePath,
                        Arguments = relArgs,
                        Verb = "runas",
                        UseShellExecute = true
                    });
                Close();
            } catch (System.ComponentModel.Win32Exception) {
                // ERROR_CANCELLED: the user declined the UAC prompt.
                MessageBox.Show(this, "已取消管理员授权，未执行安装。",
                                "安装已取消", MessageBoxButtons.OK, MessageBoxIcon.Information);
                ShowPage(2);
            }
            return;
        }

        ShowPage(InstallPageIndex);
        string err;
        bool ok = PointPalSetup.Install(
            PointPalSetup.InstallDir,
            PointPalSetup.OptDesktop, PointPalSetup.OptStartMenu, PointPalSetup.OptAutoRun,
            PointPalSetup.OptBundle,
            PointPalSetup.MachineMode,   // machine + already admin = direct install
            delegate (int pct, string text) {
                _bar.Value = Math.Max(_bar.Minimum, Math.Min(_bar.Maximum, pct));
                _installStatus.Text = text;
            },
            out err);
        if (!ok) {
            MessageBox.Show(this, "安装过程中出错：\r\n" + err,
                            "安装失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            ShowPage(2);
            return;
        }
        if (PointPalSetup.MachineMode) {
            // This process is elevated; the pet must not inherit admin rights.
            _cbLaunch.Checked = false;
            _cbLaunch.Enabled = false;
        }
        _finishBody.Text =
            PointPalSetup.Product + " 已安装到：\r\n" + PointPalSetup.InstallDir +
            "\r\n\r\n首次运行时会提示你粘贴 WorkBuddy 登录凭证（在浏览器里复制一段 cURL 即可）。";
        ShowPage(_pages.Length - 1);
    }

    public void FakeInstallProgress() {
        _bar.Value = 60;
        _installStatus.Text = "正在复制程序文件…";
        _finishBody.Text = PointPalSetup.Product + " 已安装到：\r\n" + PointPalSetup.InstallDir;
    }

    public void SetDirForShot(string dir) {
        _dirBox.Text = dir;   // fires TextChanged -> RefreshDirWarn
    }

    void Cancel() {
        if (MessageBox.Show(this, "确定要取消安装吗？", Text,
                            MessageBoxButtons.YesNo, MessageBoxIcon.Question,
                            MessageBoxDefaultButton.Button2) == DialogResult.Yes)
            Close();
    }

    void Browse() {
        using (FolderBrowserDialog d = new FolderBrowserDialog()) {
            d.Description = "选择安装文件夹";
            d.ShowNewFolderButton = true;
            try { d.SelectedPath = _dirBox.Text; } catch { }
            if (d.ShowDialog(this) == DialogResult.OK) _dirBox.Text = d.SelectedPath;
        }
        RefreshDirWarn();
    }

    void RefreshDirWarn() {
        try {
            string dir = PointPalSetup.NormalizeInstallDir(_dirBox.Text);
            _dirHint.Text = dir.Length > 0
                ? "将安装到：" + dir + "，预计需要 " + PointPalSetup.RequiredSpaceText() + " 空间"
                : "";
            if (dir.Length > 0 && PointPalSetup.IsSystemLocation(dir)) {
                _dirWarn.Text = "此位置需要管理员权限，点击「安装」时会弹出授权确认。";
            } else {
                string exe = Path.Combine(dir, PointPalSetup.PetExe);
                _dirWarn.Text = File.Exists(exe) ? "检测到该文件夹已有旧版本，将直接覆盖升级。" : "";
            }
        } catch { _dirHint.Text = ""; _dirWarn.Text = ""; }
    }

    void Warn(string msg) {
        MessageBox.Show(this, msg, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    // ------------------------------------------------------------ controls

    Panel PagePanel() {
        Panel p = new Panel();
        p.Bounds = RB(0, 64, 520, 282);
        p.Visible = false;
        Controls.Add(p);
        if (_pages == null) _pages = new Panel[0];
        Array.Resize(ref _pages, _pages.Length + 1);
        _pages[_pages.Length - 1] = p;
        return p;
    }

    Label MkLabel(string text, Font font, Color color, int x, int y, int w, int h) {
        Label l = new Label();
        l.Text = text; l.Font = F(font); l.ForeColor = color;
        l.Bounds = RB(x, y, w, h);
        l.AutoSize = false;
        return l;
    }

    CheckBox MkCheck(string text, int x, int y, bool on) {
        CheckBox c = new CheckBox();
        c.Text = text; c.Checked = on;
        c.Bounds = RB(x, y, 440, 24);
        return c;
    }

    Button MkBtn(string text, int x, int y, EventHandler onClick) {
        Button b = new Button();
        b.Text = text;
        b.Bounds = RB(x, y, 92, 30);
        b.Click += onClick;
        return b;
    }
}

internal static class Native {
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool SetProcessDPIAware();
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
}
