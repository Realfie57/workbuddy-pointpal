using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using System.Reflection;

// ------------------------------------------------------------------ utils ---

// Raw ARGB access. Unlock as soon as possible: GDI+ must never draw into a
// bitmap while it is still locked.
public sealed class Buf : IDisposable {
    public readonly Bitmap Bmp;
    public readonly int W, H, Stride;
    public readonly byte[] P;
    readonly BitmapData _d;

    public Buf(Bitmap b) {
        Bmp = b; W = b.Width; H = b.Height;
        _d = b.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        Stride = _d.Stride;
        P = new byte[Stride * H];
        Marshal.Copy(_d.Scan0, P, 0, P.Length);
    }
    public void Flush() { Marshal.Copy(P, 0, _d.Scan0, P.Length); }
    public void Dispose() { Bmp.UnlockBits(_d); }
}

public static class Cs {
    // src over dst with straight (non-premultiplied) alpha
    public static void Blend(byte[] d, int i, int r, int g, int b, double a) {
        if (a <= 0) return;
        if (a > 1) a = 1;
        double da = d[i + 3] / 255.0;
        double oa = a + da * (1 - a);
        if (oa <= 0.0001) { d[i] = 0; d[i+1] = 0; d[i+2] = 0; d[i+3] = 0; return; }
        d[i]     = (byte)Math.Round((b * a + d[i]     * da * (1 - a)) / oa);
        d[i + 1] = (byte)Math.Round((g * a + d[i + 1] * da * (1 - a)) / oa);
        d[i + 2] = (byte)Math.Round((r * a + d[i + 2] * da * (1 - a)) / oa);
        d[i + 3] = (byte)Math.Round(oa * 255);
    }
}

internal static class Native {
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("user32.dll")] public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst,
        ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hDC);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObj);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hDC);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr hObj);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("gdi32.dll")] public static extern int GetDeviceCaps(IntPtr hdc, int index);

    // Cross-process "wake the running pet" plumbing. The layered pet window is
    // WS_EX_TOOLWINDOW + WS_EX_NOACTIVATE, so we cannot just SetForegroundWindow
    // it; instead we post a private message it listens for and let it react.
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    // HWND_BROADCAST: the pet has no reliable window title to FindWindow on
    // (SetWindowText is never called), so a broadcast is the dependable route.
    public static readonly IntPtr HWND_BROADCAST = new IntPtr(0xFFFF);
    // WM_APP + 1: an arbitrary app-private message, safe from collisions with
    // the system range and with SetWindowLong-style messages.
    public const int WM_PET_WAKE = 0x8000 + 1;

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    public const int ULW_ALPHA = 0x02;
    public const byte AC_SRC_OVER = 0x00;
    public const byte AC_SRC_ALPHA = 0x01;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WM_NCHITTEST = 0x0084;
    // Broadcast to every top-level window when a display is added, removed, or
    // reconfigured. Used only to rescue the widget if it lands off-screen.
    public const int WM_DISPLAYCHANGE = 0x007E;
    public const int HTTRANSPARENT = -1;
    public const int HTCLIENT = 1;
}

// ----------------------------------------------------------------- one pet ---

// A desktop pet is a per-user singleton: two copies would overlap, double-poll
// the API and double-consume the balance. The vet launcher ("鍚姩 WorkBuddy +
// PointPal.vbs") checks WMI before starting one, but that guard only covers
// launches made THROUGH the launcher - double-clicking the app's own desktop
// shortcut goes straight to the exe and produced a second pet.
//
// So the real guard lives here, in the process itself. A session-scoped named
// mutex ("Local\" = one per logon session, so two Windows users may each keep
// their own pet) is acquired before any window exists. The second process finds
// the mutex taken, pokes the running pet awake, and exits without ever
// building a window. Nothing is shown to the user: the point is that "start
// the pet" is idempotent, not that it nags.
//
// Test modes (--selftest/--shot/--credtest/--credcheck/--soundtest/--soundprobe/
// --soundsoak/--soundab/--simchain) must NOT take the mutex: the regression suite
// runs while the user's real pet is up, and refusing to start would silently turn
// those runs into no-ops.
sealed class SingleInstance : IDisposable {
    const string MutexName = "Local\\WorkBuddy.PointPal.SingleInstance";
    Mutex _mutex;
    bool _owned;

    // Returns true when this process is the one and only pet. Returns false
    // when another pet already holds the slot (this process should exit).
    public bool Acquire() {
        try {
            bool created;
            _mutex = new Mutex(true, MutexName, out created);
            _owned = created;
            return created;
        } catch (AbandonedMutexException) {
            // The previous owner died without releasing; we inherit the slot.
            _owned = true;
            return true;
        } catch {
            // If the mutex cannot be created at all, fail OPEN: a pet that
            // refuses to start is worse than a rare duplicate.
            _owned = false;
            return true;
        }
    }

    // Ask the already-running pet to surface itself. Best-effort; failures are
    // harmless because the caller exits either way.
    //
    // The pet window carries a fixed title ("WorkBuddy PointPal", set in the
    // constructor) but is WS_EX_TOOLWINDOW + WS_EX_NOACTIVATE, so it must not
    // be foregrounded directly. FindWindow targets it by title; the broadcast
    // stays as a fallback for the (unlikely) case where the title lookup fails.
    public static void WakeRunningPet() {
        try {
            IntPtr h = Native.FindWindow(null, "WorkBuddy PointPal");
            if (h != IntPtr.Zero)
                Native.PostMessage(h, (uint)Native.WM_PET_WAKE, IntPtr.Zero, IntPtr.Zero);
            else
                Native.PostMessage(Native.HWND_BROADCAST, (uint)Native.WM_PET_WAKE,
                                   IntPtr.Zero, IntPtr.Zero);
        } catch { }
    }

    public void Dispose() {
        try {
            if (_mutex != null) {
                if (_owned) { try { _mutex.ReleaseMutex(); } catch { } }
                _mutex.Close();
            }
        } catch { }
    }
}

// ------------------------------------------------------------- animations ---

sealed class Hit {
    public double T;
    public const double Dur = 0.55;
    public bool Done { get { return T >= Dur; } }
    public double Pulse {
        get {
            if (T < 0.20) return 1.0;                       // solid flash on impact
            double e = Math.Max(0, 1 - (T - 0.20) / (Dur - 0.20));
            return Math.Sin((T - 0.20) * 26) * 0.55 * e * e;
        }
    }
}

sealed class Floater {
    public double T, Dur = 1.05, Jitter;
    public int X, Y;
    public string Text;
    public bool Done { get { return T >= Dur; } }
}

// A selectable character: artwork file plus the geometry measured on it.
// Qx/Qy are the tablet screen corners (TL,TR,BR,BL) in sprite pixels;
// Hx/Hy is where the damage numbers take off from. The three mac sprites
// share one template, so their quads match to within a pixel.
sealed class CharSpec {
    public string Id, Name, File;
    public double[] Qx, Qy;
    public double Hx, Hy;
    public CharSpec(string id, string name, string file,
                    double[] qx, double[] qy, double hx, double hy) {
        Id = id; Name = name; File = file; Qx = qx; Qy = qy; Hx = hx; Hy = hy;
    }
}

// Small modal input dialog. ShowDialog pumps its own loop, so it works on top
// of the layered main window.
//
// TWO LAYOUTS. The original was a single-line box, which silently destroyed
// pasted credentials: a WinForms TextBox with AcceptsReturn = false (the
// default) keeps only the text BEFORE THE FIRST NEWLINE when you paste, so an
// 18-line "Copy as cURL" collapsed to its first line and the parser never saw
// the Cookie. multiline = true gives the credential prompt a real editor that
// accepts newlines, wraps, scrolls, and starts big enough to see the payload.
sealed class InputDialog : Form {
    TextBox _box;
    Label _lab;
    Label _hint;          // the "unit"/hint strip under the editor
    Button _guide;
    // Shown on the button that opens the credential guide. Only this dialog
    // uses it, so it lives here rather than in WbPet's string block.
    const string GUIDE_LABEL = "\u5982\u4F55\u83B7\u53D6\u51ED\u8BC1...";

    public InputDialog(string title, string prompt, string unit, string initial) {
        Build(title, prompt, unit, initial, false, null, null);
    }
    public InputDialog(string title, string prompt, string unit, string initial, bool multiline) {
        Build(title, prompt, unit, initial, multiline, null, null);
    }
    // guideBody != null adds a "how do I get one" button beside OK / Cancel.
    public InputDialog(string title, string prompt, string unit, string initial,
                       bool multiline, string guideBody, string guideTitle) {
        Build(title, prompt, unit, initial, multiline, guideBody, guideTitle);
    }

    // The bounds below are authored for 96 dpi. This process is DPI aware, so
    // text is drawn at its physical size while programmatic bounds stay in raw
    // pixels: on a 150% display the prompt label clips its top line (reported
    // 2026-10-01 from a first-time install). Scaling by the monitor's real DPI
    // keeps every dialog correct at any scale factor.
    static int P(int v) {
        // WBPET_UI_SCALE pins the factor, so --toksheet can render the scaled
        // layout on a 96-dpi machine and prove it still fits.
        float s = 0f;
        string forced = Environment.GetEnvironmentVariable("WBPET_UI_SCALE");
        if (!string.IsNullOrEmpty(forced))
            float.TryParse(forced, NumberStyles.Float, CultureInfo.InvariantCulture, out s);
        if (s <= 0f) {
            s = 1f;
            try { using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) s = g.DpiY / 96f; }
            catch { }
        }
        if (s <= 0f) s = 1f;
        return (int)Math.Round(v * s);
    }
    // AboutDialog and BusyNotice are separate windows but must scale by exactly
    // the same rule, so they call through to this one.
    internal static int Ps(int v) { return P(v); }
    static Rectangle B(int x, int y, int w, int h) {
        return new Rectangle(P(x), P(y), P(w), P(h));
    }

    void Build(string title, string prompt, string unit, string initial,
               bool multiline, string guideBody, string guideTitle) {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false; MinimizeBox = false;
        TopMost = true;
        // Layout is scaled by hand from the real DPI (see P/B below). Turning
        // the framework's own font-based scaling off keeps the two from
        // stacking and making the dialog twice as large.
        AutoScaleMode = AutoScaleMode.None;
        try { if (WbPet.AppIcon != null) Icon = WbPet.AppIcon; } catch { }

        Label lab = new Label();
        lab.Text = prompt;
        // AutoSize must be OFF. A WinForms Label defaults to AutoSize = true,
        // which IGNORES the height in Bounds and grows the control to fit the
        // text - so every "the label is N px tall" assumption in the layout
        // below was fiction, and the hint silently ran under the button row
        // (caught by --toksheet on 2026-10-02: the second sentence was clipped
        // and the first overprinted OK / Cancel). With AutoSize off the Bounds
        // given here are authoritative and the measurement in LayoutReport is
        // a real check.
        lab.AutoSize = false;
        _lab = lab;
        Controls.Add(lab);

        _box = new TextBox();
        _box.Text = initial;
        if (multiline) {
            // accepts newlines, wraps long lines, scrolls when it overflows
            _box.Multiline = true;
            _box.AcceptsReturn = true;
            _box.WordWrap = true;
            _box.ScrollBars = ScrollBars.Vertical;
        }

        Label u = new Label();
        u.Text = unit;
        u.AutoSize = false;      // same reason as `lab` above
        _hint = u;

        Button ok = new Button();
        ok.Text = "OK";
        ok.DialogResult = DialogResult.OK;

        Button cancel = new Button();
        cancel.Text = "Cancel";
        cancel.DialogResult = DialogResult.Cancel;

        // Optional "how do I get this?" button, bottom-left, on the same row
        // as OK / Cancel. Opens the credential guide that used to live in the
        // tray menu, so the help sits where the confusion actually happens.
        Button guide = null;
        if (multiline && !string.IsNullOrEmpty(guideBody)) {
            guide = new Button();
            guide.Text = GUIDE_LABEL;
            _guide = guide;
            string body = guideBody, head = guideTitle;
            guide.Click += delegate {
                MessageBox.Show(this, body, head,
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
        }

        if (multiline) {
            // Prompt gets room for two lines at any DPI; the editor, hint and
            // button row are laid out below it without overlap.
            //
            // The hint row is MEASURED, not guessed. It is the one label whose
            // clipping reads as "that's all there is", so a hand-picked height
            // is how the expiry sentence got dropped at 100% and the visible
            // text overprinted OK / Cancel (--toksheet render, 2026-10-02).
            // Measuring keeps the hint and the buttons from ever meeting.
            int labH  = P(44);
            int boxY  = P(12) + labH + P(8);
            int boxH  = P(212);
            int hintY = boxY + boxH + P(8);
            int hintW = P(442);
            // MeasureText returns PHYSICAL pixels (the font is already scaled
            // by DPI), so this height must NOT go through P() again.
            Size hintTxt = TextRenderer.MeasureText(
                unit, u.Font, new Size(hintW, int.MaxValue),
                TextFormatFlags.WordBreak);
            int hintH = Math.Max(P(20), hintTxt.Height + P(6));
            int rowY  = hintY + hintH + P(12);
            int btnH  = P(28);
            ClientSize = new Size(P(470), rowY + btnH + P(14));
            lab.Bounds = new Rectangle(P(14), P(12), P(442), labH);
            _box.Bounds = new Rectangle(P(14), boxY, P(442), boxH);
            u.Bounds = new Rectangle(P(14), hintY, hintW, hintH);
            if (guide != null)
                guide.Bounds = new Rectangle(P(14), rowY, P(150), btnH);
            ok.Bounds = new Rectangle(P(290), rowY, P(78), btnH);
            cancel.Bounds = new Rectangle(P(378), rowY, P(78), btnH);
        } else {
            ClientSize = new Size(P(330), P(148));
            lab.Bounds = B(14, 12, 300, 42);
            _box.Bounds = B(14, 56, 230, 26);
            u.Bounds = B(250, 59, 70, 22);
            ok.Bounds = B(155, 98, 75, 28);
            cancel.Bounds = B(240, 98, 75, 28);
        }
        Controls.Add(_box);
        Controls.Add(u);
        if (guide != null) Controls.Add(guide);
        Controls.Add(ok);
        Controls.Add(cancel);

        AcceptButton = ok; CancelButton = cancel;
        _box.SelectAll();
        _box.Focus();
    }
    // Trim only the OUTER whitespace: a credential legitimately contains inner
    // blank lines and trailing backslashes, and the parser needs them intact.
    public string Value { get { return _box.Text == null ? "" : _box.Text.Trim(); } }

    // Whether the prompt actually fits its label at the current scale, and that
    // the editor sits clear below it. --toksheet asserts this, so a DPI
    // regression fails loudly instead of silently clipping the top line (the
    // bug a first-time user caught on 2026-10-01).
    public string LayoutReport() {
        Size need = TextRenderer.MeasureText(
            _lab.Text, _lab.Font, new Size(_lab.Width, int.MaxValue),
            TextFormatFlags.WordBreak);
        // The hint is the one label whose clipping is invisible in a casual
        // look (a missing second sentence reads as "that's all there is"), so
        // assert it explicitly: measure the wrapped text at the label's real
        // width and compare against the height the layout gave it.
        Size hintWant = TextRenderer.MeasureText(
            _hint.Text, _hint.Font, new Size(_hint.Width, int.MaxValue),
            TextFormatFlags.WordBreak);
        float scale = P(100) / 100f;
        return "scale=" + scale.ToString("0.##", CultureInfo.InvariantCulture) +
               " promptNeed=" + need.Height + " labelHas=" + _lab.Height +
               " promptFits=" + (need.Height <= _lab.Height) +
               " editorTop=" + _box.Top + " promptBottom=" + _lab.Bottom +
               " editorClear=" + (_box.Top >= _lab.Bottom) +
               " guideShown=" + (_guide != null) +
               " hintNeed=" + hintWant.Height + " hintHas=" + _hint.Height +
               " hintFits=" + (hintWant.Height <= _hint.Height) +
               " client=" + ClientSize.Width + "x" + ClientSize.Height;
    }
}

// ----------------------------------------------------------- about box -----
//
// Replaces the old plain MessageBox so the body text and a "妫€鏌ユ洿鏂? button
// can share one window. Laid out with the same hand-scaled P() helper as
// InputDialog, and with AutoScaleMode off for the same reason: this process is
// DPI aware, so letting the framework scale on top of our own scaling would
// double every dimension.
// ------------------------------------------------------- update found -----
//
// "A newer version is available". Deliberately NOT a MessageBox: a MessageBox
// cannot relabel its buttons, so the choice reads as "confirm / decline the
// update" while the only effect of confirming is opening a browser tab.
// Reported 2026-10-02 by the first external user, who could not tell what OK
// would do without pressing it.
//
// Laid out with the same hand-scaled P() helper as InputDialog / AboutDialog,
// and AutoScaleMode off for the same reason: this process is DPI aware, so the
// framework's own font scaling would stack on top of ours and double every
// dimension. Text heights are MEASURED, never guessed - a hand-picked number
// is how the note line starts clipping, and a clipped note reads as "that's
// all there is".
sealed class UpdateFoundDialog : Form {
    public bool OpenRequested;

    readonly Label _body;
    readonly Label _note;
    readonly Button _open;
    readonly Button _later;

    public UpdateFoundDialog(string title, string body, string note,
                             string openLabel, string laterLabel) {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        // CenterScreen, NOT CenterParent: the owner is the pet itself, a small
        // always-on-top form parked in a screen corner.
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false; MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
        try { if (WbPet.AppIcon != null) Icon = WbPet.AppIcon; } catch { }

        int w = InputDialog.Ps(430);
        int pad = InputDialog.Ps(16);
        int textW = w - pad * 2;

        _body = new Label();
        _body.Text = body;
        _body.AutoSize = false;               // Bounds height is authoritative
        _body.Font = new Font("Microsoft YaHei", 9.75F);

        _note = new Label();
        _note.Text = note;
        _note.AutoSize = false;
        _note.ForeColor = SystemColors.GrayText;

        // MeasureText returns PHYSICAL pixels (the font is already DPI scaled),
        // so these heights must NOT go through Ps() a second time.
        Size bodyNeed = TextRenderer.MeasureText(
            body, _body.Font, new Size(textW, int.MaxValue), TextFormatFlags.WordBreak);
        Size noteNeed = TextRenderer.MeasureText(
            note, _note.Font, new Size(textW, int.MaxValue), TextFormatFlags.WordBreak);
        int bodyH = bodyNeed.Height + InputDialog.Ps(4);
        int noteH = noteNeed.Height + InputDialog.Ps(4);

        int noteY = pad + bodyH + InputDialog.Ps(10);
        int btnH  = InputDialog.Ps(30);
        int rowY  = noteY + noteH + InputDialog.Ps(16);
        ClientSize = new Size(w, rowY + btnH + pad);
        _body.Bounds = new Rectangle(pad, pad, textW, bodyH);
        _note.Bounds = new Rectangle(pad, noteY, textW, noteH);

        // Primary action on the right - the Windows convention.
        int laterW = InputDialog.Ps(104);
        int openW  = InputDialog.Ps(150);
        int gap    = InputDialog.Ps(8);
        int openX  = w - pad - openW;
        int laterX = openX - gap - laterW;

        _open = new Button();
        _open.Text = openLabel;
        _open.Bounds = new Rectangle(openX, rowY, openW, btnH);
        _open.Click += delegate {
            OpenRequested = true;
            DialogResult = DialogResult.OK;
            Close();
        };

        _later = new Button();
        _later.Text = laterLabel;
        _later.Bounds = new Rectangle(laterX, rowY, laterW, btnH);
        _later.Click += delegate {
            DialogResult = DialogResult.Cancel;
            Close();
        };

        Controls.Add(_body);
        Controls.Add(_note);
        Controls.Add(_open);
        Controls.Add(_later);
        AcceptButton = _open;    // Enter = open the download page
        CancelButton = _later;   // Esc   = later
    }

    // What --updfound asserts. The captions ARE the point of this class, and
    // the note is the one line whose clipping is invisible in a screenshot (a
    // dropped sentence just reads as "that's all there is").
    public string LayoutReport() {
        Size bodyNeed = TextRenderer.MeasureText(
            _body.Text, _body.Font, new Size(_body.Width, int.MaxValue), TextFormatFlags.WordBreak);
        Size noteNeed = TextRenderer.MeasureText(
            _note.Text, _note.Font, new Size(_note.Width, int.MaxValue), TextFormatFlags.WordBreak);
        return "bodyFits=" + (bodyNeed.Height <= _body.Height) +
               " noteFits=" + (noteNeed.Height <= _note.Height) +
               " buttonsClear=" + (_open.Top >= _note.Bottom) +
               " laterLeftOfOpen=" + (_later.Right <= _open.Left) +
               " open=\"" + _open.Text + "\"" +
               " later=\"" + _later.Text + "\"" +
               " client=" + ClientSize.Width + "x" + ClientSize.Height;
    }
}

sealed class AboutDialog : Form {
    readonly string _updateLabel;
    public bool UpdateRequested;

    public AboutDialog(string body, string title, string updateLabel) {
        _updateLabel = updateLabel;
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        // CenterScreen, NOT CenterParent: the owner is the pet itself, which is a
        // small always-on-top form parked in a screen corner - so CenterParent
        // puts this dialog in that same corner instead of in front of the user.
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false; MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
        try { if (WbPet.AppIcon != null) Icon = WbPet.AppIcon; } catch { }

        Label lab = new Label();
        lab.Text = body;
        lab.AutoSize = false;                 // Bounds height is authoritative
        lab.Font = new Font("Microsoft YaHei", 9.75F);

        int w = InputDialog.Ps(430);
        int pad = InputDialog.Ps(16);
        int textW = w - pad * 2;

        // The body is a fixed block of text, so its height is measured rather
        // than guessed - a hand-picked number is how labels start clipping.
        // MeasureText returns PHYSICAL pixels, so no second P() on it.
        Size need = TextRenderer.MeasureText(
            body, lab.Font, new Size(textW, int.MaxValue), TextFormatFlags.WordBreak);
        int textH = need.Height + InputDialog.Ps(6);

        int btnH = InputDialog.Ps(30);
        int rowY = pad + textH + InputDialog.Ps(16);
        int clientH = rowY + btnH + pad;

        ClientSize = new Size(w, clientH);
        lab.Bounds = new Rectangle(pad, pad, textW, textH);
        Controls.Add(lab);

        Button upd = new Button();
        upd.Text = updateLabel;
        upd.Bounds = new Rectangle(pad, rowY, InputDialog.Ps(112), btnH);
        upd.Click += delegate {
            UpdateRequested = true;
            // Close About FIRST, so the "checking..." notice is not parented
            // over a dialog that is about to disappear (asked for 2026-10-02).
            DialogResult = DialogResult.OK;
            Close();
        };
        Controls.Add(upd);

        Button ok = new Button();
        ok.Text = "OK";
        ok.DialogResult = DialogResult.Cancel;
        ok.Bounds = new Rectangle(w - pad - InputDialog.Ps(88), rowY, InputDialog.Ps(88), btnH);
        Controls.Add(ok);

        AcceptButton = ok;
        CancelButton = ok;
    }
}

// ------------------------------------------------------------- settings -----
//
// One window for everything that is a SETTING rather than an action. The
// right-click menu used to carry all of these inline and had grown to eleven
// entries (2026-10-13). Tabs keep it short: display / charging / credential.
//
// Layout follows the same rules as InputDialog and AboutDialog, for the same
// reasons: this process is DPI aware, so bounds are scaled by hand through
// InputDialog.Ps() with AutoScaleMode off, and the window is centred on the
// SCREEN (never CenterParent - the owner is the pet, a small top-most form
// parked in a corner).
//
// Commit model (revised 2026-10-13 r2): the pet is the single source of truth,
// but the dialog now buffers edits and only pushes them on 应用 / 确认. 取消
// restores every touched setting to the value the dialog opened with. Without
// this, a Cancel button would be a lie - the old build applied each change the
// instant the widget changed.
//
// 应用 and 确认 differ only in whether the window closes: both flush the same
// pending set, so a user who applies and then cancels still keeps what they
// applied (the baseline moves with each apply).
sealed class SettingsDialog : Form {
    // Applies a new value; the pet re-renders and persists.
    public Action<double> SetCm;
    public Action<double> SetStep;
    public Action<double> SetFloatMult;
    public Action<string> SetLabel;
    public Action<string> SetChar;
    public Action<bool>   SetSound;
    public Action<bool>   SetRememberScreen;
    public Action         AskToken;          // opens the credential dialog

    // ---- credential page wiring (1.2.19.5) --------------------------------
    // Probe returns 0 = accepted, 1 = rejected (401 / nothing stored),
    // 2 = could not tell. It runs on a worker thread and must be safe to call
    // repeatedly; the pet owns it because the credential lives there.
    public Func<int>     CredentialProbe;
    // Re-reads the "set / not set" block text after a credential was pasted,
    // so the page cannot keep claiming "not set" right after the user set one.
    public Func<string>  CredBlockSource;

    readonly Label  _credBody   = new Label();
    readonly Label  _credStatus = new Label();
    readonly Button _credSet    = new Button();
    readonly Button _credTest   = new Button();
    System.Windows.Forms.Timer _credTimer;      // re-tests while the tab is open
    int  _credBusy;                             // one probe in flight at a time
    volatile bool _credClosed;                  // window gone: stop everything
    int  _credState;                            // one of the Cred* constants

    // States of the credential line. 0 is "no verdict yet" (nothing printed).
    internal const int CredNone = 0, CredTesting = 1, CredOk = 2, CredBad = 3, CredNet = 4;
    // How often the open credential page re-tests. Matches the widget's own
    // poll cadence, so the page and the tablet never disagree for long.
    const int CRED_POLL_MS = 5000;
    // The "testing..." line must stay readable even when the gateway answers in
    // 30 ms - otherwise the user cannot tell whether anything was tested at all.
    const int CRED_MIN_TEST_MS = 500;
    // Height of the status line, in unscaled units. One line is enough at the
    // dialog's width; AutoSize stays off so the reserved space is fixed.
    const int CredStatusH = 22;

    // Values as they were when the dialog opened - what 取消 restores.
    readonly double _cm0, _step0, _floatMult0;
    readonly string _label0, _char0;
    readonly bool _sound0, _remember0;

    // Pending values, edited by the widgets and flushed on apply/ok.
    double _cm, _step, _floatMult;
    string _label, _charId;
    bool _sound, _remember;

    // Live-updated widgets.
    readonly NumPicker _sizePick;
    readonly NumPicker _stepPick;
    readonly NumPicker _floatPick;
    readonly ComboBox _charBox = new ComboBox();
    readonly CheckBox _soundBox = new CheckBox();
    readonly TextBox  _labelBox = new TextBox();
    readonly RadioButton _startRemember = new RadioButton();
    readonly RadioButton _startPrimary  = new RadioButton();
    readonly Label    _posHint = new Label();
    readonly Label    _startupHint = new Label();
    TabControl _tabs;                 // kept so --setsheet can walk the tabs

    // Test hooks: --setsheet renders one tab at a time and reports which one it
    // captured, so a screenshot can be tied to a tab without OCR.
    internal void SelectTabForTest(int i) {
        if (_tabs != null && i >= 0 && i < _tabs.TabPages.Count) _tabs.SelectedIndex = i;
    }
    internal string TabTitleForTest() {
        return _tabs == null ? "(none)" : _tabs.SelectedTab.Text;
    }

    // Reports whether every control actually fits inside its tab's client
    // area. A clipped row is invisible in a casual look and reads as "that
    // option does not exist", so --setsheet asserts it.
    internal string LayoutReport() {
        if (_tabs == null) return "no tabs";
        int worst = 0; string worstName = "";
        foreach (TabPage p in _tabs.TabPages) {
            int avail = p.ClientSize.Height;
            foreach (Control c in p.Controls) {
                int bottom = c.Bottom;
                if (bottom > avail && bottom - avail > worst) {
                    worst = bottom - avail; worstName = p.Text + "/" + c.Text;
                }
            }
        }
        bool radiosVisible =
            _startRemember.Parent != null && _startPrimary.Parent != null &&
            _startRemember.Bottom <= _startRemember.Parent.ClientSize.Height &&
            _startPrimary.Bottom  <= _startPrimary.Parent.ClientSize.Height;
        return "tabs=" + _tabs.TabPages.Count +
               " tabClientH=" + _tabs.TabPages[0].ClientSize.Height +
               " overflowWorst=" + worst + (worst > 0 ? " @" + worstName : "") +
               " startupRadiosVisible=" + radiosVisible;
    }

    // Per-row bounds for the three numeric rows. Size is in centimetres, the
    // floater multiplier in "x", the step in whatever the balance unit is.
    // Kept in step with the WbPet-side clamps and state.ini reader.
    internal const double SIZE_MAX_CM = 20.0;
    internal const double STEP_MAX    = 999.99;
    internal const double FLOAT_MAX   = 10.0;
    // Floors differ too (asked 2026-10-13): a hair under half a centimetre
    // renders the pet as a speck, so size and the floater multiplier stop at
    // 0.5; the step is a money amount, where 0.01 is the sensible floor.
    internal const double SIZE_MIN_CM = 0.5;
    internal const double FLOAT_MIN   = 0.5;
    internal const double STEP_MIN    = 0.01;
    // Smallest of the three - for assertions that just need "some valid value".
    internal const double NUM_MIN     = 0.01;

    // Charging/display share the numeric presets that used to live in the menu.
    static readonly double[] Sizes  = new double[] { 2.0, 3.0, 4.0, 6.0, 8.0 };
    // EGG_STEPS stay out of this list on purpose - see WbPet.EGG_STEPS. The
    // --eggsound self-check asserts that against this very array.
    internal static readonly double[] Steps  = new double[] { 0.1, 1, 5, 10 };
    static readonly double[] Floats = new double[] { 1, 2, 3, 4 };

    // Read-only views for --pickertest, which rebuilds pickers from the same
    // arrays the dialog uses so the assertions cannot drift from the real UI.
    internal static double[] SizePresets  { get { return Sizes; } }
    internal static double[] FloatPresets { get { return Floats; } }

    public SettingsDialog(string title,
                          double cm, double step, double floatMult,
                          string label, string charId, bool sound, bool rememberScreen,
                          CharSpec[] chars, string credBlockText) {
        _cm0 = cm; _step0 = step; _floatMult0 = floatMult;
        _label0 = label; _char0 = charId; _sound0 = sound; _remember0 = rememberScreen;
        _cm = cm; _step = step; _floatMult = floatMult;
        _label = label; _charId = charId; _sound = sound; _remember = rememberScreen;

        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false; MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
        try { if (WbPet.AppIcon != null) Icon = WbPet.AppIcon; } catch { }

        int w = InputDialog.Ps(DlgW);
        int pad = InputDialog.Ps(DlgPad);
        int tabH = InputDialog.Ps(DisplayTabHeight());

        // Per-row limits (asked 2026-10-13): size tops out at 20cm, the
        // floater multiplier at 10x, the deduction step keeps 999.99. All
        // three share the 0.01 floor.
        _sizePick  = new NumPicker(WbPet.SettingsCustomHint, SIZE_MIN_CM, SIZE_MAX_CM, _cm);
        _stepPick  = new NumPicker(WbPet.SettingsCustomHint, STEP_MIN, STEP_MAX, _step);
        _floatPick = new NumPicker(WbPet.SettingsCustomHint, FLOAT_MIN, FLOAT_MAX, _floatMult);
        _sizePick.SetPresets(Sizes, WbPet.CmFor);
        _stepPick.SetPresets(Steps, WbPet.FmtFor);
        _floatPick.SetPresets(Floats, delegate (double v) { return WbPet.FmtFor(v) + " \u500D"; });
        _sizePick.PresetChosen  += delegate (double v) { _cm = v; };
        _stepPick.PresetChosen  += delegate (double v) { _step = v; };
        _floatPick.PresetChosen += delegate (double v) { _floatMult = v; };
        _sizePick.CustomEntered  += delegate (double v) { _cm = v; };
        _stepPick.CustomEntered  += delegate (double v) { _step = v; };
        _floatPick.CustomEntered += delegate (double v) { _floatMult = v; };

        TabControl tabs = new TabControl();
        _tabs = tabs;
        tabs.Font = new Font("Microsoft YaHei", 9.75F);
        tabs.Bounds = new Rectangle(pad, pad, w - pad * 2, tabH);
        tabs.TabPages.Add(BuildDisplayTab(chars));
        tabs.TabPages.Add(BuildChargeTab());
        tabs.TabPages.Add(BuildCredTab(credBlockText));
        Controls.Add(tabs);

        int rowY = tabs.Bottom + InputDialog.Ps(12);
        int btnH = InputDialog.Ps(30);
        int btnW = InputDialog.Ps(76);
        int gap  = InputDialog.Ps(8);

        // Right to left: Apply, Cancel, OK - the Windows convention puts the
        // commit button on the right. 确认 and 应用 both flush; 取消 reverts.
        Button ok = new Button();
        ok.Text = WbPet.SettingsBtnOk;
        ok.DialogResult = DialogResult.OK;
        ok.Bounds = new Rectangle(w - pad - btnW, rowY, btnW, btnH);
        ok.Click += delegate { ApplyPending(); };

        Button cancel = new Button();
        cancel.Text = WbPet.SettingsBtnCancel;
        cancel.DialogResult = DialogResult.Cancel;
        cancel.Bounds = new Rectangle(w - pad - btnW * 2 - gap, rowY, btnW, btnH);
        cancel.Click += delegate { RevertPending(); };

        Button apply = new Button();
        apply.Text = WbPet.SettingsBtnApply;
        apply.Bounds = new Rectangle(w - pad - btnW * 3 - gap * 2, rowY, btnW, btnH);
        apply.Click += delegate { ApplyPending(); };

        Controls.Add(ok);
        Controls.Add(cancel);
        Controls.Add(apply);
        ClientSize = new Size(w, rowY + btnH + pad);

        // Widgets only touch the PENDING copies. Nothing reaches the pet until
        // 应用 / 确认, which is what makes 取消 truthful.
        _charBox.SelectedIndexChanged += delegate {
            if (_charBox.SelectedIndex >= 0) _charId = chars[_charBox.SelectedIndex].Id;
        };
        _soundBox.CheckedChanged += delegate { _sound = _soundBox.Checked; };
        _labelBox.Validated += delegate { CommitLabelEdit(); };
        _labelBox.KeyDown += delegate (object s, KeyEventArgs e) {
            if (e.KeyCode == Keys.Enter) { CommitLabelEdit(); e.SuppressKeyPress = true; }
        };
        _startRemember.CheckedChanged += delegate { if (_startRemember.Checked) _remember = true;  };
        _startPrimary.CheckedChanged  += delegate { if (_startPrimary.Checked)  _remember = false; };

        // The credential page tests itself the moment it becomes visible and
        // keeps doing so until the window closes (asked 2026-10-18). Both
        // directions matter: leaving the tab stops the probing, coming back
        // starts it again - only closing the window is final.
        tabs.SelectedIndexChanged += delegate { SyncCredPolling(); };
        FormClosed += delegate { StopCredTimerFinal(); };

        AcceptButton = ok;
        CancelButton = cancel;
    }

    // ---- credential page behaviour ----------------------------------------

    // The tab is fixed at index 2 (display / charging / credential).
    internal const int CredTabIndex = 2;

    void SyncCredPolling() {
        if (_tabs == null) return;
        if (_tabs.SelectedIndex == CredTabIndex) StartCredPolling();
        else StopCredTimer();
    }

    void StartCredPolling() {
        if (_credClosed) return;
        if (_credTimer == null) {
            _credTimer = new System.Windows.Forms.Timer();
            _credTimer.Interval = CRED_POLL_MS;
            _credTimer.Tick += delegate { TestCredentialNow(); };
        }
        if (!_credTimer.Enabled) _credTimer.Start();
        TestCredentialNow();
    }

    // Stops the timer but keeps it alive: switching tabs away and back must
    // resume the checks.
    void StopCredTimer() {
        if (_credTimer != null) _credTimer.Stop();
    }

    // Called once the window is gone - nothing may touch the controls after it.
    void StopCredTimerFinal() {
        _credClosed = true;
        if (_credTimer != null) { _credTimer.Stop(); _credTimer.Dispose(); _credTimer = null; }
    }

    // Runs one probe on the thread pool. The label is only repainted for the
    // FIRST probe (so it reads "testing...") - later re-tests update the label
    // only when the verdict changes, otherwise the line would blink between
    // "valid!" and "testing..." every five seconds.
    internal void TestCredentialNow() { TestCredentialCore(false); }

    // Explicit user action (the button, or just after pasting a credential):
    // always show the "testing..." line, even when the previous verdict is
    // still on screen - the user asked for this and expects to see something.
    internal void TestCredentialUserRequested() { TestCredentialCore(true); }

    void TestCredentialCore(bool announce) {
        if (_credClosed) return;
        if (CredentialProbe == null) return;
        if (Interlocked.CompareExchange(ref _credBusy, 1, 0) != 0) return;
        bool showTesting = announce || _credState == CredNone;
        if (showTesting) SetCredState(CredTesting);
        ThreadPool.QueueUserWorkItem(delegate {
            int started = Environment.TickCount;
            int r = 2;
            try { r = CredentialProbe(); } catch { r = 2; }
            // Floor the visible duration of the "testing..." line. Only when we
            // actually put it up - a silent background re-test has nothing to
            // read. Sleeping here, on a worker, keeps the UI thread free.
            if (showTesting) {
                int left = CRED_MIN_TEST_MS - (Environment.TickCount - started);
                if (left > 0) System.Threading.Thread.Sleep(left);
            }
            int state = r == 0 ? CredOk : (r == 1 ? CredBad : CredNet);
            try {
                if (!_credClosed && !IsDisposed && IsHandleCreated) {
                    BeginInvoke((MethodInvoker)delegate {
                        Interlocked.Exchange(ref _credBusy, 0);
                        if (_credClosed || IsDisposed) return;
                        SetCredState(state);
                    });
                    return;
                }
            } catch { }
            Interlocked.Exchange(ref _credBusy, 0);
        });
    }

    void SetCredState(int s) {
        _credState = s;
        _credStatus.Text = CredStatusText(s);
        _credStatus.ForeColor = s == CredOk ? Color.FromArgb(0, 122, 61)
                              : s == CredBad ? Color.FromArgb(190, 40, 40)
                              : SystemColors.GrayText;
        _credStatus.Refresh();
    }

    // Single source of truth for what the line says - --parsetest asserts that
    // every verdict maps to a distinct, non-empty string.
    internal static string CredStatusText(int s) {
        if (s == CredOk)      return WbPet.CredTextOk;
        if (s == CredBad)     return WbPet.CredTextBad;
        if (s == CredNet)     return WbPet.CredTextNet;
        if (s == CredTesting) return WbPet.CredTextTesting;
        return "";
    }

    internal string CredStatusForTest { get { return _credStatus.Text; } }
    internal int    CredStateForTest  { get { return _credState; } }
    internal bool   CredPollingForTest {
        get { return _credTimer != null && _credTimer.Enabled; }
    }

    void CommitLabelEdit() {
        string v = _labelBox.Text.Trim();
        if (v.Length > 0) _label = v;
    }

    // Pushes every pending value through to the pet. Called by 应用 and 确认.
    // The baseline is refreshed too, so a later 取消 keeps what was applied.
    void ApplyPending() {
        CommitLabelEdit();
        if (SetCm != null) SetCm(_cm);
        if (SetStep != null) SetStep(_step);
        if (SetFloatMult != null) SetFloatMult(_floatMult);
        if (SetLabel != null) SetLabel(_label);
        if (SetChar != null) SetChar(_charId);
        if (SetSound != null) SetSound(_sound);
        if (SetRememberScreen != null) SetRememberScreen(_remember);
    }

    // Puts every setting back to the value the dialog opened with, and pushes
    // that restored state out (the pet may already have been changed by an
    // earlier 应用 in the same session).
    void RevertPending() {
        _cm = _cm0; _step = _step0; _floatMult = _floatMult0;
        _label = _label0; _charId = _char0; _sound = _sound0; _remember = _remember0;
        if (SetCm != null) SetCm(_cm);
        if (SetStep != null) SetStep(_step);
        if (SetFloatMult != null) SetFloatMult(_floatMult);
        if (SetLabel != null) SetLabel(_label);
        if (SetChar != null) SetChar(_charId);
        if (SetSound != null) SetSound(_sound);
        if (SetRememberScreen != null) SetRememberScreen(_remember);
    }

    static Font SmallFont() { return new Font("Microsoft YaHei", 8.25F); }

    // ---- layout helpers ---------------------------------------------------
    //
    // EVERY coordinate in this class is tracked in UNSCALED 96-dpi units and
    // converted ONCE, at the point of assignment. The first draft accumulated
    // already-scaled values and then passed them through Ps() again, which
    // double-scaled every row below the first and pushed the startup radios
    // 101px past the bottom of the tab (caught by --setsheet, 2026-10-13).
    // Scaling twice is the classic DPI bug here: see the P() rule.
    const int DlgW      = 430;
    const int DlgPad    = 14;
    const int RowH      = 26;
    const int RowGap    = 8;
    const int TabPad    = 12;
    const int LabW      = 96;
    const int HintH     = 18;
    // A numeric row is a preset combo plus a free-text box. The pair splits the
    // row width 3:2 as asked: presets 3/5, editor 2/5 (minus the gap).
    static int TabCtlW  { get { return DlgW - DlgPad * 2 - LabW - TabPad * 2; } }
    static int PairGap  { get { return 6; } }
    static int PresetW  { get { return (TabCtlW - PairGap) * 3 / 5; } }
    static int EditW    { get { return TabCtlW - PairGap - PresetW; } }

    // Height the display tab needs, in unscaled units. TabHeadH is the strip
    // the TabControl spends on its tab buttons: the page client area is
    // shorter than the control's own height, so the arithmetic must reserve it
    // or the last row hangs below the fold.
    const int TabHeadH  = 22;
    static int DisplayTabHeight() {
        return TabHeadH                     // the tab-button strip inside the control
             + TabPad                       // top padding
             + 3 * (RowH + RowGap)          // pet size, character, screen-label rows
             + (HintH + 2)                  // "which screen" hint above the radios
             + (RowH - 4)                   // "remember" radio
             + RowH                         // "always primary" radio
             + HintH                        // the "next launch only" hint
             + TabPad;                      // bottom padding
    }

    // Places a label + control pair on a row. y is unscaled.
    void AddRow(TabPage p, string label, Control ctl, int y) {
        Label l = new Label();
        l.Text = label;
        l.Font = new Font("Microsoft YaHei", 9.75F);
        l.AutoSize = false;                    // Bounds height is authoritative
        l.TextAlign = ContentAlignment.MiddleLeft;
        l.Bounds = new Rectangle(InputDialog.Ps(TabPad), InputDialog.Ps(y),
                                 InputDialog.Ps(LabW), InputDialog.Ps(RowH));
        p.Controls.Add(l);

        ctl.Font = new Font("Microsoft YaHei", 9.75F);
        ctl.Bounds = new Rectangle(InputDialog.Ps(TabPad + LabW), InputDialog.Ps(y),
                                   InputDialog.Ps(TabCtlW), InputDialog.Ps(RowH));
        p.Controls.Add(ctl);
    }

    // Places a NumPicker (preset combo + custom editor) on a row.
    void AddPickerRow(TabPage p, string label, NumPicker pick, int y) {
        Label l = new Label();
        l.Text = label;
        l.Font = new Font("Microsoft YaHei", 9.75F);
        l.AutoSize = false;
        l.TextAlign = ContentAlignment.MiddleLeft;
        l.Bounds = new Rectangle(InputDialog.Ps(TabPad), InputDialog.Ps(y),
                                 InputDialog.Ps(LabW), InputDialog.Ps(RowH));
        p.Controls.Add(l);

        int x = TabPad + LabW;
        pick.Box.Bounds = new Rectangle(InputDialog.Ps(x), InputDialog.Ps(y),
                                        InputDialog.Ps(PresetW), InputDialog.Ps(RowH));
        pick.Edit.Bounds = new Rectangle(InputDialog.Ps(x + PresetW + PairGap), InputDialog.Ps(y),
                                         InputDialog.Ps(EditW), InputDialog.Ps(RowH));
        p.Controls.Add(pick.Box);
        p.Controls.Add(pick.Edit);
    }

    // A one-off grey caption under a group of radios.
    static Label HintLabel(string text, int x, int y, int w) {
        Label h = new Label();
        h.Text = text;
        h.AutoSize = false;
        h.ForeColor = SystemColors.GrayText;
        h.Font = SmallFont();
        h.Bounds = new Rectangle(InputDialog.Ps(x), InputDialog.Ps(y),
                                 InputDialog.Ps(w), InputDialog.Ps(HintH));
        return h;
    }

    TabPage BuildDisplayTab(CharSpec[] chars) {
        TabPage p = new TabPage(WbPet.SettingsTabDisplay);
        int y = TabPad;

        AddPickerRow(p, WbPet.SettingsSizeLabel, _sizePick, y); y += RowH + RowGap;

        AddRow(p, WbPet.SettingsCharLabel, _charBox, y); y += RowH + RowGap;
        foreach (CharSpec c in chars) _charBox.Items.Add(c.Name);
        _charBox.SelectedIndex = CharIndex(chars, _charId);
        _charBox.DropDownStyle = ComboBoxStyle.DropDownList;

        AddRow(p, WbPet.SettingsLabelLabel, _labelBox, y); y += RowH + RowGap;
        _labelBox.Text = _label;

        // "Which screen do you want the pet on next launch" - spelled out,
        // because the pair of radios is otherwise easy to misread.
        _startupHint.Text = WbPet.SettingsStartupHint;
        _startupHint.AutoSize = false;
        _startupHint.ForeColor = SystemColors.GrayText;
        _startupHint.Font = SmallFont();
        _startupHint.Bounds = new Rectangle(InputDialog.Ps(TabPad), InputDialog.Ps(y),
                                            InputDialog.Ps(LabW + TabCtlW), InputDialog.Ps(HintH));
        p.Controls.Add(_startupHint);
        y += HintH + 2;

        _startRemember.Text = WbPet.SettingsStartRemember;
        _startRemember.Checked = _remember;
        _startRemember.Font = new Font("Microsoft YaHei", 9.75F);
        _startRemember.Bounds = new Rectangle(InputDialog.Ps(TabPad), InputDialog.Ps(y),
                                              InputDialog.Ps(LabW + TabCtlW), InputDialog.Ps(RowH));
        p.Controls.Add(_startRemember); y += RowH - 4;

        _startPrimary.Text = WbPet.SettingsStartPrimary;
        _startPrimary.Checked = !_remember;
        _startPrimary.Font = new Font("Microsoft YaHei", 9.75F);
        _startPrimary.Bounds = new Rectangle(InputDialog.Ps(TabPad), InputDialog.Ps(y),
                                             InputDialog.Ps(LabW + TabCtlW), InputDialog.Ps(RowH));
        p.Controls.Add(_startPrimary); y += RowH;

        _posHint.Text = WbPet.SettingsPosHint;
        _posHint.AutoSize = false;
        _posHint.ForeColor = SystemColors.GrayText;
        _posHint.Font = SmallFont();
        _posHint.Bounds = new Rectangle(InputDialog.Ps(TabPad), InputDialog.Ps(y),
                                        InputDialog.Ps(LabW + TabCtlW), InputDialog.Ps(HintH));
        p.Controls.Add(_posHint);
        return p;
    }

    TabPage BuildChargeTab() {
        TabPage p = new TabPage(WbPet.SettingsTabCharging);
        int y = TabPad;

        AddPickerRow(p, WbPet.SettingsStepLabel, _stepPick, y); y += RowH + RowGap;
        AddPickerRow(p, WbPet.SettingsFloatLabel, _floatPick, y); y += RowH + RowGap;

        // The hit-sound switch moved here from the display tab and was renamed:
        // it fires on a deduction, so it belongs with the charging settings.
        _soundBox.Text = WbPet.SettingsSoundLabel;
        _soundBox.Checked = _sound;
        _soundBox.Font = new Font("Microsoft YaHei", 9.75F);
        _soundBox.Bounds = new Rectangle(InputDialog.Ps(TabPad), InputDialog.Ps(y),
                                         InputDialog.Ps(LabW + TabCtlW), InputDialog.Ps(RowH));
        p.Controls.Add(_soundBox);
        return p;
    }

    TabPage BuildCredTab(string blockText) {
        TabPage p = new TabPage(WbPet.SettingsTabCredential);

        _credBody.Font = new Font("Microsoft YaHei", 9.75F);
        _credBody.AutoSize = false;            // Bounds height is authoritative
        p.Controls.Add(_credBody);

        _credSet.Text = WbPet.SettingsSetToken;
        _credSet.Font = new Font("Microsoft YaHei", 9.75F);
        _credSet.Click += delegate {
            // The credential dialog is its own modal window. Apply first so the
            // user does not lose pending edits when they go paste a token.
            ApplyPending();
            if (AskToken != null) AskToken();
            // The block text is built when the dialog opens, so it would still
            // read "not set" right after a successful paste. Re-pull it, then
            // prove the new credential straight away (asked 2026-10-18).
            if (CredBlockSource != null) SetCredBlock(CredBlockSource());
            TestCredentialUserRequested();
        };
        p.Controls.Add(_credSet);

        _credTest.Text = WbPet.SettingsTestToken;
        _credTest.Font = new Font("Microsoft YaHei", 9.75F);
        _credTest.Click += delegate { TestCredentialUserRequested(); };
        p.Controls.Add(_credTest);

        _credStatus.AutoSize = false;
        _credStatus.Font = SmallFont();
        _credStatus.TextAlign = ContentAlignment.MiddleLeft;
        _credStatus.ForeColor = SystemColors.GrayText;
        p.Controls.Add(_credStatus);

        SetCredBlock(blockText);

        // Opening the window with the credential tab already selected is not a
        // path the code takes today (it starts on Display), but polling must not
        // depend on that accident.
        if (_tabs != null && _tabs.SelectedIndex == CredTabIndex) StartCredPolling();
        return p;
    }

    // Lays the page out for the given block text and pushes the rows below it
    // down accordingly. TextRenderer measures in PHYSICAL pixels, so the
    // measured height is not passed through Ps() a second time (see the DPI
    // note on the layout helpers).
    void SetCredBlock(string text) {
        if (text == null) text = "";
        _credBody.Text = text;
        Size need = TextRenderer.MeasureText(
            text, _credBody.Font, new Size(InputDialog.Ps(LabW + TabCtlW), int.MaxValue),
            TextFormatFlags.WordBreak);
        int textH = need.Height + InputDialog.Ps(6);
        _credBody.Bounds = new Rectangle(InputDialog.Ps(TabPad), InputDialog.Ps(TabPad),
                                         InputDialog.Ps(LabW + TabCtlW), textH);

        int yBtn = InputDialog.Ps(TabPad) + textH + InputDialog.Ps(10);
        _credSet.Bounds  = new Rectangle(InputDialog.Ps(TabPad), yBtn,
                                         InputDialog.Ps(140), InputDialog.Ps(30));
        _credTest.Bounds = new Rectangle(InputDialog.Ps(TabPad) + InputDialog.Ps(148), yBtn,
                                         InputDialog.Ps(130), InputDialog.Ps(30));

        int ySt = yBtn + InputDialog.Ps(30) + InputDialog.Ps(8);
        _credStatus.Bounds = new Rectangle(InputDialog.Ps(TabPad), ySt,
                                           InputDialog.Ps(LabW + TabCtlW), InputDialog.Ps(CredStatusH));
    }

    // Index of the preset equal to v, else the closest one.
    static int Nearest(double[] values, double v) {
        int best = 0; double bestD = double.MaxValue;
        for (int i = 0; i < values.Length; i++) {
            double d = Math.Abs(values[i] - v);
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    static int CharIndex(CharSpec[] chars, string id) {
        for (int i = 0; i < chars.Length; i++) if (chars[i].Id == id) return i;
        return 0;
    }
}

// ---------------------------------------------------------- numeric picker --
//
// A preset dropdown plus a free-text editor, for one numeric setting. This
// replaces the old menu's "presets + custom..." pair of rows: the two sit side
// by side on a single line (asked 2026-10-13).
//
// How it reads:
//   * on open, the box shows the setting's CURRENT value (not a placeholder):
//     a saved preset selects that combo row, anything else shows up in the
//     box with the combo on "自定义". Reopening therefore echoes what was
//     saved instead of snapping back to the first preset (bug fixed
//     2026-10-13: the ctor took an `initial` it never used).
//   * the combo shows a preset name; the box is empty and shows the grey
//     placeholder "自定义...".
//   * typing a valid number into the box switches the combo to a "自定义" row
//     and keeps only the digits in the box - which is what the user asked for,
//     so the leftover placeholder never sits next to a real number.
//   * picking a preset again clears the box and drops the "自定义" row.
//
// Input rules: at most two decimals, clamped to a per-setting [min, max]
// (asked 2026-10-13; limits differ per row - size tops out at 20cm, the
// floater multiplier at 10x - so the ceiling is a ctor argument, not a
// constant). Enforcement is in the key handler rather than only on commit,
// so an out-of-range character never appears and then vanishes.
sealed class NumPicker {
    // "custom..." - the grey placeholder and the synthetic combo label.
    const string CUSTOM_ROW = "\u81EA\u5B9A\u4E49";      // custom

    public readonly ComboBox Box = new ComboBox();
    public readonly TextBox  Edit = new TextBox();

    public event Action<double> PresetChosen;
    public event Action<double> CustomEntered;

    readonly string _placeholder;
    readonly double _min, _max;
    readonly double _initial;        // value the picker opens on (see ShowValue)
    double[] _presets;
    string[] _labels;
    bool _customShown;               // is the "custom" row currently in the list?
    bool _suppress;                  // guard against re-entrant SelectedIndexChanged

    public NumPicker(string placeholder, double min, double max, double initial) {
        _placeholder = placeholder;
        _min = min; _max = max;
        _initial = initial;

        Box.Font = new Font("Microsoft YaHei", 9.75F);
        Box.DropDownStyle = ComboBoxStyle.DropDownList;
        Box.SelectedIndexChanged += delegate { OnPreset(); };

        Edit.Font = new Font("Microsoft YaHei", 9.75F);
        Edit.Text = placeholder;
        Edit.ForeColor = SystemColors.GrayText;
        Edit.GotFocus += delegate { if (IsPlaceholder) { Edit.Text = ""; Edit.ForeColor = SystemColors.WindowText; } };
        Edit.LostFocus += delegate { if (Edit.Text.Trim().Length == 0) ShowPlaceholder(); };
        Edit.KeyPress += delegate (object s, KeyPressEventArgs e) { e.Handled = !AllowChar(e.KeyChar); };
        Edit.TextChanged += delegate { OnText(); };
    }

    bool IsPlaceholder { get { return Edit.Text == _placeholder && Edit.ForeColor == SystemColors.GrayText; } }

    void ShowPlaceholder() {
        Edit.Text = _placeholder;
        Edit.ForeColor = SystemColors.GrayText;
    }

    // Digits, one dot, and at most two decimals. The integer-digit cap follows
    // the ceiling (20cm admits 2 digits, 999.99 admits 3), so a value can be
    // typed up to - but never past - its own limit. Backspace always passes.
    bool AllowChar(char c) {
        if (char.IsControl(c)) return true;
        if (!char.IsDigit(c) && c != '.') return false;
        if (IsPlaceholder) return true;                   // first keystroke replaces it
        string cur = Edit.Text;
        int sel = Edit.SelectionLength;
        if (c == '.') return cur.IndexOf('.') < 0;
        int dot = cur.IndexOf('.');
        if (dot >= 0 && sel == 0 && cur.Length - dot - 1 >= 2) return false;   // 2 dp max
        if (dot < 0 && sel == 0 && cur.Length >= IntDigits()) return false;    // int cap
        return true;
    }

    // How many integer digits the ceiling allows (999.99 -> 3, 20 -> 2, 10 -> 2).
    int IntDigits() {
        int n = (int)Math.Floor(_max);
        int d = 1;
        while (n >= 10) { n /= 10; d++; }
        return d;
    }

    public void SetPresets(double[] values, Func<double, string> label) {
        _presets = values;
        _labels = new string[values.Length];
        for (int i = 0; i < values.Length; i++) _labels[i] = label(values[i]);
        Box.Items.Clear();
        foreach (string s in _labels) Box.Items.Add(s);
        _customShown = false;
        // Seed from the value the dialog opened with - NOT from Current(),
        // which at this point still reads the untouched placeholder and so
        // collapsed every row to its first preset (the reopen bug).
        ShowValue(_initial);
    }

    // Renders v into the pair at open time, so a reopened dialog echoes what
    // was saved. Two cases (asked 2026-10-13):
    //   * the value IS a preset - the combo already says it, so the editor
    //     goes back to the grey "自定义..." placeholder rather than repeating
    //     the number the dropdown is showing;
    //   * the value is not a preset (or carries more precision than the list
    //     does) - the number lives in the editor and the combo reads 自定义.
    public void ShowValue(double v) {
        int hit = -1;
        if (_presets != null) {
            for (int i = 0; i < _presets.Length; i++)
                if (Math.Abs(_presets[i] - v) < 1e-9) { hit = i; break; }
        }
        _suppress = true;
        if (hit >= 0) {
            ShowPlaceholder();                 // combo carries the value
            Box.SelectedIndex = hit;
        } else {
            Edit.Text = v.ToString("0.##", CultureInfo.InvariantCulture);
            Edit.ForeColor = SystemColors.WindowText;
            if (!_customShown) { Box.Items.Add(CUSTOM_ROW); _customShown = true; }
            Box.SelectedIndex = Box.Items.Count - 1;
        }
        _suppress = false;
    }

    // Index of the preset equal to v, else the closest one.
    static int NearestIn(double[] values, double v) {
        int best = 0; double bestD = double.MaxValue;
        for (int i = 0; i < values.Length; i++) {
            double d = Math.Abs(values[i] - v);
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    // The value the picker currently represents (preset or typed).
    double Current() {
        double v;
        if (!IsPlaceholder && double.TryParse(Edit.Text.Trim(), NumberStyles.Float,
                                              CultureInfo.InvariantCulture, out v)) return v;
        return (_presets != null && Box.SelectedIndex >= 0 && Box.SelectedIndex < _presets.Length)
            ? _presets[Box.SelectedIndex] : _min;
    }

    // ---- test hooks (--pickertest) ---------------------------------------
    // What the pair displays right now, without a message pump.
    public double ShownValue() { return Current(); }
    public bool IsCustomShown { get { return _customShown; } }
    // True while the editor is showing the grey "自定义..." placeholder.
    public bool IsShowingPlaceholder { get { return IsPlaceholder; } }
    // Would the key handler accept every character of `digits`, starting from an
    // empty (non-placeholder) box? Simulates the real editing path.
    public bool AcceptsDigits(string digits) {
        string saveText = Edit.Text; Color saveCol = Edit.ForeColor; int saveSel = Edit.SelectionLength;
        try {
            Edit.Text = ""; Edit.ForeColor = SystemColors.WindowText; Edit.SelectionLength = 0;
            foreach (char c in digits) {
                if (!AllowChar(c)) return false;
                Edit.Text = Edit.Text + c;
            }
            return true;
        } finally {
            Edit.Text = saveText; Edit.ForeColor = saveCol; Edit.SelectionLength = saveSel;
        }
    }
    // Runs `v` through the same clamp OnText applies, without touching the
    // widgets. Proves the floor is enforced wherever a value enters.
    public double CommitValue(double v) {
        if (v < _min) v = _min;
        if (v > _max) v = _max;
        return v;
    }

    void EnsureCustomRow() {
        if (_customShown) return;
        Box.Items.Add(CUSTOM_ROW);
        _customShown = true;
    }

    void OnPreset() {
        if (_suppress) return;
        int i = Box.SelectedIndex;
        if (_customShown && i == Box.Items.Count - 1) return;   // the custom row itself
        if (_presets == null || i < 0 || i >= _presets.Length) return;
        // Picking a preset abandons whatever was typed.
        _suppress = true;
        if (IsPlaceholder || Edit.Text.Trim().Length > 0) ShowPlaceholder();
        _suppress = false;
        if (PresetChosen != null) PresetChosen(_presets[i]);
    }

    void OnText() {
        if (_suppress) return;
        string t = Edit.Text.Trim();
        if (t.Length == 0 || IsPlaceholder) return;
        double v;
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return;
        v = CommitValue(v);                     // single home for the [min,max] clamp
        // Switch the combo to "自定义" and keep the number in the box. Guarded,
        // because editing the item list raises SelectedIndexChanged.
        _suppress = true;
        if (!_customShown) {
            Box.Items.Add(CUSTOM_ROW);
            _customShown = true;
        }
        Box.SelectedIndex = Box.Items.Count - 1;
        _suppress = false;
        if (CustomEntered != null) CustomEntered(v);
    }
}

// ---------------------------------------------------------------- sound ----
//
// Overlapping hit sounds through the Win32 MCI interface: every cue is played
// on its own alias, so a new hit never cuts off the previous one.
public sealed class SoundPool {
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    static extern int mciSendStringW(string cmd, StringBuilder ret, int len, IntPtr hwnd);
    static string Send(string cmd) {
        StringBuilder sb = new StringBuilder(256);
        mciSendStringW(cmd, sb, sb.Capacity, IntPtr.Zero);
        return sb.ToString();
    }

    // MCI aliases are PROCESS-GLOBAL. Two pools sharing an alias means the
    // second pool's "close + open ... alias" silently rebinds the first
    // pool's handles to its own file (confirmed 2026-10-07: the hit pool
    // started playing er.mp3 after the egg pool opened). Every instance
    // therefore claims its own alias tag from this counter.
    static int _tagSeq;
    readonly string _tag;            // unique alias prefix, e.g. "wbpp0_"
    // What tag the n-th pool from now would take. Used by --eggsound to prove
    // two pools can never collide, without opening any device.
    public static string PeekNextTag(int n) {
        return "wbpp" + (_tagSeq + n).ToString(CultureInfo.InvariantCulture) + "_";
    }
    readonly string _path;
    readonly string _bare;           // file name only - MCI mangles non-ASCII paths
    readonly int _slots;
    readonly int _volume;            // 0..100; stored so a reopened slot keeps it
    readonly string[] _alias;
    readonly bool[] _open;
    readonly string _logPath;
    public bool Failed; public string Error = ""; public int Plays;

    public SoundPool(string path, int slots, int volumePercent, string logPath) {
        _path = path; _slots = slots; _logPath = logPath; _volume = volumePercent;
        _bare = Path.GetFileName(path);
        _tag = "wbpp" + (++_tagSeq).ToString(CultureInfo.InvariantCulture) + "_";
        _alias = new string[slots]; _open = new bool[slots];
        try {
            // MCI receives a mangled path when the folder name is not ASCII, so
            // switch the process working directory to the sound's own folder and
            // have MCI open the bare file name instead of a full path.
            try { Directory.SetCurrentDirectory(Path.GetDirectoryName(path)); } catch { }
            for (int i = 0; i < slots; i++) {
                _alias[i] = _tag + i.ToString(CultureInfo.InvariantCulture);
                Send("close " + _alias[i]);
                _open[i] = OpenSlot(i);
            }
            if (!_open[0]) { Failed = true; Error = "cannot open " + path; }
        } catch (Exception ex) { Failed = true; Error = ex.Message; }
    }

    // Open one alias the way the constructor learned to: bare file name first
    // (non-ASCII folder names confuse MCI), full path as a fallback, then
    // re-apply the volume - a freshly opened alias is always at full volume.
    bool OpenSlot(int i) {
        Send("close " + _alias[i]);
        bool ok = Send("open \"" + _bare + "\" type mpegvideo alias " + _alias[i]).Length == 0;
        if (!ok) ok = Send("open \"" + _path + "\" type mpegvideo alias " + _alias[i]).Length == 0;
        if (ok) Send("setaudio " + _alias[i] + " volume to " + _volume * 10);
        return ok;
    }

    // True when the alias can safely take a new "seek + play".
    //
    // BLACKLIST, not whitelist. We only refuse the states that mean a cue is
    // currently sounding or the device is mid-transition:
    //   playing  - obviously busy;
    //   seeking  - still settling after a previous seek, so play now would
    //              restart/cut the cue already sounding;
    //   paused   - needs "resume", not "seek + play".
    //
    // Everything else is treated as usable. That deliberately includes the empty
    // string and "open": a freshly opened alias answers its first "status mode"
    // with something other than "stopped" (MCI_MODE_OPEN / not ready), and an
    // earlier whitelist of exactly "stopped" therefore found NO free slot at all
    // before the first cue - single playback broke. "not ready" is also let
    // through, because the device reports it for a moment after every open/seek
    // and it clears on its own; refusing it starved the pool during a burst.
    public static bool IsSlotFree(string mode) {
        string m = (mode ?? "").Trim();
        if (m.Length == 0) return true;                       // never opened / just opened
        if (m.IndexOf("playing", StringComparison.OrdinalIgnoreCase) >= 0) return false;
        if (m.IndexOf("seeking", StringComparison.OrdinalIgnoreCase) >= 0) return false;
        if (m.IndexOf("paused", StringComparison.OrdinalIgnoreCase) >= 0) return false;
        return true;
    }

    // Plays on a free alias and returns its index, or -1 when nothing played.
    //
    // A slot is taken only when MCI positively reports a cue in flight - see
    // IsSlotFree. The old test was IndexOf("playing") >= 0, which treated
    // seeking/not ready as free and chopped the tail off back-to-back cues; the
    // intermediate fix went too far the other way and demanded exactly
    // "stopped", which no freshly opened alias ever reports, so even the first
    // cue found no home. The rule now splits the difference: refuse only what is
    // demonstrably busy, accept empty / open / stopped / not ready.
    public int Play() {
        if (Failed) return -1;
        try {
            for (int attempt = 0; attempt < 2; attempt++) {
                for (int i = 0; i < _slots; i++) {
                    if (!_open[i]) continue;
                    if (!IsSlotFree(Send("status " + _alias[i] + " mode"))) continue;
                    Send("seek " + _alias[i] + " to start");
                    Send("play " + _alias[i]);
                    Plays++;
                    return i;
                }
                // Every slot is busy (rare, needs four overlapping cues). Take
                // the oldest one back so a burst still overlaps - but reopen it
                // through OpenSlot so the volume survives and a non-ASCII path
                // does not silently kill the alias for the rest of the session.
                if (attempt == 0) _open[0] = OpenSlot(0);
            }
        } catch (Exception ex) {
            Failed = true; Error = ex.Message;
            try { File.AppendAllText(_logPath, DateTime.Now.ToString("s") + " sound: " + ex.Message + "\r\n"); } catch { }
        }
        return -1;
    }

    public void Dispose() {
        try { for (int i = 0; i < _slots; i++) if (_open[i]) Send("close " + _alias[i]); } catch { }
    }

    // Report what this pool's alias `i` currently resolves to. MCI has no
    // "which file" query, so the length stands in: hit.mp3 and er.mp3 differ in
    // length (185 vs 196), which is how --aliascheck detects a rebind.
    public string DbgInfo(int i) {
        string a = _alias[i];
        return a + " mode='" + Send("status " + a + " mode").Trim() + "'" +
               " length=" + Send("status " + a + " length").Trim();
    }

    // Variant of Play() that lets --soundab force a specific historical rule on
    // the SAME pool, so the three rules are compared under identical conditions.
    // 0 = old substring test, 1 = strict "stopped", 2 = current blacklist.
    //
    // LastRuleNote records, for the cue just placed, the raw mode string that was
    // used to accept the slot and how many aliases were still settling. A rule
    // that accepts a non-terminal slot is the one that cuts tails, even though it
    // reports a successful placement.
    public string LastRuleNote = "";
    public int PlayRule(int rule) {
        if (Failed) return -1;
        try {
            // snapshot every alias mode BEFORE choosing, so the note describes
            // the state the rule actually saw
            string[] snap = new string[_slots];
            for (int i = 0; i < _slots; i++)
                snap[i] = _open[i] ? Send("status " + _alias[i] + " mode").Trim() : "(closed)";

            for (int i = 0; i < _slots; i++) {
                if (!_open[i]) continue;
                string mode = snap[i];
                bool free;
                switch (rule) {
                    case 0:  free = !(mode.IndexOf("playing") >= 0); break;   // the original bug
                    case 1:  free = string.Equals(mode, "stopped", StringComparison.OrdinalIgnoreCase); break;
                    default: free = IsSlotFree(mode); break;
                }
                if (!free) continue;
                // was the chosen slot actually settled?
                bool settled = mode.Length == 0 ||
                               mode.IndexOf("stopped", StringComparison.OrdinalIgnoreCase) >= 0;
                LastRuleNote = (settled ? "SETTLED" : "NOT-SETTLED(" + mode + ")");
                Send("seek " + _alias[i] + " to start");
                Send("play " + _alias[i]);
                Plays++;
                return i;
            }
            LastRuleNote = "NO-SLOT";
        } catch (Exception ex) {
            Failed = true; Error = ex.Message;
        }
        return -1;
    }

    // Diagnostics for --soundprobe: the raw status string of every alias, so a
    // dropped cue can be told apart from a device that never opened.
    public string DebugModes() {
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < _slots; i++) {
            if (i > 0) sb.Append(" | ");
            if (!_open[i]) { sb.Append(i + ":closed"); continue; }
            string m = Send("status " + _alias[i] + " mode").Trim();
            sb.Append(i + ":" + (m.Length == 0 ? "(empty)" : m));
        }
        return sb.ToString();
    }

    // Raw mode of a single alias, for the --soundab transition timer.
    public string ModeOf(int i) {
        if (i < 0 || i >= _slots || !_open[i]) return "(closed)";
        return Send("status " + _alias[i] + " mode").Trim();
    }
}
// ------------------------------------------------------------------ window ---

public sealed class WbPet : Form {
    const string DEFAULT_LABEL = "WorkBuddy \u79EF\u5206";                       // WorkBuddy credits
    const string S_HELP    = "\u6F14\u793A\u8FDE\u7EED\u6263\u8D39";             // demo consecutive charges
    const string S_SIZE    = "\u5C3A\u5BF8";                                     // size
    const string S_REFRESH = "\u7ACB\u5373\u5237\u65B0\u79EF\u5206";             // refresh now
    const string S_TEST    = "\u6D4B\u8BD5\u4E00\u6B21\u6263\u8D39\u6548\u679C"; // test one charge
    const string S_QUIT    = "\u9000\u51FA";                                     // quit
    const string S_CUSTOM  = "\u81EA\u5B9A\u4E49...";                            // custom...
    const string S_HELPT   = "\u6F14\u793A\u6263\u8D39\u79EF\u5206";             // demo amount title
    const string S_HELPP   = "\u8981\u6F14\u793A\u6263\u591A\u5C11\u79EF\u5206"; // demo amount prompt
    const string S_SIZET   = "\u5C3A\u5BF8";                                     // size title
    const string S_SIZEP   = "\u9AD8\u5EA6\uFF08\u5398\u7C73\uFF09";             // height prompt (cm)
    const string S_NOKEY   = "\u7F3A\u5C11\u767B\u5F55\u51ED\u8BC1";             // no token
    const string S_EXPIRED = "\u51ED\u8BC1\u5DF2\u8FC7\u671F";                   // token expired
    const string S_LOADING = "\u8FDE\u63A5\u4E2D...";                            // connecting...
    const string S_TOKENT  = "WorkBuddy \u51ED\u8BC1";                           // token dialog title
    const string S_TOKENP  = "\u7C98\u8D34\u6574\u6BB5 cURL\uFF08\u542B Cookie \u548C User-Agent\uFF09\r\n" +
                             "\u6216\u76F4\u63A5\u7C98 Cookie \u4E32\uFF1B\u7559\u7A7A\u5219\u4E0D\u4FEE\u6539";
    // The hint under the credential editor: ONE line, about expiry only. The
    // paste rule that used to lead it was dropped on request (2026-10-02) -
    // the prompt above already says "paste the whole cURL (Cookie + User-Agent)",
    // so repeating it here just pushed the expiry note out of view.
    const string S_TOKENH  = "\u767B\u5F55\u51ED\u8BC1\u4F1A\u5728\u4E00\u6BB5\u65F6\u95F4\u540E\u8FC7\u671F\uFF0C\u5230\u65F6\u9700\u91CD\u65B0\u83B7\u53D6\uFF0C\u8FC7\u671F\u540E\u5E94\u7528\u4F1A\u7EA2\u70B9\u63D0\u9192\u3002";
    const string S_SETTOKEN= "\u8BBE\u7F6E\u767B\u5F55\u51ED\u8BC1";             // set token menu
    // The "how do I get one" label lives on the credential dialog now (its
    // InputDialog owns that string), not as a tray entry - see GUIDE_LABEL.
    const string S_GUIDET  = "\u5982\u4F55\u83B7\u53D6\u51ED\u8BC1";             // guide title
    const string S_STEPMENU= "\u6263\u8D39\u6B65\u957F";                         // step submenu
    const string S_STEPT   = "\u6263\u8D39\u6B65\u957F";                         // step dialog title
    const string S_STEPP   = "\u6BCF\u6B21\u52A8\u753B\u6263\u591A\u5C11\u79EF\u5206";
    const string S_LBLMENU = "\u5C4F\u5E55\u6807\u7B7E...";                      // screen label menu
    const string S_LBLT    = "\u5C4F\u5E55\u6807\u7B7E";                         // label dialog title
    const string S_LBLP    = "\u5E73\u677F\u7B2C\u4E00\u884C\u663E\u793A\u7684\u6587\u5B57";
    const string S_CHARMENU= "\u89D2\u8272";                                     // character submenu
    // Easter egg (v1.2.16.zc): when the amount actually taken off in one
    // animation is exactly one of these, the cue switches to the alternate
    // recording (WBPET_EGG_FILE, bundled as er.mp3). Matched against the
    // REAL per-cue amount in MakeTick, so a rehearsal or a big demo run
    // hits it too - not just the configured step. Matched by exact
    // two-decimal rounding (NOT a tolerance - see IsEggStep; a tolerance
    // let 3.25006 fire). Deliberately NOT listed in the step menu: the
    // whole point is that the user discovers them, so type one into
    // "custom" (or set it any other way) to hear it.
    static readonly double[] EGG_STEPS = new double[] { 3.25, 32.5, 325 };
    const string EGG_FILE = "er.mp3";
    // Shown after the numeric version and appended to the User-Agent. Keep
    // in step with _asm_pet.cs (assembly 4th segment) and setup.cs
    // (VersionSuffix). Deliberately NOT part of the assembly version: a
    // non-numeric segment would refuse to load.
    const string VersionTag = "";
        const string S_SOUNDMENU="\u53D7\u51FB\u97F3\u6548";                         // hit sound toggle
    const string S_ABOUT   = "\u5173\u4E8E...";                                  // about...
    const string S_ABOUTT  = "\u5173\u4E8E";                                     // about (title)
    // ---- settings dialog (1.2.19) -----------------------------------------
    // The right-click menu had grown to eleven entries, eight of which were
    // settings rather than actions. They moved into one dialog opened from a
    // "settings..." entry sitting directly above "about..." (asked 2026-10-13).
    // The menu now keeps only the per-session ACTIONS - refresh, test a charge,
    // demo charges, settings, about, quit.
    const string S_SETTINGS  = "\u8BBE\u7F6E...";                                // settings...
    const string S_SETTINGST = "\u8BBE\u7F6E";                                   // settings (title)
    const string S_TAB_DISP  = "\u663E\u793A";                                   // tab: display
    const string S_TAB_CHARGE= "\u6263\u8D39";                                   // tab: charging
    const string S_TAB_CRED  = "\u51ED\u8BC1";                                   // tab: credential
    const string S_STARTUP   = "\u542F\u52A8\u843D\u70B9";                       // startup position
    const string S_START_REM = "\u8BB0\u4F4F\u4E0A\u6B21\u6240\u5728\u5C4F\u5E55";   // remember last screen
    const string S_START_PRI = "\u59CB\u7EC8\u843D\u5728\u4E3B\u5C4F\u5E55";         // always the primary
    const string S_POS_HINT  = "\u4EC5\u5F71\u54CD\u4E0B\u6B21\u542F\u52A8\uFF0C\u5F53\u524D\u4F4D\u7F6E\u4E0D\u53D8\u3002"; // affects next launch only
    // New for 1.2.19 r2 (asked 2026-10-13):
    //   * the size row gets a clearer name now that it shares a dialog with
    //     other "sizes" (step / damage font);
    //   * the hit-sound switch is really a CHARGING sound, and lives on the
    //     charging tab now, so it is renamed to match;
    //   * the startup-position pair gets a one-line "what am I choosing" hint;
    //   * OK/Cancel/Apply get Chinese captions to match the rest of the UI.
    const string S_SIZEMENU  = "\u684C\u5BA0\u5C3A\u5BF8";                       // pet size
    const string S_SOUND_MENU2="\u6263\u8D39\u97F3\u6548";                       // charging sound
    const string S_STARTUP_HINT="\u9009\u62E9\u4E0B\u6B21\u542F\u52A8\u65F6\u684C\u5BA0\u843D\u5728\u54EA\u5757\u5C4F\u5E55\u3002"; // which screen next launch
    const string S_BTN_OK    = "\u786E\u8BA4";                                   // OK
    const string S_BTN_CANCEL= "\u53D6\u6D88";                                   // Cancel
    const string S_BTN_APPLY = "\u5E94\u7528";                                   // Apply

    // ---- credential page: live validity check (1.2.19.5, asked 2026-10-18) --
    // The page keeps testing the stored credential for as long as the settings
    // window is open on that tab, so a dead session is visible BEFORE the
    // balance collapses into "--". Four states: one transient, three steady.
    const string S_CRED_TESTING = "\u6D4B\u8BD5\u51ED\u8BC1\u662F\u5426\u6709\u6548\u2026";   // testing whether the credential is valid...
    const string S_CRED_OK      = "\u5B58\u50A8\u7684\u51ED\u8BC1\u6709\u6548\uFF01";         // the stored credential is valid!
    const string S_CRED_BAD     = "\u5B58\u50A8\u7684\u51ED\u8BC1\u5DF2\u5931\u6548\uFF01\u8BF7\u91CD\u65B0\u7C98\u8D34\u51ED\u8BC1"; // ...has expired! paste a new one
    // Only shown when the request could not be made at all (no HTTP answer -
    // cable out, DNS dead, proxy hiccup). Reporting that as "expired" would
    // send the user off to re-paste a credential that is perfectly fine.
    const string S_CRED_NET     = "\u6D4B\u8BD5\u5931\u8D25\uFF1A\u7F51\u7EDC\u5F02\u5E38\uFF0C\u7A0D\u540E\u81EA\u52A8\u91CD\u8BD5"; // test failed: network, will retry
    const string S_TESTCRED     = "\u6D4B\u8BD5\u5F53\u524D\u51ED\u8BC1";                      // test current credential

    // The settings dialog is a separate class, so the handful of strings it
    // needs are surfaced here rather than duplicated. Also lets --settest
    // print the exact labels the window will show.
    internal static string SettingsTabDisplay    { get { return S_TAB_DISP; } }
    internal static string SettingsTabCharging   { get { return S_TAB_CHARGE; } }
    internal static string SettingsTabCredential { get { return S_TAB_CRED; } }
    internal static string SettingsSizeLabel     { get { return S_SIZEMENU; } }
    internal static string SettingsCharLabel     { get { return S_CHARMENU; } }
    internal static string SettingsLabelLabel    { get { return S_LBLT; } }
    internal static string SettingsSoundLabel    { get { return S_SOUND_MENU2; } }
    internal static string SettingsStepLabel     { get { return S_STEPMENU; } }
    internal static string SettingsFloatLabel    { get { return S_FLOTMENU; } }
    internal static string SettingsStartRemember { get { return S_START_REM; } }
    internal static string SettingsStartPrimary  { get { return S_START_PRI; } }
    internal static string SettingsPosHint       { get { return S_POS_HINT; } }
    internal static string SettingsStartupHint   { get { return S_STARTUP_HINT; } }
    internal static string SettingsSetToken      { get { return S_SETTOKEN; } }
    internal static string SettingsTestToken     { get { return S_TESTCRED; } }
    internal static string CredTextTesting       { get { return S_CRED_TESTING; } }
    internal static string CredTextOk            { get { return S_CRED_OK; } }
    internal static string CredTextBad           { get { return S_CRED_BAD; } }
    internal static string CredTextNet           { get { return S_CRED_NET; } }
    internal static string SettingsCustomHint    { get { return S_CUSTOM; } }     // "custom..."
    internal static string SettingsBtnOk         { get { return S_BTN_OK; } }
    internal static string SettingsBtnCancel     { get { return S_BTN_CANCEL; } }
    internal static string SettingsBtnApply      { get { return S_BTN_APPLY; } }
    // Reuse the widget's own formatters so the combo boxes read exactly like
    // the menu rows they replace.
    internal static string CmFor(double v)  { return CmLabel(v); }
    internal static string FmtFor(double v) { return FmtAmt(v); }
    // ---- update check (About > "check for updates") ----------------------
    // The check reads the 302 that github.com itself returns for
    // /releases/latest. That redirect is served by github.com, NOT by
    // api.github.com, so it does not spend the unauthenticated API quota
    // (60 requests/hour per source IP, shared by everyone behind a NAT).
    // Verified 2026-10-02: cli/cli -> /releases/tag/v2.102.0.
    const string S_UPDATE  = "\u68C0\u67E5\u66F4\u65B0";                         // check for updates
    const string S_UPDCHK  = "\u6B63\u5728\u68C0\u67E5\u66F4\u65B0...";          // checking for updates...
    const string S_UPDFAIL = "\u68C0\u67E5\u66F4\u65B0\u5931\u8D25";             // update check failed
    const string S_UPDT    = "\u68C0\u67E5\u66F4\u65B0";                         // update check (title)
    const string S_UPDFAILP= "\u65E0\u6CD5\u8FDE\u63A5\u5230 GitHub\uFF0C\u8BF7\u7A0D\u540E\u91CD\u8BD5\u3002"; // can't reach GitHub
    const string S_UPDSAME = "\u5F53\u524D\u5DF2\u662F\u6700\u65B0\u7248\u672C";  // already up to date
    const string S_UPDSAMEP="\u672C\u7248\u672C\u4E3A\u6700\u65B0\u7248\u672C\u3002"; // this is the latest version
    const string S_UPDFOUND="\u53D1\u73B0\u65B0\u7248\u672C";                    // new version available
    const string S_UPDNEW  = "\u65B0\u7248\u672C";                               // new version
    const string S_UPDCUR  = "\u5F53\u524D\u7248\u672C";                         // current version
    const string S_UPDOPEN = "\u6253\u5F00\u4E0B\u8F7D\u9875\u9762";             // open download page
    const string S_UPDLATER= "\u7A0D\u540E\u518D\u8BF4";                         // later
    // Shown inside the dialog. Relabelled buttons alone still leave "what is
    // actually going to happen" to be guessed at (reported 2026-10-02: the old
    // MessageBox offered only "new version / current version" above an OK
    // button, and the user could not tell that OK would open a browser).
    const string S_UPDNOTE = "\u70B9\u300C\u6253\u5F00\u4E0B\u8F7D\u9875\u9762\u300D\u5C06\u5728\u6D4F\u89C8\u5668\u4E2D\u67E5\u770B\u4E0B\u8F7D\u5730\u5740\u3002";
    // The repo the check points at. Keep in step with the installer's
    // Publisher/Product when the project ever moves.
    const string UPD_OWNER = "Realfie57";
    const string UPD_REPO  = "workbuddy-pointpal";
    // The progress notice must stay up this long even if the network answers
    // faster, so the user sees that something actually happened (asked for
    // 2026-10-02). Reported result waits out the remainder.
    const int UPD_MIN_MS = 700;
    const string S_FLOTMENU= "\u6263\u8D39\u5B57\u53F7";                         // floater size submenu
    const string S_FLOTT   = "\u6263\u8D39\u5B57\u53F7";                         // floater dialog title
    const string S_FLOTP   = "\u5934\u9876\u6570\u5B57\u7684\u500D\u6570\uFF080.5-10\uFF09"; // floater multiplier prompt
    const string GUIDE_BODY =
        "1. \u6D4F\u89C8\u5668\u6253\u5F00 www.workbuddy.cn \u5E76\u767B\u5F55\r\n" +
        "2. \u6309 F12 \u6253\u5F00\u5F00\u53D1\u8005\u5DE5\u5177\uFF0C\u5207\u5230 Network\uFF08\u7F51\u7EDC\uFF09\u6807\u7B7E\u9875\r\n" +
        "3. \u9009\u62E9\u67E5\u770B Doc\uFF08\u6587\u6863\uFF09\u7C7B\u578B\u7684\u8BF7\u6C42\uFF0C\u5237\u65B0\u9875\u9762\r\n" +
        "4. \u627E\u5230 www.workbuddy.cn\uFF0C\u53F3\u952E \u2192 \u590D\u5236 \u2192 \u4EE5 cURL \u683C\u5F0F\u590D\u5236\uFF08bash\uFF09\r\n" +
        "5. \u53F3\u952E\u684C\u5BA0 \u2192 \u8BBE\u7F6E\u2026 \u2192 \u51ED\u8BC1\u9875\u7B7E \u2192 \u8BBE\u7F6E\u767B\u5F55\u51ED\u8BC1\uFF0C\u6574\u6BB5\u7C98\u8D34\u5373\u53EF\r\n\r\n" +
        "\u7A0B\u5E8F\u4F1A\u81EA\u52A8\u4ECE cURL \u91CC\u63D0\u53D6 Cookie \u548C User-Agent\u3002\r\n" +
        "\u91CD\u8981\uFF1A\u7F51\u5173\u628A\u767B\u5F55\u4F1A\u8BDD\u7ED1\u5B9A\u5230\u6D4F\u89C8\u5668\u7684 User-Agent\uFF0C\r\n" +
        "\u5FC5\u987B\u6574\u6BB5 cURL \u4E00\u8D77\u7C98\uFF0C\u53EA\u7C98 Cookie \u4E0D\u5E26 UA \u5FC5\u5B9A 401\u3002\r\n" +
        "\u82E5\u590D\u5236\u7684\u662F cURL\uFF08cmd\uFF09\u6216 PowerShell \u683C\u5F0F\uFF0C\u8BF7\u6539\u7528 cURL\uFF08bash\uFF09\u3002";

    readonly string _baseDir;
    // Everything the pet writes is split into two subfolders of the data
    // directory, so neither kind of file can be mistaken for the other:
    //   userdata\  - token.txt / state.ini / pet.log / wb-path.txt (user's
    //                own data; an uninstall only removes it on request)
    //   shots\     - the self-check PNGs (--shot, --selftest, --aboutsheet,
    //                --toksheet, --setsheet, --updsheet, --updfound). These
    //                are throwaway build artefacts: a fresh self-check wipes
    //                the folder first, and an uninstall always removes it.
    // The unpacked artwork (sprite.png, characters\, *.mp3, icon) stays in the
    // data folder root on purpose: it is a writable copy of an embedded
    // resource, not user data - see ExtractResources().
    readonly string _userDir;
    readonly string _shotsDir;
    readonly string _apiUrl;
    readonly int _pollMs;
    string _token;
    string _uid;
    string _ua = "";               // browser User-Agent the session is bound to
    bool _credIsCookie;            // credential is a Cookie header value, not a Bearer token
    // Test runs must be READ-ONLY with respect to the user's credential.
    // --credtest feeds synthetic fixtures through SetToken(), which persists via
    // SaveToken(): running the regression suite therefore REPLACED the live
    // token.txt with a fixture (found 2026-09-30 - a --credtest run left the
    // widget on 401 and the file held the 80-char CK fixture). Every test mode
    // sets this, so SaveToken() becomes a no-op and only the real "set token"
    // menu path can ever write the file.
    bool _suppressSave;

    // Subfolders of the data directory. Both are shared with the uninstaller,
    // which deletes them by name, so keep the strings in one place.
    public const string UserSubdir  = "userdata";
    public const string ShotsSubdir = "shots";
    // Files that used to live in the data folder root and now belong in
    // userdata\. Migrated on launch (MoveOldDataFiles) and looked up in both
    // places on read, so an upgrade never loses the user's credentials.
    public static readonly string[] UserFiles = new string[] {
        "token.txt", "state.ini", "pet.log", "wb-path.txt"
    };

    // pet.log lives in userdata\ now. Static so the SoundPool call sites (some
    // of which are static helpers) can reach it from a bare baseDir.
    public static string LogFileIn(string baseDir) {
        return Path.Combine(baseDir, UserSubdir, "pet.log");
    }

    // One list, so the mutex guard, the first-run dialog guard and the credential
    // guard can never drift apart.
    public static readonly string[] TestModes = new string[] {
        "--selftest", "--shot", "--credtest", "--credcheck",
        "--soundtest", "--soundprobe", "--soundsoak", "--soundab", "--simchain",
        "--abouttest", "--aboutsheet", "--toksheet", "--setsheet", "--updtest", "--updsheet",
        "--updnet", "--updfound", "--eggsound", "--eggsoundlive", "--aliascheck",
        "--wbtest", "--cryptest", "--pickertest", "--rescuetest", "--parsetest",
        "--credprobe"
    };
    public static bool IsTestMode(string[] args) {
        foreach (string a in args)
            if (Array.IndexOf(TestModes, a) >= 0) return true;
        return false;
    }

    // The subset of test modes that write PNGs into shots\. Kept separate from
    // TestModes because the wipe (below) must not fire for the logic-only modes
    // (--eggsound, --aliascheck, ...) that never touch that folder.
    public static readonly string[] SheetModes = new string[] {
        "--shot", "--selftest", "--aboutsheet", "--toksheet",
        "--setsheet", "--updsheet", "--updfound"
    };
    public static bool WantsSheets(string[] args) {
        foreach (string a in args)
            if (Array.IndexOf(SheetModes, a) >= 0) return true;
        return false;
    }

    // A self-check run starts from an empty folder: the last run's PNGs are
    // stale by definition, and leaving them behind made it impossible to tell
    // "the newest render" from "something an earlier build wrote". Called once,
    // before the first artefact is saved.
    public static void ClearShots(string shotsDir) {
        try {
            if (!Directory.Exists(shotsDir)) return;
            foreach (string f in Directory.GetFiles(shotsDir)) {
                try { File.Delete(f); } catch { }
            }
            foreach (string d in Directory.GetDirectories(shotsDir)) {
                try { Directory.Delete(d, true); } catch { }
            }
        } catch { }
    }

    // Resolve an artefact path the way the rendering modes expect: an argument
    // WITH a directory component is honoured as given (callers may point at an
    // absolute path), while a bare file name lands in shots\ rather than in
    // whatever the process CWD happens to be. Before this, a relative name went
    // to the data folder root, which is exactly where the clutter came from.
    public static string ResolveShot(string shotsDir, string arg, string fallback) {
        string name = string.IsNullOrEmpty(arg) ? fallback : arg;
        if (string.IsNullOrEmpty(name)) return shotsDir;
        try {
            if (Path.IsPathRooted(name)) return name;
            string dir = Path.GetDirectoryName(name);
            if (!string.IsNullOrEmpty(dir)) return name;   // explicit sub-path
            return Path.Combine(shotsDir, name);
        } catch { return Path.Combine(shotsDir, name); }
    }

    // Same rule for the two multi-file modes (--setsheet / --updsheet), which
    // take a DIRECTORY. An empty argument now means shots\<sub> rather than the
    // process CWD, which is what used to litter the data folder root.
    public static string ResolveSheetDir(string shotsDir, string arg) {
        const string sub = "sheets";
        try {
            if (string.IsNullOrEmpty(arg)) return Path.Combine(shotsDir, sub);
            if (Path.IsPathRooted(arg)) return arg;
            string parent = Path.GetDirectoryName(arg);
            if (!string.IsNullOrEmpty(parent)) return arg;
            return Path.Combine(shotsDir, arg);
        } catch { return Path.Combine(shotsDir, sub); }
    }

    Bitmap _flat, _flatFlip, _sprNormal, _sprRed, _canvas;
    double _scale = 1.0;
    int _w, _h;
    double[] _fx, _fy;
    double _cm = 8.0;            // widget HEIGHT in centimetres (width follows the art's aspect)
    int _headX, _headY;

    // Character roster. Every character ships two cuts: the full-body art
    // (1536x1024, tail and all) and the tail-less close-up (1024x1024, the
    // right 1024px of the full cut, so its quad is the full quad - 512 in x).
    // Quads are measured per file (see _tools/check_quads3.py); heads for the
    // close-ups reuse the full-cut anchor shifted by the same 512.
    static readonly CharSpec[] Chars = new CharSpec[] {
        new CharSpec("dsh", "\u5927\u80A5\u9C7C\uFF08\u53BB\u5C3E\u7248\uFF09", "sprite.png",
            new double[] { 550.3, 946.6, 980.9, 584.6 },
            new double[] { 706.3, 643.8, 861.4, 924.0 }, 686, 369),
        new CharSpec("dsh-full", "\u5927\u80A5\u9C7C\uFF08\u5B8C\u6574\u7248\uFF09", @"characters\sprite-dsh.png",
            new double[] { 1039.0, 1421.1, 1457.4, 1075.4 },
            new double[] { 693.9, 627.6, 836.1, 902.6 }, 1152, 369),
        new CharSpec("claude", "Claude\uFF08\u5B8C\u6574\u7248\uFF09", @"characters\sprite-claude.png",
            new double[] { 1039.0, 1421.1, 1457.4, 1075.4 },
            new double[] { 693.9, 627.6, 836.1, 902.6 }, 1152, 369),
        new CharSpec("claude-detailed", "Claude\uFF08\u53BB\u5C3E\u7248\uFF09", @"characters\sprite-claude_detailed.png",
            new double[] { 527.0, 908.9, 945.2, 563.3 },
            new double[] { 693.8, 627.4, 836.3, 902.7 }, 640, 369),
        new CharSpec("gemini", "Gemini\uFF08\u5B8C\u6574\u7248\uFF09", @"characters\sprite-gemini.png",
            new double[] { 1039.6, 1421.9, 1458.2, 1075.9 },
            new double[] { 694.1, 627.8, 836.9, 903.3 }, 1152, 369),
        new CharSpec("gemini-detailed", "Gemini\uFF08\u53BB\u5C3E\u7248\uFF09", @"characters\sprite-gemini_detailed.png",
            new double[] { 527.6, 909.9, 946.2, 563.9 },
            new double[] { 694.1, 627.8, 836.9, 903.3 }, 640, 369),
        new CharSpec("gpt", "GPT\uFF08\u5B8C\u6574\u7248\uFF09", @"characters\sprite-gpt.png",
            new double[] { 1039.3, 1421.0, 1457.3, 1075.7 },
            new double[] { 693.7, 627.2, 835.8, 902.3 }, 1152, 369),
        new CharSpec("gpt-detailed", "GPT\uFF08\u53BB\u5C3E\u7248\uFF09", @"characters\sprite-gpt_detailed.png",
            new double[] { 527.3, 909.0, 945.3, 563.7 },
            new double[] { 693.7, 627.2, 835.8, 902.3 }, 640, 369),
    };
    CharSpec _char;              // active character
    bool _sideRight;             // snapped to the bottom-right (art mirrored, faces left)
    double _floatMult = 4.0;     // damage-number size multiplier

    string _label = DEFAULT_LABEL;
    double _step = 1.0;          // credits per hurt animation, adjustable

    readonly List<Hit> _hits = new List<Hit>();
    readonly List<Floater> _floaters = new List<Floater>();
    readonly System.Windows.Forms.Timer _timer, _poll;
    volatile bool _dirty = true;
    volatile int _pollWant = 1;      // 0 = idle, 1 = auto (stepwise), 2 = snap to latest

    // The printed number is NOT tracked on its own - it is defined as
    // (_bookedBal - _testOffset), and _bookedBal is only ever moved inside the
    // cue firing code. Everything else works on the pair below, so the number
    // simply cannot drift away from the animation:
    //
    //   _bookedAt  balance the booking corresponds to
    //   _bookedBal number printed when the balance was _bookedAt
    //
    // When the server reports a new balance the whole difference
    // (_bookedAt - bal) is queued; it is then paid off one step per cue, so a
    // 5-credit drop plays five complete animations at the default step of 1.
    const double CueGapSec = 0.2;
    const int MaxCuesPerPoll = 40;   // ceiling on catch-up after a big jump

    double _realBal = double.NaN;
    double _totalBal = double.NaN;   // sum of cycleTotal, tray tooltip only
    double _bookedAt = double.NaN;
    double _bookedBal = double.NaN;
    double _testOffset = 0;          // display-only offset from test cues
    double _pending = 0;             // booked difference not yet charged
    double _pendingStep = 0;         // amount of the step being paid off
    double _dueGap;                  // seconds until the next cue may fire
    double _lastCueAmount = 0;       // amount of the most recent cue (diagnostics)
    float _floaterStep = 20f;        // vertical spacing of the number trail, in pixels

    double DrawnBalance {
        get {
            if (double.IsNaN(_bookedBal)) return double.NaN;
            double v = _bookedBal - _testOffset;
            return Math.Round(v < 0 ? 0 : v, 2);
        }
    }
    string _status = S_LOADING;
    volatile bool _connected;
    volatile string _lastPollResult = "";

    bool _drag; Point _dragStart, _winStart;
    bool _snapping; double _snapT; Point _snapFrom, _snapTo;
    // Reachability watchdog (1.2.19). A monitor unplugged while the widget
    // sat on it can leave the window parked off every screen, and
    // WM_DISPLAYCHANGE is neither guaranteed to arrive nor guaranteed to
    // arrive after the new topology is committed - so relying on that one
    // message left the pet invisible until restart (the "pulled the cable,
    // pet vanished" report, 2026-10-13). Hence: re-check hard for a moment
    // after a display change, then once in a while forever.
    const double WATCHDOG_MS      = 5000;   // idle backstop cadence
    const double RESCUE_BURST_MS  = 1500;   // fast re-checks after a change
    double _watchdogAccum;                  // ms since the last idle check
    double _rescueBurstLeft;                // ms of fast checking remaining
    // "Remember which screen" (1.2.19). true = on the next launch, come back to
    // the same corner of the same monitor the user last settled on; false = the
    // historical behaviour of always starting bottom-left of the primary screen.
    // Both branches still snap to a corner at launch - this only chooses WHICH
    // corner. Persisted as posmode in state.ini.
    bool _rememberScreen;
    // The last settled corner, normalised against the primary screen so it can
    // be rebuilt after a reboot even if monitor numbers shuffle. -1 = "never
    // recorded", which makes the first launch after an upgrade fall back to the
    // primary screen rather than guessing.
    bool _anchorValid;
    Rectangle _anchorWa;             // work area of the screen it was dropped on
    Rectangle _anchorPrimary;        // primary work area when it was recorded
    bool _anchorRight;               // which bottom corner
    byte[] _hitMap; int _hitW, _hitH;
    // shared app icon (DaFeiYu.ico when present): tray + dialogs
    public static Icon AppIcon;
    NotifyIcon _tray;
    ContextMenuStrip _menu;
    // No per-setting menu items any more: those live in SettingsDialog
    // (1.2.19). The menu is actions only, so there is nothing to keep checked.
    int _demoLeft;                   // cues left in a rehearsal run
    double _demoAmount = 1;

    // current frame's shake offset, so the readout and the floating numbers move
    // together with the character
    double _shakeX, _shakeY;
    // hit sound: one MCI alias per concurrent cue
    SoundPool _sound;
    // Easter-egg cue, opened lazily on the first 3.25 / 32.5 / 325 charge so
    // the extra MCI handles are not spent on a sound most sessions never
    // play. Independent pool: a new cue must never cut off the hit sound.
    SoundPool _soundEgg;
    bool _eggTried;
    bool _soundEnabled = true;
    int _soundMisses;                // consecutive Play() failures (see PlayHitSound)
    bool _soundWanted = true;
    volatile bool _noNetwork;        // set by the offline self-tests
    int _pollInFlight;               // only one balance request at a time
    double _bankedBal; bool _bankedSnap; bool _bankedValid;
    int _pollSeq;                    // poll counter, drives the periodic cross-check
    int _pollSkip;                   // poll ticks to sit out (401 backoff)
    bool _credAlertShown;            // "credential rejected" balloon, once per run
    // Cross-check the PaaS ledger on every user-initiated refresh plus once
    // every LedgerCheckEvery polls (~5 min at the default 5 s cadence). The
    // ledger is 82 KB, so it is not something to hit every single poll.
    const int LedgerCheckEvery = 60;
    // A rejected credential does not fix itself in five seconds; backing off
    // keeps the widget from hammering the gateway until the user re-pastes.
    const int CredBackoffTicks = 6;

    public WbPet(string baseDir, string[] args) {
        _baseDir = baseDir;
        _userDir = Path.Combine(baseDir, UserSubdir);
        _shotsDir = Path.Combine(baseDir, ShotsSubdir);
        _token   = Get("WBPET_TOKEN", "");
        _uid     = Get("WBPET_UID", "");
        _ua      = Get("WBPET_UA", "");
        _apiUrl  = Get("WBPET_API", "https://www.workbuddy.cn/billing/meter/get-user-resource-summary");
        _pollMs  = int.Parse(Get("WBPET_POLL_MS", "5000"));
        _cm      = double.Parse(Get("WBPET_CM", "8"), CultureInfo.InvariantCulture);
        _step    = double.Parse(Get("WBPET_STEP", "1"), CultureInfo.InvariantCulture);
        if (_step <= 0) _step = 1;
        _label   = Get("WBPET_LABEL", DEFAULT_LABEL);
        _soundWanted = Get("WBPET_SOUND", "1") != "0";
        _floatMult = double.Parse(Get("WBPET_FLOATMULT", "4"), CultureInfo.InvariantCulture);
        _sideRight = Get("WBPET_SIDE", "left") == "right";
        // Any test mode is read-only with respect to the credential file - see
        // the _suppressSave comment. Set here, before anything can call SetToken.
        _suppressSave = IsTestMode(args);
        // env / token.txt loaded credentials never went through SetToken
        _credIsCookie = _token.IndexOf(';') >= 0 ||
                        (_token.IndexOf('=') > 0 && !_token.EndsWith("="));

        _char = FindChar(Get("WBPET_CHAR", "dsh"));

        // hit sound (mp3) - opens a small pool of MCI aliases up front so the
        // very first cue has no lag and overlapping cues do not cut each other.
        // The path is built here from baseDir rather than handed over through
        // the environment: values crossing the PowerShell/C# boundary come back
        // ANSI-mangled when the folder name is not ASCII, while a path built in
        // this process stays correct.
        string sndPath = Path.Combine(baseDir, Get("WBPET_SOUND_FILE", "hit.mp3"));
        if (_soundWanted && File.Exists(sndPath)) {
            int vol = int.Parse(Get("WBPET_VOLUME", "80"));
            _sound = new SoundPool(sndPath, 4, vol, LogFileIn(baseDir));
            if (_sound.Failed) Log("sound pool failed: " + _sound.Error);
            else Log("sound ready: " + sndPath + " volume=" + vol);
        } else if (_soundWanted) {
            Log("sound file missing: " + sndPath + " (hit sound disabled)");
        }

        // The easter-egg cue is deliberately NOT pre-opened here. It is only
        // reachable at the three egg steps, so pre-opening it would spend four
        // MCI handles on a sound most sessions never play, and would give the
        // egg's own diagnostics a launch-time "ready" line that hides whether
        // routing actually worked. EggPool() opens it on first need instead.

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Text = "WorkBuddy PointPal";

        ReadState();                 // may override _char, _sideRight, sizes...
        _soundEnabled = _soundWanted;
        LoadCharacter();             // bitmaps + geometry for _char
        Relayout();
        PlaceOnStartup();
        BuildMenu();

        // First run: nothing configured a token, so ask once instead of leaving
        // the tablet stuck on "--". Cancelling keeps it offline.
        bool quietRun = WbPet.IsTestMode(args);
        if (string.IsNullOrEmpty(_token) && !quietRun && !File.Exists(TokenPath)) {
            // Same window as the tray menu's "set credential": a first-time
            // user should not meet a bare one-line box with no explanation.
            string k;
            if (AskCredential("", out k)) {
                if (k.Length > 0) {
                    SetToken(k);
                    Log("token saved on first run (length " + _token.Length + ")");
                }
            } else {
                Log("first run: no token entered, staying offline until one is set");
            }
        }

        _tray = new NotifyIcon();
        try {
            string icoPath = Path.Combine(baseDir, "DaFeiYu.ico");
            if (File.Exists(icoPath)) AppIcon = new Icon(icoPath);
        } catch (Exception ex) { Log("icon load failed: " + ex.Message); }
        _tray.Icon = AppIcon != null ? AppIcon : SystemIcons.Application;
        _tray.Text = "WB - " + (_status.Length > 40 ? _status.Substring(0, 40) : _status);
        _tray.ContextMenuStrip = _menu;
        _tray.Visible = true;
        _tray.DoubleClick += delegate { DemoCharge(_step * 5); };

        _timer = new System.Windows.Forms.Timer();
        _timer.Interval = int.Parse(Get("WBPET_TICK_MS", "33"));
        _timer.Tick += delegate { OnTick(); };
        _timer.Start();

        _poll = new System.Windows.Forms.Timer();
        _poll.Interval = _pollMs < 1000 ? 1000 : _pollMs;
        _poll.Tick += delegate {
            if (_noNetwork) return;
            // Backoff after a rejected credential: sit out a few ticks instead
            // of firing a request that is guaranteed to 401.
            if (_pollSkip > 0) { _pollSkip--; return; }
            _pollWant = 1;
        };
        _poll.Start();

        PushLayer();
    }

    static string Get(string n, string f) {
        string v = Environment.GetEnvironmentVariable(n);
        return string.IsNullOrEmpty(v) ? f : v;
    }

    // NOTE (1.2.18): a TryAutoDetectToken() used to sit here. It read
    // %USERPROFILE%\.workbuddy\token.txt and credentials.txt - the WorkBuddy
    // client's OWN data folder - and adopted whatever it found as the login
    // credential. It is gone for two reasons:
    //   1. It never worked. WorkBuddy keeps its session encrypted / in-process
    //      and neither file exists (verified on the dev machine 2026-10-12),
    //      so all it ever produced was a "no credential found" log line.
    //   2. It was the most malware-looking code in this file: an unsigned
    //      third-party app probing another application's config directory for
    //      a token. Two string literals are enough for a static scanner to
    //      draw the obvious conclusion.
    // Pasting the cURL into the credential dialog is the ONLY supported path.

    // Accepts a whole "Copy as cURL" command (recommended), a bare Cookie
    // value, "token", "Bearer token", or "uid:<credential>".
    // The web app authenticates with cookies and the gateway BINDS the session
    // to the exact browser User-Agent, which is why pasting the whole cURL
    // (cookie + UA in one go) is the reliable path.
    void SetToken(string raw) {
        string t = raw.Trim();
        if (t.StartsWith("curl", StringComparison.OrdinalIgnoreCase)) {
            // Scan EVERY header flag generically instead of anchor-hunting one
            // header per regex.
            //
            // Two independent defects lived here (verified 2026-09-30 against the
            // real .NET regex engine):
            //   1. The cookie was only sought behind cURL's own -b/--cookie FLAG,
            //      but Chrome/Firefox "Copy as cURL" send it as a "-H 'cookie:..'"
            //      HEADER. Nothing matched, the bearer branch missed too, and the
            //      credential came back empty -> the widget stayed on 401.
            //   2. Each header search anchored on a bare "-H", letting the leading
            //      "-H 'accept: ...'" swallow the later "-H 'user-agent: ...'".
            //
            // One tolerant pass over every "-X 'name: value'" pair fixes both and
            // cannot silently drop a header. See _docs/鍑瘉瑙ｆ瀽缂洪櫡_澶嶆牳涓庝慨澶峗v1.2.3.md.
            string cookie = null, bearer = null, ua = null, uid = null;
            foreach (Match m in Regex.Matches(t, "-(?:H|h|-header)\\s+(['\"])([A-Za-z][A-Za-z0-9_-]*):[ \\t]*(.*?)\\1",
                                              RegexOptions.Singleline)) {
                string name = m.Groups[2].Value.ToLowerInvariant();
                string val  = m.Groups[3].Value.Trim();
                if (val.Length == 0) continue;
                if (name == "cookie") cookie = val;
                else if (name == "authorization") bearer = Regex.Replace(val, "(?i)^bearer\\s+", "");
                else if (name == "user-agent") ua = val;
                else if (name == "x-user-id") uid = val;
            }
            if (cookie == null) {
                // cURL's own cookie flag: -b / --cookie (not a header)
                Match mb = Regex.Match(t, "(?:-b|--cookie)\\s*'([^']*)'");
                if (!mb.Success) mb = Regex.Match(t, "(?:-b|--cookie)\\s*\"([^\"]*)\"");
                if (mb.Success) cookie = mb.Groups[1].Value.Trim();
            }
            if (ua == null) {
                // -A / --user-agent shorthand
                Match mua = Regex.Match(t, "(?:-A|--user-agent)\\s*'([^']*)'");
                if (!mua.Success) mua = Regex.Match(t, "(?:-A|--user-agent)\\s*\"([^\"]*)\"");
                if (mua.Success) ua = mua.Groups[1].Value.Trim();
            }
            if (!string.IsNullOrEmpty(cookie)) {
                _token = cookie;
                _credIsCookie = true;
            } else if (!string.IsNullOrEmpty(bearer)) {
                _token = bearer;
                _credIsCookie = false;
            } else {
                Log("SetToken: cURL pasted but neither cookie nor Authorization found");
                return;
            }
            if (!string.IsNullOrEmpty(ua)) _ua = ua;
            if (!string.IsNullOrEmpty(uid)) _uid = uid;
            SaveToken();
            Log("token set from cURL (mode=" + (_credIsCookie ? "cookie" : "bearer") +
                ", length " + _token.Length + ", ua=" + (_ua.Length > 0 ? "set" : "empty") +
                ", uid=" + (_uid.Length > 0 ? "set" : "empty") + ")");
            return;
        }
        if (t.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase)) t = t.Substring(14).Trim();
        if (t.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase)) t = t.Substring(7).Trim();
        if (t.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) t = t.Substring(7).Trim();
        int colon = t.IndexOf(':');
        if (colon >= 8 && colon < 64 && t.Length > colon + 10 &&
            Regex.IsMatch(t.Substring(0, colon), "^[0-9a-fA-F-]+$")) {
            // "uid:credential" - the uid side is a short hex/uuid-like string
            _uid = t.Substring(0, colon).Trim();
            t = t.Substring(colon + 1).Trim();
        }
        _token = t;
        // cookie shape: contains ';' between pairs, or 'key=value' not at the end
        _credIsCookie = t.IndexOf(';') >= 0 ||
                        (t.IndexOf('=') > 0 && !t.EndsWith("="));
        SaveToken();
    }

    void BuildMenu() {
        _menu = new ContextMenuStrip();

        ToolStripMenuItem refresh = new ToolStripMenuItem(S_REFRESH);
        // A deliberate refresh must cut through the 401 backoff - otherwise the
        // user pastes a fresh credential and then waits 30 s for nothing.
        refresh.Click += delegate { _pollSkip = 0; _pollWant = 2; };
        _menu.Items.Add(refresh);

        ToolStripMenuItem test = new ToolStripMenuItem(S_TEST);
        test.Click += delegate { DemoCharge(_step); };
        _menu.Items.Add(test);

        // submenu: rehearse a bigger deduction to watch a run of consecutive cues
        ToolStripMenuItem demoItem = new ToolStripMenuItem(S_HELP);
        double[] demos = new double[] { 1, 5, 10, 20, 50 };
        foreach (double d in demos) {
            double v = d;
            ToolStripMenuItem it = new ToolStripMenuItem("-" + FmtAmt(v) +
                                                         "  (" + CueCount(v) + " \u6B21)");
            it.Click += delegate { DemoCharge(v); };
            demoItem.DropDownItems.Add(it);
        }
        demoItem.DropDownItems.Add(new ToolStripSeparator());
        ToolStripMenuItem demoCustom = new ToolStripMenuItem(S_CUSTOM);
        demoCustom.Click += delegate { AskDemo(); };
        demoItem.DropDownItems.Add(demoCustom);
        _menu.Items.Add(demoItem);

        _menu.Items.Add(new ToolStripSeparator());

        // Everything that used to sit between here and "about" moved into the
        // settings dialog (2026-10-13): size, step, damage-number size, screen
        // label, character, hit sound and the credential entry. The menu keeps
        // only the per-session ACTIONS, which is what it is for.
        ToolStripMenuItem settings = new ToolStripMenuItem(S_SETTINGS);
        settings.Click += delegate { ShowSettings(); };
        _menu.Items.Add(settings);

        // Second to last, directly above Quit, as a conventional place for it.
        ToolStripMenuItem about = new ToolStripMenuItem(S_ABOUT);
        about.Click += delegate { ShowAbout(); };
        _menu.Items.Add(about);

        ToolStripMenuItem quit = new ToolStripMenuItem(S_QUIT);
        quit.Click += delegate { Quit(); };
        _menu.Items.Add(quit);

        _menu.Opening += delegate { RefreshMenuChecks(); };
    }

    static string FmtAmt(double v) {
        return v == Math.Floor(v)
            ? v.ToString("0", CultureInfo.InvariantCulture)
            : v.ToString("0.##", CultureInfo.InvariantCulture);
    }
    string CueCount(double amount) {
        int n = (int)Math.Round(amount / _step);
        return n < 1 ? "1" : n.ToString(CultureInfo.InvariantCulture);
    }
    static string CmLabel(double v) {
        return v.ToString("0.#", CultureInfo.InvariantCulture) + " cm";
    }

    // The menu carries no checkable rows since 1.2.19 (they moved into the
    // settings dialog), so there is nothing left to refresh. Kept as a named
    // no-op because the Opening hook and a couple of tests still call it.
    void RefreshMenuChecks() {
    }

    // ------------------------------------------------------------- settings ---

    // A second copy of the pet was started and handed the wake over. Show that
    // we are already here instead of silently doing nothing: a tray balloon is
    // visible without a modal dialog and without touching the balance.
    public void Wake() {
        try {
            if (_tray != null) {
                _tray.BalloonTipTitle = "WorkBuddy PointPal";
                _tray.BalloonTipText = "\u684C\u5BA0\u5DF2\u7ECF\u5728\u8FD0\u884C\u4E86\uFF0C\u65E0\u9700\u91CD\u590D\u542F\u52A8\u3002";
                _tray.BalloonTipIcon = ToolTipIcon.Info;
                _tray.ShowBalloonTip(3000);
            }
            Log("wake: second instance handed over to this one");
        } catch { }
    }

    // Rehearsal: queue ceil(amount / step) cues. They walk the printed number
    // down through _testOffset, leaving the real balance and the booking alone,
    // so a refresh afterwards simply restores the true value.
    void DemoCharge(double amount) {
        if (amount < _step) amount = _step;
        int cues = (int)Math.Round(amount / _step);
        if (cues < 1) cues = 1;
        if (cues > 500) cues = 500;
        _demoLeft = cues;
        _demoAmount = _step;
        Log("demo charge: -" + FmtAmt(amount) + " -> " + cues + " cue(s), " +
            CueGapSec.ToString("0.#") + "s apart");
        _dirty = true;
    }

    void AskDemo() {
        using (InputDialog d = new InputDialog(S_HELPT, S_HELPP, "", "10")) {
            if (d.ShowDialog(this) != DialogResult.OK) return;
            double v;
            if (double.TryParse(d.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v > 0)
                DemoCharge(v);
            else
                Log("demo amount rejected: '" + d.Value + "'");
        }
    }

    void SetStep(double v) {
        if (v < 0.01) v = 0.01;
        if (v > 100000) v = 100000;
        _step = v;
        SaveState();
        Log("step set to " + FmtAmt(v));
    }

    void AskStep() {
        using (InputDialog d = new InputDialog(S_STEPT, S_STEPP, "",
                                               _step.ToString("0.##", CultureInfo.InvariantCulture))) {
            if (d.ShowDialog(this) != DialogResult.OK) return;
            double v;
            if (double.TryParse(d.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v > 0)
                SetStep(v);
            else
                Log("custom step rejected: '" + d.Value + "'");
        }
    }

    // The About box. Version comes from the exe's own assembly metadata, which
    // _asm_pet.cs stamps and _tools/_sync_version.py keeps in step with
    // setup.cs - so the number shown here is the number Explorer shows, and it
    // cannot silently drift from the installer's. Only the 3-part form is
    // printed: the stored value is "1.2.9.0" and the trailing ".0" is noise.
    //
    // Everything that used to be its own menu row now lives in one window
    // (2026-10-13). The dialog is handed the current values and a set of
    // callbacks back into the pet, so the pet keeps owning all state.
    void ShowSettings() {
        using (SettingsDialog d = new SettingsDialog(
                   S_SETTINGST, _cm, _step, _floatMult, _label, _char.Id,
                   _soundWanted, _rememberScreen, Chars, SettingsCredBlock())) {
            d.SetCm = SetCm;
            d.SetStep = SetStep;
            d.SetFloatMult = SetFloatMult;
            d.SetLabel = delegate (string v) { SetLabel(v); };
            d.SetChar = SetCharacter;
            d.SetSound = delegate (bool on) { if (on != _soundWanted) ToggleSound(); };
            d.SetRememberScreen = SetRememberScreen;
            d.AskToken = AskToken;
            // The credential page owns the polling, the pet owns the probe: one
            // request path, the same headers, the same idea of "accepted".
            d.CredentialProbe = new Func<int>(ProbeCredential);
            d.CredBlockSource = new Func<string>(SettingsCredBlock);
            d.ShowDialog(this);
        }
    }

    // One cheap request whose only job is to answer "is the stored credential
    // still accepted?". Returns 0 = yes, 1 = rejected (that includes "nothing
    // stored"), 2 = the question could not be asked (transport failure).
    // Deliberately does NOT touch _realBal / _bookedAt / the tray: a probe must
    // never move the number, only report on the credential.
    int ProbeCredential() {
        if (string.IsNullOrEmpty(_token)) return 1;
        if (_noNetwork) return 0;              // offline self-tests stay offline
        try {
            string body = FetchPost(_apiUrl, "{}");
            return double.IsNaN(ParseCredits(body)) ? 2 : 0;
        } catch (Exception ex) {
            return ex.Message.IndexOf("401") >= 0 ? 1 : 2;
        }
    }

    // The text on the credential tab. Short: the long "how do I get one" walk
    // through is the button inside the credential dialog itself.
    string SettingsCredBlock() {
        bool has = !string.IsNullOrEmpty(_token);
        return S_SETTOKEN + "\uFF1A" + (has ? "\u5DF2\u8BBE\u7F6E" : "\u672A\u8BBE\u7F6E") + "\r\n\r\n" +
               "\u51ED\u8BC1\u4FDD\u5B58\u5728\u672C\u673A\u6570\u636E\u76EE\u5F55\uFF0C" +
               "\u7ECF DPAPI \u672C\u7528\u6237\u52A0\u5BC6\uFF0C\u53EF\u5728\u4E0B\u65B9\u91CD\u65B0\u8BBE\u7F6E\u3002\r\n" +
               "\u8FC7\u671F\u540E\u4F1A\u81EA\u52A8\u63D0\u9192\uFF0C\u4E5F\u53EF\u70B9\u4E0B\u65B9\u300C"
             + "\u6D4B\u8BD5\u5F53\u524D\u51ED\u8BC1\u300D\u968F\u65F6\u68C0\u67E5\u3002";
    }

    // This used to be a plain MessageBox. It is now a small dialog because the
    // check-update button has to live somewhere, and a MessageBox cannot host
    // a third button that is neither OK nor Cancel (asked for 2026-10-02).
    void ShowAbout() {
        bool wantCheck;
        using (AboutDialog d = new AboutDialog(AboutBody(), S_ABOUTT, S_UPDATE)) {
            d.ShowDialog(this);
            wantCheck = d.UpdateRequested;
        }
        // About is closed by now, so the notice owns the screen alone.
        if (wantCheck) CheckForUpdates();
    }

    // "妫€鏌ユ洿鏂? pressed from the About dialog: close About, show the
    // "checking..." notice, then show the outcome. The notice is deliberately
    // its own top-most window rather than a MessageBox, because a modal
    // MessageBox would have to be dismissed by the user before the result
    // could appear - and the whole point is that it closes by itself.
    void CheckForUpdates() {
        Stopwatch sw = Stopwatch.StartNew();

        // The notice is shown on its own thread so the network call here can
        // block without freezing the UI. A borderless form pumped by
        // Application.Run on a worker keeps it painted and draggable-looking
        // for the whole wait, without needing a second message loop here.
        BusyNotice notice = new BusyNotice(S_UPDCHK);
        Thread uiThread = new Thread(delegate () {
            try { Application.Run(notice); } catch { }
        });
        uiThread.IsBackground = true;      // never keep the process alive
        uiThread.SetApartmentState(ApartmentState.STA);
        uiThread.Start();

        // Give the window a moment to actually appear before the (possibly
        // instant) answer tries to close it.
        Thread.Sleep(120);

        string latest = null;
        try { latest = FetchLatestTag(); } catch { latest = null; }

        // Never flash by: hold the notice for at least UPD_MIN_MS so a fast or
        // cached answer still reads as "it did something".
        int wait = UPD_MIN_MS - (int)sw.ElapsedMilliseconds;
        if (wait > 0) Thread.Sleep(wait);

        // Tear the notice down on its own thread.
        try { notice.BeginInvoke((MethodInvoker)delegate { notice.Close(); }); } catch { }
        try { uiThread.Join(1000); } catch { }

        if (string.IsNullOrEmpty(latest)) {
            MessageBox.Show(this, S_UPDFAILP, S_UPDFAIL,
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        string cur = AboutVersion();
        if (CompareVersions(latest, cur) <= 0) {
            MessageBox.Show(this, S_UPDSAMEP + "\r\n\r\n" + S_UPDCUR + "\uFF1A" + AboutVersionLabel(),
                            S_UPDSAME, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        string msg = S_UPDNEW + "\uFF1A" + latest + "\r\n" +
                     S_UPDCUR + "\uFF1A" + cur;
        // A MessageBox cannot relabel its buttons, so "OK / Cancel" would read
        // as "confirm / decline the update" when the only effect of OK is
        // opening a browser tab (reported 2026-10-02). Use a real form whose
        // captions say what they do, plus a line spelling it out.
        using (UpdateFoundDialog dlg = new UpdateFoundDialog(
                   S_UPDFOUND, msg, S_UPDNOTE, S_UPDOPEN, S_UPDLATER)) {
            dlg.ShowDialog(this);
            // "later" leaves everything untouched - no download, no file write.
            if (dlg.OpenRequested) OpenReleasePage(latest);
        }
    }

    // --updtest: prove the update-check logic on the SHIPPED binary. Nothing
    // here touches the network, so it runs offline and is safe in CI. The
    // version compare is the part worth pinning: a string compare would rank
    // "1.2.9" above "1.2.13" and quietly tell every user they are up to date.
    public void RunUpdateTest() {
        Console.WriteLine("update-check logic (--updtest)");

        int bad = 0, ok = 0;
        CheckVersion("1.2.13", "1.2.9",   1, ref ok, ref bad);   // string-compare trap
        CheckVersion("1.2.9",  "1.2.13", -1, ref ok, ref bad);
        CheckVersion("v1.2.13","1.2.13",  0, ref ok, ref bad);   // leading v
        CheckVersion("v1.2.14","1.2.13",  1, ref ok, ref bad);
        CheckVersion("1.2",    "1.2.0",   0, ref ok, ref bad);   // unequal depth
        CheckVersion("1.2.0.1","1.2",     1, ref ok, ref bad);   // 4th segment counts
        CheckVersion("1.2.0.0","1.2",     0, ref ok, ref bad);   // ...but trailing 0 does not
        CheckVersion("1.2.13.0","1.2.13", 0, ref ok, ref bad);   // the exe's own "x.y.z.0" shape
        CheckVersion("1.10.0", "1.9.0",   1, ref ok, ref bad);   // numeric not lexical
        CheckVersion("2.0.0",  "1.99.99", 1, ref ok, ref bad);
        CheckVersion("release-1.2.13","1.2.13", 0, ref ok, ref bad);
        CheckVersion("1_2_13", "1.2.13",  0, ref ok, ref bad);
        CheckVersion("1.2.13", "1.2.13",  0, ref ok, ref bad);
        CheckVersion("",       "1.2.13", -1, ref ok, ref bad);   // garbage -> older

        Console.WriteLine("  versionCompare ok=" + ok + " bad=" + bad);

        // The URL the button opens must be a real releases page, not the API.
        string tagUrl = "https://github.com/" + UPD_OWNER + "/" + UPD_REPO + "/releases/tag/v1.2.14";
        string latUrl = "https://github.com/" + UPD_OWNER + "/" + UPD_REPO + "/releases/latest";
        Console.WriteLine("  tagUrl=" + tagUrl);
        Console.WriteLine("  latestUrl=" + latUrl);
        Console.WriteLine("  noApiInUrls=" +
            (tagUrl.IndexOf("api.github.com") < 0 && latUrl.IndexOf("api.github.com") < 0));
        Console.WriteLine("  minNoticeMs=" + UPD_MIN_MS);
        Console.WriteLine("  noticeFloorsAt0_7s=" + (UPD_MIN_MS >= 700));

        // The About dialog must expose the button and hand back the request.
        using (AboutDialog d = new AboutDialog(AboutBody(), S_ABOUTT, S_UPDATE)) {
            bool hasUpd = false;
            foreach (Control c in d.Controls) {
                Button b = c as Button;
                if (b != null && b.Text == S_UPDATE) { hasUpd = true; break; }
            }
            Console.WriteLine("  aboutHasUpdateBtn=" + hasUpd);
            Console.WriteLine("  aboutUpdateRequestedDefault=" + d.UpdateRequested);
            Console.WriteLine("  aboutTitle=" + d.Text);
            // CenterScreen, not CenterParent: the parent is the pet, which lives
            // in a screen corner, so CenterParent would exile this dialog to that
            // same corner. Assert it here so a revert is caught offline.
            Console.WriteLine("  aboutCenteredOnScreen=" +
                (d.StartPosition == FormStartPosition.CenterScreen));
        }
        Console.WriteLine(bad == 0 ? "UPD TEST: ALL PASS" : "UPD TEST: FAILURES=" + bad);
    }

    static void CheckVersion(string a, string b, int expect, ref int ok, ref int bad) {
        int got = CompareVersions(a, b);
        if (Math.Sign(got) == Math.Sign(expect)) { ok++; return; }
        bad++;
        Console.WriteLine("  FAIL " + a + " vs " + b +
                          " expect " + expect + " got " + got);
    }

    // The notice window: borderless, top-most, no buttons. It is closed by
    // CheckForUpdates once the answer is in, so the user never has to click it.
    sealed class BusyNotice : Form {
        public BusyNotice(string text) {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = false;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.White;

            Label l = new Label();
            l.Text = text;
            l.AutoSize = false;                 // authoritative Bounds, as elsewhere
            l.TextAlign = ContentAlignment.MiddleCenter;
            l.Font = new Font("Microsoft YaHei", 10F);
            Controls.Add(l);

            // MeasureText returns physical pixels, so this height must not be
            // scaled a second time.
            Size need = TextRenderer.MeasureText(text, l.Font);
            int w = Math.Max(InputDialog.Ps(200), need.Width + InputDialog.Ps(44));
            int h = Math.Max(InputDialog.Ps(60), need.Height + InputDialog.Ps(36));
            ClientSize = new Size(w, h);
            l.Bounds = new Rectangle(0, 0, w, h);
            // A hairline border so a white box on a white page still reads as
            // a window rather than a rendering glitch.
            try { Region = null; } catch { }
        }
        protected override void OnPaint(PaintEventArgs e) {
            base.OnPaint(e);
            using (Pen p = new Pen(Color.FromArgb(255, 180, 180, 180)))
                e.Graphics.DrawRectangle(p, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        }
        protected override void OnShown(EventArgs e) {
            base.OnShown(e);
            // Without this the borderless window can paint late and show as a
            // blank white rectangle for the first frames.
            Invalidate();
            Update();
        }
    }

    // git's tag conventions to tolerate: "v1.2.13", "1.2.13", "release-1.2.13".
    // Every run of digits is a segment - NOT just the first three. Capping at
    // three looked harmless but silently collapsed "1.2.0.1" to "1.2.0", which
    // then compared EQUAL to "1.2" and would have hidden a real update.
    // (--updtest caught exactly that on 2026-10-02.)
    //
    // A trailing non-numeric suffix ("1.2.13-beta") is deliberately ignored:
    // for a manual "is there something newer?" prompt, treating a prerelease
    // as the release is the safer failure direction than treating it as older.
    static string NormalizeVersion(string s) {
        if (string.IsNullOrEmpty(s)) return "";
        MatchCollection ms = Regex.Matches(s, @"\d+");
        if (ms.Count == 0) return "";
        StringBuilder sb = new StringBuilder();
        foreach (Match m in ms) {
            if (sb.Length > 0) sb.Append('.');
            sb.Append(m.Value);
        }
        return sb.ToString();
    }

    // Segment-wise integer compare. A string compare would rank "1.2.9" above
    // "1.2.13", which is exactly the sort of quiet wrongness that erodes trust
    // in an update prompt.
    static int CompareVersions(string a, string b) {
        string[] xa = NormalizeVersion(a).Split('.');
        string[] xb = NormalizeVersion(b).Split('.');
        int n = Math.Max(xa.Length, xb.Length);
        for (int i = 0; i < n; i++) {
            int va = 0, vb = 0;
            if (i < xa.Length) int.TryParse(xa[i], out va);
            if (i < xb.Length) int.TryParse(xb[i], out vb);
            if (va != vb) return va < vb ? -1 : 1;
        }
        return 0;
    }

    static void OpenReleasePage(string tag) {
        string url = string.IsNullOrEmpty(tag)
            ? "https://github.com/" + UPD_OWNER + "/" + UPD_REPO + "/releases/latest"
            : "https://github.com/" + UPD_OWNER + "/" + UPD_REPO + "/releases/tag/" + tag;
        try {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        } catch {
            try { Process.Start("explorer.exe", "\"" + url + "\""); } catch { }
        }
    }

    // Resolve the newest release tag WITHOUT spending GitHub API quota.
    //
    // github.com answers /releases/latest with a 302 whose Location header ends
    // in /releases/tag/<tag>. That is served by github.com itself, so unlike
    // api.github.com/repos/.../releases/latest it is not subject to the
    // 60-requests-per-hour-per-IP anonymous limit. Verified 2026-10-02 against
    // cli/cli (-> v2.102.0) and MeteorNOX/DeepSeek-Balance-Whale-Widget.
    //
    // A redirected HEAD cannot be read directly through HttpClient (it follows
    // the redirect transparently), so the redirect is disabled on a dedicated
    // handler and the Location header is read off the 302 itself.
    static string FetchLatestTag() {
        return FetchLatestTagFor(UPD_OWNER, UPD_REPO);
    }

    static string FetchLatestTagFor(string owner, string repo) {
        // Two transports, and this order matters.
        //
        // Accelerator tools write the system (WinINET) proxy in their "system
        // proxy" mode, and that is the only way out while it is active: a
        // direct socket just times out (reported 2026-10-02 on v1.2.13, where
        // the browser could open GitHub and the pet never could).
        //
        // The same tools LEAVE THAT SETTING BEHIND when the user switches to
        // TUN mode. The port stops listening and every proxied connection is
        // refused instantly (measured on this machine 2026-10-02 on v1.2.16,
        // where forcing the proxy again broke the check a second time).
        //
        // So: proxy first, because a refusal costs milliseconds, while a direct
        // attempt against a blocked network costs a full timeout. One wasted
        // timeout on a proxy-only machine is the price of working on both,
        // and both beats being broken.
        foreach (bool viaProxy in new bool[] { true, false }) {
            string tag = FetchTagOnce(owner, repo, viaProxy);
            if (!string.IsNullOrEmpty(tag)) return tag;
        }
        return null;
    }

    // One full attempt over ONE transport: the redirect route, then the API
    // fallback when the redirect answered with something that carried no tag.
    static string FetchTagOnce(string owner, string repo, bool viaProxy) {
        string url = "https://github.com/" + owner + "/" + repo + "/releases/latest";
        try {
            using (HttpClientHandler h = new HttpClientHandler()) {
                h.AllowAutoRedirect = false;   // we want the 302, not its target
                h.UseCookies = false;
                // viaProxy = true keeps HttpClientHandler's default, i.e. the
                // system proxy. .NET Framework never reads the HTTP_PROXY
                // environment variable (only .NET Core and later do), so the
                // local interceptor an earlier version tried to dodge was never
                // in play on this path anyway.
                try { h.UseProxy = viaProxy; } catch { }
                using (HttpClient c = new HttpClient(h)) {
                    c.Timeout = TimeSpan.FromSeconds(viaProxy ? 8 : 10);
                    c.DefaultRequestHeaders.TryAddWithoutValidation(
                        "User-Agent", "WorkBuddyPointPal/" + AboutVersionLabel());
                    using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Head, url))
                    using (HttpResponseMessage resp = c.SendAsync(req).GetAwaiter().GetResult()) {
                        int code = (int)resp.StatusCode;
                        // 302 is the normal case. Some CDN edges answer 301.
                        if (code == 301 || code == 302 || code == 303 || code == 307) {
                            Uri loc = resp.Headers.Location;
                            if (loc != null) {
                                Match m = Regex.Match(loc.ToString(), @"/releases/tag/([^/?#]+)");
                                if (m.Success) return Uri.UnescapeDataString(m.Groups[1].Value);
                            }
                        }
                        // Anything else (404 "no release yet" included) falls
                        // through: the API probe can tell "no releases" apart
                        // from "not found".
                    }
                }
            }
        } catch { }
        return FetchTagViaApiOnce(owner, repo, viaProxy);
    }

    // Fallback only. This one DOES spend API quota, so it runs second. Both
    // this and the redirect route are parameterised by transport so the caller
    // can retry the whole thing the other way round.
    static string FetchTagViaApiOnce(string owner, string repo, bool viaProxy) {
        string url = "https://api.github.com/repos/" + owner + "/" + repo + "/releases/latest";
        try {
            using (HttpClientHandler h = new HttpClientHandler()) {
                h.UseCookies = false;
                try { h.UseProxy = viaProxy; } catch { }
                using (HttpClient c = new HttpClient(h)) {
                    c.Timeout = TimeSpan.FromSeconds(viaProxy ? 8 : 10);
                    c.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/vnd.github+json");
                    c.DefaultRequestHeaders.TryAddWithoutValidation(
                        "User-Agent", "WorkBuddyPointPal/" + AboutVersionLabel());
                    string body = c.GetStringAsync(url).GetAwaiter().GetResult();
                    Match m = Regex.Match(body, "\"tag_name\"\\s*:\\s*\"([^\"]+)\"");
                    if (m.Success) return Uri.UnescapeDataString(m.Groups[1].Value);
                }
            }
        } catch { }
        return null;
    }

    // Split out so --abouttest can inspect the exact text without a modal
    // dialog blocking the (headless) test process.
    string AboutBody() {
        StringBuilder sb = new StringBuilder();
        sb.Append("WorkBuddy PointPal ").Append(AboutVersionLabel()).Append("\r\n");
        // Chinese is written as \uXXXX, matching every other string in this
        // file: wb_pet.cs is compiled without /codepage:65001, so a literal
        // multi-byte character here would be a real mojibake risk.
        sb.Append("\u4E00\u4E2A\u4F4F\u5728\u684C\u9762\u89D2\u843D\u7684\u79EF\u5206\u5C0F\u4F19\u4F34");   // a credits buddy in the desktop corner
        sb.Append("\uFF0C\u5B9E\u65F6\u663E\u793A WorkBuddy \u79EF\u5206\u4F59\u989D\u3002\r\n\r\n");        // showing your live WorkBuddy balance
        sb.Append("\u4F5C\u8005\uFF1ARealfie\r\n");                                                      // author
        sb.Append("\u7F16\u6392\uFF1A\u57FA\u4E8E\u5F00\u6E90\u9879\u76EE DSH \u4F59\u989D\u684C\u5BA0");
        sb.Append("\u6539\u7F16\r\n\r\n");                                                               // adapted from the DSH pet
        sb.Append("\u672C\u7A0B\u5E8F\u662F\u975E\u5B98\u65B9\u7684\u7B2C\u4E09\u65B9\u5C0F\u5DE5\u5177");
        sb.Append("\uFF0C\u4E0E WorkBuddy \u5B98\u65B9\u65E0\u5173\uFF0C\u4E5F\u672A\u83B7\u5B98\u65B9\u6388\u6743\u3002\r\n\r\n");  // unofficial third-party tool
        sb.Append("\u6570\u636E\u76EE\u5F55\uFF1A\r\n").Append(_baseDir).Append("\r\n\r\n");              // data folder
        sb.Append("\u8FD0\u884C\u65B9\u5F0F\uFF1A\u4EE5\u4F60\u7684\u8EAB\u4EFD\u672C\u5730\u8FD0\u884C");
        sb.Append("\uFF0C\u4E0D\u4F1A\u4E0A\u4F20\u4EFB\u4F55\u6570\u636E\uFF1B\r\n");                     // runs locally as you, uploads nothing
        sb.Append("\u79EF\u5206\u4EC5\u4ECE WorkBuddy \u5B98\u65B9\u63A5\u53E3\u8BFB\u53D6\u3002");        // credits are read from the official API only
        return sb.ToString();
    }

    // "1.2.9" out of "1.2.9.0"; "1.2.19.5" out of "1.2.19.5".
    // The 4th segment used to be a build marker that was never shown, so a
    // patch release could not be told apart from the mainline one. Since
    // 1.2.19.5 it IS shown - but only when it is non-zero, which keeps every
    // earlier number printing exactly as it used to (".0" stays invisible).
    // The update check reads this same string, and CompareVersions is
    // length-tolerant, so a 4-part tag compares correctly against it.
    static string AboutVersion() {
        try {
            Version v = Assembly.GetExecutingAssembly().GetName().Version;
            if (v != null) {
                string s = v.Major + "." + v.Minor + "." + v.Build;
                if (v.Revision > 0) s += "." + v.Revision;
                return s;
            }
        } catch { }
        return "?";
    }

    // What the About box prints: the numeric version plus the build tag, e.g.
    // "1.2.16.zc". The tag rides in the assembly's 4th segment (which
    // must stay numeric or the assembly refuses to load), so it is appended
    // here AND exposed through VersionTag - there is one place to change it.
    static string AboutVersionLabel() {
        return AboutVersion() + (VersionTag.Length > 0 ? "." + VersionTag : "");
    }

    // --aboutsheet <png>: paint the About body exactly as the message box would
    // lay it out, so the wording and wrapping can be reviewed without clicking
    // through a modal dialog.
    // --aboutsheet <png>: render the About dialog itself, so the wording, the
    // wrapping and the button row can be reviewed without clicking through a
    // modal. This used to hand-draw a mock of a MessageBox; now that the box is
    // a real form with a third button, painting the form is the only way the
    // picture can be trusted to match what the user gets.
    public void SaveAboutSheet(string path) {
        using (AboutDialog d = new AboutDialog(AboutBody(), S_ABOUTT, S_UPDATE)) {
            d.StartPosition = FormStartPosition.Manual;
            d.Location = new Point(-10000, -10000);
            d.Show();
            // Pump until the controls have actually been placed and painted; a
            // single DoEvents() captures the window mid-layout (blank form,
            // children still at 0,0). Bounded so a headless session cannot spin.
            DateTime deadline = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < deadline) {
                Application.DoEvents();
                System.Threading.Thread.Sleep(30);
                break;   // one settle pass is enough for a static dialog
            }
            Console.WriteLine("aboutlayout: client=" + d.ClientSize.Width + "x" + d.ClientSize.Height);
            using (Bitmap b = new Bitmap(d.Width, d.Height)) {
                d.DrawToBitmap(b, new Rectangle(0, 0, d.Width, d.Height));
                b.Save(path, ImageFormat.Png);
            }
            d.Close();
        }
        Console.WriteLine("aboutsheet written: " + path);
    }

    // --updsheet <dir>: render the update-check UI (the "checking..." notice and
    // both possible answers) so the wording can be reviewed without a network
    // round-trip or a click-through. Purely local.
    public void SaveUpdateSheet(string dir) {
        try { if (!Directory.Exists(dir)) Directory.CreateDirectory(dir); } catch { }

        BusyNotice n = new BusyNotice(S_UPDCHK);
        n.StartPosition = FormStartPosition.Manual;
        n.Location = new Point(-10000, -10000);
        n.Show();
        Application.DoEvents();
        using (Bitmap b = new Bitmap(n.Width, n.Height)) {
            n.DrawToBitmap(b, new Rectangle(0, 0, n.Width, n.Height));
            b.Save(Path.Combine(dir, "upd_checking.png"), ImageFormat.Png);
        }
        Console.WriteLine("notice client=" + n.ClientSize.Width + "x" + n.ClientSize.Height);
        n.Close();

        Console.WriteLine("strings:");
        Console.WriteLine("  checking = " + S_UPDCHK);
        Console.WriteLine("  same     = " + S_UPDSAME);
        Console.WriteLine("  samemsg  = " + S_UPDSAMEP);
        Console.WriteLine("  found    = " + S_UPDFOUND);
        Console.WriteLine("  new      = " + S_UPDNEW);
        Console.WriteLine("  current  = " + S_UPDCUR);
        Console.WriteLine("  open     = " + S_UPDOPEN);
        Console.WriteLine("  fail     = " + S_UPDFAIL);
        Console.WriteLine("  failmsg  = " + S_UPDFAILP);
        Console.WriteLine("updsheet written: " + dir);
    }

    // --updfound <png>: render the "new version found" dialog itself, so the
    // button captions and the note line can be reviewed without waiting for a
    // real release to appear. Purely local - no network. The layout assertion
    // rides along because the note is the line whose clipping a screenshot
    // hides: a dropped sentence just reads as "that's all there is".
    public void SaveUpdateFoundSheet(string path) {
        // A deliberately fictional version, so a screenshot can never be
        // mistaken for a real "update available" notification.
        string body = S_UPDNEW + "\uFF1A" + "v9.9.9" + "\r\n" +
                      S_UPDCUR + "\uFF1A" + AboutVersionLabel();
        using (UpdateFoundDialog d = new UpdateFoundDialog(
                   S_UPDFOUND, body, S_UPDNOTE, S_UPDOPEN, S_UPDLATER)) {
            d.StartPosition = FormStartPosition.Manual;
            d.Location = new Point(-10000, -10000);
            d.Show();
            DateTime deadline = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < deadline) {
                Application.DoEvents();
                System.Threading.Thread.Sleep(30);
                break;   // one settle pass is enough for a static dialog
            }
            Console.WriteLine("updfound: " + d.LayoutReport());
            using (Bitmap b = new Bitmap(d.Width, d.Height)) {
                d.DrawToBitmap(b, new Rectangle(0, 0, d.Width, d.Height));
                b.Save(path, ImageFormat.Png);
            }
            d.Close();
        }
        Console.WriteLine("updfound written: " + path);
    }

    // --updnet <owner/repo>: hit the REAL network once and print what the
    // redirect route resolved. This is the only way to prove the discovery
    // method still works against live github.com, so it is kept as an explicit
    // opt-in probe rather than folded into --updtest (which must stay offline).
    public void RunUpdateNetProbe(string target) {
        string owner = UPD_OWNER, repo = UPD_REPO;
        if (!string.IsNullOrEmpty(target) && target.IndexOf('/') > 0) {
            int sl = target.IndexOf('/');
            owner = target.Substring(0, sl);
            repo = target.Substring(sl + 1);
        }
        Console.WriteLine("update network probe");
        Console.WriteLine("  target    = " + owner + "/" + repo);
        Console.WriteLine("  local ver = " + AboutVersion());

        string url = "https://github.com/" + owner + "/" + repo + "/releases/latest";
        Console.WriteLine("  HEAD      = " + url);
        string tag = FetchLatestTagFor(owner, repo);
        Console.WriteLine("  resolved  = " + (string.IsNullOrEmpty(tag) ? "(none)" : tag));
        if (!string.IsNullOrEmpty(tag)) {
            Console.WriteLine("  compare   = " + CompareVersions(tag, AboutVersion()) +
                              "  (1 = newer available)");
            Console.WriteLine("  page      = https://github.com/" + owner + "/" + repo +
                              "/releases/tag/" + tag);
        }
        Console.WriteLine(string.IsNullOrEmpty(tag) ? "UPD NET: NO RESULT" : "UPD NET: OK");
    }

    // --toksheet <png>: render the credential dialog itself, so a layout or DPI
    // regression in it can be reviewed without a GUI session. This is the same
    // window both first run and the tray menu present.
    public void SaveTokenSheet(string path) {
        // Same factory the real prompts use, so the picture cannot show a
        // layout the user will never see.
        using (InputDialog d = NewCredentialDialog("")) {
            d.StartPosition = FormStartPosition.Manual;
            d.Location = new Point(-10000, -10000);
            d.Show();
            // A single DoEvents() pumped the message queue only once and the
            // capture then caught the window mid-layout (blank form, children
            // still at 0,0). Pump until the controls have actually been placed
            // and painted, with a wall-clock ceiling so a headless session can
            // never spin here forever.
            DateTime deadline = DateTime.UtcNow.AddSeconds(2);
            while (DateTime.UtcNow < deadline) {
                Application.DoEvents();
                System.Threading.Thread.Sleep(30);
                if (d.LayoutReport().IndexOf("promptFits=True") >= 0) break;            }
            Console.WriteLine("toklayout: " + d.LayoutReport());
            using (Bitmap b = new Bitmap(d.Width, d.Height)) {
                d.DrawToBitmap(b, new Rectangle(0, 0, d.Width, d.Height));
                b.Save(path, ImageFormat.Png);
            }
            d.Close();
        }
        Console.WriteLine("toksheet written: " + path);
    }

    // --setsheet <dir>: render the settings dialog once per tab, so the layout
    // of a window that is otherwise only reachable by right-clicking the pet
    // can be reviewed offline at the real DPI. Purely local.
    public void SaveSettingsSheet(string dir) {
        try { if (!Directory.Exists(dir)) Directory.CreateDirectory(dir); } catch { }
        string[] names = new string[] { "set_display.png", "set_charge.png", "set_cred.png" };
        for (int tab = 0; tab < names.Length; tab++) {
            using (SettingsDialog d = new SettingsDialog(
                       S_SETTINGST, _cm, _step, _floatMult, _label, _char.Id,
                       _soundWanted, _rememberScreen, Chars, SettingsCredBlock())) {
                d.StartPosition = FormStartPosition.Manual;
                d.Location = new Point(-10000, -10000);
                // A deterministic verdict, so the credential tab's status line
                // is really painted in the capture instead of the screenshot
                // showing an empty strip that could be hiding a clipped label.
                d.CredentialProbe = delegate { return 0; };
                d.Show();
                d.SelectTabForTest(tab);
                // Pump until the controls have been placed and painted; a single
                // DoEvents() captures the window mid-layout (blank form, children
                // still at 0,0). Bounded so a headless session cannot spin.
                DateTime deadline = DateTime.UtcNow.AddSeconds(2);
                bool waitVerdict = (tab == SettingsDialog.CredTabIndex);
                Stopwatch wait = Stopwatch.StartNew();
                while (DateTime.UtcNow < deadline) {
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(30);
                    // The credential page answers from a worker thread; give it
                    // a moment to land so the sheet shows a real verdict.
                    if (waitVerdict) {
                        // CredOk/CredBad/CredNet are the settled states;
                        // CredTesting must not end the wait.
                        if (d.CredStateForTest >= SettingsDialog.CredOk) break;
                    } else {
                        break;   // one settle pass is enough for a static dialog
                    }
                }
                // Evidence for the "testing..." floor: with an instant stub
                // answer this can only settle early if the 500 ms hold is gone.
                if (waitVerdict)
                    Console.WriteLine("credwait[" + tab + "]: " + wait.ElapsedMilliseconds + " ms");
                Console.WriteLine("setlayout[" + tab + "]: client=" +
                                  d.ClientSize.Width + "x" + d.ClientSize.Height +
                                  " tab=" + d.TabTitleForTest());
                Console.WriteLine("setfit[" + tab + "]: " + d.LayoutReport());
                // The credential tab carries a status line and a second button;
                // print what it holds so the screenshot can be tied to a state.
                Console.WriteLine("credstatus[" + tab + "]: '" + d.CredStatusForTest +
                                  "' polling=" + d.CredPollingForTest);
                using (Bitmap b = new Bitmap(d.Width, d.Height)) {
                    d.DrawToBitmap(b, new Rectangle(0, 0, d.Width, d.Height));
                    b.Save(Path.Combine(dir, names[tab]), ImageFormat.Png);
                }
                d.Close();
            }
        }
        Console.WriteLine("setsheet written: " + dir);
    }

    // --rescuetest: run with the widget stranded where a monitor used to be.
    // A headless geometry test - the real unplug cannot be scripted, but the
    // consequence can: the widget sits entirely outside every screen and must
    // come back. Also pins the promise that a REACHABLE widget is untouched
    // (a deliberate drop on a second monitor stays put).
    public void RunRescueTest() {
        Console.WriteLine("off-screen rescue regression (--rescuetest)");
        int pass = 0, fail = 0;
        Action<string, bool> check = delegate (string name, bool ok) {
            Console.WriteLine("  " + (ok ? "PASS  " : "FAIL  ") + name);
            if (ok) pass++; else fail++;
        };

        Console.WriteLine("  screens = " + Screen.AllScreens.Length);
        foreach (Screen sc in Screen.AllScreens)
            Console.WriteLine("    " + sc.DeviceName + " bounds=" + sc.Bounds +
                              " work=" + sc.WorkingArea + " primary=" + sc.Primary);

        // 1. Stranded: park the widget past the right edge of the virtual
        //    desktop, i.e. exactly where a monitor that just left used to be.
        Rectangle vd = SystemInformation.VirtualScreen;
        Point stranded = new Point(vd.Right + 1200, vd.Top + 200);
        Location = stranded;
        check("a stranded widget is detected as off-screen", !IsOnSomeScreenPublic);
        ForceDisplayChangeForTest();
        check("the rescue brought it back on-screen", IsOnSomeScreenPublic);
        check("the rescue moved it off the stranded spot", Location != stranded);

        // 2. It must land FULLY inside a real work area, not straddling one.
        Rectangle r = new Rectangle(Location, Size);
        bool inside = false;
        foreach (Screen sc in Screen.AllScreens) {
            Rectangle wa = sc.WorkingArea;
            if (r.Left >= wa.Left && r.Right <= wa.Right &&
                r.Top >= wa.Top && r.Bottom <= wa.Bottom) inside = true;
        }
        check("the rescued widget sits fully inside a work area", inside);

        // 3. Still reachable -> must not move. This is the standing promise
        //    that only an unreachable pet is ever relocated.
        Point before = Location;
        ForceDisplayChangeForTest();
        check("a reachable widget is left exactly where it was", Location == before);

        // 4. The nearest-screen pick, against synthetic monitors (primary,
        //    one to the right, one to the left).
        Rectangle[] syn = new Rectangle[] {
            new Rectangle(0, 0, 1920, 1040),
            new Rectangle(1920, 0, 1280, 1024),
            new Rectangle(-1600, 0, 1600, 900)
        };
        check("nearest pick: far right -> the right-hand screen",
              NearestScreenIndexPublic(syn, new Rectangle(5000, 100, 100, 100)) == 1);
        check("nearest pick: far left -> the left-hand screen",
              NearestScreenIndexPublic(syn, new Rectangle(-4000, 100, 100, 100)) == 2);
        check("nearest pick: inside the primary -> the primary",
              NearestScreenIndexPublic(syn, new Rectangle(100, 100, 100, 100)) == 0);
        check("nearest pick: just past the right screen -> that screen",
              NearestScreenIndexPublic(syn, new Rectangle(3210, 100, 10, 10)) == 1);

        // 5. The watchdog ALONE - no display-change notice sent at all. This is
        //    the real guarantee for the case the notice never arrives, or
        //    arrives before the new topology is live (the classmate's report).
        PumpTicks(60);                       // drain any burst case 3 left armed
        Point stranded2 = new Point(vd.Right + 2000, vd.Bottom + 300);
        Location = stranded2;
        check("stranded again with no notice sent", !IsOnSomeScreenPublic);
        PumpTicks(220);                      // idle ticks well past the 5s cadence
        check("the watchdog alone brought it back", IsOnSomeScreenPublic);
        check("the watchdog moved it off the stranded spot", Location != stranded2);

        // 6. ...and it must be idle, not twitchy: a reachable pet is never
        //    nudged by the backstop, no matter how long it runs.
        Point keep = Location;
        PumpTicks(300);
        check("the idle watchdog never moves a reachable widget", Location == keep);

        // 7. The watchdog stands down during a drag. Yanking the pet out of the
        //    user's hand mid-gesture would be worse than the bug being fixed.
        SetDragForTest(true);
        Point dragged = new Point(vd.Right + 2500, vd.Bottom + 400);
        Location = dragged;
        PumpTicks(300);                      // well past both cadences
        check("the watchdog stands down while a drag is in progress",
              Location == dragged);
        SetDragForTest(false);
        PumpTicks(220);                      // released -> now it may act
        check("the watchdog takes over again once the drag ends",
              IsOnSomeScreenPublic);

        Console.WriteLine("\nrescuetest: " + pass + " passed, " + fail + " failed");
    }

    // --parsetest: the 2026-10-17 doubling bug in one place.
    //
    // The gateway now answers the SAME account with two different bodies - a
    // lean one (Packages only) and a rich one (Packages + TotalCredits +
    // CreditGroups, where CreditGroups repeats the same breakdown). The old
    // parser scanned from "Packages" to the end of the body and therefore
    // counted the rich form twice, so the widget printed exactly 2x and then
    // booked an endless charge animation. These fixtures are the real bodies,
    // truncated only in the requestId.
    //
    // Deliberately ASCII-only inside the JSON (the package NAME is dropped):
    // the fixture is about numbers, and this file is compiled under the ANSI
    // codepage, so a literal Chinese value would arrive here as mojibake.
    const string FIX_LEAN =
        "{\"code\":0,\"msg\":\"OK\",\"requestId\":\"lean\",\"data\":{\"Packages\":[" +
        "{\"PackageCode\":\"TCACA_code_007_nzdH5h4Nl0\",\"CycleTotalCapacity\":\"4768\"," +
        "\"CycleRemainCapacity\":\"3474.10000165\",\"CycleUsedCapacity\":\"1293.89999835\"," +
        "\"CycleFrozenCapacity\":\"0\",\"CapacityUnit\":\"credits\",\"TotalCount\":54}," +
        "{\"PackageCode\":\"TCACA_code_008_cfWoLwvjU4\",\"CycleTotalCapacity\":\"500\"," +
        "\"CycleRemainCapacity\":\"500\",\"CycleUsedCapacity\":\"0\"," +
        "\"CycleFrozenCapacity\":\"0\",\"CapacityUnit\":\"credits\",\"TotalCount\":1}]," +
        "\"SubscriptionPackageCode\":\"\",\"SubscriptionPackageName\":\"x\"," +
        "\"IsPaidUser\":false,\"IsProtectedPriceUser\":false}}";

    const string FIX_RICH =
        "{\"code\":0,\"msg\":\"OK\",\"requestId\":\"rich\",\"data\":{\"Packages\":[" +
        "{\"PackageCode\":\"TCACA_code_007_nzdH5h4Nl0\",\"CycleTotalCapacity\":\"4768\"," +
        "\"CycleRemainCapacity\":\"3474.10000165\",\"CycleUsedCapacity\":\"1293.89999835\"," +
        "\"CycleFrozenCapacity\":\"0\",\"CapacityUnit\":\"credits\",\"TotalCount\":54}," +
        "{\"PackageCode\":\"TCACA_code_008_cfWoLwvjU4\",\"CycleTotalCapacity\":\"500\"," +
        "\"CycleRemainCapacity\":\"500\",\"CycleUsedCapacity\":\"0\"," +
        "\"CycleFrozenCapacity\":\"0\",\"CapacityUnit\":\"credits\",\"TotalCount\":1}]," +
        "\"TotalCredits\":\"3974.10000165\",\"SubscriptionPackageCode\":\"\"," +
        "\"SubscriptionPackageName\":\"x\",\"IsPaidUser\":false,\"IsProtectedPriceUser\":false," +
        "\"CreditGroups\":[" +
        "{\"Kind\":\"plan_base\",\"Name\":\"b\",\"CycleTotalCapacity\":\"500\"," +
        "\"CycleRemainCapacity\":\"500\",\"CycleUsedCapacity\":\"0\",\"CycleFrozenCapacity\":\"0\"," +
        "\"CapacityUnit\":\"credits\",\"TotalCount\":1}," +
        "{\"Kind\":\"addon\",\"Name\":\"a\",\"CycleTotalCapacity\":\"0\",\"CycleRemainCapacity\":\"0\"," +
        "\"CycleUsedCapacity\":\"0\",\"CycleFrozenCapacity\":\"0\",\"CapacityUnit\":\"credits\",\"TotalCount\":0}," +
        "{\"Kind\":\"benefit\",\"Name\":\"c\",\"CycleTotalCapacity\":\"4768\"," +
        "\"CycleRemainCapacity\":\"3474.10000165\",\"CycleUsedCapacity\":\"1293.89999835\"," +
        "\"CycleFrozenCapacity\":\"0\",\"CapacityUnit\":\"credits\",\"TotalCount\":54}]}}";

    public void RunParseTest() {
        Console.WriteLine("balance parsing regression (--parsetest)");
        int pass = 0, fail = 0;
        Action<string, bool> check = delegate (string name, bool ok) {
            Console.WriteLine("  " + (ok ? "PASS  " : "FAIL  ") + name);
            if (ok) pass++; else fail++;
        };
        const double WANT = 3974.10000165;

        double lean = ParseCredits(FIX_LEAN);
        double rich = ParseCredits(FIX_RICH);
        Console.WriteLine("  lean=" + lean.ToString("0.####", CultureInfo.InvariantCulture) +
                          " rich=" + rich.ToString("0.####", CultureInfo.InvariantCulture));

        check("lean body parses to the real total", Math.Abs(lean - WANT) < 1e-6);
        check("rich body parses to the SAME total", Math.Abs(rich - WANT) < 1e-6);
        // The whole point: the duplicated group must not add a second copy.
        check("rich body is not double counted", Math.Abs(rich - WANT * 2) > 1);
        check("CreditGroups alone never contributes",
              Math.Abs(rich - lean) < 1e-6);

        // Cycle total (tray tooltip) obeys the same scoping.
        check("lean cycleTotal = 4768 + 500",
              Math.Abs(ParseCreditsTotal(FIX_LEAN) - 5268.0) < 1e-6);
        check("rich cycleTotal ignores CreditGroups",
              Math.Abs(ParseCreditsTotal(FIX_RICH) - 5268.0) < 1e-6);

        // The server's own total is read as a cross-check, not as the value.
        check("TotalCredits field is readable", Math.Abs(ParseTotalField(FIX_RICH) - WANT) < 1e-6);
        check("TotalCredits absent on the lean body",
              double.IsNaN(ParseTotalField(FIX_LEAN)));

        // Fallback path: no Packages at all -> the printed total stands in.
        check("Packages missing falls back to TotalCredits",
              Math.Abs(ParseCredits("{\"data\":{\"TotalCredits\":\"123.45\"}}") - 123.45) < 1e-6);
        // ...but a body with neither must FAIL rather than invent a number.
        check("no Packages and no TotalCredits -> NaN",
              double.IsNaN(ParseCredits("{\"data\":{}}")));
        check("garbage -> NaN", double.IsNaN(ParseCredits("not json at all")));
        check("empty -> NaN", double.IsNaN(ParseCredits("")));

        // A bracket inside a string value must not end the scope early.
        string tricky =
            "{\"data\":{\"Packages\":[{\"Note\":\"]}\",\"CycleRemainCapacity\":\"10\"}]," +
            "\"CreditGroups\":[{\"CycleRemainCapacity\":\"10\"}]}}";
        check("a ']' inside a string does not end the array",
              Math.Abs(ParseCredits(tricky) - 10.0) < 1e-6);

        // Ledger shape (free/paid packages): one row per resource cycle.
        string ledger =
            "{\"data\":{\"Accounts\":[" +
            "{\"CycleCapacityRemainPrecise\":\"3473.27\",\"CapacityRemainPrecise\":\"0\"}," +
            "{\"CycleCapacityRemainPrecise\":\"500\",\"CapacityRemainPrecise\":\"0\"}]," +
            "\"TotalCount\":2}}";
        string scope = ArrayAfter(ledger, "\"Accounts\"");
        double led = scope == null ? double.NaN : SumMatches(RxAcctRemain, scope);
        check("ledger sums CycleCapacityRemainPrecise only",
              !double.IsNaN(led) && Math.Abs(led - 3973.27) < 1e-6);

        // The verdict texts of the credential page must stay distinct - the
        // label is the only feedback the user gets.
        string txtTesting = SettingsDialog.CredStatusText(SettingsDialog.CredTesting);
        string txtOk      = SettingsDialog.CredStatusText(SettingsDialog.CredOk);
        string txtBad     = SettingsDialog.CredStatusText(SettingsDialog.CredBad);
        check("credential verdict texts are all non-empty",
              txtTesting.Length > 0 && txtOk.Length > 0 && txtBad.Length > 0);
        check("credential verdict texts are distinct",
              txtTesting != txtOk && txtTesting != txtBad && txtOk != txtBad);
        check("no verdict prints nothing",
              SettingsDialog.CredStatusText(SettingsDialog.CredNone).Length == 0);

        Console.WriteLine("\nparsetest: " + pass + " passed, " + fail + " failed");
    }

    // --pickertest: the reopen bug in one place. Each NumPicker is handed a
    // value the way SettingsDialog hands it one, then asked what it now shows.
    // A picker that ignores its initial value reports the first preset; these
    // assertions pin the echo behaviour for good.
    public void RunPickerTest() {
        Console.WriteLine("numeric picker reopen regression (--pickertest)");
        int pass = 0, fail = 0;

        Action<string, bool> check = delegate (string name, bool ok) {
            Console.WriteLine("  " + (ok ? "PASS  " : "FAIL  ") + name);
            if (ok) pass++; else fail++;
        };

        // Values chosen to exercise both branches: an exact preset hit, a value
        // between presets (must land in the editor as 自定义), and the extremes.
        double[] sizes = SettingsDialog.SizePresets;
        double[] steps = SettingsDialog.Steps;
        double[] floats = SettingsDialog.FloatPresets;

        // 1. exact preset (step 5 is in the list): the combo carries it, the
        //    editor stays on the placeholder rather than echoing the number.
        {
            NumPicker p = new NumPicker("自定义...", SettingsDialog.NUM_MIN,
                                        SettingsDialog.STEP_MAX, 5.0);
            p.SetPresets(steps, FmtFor);
            check("step=5 -> combo picks the 5 row, box keeps the placeholder",
                  p.ShownValue() == 5.0 && !p.IsCustomShown && p.IsShowingPlaceholder);
        }
        // 2. saved value that is NOT a preset (the real-world state.ini case)
        {
            NumPicker p = new NumPicker("自定义...", SettingsDialog.NUM_MIN,
                                        SettingsDialog.STEP_MAX, 3.25);
            p.SetPresets(steps, FmtFor);
            check("step=3.25 -> box shows 3.25, combo on 自定义",
                  Math.Abs(p.ShownValue() - 3.25) < 1e-9 && p.IsCustomShown);
        }
        // 3. size at a preset
        {
            NumPicker p = new NumPicker("自定义...", SettingsDialog.NUM_MIN,
                                        SettingsDialog.SIZE_MAX_CM, 6.0);
            p.SetPresets(sizes, CmFor);
            check("size=6 -> combo picks the 6cm row, box keeps the placeholder",
                  Math.Abs(p.ShownValue() - 6.0) < 1e-9 && !p.IsCustomShown &&
                  p.IsShowingPlaceholder);
        }
        // 4. floater at a preset
        {
            NumPicker p = new NumPicker("自定义...", SettingsDialog.NUM_MIN,
                                        SettingsDialog.FLOAT_MAX, 3.0);
            p.SetPresets(floats, delegate (double v) { return FmtFor(v) + " \u500D"; });
            check("floatmult=3 -> combo picks the 3x row, box keeps the placeholder",
                  Math.Abs(p.ShownValue() - 3.0) < 1e-9 && !p.IsCustomShown &&
                  p.IsShowingPlaceholder);
        }
        // 5/6/7. limits are per-row - both floor and ceiling
        check("size range is 0.5..20",
              Math.Abs(SettingsDialog.SIZE_MIN_CM - 0.5) < 1e-9 &&
              Math.Abs(SettingsDialog.SIZE_MAX_CM - 20.0) < 1e-9);
        check("float range is 0.5..10",
              Math.Abs(SettingsDialog.FLOAT_MIN - 0.5) < 1e-9 &&
              Math.Abs(SettingsDialog.FLOAT_MAX - 10.0) < 1e-9);
        check("step range is 0.01..999.99",
              Math.Abs(SettingsDialog.STEP_MIN - 0.01) < 1e-9 &&
              Math.Abs(SettingsDialog.STEP_MAX - 999.99) < 1e-9);
        check("every floor sits below its own ceiling",
              SettingsDialog.SIZE_MIN_CM < SettingsDialog.SIZE_MAX_CM &&
              SettingsDialog.FLOAT_MIN   < SettingsDialog.FLOAT_MAX &&
              SettingsDialog.STEP_MIN    < SettingsDialog.STEP_MAX);

        // The 1.5cm preset was removed on request (2026-10-13).
        {
            bool has15 = false;
            foreach (double v in sizes) if (Math.Abs(v - 1.5) < 1e-9) has15 = true;
            check("size preset list no longer offers 1.5cm", !has15);
        }

        // 8/9/10. the integer-digit cap tracks each ceiling
        {
            NumPicker sz = new NumPicker("自定义...", SettingsDialog.SIZE_MIN_CM,
                                         SettingsDialog.SIZE_MAX_CM, 1.5);
            sz.SetPresets(sizes, CmFor);
            check("size admits '20' but not '200'", sz.AcceptsDigits("20") && !sz.AcceptsDigits("200"));

            NumPicker fl = new NumPicker("自定义...", SettingsDialog.FLOAT_MIN,
                                         SettingsDialog.FLOAT_MAX, 1.0);
            fl.SetPresets(floats, delegate (double v) { return FmtFor(v) + " \u500D"; });
            check("float admits '10' but not '100'", fl.AcceptsDigits("10") && !fl.AcceptsDigits("100"));

            NumPicker st = new NumPicker("自定义...", SettingsDialog.STEP_MIN,
                                         SettingsDialog.STEP_MAX, 1.0);
            st.SetPresets(steps, FmtFor);
            check("step admits '999' but not '1000'", st.AcceptsDigits("999") && !st.AcceptsDigits("1000"));
        }

        // 11. two decimals, no more
        {
            NumPicker p = new NumPicker("自定义...", SettingsDialog.NUM_MIN,
                                        SettingsDialog.STEP_MAX, 1.0);
            p.SetPresets(steps, FmtFor);
            check("only two decimals survive a keystroke sequence",
                  p.AcceptsDigits("3.25") && !p.AcceptsDigits("3.256"));
        }

        // 12/13. a value typed below the floor is clamped up on commit, so a
        // half-typed "0.1" (on the way to "0.5") can never leave the row
        // holding something the pet would reject.
        {
            NumPicker sz = new NumPicker("自定义...", SettingsDialog.SIZE_MIN_CM,
                                         SettingsDialog.SIZE_MAX_CM, 1.5);
            sz.SetPresets(sizes, CmFor);
            check("size typed 0.1 clamps up to 0.5",
                  Math.Abs(sz.CommitValue(0.1) - 0.5) < 1e-9);

            NumPicker st = new NumPicker("自定义...", SettingsDialog.STEP_MIN,
                                         SettingsDialog.STEP_MAX, 1.0);
            st.SetPresets(steps, FmtFor);
            check("step typed 0.001 clamps up to 0.01",
                  Math.Abs(st.CommitValue(0.001) - 0.01) < 1e-9);
        }

        Console.WriteLine("\npickertest: " + pass + " passed, " + fail + " failed");
    }

    // --abouttest: mechanical proof of the things a screenshot cannot show.
    public void RunAboutTest() {
        Console.WriteLine("about menu regression (--abouttest)");

        int n = _menu.Items.Count;
        int aboutIdx = -1, quitIdx = -1, settingsIdx = -1;
        for (int i = 0; i < n; i++) {
            ToolStripMenuItem mi = _menu.Items[i] as ToolStripMenuItem;
            if (mi == null) continue;
            if (mi.Text == S_ABOUT) aboutIdx = i;
            if (mi.Text == S_QUIT) quitIdx = i;
            if (mi.Text == S_SETTINGS) settingsIdx = i;
        }
        Console.WriteLine("  menu items      = " + n);
        Console.WriteLine("  Settings index  = " + settingsIdx);
        Console.WriteLine("  About index     = " + aboutIdx);
        Console.WriteLine("  Quit index      = " + quitIdx);

        bool secondToLast = (aboutIdx >= 0) && (quitIdx == n - 1) && (aboutIdx == n - 2);
        Console.WriteLine("  AboutIsSecondToLast=" + secondToLast);
        // The menu is actions-only since 1.2.19. Settings must sit directly
        // above About (asked 2026-10-13), so a future reshuffle breaks here
        // rather than in a screenshot nobody diffs.
        bool settingsAboveAbout = (settingsIdx >= 0) && (settingsIdx == aboutIdx - 1);
        Console.WriteLine("  SettingsAboveAbout=" + settingsAboveAbout);

        string body = AboutBody();
        int lines = body.Split('\n').Length;
        Console.WriteLine("  AboutVer=" + AboutVersionLabel());
        Console.WriteLine("  AboutBodyLines=" + lines);
        Console.WriteLine("  AboutBodyHasDataDir=" + (body.IndexOf(_baseDir) >= 0));
        Console.WriteLine("  --- About box text ---");
        foreach (string l in body.Replace("\r", "").Split('\n'))
            Console.WriteLine("  | " + l);
        Console.WriteLine("  ----------------------");

        // Compare the dialog's version against the assembly metadata rather
        // than a literal: a hardcoded string here silently red-lights this
        // self-check the moment the version is bumped, which is exactly the
        // kind of false alarm that trains people to ignore the suite.
        string expectVer = AboutVersion();
        bool verOk = expectVer != "?" && body.IndexOf("WorkBuddy PointPal " + expectVer) >= 0;
        bool ok = secondToLast &&
                  settingsAboveAbout &&
                  verOk &&
                  body.IndexOf("Realfie") >= 0 &&
                  body.IndexOf(_baseDir) >= 0 &&
                  lines >= 6;
        Console.WriteLine("  abouttest: " + (ok ? "PASS" : "FAIL"));
        if (!ok) Environment.ExitCode = 1;
    }

    void AskLabel() {
        using (InputDialog d = new InputDialog(S_LBLT, S_LBLP, "", _label)) {
            if (d.ShowDialog(this) != DialogResult.OK) return;
            SetLabel(d.Value);
        }
    }

    // Shared by AskLabel and the settings dialog's label box. An empty value is
    // refused, so the tablet's first line can never go blank by accident.
    void SetLabel(string v) {
        v = (v ?? "").Trim();
        if (v.Length == 0 || v == _label) return;
        _label = v;
        SaveState();
        _dirty = true;
        RenderToCanvas();
        PushLayer();
        Log("label set to '" + v + "'");
    }

    // ------------------------------------------------------------ characters ---

    static CharSpec FindChar(string id) {
        for (int i = 0; i < Chars.Length; i++) if (Chars[i].Id == id) return Chars[i];
        return Chars[0];
    }

    // (Re)loads the artwork for _char and its mirrored copy. The mirror is
    // rendered whenever the widget snaps to the right edge, so the character
    // keeps facing INTO the screen instead of looking at the bezel.
    void LoadCharacter() {
        // WBPET_SPRITE overrides only the default character's art; everyone
        // else always loads their own bundled file.
        string sprite = _char.Id == "dsh"
            ? Get("WBPET_SPRITE", Path.Combine(_baseDir, _char.File))
            : Path.Combine(_baseDir, _char.File);
        if (!File.Exists(sprite)) {
            Log("character art missing: " + sprite + " (falling back to dsh)");
            _char = Chars[0];
            sprite = Path.Combine(_baseDir, _char.File);
            if (!File.Exists(sprite)) throw new FileNotFoundException("sprite not found: " + sprite);
        }
        Bitmap fresh = new Bitmap(sprite);
        if (_flat != null) _flat.Dispose();
        if (_flatFlip != null) _flatFlip.Dispose();
        _flat = fresh;
        _flatFlip = (Bitmap)fresh.Clone();
        _flatFlip.RotateFlip(RotateFlipType.RotateNoneFlipX);
    }

    void SetCharacter(string id) {
        CharSpec c = FindChar(id);
        if (c.Id == _char.Id) return;
        _char = c;
        LoadCharacter();
        Relayout();
        SnapToCorner(true);               // keep it pinned to the corner
        SaveState();
        RenderToCanvas();
        PushLayer();
        Log("character set to " + _char.Id);
    }

    // ------------------------------------------------------------------ side ---

    // Which bottom corner the widget lives in. Flipping sides also mirrors the
    // artwork; Relayout bakes the mirror into every derived bitmap and quad.
    void SetSide(bool right, bool immediate) {
        if (right == _sideRight) { SnapToCorner(immediate); return; }
        _sideRight = right;
        Relayout();
        SnapToCorner(immediate);
        SaveState();
        RenderToCanvas();
        PushLayer();
        Log("side set to " + (right ? "right" : "left"));
    }

    // ------------------------------------------------------------ floater size ---

    void UpdateFloaterStep() {
        _floaterStep = (float)Math.Max(18.0 * _floatMult, 34.0 * _scale * _floatMult * 1.7);
    }

    void SetFloatMult(double v) {
        // Matches SettingsDialog.FLOAT_MIN / FLOAT_MAX: the floater row
        // offers 0.5-10x.
        if (v < 0.5) v = 0.5;
        if (v > 10) v = 10;
        if (Math.Abs(v - _floatMult) < 1e-9) return;
        _floatMult = v;
        UpdateFloaterStep();
        SaveState();
        Log("floater size set to x" + FmtAmt(v));
    }

    void AskFloatMult() {
        using (InputDialog d = new InputDialog(S_FLOTT, S_FLOTP, "",
                                               _floatMult.ToString("0.##", CultureInfo.InvariantCulture))) {
            if (d.ShowDialog(this) != DialogResult.OK) return;
            double v;
            if (double.TryParse(d.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v > 0)
                SetFloatMult(v);
            else
                Log("custom floater size rejected: '" + d.Value + "'");
        }
    }

    // ------------------------------------------------------------------ sound ---

    void ToggleSound() {
        _soundWanted = !_soundWanted;
        _soundEnabled = _soundWanted;
        if (_soundWanted && _sound == null) {          // lazy-load after an "off" start
            string sndPath = Path.Combine(_baseDir, Get("WBPET_SOUND_FILE", "hit.mp3"));
            if (File.Exists(sndPath)) {
                int vol = int.Parse(Get("WBPET_VOLUME", "80"));
                _sound = new SoundPool(sndPath, 4, vol, LogFileIn(_baseDir));
                if (_sound.Failed) { Log("sound pool failed: " + _sound.Error); _soundEnabled = false; }
            }
        }
        SaveState();
        Log("hit sound " + (_soundWanted ? "on" : "off"));
    }

    // "启动落点" setting, chosen in the settings dialog. Remembering takes
    // effect on the NEXT launch: the widget is not moved now, because the user
    // explicitly asked that a chosen corner stays where it is (2026-10-13).
    void SetRememberScreen(bool on) {
        if (on == _rememberScreen) return;
        _rememberScreen = on;
        // Turning remembering on while parked somewhere records that spot, so
        // the promise "come back here" is about the place the user can see.
        if (on) SavePos();
        SaveState();
        Log("startup position mode = " + (on ? "remember this screen" : "always primary"));
    }

    void SetCm(double v) {
        // Matches SettingsDialog.SIZE_MIN_CM / SIZE_MAX_CM: the size row
        // offers 0.5-20cm.
        if (v < 0.5) v = 0.5;
        if (v > 20) v = 20;
        _cm = v;
        Relayout();
        SnapToCorner(true);               // keep it pinned to the corner
        SaveState();
        RenderToCanvas();
        PushLayer();
        Log("size set to " + CmLabel(v) + " -> " + _w + "x" + _h + " px");
    }

    void AskCm() {
        using (InputDialog d = new InputDialog(S_SIZET, S_SIZEP, "cm",
                                               _cm.ToString("0.##", CultureInfo.InvariantCulture))) {
            if (d.ShowDialog(this) != DialogResult.OK) return;
            double v;
            if (double.TryParse(d.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v > 0)
                SetCm(v);
            else
                Log("custom size rejected: '" + d.Value + "'");
        }
    }

    // Builds (but does not show) the credential dialog. The ONLY place it is
    // constructed, so first run, the tray menu and the --toksheet renderer can
    // never drift apart in wording or layout.
    InputDialog NewCredentialDialog(string initial) {
        return new InputDialog(S_TOKENT, S_TOKENP, S_TOKENH, initial, true,
                               GUIDE_BODY, S_GUIDET);
    }

    // The one credential prompt, shared by first run and the tray menu, so a
    // first-time user meets exactly the same window (multi-line editor, the
    // paste hint, and the "how do I get one" button) as someone changing it
    // later. Returns false when the user cancels.
    bool AskCredential(string initial, out string entered) {
        entered = null;
        using (InputDialog d = NewCredentialDialog(initial)) {
            if (d.ShowDialog(this) != DialogResult.OK) return false;
            entered = d.Value;
            return true;
        }
    }

    void AskToken() {
        // multiline: the whole "Copy as cURL" can be 10-20 lines, and a
        // single-line box would keep only the first one on paste.
        string k;
        if (!AskCredential(_token, out k)) return;
        if (k.Length > 0 && k != _token) {
            SetToken(k);
            // A brand-new credential ends any backoff and re-arms the one-shot
            // "rejected" balloon, so the very next poll gets a fair chance.
            _pollSkip = 0;
            _credAlertShown = false;
            _pollWant = 2;
            Log("token set manually (mode=" + (_credIsCookie ? "cookie" : "bearer") +
                ", length " + _token.Length + ", uid=" +
                (_uid.Length > 0 ? "set" : "empty") + ")");
        }
    }

    // ------------------------------------------------------------ geometry ---

    void Relayout() {
        // Physical size has to come from the real monitor DPI. If the process
        // could not be made DPI aware, Windows lies and reports 96, which would
        // shrink the widget on a scaled display - so fall back to the monitor's
        // actual DPI instead of trusting a 96 that was never real.
        double dpiY = 96;
        try { using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) dpiY = g.DpiY; } catch { }
        if (dpiY <= 96.5) {
            try {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) {
                    IntPtr hdc = g.GetHdc();
                    int raw = Native.GetDeviceCaps(hdc, 90);          // LOGPIXELSY
                    g.ReleaseHdc(hdc);
                    if (raw > 0) dpiY = raw;
                }
            } catch { }
        }
        if (dpiY < 72) dpiY = 96;
        int px = (int)Math.Round(_cm / 2.54 * dpiY);
        if (px < 40) px = 40;

        // The window takes the artwork's aspect ratio: _cm sets the HEIGHT and
        // the width follows. The default art is square; the mac characters are
        // 3:2 (their hair/tail needs the extra room).
        Bitmap art = _sideRight ? _flatFlip : _flat;
        _scale = (double)px / art.Height;
        int sw = Math.Max(2, (int)Math.Round(art.Width * _scale));
        int sh = Math.Max(2, (int)Math.Round(art.Height * _scale));
        _w = sw; _h = sh;
        ClientSize = new Size(_w, _h);

        Bitmap scaled = new Bitmap(sw, sh, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(scaled)) {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImage(art, new Rectangle(0, 0, sw, sh));
        }
        if (_sprNormal != null) _sprNormal.Dispose();
        if (_sprRed != null) _sprRed.Dispose();
        if (_canvas != null) _canvas.Dispose();

        _sprNormal = scaled;
        _sprRed = BuildRedLayer(scaled);
        _canvas = new Bitmap(_w, _h, PixelFormat.Format32bppArgb);

        // Tablet screen quad in sprite pixels, measured per character (see the
        // Chars table). When the artwork is mirrored the corners swap sides:
        // TL <- mirror of TR, and so on, so the panel still lands on the screen.
        double[] qx = new double[4]; double[] qy = new double[4];
        double hx = _char.Hx, hy = _char.Hy;
        if (!_sideRight) {
            for (int i = 0; i < 4; i++) { qx[i] = _char.Qx[i]; qy[i] = _char.Qy[i]; }
        } else {
            double W = art.Width;
            qx[0] = W - _char.Qx[1]; qy[0] = _char.Qy[1];
            qx[1] = W - _char.Qx[0]; qy[1] = _char.Qy[0];
            qx[2] = W - _char.Qx[3]; qy[2] = _char.Qy[3];
            qx[3] = W - _char.Qx[2]; qy[3] = _char.Qy[2];
            hx = W - _char.Hx;
        }
        _fx = new double[4]; _fy = new double[4];
        for (int i = 0; i < 4; i++) { _fx[i] = qx[i] * _scale; _fy[i] = qy[i] * _scale; }

        _headX = (int)Math.Round(hx * _scale);
        _headY = (int)Math.Round(hy * _scale);
        UpdateFloaterStep();
        _hitMap = null;
        _dirty = true;
    }

    // Flat red copy of the art for the hurt flash (alpha preserved).
    static Bitmap BuildRedLayer(Bitmap src) {
        Bitmap dst = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
        Buf s = new Buf(src);
        try {
            Buf d = new Buf(dst);
            try {
                for (int y = 0; y < s.H; y++) {
                    for (int x = 0; x < s.W; x++) {
                        int si = y * s.Stride + x * 4, di = y * d.Stride + x * 4;
                        d.P[di]     = 34;
                        d.P[di + 1] = 48;
                        d.P[di + 2] = 255;
                        d.P[di + 3] = s.P[si + 3];
                    }
                }
                d.Flush();
            } finally { d.Dispose(); }
        } finally { s.Dispose(); }
        return dst;
    }

    // The working area the widget is allowed to snap into. Until 1.2.19 this
    // was hardcoded to Screen.PrimaryScreen, so the pet fled back to the main
    // monitor no matter where it was dropped - a bug on any multi-monitor
    // desk (reported 2026-10-13). The rule now: the area of the screen the
    // widget currently sits on. On a single-monitor machine FromRectangle
    // returns the primary screen, so nothing changes there.
    Rectangle SnapArea() {
        try { return Screen.FromRectangle(Bounds).WorkingArea; }
        catch { return Screen.PrimaryScreen.WorkingArea; }
    }

    // Anchors a point into a work area without knowing the widget size, so the
    // pick-a-corner rule can be unit-tested against synthetic monitors (see
    // --monitortest). Pure: no fields, no Screen calls.
    internal static Point CornerIn(Rectangle wa, int w, int h, bool sideRight) {
        return sideRight
            ? new Point(wa.Right - w, wa.Bottom - h)
            : new Point(wa.Left,      wa.Bottom - h);
    }

    // Which bottom corner a drop point of the widget's centre picks on a given
    // work area. Pure, for the same reason as CornerIn.
    internal static bool SideFor(Rectangle wa, int centreX) {
        return (centreX - wa.Left) * 2 >= wa.Width;
    }

    void SnapToCorner(bool immediate) {
        Point target = CornerIn(SnapArea(), _w, _h, _sideRight);
        if (immediate) { Location = target; _snapping = false; SavePos(); return; }
        _snapFrom = Location;
        _snapTo = target;
        _snapT = 0;
        _snapping = true;
    }

    // First placement of a session. Two modes (see the "启动落点" setting):
    //   remember -> rebuild the screen the user last settled on, from the
    //               anchor offsets, and park in the same corner;
    //   primary  -> historical behaviour: bottom corner of the primary screen,
    //               left or right per the saved side.
    // The remembered screen is matched to a real monitor by work area; if it is
    // gone (monitor unplugged, resolution changed) the primary screen is used,
    // so the widget is never placed off-screen.
    void PlaceOnStartup() {
        bool wantRemember = _rememberScreen && _anchorValid;
        if (wantRemember) {
            Rectangle wa = AnchorToCurrentScreen();
            if (wa.Width > 0) {
                Point t = CornerIn(wa, _w, _h, _anchorRight);
                _sideRight = _anchorRight;
                Location = t;
                _snapping = false;
                SavePos();
                Log("startup: restored to remembered screen " + wa.X + "," + wa.Y +
                    " corner=" + (_anchorRight ? "right" : "left"));
                return;
            }
            Log("startup: remembered screen is gone, falling back to the primary");
        }
        SnapToCorner(true);
        Log("startup: placed on the primary screen (remember=" + _rememberScreen +
            ", anchorValid=" + _anchorValid + ")");
    }

    // Maps the stored anchor onto whichever current monitor best matches it.
    // Preference order: exact work area, then an equal-size monitor whose
    // position moved, then same-size fallback by index. Empty rectangle = no
    // candidate (single-monitor desks, or the screen went away).
    Rectangle AnchorToCurrentScreen() {
        Rectangle best = Rectangle.Empty;
        Screen[] all = Screen.AllScreens;
        // exact match (the common case: nothing changed)
        foreach (Screen s in all)
            if (s.WorkingArea == _anchorWa) return s.WorkingArea;
        // same size, moved: the user rearranged the desk or a panel width changed
        foreach (Screen s in all)
            if (s.WorkingArea.Width == _anchorWa.Width &&
                s.WorkingArea.Height == _anchorWa.Height) {
                if (s.WorkingArea != _anchorPrimary) return s.WorkingArea;
                best = s.WorkingArea;   // keep the primary as a last resort
            }
        return best;
    }

    // Only a rescue, never a relocation: if a monitor was unplugged or the
    // resolution changed under us, the widget can end up parked off-screen
    // (reported 2026-10-13). Nudge it into the nearest visible corner ONLY in
    // that case - a deliberate drop on a second monitor is left alone.
    void RescueIfOffScreen() {
        if (IsOnSomeScreen()) return;
        Rectangle wa = RescueArea();
        Point target = CornerIn(wa, _w, _h, _sideRight);
        Location = target;
        _snapping = false;
        SavePos();
        // Moving a layered window does not repaint its surface, and after a
        // mode change the compositor may have dropped ours outright. Re-push
        // so the pet can never end up "there, but invisible".
        RenderToCanvas();
        PushLayer();
        Log("display change: widget was off-screen, rescued to " + wa.X + "," + wa.Y +
            " corner=" + (_sideRight ? "right" : "left"));
    }

    // Where a stranded widget goes: the monitor NEAREST to what is left of it,
    // falling back to the primary. Deliberately not Screen.FromRectangle -
    // what that returns for a rectangle intersecting nothing is not something
    // to build a rescue on.
    Rectangle RescueArea() {
        try {
            Screen[] all = Screen.AllScreens;
            if (all.Length > 0) {
                Rectangle[] rs = new Rectangle[all.Length];
                for (int i = 0; i < all.Length; i++) rs[i] = all[i].Bounds;
                int k = NearestScreenIndex(rs, Bounds);
                if (k >= 0) return all[k].WorkingArea;
            }
        } catch { }
        try { return Screen.PrimaryScreen.WorkingArea; } catch { }
        return SystemInformation.WorkingArea;
    }

    // Index of the monitor closest to r, measured centre-to-rectangle so a
    // monitor diagonally away is not preferred over the one right beside it.
    // Pure: no Screen calls, so the pick is assertable (--rescuetest).
    internal static int NearestScreenIndex(Rectangle[] screens, Rectangle r) {
        Point c = new Point(r.Left + r.Width / 2, r.Top + r.Height / 2);
        int best = -1; long bestD = long.MaxValue;
        for (int i = 0; i < screens.Length; i++) {
            Rectangle s = screens[i];
            int dx = 0, dy = 0;
            if (c.X < s.Left) dx = s.Left - c.X;
            else if (c.X >= s.Right) dx = c.X - s.Right + 1;
            if (c.Y < s.Top) dy = s.Top - c.Y;
            else if (c.Y >= s.Bottom) dy = c.Y - s.Bottom + 1;
            long d = (long)dx * dx + (long)dy * dy;
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    // The watchdog, driven off the animation timer so no extra timer exists.
    // Checks hard for a moment after a display change (the new topology may
    // not be live the instant the notice arrives), then settles into a slow
    // idle cadence that costs a few rectangle intersections every 5s.
    void WatchdogTick() {
        // Never fight the user's own hand: while a drag is in progress the
        // widget is exactly where they are putting it.
        if (_drag) return;
        double dtms = _timer == null ? 33 : _timer.Interval;
        if (_rescueBurstLeft > 0) {
            _rescueBurstLeft -= dtms;
            RescueIfOffScreen();
            return;
        }
        _watchdogAccum += dtms;
        if (_watchdogAccum < WATCHDOG_MS) return;
        _watchdogAccum = 0;
        RescueIfOffScreen();
    }

    bool IsOnSomeScreen() {
        try {
            Rectangle b = Bounds;
            foreach (Screen s in Screen.AllScreens) {
                // A sliver still counts: only "entirely outside every monitor"
                // is a rescue. Intersecting at all means the user can reach it.
                if (Rectangle.Intersect(s.Bounds, b).Width > 0 &&
                    Rectangle.Intersect(s.Bounds, b).Height > 0) return true;
            }
            return false;
        } catch { return true; }
    }

    // ---- test hooks (--rescuetest) -----------------------------------------
    public bool IsOnSomeScreenPublic { get { return IsOnSomeScreen(); } }
    public static int NearestScreenIndexPublic(Rectangle[] screens, Rectangle r) {
        return NearestScreenIndex(screens, r);
    }
    // Exactly what the WndProc does for WM_DISPLAYCHANGE, minus the message.
    public void ForceDisplayChangeForTest() {
        _rescueBurstLeft = RESCUE_BURST_MS;
        RescueIfOffScreen();
    }
    public void SetDragForTest(bool on) { _drag = on; }

    // --------------------------------------------------------------- state ---

    // Both live in userdata\ now. ReadOld/Resolve below fall back to the old
    // data-folder-root location so an upgrade in progress is still readable.
    string StatePath { get { return Path.Combine(_userDir, "state.ini"); } }
    string TokenPath { get { return Path.Combine(_userDir, "token.txt"); } }
    string LogPath   { get { return ResolveUserFile(_userDir, _baseDir, "pet.log"); } }

    // Prefer the new (userdata\) copy; fall back to the legacy root copy. A
    // caller that only wants to READ should use this; writers use the new path
    // directly (and MigrateUserFiles has already moved any legacy file over).
    static string ResolveUserFile(string userDir, string baseDir, string name) {
        try {
            string nw = Path.Combine(userDir, name);
            if (File.Exists(nw)) return nw;
            string old = Path.Combine(baseDir, name);
            if (File.Exists(old)) return old;
        } catch { }
        return Path.Combine(userDir, name);
    }

    // Users of the data folder may be very old installations whose userdata\
    // does not exist yet (migration runs at boot, but a writer should never
    // depend on that having happened).
    void EnsureUserDir() {
        try { if (!Directory.Exists(_userDir)) Directory.CreateDirectory(_userDir); } catch { }
    }

    void ReadState() {
        try {
            // Migration normally already moved it, but read through the resolver
            // anyway so a read can never miss a legacy file.
            string path = ResolveUserFile(_userDir, _baseDir, "state.ini");
            if (!File.Exists(path)) return;
            foreach (string line in File.ReadAllLines(path, Encoding.UTF8)) {
                int i = line.IndexOf('=');
                if (i <= 0) continue;
                string k = line.Substring(0, i).Trim(), v = line.Substring(i + 1).Trim();
                double d;
                if (k == "cm" && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d) && d >= 0.5 && d <= 20)
                    _cm = d;
                else if (k == "step" && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d) && d >= 0.01)
                    _step = d;
                else if (k == "label" && v.Length > 0)
                    _label = v;
                else if (k == "char")
                    _char = FindChar(v);
                else if (k == "side")
                    _sideRight = (v == "right");
                else if (k == "posmode")
                    _rememberScreen = (v == "remember");
                else if (k == "anchor") {
                    // "dx,dy,dw,dh,px,py,pw,ph,side" - the screen's work area as
                    // an offset from the primary's top-left, plus the primary's
                    // size, plus which corner. Offsets (not absolute pixels) so
                    // a rearranged desk still lands somewhere sensible.
                    string[] p = v.Split(',');
                    long[] q = new long[8];
                    bool good = p.Length == 9;
                    if (good)
                        for (int j = 0; j < 8; j++)
                            good &= long.TryParse(p[j], NumberStyles.Integer,
                                                  CultureInfo.InvariantCulture, out q[j]);
                    if (good) {
                        _anchorWa = new Rectangle(
                            (int)(q[4] + q[0]), (int)(q[5] + q[1]), (int)q[2], (int)q[3]);
                        _anchorPrimary = new Rectangle(
                            (int)q[4], (int)q[5], (int)q[6], (int)q[7]);
                        _anchorRight = (p[8] == "right");
                        _anchorValid = true;
                    }
                }
                else if (k == "floatmult" && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d) && d >= 0.5 && d <= 10)
                    _floatMult = d;
                else if (k == "sound")
                    _soundWanted = (v != "0");
            }
        } catch { }
    }

    void SaveState() {
        try {
            EnsureUserDir();
            File.WriteAllText(StatePath,
                "cm=" + _cm.ToString("0.##", CultureInfo.InvariantCulture) + "\r\n" +
                "step=" + _step.ToString("0.##", CultureInfo.InvariantCulture) + "\r\n" +
                "label=" + _label + "\r\n" +
                "char=" + _char.Id + "\r\n" +
                "side=" + (_sideRight ? "right" : "left") + "\r\n" +
                "floatmult=" + _floatMult.ToString("0.##", CultureInfo.InvariantCulture) + "\r\n" +
                "sound=" + (_soundWanted ? "1" : "0") + "\r\n" +
                "posmode=" + (_rememberScreen ? "remember" : "primary") + "\r\n" +
                AnchorLine(),
                new UTF8Encoding(false));
        } catch { }
    }

    // The settled corner, as an offset from the primary screen's top-left (see
    // the reader in ReadState). Empty until the widget has been placed once.
    string AnchorLine() {
        if (!_anchorValid) return "";
        int dx = _anchorWa.Left - _anchorPrimary.Left;
        int dy = _anchorWa.Top  - _anchorPrimary.Top;
        return "anchor=" +
            dx.ToString(CultureInfo.InvariantCulture) + "," +
            dy.ToString(CultureInfo.InvariantCulture) + "," +
            _anchorWa.Width.ToString(CultureInfo.InvariantCulture) + "," +
            _anchorWa.Height.ToString(CultureInfo.InvariantCulture) + "," +
            _anchorPrimary.Left.ToString(CultureInfo.InvariantCulture) + "," +
            _anchorPrimary.Top.ToString(CultureInfo.InvariantCulture) + "," +
            _anchorPrimary.Width.ToString(CultureInfo.InvariantCulture) + "," +
            _anchorPrimary.Height.ToString(CultureInfo.InvariantCulture) + "," +
            (_anchorRight ? "right" : "left") + "\r\n";
    }

    // Records the corner the widget just settled into. Called from every place
    // the widget parks itself, so "remember which screen" always reflects the
    // user's latest choice rather than the launch position.
    void SavePos() {
        try {
            _anchorWa = SnapArea();
            _anchorPrimary = Screen.PrimaryScreen.WorkingArea;
            _anchorRight = _sideRight;
            _anchorValid = true;
        } catch { }
    }

    void SaveToken() {
        // A test run parsed synthetic fixtures - never let those reach disk.
        if (_suppressSave) {
            Log("SaveToken skipped (test mode); the live credential is untouched");
            return;
        }
        try {
            EnsureUserDir();
            // line1 = credential, line2 = uid (optional), line3 = User-Agent,
            // sealed with DPAPI under the CURRENT USER before it touches disk.
            // The plaintext used to be written verbatim, so a copied, backed-up
            // or mis-sent data folder handed over a live session. The DPAPI key
            // is derived from the user's logon credentials and never stored on
            // disk, so the bytes are useless off this machine / this account.
            //
            // It is NOT protection against code already running as this user:
            // such code can call the very same decrypt API. See PRIVACY.md.
            string body = _token + "\r\n" + _uid + "\r\n" + _ua;
            byte[] plain  = new UTF8Encoding(false).GetBytes(body);
            byte[] sealed_ = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(TokenPath, sealed_);
        } catch { }
    }

    // -------------------------------------------------------------- window ---

    protected override CreateParams CreateParams {
        get {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e) {
        base.OnHandleCreated(e);
        _dirty = true;
        RenderToCanvas();
        PushLayer();
    }

    void PushLayer() {
        if (_canvas == null || Handle == IntPtr.Zero) return;
        IntPtr screenDc = Native.GetDC(IntPtr.Zero);
        IntPtr memDc = Native.CreateCompatibleDC(screenDc);
        IntPtr hBmp = IntPtr.Zero, old = IntPtr.Zero;
        try {
            hBmp = _canvas.GetHbitmap(Color.FromArgb(0));
            old = Native.SelectObject(memDc, hBmp);
            Native.SIZE size = new Native.SIZE(); size.cx = _canvas.Width; size.cy = _canvas.Height;
            Native.POINT src = new Native.POINT(); src.X = 0; src.Y = 0;
            Native.POINT dst = new Native.POINT(); dst.X = Left; dst.Y = Top;
            Native.BLENDFUNCTION bf = new Native.BLENDFUNCTION();
            bf.BlendOp = Native.AC_SRC_OVER; bf.BlendFlags = 0;
            bf.SourceConstantAlpha = 255; bf.AlphaFormat = Native.AC_SRC_ALPHA;
            Native.UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref bf, Native.ULW_ALPHA);
        } finally {
            if (old != IntPtr.Zero) Native.SelectObject(memDc, old);
            if (hBmp != IntPtr.Zero) Native.DeleteObject(hBmp);
            Native.DeleteDC(memDc);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    protected override void WndProc(ref Message m) {
        if (m.Msg == Native.WM_PET_WAKE) {
            // A second copy was launched and handed the wake to us. Acknowledge
            // visibly so the user understands why no new pet appeared - but do
            // NOT touch the balance (that would fake spending).
            Wake();
            return;
        }
        if (m.Msg == Native.WM_NCHITTEST) {
            int lp = (int)m.LParam;
            int x = (short)(lp & 0xFFFF), y = (short)((lp >> 16) & 0xFFFF);
            Point cp = PointToClient(new Point(x, y));
            bool solid = false;
            byte[] map = _hitMap;
            if (map != null && cp.X >= 0 && cp.Y >= 0 && cp.X < _hitW && cp.Y < _hitH)
                solid = map[cp.Y * _hitW + cp.X] > 8;
            m.Result = (IntPtr)(solid ? Native.HTCLIENT : Native.HTTRANSPARENT);
            return;
        }
        if (m.Msg == Native.WM_DISPLAYCHANGE) {
            // A monitor was added/removed or a resolution changed. Deliberately
            // NOT a re-snap: the user told us a chosen corner stays put (asked
            // 2026-10-13). We only rescue the widget if the change left it
            // entirely off-screen, which is a "can't reach it" bug, not a
            // preference.
            //
            // Check now AND for the next moment: this notice can arrive before
            // the new topology is live, in which case AllScreens still lists
            // the monitor that just left and a single check sees nothing
            // wrong. No second notice follows, so one-shot checking left the
            // pet stranded off-screen for good (reported 2026-10-13).
            _rescueBurstLeft = RESCUE_BURST_MS;
            RescueIfOffScreen();
            PushLayer();
        }
        base.WndProc(ref m);
    }

    protected override void OnMouseDown(MouseEventArgs e) {
        if (e.Button == MouseButtons.Left) {
            _drag = true;
            _snapping = false;
            _dragStart = Cursor.Position;
            _winStart = Location;
        } else if (e.Button == MouseButtons.Right) {
            RefreshMenuChecks();
            _menu.Show(Cursor.Position);
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e) {
        if (_drag) {
            Point p = Cursor.Position;
            Location = new Point(_winStart.X + (p.X - _dragStart.X), _winStart.Y + (p.Y - _dragStart.Y));
            PushLayer();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseEventArgs e) {
        if (_drag) {
            _drag = false;
            // The drop picks the corner: the widget's centre relative to the
            // monitor it was dropped on decides left vs right (bottom either
            // way). Both the monitor and the split use the DROP position - a
            // multi-monitor desk used to compare against the primary screen
            // only, so dropping on a second monitor snapped back to the first
            // (reported 2026-10-13).
            Rectangle wa = SnapArea();   // SnapArea uses Bounds, still at the drop point
            bool right = SideFor(wa, Left + _w / 2);
            SetSide(right, false);        // release -> fly to its corner
        }
        base.OnMouseUp(e);
    }

    void Quit() {
        try { _timer.Stop(); _poll.Stop(); } catch { }
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        if (_sound != null) { try { _sound.Dispose(); } catch { } }
        if (_soundEgg != null) { try { _soundEgg.Dispose(); } catch { } }
        SaveState();
        Application.Exit();
    }

    // -------------------------------------------------------------- render ---

    public void RenderToCanvas() {
        if (_canvas == null) return;

        Buf buf = new Buf(_canvas);
        try {
            byte[] p = buf.P;
            Array.Clear(p, 0, p.Length);

            double sx = 0, sy = 0;
            for (int i = 0; i < _hits.Count; i++) {
                Hit h = _hits[i];
                double e = Math.Max(0, 1 - h.T / Hit.Dur);
                sx += Math.Sin(h.T * 24) * 3.2 * e;
                sy += Math.Cos(h.T * 19) * 2.8 * e;
            }
            // hard ceiling: overlapping cues must not fling the sprite off-screen
            double shakeMax = Math.Min(12.0, _w * 0.06);
            if (sx > shakeMax) sx = shakeMax; else if (sx < -shakeMax) sx = -shakeMax;
            if (sy > shakeMax) sy = shakeMax; else if (sy < -shakeMax) sy = -shakeMax;
            int ox = (int)Math.Round(sx), oy = (int)Math.Round(sy);
            _shakeX = ox; _shakeY = oy;      // screen text + floating numbers follow this

            BlitLayer(_sprNormal, p, buf.Stride, ox, oy, 1.0);
            for (int i = 0; i < _hits.Count; i++) {
                double pulse = _hits[i].Pulse;
                if (pulse > 0.01) {
                    // The hurt overlay is toned down as the widget grows: 60% of a
                    // 113px sprite is a readable flash, 60% of a 454px one would
                    // just be a red silhouette.
                    double maxTint = Math.Max(0.30, 0.64 - _scale * 0.75);
                    BlitLayer(_sprRed, p, buf.Stride, ox, oy, Math.Min(maxTint, maxTint * pulse));
                }
            }
            buf.Flush();
        } finally { buf.Dispose(); }

        DrawScreen();                                  // transparent screen + text only
        if (!_connected && _lastPollResult.Length > 0) DrawAlert();

        if (_floaters.Count > 0) {
            Buf b2 = new Buf(_canvas);
            try {
                for (int i = 0; i < _floaters.Count; i++) DrawFloater(_floaters[i], b2);
                b2.Flush();
            } finally { b2.Dispose(); }
        }

        Buf b3 = new Buf(_canvas);
        try { BuildHitMap(b3); } finally { b3.Dispose(); }
        _dirty = false;
    }

    void BlitLayer(Bitmap layer, byte[] dst, int dstStride, int ox, int oy, double alpha) {
        if (layer == null) return;
        Buf src = new Buf(layer);
        try {
            byte[] sp = src.P; int ss = src.Stride;
            for (int y = 0; y < src.H; y++) {
                int ty = y + oy;
                if (ty < 0 || ty >= _h) continue;
                int srow = y * ss, drow = ty * dstStride;
                for (int x = 0; x < src.W; x++) {
                    int tx = x + ox;
                    if (tx < 0 || tx >= _w) continue;
                    int si = srow + x * 4;
                    int sa = sp[si + 3];
                    if (sa == 0) continue;
                    Cs.Blend(dst, drow + tx * 4, sp[si + 2], sp[si + 1], sp[si], sa / 255.0 * alpha);
                }
            }
        } finally { src.Dispose(); }
    }

    void BuildHitMap(Buf buf) {
        if (_hitMap == null || _hitW != _w || _hitH != _h) {
            _hitMap = new byte[_w * _h]; _hitW = _w; _hitH = _h;
        }
        byte[] p = buf.P;
        for (int y = 0; y < _h; y++) {
            int row = y * _w, srow = y * buf.Stride;
            for (int x = 0; x < _w; x++) _hitMap[row + x] = p[srow + x * 4 + 3];
        }
    }

    // The panel bitmap carries ONLY the label and the number. The tablet screen
    // is an opaque black surface in the artwork, so the text is drawn light.
    Bitmap BuildPanel(int pw, int ph) {
        Bitmap panel = new Bitmap(pw, ph, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(panel)) {
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;

            float W = pw, H = ph;
            using (StringFormat sf = new StringFormat()) {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                sf.FormatFlags = StringFormatFlags.NoWrap;

                // Custom labels can be longer than the default, so the font is
                // shrunk to fit the panel instead of letting the layout rect
                // clip the text.
                float fLabel = H * 0.21f;
                using (Font probe = new Font("Microsoft YaHei UI", fLabel, FontStyle.Bold, GraphicsUnit.Pixel)) {
                    SizeF want = g.MeasureString(_label, probe);
                    if (want.Width > W * 0.92f) fLabel *= (W * 0.92f) / want.Width;
                }
                using (Font f = new Font("Microsoft YaHei UI", fLabel, FontStyle.Bold, GraphicsUnit.Pixel))
                using (Brush shadow = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
                using (Brush b = new SolidBrush(Color.FromArgb(235, 158, 182, 224))) {
                    RectangleF lr = new RectangleF(0, H * 0.04f, W, fLabel * 1.5f);
                    g.DrawString(_label, f, shadow, new RectangleF(lr.X + 1.5f, lr.Y + 1.5f, lr.Width, lr.Height), sf);
                    g.DrawString(_label, f, b, lr, sf);
                }

                string txt = double.IsNaN(DrawnBalance) ? "--" : FmtAmt(DrawnBalance);
                float fBal = H * 0.48f;
                using (Font probe = new Font("Arial", fBal, FontStyle.Bold, GraphicsUnit.Pixel)) {
                    SizeF want = g.MeasureString(txt, probe);
                    if (want.Width > W * 0.9f) fBal *= (W * 0.9f) / want.Width;
                }
                using (Font fb = new Font("Arial", fBal, FontStyle.Bold, GraphicsUnit.Pixel))
                using (Brush sh = new SolidBrush(Color.FromArgb(160, 0, 0, 0)))
                using (Brush bb = new SolidBrush(Color.FromArgb(255, 240, 246, 255))) {
                    SizeF sb = g.MeasureString(txt, fb);
                    float left = (W - sb.Width) / 2f;
                    float top = H * 0.44f;
                    g.DrawString(txt, fb, sh, left + 1.5f, top + 1.5f);
                    g.DrawString(txt, fb, bb, left, top);
                }
            }
        }
        return panel;
    }

    void DrawScreen() {
        float lw = (float)Math.Sqrt(Math.Pow(_fx[1] - _fx[0], 2) + Math.Pow(_fy[1] - _fy[0], 2));
        float lh = (float)Math.Sqrt(Math.Pow(_fx[3] - _fx[0], 2) + Math.Pow(_fy[3] - _fy[0], 2));
        if (lw < 8 || lh < 8) return;

        int pw = 520;
        int ph = (int)Math.Round(pw * (lh / lw));
        if (ph < 24) { ph = 24; pw = (int)Math.Round(ph * (lw / lh)); }

        Bitmap panel = BuildPanel(pw, ph);
        // NOTE: no panel pre-flip on the mirrored side. The mirrored quad
        // already maps the panel's top-left corner onto the screen's physical
        // top-left, so the readout stays readable all by itself; flipping the
        // panel here would mirror the text twice.
        try {
            using (Graphics g = Graphics.FromImage(_canvas)) {
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                PointF[] dest = new PointF[3];
                dest[0] = new PointF((float)(_fx[0] + _shakeX), (float)(_fy[0] + _shakeY));
                dest[1] = new PointF((float)(_fx[1] + _shakeX), (float)(_fy[1] + _shakeY));
                dest[2] = new PointF((float)(_fx[3] + _shakeX), (float)(_fy[3] + _shakeY));
                g.DrawImage(panel, dest);
            }
        } finally { panel.Dispose(); }
    }

    // offline indicator: a small dot, so the screen keeps showing only the
    // label and the number
    void DrawAlert() {
        using (Graphics g = Graphics.FromImage(_canvas)) {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float r = Math.Max(2.5f, (float)(_h * 0.032));
            float cx = (float)(_fx[1] + _fx[2]) / 2f + (float)_shakeX;
            float cy = (float)(_fy[1] + _fy[2]) / 2f + (float)(_h * 0.05) + (float)_shakeY;
            using (Brush b = new SolidBrush(Color.FromArgb(235, 228, 62, 52)))
                g.FillEllipse(b, cx - r, cy - r, r * 2, r * 2);
            using (Pen p = new Pen(Color.FromArgb(210, 255, 255, 255), Math.Max(1f, r * 0.3f)))
                g.DrawEllipse(p, cx - r, cy - r, r * 2, r * 2);
        }
    }

    // Damage numbers scale with the user-set multiplier (the floater-size menu).
    // Everything derived from the line height (cascade spacing, flight
    // distance, jitter) follows, so the animation keeps its proportions.
    float FloaterEm() { return (float)Math.Max(13.0 * _floatMult, 34.0 * _scale * _floatMult); }

    void DrawFloater(Floater fl, Buf dst) {
        double t = fl.T / fl.Dur;
        float em = FloaterEm();
        float pop = t < 0.18 ? (float)(0.62 + 0.38 * (t / 0.18)) : 1f;
        double alpha = t < 0.55 ? 1.0 : (1 - (t - 0.55) / 0.45);
        if (alpha <= 0) return;

        Bitmap bmp = MakeFloaterBitmap(fl.Text, em);
        try {
            Buf src = new Buf(bmp);
            try {
                byte[] p = dst.P, sp = src.P;
                int ds = dst.Stride, ss = src.Stride;
                int w = (int)(src.W * pop), h = (int)(src.H * pop);
                // the shake offset is applied here so the number shakes together
                // with the character instead of hanging in place
            int x0 = fl.X + (int)Math.Round(fl.Jitter * Math.Sin(t * 9)) + (int)Math.Round(_shakeX);
            int y0 = fl.Y - (int)Math.Round(t * em * 2.7) + (int)Math.Round(_shakeY);
            // At 4x size the old left anchor would push wide numbers past the
            // right edge of the canvas, so keep the whole glyph on-screen.
            if (x0 < 0) x0 = 0;
            else if (x0 + w > _w) x0 = _w - w;
                for (int yy = 0; yy < h; yy++) {
                    int syy = (int)(yy / pop); if (syy >= src.H) break;
                    int ty = y0 + yy; if (ty < 0 || ty >= _h) continue;
                    for (int xx = 0; xx < w; xx++) {
                        int sxx = (int)(xx / pop); if (sxx >= src.W) break;
                        int tx = x0 + xx; if (tx < 0 || tx >= _w) continue;
                        int si = syy * ss + sxx * 4;
                        int sa = sp[si + 3]; if (sa == 0) continue;
                        Cs.Blend(p, ty * ds + tx * 4, sp[si + 2], sp[si + 1], sp[si], sa / 255.0 * alpha);
                    }
                }
            } finally { src.Dispose(); }
        } finally { bmp.Dispose(); }
    }

    Bitmap MakeFloaterBitmap(string text, float em) {
        using (Font f = new Font("Arial", em, FontStyle.Bold, GraphicsUnit.Pixel)) {
            Size sz = TextRenderer.MeasureText(text, f, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding);
            int pad = (int)(em * 0.45f);
            Bitmap bmp = new Bitmap(Math.Max(4, sz.Width + pad * 2), Math.Max(4, sz.Height + pad * 2),
                                    PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp)) {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                using (GraphicsPath path = new GraphicsPath()) {
                    path.AddString(text, f.FontFamily, (int)FontStyle.Bold, em, new PointF(pad, pad),
                                   StringFormat.GenericTypographic);
                    using (Pen outline = new Pen(Color.FromArgb(255, 92, 8, 8), em * 0.16f)) {
                        outline.LineJoin = LineJoin.Round;
                        g.DrawPath(outline, path);
                    }
                    using (Brush fill = new SolidBrush(Color.FromArgb(255, 255, 72, 60)))
                        g.FillPath(fill, path);
                }
            }
            return bmp;
        }
    }

    // ---------------------------------------------------------------- tick ---

    void OnTick() {
        try {
            WatchdogTick();                  // the pet must never be unreachable
            if (_pollWant != 0) { int want = _pollWant; _pollWant = 0; PollNow(want == 2); }

            double dt = _timer.Interval / 1000.0;
            bool anim = false;

            if (_snapping) {
                _snapT += dt / 0.16;
                if (_snapT >= 1) { Location = _snapTo; _snapping = false; }
                else {
                    double e = 1 - Math.Pow(1 - _snapT, 3);
                    Location = new Point(
                        (int)Math.Round(_snapFrom.X + (_snapTo.X - _snapFrom.X) * e),
                        (int)Math.Round(_snapFrom.Y + (_snapTo.Y - _snapFrom.Y) * e));
                }
                PushLayer();
            }

            _lastCueAmount = 0;              // diagnostics: what fired this frame
            DrainBankedBalance();            // apply any reading that arrived

            if (_dueGap > 0) _dueGap -= dt;

            // The ONLY place the printed number moves: one step per cue, never
            // more, and never without a cue. The debt is re-derived every time a
            // reading lands, so a reading arriving mid-cue can never strand it.
            double take = 0;
            if (_dueGap <= 0 && _pendingStep <= 0) {
                if (_pending >= _step - 1e-9) take = _step;
                else if (_pending > 1e-9) take = Math.Round(_pending, 2);   // sub-step sliver
            }
            if (take > 0) {
                _pending = Math.Round(_pending - take, 4);
                if (_pending < 1e-9) _pending = 0;
                _bookedAt = Math.Round(_bookedAt - take, 4);
                _bookedBal = Math.Round(_bookedBal - take, 4);
                _pendingStep = take;
                _dueGap = CueGapSec;
            }

            // The cue fires as soon as its slot is due. It deliberately does NOT
            // wait for the previous number to finish flying: with a 0.2s rhythm
            // that wait would stretch every cue to over a second. The numbers
            // stack upwards instead, comet-style, so a run of cues still reads
            // clearly.
            if (_pendingStep > 0) {
                _lastCueAmount = _pendingStep;
                MakeTick(_pendingStep, false);
                _pendingStep = 0;
                anim = true;
            }

            // Rehearsal run: same rhythm as real charges, so a custom amount shows
            // exactly what a burst of real spending looks like. The printed number
            // walks down; the books stay untouched.
            if (_demoLeft > 0 && _pendingStep <= 0 && _dueGap <= 0) {
                _lastCueAmount = _demoAmount;
                MakeTick(_demoAmount, true);
                _demoLeft--;
                _dueGap = CueGapSec;
                anim = true;
            }
            for (int i = _hits.Count - 1; i >= 0; i--) {
                _hits[i].T += dt; anim = true;
                if (_hits[i].Done) _hits.RemoveAt(i);
            }
            for (int i = _floaters.Count - 1; i >= 0; i--) {
                _floaters[i].T += dt; anim = true;
                if (_floaters[i].Done) _floaters.RemoveAt(i);
            }

            if (anim || _dirty) { RenderToCanvas(); PushLayer(); }
        } catch (Exception ex) { Log("tick: " + ex); }
    }

    // fromTest: the menu / tray "test one charge" cue only pretends to spend.
    // It plays the animation and drops the printed number through _testOffset
    // while leaving _realBal and the step yardstick alone, so a later balance
    // refresh neither corrects the number back up nor mistakes the rehearsal for
    // credits actually spent. There is deliberately no cap on the offset: capping
    // it used to freeze the number after ten cues. The printed value clamps at
    // zero instead, and any real spending clears the offset.
    void MakeTick(double amount, bool fromTest) {
        // Cap the simultaneous hurt overlays. Real charges are already capped by
        // the tick loop, but repeatedly clicking "test one charge" used to stack
        // an unbounded number of them and each one added its own shake, which
        // added up to a sprite flying across the screen.
        if (_hits.Count < 3) _hits.Add(new Hit());
        if (fromTest) {
            _testOffset = Math.Round(_testOffset + amount, 4);
        } else {
            _testOffset = 0;                         // real movement clears rehearsals
        }
        Floater fl = new Floater();
        fl.Text = "-" + FmtAmt(amount);
        fl.X = _headX - (int)(_w * 0.06);
        // Cascade: each number that is still in the air pushes the next one up, so
        // a fast run reads as a comet trail instead of a stack printed in place.
        // Counted over a short window, otherwise a long flight would fling a later
        // cue far above the head.
        int trail = 0;
        for (int i = 0; i < _floaters.Count; i++) if (_floaters[i].T < 0.5) trail++;
        if (trail > 3) trail = 3;
        fl.Y = _headY - (int)Math.Round(trail * _floaterStep);
        // At 4x size a deep cascade would start above the canvas and stay
        // invisible for its whole flight, so pin late numbers to the top band.
        int minY = 4 + (int)Math.Round(FloaterEm() * 1.5);
        if (fl.Y < minY) fl.Y = minY;
        fl.Jitter = 7 * _scale * _floatMult;
        _floaters.Add(fl);
        if (_floaters.Count > 20) _floaters.RemoveAt(0);
        PlayHitSound(amount);    // every pop plays, even on top of the last one
        _dirty = true;
    }

    public void MakeTick(double amount) { MakeTick(amount, false); }

    // Every damage number plays the cue on its own MCI alias, so a new hit never
    // cuts off the previous one.
    //
    // A single miss must NOT silence the pet for the rest of the session (it
    // used to: one -1 set _soundEnabled = false and only the menu could undo
    // it). Count consecutive misses instead and only give up when the sound
    // really looks broken; any success resets the counter.
    void PlayHitSound(double amount) {
        if (!_soundEnabled) return;
        SoundPool pool = IsEggStep(amount) ? EggPool() : _sound;
        if (pool == null) return;
        int slot = pool.Play();
        if (slot < 0) {
            _soundMisses++;
            if (_soundMisses == 1 || _soundMisses % 10 == 0)
                Log("sound: nothing played (miss #" + _soundMisses +
                    " failed=" + _sound.Failed + " err=" + _sound.Error + ")");
            if (_soundMisses >= 20) {
                _soundEnabled = false;                   // persistent failure: stop trying
                Log("sound: disabled after " + _soundMisses + " consecutive misses");
            }
        } else {
            _soundMisses = 0;
        }
    }

    // Full-precision rendering for the egg test's failure lines. FmtAmt rounds
    // to two decimals - the very precision under test - so a failure report
    // through it would print "3.25" for 3.2499999983 and hide the real cause.
    static string Dbg(double v) {
        return v.ToString("0.##########", CultureInfo.InvariantCulture) +
               " (round2=" + Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture) + ")";
    }

    // Easter-egg matching: the cue must be the quantity 3.25 / 32.5 / 325 as
    // that number is understood in ordinary decimal arithmetic.
    //
    // The obvious spellings both fail, and the self-test pins why:
    //   * Math.Round(amount, 2) is BANKER'S rounding (midpoint to even), so
    //     3.25006 -> 3.25 but also 3.5 -> 4 and 0.125 -> 0.12. Worse, in
    //     binary, 3.2495 rounds to 3.25 as well, so a neighbouring step of
    //     3.2495 would have played the easter egg.
    //   * A tolerance picks an arbitrary boundary that no user could guess.
    //
    // Rounding through decimal is what people mean by "to two places", so the
    // rule is: the amount, rounded to two decimal places, equals the step.
    // Only the amount itself can be a hair off (the step and the egg values
    // are exactly representable in decimal), and balances are already rounded
    // to four places before they ever reach a cue.
    static bool IsEggStep(double amount) {
        double r = (double)Math.Round((decimal)amount, 2, MidpointRounding.AwayFromZero);
        for (int i = 0; i < EGG_STEPS.Length; i++)
            if (r == EGG_STEPS[i]) return true;
        return false;
    }

    static string EggStepText() {
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < EGG_STEPS.Length; i++) {
            if (i > 0) sb.Append(" / ");
            sb.Append(FmtAmt(EGG_STEPS[i]));
        }
        return sb.ToString();
    }

    // The egg pool, opened on demand the first time a 3.25 / 32.5 / 325 cue
    // fires. Opened through the SAME path the constructor uses; if the sound
    // is off, or the file is not there, this returns null and the plain hit
    // sound is used - the easter egg must never be able to silence a cue.
    SoundPool EggPool() {
        if (_eggTried) return _soundEgg;
        _eggTried = true;
        try {
            string p = Path.Combine(_baseDir, Get("WBPET_EGG_FILE", EGG_FILE));
            if (!File.Exists(p)) {
                Log("egg sound missing: " + p + " (using " + Get("WBPET_SOUND_FILE", "hit.mp3") + ")");
                return null;
            }
            int vol = int.Parse(Get("WBPET_VOLUME", "80"));
            _soundEgg = new SoundPool(p, 4, vol, LogFileIn(_baseDir));
            if (_soundEgg.Failed) { Log("egg sound pool failed: " + _soundEgg.Error); return null; }
            // The amount is logged because this pool is now opened on demand:
            // the line is the only evidence of which charge asked for it, and
            // the self-test reads it back.
            Log("egg sound opened on demand: " + p + " (steps " + EggStepText() +
                ", last cue " + FmtAmt(_lastCueAmount) + ")");
            return _soundEgg;
        } catch (Exception ex) {
            Log("egg sound error: " + ex.Message);
            return null;
        }
    }

    // ------------------------------------------------------------- polling ---
    //
    // Two transports: .NET HttpClient first, then the local Node runtime. Some
    // locked-down Windows setups make schannel refuse to acquire TLS credentials
    // for .NET while Node ships its own OpenSSL stack and still works.

    static readonly HttpClient Http = CreateClient();
    static HttpClient CreateClient() {
        try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }
        // UseCookies MUST be off: with the default CookieContainer enabled the
        // handler silently drops the manually-set Cookie header and the gateway
        // answers 401 (verified 2026-09-29).
        HttpClientHandler handler = new HttpClientHandler();
        handler.UseCookies = false;
        // Go DIRECT. The machine carries HTTP_PROXY/HTTPS_PROXY (WorkBuddy's own
        // local interceptor, http://127.0.0.1:11873) plus lowercase twins; with
        // UseProxy left at its default the handler routes the gateway call
        // through that proxy, which then answers 401. The node fallback ignores
        // *_PROXY by design, so disabling it here keeps both transports honest.
        try { handler.UseProxy = false; } catch { }
        HttpClient c = new HttpClient(handler);
        c.Timeout = TimeSpan.FromSeconds(15);
        return c;
    }

    static string _nodePath;
    static bool _nodeSearched;
    static string FindNode() {
        if (_nodeSearched) return _nodePath;
        _nodeSearched = true;

        string explicitPath = Environment.GetEnvironmentVariable("WBPET_NODE");
        if (!string.IsNullOrEmpty(explicitPath) && File.Exists(explicitPath)) { _nodePath = explicitPath; return _nodePath; }

        string fromPath = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(fromPath)) {
            foreach (string dir in fromPath.Split(';')) {
                if (dir.Trim().Length == 0) continue;
                try {
                    string cand = Path.Combine(dir.Trim(), "node.exe");
                    if (File.Exists(cand)) { _nodePath = cand; return _nodePath; }
                } catch { }
            }
        }
        string[] roots = new string[] {
            Environment.GetEnvironmentVariable("ProgramFiles"),
            Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
            Environment.GetEnvironmentVariable("ProgramW6432"),
            Environment.GetEnvironmentVariable("LOCALAPPDATA"),
            Environment.GetEnvironmentVariable("APPDATA")
        };
        foreach (string r in roots) {
            if (string.IsNullOrEmpty(r)) continue;
            try {
                foreach (string sub in new string[] { "nodejs\\node.exe", "Programs\\nodejs\\node.exe",
                                                      "nvm\\current\\node.exe" }) {
                    string cand = Path.Combine(r, sub);
                    if (File.Exists(cand)) { _nodePath = cand; return _nodePath; }
                }
                if (Directory.Exists(r)) {
                    foreach (string d in Directory.GetDirectories(r, "node-v*")) {
                        string cand = Path.Combine(d, "node.exe");
                        if (File.Exists(cand)) { _nodePath = cand; return _nodePath; }
                    }
                }
            } catch { }
        }
        return _nodePath;
    }

    string FetchBalance() {
        // Keep the FIRST failure, not the fallback's. When the request itself is
        // rejected (401 = credential/UA problem) node fails the same way, and
        // reporting node's exit code buried the real cause during the 2026-09-30
        // bug hunt.
        string first = null;
        try {
            return FetchWithHttp();
        } catch (Exception ex) {
            first = ex.GetType().Name + ": " + ex.Message;
            Log("http transport failed (" + first + "), trying node");
        }
        try {
            return FetchWithNode();
        } catch (Exception ex2) {
            throw new Exception(first + " / node: " + ex2.Message);
        }
    }

    string FetchWithHttp() { return FetchPost(_apiUrl, "{}"); }

    // Same headers, arbitrary URL + body: the summary takes {}, the package
    // ledgers take a filter. Keeping one code path means a header added for the
    // gateway cannot be missing on the "other" request.
    string FetchPost(string url, string json) {
        HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        if (_credIsCookie) req.Headers.TryAddWithoutValidation("Cookie", _token);
        else req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + _token);
        if (_ua.Length > 0) req.Headers.TryAddWithoutValidation("User-Agent", _ua);
        req.Headers.TryAddWithoutValidation("Accept", "application/json");
        req.Headers.TryAddWithoutValidation("Accept-Language", "zh");
        if (_uid.Length > 0) req.Headers.TryAddWithoutValidation("X-User-Id", _uid);
        HttpResponseMessage resp = Http.SendAsync(req).GetAwaiter().GetResult();
        string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        if (!resp.IsSuccessStatusCode) throw new Exception("HTTP " + (int)resp.StatusCode);
        return body;
    }

    string FetchWithNode() {
        string node = FindNode();
        if (node == null) throw new Exception("node not found");
        // ---- why this script reads stdin, not argv or process.env --------
        // The credentials used to travel as environment variables, which meant
        // building a ProcessStartInfo environment. That is impossible on this
        // machine: the OS block carries BOTH spellings of several names
        // (Path/PATH, HTTP_PROXY/http_proxy, HTTPS_PROXY/https_proxy - measured
        // 2026-09-30), and .NET's ProcessStartInfo.Environment / .EnvironmentVariables
        // are case-insensitive StringDictionaries it fills with Add(). Merely
        // READING either property throws
        //   ArgumentException: 宸叉坊鍔犻」銆傚瓧鍏镐腑鐨勫叧閿瓧:鈥渉ttp_proxy鈥濇墍娣诲姞鐨勫叧閿瓧:鈥淗TTP_PROXY鈥?        // and because the throw happens on access, no amount of Remove()/Clear()
        // cleanup can run first (all verified with a real .NET probe). Node
        // itself is fine with the duplicated block; only the .NET side chokes.
        //
        // So: leave the child's environment entirely alone (it inherits the raw
        // block and Node copes) and hand the request parameters over on STDIN.
        // NOT argv: the command line of a process is readable by anything
        // running as this user (task manager, WMI, Process Explorer), so a
        // credential placed there is effectively published machine-wide.
        // Node's require('https') ignores *_PROXY anyway, so the inherited
        // proxies are harmless for this direct call.
        string script =
            "const h=require('https');" +
            "let raw='';" +
            "process.stdin.on('data',d=>{raw+=d});" +
            "process.stdin.on('end',()=>{go(raw)});" +
            "function go(raw){" +
            "let c={};try{c=JSON.parse(raw||'{}')}catch(e){console.error('bad stdin');process.exit(6)}" +
            "const u=new URL(c.url);" +
            "const hdr={'Content-Type':'application/json',Accept:'application/json','Accept-Language':'zh'};" +
            // Guard the missing-key case: the unguarded form built 'Bearer
            // undefined' and turned a clear credential problem into a confusing
            // second-order failure that hid the real exception.
            "if(!c.key){console.error('no key passed to node fallback');process.exit(4)}" +
            "if(c.cookie)hdr.Cookie=c.key;else hdr.Authorization='Bearer '+c.key;" +
            "if(c.ua)hdr['User-Agent']=c.ua;" +
            "if(c.uid)hdr['X-User-Id']=c.uid;" +
            "const r=h.request({method:'POST',hostname:u.hostname,path:u.pathname+u.search,headers:hdr}," +
            "s=>{let b='';s.on('data',x=>b+=x);s.on('end',()=>{" +
            // exit 5 carries the HTTP status so the caller can report the REAL
            // failure (e.g. 401) instead of an opaque node exit code.
            "if(s.statusCode>=300){console.error('http '+s.statusCode);process.exit(5)}" +
            "process.stdout.write(b)})});" +
            "r.on('error',e=>{console.error(String(e.message||e));process.exit(2)});" +
            "r.setTimeout(15000,()=>{console.error('timeout');process.exit(3)});" +
            "r.write('{}');r.end();}";

        // The payload is JSON-encoded and written to the child's stdin, so no
        // quoting rules apply and no Chinese-character surprise can end up on
        // the command line, where other processes could read it.
        string payload = "{"
            + "\"url\":"  + JsonStr(_apiUrl)
            + ",\"key\":" + JsonStr(_token)
            + ",\"uid\":" + JsonStr(_uid)
            + ",\"ua\":"  + JsonStr(_ua)
            + ",\"cookie\":" + (_credIsCookie ? "true" : "false")
            + "}";

        ProcessStartInfo psi = new ProcessStartInfo();
        psi.FileName = node;
        // Only the script rides on the command line; the payload goes to stdin
        // (below). The command line must never carry the credential again.
        psi.Arguments = "-e \"" + script.Replace("\"", "\\\"") + "\"";
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        // NOTE: deliberately NOT touching psi.Environment / psi.EnvironmentVariables
        // here (see the comment above).

        using (Process p = Process.Start(psi)) {
            // Hand the payload over and close the pipe so node's 'end' fires.
            p.StandardInput.Write(payload);
            p.StandardInput.Close();
            string outp = p.StandardOutput.ReadToEnd();
            string errp = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(20000)) { try { p.Kill(); } catch { } throw new Exception("node timeout"); }
            if (p.ExitCode != 0 || outp.Trim().Length == 0)
                throw new Exception("node exit " + p.ExitCode + " " + Trunc(errp));
            if (outp.IndexOf("cycleRemain") < 0 && outp.IndexOf("\"code\"") < 0)
                throw new Exception("node: " + Trunc(outp));
            return outp;
        }
    }

    // Minimal JSON string encoder for the values handed to the node fallback:
    // quotes, backslashes and control characters are the only things that can
    // break the argv payload, and Chinese characters ride through as UTF-8.
    static string JsonStr(string s) {
        if (s == null) return "\"\"";
        StringBuilder b = new StringBuilder(s.Length + 2);
        b.Append('"');
        foreach (char c in s) {
            switch (c) {
                case '"':  b.Append("\\\""); break;
                case '\\': b.Append("\\\\"); break;
                case '\b': b.Append("\\b");  break;
                case '\f': b.Append("\\f");  break;
                case '\n': b.Append("\\n");  break;
                case '\r': b.Append("\\r");  break;
                case '\t': b.Append("\\t");  break;
                default:
                    if (c < ' ') b.Append("\\u").Append(((int)c).ToString("x4"));
                    else b.Append(c);
                    break;
            }
        }
        b.Append('"');
        return b.ToString();
    }

    static string Trunc(string s) {
        if (s == null) return "";
        s = s.Replace("\r", " ").Replace("\n", " ");
        return s.Length > 200 ? s.Substring(0, 200) : s;
    }

    // snap = the user asked for it (the menu's "refresh now", or --shot): the
    // tablet jumps straight to the value just read. snap = false is the
    // background poll, which keeps walking down in whole steps so every step
    // still gets its hurt animation. Synchronous either way, so a menu click is
    // answered on the spot and always logged.
    // The fetch runs on a worker thread and the result is banked for the next
    // frame: with the poll now every couple of seconds, doing the HTTP call on
    // the UI thread would visibly freeze the widget. The banked reading is
    // applied by OnTick, so the tablet still reacts within one frame of it
    // arriving. Only one request is ever in flight.
    void PollNow(bool snap) {
        if (string.IsNullOrEmpty(_token)) {
            _connected = false;
            _status = S_NOKEY;
            _lastPollResult = "no token";
            _dirty = true;
            Log("poll skipped: no token (menu: " + S_SETTOKEN + ")");
            return;
        }
        if (Interlocked.CompareExchange(ref _pollInFlight, 1, 0) != 0) return;
        bool wantSnap = snap;
        int seq = Interlocked.Increment(ref _pollSeq);
        bool wantCheck = wantSnap || (seq % LedgerCheckEvery == 0);
        ThreadPool.QueueUserWorkItem(delegate {
            try {
                string body = FetchBalance();
                double bal = ParseCredits(body);
                if (double.IsNaN(bal)) { Fail("cannot parse response: " + Trunc(body)); return; }
                double tot = ParseCreditsTotal(body);

                // The gateway prints its own total next to the packages, which
                // gives a free consistency check on the scoped parse: if the
                // two ever drift apart the envelope changed shape again, and
                // the log says so before the number looks wrong on screen.
                double selfTotal = ParseTotalField(body);
                if (!double.IsNaN(selfTotal) && Math.Abs(selfTotal - bal) > 0.5)
                    Log("TotalCredits mismatch: total=" + FmtAmt(selfTotal) +
                        " PackagesSum=" + FmtAmt(bal) + " (using PackagesSum)");

                // Second opinion from the raw ledger (see FetchDetailCredits).
                if (wantCheck && !_noNetwork) {
                    double ledger = FetchDetailCredits();
                    if (!double.IsNaN(ledger)) {
                        if (CreditsDisagree(bal, ledger)) {
                            Log("ledger disagrees: summary=" + FmtAmt(bal) +
                                " ledger=" + FmtAmt(ledger) + " -> using ledger");
                            bal = ledger;
                            if (double.IsNaN(tot)) tot = ledger;
                        } else {
                            Log("ledger agrees: " + FmtAmt(ledger));
                        }
                    }
                }

                lock (_hits) {
                    _bankedBal = bal; _bankedSnap = wantSnap; _bankedValid = true;
                    if (!double.IsNaN(tot)) _totalBal = tot;
                }
            } catch (Exception ex) {
                Fail(ex.Message);
            } finally {
                Interlocked.Exchange(ref _pollInFlight, 0);
            }
        });
    }

    // applies a banked reading; called from the render tick
    void DrainBankedBalance() {
        // Offline self-tests must ignore even a reading that was already
        // fetched: a request issued during start-up can land in the middle of a
        // simulated sequence and wipe the pending amount, which made the test
        // (and this bug hunt) lie.
        if (_noNetwork) { lock (_hits) { _bankedValid = false; } return; }
        double bal; bool snap;
        lock (_hits) {
            if (!_bankedValid) return;
            bal = _bankedBal; snap = _bankedSnap; _bankedValid = false;
        }
        ApplyBalance(bal, snap);
        Log("poll ok" + (snap ? " (snap)" : "") +
            ": balance=" + FmtAmt(bal) +
            " printed=" + DrawnText +
            " bookedAt=" + (double.IsNaN(_bookedAt) ? -1 : _bookedAt) +
            " pending=" + _pending.ToString("0.####", CultureInfo.InvariantCulture));
        try {
            string tip = "WB " + DrawnText;
            if (!double.IsNaN(_totalBal)) tip += " / " + FmtAmt(_totalBal);
            _tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
        } catch { }
    }

    // blocking variant, used by the offline self-tests only
    void PollNowSync(bool snap) {
        try {
            string body = FetchBalance();
            double bal = ParseCredits(body);
            if (double.IsNaN(bal)) { Fail("cannot parse response: " + Trunc(body)); return; }
            ApplyBalance(bal, snap);
        } catch (Exception ex) { Fail(ex.Message); }
    }

    // The whole balance bookkeeping lives here. Nothing else may move the printed
    // number: the total still owed is always re-derived from
    // (_bookedAt - bal), so a reading that arrives at any moment can never make
    // the display drift on its own.
    void ApplyBalance(double bal, bool snap) {
        lock (_hits) {
            _connected = true;
            _lastPollResult = "";
            // A good reading ends the backoff and re-arms the one-shot balloon,
            // so a credential that dies again later warns again.
            _pollSkip = 0;
            _credAlertShown = false;
            bool firstReading = double.IsNaN(_realBal);
            _realBal = bal;
            if (firstReading) {
                _bookedAt = bal;
                _bookedBal = bal;
                _pending = 0;
            }
            if (snap) {
                // "refresh now": believe the server exactly, drop rehearsals and
                // anything still queued
                _bookedAt = bal;
                _bookedBal = bal;
                _testOffset = 0;
                _pending = 0;
                _pendingStep = 0;
                _dueGap = 0;
            } else if (!firstReading) {
                // The whole difference becomes the debt. A cue already committed
                // but not yet charged counts as part of it, so it is subtracted
                // rather than overwritten - overwriting it is what used to make
                // the number move by less than one animation.
                double owed = Math.Round(_bookedAt - bal, 4);
                if (owed < -1e-9) {
                    // the server walked the balance back up (a correction, not a
                    // top-up): re-anchor so the debt is only what is uncharged
                    owed = _pending;
                    _bookedAt = Math.Round(bal + owed, 4);
                    _bookedBal = Math.Round(bal + owed, 4);
                }
                if (owed >= _pendingStep - 1e-9) owed = Math.Round(owed - _pendingStep, 4);
                else owed = 0;                    // the cue in flight already covers it
                // cap the queue to keep a huge jump from turning into minutes of
                // animation
                double cap = _step * MaxCuesPerPoll;
                if (owed > cap) owed = cap;
                _pending = owed < 1e-9 ? 0 : owed;
                if (_pending > 1e-9 && _dueGap <= 0) _dueGap = 0;   // first cue now
            }
        }
        _dirty = true;
    }

    void Fail(string why) {
        bool expired = why.IndexOf("401") >= 0;
        lock (_hits) {
            _connected = false;
            _status = expired ? S_EXPIRED : S_LOADING;
            _lastPollResult = why;
        }
        Log("poll FAILED: " + why + (expired
            ? " (credential rejected: expired, or cookie pasted without its bound User-Agent - paste the full cURL via the menu)"
            : ""));
        if (expired) {
            // 1.2.19.5: the tray used to say nothing at all, so a dead session
            // looked exactly like "the numbers stopped moving". One balloon per
            // failure streak, then back off instead of hammering the gateway.
            _pollSkip = CredBackoffTicks;
            if (!_credAlertShown) {
                _credAlertShown = true;
                NotifyCredentialRejected();
            }
        }
        _dirty = true;
    }

    void NotifyCredentialRejected() {
        try {
            if (_tray == null) return;
            _tray.BalloonTipTitle = "WorkBuddy PointPal";
            _tray.BalloonTipText = S_EXPIRED + "\uFF0C\u8BF7\u53F3\u952E\u6253\u5F00\u300C" +
                                   S_SETTINGS + "\u300D\u91CD\u65B0\u7C98\u8D34\u51ED\u8BC1\u3002";
            _tray.BalloonTipIcon = ToolTipIcon.Warning;
            _tray.ShowBalloonTip(6000);
        } catch { }
    }

    // balance = sum of data.Packages[].CycleRemainCapacity.
    //
    // 1.2.19.5: the scan is SCOPED TO THE "Packages" ARRAY. On 2026-10-17 the
    // gateway began returning the same breakdown a second time in
    // CreditGroups[] (plus a TotalCredits total). The old code scanned from
    // "Packages" to the END OF THE BODY, so when the rich body arrived the same
    // credits were counted twice and the widget printed exactly 2x - which then
    // booked an endless charge animation. The desktop client's own parser
    // (parseResourceSummary in app.asar) likewise reads Packages ONLY.
    static readonly Regex RxRemain =
        new Regex("cycleRemain(?:Capacity)?\"?\\s*:\\s*\"?(-?[0-9]+(?:\\.[0-9]+)?)",
                  RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex RxTotal =
        new Regex("cycleTotal(?:Capacity)?\"?\\s*:\\s*\"?(-?[0-9]+(?:\\.[0-9]+)?)",
                  RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // The server's own total. Used for the sanity check and as a last-resort
    // fallback - never as the primary value (the client does not use it either).
    static readonly Regex RxTotalField =
        new Regex("\"TotalCredits\"\\s*:\\s*\"?(-?[0-9]+(?:\\.[0-9]+)?)",
                  RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // One row of the PaaS ledger (free/paid package accounts).
    static readonly Regex RxAcctRemain =
        new Regex("\"CycleCapacityRemainPrecise\"\\s*:\\s*\"?(-?[0-9]+(?:\\.[0-9]+)?)",
                  RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // The literal text of the JSON array that follows `key` (quoted, exact
    // case), brackets balanced and strings skipped. Returns null when absent.
    // Scoping by brackets - rather than by "everything after" - is what makes
    // a duplicated group elsewhere in the envelope harmless.
    static string ArrayAfter(string json, string key) {
        if (json == null) return null;
        int k = json.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (k < 0) return null;
        int open = json.IndexOf('[', k + key.Length);
        if (open < 0) return null;
        bool inStr = false, esc = false;
        int depth = 0;
        for (int i = open; i < json.Length; i++) {
            char c = json[i];
            if (inStr) {
                if (esc) esc = false;
                else if (c == '\\') esc = true;
                else if (c == '"') inStr = false;
                continue;
            }
            if (c == '"') { inStr = true; continue; }
            if (c == '[') depth++;
            else if (c == ']') {
                depth--;
                if (depth == 0) return json.Substring(open, i - open + 1);
            }
        }
        return null;
    }

    static double SumMatches(Regex rx, string json) {
        double sum = 0; int n = 0;
        foreach (Match m in rx.Matches(json)) {
            double v;
            if (double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) {
                if (v > 0) { sum += v; n++; }
            }
        }
        return n > 0 ? sum : double.NaN;
    }

    static double ParseCredits(string json) {
        string scope = ArrayAfter(json, "\"Packages\"");
        if (scope != null) {
            double v = SumMatches(RxRemain, scope);
            if (!double.IsNaN(v)) return v;
        }
        // No usable Packages array: take the server's own total if it printed
        // one. Falling back to "scan the whole body" is what caused 2026-10-17
        // and must never come back.
        return ParseTotalField(json);
    }

    static double ParseTotalField(string json) {
        if (json == null) return double.NaN;
        Match m = RxTotalField.Match(json);
        double v;
        if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float,
                                         CultureInfo.InvariantCulture, out v)) return v;
        return double.NaN;
    }

    static double ParseCreditsTotal(string json) {
        string scope = ArrayAfter(json, "\"Packages\"");
        return scope == null ? double.NaN : SumMatches(RxTotal, scope);
    }

    // ---- second opinion from the PaaS ledger (1.2.19.5) --------------------
    //
    // The summary endpoint is the fast path, but it is also the endpoint that
    // changed shape under us. The two package endpoints return the raw ledger
    // (one row per resource cycle, 55 rows on this account) and are immune to
    // envelope changes up there. They are too heavy to poll (82 KB), so they
    // are used as a CROSS-CHECK: whenever it disagrees by more than a hair, the
    // ledger wins and the disagreement is logged.
    //
    // The codes are the client's own Q enum (renderer/assets/common-*.js);
    // omitting them is a hard 10001 "PackageCodes required".
    static readonly string[] FREE_PKG_CODES = new string[] {
        "TCACA_code_001_PqouKr6QWV", "TCACA_code_008_cfWoLwvjU4",
        "TCACA_code_035_ArVxJcGDsm", "TCACA_code_006_DbXS0lrypC",
        "TCACA_code_039_KRcQj7wUat", "TCACA_code_040_mi9rCYg46x",
        "TCACA_code_007_nzdH5h4Nl0", "TCACA_code_028_NtpWi0jzXs",
        "TCACA_code_037_WxOD3MpI2o", "TCACA_code_029_6wCGEWquYy",
        "TCACA_code_030_BjSt89qTvr"
    };
    static readonly string[] PAID_PKG_CODES = new string[] {
        "TCACA_code_002_AkiJS3ZHF5", "TCACA_code_005_maRGyrHhw1",
        "TCACA_code_003_FAnt7lcmRT", "TCACA_code_023_4xbGhMrE6q",
        "TCACA_code_026_BaESVICNoi", "TCACA_code_027_0FCGVA6vSa",
        "TCACA_code_009_0XmEQc2xOf", "TCACA_code_036_lupO5WgNdG",
        "TCACA_code_038_OhvqZtiPKr"
    };

    // Same host as _apiUrl, sibling path. Returns null when _apiUrl has been
    // pointed somewhere else (WBPET_API override / a fixture) - in that case we
    // simply do not cross-check rather than firing at the real gateway.
    string LedgerUrl(string leaf) {
        int i = _apiUrl.LastIndexOf("/billing/meter/", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        return _apiUrl.Substring(0, i) + "/billing/meter/" + leaf;
    }

    static string PkgBody(string[] codes, bool isFree) {
        StringBuilder b = new StringBuilder();
        b.Append("{\"PackageCodes\":[");
        for (int i = 0; i < codes.Length; i++) {
            if (i > 0) b.Append(',');
            b.Append('"').Append(codes[i]).Append('"');
        }
        b.Append("],\"PageNumber\":1,\"PageSize\":200");
        string day = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (isFree) {
            b.Append(",\"SlicePeriodStartTime\":\"").Append(day).Append(" 00:00:00\"");
            b.Append(",\"SlicePeriodEndTime\":\"").Append(day).Append(" 23:59:59\"");
        } else {
            b.Append(",\"NeedRenewInfo\":true");
        }
        b.Append('}');
        return b.ToString();
    }

    // Sum of one ledger page; NaN when it could not be read (the caller treats
    // NaN as "no second opinion available", never as zero).
    double FetchLedgerSum(string url, string body, string tag) {
        try {
            string r = FetchPost(url, body);
            string scope = ArrayAfter(r, "\"Accounts\"");
            if (scope == null) { Log("ledger " + tag + ": no Accounts array"); return double.NaN; }
            double v = SumMatches(RxAcctRemain, scope);
            Log("ledger " + tag + ": " + (double.IsNaN(v) ? "empty" : FmtAmt(v)));
            return v;
        } catch (Exception ex) {
            Log("ledger " + tag + " failed: " + ex.Message);
            return double.NaN;
        }
    }

    double FetchDetailCredits() {
        string free = LedgerUrl("get-user-resource-free-packages");
        string paid = LedgerUrl("get-user-resource-paid-packages");
        if (free == null) return double.NaN;
        double sum = 0; bool any = false;
        double f = FetchLedgerSum(free, PkgBody(FREE_PKG_CODES, true), "free");
        if (!double.IsNaN(f)) { sum += f; any = true; }
        if (paid != null) {
            double p = FetchLedgerSum(paid, PkgBody(PAID_PKG_CODES, false), "paid");
            if (!double.IsNaN(p)) { sum += p; any = true; }
        }
        return any ? sum : double.NaN;
    }

    // A difference of a few credits is normal (the two endpoints are read
    // milliseconds apart and every message burns credits); a difference of a
    // percent is a shape change. 2% or 1 credit, whichever is larger.
    static bool CreditsDisagree(double a, double b) {
        double tol = Math.Max(1.0, Math.Abs(b) * 0.02);
        return Math.Abs(a - b) > tol;
    }

    void Log(string msg) {
        try {
            EnsureUserDir();
            File.AppendAllText(Path.Combine(_userDir, "pet.log"),
                DateTime.Now.ToString("s") + " " + msg + "\r\n");
        } catch { }
    }

    // ---------------------------------------------------------------- entry --

    [STAThread]
    public static void Run(string baseDir, string[] args) {
        try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); }
        catch { try { Native.SetProcessDPIAware(); } catch { } }

        // A rendering self-check starts from an empty shots\ folder: the last
        // run's PNGs are stale, and keeping them made "which build drew this?"
        // unanswerable. Done before any window is built, so it also covers the
        // case where the run dies halfway and leaves a partial set behind.
        string shotsDir = Path.Combine(baseDir, ShotsSubdir);
        if (WantsSheets(args)) {
            ClearShots(shotsDir);
            try { Directory.CreateDirectory(shotsDir); } catch { }
        }

        bool selftest = Array.IndexOf(args, "--selftest") >= 0;
        bool shot = Array.IndexOf(args, "--shot") >= 0;
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        WbPet pet = new WbPet(baseDir, args);

        if (shot) {
            System.Windows.Forms.Timer shotTimer = new System.Windows.Forms.Timer();
            shotTimer.Interval = 2500;
            shotTimer.Tick += delegate {
                shotTimer.Stop();
                pet.PollNowSyncPublic(false);
                Console.WriteLine("rest  : " + pet.ShakeReport());

                // menu "refresh now" must print the latest value immediately
                Console.WriteLine("shown after auto poll     = " + pet.DrawnText);
                pet.PollNowSyncPublic(true);
                Console.WriteLine("shown after refresh now   = " + pet.DrawnText +
                                  "   (must equal the live balance)");

                // regression: the number must keep stepping for EVERY rehearsal.
                // It used to freeze after ten because the offset was capped.
                pet.PollNowSyncPublic(true);
                string before = pet.DrawnText;
                for (int i = 1; i <= 15; i++) pet.MakeTick(3, true);
                Console.WriteLine("printed before 15 cues    = " + before);
                Console.WriteLine("printed after 15 cues     = " + pet.DrawnText +
                                  "   (must be 45 lower, not frozen)");

                // and a rehearsal must not survive a refresh as phantom spend
                for (int i = 1; i <= 3; i++) pet.MakeTick(pet.StepValue, true);
                Console.WriteLine("shown after 3 test cues   = " + pet.DrawnText);
                pet.PollNowSyncPublic(true);
                Console.WriteLine("shown after refresh again = " + pet.DrawnText +
                                  "   <- back to the live balance, no drift");

                // visual check of a fast run: five cues at the real 0.2s rhythm,
                // captured mid-cascade so the trail spacing can be inspected
                pet.ResetDemoForTest();
                pet.PollNowSyncPublic(true);
                pet.DemoCharge(5);
                pet.PumpTicks(6);                    // first cue lands
                pet.PumpTicks(6);                    // second, 0.2s later
                pet.PumpTicks(6);                    // third
                pet.SaveCanvas(Path.Combine(pet.ShotsDir, "_shot_cascade.png"));
                Console.WriteLine("cascade: " + pet.ShakeReport() +
                                  "  floaters=" + pet.FloaterCountPublic +
                                  "  printed=" + pet.DrawnText);
                pet.PumpTicks(360);                  // let the run finish
                Console.WriteLine("cascade settled -> printed " + pet.DrawnText);

                pet.ResetDemoForTest();
                pet.MakeTick(pet.StepValue, true);   // rehearsal for the screenshot
                pet.BurnFrames(2);
                Console.WriteLine("shake1: " + pet.ShakeReport());
                pet.SaveCanvas(Path.Combine(pet.ShotsDir, "_shot_flash.png"));
                pet.BurnFrames(3);
                Console.WriteLine("shake2: " + pet.ShakeReport());
                pet.BurnFrames(9);
                Console.WriteLine("after : " + pet.ShakeReport());
                pet.SaveCanvas(Path.Combine(pet.ShotsDir, "shot.png"));
                Console.WriteLine("shot ok: connected=" + pet._connected +
                                  " drawn=" + pet.DrawnText);

                // visual regression for the right-side mirror and a wide mac
                // character: render both, then restore the user's settings.
                bool prevRight = pet._sideRight;
                string prevChar = pet._char.Id;
                pet.SetSide(true, true);
                pet.MakeTick(pet.StepValue, true);
                pet.BurnFrames(2);
                pet.SaveCanvas(Path.Combine(pet.ShotsDir, "_shot_right.png"));
                Console.WriteLine("right: " + pet.ShakeReport() + " canvas=" + pet._w + "x" + pet._h);
                pet.SetCharacter("claude");
                pet.SetSide(false, true);
                pet.BurnFrames(2);
                pet.SaveCanvas(Path.Combine(pet.ShotsDir, "_shot_claude_left.png"));
                Console.WriteLine("claude-left: " + pet.ShakeReport() + " canvas=" + pet._w + "x" + pet._h);
                pet.SetSide(true, true);
                pet.BurnFrames(2);
                pet.SaveCanvas(Path.Combine(pet.ShotsDir, "_shot_claude_right.png"));
                Console.WriteLine("claude-right: " + pet.ShakeReport() + " canvas=" + pet._w + "x" + pet._h);
                pet.SetCharacter("claude-detailed");
                pet.SetSide(false, true);
                pet.BurnFrames(2);
                pet.SaveCanvas(Path.Combine(pet.ShotsDir, "_shot_detailed.png"));
                Console.WriteLine("detailed-left: " + pet.ShakeReport() + " canvas=" + pet._w + "x" + pet._h);
                pet.SetCharacter(prevChar);
                pet.SetSide(prevRight, true);
                Console.WriteLine("state restored: char=" + pet._char.Id +
                                  " side=" + (pet._sideRight ? "right" : "left"));
                Application.Exit();
            };
            shotTimer.Start();
            Application.Run(pet);
            return;
        }

        if (selftest) {
            Console.WriteLine("diag: " + pet.Diag());
            pet.GoOffline();                  // deterministic: no background poll
            pet.RunSelfTest(baseDir);
            Application.Exit();
            return;
        }

        if (Array.IndexOf(args, "--credtest") >= 0) {
            pet.GoOffline();
            pet.RunCredTest();
            Application.Exit();
            return;
        }

        // Real end-to-end credential check (--credcheck <file>): parse a pasted
        // cURL with the app's own parser and POST it to the real gateway.
        // --cryptest: round-trip the DPAPI credential store using a temp file
        // only. Proves (a) the sealed bytes carry no plaintext, (b) they read
        // back byte-identical, and (c) a 1.2.17-style plain-text file still
        // loads - the upgrade path must not log anyone out.
        if (Array.IndexOf(args, "--cryptest") >= 0) {
            ExeEntry.RunCryptTest();
            return;
        }

        int credIdx = Array.IndexOf(args, "--credcheck");
        if (credIdx >= 0) {
            pet.GoOffline();                 // stop the background poll, keep the net open
            string cpath = (credIdx + 1 < args.Length) ? args[credIdx + 1] : "";
            pet.RunCredCheck(cpath);
            Application.Exit();
            return;
        }

        if (Array.IndexOf(args, "--soundtest") >= 0) {
            RunSoundTest();
            Application.Exit();
            return;
        }

        // Easter-egg self check (--eggsound): proves offline and without an
        // audio device that exactly 3.25 / 32.5 / 325 route to the egg pool
        // and that a nearby value does NOT (the tolerance must not be so wide
        // that a plain 3.2 or a 320 charge sets it off).
        if (Array.IndexOf(args, "--eggsound") >= 0) {
            pet.GoOffline();
            pet.RunEggSoundTest();
            Application.Exit();
            return;
        }

        // --eggsoundlive <amount> [--eggopen]: drive ONE real cue of `amount`
        // through the production path (MakeTick -> PlayHitSound), so the actual
        // wiring is exercised, not just the decision table. --eggsound proves
        // "3.25 should route to the egg"; this proves "it did". Needs an audio
        // device. Prints the egg pool's own log line back out.
        int egIdx = Array.IndexOf(args, "--eggsoundlive");
        if (egIdx >= 0) {
            double amt = 3.25;
            if (egIdx + 1 < args.Length)
                double.TryParse(args[egIdx + 1], NumberStyles.Float,
                                CultureInfo.InvariantCulture, out amt);
            pet.GoOffline();
            pet.RunEggLiveCheck(amt);
            Application.Exit();
            return;
        }

        // --aliascheck: end-to-end reproduction of the MCI alias-collision
        // defect. Needs an audio device.
        if (Array.IndexOf(args, "--aliascheck") >= 0) {
            pet.RunAliasCheck();
            Application.Exit();
            return;
        }

        // Update-check self check (--updtest). Offline and network-free: it
        // pins the version comparison and the URL shape, which are the two
        // places a silent wrongness would tell every user "you're up to date".
        if (Array.IndexOf(args, "--updtest") >= 0) {
            pet.GoOffline();
            pet.RunUpdateTest();
            Application.Exit();
            return;
        }

        // --updnet [owner/repo]: the ONE mode that touches the network. It runs
        // the production fetch path once against a live repo and prints what
        // came back. --updtest must stay offline and deterministic, so the
        // live probe lives here instead of inside it.
        // Default target is the shipped repo; pass "cli/cli" etc. to probe
        // the redirect trick against any repo you like.
        int netIdx = Array.IndexOf(args, "--updnet");
        if (netIdx >= 0) {
            string target = (netIdx + 1 < args.Length) ? args[netIdx + 1] : (UPD_OWNER + "/" + UPD_REPO);
            pet.GoOffline();
            pet.RunUpdateNetProbe(target);
            Application.Exit();
            return;
        }

        // About-box self check (--abouttest). Asserts what a screenshot cannot:
        // that "About" really is the second-to-last entry (directly above Quit)
        // and that the version the box would print matches the exe's metadata.
        if (Array.IndexOf(args, "--abouttest") >= 0) {
            pet.GoOffline();
            pet.RunAboutTest();
            Application.Exit();
            return;
        }

        // --updsheet <dir>: render the update-check notice + all its strings,
        // so the wording is reviewable offline.
        int updSheetIdx = Array.IndexOf(args, "--updsheet");
        if (updSheetIdx >= 0) {
            string arg = (updSheetIdx + 1 < args.Length) ? args[updSheetIdx + 1] : "";
            string dir = WbPet.ResolveSheetDir(pet.ShotsDir, arg);
            pet.GoOffline();
            pet.SaveUpdateSheet(dir);
            Application.Exit();
            return;
        }

        // --setsheet <dir>: render every tab of the settings dialog (a window
        // otherwise only reachable by right-clicking the pet), so its layout is
        // reviewable offline at the real DPI.
        int setSheetIdx = Array.IndexOf(args, "--setsheet");
        if (setSheetIdx >= 0) {
            string arg = (setSheetIdx + 1 < args.Length) ? args[setSheetIdx + 1] : "";
            string dir = WbPet.ResolveSheetDir(pet.ShotsDir, arg);
            pet.GoOffline();
            pet.SaveSettingsSheet(dir);
            Application.Exit();
            return;
        }

        // --pickertest: prove a NumPicker reopens on the value it was given
        // rather than falling back to its first preset (the "settings reset
        // themselves" bug, 2026-10-13). Headless: builds the pickers only.
        if (Array.IndexOf(args, "--pickertest") >= 0) {
            pet.GoOffline();
            pet.RunPickerTest();
            Application.Exit();
            return;
        }

        // --rescuetest: the "pet vanished when the cable was pulled" report
        // (2026-10-13). Recreates the geometry of a monitor disappearing and
        // proves the rescue brings the widget back onto a real screen - while
        // a widget that is still reachable is NOT moved.
        if (Array.IndexOf(args, "--rescuetest") >= 0) {
            pet.GoOffline();
            pet.RunRescueTest();
            Application.Exit();
            return;
        }

        // --parsetest: the balance doubling of 2026-10-17. Pure string
        // fixtures - the two bodies the gateway alternates between - plus the
        // ledger shape and the credential-page verdict texts.
        if (Array.IndexOf(args, "--parsetest") >= 0) {
            pet.GoOffline();
            pet.RunParseTest();
            Application.Exit();
            return;
        }

        // --credprobe: the settings window's credential check, once, against the
        // live gateway. Deliberately does NOT go offline - the whole point is to
        // see what the real endpoint answers.
        if (Array.IndexOf(args, "--credprobe") >= 0) {
            pet.RunCredentialProbeTest();
            Application.Exit();
            return;
        }

        // --aboutsheet <png>: render the About dialog itself (it is a real form
        // now, not a MessageBox), so its layout is reviewable headlessly.
        int sheetIdx = Array.IndexOf(args, "--aboutsheet");
        if (sheetIdx >= 0) {
            string dst = WbPet.ResolveShot(pet.ShotsDir,
                (sheetIdx + 1 < args.Length) ? args[sheetIdx + 1] : "", "aboutsheet.png");
            pet.GoOffline();
            pet.SaveAboutSheet(dst);
            Application.Exit();
            return;
        }

        // --updfound <png>: render the "new version found" dialog. A MessageBox
        // cannot relabel its buttons, so this prompt is a real form whose
        // captions and note line are worth checking on the shipped binary.
        int foundIdx = Array.IndexOf(args, "--updfound");
        if (foundIdx >= 0) {
            string dst = WbPet.ResolveShot(pet.ShotsDir,
                (foundIdx + 1 < args.Length) ? args[foundIdx + 1] : "", "updfound.png");
            pet.GoOffline();
            pet.SaveUpdateFoundSheet(dst);
            Application.Exit();
            return;
        }

        // --toksheet <png>: render the credential dialog itself (the window
        // both first run and the tray menu show) so its layout can be checked
        // at the real DPI without clicking through a modal dialog.
        int tokIdx = Array.IndexOf(args, "--toksheet");
        if (tokIdx >= 0) {
            string dst = WbPet.ResolveShot(pet.ShotsDir,
                (tokIdx + 1 < args.Length) ? args[tokIdx + 1] : "", "toksheet.png");
            pet.GoOffline();
            pet.SaveTokenSheet(dst);
            Application.Exit();
            return;
        }

        // Real-device audio probe (--soundprobe): unlike --soundtest this one
        // needs a working audio device and actually opens the pool, so it can
        // report what MCI says about each alias right after open and after a
        // burst. Run it on the user's machine to see where a cue is being lost.
        if (Array.IndexOf(args, "--soundprobe") >= 0) {
            RunSoundProbe(baseDir);
            Application.Exit();
            return;
        }

        // A/B/C soak (--soundab [n]): compare the three historical rules.
        int abIdx = Array.IndexOf(args, "--soundab");
        if (abIdx >= 0) {
            int n = 30;
            if (abIdx + 1 < args.Length) int.TryParse(args[abIdx + 1], out n);
            if (n <= 0) n = 30;
            RunSoundAb(baseDir, n);
            Application.Exit();
            return;
        }

        // Long-rhythm soak (--soundsoak [n]): the real-world burst, default 50.
        int soakIdx = Array.IndexOf(args, "--soundsoak");
        if (soakIdx >= 0) {
            int n = 50;
            if (soakIdx + 1 < args.Length) int.TryParse(args[soakIdx + 1], out n);
            if (n <= 0) n = 50;
            RunSoundSoak(baseDir, n);
            Application.Exit();
            return;
        }

        if (Array.IndexOf(args, "--simchain") >= 0) {
            pet.GoOffline();                  // deterministic: no background poll/ticks
            // Feeds a chain of balance readings through the accounting layer and
            // prints what the tablet would show. The number must only fall in
            // whole steps, and a reading that crosses a step must be charged on
            // the spot (not held until the next poll).
            Console.WriteLine("step=" + pet.StepValue.ToString("0.##") +
                              " per cue, " + pet.CueGapText + "s apart" +
                              "   (one step per animation)");
            // single reading, then watch every tick
            pet.ResetDemoForTest();
            pet.ApplyBalancePublic(3000, true);
            Console.WriteLine("  reset            : " + pet.StateText);
            pet.ApplyBalancePublic(2999, false);   // exactly one credit
            Console.WriteLine("  server -> 2999   : " + pet.StateText);
            for (int i = 1; i <= 6; i++) {
                pet.PumpTicks(1);
                Console.WriteLine("    tick " + i + "         : " + pet.StateText +
                                  "  printed=" + pet.DrawnText);
            }
            Console.WriteLine();

            Console.WriteLine("a 5-credit drop becomes FIVE animations:");
            pet.ResetDemoForTest();
            pet.ApplyBalancePublic(3000, true);
            Console.WriteLine("  reset -> printed " + pet.DrawnText + "  (bookedAt " +
                              pet.BookedAtText + ")");
            pet.ApplyBalancePublic(2995, false);        // 5 down
            Console.WriteLine("  server -> 2995  : owed " +
                              pet.PendingCount.ToString("0.####") +
                              "  -> printed still " + pet.DrawnText);
            for (int i = 1; i <= 8; i++) {
                pet.PumpTicks(9);                        // ~0.3s per sample
                Console.WriteLine("    +0.3s -> printed " + pet.DrawnText +
                                  "  cue " + pet.LastCueText +
                                  "  owed " + pet.PendingCount.ToString("0.####"));
            }
            pet.PumpTicks(120);
            Console.WriteLine("  settled -> printed " + pet.DrawnText +
                              "  (3000 - 5 = 2995, 5 animations)");
            Console.WriteLine();

            double[] chain = new double[] { 3000, 2999, 2998, 2997, 2998, 2995, 2990 };
            pet.ResetDemoForTest();
            for (int i = 0; i < chain.Length; i++) {
                pet.ApplyBalancePublic(chain[i], false);
                string line = "  read " + chain[i].ToString("0") +
                              " -> printed " + pet.DrawnText;
                pet.PumpTicks(60);                  // let queued cues land
                Console.WriteLine(line + " -> after cues " + pet.DrawnText +
                                  (Math.Abs(pet.PendingCount) > 1e-9
                                     ? "  (still owed " + pet.PendingCount.ToString("0.####") + ")"
                                     : ""));
            }

            Console.WriteLine();
            Console.WriteLine("a big jump is paid off one step at a time, never merged:");
            pet.ResetDemoForTest();
            pet.ApplyBalancePublic(3000, true);
            Console.WriteLine("  reset -> printed " + pet.DrawnText);
            pet.ApplyBalancePublic(2975, false);   // 25 down = 25 animations
            for (int i = 1; i <= 6; i++) {
                pet.PumpTicks(24);                  // ~0.8s per sample
                Console.WriteLine("    +0.8s -> printed " + pet.DrawnText +
                                  "  cue " + pet.LastCueText +
                                  "  owed " + pet.PendingCount.ToString("0.####"));
            }
            pet.PumpTicks(900);                     // let the whole run finish
            Console.WriteLine("  settled -> printed " + pet.DrawnText +
                              "  (3000 - 25 = 2975)");

            Console.WriteLine();
            Console.WriteLine("demo cues only offset what is printed:");
            pet.ResetDemoForTest();
            pet.ApplyBalancePublic(3000, true);
            Console.WriteLine("  reset -> printed " + pet.DrawnText);
            pet.DemoCharge(5);                      // same rhythm as a real burst
            for (int i = 1; i <= 5; i++) {
                pet.PumpTicks(16);
                Console.WriteLine("    +0.53s -> printed " + pet.DrawnText +
                                  "  cuesLeft " + pet.DemoLeftText);
            }
            pet.PumpTicks(200);
            Console.WriteLine("  all demo cues done -> printed " + pet.DrawnText +
                              "  cuesLeft " + pet.DemoLeftText);
            pet.ApplyBalancePublic(2970, false);   // 30 of real spending = 30 cues
            pet.PumpTicks(900);
            Console.WriteLine("  real spend to 2970 -> printed " + pet.DrawnText +
                              "  (demo offset cleared, real cues paid off)");
            Application.Exit();
            return;
        }

        Application.Run(pet);
    }

    public string Diag() {
        return "cm=" + _cm.ToString("0.##") + " canvas=" + _w + "x" + _h +
               " scale=" + _scale.ToString("F4") + " step=" + _step.ToString("0.##") +
               " status=" + _status +
               " corner=" + Location.X + "," + Location.Y;
    }

    // reports the shake offset and where the two text layers actually land, so
    // "the text shakes with the character" can be checked numerically
    public string ShakeReport() {
        double panelCx = (_fx[1] + _fx[2]) / 2 + _shakeX;
        double panelCy = (_fy[1] + _fy[2]) / 2 + _shakeY;
        string f = "none";
        if (_floaters.Count > 0) {
            Floater fl = _floaters[_floaters.Count - 1];
            double t = fl.T / fl.Dur;
            float em = FloaterEm();
            f = "(" + (fl.X + (int)Math.Round(fl.Jitter * Math.Sin(t * 9)) + (int)Math.Round(_shakeX)) +
                "," + (fl.Y - (int)Math.Round(t * em * 2.7) + (int)Math.Round(_shakeY)) + ")";
        }
        return "shake=(" + _shakeX.ToString("F1") + "," + _shakeY.ToString("F1") + ")" +
               " screenText=(" + panelCx.ToString("F1") + "," + panelCy.ToString("F1") + ")" +
               " floater=" + f;
    }

    public bool ConnectedFlag { get { return _connected; } }
    public double RealBalance { get { return _realBal; } }
    public double PendingCount { get { return _pending; } }
    public string BookedAtText { get { return double.IsNaN(_bookedAt) ? "--" : FmtAmt(_bookedAt); } }
    public string DemoLeftText { get { return _demoLeft.ToString(CultureInfo.InvariantCulture); } }
    public string CueGapText { get { return CueGapSec.ToString("0.#", CultureInfo.InvariantCulture); } }
    public int FloaterCountPublic { get { return _floaters.Count; } }
    // clears in-flight animation state so self-test scenarios stay independent
    public void ResetDemoForTest() {
        _hits.Clear(); _floaters.Clear(); _pendingStep = 0; _demoLeft = 0; _dueGap = 0;
    }
    public double StepValue { get { return _step; } }
    public string LastCueText {
        get { return _lastCueAmount > 0 ? "-" + FmtAmt(_lastCueAmount) : "(none)"; }
    }
    public void ApplyBalancePublic(double bal, bool snap) { ApplyBalance(bal, snap); }
    // --aliascheck: end-to-end reproduction of the 2026-10-07 defect.
    //
    // MCI aliases are PROCESS-GLOBAL. Both pools used to pick "wbcppet0..3", so
    // opening the egg pool ran "close wbcppet0" + "open er.mp3 ... alias
    // wbcppet0" and silently rebound the HIT pool's handles to er.mp3. From
    // then on every cue played er.mp3, at any step, real or rehearsal - the
    // user reported exactly that ("我先切到 5 再测试扣费，出来的声音都是 er").
    //
    // The decision table (--eggsound) could not catch it: that test never opens
    // a device, and a real cue probe runs in a fresh process, where the egg pool
    // has never been opened. This probe is the one that reproduces the sequence:
    // open the egg pool, THEN place a plain cue, and compare the alias lengths.
    // hit.mp3 and er.mp3 differ in length, so the number identifies the file
    // without an MCI "which file" query (there is none).
    public void RunAliasCheck() {
        Console.WriteLine("MCI alias isolation (--aliascheck)");
        GoOffline();
        _soundEnabled = true;
        if (_sound == null) { Console.WriteLine("  SKIP no hit pool (no audio device?)"); return; }

        string hitBefore = _sound.DbgInfo(0);
        Console.WriteLine("  hit alias0 before : " + hitBefore);

        Console.WriteLine("  -- firing a 3.25 cue (opens the egg pool) --");
        MakeTick(3.25, true);
        System.Threading.Thread.Sleep(300);

        string hitAfter = _sound.DbgInfo(0);
        string eggAfter = _soundEgg == null ? "(egg pool not opened)" : _soundEgg.DbgInfo(0);
        Console.WriteLine("  hit alias0 after  : " + hitAfter);
        Console.WriteLine("  egg alias0        : " + eggAfter);

        // A plain cue must still land on the hit pool's own file.
        int before = _sound.Plays;
        MakeTick(5, true);
        System.Threading.Thread.Sleep(300);
        string hitFinal = _sound.DbgInfo(0);
        Console.WriteLine("  plain 5 cue       : hit.Plays " + before + " -> " + _sound.Plays);
        Console.WriteLine("  hit alias0 final  : " + hitFinal);

        int bad = 0;
        if (hitBefore != hitFinal) { bad++; Console.WriteLine("  FAIL hit alias changed after the egg pool opened"); }
        if (hitAfter.Contains("length=196")) { bad++; Console.WriteLine("  FAIL hit alias was rebound to er.mp3 (length 196)"); }
        if (_sound.Plays == before) { bad++; Console.WriteLine("  FAIL the plain cue did not play on the hit pool"); }
        if (_soundEgg != null && !eggAfter.Contains("length=196")) {
            Console.WriteLine("  note: egg alias length is " + eggAfter + " (expected er.mp3 = 196)");
        }
        Console.WriteLine(bad == 0 ? "ALIAS CHECK: ISOLATED OK" : "ALIAS CHECK: FAILURES=" + bad);
        if (bad != 0) Environment.ExitCode = 1;
    }
    // drives the real tick loop without the UI timer, for --simchain
    public void PumpTicks(int n) { for (int i = 0; i < n; i++) OnTickProbe(); }
    // tests must never talk to the network: that races with the simulated readings
    public void PollNowSyncPublic(bool snap) { PollNowSync(snap); }

    // --credprobe: run the credential check ONCE against the real gateway and
    // print the verdict, i.e. exactly what the settings window's status line
    // would show. Read-only: it never touches the balance or the credential
    // file (test modes are _suppressSave).
    public void RunCredentialProbeTest() {
        Console.WriteLine("credential probe (--credprobe)");
        Console.WriteLine("  token present : " + (!string.IsNullOrEmpty(_token)));
        Console.WriteLine("  user-agent    : " + (_ua.Length > 0 ? _ua : "(empty)"));
        int r = ProbeCredential();
        Console.WriteLine("  verdict       : " + r + " (" +
                          (r == 0 ? "accepted" : r == 1 ? "rejected" : "could not tell") + ")");
        Console.WriteLine("  status line   : " + SettingsDialog.CredStatusText(
                          r == 0 ? SettingsDialog.CredOk : (r == 1 ? SettingsDialog.CredBad : SettingsDialog.CredNet)));
        Console.WriteLine("CRED PROBE: " + (r == 0 ? "OK" : "NOT-OK"));
    }
    // Exposes the credential parser to --credtest. Kept as a thin wrapper so
    // the regression suite exercises the REAL SetToken, not a copy of it.
    public void SetTokenForTest(string raw) { SetToken(raw); }
    // Wipes credential state so a --credtest case cannot inherit whatever the
    // environment seeded at construction time. Without this the suite compares
    // against stale values and reports a false PASS.
    public void ClearCredForTest() {
        _token = "";
        _uid = "";
        _ua = "";
        _credIsCookie = false;
    }
    public string CredStateForTest {
        get {
            return (_credIsCookie ? "cookie" : "bearer") + "|" +
                   (_token == null ? "" : _token) + "|" + (_ua == null ? "" : _ua) + "|" +
                   (_uid == null ? "" : _uid);
        }
    }
    public void GoOffline() {
        _noNetwork = true;
        _pollWant = 0;
        try { _timer.Stop(); _poll.Stop(); } catch { }
    }
    public string StateText { get { return "real=" + _realBal.ToString("0.####") + " printed=" + _bookedBal.ToString("0.####") + " bookedAt=" + _bookedAt.ToString("0.####") + " pend=" + _pending.ToString("0.####") + " step=" + _pendingStep.ToString("0.####") + " gap=" + _dueGap.ToString("0.###") + " hits=" + _hits.Count + " floaters=" + _floaters.Count + " dt=" + (_timer.Interval/1000.0).ToString("0.###"); } }
    void OnTickProbe() { OnTick(); }
    public string DrawnText {
        get { return double.IsNaN(DrawnBalance) ? "--" : FmtAmt(DrawnBalance); }
    }

    // advance animation state without the timer, used by --shot
    public void BurnFrames(int n) {
        for (int i = 0; i < n; i++) {
            double dt = 0.033;
            for (int k = _hits.Count - 1; k >= 0; k--) { _hits[k].T += dt; if (_hits[k].Done) _hits.RemoveAt(k); }
            for (int k = _floaters.Count - 1; k >= 0; k--) { _floaters[k].T += dt; if (_floaters[k].Done) _floaters.RemoveAt(k); }
        }
        RenderToCanvas();
        PushLayer();
    }

    public void RunSelfTest(string baseDir) {
        PollNowSync(true);                // start from the live value
        MakeTick(_step, false);
        for (int i = 0; i < 6; i++) OnTick();
        RenderToCanvas();
        SaveCanvas(Path.Combine(_shotsDir, "selftest.png"));
        Console.WriteLine("selftest.png written; connected=" + _connected +
                          " drawn=" + DrawnText + " lastPoll=" + _lastPollResult);
    }

    // ------------------------------------------------------------------------
    // Credential-parser regression suite (--credtest).
    //
    // Guards the 2026-09-30 defect: a whole "Copy as cURL" payload was silently
    // mis-parsed, so the widget sat on 401 for every paste.
    //
    // ROOT CAUSE (confirmed against the real .NET regex engine):
    //   The cookie was only ever looked for behind cURL's own "-b" / "--cookie"
    //   FLAG. Chrome/Firefox "Copy as cURL" do not emit that flag - they emit a
    //   "-H 'cookie: ...'" HEADER. So the cookie regex found nothing, then the
    //   bearer branch (_token = Authorization) also found nothing, and SetToken
    //   bailed out leaving the credential empty.
    //
    //   A second, latent defect: the per-header patterns each anchored on a bare
    //   "-H", so the leading "-H 'accept: ...'" could swallow the later
    //   "-H 'user-agent: ...'" match. The tolerant single-pass scanner used now
    //   reads every "-X 'name: value'" pair at once and cannot drop one.
    //
    // A non-zero exit code tells the build/CI that parsing regressed.
    // ------------------------------------------------------------------------
    public void RunCredTest() {
        const string UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                          "(KHTML, like Gecko) Chrome/154.0.0.0 Safari/537.36 Edg/154.0.0.0";
        const string CK = "_gcl_au=1.1.1090787589.1790497611; session=n2ylJ3qR4KiAqvw9delT2Q|1791114916|abc";

        int pass = 0, fail = 0, firstFail = 0;
        Action<string, string, string, string> chk = delegate (string name, string curl,
                                                              string wantTok, string wantUa) {
            ClearCredForTest();          // hermetic: never inherit env-seeded state
            SetTokenForTest(curl);
            string tok = _token; string ua = _ua;
            bool ok = tok == wantTok && ua == wantUa;
            Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + name +
                              (ok ? "" : "\n          got  tok=" + tok.Length + "ch ua=" + ua.Length + "ch" +
                                         "\n          want tok=" + wantTok.Length + "ch ua=" + wantUa.Length + "ch"));
            if (ok) pass++; else { fail++; if (firstFail == 0) firstFail = 1; }
        };

        Console.WriteLine("credential parser regression (--credtest)");

        // Guard the 2026-09-30 data loss: this suite feeds fixtures through
        // SetToken(), which persists via SaveToken(). A run therefore REPLACED the
        // live token.txt with the CK fixture and left the widget on 401. The file
        // must come out byte-identical; _suppressSave is what guarantees it.
        // Both candidate locations are watched: on a not-yet-migrated install the
        // live file is still in the data folder root.
        string[] tokPaths = new string[] {
            TokenPath, Path.Combine(_baseDir, "token.txt")
        };
        string tokPath = tokPaths[0];
        bool tokHad = false;
        for (int i = 0; i < tokPaths.Length; i++) {
            if (File.Exists(tokPaths[i])) { tokPath = tokPaths[i]; tokHad = true; break; }
        }
        string tokBefore = tokHad ? HashFile(tokPath) : "";
        Console.WriteLine("  guard: live token.txt " + (tokHad ? "present at " + tokPath + ", will verify unchanged" : "absent, will verify still absent"));

        // the exact shape that failed: one line, single quotes, extra -H in front
        chk("chrome copy-as-cURL, one line", 
            "curl 'https://www.workbuddy.cn/billing/meter/get-user-resource-summary' " +
            "-H 'accept: application/json, text/plain, */*' " +
            "-H 'cookie: " + CK + "' " +
            "-H 'user-agent: " + UA + "' " +
            "--data-raw '{}'", CK, UA);

        // backslash continuations, capitalised header names
        chk("multiline + capitalised names",
            "curl 'https://www.workbuddy.cn/billing/meter/get-user-resource-summary' \\\n" +
            "  -H 'Accept: application/json' \\\n" +
            "  -H 'Cookie: " + CK + "' \\\n" +
            "  -H 'User-Agent: " + UA + "' \\\n" +
            "  --data-raw '{}'", CK, UA);

        // cURL's own cookie / UA flags instead of -H
        chk("--cookie and -A flags",
            "curl 'https://x/y' -H 'accept: */*' --cookie '" + CK + "' -A '" + UA + "'", CK, UA);

        // Firefox export
        chk("firefox style, --compressed",
            "curl 'https://x/y' -H 'User-Agent: " + UA + "' -H 'Accept: */*' " +
            "-H 'Cookie: " + CK + "' --compressed", CK, UA);

        // bearer + X-User-Id
        ClearCredForTest();
        SetTokenForTest("curl 'https://x/y' -H 'authorization: Bearer eyJhbGciOi.J9.abc' " +
                        "-H 'user-agent: " + UA + "' -H 'x-user-id: 8f3a-91bc'");
        bool bOk = _token == "eyJhbGciOi.J9.abc" && _ua == UA && _uid == "8f3a-91bc" && !_credIsCookie;
        Console.WriteLine((bOk ? "  PASS  " : "  FAIL  ") + "bearer + X-User-Id");
        if (bOk) pass++; else fail++;

        // the cookie must survive verbatim - it contains '=' of its own
        ClearCredForTest();
        SetTokenForTest("curl 'https://x/y' -H 'cookie: " + CK + "'");
        bool vOk = _token == CK;
        Console.WriteLine((vOk ? "  PASS  " : "  FAIL  ") + "cookie value not mangled by label stripping");
        if (vOk) pass++; else fail++;

        // bare cookie paste (no cURL) must still work
        ClearCredForTest();
        SetTokenForTest(CK);
        bool pOk = _token == CK && _credIsCookie;
        Console.WriteLine((pOk ? "  PASS  " : "  FAIL  ") + "bare cookie paste, no cURL");
        if (pOk) pass++; else fail++;

        // a cURL paste that carries NEITHER cookie nor Authorization must not
        // silently succeed with a half-filled credential
        ClearCredForTest();
        SetTokenForTest("curl 'https://x/y' -H 'accept: */*' --data-raw '{}'");
        bool nOk = _token.Length == 0;
        Console.WriteLine((nOk ? "  PASS  " : "  FAIL  ") + "unusable cURL leaves credential empty");
        if (nOk) pass++; else fail++;

        // ---- real-world shape: Chrome's multi-line "Copy as cURL", which uses
        // --url and passes the cookie via the -b FLAG while the UA is a header.
        // This is exactly the payload from the user's cURL.txt (2026-09-30).
        ClearCredForTest();
        SetTokenForTest(
            "curl --url 'https://www.workbuddy.cn/billing/meter/get-user-resource-summary' \\\n" +
            "  -H 'accept: application/json, text/plain, */*' \\\n" +
            "  -H 'accept-language: zh-CN,zh;q=0.9' \\\n" +
            "  -H 'content-type: application/json' \\\n" +
            "  -b '" + CK + "' \\\n" +
            "  -H 'origin: https://www.workbuddy.cn' \\\n" +
            "  -H 'user-agent: " + UA + "' \\\n" +
            "  -H 'x-client-platform: web' \\\n" +
            "  --data-raw '{}'");
        bool mbOk = _token == CK && _ua == UA && _credIsCookie;
        Console.WriteLine((mbOk ? "  PASS  " : "  FAIL  ") +
                          "multiline --url cURL with -b flag  [tok=" + _token.Length +
                          "ch ua=" + _ua.Length + "ch cookie=" + _credIsCookie + "]");
        if (mbOk) pass++; else fail++;

        // ---- regression guard for the truncation bug itself: the FIRST LINE
        // ALONE must NOT be accepted as a credential. If the input box ever
        // starts collapsing multi-line pastes again, this is what the parser
        // would be handed, and it must fail loudly rather than half-work.
        ClearCredForTest();
        SetTokenForTest("curl --url 'https://www.workbuddy.cn/billing/meter/get-user-resource-summary' \\");
        bool tOk = _token.Length == 0;
        Console.WriteLine((tOk ? "  PASS  " : "  FAIL  ") +
                          "truncated first-line-only cURL leaves credential empty");
        if (tOk) pass++; else fail++;

        // The guard set up at the top of this suite: the live credential file must
        // be untouched. Without _suppressSave the last SetToken() call above would
        // have written its fixture straight over it.
        Console.WriteLine();
        bool tokSame = (tokHad == File.Exists(tokPath)) &&
                       (!tokHad || HashFile(tokPath) == tokBefore);
        if (tokSame) pass++;
        else {
            fail++;
            Console.WriteLine("  FAIL  live token.txt was MODIFIED by the test suite" +
                              (tokHad ? "  (sha1 " + tokBefore + " -> " +
                                        (File.Exists(tokPath) ? HashFile(tokPath) : "(gone)") + ")" : ""));
        }
        if (tokSame)
            Console.WriteLine("  PASS  live token.txt untouched by the suite");

        Console.WriteLine();
        Console.WriteLine("credtest: " + pass + " passed, " + fail + " failed");
        if (fail > 0) Environment.ExitCode = 1;
    }

    // SHA1 of a whole file (BOM included). Used by the --credtest guard to prove
    // the live credential file came out of a run byte-identical.
    static string HashFile(string path) {
        using (SHA1 sha = SHA1.Create())
        using (FileStream f = File.OpenRead(path)) {
            byte[] h = sha.ComputeHash(f);
            StringBuilder sb = new StringBuilder(h.Length * 2);
            foreach (byte b in h) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }

    // End-to-end credential check (--credcheck <file>).
    //
    // --credtest pins the PARSER against synthetic fixtures. This one takes a REAL
    // pasted cURL through the app's own SetToken and then through the app's own
    // transport, so it separates three things that look identical from outside:
    //   * the cURL parses into a credential at all;
    //   * the gateway accepts it (HTTP 200 + code 0) instead of answering 401;
    //   * the network path really reaches the site.
    //
    // It parses into memory only. SaveToken() is a no-op in every test mode
    // (_suppressSave), which is what makes that guarantee hold - an earlier
    // version restored token.txt by hand here and still lost it, because
    // --credtest had already overwritten the file before this mode ever ran.
    public void RunCredCheck(string path) {
        Console.WriteLine("credential check (--credcheck)");
        if (!File.Exists(path)) {
            Console.WriteLine("  no such file: " + path);
            Environment.ExitCode = 1;
            return;
        }
        string raw = File.ReadAllText(path);
        Console.WriteLine("  file    : " + path + "  (" + raw.Length + " chars)");
        Console.WriteLine("  save    : suppressed (live credential is read-only here)");
        try {
            ClearCredForTest();
            SetTokenForTest(raw);
            Console.WriteLine("  mode    : " + (_credIsCookie ? "cookie" : "bearer"));
            Console.WriteLine("  cred len: " + _token.Length);
            Console.WriteLine("  ua      : " + (_ua.Length > 0 ? _ua.Length + " chars" : "EMPTY (a 401 risk)"));
            Console.WriteLine("  uid     : " + (_uid.Length > 0 ? _uid : "(none)"));
            if (_token.Length == 0) {
                Console.WriteLine();
                Console.WriteLine("credcheck: FAIL - nothing parsed, the widget would sit on 401");
                Environment.ExitCode = 1;
                return;
            }
            Console.WriteLine();
            Console.WriteLine("  POST " + _apiUrl);
            string body = FetchBalance();
            Console.WriteLine("  status  : OK");
            Console.WriteLine("  body    : " + (body.Length > 400 ? body.Substring(0, 400) + " ..." : body));
            Console.WriteLine();
            Console.WriteLine("credcheck: WORKS - the gateway accepted this cURL");
        } catch (Exception ex) {
            Console.WriteLine("  status  : FAILED");
            Console.WriteLine();
            Console.WriteLine("credcheck: REJECTED - " + ex.Message);
            Environment.ExitCode = 1;
        }
    }

    // Drives ONE real cue of `amount` through the production charge path so the
    // easter-egg wiring is exercised end to end, then reports what happened.
    // MakeTick calls PlayHitSound(amount), which is the exact code a live
    // balance drop reaches - nothing here is a copy of the routing logic. The
    // egg pool opens on demand on the first egg cue, which is what the caller
    // reads out of the log; that is deliberately the only on-demand log line,
    // so its presence is an unambiguous "yes, this amount went to the egg".
    public void RunEggLiveCheck(double amount) {
        Console.WriteLine("easter-egg live cue (--eggsoundlive)");
        // _soundEnabled follows state.ini's "sound=" line, so a user who muted
        // the pet would make this probe report "no pool" and look like a bug in
        // the easter egg. The probe is about routing, not about the mute, so it
        // states the flag it measured and forces it on for the cue.
        Console.WriteLine("  soundEnabledFromState=" + _soundEnabled);
        _soundEnabled = true;

        Console.WriteLine("  amount     : " + FmtAmt(amount));
        Console.WriteLine("  routesEgg  : " + IsEggStep(amount));
        string eggPath = Path.Combine(_baseDir, Get("WBPET_EGG_FILE", EGG_FILE));
        Console.WriteLine("  egg file   : " + eggPath + "  exists=" + File.Exists(eggPath));
        Console.WriteLine("  hitPool    : " + (_sound != null) +
                          "  failed=" + (_sound == null ? "n/a" : _sound.Failed.ToString()) +
                          "  plays=" + (_sound == null ? -1 : _sound.Plays));

        _lastCueAmount = 0;
        MakeTick(amount, true);            // fromTest: no real credits are spent
        // Let MCI settle far enough to answer, then report the pool state.
        System.Threading.Thread.Sleep(250);
        bool opened = _soundEgg != null;
        int hitPlaysNow = _sound == null ? -1 : _sound.Plays;
        Console.WriteLine("  eggPoolOpen= " + opened +
                          (opened ? "  failed=" + _soundEgg.Failed + "  plays=" + _soundEgg.Plays : ""));
        Console.WriteLine("  lastCue    : " + FmtAmt(_lastCueAmount));
        Console.WriteLine("  hitPlaysNow: " + hitPlaysNow);
        // Both branches must have produced a cue on SOME pool - an egg-pool
        // "opened" with no play, or a hit-pool with no new play, means the cue
        // silently went nowhere.
        bool played = opened ? _soundEgg.Plays > 0 : hitPlaysNow > 0;
        Console.WriteLine(!played ? "egg live: NOTHING PLAYED (check the audio device)"
                          : opened ? "egg live: EGG POOL USED"
                                   : "egg live: plain hit sound");
    }

    // Easter-egg routing (--eggsound). Pure logic: no device, no file, so it
    // runs anywhere and can never be the thing that breaks the audio tests
    // above.
    //
    // The boundary is the whole point of this test. The first build used a
    // 1e-6 tolerance and this suite caught it: a 0.1% fuzz (a custom step such
    // as 3.25 + 0.1%, or 3.25 rounded through a balance) also fired, so the
    // easter egg leaked onto any near-miss step. The rule was changed to an
    // exact two-decimal match, and both sides are pinned below - exact hits,
    // and the slivers that are NOT the step.
    void RunEggSoundTest() {
        Console.WriteLine("easter-egg sound routing (--eggsound)");
        int bad = 0, ok = 0;
        Console.WriteLine("  egg steps  : " + EggStepText());
        Console.WriteLine("  egg file   : " + EGG_FILE);
        Console.WriteLine("  version tag: " + (VersionTag.Length > 0 ? VersionTag : "(none)") +
                          "   about label=" + AboutVersionLabel());

        // Boundaries are chosen so the expectation follows from ordinary
        // decimal rounding to two places, not from a tolerance anybody had to
        // pick. Both directions are pinned:
        //   hit  - the three egg amounts, their binary near-representations
        //          (what a double actually holds), and values within half a
        //          unit in the second decimal that round UP to the egg;
        //   miss - the other half of each boundary, which rounds away from it.
        // The first build used a 1e-6 tolerance and this suite rejects it: a
        // 3.25006 charge played the egg even though it is not the 3.25 step.
        double[] hit = new double[] {
            3.25, 32.5, 325,
            3.2500000000000004,   // the double nearest 3.25
            3.25006, 3.2549,      // round up to 3.25, nothing finer
            32.500000000000004, 32.5049,
            325.00000000000006, 325.0049,
            3.2499999992          // the sub-step sliver, which is charged as the step
        };
        double[] miss = new double[] {
            0.1, 1, 3, 3.2, 3.3, 3.5, 5, 10, 30, 32, 33, 65, 100,
            320, 330, 350, 1000, 0,
            // the far half of each rounding boundary - these round away
            3.2551, 3.2757,
            32.5051, 32.5054,
            325.0051, 325.0055,
            4.2501
        };
        for (int i = 0; i < hit.Length; i++) {
            bool got = IsEggStep(hit[i]);
            if (got) ok++; else { bad++; Console.WriteLine("  FAIL " + Dbg(hit[i]) + " should use the egg sound"); }
        }
        for (int i = 0; i < miss.Length; i++) {
            bool got = IsEggStep(miss[i]);
            if (!got) ok++; else { bad++; Console.WriteLine("  FAIL " + Dbg(miss[i]) + " must NOT use the egg sound"); }
        }
        Console.WriteLine("  routing ok=" + ok + " bad=" + bad);

        // The egg steps must NOT be offered in the step picker - that would put
        // the easter egg on display, and the whole point is discovery. They are
        // still reachable by any path that sets the value directly (an old
        // state.ini, WBPET_STEP, --eggsoundlive), which is the intended route.
        // Since 1.2.19 the picker is the settings dialog's preset array rather
        // than a menu submenu, so the assertion reads THAT list.
        int inMenu = 0, menuSteps = 0;
        foreach (double v in SettingsDialog.Steps) {
            menuSteps++;
            if (IsEggStep(v)) inMenu++;
        }
        Console.WriteLine("  eggStepsInMenu=" + inMenu + " (expect 0: kept hidden)");
        if (inMenu != 0) bad++;
        Console.WriteLine("  menuSteps=" + menuSteps + " (expect 4: 0.1/1/5/10)");
        if (menuSteps != 4) bad++;

        // Alias uniqueness (the 2026-10-07 defect). MCI aliases are process-wide,
        // so two pools that pick the same names make the second pool's
        // "close + open ... alias" rebind the first pool's handles to its own
        // file. Symptom: once the egg fired, EVERY later cue played er.mp3 -
        // any step, real or demo. The alias tags must be disjoint.
        //
        // Cheap and device-free: compare the tag prefixes the two pools would
        // take. SoundPool allocates from a static counter, so the next two
        // tags can be predicted without constructing anything.
        string t1 = SoundPool.PeekNextTag(1);
        string t2 = SoundPool.PeekNextTag(2);
        Console.WriteLine("  aliasTags  : " + t1 + " / " + t2 +
                          "   disjoint=" + (t1 != t2));
        if (t1 == t2) { bad++; Console.WriteLine("  FAIL pool alias tags collide"); }

        Console.WriteLine(bad == 0 ? "EGG TEST: ALL PASS" : "EGG TEST: FAILURES=" + bad);
        if (bad != 0) Environment.ExitCode = 1;
    }

    // MCI slot-state rule (--soundtest).
    //
    // Pure logic, deliberately device-free: it asserts the decision table that
    // says whether an alias may take a new cue, so it stays runnable even on a
    // box where mciSendStringW "open" fails with 266 (no audio device). Use
    // --soundprobe / --soundsoak when a real device IS present - this host turned
    // out to have a working one, so "no device here" must never be assumed.
    //
    // The rule is a BLACKLIST: reject only the states that mean a cue is sounding
    // or the device is mid-transition (playing / seeking / paused). Everything
    // else - including "" and "not ready" - is usable, because a freshly opened
    // alias never reports "stopped" before its first play. The earlier whitelist
    // ("stopped" only) starved the pool and broke even a single cue.
    static void RunSoundTest() {
        Console.WriteLine("sound slot-state rule (--soundtest)");
        Console.WriteLine("  (no audio device needed: this asserts the decision table)");
        Console.WriteLine();

        int pass = 0, fail = 0;
        Action<string, bool, bool> check = delegate (string what, bool got, bool want) {
            bool ok = (got == want);
            Console.WriteLine((ok ? "  PASS  " : "  FAIL  ") + what +
                              "   got=" + got + " want=" + want);
            if (ok) pass++; else fail++;
        };

        // busy - these are the three the rule must always refuse
        check("playing                    -> busy", SoundPool.IsSlotFree("playing"), false);
        check("seeking                    -> busy", SoundPool.IsSlotFree("seeking"), false);
        check("paused                     -> busy", SoundPool.IsSlotFree("paused"), false);
        check("playing (mixed case)       -> busy", SoundPool.IsSlotFree("PLAYING"), false);
        check("seeking (with whitespace)  -> busy", SoundPool.IsSlotFree("  seeking\r\n"), false);

        // free - a cue may start here
        check("stopped                    -> free", SoundPool.IsSlotFree("stopped"), true);
        check("STOPPED (case)              -> free", SoundPool.IsSlotFree("STOPPED"), true);
        check("empty                      -> free", SoundPool.IsSlotFree(""), true);
        check("null                       -> free", SoundPool.IsSlotFree(null), true);
        check("open                       -> free", SoundPool.IsSlotFree("open"), true);
        // MCI_MODE_NOT_READY clears on its own and must not starve a burst
        check("not ready                  -> free", SoundPool.IsSlotFree("not ready"), true);
        check("garbage                    -> free", SoundPool.IsSlotFree("wat"), true);

        // Guard the exact regression this rule exists to prevent: back-to-back
        // cues must never be scheduled onto an alias that is still sounding. If
        // someone reintroduces the old substring-only test, these pass -- but if
        // someone forgets "seeking"/"paused", they now fail.
        Console.WriteLine();
        Console.WriteLine("  regression guard: mid-cue states must stay busy");
        foreach (string s in new string[] { "playing", "seeking", "paused" }) {
            check("\"" + s + "\" must be busy", SoundPool.IsSlotFree(s), false);
        }

        // Guard the opposite regression: the strict whitelist that broke single
        // playback. None of these may ever be treated as busy again.
        Console.WriteLine();
        Console.WriteLine("  regression guard: pre-first-cue states must stay free");
        foreach (string s in new string[] { "", "open", "not ready" }) {
            check("'" + s + "' must be free", SoundPool.IsSlotFree(s), true);
        }

        Console.WriteLine();
        Console.WriteLine("soundtest: " + pass + " passed, " + fail + " failed");
        if (fail > 0) Environment.ExitCode = 1;
    }

    // Real-device audio probe (--soundprobe).
    //
    // Needs an actual sound card, so it only makes sense on the user's machine.
    // Opens the same 4-slot pool the pet uses, then walks a burst at the real
    // 0.2s rhythm and prints, per cue, which slot was picked and what MCI says
    // about every alias. That shows exactly where a cue is lost: nothing free
    // (pool starved) versus open failed (device problem).
    static void RunSoundProbe(string baseDir) {
        string snd = Path.Combine(baseDir, "hit.mp3");
        Console.WriteLine("sound probe (--soundprobe)");
        Console.WriteLine("  file   : " + snd);
        Console.WriteLine("  exists : " + File.Exists(snd) +
                          (File.Exists(snd) ? "  " + new FileInfo(snd).Length + " bytes" : ""));
        if (!File.Exists(snd)) { Console.WriteLine("  abort: no sound file"); Environment.ExitCode = 1; return; }

        int vol = 80;
        SoundPool pool = new SoundPool(snd, 4, vol, LogFileIn(baseDir));
        Console.WriteLine("  open   : failed=" + pool.Failed + " err=" + pool.Error);
        Console.WriteLine("  mode after open : " + pool.DebugModes());
        if (pool.Failed) {
            Console.WriteLine();
            Console.WriteLine("  the pool could not open the cue on this machine.");
            Console.WriteLine("  that is the bug: no amount of slot bookkeeping will help.");
            Environment.ExitCode = 1;
            return;
        }

        Console.WriteLine();
        Console.WriteLine("  burst of 4 cues, 200ms apart (the real rhythm):");
        int played = 0;
        for (int cue = 1; cue <= 4; cue++) {
            int slot = pool.Play();
            if (slot >= 0) played++;
            Console.WriteLine("    cue " + cue + " -> slot " + (slot < 0 ? "NONE" : slot.ToString()) +
                              "   plays=" + pool.Plays + "   modes=" + pool.DebugModes());
            if (cue < 4) System.Threading.Thread.Sleep(200);
        }

        Console.WriteLine();
        Console.WriteLine("  burst of 8 cues, 90ms apart (stress):");
        for (int cue = 1; cue <= 8; cue++) {
            int slot = pool.Play();
            if (slot >= 0) played++;
            Console.WriteLine("    cue " + cue + " -> slot " + (slot < 0 ? "NONE" : slot.ToString()) +
                              "   modes=" + pool.DebugModes());
            if (cue < 8) System.Threading.Thread.Sleep(90);
        }

        System.Threading.Thread.Sleep(600);
        Console.WriteLine();
        Console.WriteLine("  settled modes: " + pool.DebugModes());
        pool.Dispose();
        Console.WriteLine();
        Console.WriteLine("soundprobe: played " + played + "/12 cues"
                          + (played == 12 ? "  (all good)" : "  <- some cues were dropped"));
        if (played < 12) Environment.ExitCode = 1;
    }

    // Long-rhythm soak (--soundsoak): the real-world scenario. Plays N cues at
    // exactly CueGapSec (0.2s) - the rhythm a burst of charges produces - and
    // reports whether every cue found a slot. This is the run that would have
    // caught the "consecutive charges lose their sound" defect, and it is the
    // one that proves the v1.2.7 whitelist regression is gone.
    static void RunSoundSoak(string baseDir, int cues) {
        string snd = Path.Combine(baseDir, "hit.mp3");
        Console.WriteLine("sound soak (--soundsoak " + cues + ")");
        Console.WriteLine("  file  : " + snd + "  (" + (File.Exists(snd) ? new FileInfo(snd).Length + " bytes" : "MISSING") + ")");
        Console.WriteLine("  rhythm: " + CueGapSec.ToString("0.#") + "s   cues: " + cues);
        if (!File.Exists(snd)) { Console.WriteLine("  abort: no sound file"); Environment.ExitCode = 1; return; }

        SoundPool pool = new SoundPool(snd, 4, 80, LogFileIn(baseDir));
        Console.WriteLine("  open  : failed=" + pool.Failed + " err=" + pool.Error);
        if (pool.Failed) { Console.WriteLine("  abort: pool could not open"); Environment.ExitCode = 1; return; }

        int played = 0, missed = 0, allBusy = 0;
        int gapMs = (int)Math.Round(CueGapSec * 1000);
        for (int cue = 1; cue <= cues; cue++) {
            int slot = pool.Play();
            if (slot >= 0) {
                played++;
            } else {
                missed++;
                string modes = pool.DebugModes();
                if (modes.IndexOf("playing") >= 0) allBusy++;
                if (missed <= 5) Console.WriteLine("    MISS at cue " + cue + "  modes=" + modes);
            }
            System.Threading.Thread.Sleep(gapMs);
        }
        System.Threading.Thread.Sleep(600);

        Console.WriteLine();
        Console.WriteLine("  modes after soak: " + pool.DebugModes());
        pool.Dispose();
        Console.WriteLine();
        Console.WriteLine("soundsoak: " + played + "/" + cues + " played, " + missed + " missed" +
                          (allBusy > 0 ? " (" + allBusy + " with a slot still playing)" : ""));
        if (missed > 0) Environment.ExitCode = 1;
        else Console.WriteLine("  every cue found a slot - no sound is dropped.");
    }

    // A/B soak (--soundab n): runs the SAME 0.2s burst three times, each time
    // under a different slot-state rule, and reports how many cues each rule
    // managed to place. This is the controlled experiment that pins the defect:
    //   A = old rule   IndexOf("playing") >= 0        (seeking/not ready look free)
    //   B = strict     == "stopped"                   (v1.2.7 first try: broke singles)
    //   C = current    blacklist of playing/seeking/paused
    // For A the interesting number is not just how many played but how many cues
    // landed on a slot that was still OPEN/SETTLING (a cut tail).
    static void RunSoundAb(string baseDir, int cues) {
        string snd = Path.Combine(baseDir, "hit.mp3");
        Console.WriteLine("sound A/B/C soak (--soundab " + cues + ")");
        Console.WriteLine("  file: " + snd + "  (" + (File.Exists(snd) ? new FileInfo(snd).Length + " bytes" : "MISSING") + ")");
        Console.WriteLine("  rhythm: " + CueGapSec.ToString("0.#") + "s   cues: " + cues);
        if (!File.Exists(snd)) { Console.WriteLine("  abort: no sound file"); Environment.ExitCode = 1; return; }

        string[] names = new string[] {
            "A old     IndexOf(\"playing\")",
            "B strict   == \"stopped\"",
            "C current  blacklist"
        };
        int rounds = names.Length;
        for (int r = 0; r < rounds; r++) {
            SoundPool pool = new SoundPool(snd, 4, 80, LogFileIn(baseDir));
            if (pool.Failed) { Console.WriteLine("  [" + names[r] + "] open failed: " + pool.Error); continue; }
            int played = 0, cut = 0;
            int gapMs = (int)Math.Round(CueGapSec * 1000);
            for (int cue = 1; cue <= cues; cue++) {
                int slot = pool.PlayRule(r);
                if (slot >= 0) {
                    played++;
                    if (pool.LastRuleNote.StartsWith("NOT-SETTLED")) cut++;
                }
                System.Threading.Thread.Sleep(gapMs);
            }
            System.Threading.Thread.Sleep(400);
            Console.WriteLine("  [" + names[r] + "]  played " + played + "/" + cues +
                              "  missed " + (cues - played) +
                              "  placed-on-unsettled-slot " + cut);
            pool.Dispose();
            System.Threading.Thread.Sleep(200);
        }
        Console.WriteLine();
        Console.WriteLine("  reading: A should place every cue but cut tails (the old bug);");
        Console.WriteLine("           B should starve the pool early (the v1.2.7 rev1 regression);");
        Console.WriteLine("           C should place every cue with no cut tail.");

        // Transition timing: how long does one alias actually stay "playing"?
        // If that is far below CueGapSec, this machine simply cannot reproduce
        // the real-world race and the A/B result above is uninformative.
        Console.WriteLine();
        Console.WriteLine("  transition timing (one alias):");
        SoundPool tp = new SoundPool(snd, 4, 80, LogFileIn(baseDir));
        if (!tp.Failed) {
            int s = tp.Play();
            if (s >= 0) {
                System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                string seen = "";
                long becameStopped = -1;
                while (sw.ElapsedMilliseconds < 1500) {
                    string m = tp.ModeOf(s);
                    if (m != seen) {
                        Console.WriteLine("    t=" + sw.ElapsedMilliseconds.ToString().PadLeft(4) +
                                          "ms  -> " + (m.Length == 0 ? "(empty)" : m));
                        seen = m;
                        if (m.IndexOf("stopped", StringComparison.OrdinalIgnoreCase) >= 0) {
                            becameStopped = sw.ElapsedMilliseconds; break;
                        }
                    }
                    System.Threading.Thread.Sleep(5);
                }
                if (becameStopped >= 0)
                    Console.WriteLine("    alias settled after " + becameStopped + "ms" +
                                      "   (cue is " + CueGapSec.ToString("0.#") + "s = " +
                                      (CueGapSec * 1000).ToString("0") + "ms of rhythm)");
            }
            tp.Dispose();
        }
    }

    // Where the rendering test modes put their PNGs. Public so Run() can hand
    // it to the screenshot paths without duplicating the rule.
    public string ShotsDir { get { return _shotsDir; } }

    public void SaveCanvas(string path) {
        try {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        } catch { }
        using (Bitmap copy = new Bitmap(_canvas.Width, _canvas.Height, PixelFormat.Format32bppArgb)) {
            using (Graphics g = Graphics.FromImage(copy)) g.DrawImageUnscaled(_canvas, 0, 0);
            using (Bitmap flat = new Bitmap(copy.Width, copy.Height)) {
                using (Graphics g = Graphics.FromImage(flat)) {
                    using (LinearGradientBrush lg = new LinearGradientBrush(
                            new Rectangle(0, 0, flat.Width, flat.Height),
                            Color.FromArgb(255, 28, 32, 46), Color.FromArgb(255, 62, 42, 58),
                            LinearGradientMode.ForwardDiagonal))
                        g.FillRectangle(lg, 0, 0, flat.Width, flat.Height);
                    g.DrawImageUnscaled(copy, 0, 0);
                }
                flat.Save(path, ImageFormat.Png);
            }
        }
    }
}

// ------------------------------------------------------------- exe entry ---
//
// Entry point for the standalone single-file build (see build_exe.ps1). The
// PowerShell launcher never calls this; it invokes WbPet.Run directly after
// Add-Type. Here we own the whole boot: pick a per-user data folder, unpack
// the embedded artwork/sound/icon into it on first run, load token.txt, then
// hand over to the same WbPet.Run the launcher uses. Target: any Windows
// 10/11 machine, no PowerShell window, no admin rights, nothing to install
// beyond copying the exe.
public static class ExeEntry {
    // Embedded resources, listed by their on-disk relative path with '/' as
    // separator. The compiler names each embedded resource by its BARE FILE
    // NAME, which is why every file under characters\ has a unique name.
    // Extracted to the data folder only when missing, so user-replaced files
    // survive.
    static readonly string[] ResFiles = new string[] {
        "sprite.png",
        "characters/sprite-dsh.png",
        "characters/sprite-claude.png",
        "characters/sprite-claude_detailed.png",
        "characters/sprite-gemini.png",
        "characters/sprite-gemini_detailed.png",
        "characters/sprite-gpt.png",
        "characters/sprite-gpt_detailed.png",
        "hit.mp3",
        "er.mp3",
        "DaFeiYu.ico",
    };

    // ------------------------------------------------------ dual launcher ---
    //
    // "WorkBuddy & PointPal" (one click starts the pet AND WorkBuddy) used to be
    // a .vbs that the installer dropped beside the pet. Shipping a script turned
    // out to be a liability in itself: 360 quarantined both of ours
    // (virus.vbs.powershell.a and virus.vbs.dropper.d) and told users the app was
    // a dropper. The shortcut now targets THIS exe with --launch-wb, so the
    // locating logic lives in the binary users already run, and no .vbs ships.
    //
    // Side benefit: the taskbar only pins .exe targets. The old shortcut had to
    // aim at wscript.exe as a workaround; this one is pinnable directly.
    public const string LaunchWbSwitch = "--launch-wb";

    public static bool WantsWorkBuddy(string[] args) {
        foreach (string a in args)
            if (string.Equals(a, LaunchWbSwitch, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // Runs on a worker thread with a short delay so the pet paints first:
    // WorkBuddy arriving a second later is what a "start both" press implies.
    public static void LaunchWorkBuddy() {
        ThreadPool.QueueUserWorkItem(delegate {
            try { Thread.Sleep(1200); } catch { }
            try { StartWorkBuddy(); } catch { }
        });
    }

    static void StartWorkBuddy() {
        string wb = FindWorkBuddy();
        if (wb != null) {
            try {
                ProcessStartInfo psi = new ProcessStartInfo(wb);
                psi.UseShellExecute = true;
                psi.WorkingDirectory = Path.GetDirectoryName(wb);
                Process.Start(psi);
                return;
            } catch { }
        }
        // Same remedies the retired launcher listed, minus the file it can no
        // longer point at: a Start menu shortcut is the clue we look for.
        MessageBox.Show(
            "\u6CA1\u80FD\u81EA\u52A8\u627E\u5230 WorkBuddy\uFF0C\u8BF7\u624B\u52A8\u542F\u52A8\u5B83\u3002" + "\r\n\r\n" +
            "\u60F3\u8BA9\u5B83\u4EE5\u540E\u81EA\u52A8\u542F\u52A8\uFF0C\u4EFB\u9009\u4E00\u79CD\uFF1A" + "\r\n" +
            "  1. \u5F00\u59CB\u83DC\u5355\u91CC\u6709 WorkBuddy \u7684\u5FEB\u6377\u65B9\u5F0F\u5373\u53EF\uFF08\u672C\u7A0B\u5E8F\u4F1A\u53BB\u90A3\u91CC\u627E\uFF09\uFF1B" + "\r\n" +
            "  2. \u628A WorkBuddy.exe \u7684\u5B8C\u6574\u8DEF\u5F84\u5199\u8FDB\u6570\u636E\u76EE\u5F55\u4E0B\u7684 wb-path.txt\uFF1B" + "\r\n" +
            "  3. \u628A\u5B8C\u6574\u8DEF\u5F84\u53CD\u9988\u7ED9\u4F5C\u8005\uFF0C\u4E0B\u4E00\u7248\u52A0\u8FDB\u68C0\u7D22\u8303\u56F4\u3002",
            "WorkBuddy & PointPal", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // ---- locating WorkBuddy ------------------------------------------------
    //
    // WorkBuddy ships no uninstall entry, no App Paths key and no path-valued
    // registry key (measured on the dev machine), so a Start menu shortcut is
    // the only dependable clue. wb-path.txt and a drive sweep stay as fallbacks,
    // in the same order the retired script used.
    static string FindWorkBuddy() {
        string wb = WbFromOverride();
        if (wb == null) wb = WbFromStartMenu();
        if (wb == null) wb = WbFromDrives();
        if (wb == null) {
            string la = Environment.GetEnvironmentVariable("LOCALAPPDATA");
            string pf = Environment.GetEnvironmentVariable("ProgramFiles");
            string p6 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
            la = la == null ? "" : la; pf = pf == null ? "" : pf; p6 = p6 == null ? "" : p6;
            wb = FirstExisting(new string[] {
                Path.Combine(la, @"Programs\WorkBuddy\WorkBuddy.exe"),
                Path.Combine(pf, @"WorkBuddy\WorkBuddy.exe"),
                Path.Combine(p6, @"WorkBuddy\WorkBuddy.exe"),
                Path.Combine(la, @"WorkBuddy\WorkBuddy.exe")
            });
        }
        return wb;
    }

    // wb-path.txt holds full paths, one per line; the first one that exists
    // wins. It is looked for in userdata\ (writable without admin), then in the
    // old data-folder root, and finally next to this exe, which is where the
    // retired .vbs expected it.
    static string WbFromOverride() {
        string dataDir = ExeDataDir(), exeDir = ThisExeDir();
        string[] spots = new string[] {
            dataDir.Length == 0 ? "" : Path.Combine(dataDir, WbPet.UserSubdir, "wb-path.txt"),
            dataDir.Length == 0 ? "" : Path.Combine(dataDir, "wb-path.txt"),
            exeDir.Length == 0 ? "" : Path.Combine(exeDir, "wb-path.txt")
        };
        foreach (string p in spots) {
            try {
                if (string.IsNullOrEmpty(p) || !File.Exists(p)) continue;
                foreach (string raw in File.ReadAllLines(p)) {
                    string line = raw.Trim();
                    if (line.Length > 0 && File.Exists(line)) return line;
                }
            } catch { }
        }
        return null;
    }

    static string ExeDataDir() {
        try {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WorkBuddy PointPal");
        } catch { return ""; }
    }

    static string ThisExeDir() {
        try { return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location); }
        catch { return ""; }
    }

    static string WbFromStartMenu() {
        string appData = Environment.GetEnvironmentVariable("APPDATA");
        string progData = Environment.GetEnvironmentVariable("ProgramData");
        appData = appData == null ? "" : appData;
        progData = progData == null ? "" : progData;
        string[] roots = new string[] {
            Path.Combine(appData, @"Microsoft\Windows\Start Menu\Programs"),
            Path.Combine(progData, @"Microsoft\Windows\Start Menu\Programs")
        };
        foreach (string r in roots) {
            string found = ScanMenuFor(r, 0);
            if (found != null) return found;
        }
        return null;
    }

    // Recursive, depth-capped so a pathological tree cannot spin forever.
    static string ScanMenuFor(string dir, int depth) {
        if (depth > 6 || string.IsNullOrEmpty(dir)) return null;
        try {
            if (!Directory.Exists(dir)) return null;
            foreach (string f in Directory.GetFiles(dir, "*.lnk")) {
                string name = Path.GetFileName(f).ToLowerInvariant();
                // Must mention WorkBuddy but NOT PointPal: our own shortcut also
                // lives in the Start menu and would otherwise match itself.
                if (name.IndexOf("workbuddy") >= 0 && name.IndexOf("pointpal") < 0) {
                    string t = TargetOfLnk(f);
                    if (t != null) return t;
                }
            }
            foreach (string sub in Directory.GetDirectories(dir)) {
                string t = ScanMenuFor(sub, depth + 1);
                if (t != null) return t;
            }
        } catch { }
        return null;
    }

    // Reads a .lnk through the Windows Script Host COM object, late-bound so no
    // interop assembly is needed. Best-effort throughout: a missing COM
    // registration or a broken shortcut just falls through to the next probe.
    static string TargetOfLnk(string lnkPath) {
        object shell = null;
        try {
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) return null;
            shell = Activator.CreateInstance(t);
            object lnk = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod,
                                        null, shell, new object[] { lnkPath });
            string target = null, work = null;
            try {
                target = t.InvokeMember("TargetPath", BindingFlags.GetProperty, null, lnk, null) as string;
            } catch { }
            if (!string.IsNullOrEmpty(target)) {
                if (target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(target))
                    return target;
                try {
                    string inDir = Path.Combine(target, "WorkBuddy.exe");
                    if (Directory.Exists(target) && File.Exists(inDir)) return inDir;
                } catch { }
            }
            try {
                work = t.InvokeMember("WorkingDirectory", BindingFlags.GetProperty, null, lnk, null) as string;
            } catch { }
            if (!string.IsNullOrEmpty(work)) {
                string p = Path.Combine(work, "WorkBuddy.exe");
                if (File.Exists(p)) return p;
            }
        } catch {
        } finally {
            try { if (shell != null) Marshal.ReleaseComObject(shell); } catch { }
        }
        return null;
    }

    // Last resort: the handful of places WorkBuddy is normally installed to, on
    // every ready fixed or removable drive.
    static string WbFromDrives() {
        string[] tails = new string[] {
            @"Program Files\WorkBuddy\WorkBuddy.exe",
            @"Program Files (x86)\WorkBuddy\WorkBuddy.exe",
            @"WorkBuddy\WorkBuddy.exe",
            @"Programs\WorkBuddy\WorkBuddy.exe",
            @"Apps\WorkBuddy\WorkBuddy.exe"
        };
        try {
            foreach (DriveInfo d in DriveInfo.GetDrives()) {
                try {
                    if (!d.IsReady) continue;
                    if (d.DriveType != DriveType.Fixed && d.DriveType != DriveType.Removable) continue;
                    foreach (string tail in tails) {
                        string p = Path.Combine(d.RootDirectory.FullName, tail);
                        if (File.Exists(p)) return p;
                    }
                } catch { }
            }
        } catch { }
        return null;
    }

    static string FirstExisting(string[] list) {
        foreach (string p in list) {
            if (string.IsNullOrEmpty(p)) continue;
            try { if (File.Exists(p)) return p; } catch { }
        }
        return null;
    }

    [STAThread]
    public static void Main(string[] args) {
        // ---- single instance guard -------------------------------------
        // Test modes run as throwaway processes and must never be blocked by
        // (or steal the slot from) the user's real pet.
        bool isTestMode = WbPet.IsTestMode(args);
        bool wantWb = WantsWorkBuddy(args);

        // --wbtest: report which WorkBuddy the dual launcher would start, then
        // exit WITHOUT starting anything. Launching it for real is exactly the
        // side effect a test must not have, so the search logic gets a mode of
        // its own; stdout is captured via _tools/_run_capture.ps1 (winexe).
        if (Array.IndexOf(args, "--wbtest") >= 0) {
            Console.WriteLine("wbtest: override     -> " + (WbFromOverride() ?? "(none)"));
            Console.WriteLine("wbtest: start menu   -> " + (WbFromStartMenu() ?? "(none)"));
            Console.WriteLine("wbtest: drives       -> " + (WbFromDrives() ?? "(none)"));
            Console.WriteLine("wbtest: resolved     -> " + (FindWorkBuddy() ?? "(not found)"));
            return;
        }

        SingleInstance guard = null;
        if (!isTestMode) {
            guard = new SingleInstance();
            if (!guard.Acquire()) {
                // Another pet is already up. Nudge it to the front and leave
                // quietly - no message box, no second window.
                SingleInstance.WakeRunningPet();
                guard.Dispose();
                // A second press of the "start both" shortcut while the pet is
                // already running must still bring WorkBuddy up: the pet half is
                // already satisfied, the WorkBuddy half is not.
                if (wantWb) LaunchWorkBuddy();
                return;
            }
        }

        string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string baseDir = Path.Combine(localApp, "WorkBuddy PointPal");
        try {
            MigrateOldDataDir(localApp, baseDir);
            Directory.CreateDirectory(baseDir);
            MigrateUserFiles(baseDir);
            ExtractResources(baseDir);
            BootCredentials(baseDir);
            if (wantWb) LaunchWorkBuddy();
            WbPet.Run(baseDir, args);
        } catch (Exception ex) {
            try {
                EnsureUserDirs(baseDir);
                File.WriteAllText(Path.Combine(baseDir, WbPet.UserSubdir, "error.log"), ex.ToString(),
                                  new UTF8Encoding(false));
            } catch { }
            MessageBox.Show(ex.Message, "WorkBuddy PointPal",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
        } finally {
            // Release once the message loop returns, so a later launch works.
            if (guard != null) guard.Dispose();
        }
    }


    // The app used to have a Chinese name and kept its data in a folder of
    // that name. On the first run after the rename, carry the user's
    // credentials and settings over so nobody has to paste their token again.
    // Copy (never move): the old folder stays untouched as a fallback.
    static void MigrateOldDataDir(string localApp, string baseDir) {
        try {
            if (Directory.Exists(baseDir)) return;
            string oldName = "WorkBuddy" + (char)0x79EF + (char)0x5206 + (char)0x684C + (char)0x5BA0;
            string oldDir = Path.Combine(localApp, oldName);
            if (!Directory.Exists(oldDir)) return;
            Directory.CreateDirectory(baseDir);
            foreach (string f in new string[] { "token.txt", "state.ini" }) {
                string src = Path.Combine(oldDir, f);
                string dst = Path.Combine(baseDir, f);
                if (File.Exists(src) && !File.Exists(dst)) File.Copy(src, dst);
            }
        } catch { }
    }

    // Create the two subfolders the pet writes into. Cheap and idempotent;
    // callers that WRITE use it rather than trusting the migration to have run.
    static void EnsureUserDirs(string baseDir) {
        try {
            foreach (string sub in new string[] { WbPet.UserSubdir, WbPet.ShotsSubdir }) {
                string d = Path.Combine(baseDir, sub);
                if (!Directory.Exists(d)) Directory.CreateDirectory(d);
            }
        } catch { }
    }

    // Files that used to sit in the data folder root now live in userdata\.
    // A MOVE (not a copy) so nothing is left behind in the root, and the move
    // only happens when the new location is still empty - a user who has
    // already run the new build keeps the newer file rather than having a stale
    // root copy clobber it.
    static void MigrateUserFiles(string baseDir) {
        try {
            string userDir = Path.Combine(baseDir, WbPet.UserSubdir);
            bool any = false;
            foreach (string f in WbPet.UserFiles) {
                if (File.Exists(Path.Combine(baseDir, f))) { any = true; break; }
            }
            if (!any) return;
            if (!Directory.Exists(userDir)) Directory.CreateDirectory(userDir);
            foreach (string f in WbPet.UserFiles) {
                string src = Path.Combine(baseDir, f);
                string dst = Path.Combine(userDir, f);
                try {
                    if (!File.Exists(src)) continue;
                    if (File.Exists(dst)) {
                        // Both exist: the new-location copy is the live one.
                        // Drop the stale root copy so the folder stays clean.
                        try { File.Delete(src); } catch { }
                        continue;
                    }
                    File.Move(src, dst);
                } catch { }
            }
        } catch { }
    }

    // The compiler records embedded resources by bare file name regardless
    // of any directory in the path, so match on that alone.
    static Stream OpenRes(Assembly asm, string rel) {
        string want = rel;
        int s = want.LastIndexOf('/'); if (s >= 0) want = want.Substring(s + 1);
        s = want.LastIndexOf('\\'); if (s >= 0) want = want.Substring(s + 1);
        foreach (string n in asm.GetManifestResourceNames()) {
            string nn = n;
            int t = nn.LastIndexOf('/'); if (t >= 0) nn = nn.Substring(t + 1);
            t = nn.LastIndexOf('\\'); if (t >= 0) nn = nn.Substring(t + 1);
            if (nn == want) return asm.GetManifestResourceStream(n);
        }
        return null;
    }

    // Unpack the embedded artwork/sound/icon into the data folder.
    //
    // First run: everything is written.
    // Later runs: a resource is rewritten ONLY when the copy embedded in THIS
    // exe differs from the copy we unpacked last time (tracked by hash in the
    // manifest below). That gives two things at once:
    //   * shipping a new hit.mp3 (or any other asset) in a new exe makes it
    //     take effect on the next launch - without a stale cache shadowing it;
    //   * a file the user replaced by hand is left alone, because the embedded
    //     bytes did not change, so there is nothing to refresh.
    // Anything that cannot be read/hashed is skipped quietly; a broken asset
    // must never stop the pet from starting.
    static void ExtractResources(string baseDir) {
        Assembly asm = Assembly.GetExecutingAssembly();
        string manifestPath = Path.Combine(baseDir, ".res-manifest");
        Dictionary<string, string> have = ReadManifest(manifestPath);
        Dictionary<string, string> now = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool changed = false;

        foreach (string rel in ResFiles) {
            string key = rel.Replace('/', '\\');
            string dst = Path.Combine(baseDir, rel.Replace('/', Path.DirectorySeparatorChar));
            string embHash;
            using (Stream s = OpenRes(asm, rel)) {
                if (s == null) throw new FileNotFoundException("embedded resource missing: " + rel);
                embHash = HashStream(s);
            }
            now[key] = embHash;

            string hadHash;
            bool known = have.TryGetValue(key, out hadHash);
            bool embChanged = !known || !string.Equals(hadHash, embHash, StringComparison.OrdinalIgnoreCase);

            if (!embChanged && File.Exists(dst)) continue;   // nothing new to push
            changed = true;

            string dn = Path.GetDirectoryName(dst);
            if (dn != null && dn.Length > 0) Directory.CreateDirectory(dn);
            // Write beside the target then move over it, so a half-written
            // file can never be left behind if we are killed mid-copy.
            string tmp = dst + ".new";
            using (Stream s = OpenRes(asm, rel))
            using (FileStream f = File.Create(tmp)) { s.CopyTo(f); }
            try {
                if (File.Exists(dst)) File.Delete(dst);
                File.Move(tmp, dst);
            } catch {
                // Target locked (e.g. already loaded): keep what is there
                // rather than failing the whole launch.
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        if (changed) WriteManifest(manifestPath, now);
    }

    static string HashStream(Stream s) {
        using (SHA1 sha = SHA1.Create()) {
            byte[] h = sha.ComputeHash(s);
            StringBuilder sb = new StringBuilder(h.Length * 2);
            foreach (byte b in h) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }

    // Format: one "relative\path=sha1" line each. Missing/corrupt file -> empty
    // map, which makes every embedded resource look new (i.e. a full unpack,
    // the safe fallback).
    static Dictionary<string, string> ReadManifest(string path) {
        Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try {
            if (!File.Exists(path)) return map;
            foreach (string line in File.ReadAllLines(path, Encoding.UTF8)) {
                int i = line.IndexOf('=');
                if (i <= 0) continue;
                map[line.Substring(0, i)] = line.Substring(i + 1).Trim();
            }
        } catch { }
        return map;
    }

    static void WriteManifest(string path, Dictionary<string, string> map) {
        try {
            StringBuilder sb = new StringBuilder();
            foreach (KeyValuePair<string, string> kv in map) {
                sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\n');
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        } catch { }
    }

    // Same contract as the PowerShell launcher: token.txt line1 = credential,
    // line2 = uid (optional), line3 = User-Agent. Real environment variables
    // still win; nothing is overwritten.
    //
    // An older build WROTE these as "mode=" / "token=" / "ua=" prefixed lines
    // but READ them back positionally, so it could not read its own output and
    // every wallet set through the menu died with 401. That build is gone, but
    // files cut with it (or by hand) are still around, so strip a leading
    // "key=" label defensively rather than trusting the format. Do NOT just
    // split on '=' - the cookie value itself contains '='.
    static string StripCredLabel(string line) {
        foreach (string k in new string[] { "mode", "token", "uid", "ua" }) {
            if (line.StartsWith(k + "=", StringComparison.OrdinalIgnoreCase))
                return line.Substring(k.Length + 1).Trim();
        }
        return line;
    }

    // --cryptest: proves the 1.2.18 credential store round-trips and that a
    // 1.2.17-style plain-text file still loads. A temp file is used throughout
    // - the live token.txt is never touched.
    public static void RunCryptTest() {
        Console.WriteLine("credential store round-trip (--cryptest)");
        int nOk = 0, nBad = 0;
        string tmp = Path.Combine(Path.GetTempPath(),
                                  "wbpet_crypt_" + Guid.NewGuid().ToString("N") + ".txt");
        string body = "COOKIEVALUE=abc; session=xyz\r\nuid-1234\r\n" + new string('U', 125);
        try {
            byte[] sealed_ = ProtectedData.Protect(new UTF8Encoding(false).GetBytes(body),
                                                   null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(tmp, sealed_);

            bool noPlain = !BytesContain(File.ReadAllBytes(tmp), "COOKIEVALUE");
            Console.WriteLine((noPlain ? "  PASS  " : "  FAIL  ") + "sealed file contains no plaintext");
            if (noPlain) nOk++; else nBad++;

            string[] lines = ReadCredentialLines(tmp);
            bool rt = lines != null && lines.Length >= 2 &&
                      lines[0] == "COOKIEVALUE=abc; session=xyz" && lines[1] == "uid-1234";
            Console.WriteLine((rt ? "  PASS  " : "  FAIL  ") + "sealed file reads back identically");
            if (rt) nOk++; else nBad++;

            // Exactly what 1.2.17 wrote: plain text, three lines, UTF-8 with BOM.
            File.WriteAllText(tmp, body, new UTF8Encoding(true));
            string[] legacy = ReadCredentialLines(tmp);
            bool lOk = legacy != null && legacy.Length >= 3 &&
                       legacy[0] == "COOKIEVALUE=abc; session=xyz" && legacy[1] == "uid-1234";
            Console.WriteLine((lOk ? "  PASS  " : "  FAIL  ") + "legacy plain-text file still loads");
            if (lOk) nOk++; else nBad++;
        } catch (Exception ex) {
            Console.WriteLine("  FAIL  exception: " + ex.Message);
            nBad++;
        } finally {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
        Console.WriteLine("cryptest: " + nOk + " passed, " + nBad + " failed");
    }

    // Plain substring test over raw bytes - used above to prove a sealed file
    // does not carry the credential in the clear.
    static bool BytesContain(byte[] hay, string needle) {
        byte[] n = Encoding.ASCII.GetBytes(needle);
        if (n.Length == 0 || hay.Length < n.Length) return false;
        for (int i = 0; i + n.Length <= hay.Length; i++) {
            bool all = true;
            for (int j = 0; j < n.Length; j++) {
                if (hay[i + j] != n[j]) { all = false; break; }
            }
            if (all) return true;
        }
        return false;
    }

    // token.txt is DPAPI-sealed as of 1.2.18. Files written by 1.2.17 and
    // earlier are plain text (with a BOM), so try the seal first and fall back
    // to the legacy shape - an upgrade must never log the user out.
    static string[] ReadCredentialLines(string tf) {
        try {
            byte[] rawBytes = File.ReadAllBytes(tf);
            if (rawBytes.Length == 0) return null;
            try {
                byte[] plain = ProtectedData.Unprotect(rawBytes, null, DataProtectionScope.CurrentUser);
                return new UTF8Encoding(false).GetString(plain).Replace("\r\n", "\n").Split('\n');
            } catch {
                // Not a DPAPI blob - a legacy plain-text file. Fall through.
            }
            // Encoding.UTF8 is BOM-aware, matching the old loader exactly.
            return File.ReadAllLines(tf, Encoding.UTF8);
        } catch { return null; }
    }

    static void BootCredentials(string baseDir) {
        try {
            if (Environment.GetEnvironmentVariable("WBPET_TOKEN") != null) return;
            // New location first, then the pre-userdata\ one, so an install that
            // has not migrated yet still logs in.
            string userDir = Path.Combine(baseDir, WbPet.UserSubdir);
            string tf = Path.Combine(userDir, "token.txt");
            if (!File.Exists(tf)) tf = Path.Combine(baseDir, "token.txt");
            if (!File.Exists(tf)) return;
            string[] lines = ReadCredentialLines(tf);
            if (lines == null) return;
            for (int i = 0; i < lines.Length; i++) lines[i] = StripCredLabel(lines[i].Trim());
            if (lines.Length >= 1 && lines[0].Length > 0)
                Environment.SetEnvironmentVariable("WBPET_TOKEN", lines[0]);
            if (lines.Length >= 2 && lines[1].Length > 0 &&
                Environment.GetEnvironmentVariable("WBPET_UID") == null)
                Environment.SetEnvironmentVariable("WBPET_UID", lines[1]);
            if (lines.Length >= 3 && lines[2].Length > 0 &&
                Environment.GetEnvironmentVariable("WBPET_UA") == null)
                Environment.SetEnvironmentVariable("WBPET_UA", lines[2]);
        } catch { }
    }
}

