// FocusKit - background presentation helper: Spotlight, Zoom and Draw on screen with global hotkeys.
// Single-file C# 5 / .NET Framework 4.x WinForms app. Build with build.cmd.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace FocusKit
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool created;
            using (var mutex = new Mutex(true, "FocusKit_SingleInstance_Mutex", out created))
            {
                if (!created)
                {
                    MessageBox.Show("FocusKit is already running - look for its icon in the system tray.", "FocusKit");
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
                {
                    Log.Write("ERROR " + e.Exception);
                    if (TrayApp.Instance != null) TrayApp.Instance.Notify("FocusKit error", e.Exception.Message + "\nDetails: " + Log.FilePath);
                };
                bool selfTest = Array.IndexOf(Environment.GetCommandLineArgs(), "--selftest") >= 0;
                Application.Run(new TrayApp(selfTest));
            }
        }
    }

    // ------------------------------------------------------------------ native
    static class Native
    {
        [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);

        public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int idHook, HookProc fn, IntPtr hMod, uint threadId);
        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hHook);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hHook, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")] public static extern IntPtr GetModuleHandle(string name);

        [StructLayout(LayoutKind.Sequential)]
        public struct MSLLHOOKSTRUCT { public int x; public int y; public uint mouseData; public uint flags; public uint time; public IntPtr extra; }
        [StructLayout(LayoutKind.Sequential)]
        public struct KBDLLHOOKSTRUCT { public uint vkCode; public uint scanCode; public uint flags; public uint time; public IntPtr extra; }

        public static bool IsDown(int vk) { return (GetAsyncKeyState(vk) & 0x8000) != 0; }

        // --- GDI / layered window
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
        [DllImport("gdi32.dll")] public static extern bool StretchBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int sw, int sh, int rop);
        [DllImport("gdi32.dll")] public static extern int SetStretchBltMode(IntPtr hdc, int mode);
        [DllImport("gdi32.dll")] public static extern bool GdiFlush();
        [DllImport("gdi32.dll")] public static extern bool SetBrushOrgEx(IntPtr hdc, int x, int y, IntPtr prev);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool UpdateLayeredWindowIndirect(IntPtr hwnd, ref ULWINFO info);

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
        {
            public int biSize; public int biWidth; public int biHeight; public short biPlanes; public short biBitCount;
            public int biCompression; public int biSizeImage; public int biXPelsPerMeter; public int biYPelsPerMeter;
            public int biClrUsed; public int biClrImportant;
        }
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int left, top, right, bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
        [StructLayout(LayoutKind.Sequential)]
        public unsafe struct ULWINFO
        {
            public int cbSize; public IntPtr hdcDst; public POINT* pptDst; public SIZE* psize; public IntPtr hdcSrc;
            public POINT* pptSrc; public int crKey; public BLENDFUNCTION* pblend; public int dwFlags; public RECT* prcDirty;
        }

        public static Bitmap CaptureScreen(Rectangle r)
        {
            // BitBlt with CAPTUREBLT so layered/transparent windows are included
            var raw = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppRgb);
            using (var g = Graphics.FromImage(raw))
            {
                IntPtr dst = g.GetHdc();
                IntPtr src = GetDC(IntPtr.Zero);
                try { BitBlt(dst, 0, 0, r.Width, r.Height, src, r.X, r.Y, 0x00CC0020 | 0x40000000); }
                finally { ReleaseDC(IntPtr.Zero, src); g.ReleaseHdc(dst); }
            }
            var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp)) g.DrawImageUnscaled(raw, 0, 0);
            raw.Dispose();
            return bmp;
        }
    }

    static class Log
    {
        public static string FilePath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FocusKit", "log.txt"); }
        }
        public static void Write(string msg)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine);
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------ hotkey + settings
    public class Hotkey
    {
        public const int ALT = 1, CTRL = 2, SHIFT = 4, WIN = 8;
        public int Mods;
        public Keys Key;
        public Hotkey(int mods, Keys key) { Mods = mods; Key = key; }
        public bool IsEmpty { get { return Key == Keys.None; } }
        public bool SameAs(Hotkey o) { return !IsEmpty && o != null && Mods == o.Mods && Key == o.Key; }

        public override string ToString()
        {
            if (IsEmpty) return "(none)";
            var parts = new List<string>();
            if ((Mods & CTRL) != 0) parts.Add("Ctrl");
            if ((Mods & ALT) != 0) parts.Add("Alt");
            if ((Mods & SHIFT) != 0) parts.Add("Shift");
            if ((Mods & WIN) != 0) parts.Add("Win");
            parts.Add(KeyName(Key));
            return string.Join("+", parts.ToArray());
        }

        public static string KeyName(Keys k)
        {
            if (k >= Keys.D0 && k <= Keys.D9) return ((int)(k - Keys.D0)).ToString();
            if (k >= Keys.NumPad0 && k <= Keys.NumPad9) return "Num" + (int)(k - Keys.NumPad0);
            switch (k)
            {
                case Keys.Oemplus: return "=";
                case Keys.OemMinus: return "-";
                case Keys.Oemcomma: return ",";
                case Keys.OemPeriod: return ".";
                case Keys.OemQuestion: return "/";
                case Keys.Oemtilde: return "`";
                case Keys.OemSemicolon: return ";";
                case Keys.OemQuotes: return "'";
                case Keys.OemOpenBrackets: return "[";
                case Keys.OemCloseBrackets: return "]";
                case Keys.OemPipe: return "\\";
                case Keys.Next: return "PageDown";
                case Keys.Prior: return "PageUp";
            }
            return k.ToString();
        }

        public string Serialize() { return Mods + "," + (int)Key; }
        public static Hotkey Parse(string s, Hotkey fallback)
        {
            try { var p = s.Split(','); return new Hotkey(int.Parse(p[0]), (Keys)int.Parse(p[1])); }
            catch { return fallback; }
        }
    }

    public class Settings
    {
        public Hotkey Spotlight = new Hotkey(Hotkey.CTRL | Hotkey.SHIFT, Keys.D1);
        public Hotkey Zoom = new Hotkey(Hotkey.CTRL | Hotkey.SHIFT, Keys.D2);
        public Hotkey Draw = new Hotkey(Hotkey.CTRL | Hotkey.SHIFT, Keys.D3);
        public int SpotRadius = 160;
        public int SpotDim = 65;          // percent darkness outside the spotlight
        public float ZoomLevel = 2f;
        public int PenColor = Color.FromArgb(255, 59, 48).ToArgb();
        public int PenWidth = 5;
        public bool FirstRun = true;

        static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FocusKit"); } }
        static string FilePath { get { return Path.Combine(Dir, "settings.ini"); } }

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                s.FirstRun = false;
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    int i = line.IndexOf('=');
                    if (i < 0) continue;
                    string k = line.Substring(0, i).Trim(), v = line.Substring(i + 1).Trim();
                    int n; float f;
                    switch (k)
                    {
                        case "Spotlight": s.Spotlight = Hotkey.Parse(v, s.Spotlight); break;
                        case "Zoom": s.Zoom = Hotkey.Parse(v, s.Zoom); break;
                        case "Draw": s.Draw = Hotkey.Parse(v, s.Draw); break;
                        case "SpotRadius": if (int.TryParse(v, out n)) s.SpotRadius = n; break;
                        case "SpotDim": if (int.TryParse(v, out n)) s.SpotDim = n; break;
                        case "ZoomLevel": if (float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out f)) s.ZoomLevel = f; break;
                        case "PenColor": if (int.TryParse(v, out n)) s.PenColor = n; break;
                        case "PenWidth": if (int.TryParse(v, out n)) s.PenWidth = n; break;
                    }
                }
            }
            catch { }
            s.SpotRadius = Math.Max(20, Math.Min(2000, s.SpotRadius));
            s.SpotDim = Math.Max(10, Math.Min(95, s.SpotDim));
            s.ZoomLevel = Math.Max(1.25f, Math.Min(16f, s.ZoomLevel));
            s.PenWidth = Math.Max(1, Math.Min(60, s.PenWidth));
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllLines(FilePath, new[] {
                    "Spotlight=" + Spotlight.Serialize(),
                    "Zoom=" + Zoom.Serialize(),
                    "Draw=" + Draw.Serialize(),
                    "SpotRadius=" + SpotRadius,
                    "SpotDim=" + SpotDim,
                    "ZoomLevel=" + ZoomLevel.ToString(CultureInfo.InvariantCulture),
                    "PenColor=" + PenColor,
                    "PenWidth=" + PenWidth,
                });
            }
            catch { }
        }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public static bool RunAtStartup
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue("FocusKit") != null;
            }
            set
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (value) k.SetValue("FocusKit", "\"" + Application.ExecutablePath + "\"");
                    else if (k.GetValue("FocusKit") != null) k.DeleteValue("FocusKit");
                }
            }
        }
    }

    // ------------------------------------------------------------------ tray app
    class HotkeyWindow : NativeWindow
    {
        public event Action<int> Pressed;
        public HotkeyWindow() { CreateHandle(new CreateParams()); }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0312 && Pressed != null) Pressed(m.WParam.ToInt32());
            base.WndProc(ref m);
        }
    }

    class TrayApp : ApplicationContext
    {
        public const int SPOT = 1, ZOOM = 2, DRAW = 3;
        readonly Settings settings;
        readonly NotifyIcon tray;
        readonly HotkeyWindow hk;
        readonly ToolStripMenuItem miSpot, miZoom, miDraw;
        Form active;
        int activeKind;
        SettingsForm settingsForm;
        public static TrayApp Instance;

        public void Notify(string title, string text)
        {
            tray.ShowBalloonTip(8000, title, text, ToolTipIcon.Warning);
        }

        public TrayApp(bool selfTest)
        {
            Instance = this;
            settings = Settings.Load();
            hk = new HotkeyWindow();
            hk.Pressed += delegate(int id) { Log.Write("Hotkey " + id + " pressed"); Toggle(id); };

            var menu = new ContextMenuStrip();
            miSpot = new ToolStripMenuItem("Spotlight", null, delegate { Delayed(SPOT); });
            miZoom = new ToolStripMenuItem("Zoom", null, delegate { Delayed(ZOOM); });
            miDraw = new ToolStripMenuItem("Draw", null, delegate { Delayed(DRAW); });
            menu.Items.Add(miSpot);
            menu.Items.Add(miZoom);
            menu.Items.Add(miDraw);
            menu.Items.Add(new ToolStripSeparator());
            var miSettings = new ToolStripMenuItem("Settings / hotkeys...", null, delegate { ShowSettings(); });
            miSettings.Font = new Font(miSettings.Font, FontStyle.Bold);
            menu.Items.Add(miSettings);
            menu.Items.Add(new ToolStripMenuItem("Exit", null, delegate { ExitThread(); }));

            tray = new NotifyIcon();
            tray.Icon = MakeIcon();
            tray.Text = "FocusKit";
            tray.ContextMenuStrip = menu;
            tray.Visible = true;
            tray.DoubleClick += delegate { ShowSettings(); };

            RegisterHotkeys();
            UpdateMenu();

            if (selfTest) { RunSelfTest(); return; }

            if (settings.FirstRun)
            {
                settings.Save();
                tray.ShowBalloonTip(8000, "FocusKit is running",
                    "Spotlight: " + settings.Spotlight + "\nZoom: " + settings.Zoom + "\nDraw: " + settings.Draw +
                    "\nRight-click the tray icon to change hotkeys.", ToolTipIcon.Info);
            }
        }

        // Opens each mode for a moment through the same path as the hotkeys, logs the result, then exits.
        void RunSelfTest()
        {
            using (var cap = Native.CaptureScreen(Screen.PrimaryScreen.Bounds))
            {
                long sum = 0; int n = 0, distinct = 0; int prev = 0;
                for (int y = 0; y < cap.Height; y += 37)
                    for (int x = 0; x < cap.Width; x += 37)
                    {
                        var c = cap.GetPixel(x, y);
                        sum += c.R + c.G + c.B; n++;
                        if (c.ToArgb() != prev) distinct++;
                        prev = c.ToArgb();
                    }
                Log.Write("SELFTEST capture " + cap.Width + "x" + cap.Height + " meanRGB=" + (sum / (3 * n)) + " colourChanges=" + distinct + "/" + n);
            }
            {
                var r = Screen.PrimaryScreen.Bounds;
                IntPtr screen = Native.GetDC(IntPtr.Zero), mdc = Native.CreateCompatibleDC(screen);
                IntPtr bmp = Native.CreateCompatibleBitmap(screen, r.Width, r.Height), old = Native.SelectObject(mdc, bmp);
                int t0 = Environment.TickCount;
                for (int k = 0; k < 3; k++) Native.BitBlt(mdc, 0, 0, r.Width, r.Height, screen, r.X, r.Y, 0x00CC0020);
                int plain = Environment.TickCount - t0;
                t0 = Environment.TickCount;
                for (int k = 0; k < 3; k++) Native.BitBlt(mdc, 0, 0, r.Width, r.Height, screen, r.X, r.Y, 0x00CC0020 | 0x40000000);
                int cblt = Environment.TickCount - t0;
                Native.SelectObject(mdc, old); Native.DeleteObject(bmp); Native.DeleteDC(mdc); Native.ReleaseDC(IntPtr.Zero, screen);
                Log.Write("SELFTEST screen grab: plain " + plain / 3 + " ms, captureblt " + cblt / 3 + " ms");
            }
            int[] steps = { SPOT, SPOT, ZOOM, DRAW, DRAW, DRAW, ZOOM, ZOOM };
            int i = 0;
            var t = new System.Windows.Forms.Timer();
            t.Interval = 700;
            t.Tick += delegate
            {
                if (i > 0)
                    Log.Write("SELFTEST after toggle " + steps[i - 1] + ": active=" +
                        (active == null ? "none" : active.GetType().Name + " visible=" + active.Visible + " bounds=" + active.Bounds));
                if (i == steps.Length) { t.Stop(); Log.Write("SELFTEST done"); ExitThread(); return; }
                if (i > 0 && active is ZoomForm) Log.Write("SELFTEST zoom smooth pass shown=" + ((ZoomForm)active).SmoothShown + "; render: " + ((ZoomForm)active).Benchmark());
                int t0 = Environment.TickCount;
                try { Toggle(steps[i]); Log.Write("SELFTEST toggle " + steps[i] + " took " + (Environment.TickCount - t0) + " ms"); }
                catch (Exception ex) { Log.Write("SELFTEST ERROR " + ex); }
                i++;
            };
            t.Start();
        }

        void Delayed(int kind)
        {
            // let the tray menu disappear before we capture the screen
            var t = new System.Windows.Forms.Timer();
            t.Interval = 250;
            t.Tick += delegate { t.Stop(); t.Dispose(); Toggle(kind); };
            t.Start();
        }

        void UpdateMenu()
        {
            miSpot.ShortcutKeyDisplayString = settings.Spotlight.ToString();
            miZoom.ShortcutKeyDisplayString = settings.Zoom.ToString();
            miDraw.ShortcutKeyDisplayString = settings.Draw.ToString();
        }

        void UnregisterHotkeys()
        {
            for (int i = 1; i <= 3; i++) Native.UnregisterHotKey(hk.Handle, i);
        }

        void RegisterHotkeys()
        {
            UnregisterHotkeys();
            var failed = new List<string>();
            Hotkey[] keys = { settings.Spotlight, settings.Zoom, settings.Draw };
            string[] names = { "Spotlight", "Zoom", "Draw" };
            for (int i = 0; i < 3; i++)
            {
                if (keys[i].IsEmpty) continue;
                bool ok = Native.RegisterHotKey(hk.Handle, i + 1, (uint)keys[i].Mods | 0x4000 /*MOD_NOREPEAT*/, (uint)keys[i].Key);
                Log.Write("Register " + names[i] + " " + keys[i] + ": " + (ok ? "ok" : "FAILED (error " + Marshal.GetLastWin32Error() + ")"));
                if (!ok) failed.Add(names[i] + " (" + keys[i] + ")");
            }
            if (failed.Count > 0)
                tray.ShowBalloonTip(8000, "FocusKit: hotkey unavailable",
                    "Already used by another app: " + string.Join(", ", failed.ToArray()) +
                    ". Right-click the tray icon > Settings to choose another.", ToolTipIcon.Warning);
        }

        void ShowSettings()
        {
            if (settingsForm != null && !settingsForm.IsDisposed) { settingsForm.Activate(); return; }
            if (active != null && !active.IsDisposed) active.Close();
            UnregisterHotkeys(); // so the current combos can be typed into the boxes
            settingsForm = new SettingsForm(settings);
            settingsForm.FormClosed += delegate
            {
                if (settingsForm.DialogResult == DialogResult.OK) { settings.Save(); UpdateMenu(); }
                settingsForm = null;
                RegisterHotkeys();
            };
            settingsForm.Show();
            settingsForm.Activate();
        }

        void Toggle(int kind)
        {
            if (settingsForm != null) return;
            if (active != null && !active.IsDisposed)
            {
                int k = activeKind;
                if (k == ZOOM && kind == DRAW) { ((ZoomForm)active).StartDrawing(); return; }
                active.Close();
                active = null;
                if (k == kind) return;
                Application.DoEvents();
                Thread.Sleep(60); // let DWM remove the old overlay before capturing
            }

            Rectangle mon = Screen.FromPoint(Cursor.Position).Bounds;
            Form f;
            if (kind == SPOT) f = new SpotlightForm(settings);
            else if (kind == ZOOM) f = new ZoomForm(settings, this, mon);
            else f = new DrawForm(settings, mon, Native.CaptureScreen(mon));
            SetActive(f, kind);
        }

        public void SetActive(Form f, int kind)
        {
            active = f;
            activeKind = kind;
            f.FormClosed += delegate { if (active == f) active = null; };
            f.Show();
            if (kind != SPOT)
            {
                f.Activate();
                Native.SetForegroundWindow(f.Handle);
            }
        }

        protected override void ExitThreadCore()
        {
            UnregisterHotkeys();
            if (active != null && !active.IsDisposed) active.Close();
            tray.Visible = false;
            tray.Dispose();
            hk.DestroyHandle();
            base.ExitThreadCore();
        }

        static Icon MakeIcon()
        {
            var b = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var br = new SolidBrush(Color.FromArgb(35, 35, 45))) g.FillEllipse(br, 1, 1, 30, 30);
                using (var br = new SolidBrush(Color.FromArgb(255, 214, 10))) g.FillEllipse(br, 8, 8, 16, 16);
                using (var p = new Pen(Color.White, 2f)) g.DrawEllipse(p, 2, 2, 28, 28);
            }
            return Icon.FromHandle(b.GetHicon());
        }
    }

    // ------------------------------------------------------------------ overlay base
    class OverlayForm : Form
    {
        public OverlayForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            KeyPreview = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        }

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x80; /* WS_EX_TOOLWINDOW: hide from Alt-Tab */ return cp; }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x02E0) return; // WM_DPICHANGED: keep exact physical-pixel bounds
            base.WndProc(ref m);
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }
    }

    // ------------------------------------------------------------------ spotlight
    // A per-pixel-alpha layered window covering every monitor. The dimmed area and the
    // soft-edged hole live in one bitmap that Windows composites atomically, so it can't tear.
    // Fully transparent pixels (inside the hole) let clicks through to the app below.
    unsafe class SpotlightForm : OverlayForm
    {
        const int Feather = 28;
        readonly Settings s;
        readonly Rectangle vs;
        readonly System.Windows.Forms.Timer timer;
        Point last = new Point(-100000, -100000);
        int lastR = -1;
        IntPtr memDc, dib, oldBmp;
        uint* bits;
        int W, H;
        uint dimPixel;
        Native.HookProc mouseProc, keyProc;
        IntPtr mouseHook, keyHook;

        public SpotlightForm(Settings s)
        {
            this.s = s;
            vs = SystemInformation.VirtualScreen;
            W = vs.Width; H = vs.Height;
            Bounds = vs;
            dimPixel = (uint)(s.SpotDim * 255 / 100) << 24; // premultiplied black
            timer = new System.Windows.Forms.Timer();
            timer.Interval = 8;
            timer.Tick += delegate { UpdateHole(false); };
        }

        // don't steal focus from the app being presented (PowerPoint etc.)
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x00080000 /*WS_EX_LAYERED*/ | 0x08000000 /*WS_EX_NOACTIVATE*/ | 0x8 /*WS_EX_TOPMOST*/;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            var bmi = new Native.BITMAPINFOHEADER();
            bmi.biSize = Marshal.SizeOf(typeof(Native.BITMAPINFOHEADER));
            bmi.biWidth = W;
            bmi.biHeight = -H; // top-down
            bmi.biPlanes = 1;
            bmi.biBitCount = 32;
            memDc = Native.CreateCompatibleDC(IntPtr.Zero);
            IntPtr p;
            dib = Native.CreateDIBSection(memDc, ref bmi, 0, out p, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero) throw new InvalidOperationException("Could not allocate spotlight surface");
            bits = (uint*)p;
            oldBmp = Native.SelectObject(memDc, dib);
            long n = (long)W * H;
            for (long i = 0; i < n; i++) bits[i] = dimPixel;
            Point c = Cursor.Position;
            c.Offset(-vs.X, -vs.Y);
            last = c; lastR = s.SpotRadius;
            DrawHole(c, lastR, Circle(c, lastR + Feather + 1));
            Present(Rectangle.Empty, true);
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            if (memDc != IntPtr.Zero)
            {
                Native.SelectObject(memDc, oldBmp);
                Native.DeleteObject(dib);
                Native.DeleteDC(memDc);
                memDc = IntPtr.Zero;
                bits = null;
            }
            base.OnHandleDestroyed(e);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            timer.Start();
            IntPtr mod = Native.GetModuleHandle(null);
            mouseProc = MouseHook;
            keyProc = KeyHook;
            mouseHook = Native.SetWindowsHookEx(14 /*WH_MOUSE_LL*/, mouseProc, mod, 0);
            keyHook = Native.SetWindowsHookEx(13 /*WH_KEYBOARD_LL*/, keyProc, mod, 0);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Stop();
            if (mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(mouseHook);
            if (keyHook != IntPtr.Zero) Native.UnhookWindowsHookEx(keyHook);
            mouseHook = keyHook = IntPtr.Zero;
            s.Save();
            base.OnFormClosed(e);
        }

        IntPtr MouseHook(int n, IntPtr w, IntPtr l)
        {
            // Ctrl + mouse wheel resizes the spotlight
            if (n >= 0 && w.ToInt32() == 0x020A && Native.IsDown(0x11))
            {
                var d = (Native.MSLLHOOKSTRUCT)Marshal.PtrToStructure(l, typeof(Native.MSLLHOOKSTRUCT));
                int delta = (short)((d.mouseData >> 16) & 0xFFFF);
                int step = Math.Max(6, s.SpotRadius / 8);
                s.SpotRadius = Math.Max(20, Math.Min(2000, s.SpotRadius + (delta > 0 ? step : -step)));
                UpdateHole(false);
                return (IntPtr)1;
            }
            return Native.CallNextHookEx(mouseHook, n, w, l);
        }

        IntPtr KeyHook(int n, IntPtr w, IntPtr l)
        {
            if (n >= 0 && (w.ToInt32() == 0x100 || w.ToInt32() == 0x104))
            {
                var d = (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(l, typeof(Native.KBDLLHOOKSTRUCT));
                if (d.vkCode == 0x1B) // Esc closes the spotlight (and isn't passed to the app below)
                {
                    BeginInvoke(new MethodInvoker(Close));
                    return (IntPtr)1;
                }
            }
            return Native.CallNextHookEx(keyHook, n, w, l);
        }

        static Rectangle Circle(Point c, int r) { return new Rectangle(c.X - r, c.Y - r, 2 * r, 2 * r); }

        Rectangle Clip(Rectangle r) { r.Intersect(new Rectangle(0, 0, W, H)); return r; }

        void FillDim(Rectangle r)
        {
            r = Clip(r);
            for (int y = r.Top; y < r.Bottom; y++)
            {
                uint* row = bits + (long)y * W;
                for (int x = r.Left; x < r.Right; x++) row[x] = dimPixel;
            }
        }

        void DrawHole(Point c, int rad, Rectangle r)
        {
            r = Clip(r);
            float a = dimPixel >> 24;
            float r0 = rad, r1 = rad + Feather;
            float r0sq = r0 * r0, r1sq = r1 * r1;
            for (int y = r.Top; y < r.Bottom; y++)
            {
                uint* row = bits + (long)y * W;
                float dy = y + 0.5f - c.Y;
                for (int x = r.Left; x < r.Right; x++)
                {
                    float dx = x + 0.5f - c.X;
                    float dsq = dx * dx + dy * dy;
                    if (dsq <= r0sq) { row[x] = 0; continue; }
                    if (dsq >= r1sq) { row[x] = dimPixel; continue; }
                    float t = ((float)Math.Sqrt(dsq) - r0) / Feather;
                    t = t * t * (3 - 2 * t); // smoothstep
                    row[x] = (uint)(a * t + 0.5f) << 24;
                }
            }
        }

        void Present(Rectangle dirty, bool full)
        {
            var dst = new Native.POINT { x = vs.X, y = vs.Y };
            var size = new Native.SIZE { cx = W, cy = H };
            var src = new Native.POINT();
            var blend = new Native.BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 /*AC_SRC_ALPHA*/ };
            dirty = Clip(dirty);
            var rc = new Native.RECT { left = dirty.Left, top = dirty.Top, right = dirty.Right, bottom = dirty.Bottom };
            var info = new Native.ULWINFO();
            info.cbSize = sizeof(Native.ULWINFO);
            info.pptDst = &dst;
            info.psize = &size;
            info.hdcSrc = memDc;
            info.pptSrc = &src;
            info.pblend = &blend;
            info.dwFlags = 2; // ULW_ALPHA
            info.prcDirty = (full || dirty.IsEmpty) ? null : &rc;
            Native.UpdateLayeredWindowIndirect(Handle, ref info);
        }

        void UpdateHole(bool force)
        {
            if (IsDisposed || bits == null) return;
            Point p = Cursor.Position;
            p.Offset(-vs.X, -vs.Y);
            int r = s.SpotRadius;
            if (!force && p == last && r == lastR) return;
            Rectangle oldArea = Circle(last, lastR + Feather + 1);
            Rectangle newArea = Circle(p, r + Feather + 1);
            FillDim(oldArea);
            DrawHole(p, r, newArea);
            last = p;
            lastR = r;
            Present(Rectangle.Union(oldArea, newArea), force);
        }

        protected override void OnPaint(PaintEventArgs e) { }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right) Close();
            base.OnMouseUp(e);
        }
    }

    // ------------------------------------------------------------------ zoom
    // Uses plain GDI StretchBlt from a device bitmap straight onto the window: far faster than
    // GDI+ at 4K. Fast (COLORONCOLOR) while moving/animating, smooth (HALFTONE) once settled.
    class ZoomForm : OverlayForm
    {
        readonly Settings s;
        readonly TrayApp app;
        readonly Rectangle mon;
        readonly System.Windows.Forms.Timer timer;
        IntPtr srcDc, srcBmp, srcOld;
        // background smoothing: its own copy of the source plus an output buffer
        IntPtr hqSrcDc, hqSrcBmp, hqSrcOld, hqDstDc, hqDstBmp, hqDstOld;
        readonly object hqLock = new object();
        volatile bool hqBusy, closed;
        int generation;
        float z = 1f, target;
        Point mouse;
        bool hqDone = true;
        int lastChange = Environment.TickCount;

        public ZoomForm(Settings s, TrayApp app, Rectangle mon)
        {
            this.s = s; this.app = app; this.mon = mon;
            Bounds = mon;
            BackColor = Color.Black;
            DoubleBuffered = false;
            SetStyle(ControlStyles.OptimizedDoubleBuffer, false);
            target = s.ZoomLevel;
            mouse = CursorInForm();

            IntPtr screen = Native.GetDC(IntPtr.Zero);
            srcDc = Native.CreateCompatibleDC(screen);
            srcBmp = Native.CreateCompatibleBitmap(screen, mon.Width, mon.Height);
            srcOld = Native.SelectObject(srcDc, srcBmp);
            Native.BitBlt(srcDc, 0, 0, mon.Width, mon.Height, screen, mon.X, mon.Y, 0x00CC0020 /*SRCCOPY*/);
            hqSrcDc = Native.CreateCompatibleDC(screen);
            hqSrcBmp = Native.CreateCompatibleBitmap(screen, mon.Width, mon.Height);
            hqSrcOld = Native.SelectObject(hqSrcDc, hqSrcBmp);
            Native.BitBlt(hqSrcDc, 0, 0, mon.Width, mon.Height, srcDc, 0, 0, 0x00CC0020);
            hqDstDc = Native.CreateCompatibleDC(screen);
            hqDstBmp = Native.CreateCompatibleBitmap(screen, mon.Width, mon.Height);
            hqDstOld = Native.SelectObject(hqDstDc, hqDstBmp);
            Native.SetStretchBltMode(hqDstDc, 4 /*HALFTONE*/);
            Native.SetBrushOrgEx(hqDstDc, 0, 0, IntPtr.Zero);
            Native.ReleaseDC(IntPtr.Zero, screen);

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 1;
            timer.Tick += delegate { Step(); };
        }

        Point CursorInForm()
        {
            Point p = Cursor.Position;
            return new Point(Math.Max(0, Math.Min(mon.Width - 1, p.X - mon.X)), Math.Max(0, Math.Min(mon.Height - 1, p.Y - mon.Y)));
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Bounds = mon;
            timer.Start();
        }

        void Step()
        {
            bool changed = false;
            Point p = CursorInForm();
            if (p != mouse) { mouse = p; changed = true; }
            if (z != target)
            {
                z += (target - z) * 0.3f;
                if (Math.Abs(z - target) < 0.01f) z = target;
                changed = true;
            }
            if (changed) { hqDone = false; generation++; lastChange = Environment.TickCount; RenderNow(false); }
            else if (!hqDone && !hqBusy && Environment.TickCount - lastChange > 120) StartSmoothPass();
        }

        void SourceRect(out int sx, out int sy, out int sw, out int sh)
        {
            // keep the pixel under the cursor fixed, so moving the mouse pans naturally
            sw = (int)Math.Round(mon.Width / z); sh = (int)Math.Round(mon.Height / z);
            sx = (int)Math.Round(mouse.X * (1 - 1 / z)); sy = (int)Math.Round(mouse.Y * (1 - 1 / z));
            sx = Math.Max(0, Math.Min(mon.Width - sw, sx));
            sy = Math.Max(0, Math.Min(mon.Height - sh, sy));
        }

        // The ~100 ms HALFTONE render runs on a worker thread; if the view changed meanwhile it's discarded.
        void StartSmoothPass()
        {
            int sx, sy, sw, sh;
            SourceRect(out sx, out sy, out sw, out sh);
            int gen = generation;
            hqBusy = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                lock (hqLock)
                {
                    if (closed) { FreeHq(); return; }
                    Native.StretchBlt(hqDstDc, 0, 0, mon.Width, mon.Height, hqSrcDc, sx, sy, sw, sh, 0x00CC0020);
                    Native.GdiFlush();
                    hqBusy = false; // cleared under the lock, so OnFormClosed always knows who frees
                }
                try
                {
                    BeginInvoke(new MethodInvoker(delegate
                    {
                        if (closed || gen != generation) return;
                        lock (hqLock)
                        {
                            if (closed || hqDstDc == IntPtr.Zero) return;
                            hqDone = true;
                            IntPtr dc = Native.GetDC(Handle);
                            Native.BitBlt(dc, 0, 0, mon.Width, mon.Height, hqDstDc, 0, 0, 0x00CC0020);
                            Native.ReleaseDC(Handle, dc);
                        }
                    }));
                }
                catch { }
            });
        }

        void FreeHq()
        {
            if (hqSrcDc == IntPtr.Zero) return;
            Native.SelectObject(hqSrcDc, hqSrcOld); Native.DeleteObject(hqSrcBmp); Native.DeleteDC(hqSrcDc);
            Native.SelectObject(hqDstDc, hqDstOld); Native.DeleteObject(hqDstBmp); Native.DeleteDC(hqDstDc);
            hqSrcDc = hqDstDc = IntPtr.Zero;
        }

        void RenderNow(bool hq)
        {
            if (!IsHandleCreated) return;
            IntPtr dc = Native.GetDC(Handle);
            Render(dc, hq);
            Native.ReleaseDC(Handle, dc);
        }

        void Render(IntPtr dc, bool hq)
        {
            // keep the pixel under the cursor fixed, so moving the mouse pans naturally
            int sw = (int)Math.Round(mon.Width / z), sh = (int)Math.Round(mon.Height / z);
            int sx = (int)Math.Round(mouse.X * (1 - 1 / z)), sy = (int)Math.Round(mouse.Y * (1 - 1 / z));
            sx = Math.Max(0, Math.Min(mon.Width - sw, sx));
            sy = Math.Max(0, Math.Min(mon.Height - sh, sy));
            Native.SetStretchBltMode(dc, hq ? 4 /*HALFTONE*/ : 3 /*COLORONCOLOR*/);
            if (hq) Native.SetBrushOrgEx(dc, 0, 0, IntPtr.Zero);
            Native.StretchBlt(dc, 0, 0, mon.Width, mon.Height, srcDc, sx, sy, sw, sh, 0x00CC0020);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            IntPtr dc = e.Graphics.GetHdc();
            if (hqDone) lock (hqLock) Native.BitBlt(dc, 0, 0, mon.Width, mon.Height, hqDstDc, 0, 0, 0x00CC0020);
            else Render(dc, false);
            e.Graphics.ReleaseHdc(dc);
        }

        public bool SmoothShown { get { return hqDone; } }

        // used by the self-test
        public string Benchmark()
        {
            IntPtr dc = Native.GetDC(Handle);
            float saved = z; z = 2f;
            int t0 = Environment.TickCount;
            for (int i = 0; i < 20; i++) { mouse = new Point(100 + i * 50, 100 + i * 30); Render(dc, false); }
            int fast = Environment.TickCount - t0;
            t0 = Environment.TickCount;
            for (int i = 0; i < 3; i++) Render(dc, true);
            int slow = Environment.TickCount - t0;
            z = saved;
            Native.ReleaseDC(Handle, dc);
            return "fast frame " + (fast / 20.0) + " ms, smooth frame " + (slow / 3.0) + " ms";
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            ZoomBy(e.Delta > 0 ? 1.25f : 0.8f);
        }

        void ZoomBy(float factor)
        {
            target = Math.Max(1f, Math.Min(16f, target * factor));
            if (target >= 1.25f) s.ZoomLevel = target;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) Close();
            else if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Oemplus || e.KeyCode == Keys.Add) ZoomBy(1.25f);
            else if (e.KeyCode == Keys.Down || e.KeyCode == Keys.OemMinus || e.KeyCode == Keys.Subtract) ZoomBy(0.8f);
            base.OnKeyDown(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) StartDrawing();
            else if (e.Button == MouseButtons.Right) Close();
        }

        // freeze the zoomed view and switch to draw mode on top of it
        public void StartDrawing()
        {
            z = target;
            var raw = new Bitmap(mon.Width, mon.Height, PixelFormat.Format32bppRgb);
            using (var g = Graphics.FromImage(raw))
            {
                IntPtr dc = g.GetHdc();
                Render(dc, true);
                g.ReleaseHdc(dc);
            }
            var view = new Bitmap(mon.Width, mon.Height, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(view)) g.DrawImageUnscaled(raw, 0, 0);
            raw.Dispose();
            var d = new DrawForm(s, mon, view);
            app.SetActive(d, TrayApp.DRAW);
            Close();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            timer.Stop();
            lock (hqLock)
            {
                closed = true;
                if (!hqBusy) FreeHq(); // otherwise the worker frees it when it finishes
            }
            if (srcDc != IntPtr.Zero)
            {
                Native.SelectObject(srcDc, srcOld);
                Native.DeleteObject(srcBmp);
                Native.DeleteDC(srcDc);
                srcDc = IntPtr.Zero;
            }
            s.Save();
            base.OnFormClosed(e);
        }
    }

    // ------------------------------------------------------------------ draw
    class Shape
    {
        public const int Free = 0, Line = 1, Rect = 2, Ellipse = 3, Arrow = 4;
        public int Kind;
        public Color Color;
        public float Width;
        public List<Point> Pts = new List<Point>();
    }

    class DrawForm : OverlayForm
    {
        static readonly Color[] Palette = {
            Color.FromArgb(255, 59, 48), Color.FromArgb(52, 199, 89), Color.FromArgb(10, 132, 255),
            Color.FromArgb(255, 214, 10), Color.FromArgb(255, 149, 0), Color.FromArgb(255, 45, 149),
        };
        static readonly string[] PaletteNames = { "Red", "Green", "Blue", "Yellow", "Orange", "Pink" };
        static readonly Keys[] PaletteKeys = { Keys.R, Keys.G, Keys.B, Keys.Y, Keys.O, Keys.P };

        const string HelpText =
            "Draw: drag    Shift: line    Ctrl: rectangle    Tab: ellipse    Ctrl+Shift: arrow\n" +
            "Colours: R G B Y O P    H: highlighter    Wheel / + -: pen size    Ctrl+Z: undo    E: erase all\n" +
            "W / K: white / black board    Ctrl+C: copy    Ctrl+S: save PNG    Esc or right-click: exit";

        readonly Settings s;
        readonly Rectangle mon;
        readonly Bitmap shot;
        Bitmap committed;
        readonly List<Shape> shapes = new List<Shape>();
        Shape cur;
        Rectangle curBounds = Rectangle.Empty;
        Color board = Color.Empty;
        bool highlighter;
        string hint = HelpText;
        DateTime hintUntil;
        Rectangle hintRect = Rectangle.Empty;
        readonly System.Windows.Forms.Timer hintTimer = new System.Windows.Forms.Timer();

        public DrawForm(Settings s, Rectangle mon, Bitmap shot)
        {
            this.s = s; this.mon = mon; this.shot = shot;
            Bounds = mon;
            BackColor = Color.Black;
            Cursor = Cursors.Cross;
            hintUntil = DateTime.Now.AddSeconds(6);
            hintTimer.Interval = 250;
            hintTimer.Tick += delegate
            {
                if (DateTime.Now > hintUntil && !hintRect.IsEmpty) { Invalidate(hintRect); hintRect = Rectangle.Empty; }
            };
            Rebuild();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Bounds = mon;
            hintTimer.Start();
        }

        void ShowHint(string text, double seconds)
        {
            if (!hintRect.IsEmpty) Invalidate(hintRect);
            hint = text;
            hintUntil = DateTime.Now.AddSeconds(seconds);
            hintRect = Rectangle.Empty;
            Invalidate(new Rectangle(0, 0, mon.Width, mon.Height / 4));
        }

        Color CurrentColor
        {
            get { var c = Color.FromArgb(s.PenColor); return highlighter ? Color.FromArgb(110, c) : Color.FromArgb(255, c); }
        }

        string PenDescription()
        {
            string name = "Custom";
            for (int i = 0; i < Palette.Length; i++) if (Palette[i].ToArgb() == s.PenColor) name = PaletteNames[i];
            return name + (highlighter ? " highlighter" : " pen") + "  -  " + s.PenWidth + " px";
        }

        void Rebuild()
        {
            if (committed != null) committed.Dispose();
            committed = new Bitmap(shot.Width, shot.Height, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(committed))
            {
                if (board.IsEmpty) g.DrawImageUnscaled(shot, 0, 0);
                else g.Clear(board);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                foreach (var sh in shapes) Render(g, sh);
            }
            Invalidate();
        }

        static Rectangle Norm(Point a, Point b)
        {
            return Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        }

        static void Render(Graphics g, Shape sh)
        {
            try
            {
                var p = sh.Pts;
                using (var pen = new Pen(sh.Color, sh.Width))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    pen.LineJoin = LineJoin.Round;
                    switch (sh.Kind)
                    {
                        case Shape.Free:
                            if (p.Count == 1)
                                using (var b = new SolidBrush(sh.Color))
                                    g.FillEllipse(b, p[0].X - sh.Width / 2, p[0].Y - sh.Width / 2, sh.Width, sh.Width);
                            else g.DrawLines(pen, p.ToArray());
                            break;
                        case Shape.Line: g.DrawLine(pen, p[0], p[1]); break;
                        case Shape.Rect: g.DrawRectangle(pen, Norm(p[0], p[1])); break;
                        case Shape.Ellipse: g.DrawEllipse(pen, Norm(p[0], p[1])); break;
                        case Shape.Arrow:
                            if (p[0] == p[1]) break;
                            float u = Math.Max(3f, 18f / sh.Width);
                            using (var cap = new AdjustableArrowCap(u, u, true))
                            {
                                pen.CustomEndCap = cap;
                                g.DrawLine(pen, p[0], p[1]);
                            }
                            break;
                    }
                }
            }
            catch { }
        }

        Rectangle ShapeBounds(Shape sh, int from)
        {
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue;
            for (int i = Math.Max(0, from); i < sh.Pts.Count; i++)
            {
                var q = sh.Pts[i];
                x0 = Math.Min(x0, q.X); y0 = Math.Min(y0, q.Y); x1 = Math.Max(x1, q.X); y1 = Math.Max(y1, q.Y);
            }
            int pad = (int)(sh.Width * (sh.Kind == Shape.Arrow ? 3 : 1)) + 24;
            var r = Rectangle.FromLTRB(x0, y0, x1, y1);
            r.Inflate(pad, pad);
            return r;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            bool ctrl = (ModifierKeys & Keys.Control) != 0, shift = (ModifierKeys & Keys.Shift) != 0;
            bool tab = Native.IsDown(0x09);
            cur = new Shape();
            cur.Kind = ctrl && shift ? Shape.Arrow : ctrl ? Shape.Rect : shift ? Shape.Line : tab ? Shape.Ellipse : Shape.Free;
            cur.Color = CurrentColor;
            cur.Width = highlighter ? s.PenWidth * 4 : s.PenWidth;
            cur.Pts.Add(e.Location);
            if (cur.Kind != Shape.Free) cur.Pts.Add(e.Location);
            Capture = true;
            curBounds = ShapeBounds(cur, 0);
            Invalidate(curBounds);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (cur == null) return;
            if (cur.Kind == Shape.Free)
            {
                var lastPt = cur.Pts[cur.Pts.Count - 1];
                if (lastPt == e.Location) return;
                cur.Pts.Add(e.Location);
                Invalidate(ShapeBounds(cur, cur.Pts.Count - 2));
            }
            else
            {
                cur.Pts[1] = e.Location;
                var nb = ShapeBounds(cur, 0);
                Invalidate(curBounds);
                Invalidate(nb);
                curBounds = nb;
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right && cur == null) { Close(); return; }
            if (e.Button != MouseButtons.Left || cur == null) return;
            shapes.Add(cur);
            using (var g = Graphics.FromImage(committed))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Render(g, cur);
            }
            Invalidate(ShapeBounds(cur, 0));
            cur = null;
            Capture = false;
        }

        protected override void OnMouseWheel(MouseEventArgs e) { ChangeWidth(e.Delta > 0 ? 1 : -1); }

        void ChangeWidth(int dir)
        {
            int step = s.PenWidth >= 12 ? 3 : 1;
            s.PenWidth = Math.Max(1, Math.Min(60, s.PenWidth + dir * step));
            ShowHint(PenDescription(), 1.5);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            e.Handled = true;
            if (e.KeyCode == Keys.Escape) { Close(); return; }
            if (e.Control && e.KeyCode == Keys.Z)
            {
                if (cur == null && shapes.Count > 0) { shapes.RemoveAt(shapes.Count - 1); Rebuild(); }
                return;
            }
            if (e.Control && e.KeyCode == Keys.C)
            {
                try { Clipboard.SetImage(committed); ShowHint("Copied to clipboard", 1.5); } catch { }
                return;
            }
            if (e.Control && e.KeyCode == Keys.S) { SavePng(); return; }
            if (e.Control || e.Alt) return;

            for (int i = 0; i < PaletteKeys.Length; i++)
                if (e.KeyCode == PaletteKeys[i]) { s.PenColor = Palette[i].ToArgb(); ShowHint(PenDescription(), 1.5); return; }

            switch (e.KeyCode)
            {
                case Keys.H: highlighter = !highlighter; ShowHint(PenDescription(), 1.5); break;
                case Keys.E: shapes.Clear(); Rebuild(); break;
                case Keys.W: board = board == Color.White ? Color.Empty : Color.White; Rebuild(); break;
                case Keys.K: board = board == Color.Black ? Color.Empty : Color.Black; Rebuild(); break;
                case Keys.Oemplus: case Keys.Add: case Keys.Up: ChangeWidth(1); break;
                case Keys.OemMinus: case Keys.Subtract: case Keys.Down: ChangeWidth(-1); break;
                case Keys.F1: case Keys.OemQuestion: ShowHint(HelpText, 6); break;
            }
        }

        // keep Tab from being swallowed as focus navigation
        protected override bool ProcessDialogKey(Keys keyData)
        {
            if ((keyData & Keys.KeyCode) == Keys.Tab) return true;
            return base.ProcessDialogKey(keyData);
        }

        void SavePng()
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "FocusKit");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "FocusKit " + DateTime.Now.ToString("yyyy-MM-dd HHmmss") + ".png");
                committed.Save(file, ImageFormat.Png);
                ShowHint("Saved " + file, 3);
            }
            catch (Exception ex) { ShowHint("Save failed: " + ex.Message, 3); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var clip = e.ClipRectangle;
            g.DrawImage(committed, clip, clip, GraphicsUnit.Pixel);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (cur != null) Render(g, cur);
            if (DateTime.Now < hintUntil) DrawHint(g);
        }

        void DrawHint(Graphics g)
        {
            using (var font = new Font("Segoe UI", 10.5f * DeviceDpiScale()))
            {
                var size = g.MeasureString(hint, font);
                int pad = (int)(12 * DeviceDpiScale());
                var r = new Rectangle((int)((mon.Width - size.Width) / 2) - pad, pad * 2, (int)size.Width + pad * 2, (int)size.Height + pad);
                hintRect = Rectangle.Inflate(r, 2, 2);
                using (var path = RoundRect(r, pad))
                using (var bg = new SolidBrush(Color.FromArgb(215, 25, 25, 30)))
                    g.FillPath(bg, path);
                // swatch of the current colour
                using (var sw = new SolidBrush(CurrentColor))
                    g.FillEllipse(sw, r.X + 6, r.Y + 6, pad - 4, pad - 4);
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.DrawString(hint, font, Brushes.White, r.X + pad, r.Y + pad / 2f);
            }
        }

        float DeviceDpiScale()
        {
            using (var g = CreateGraphics()) return g.DpiX / 96f;
        }

        static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            hintTimer.Stop();
            s.Save();
            if (committed != null) committed.Dispose();
            shot.Dispose();
            base.OnFormClosed(e);
        }
    }

    // ------------------------------------------------------------------ settings UI
    class HotkeyBox : TextBox
    {
        public Hotkey Value;

        public HotkeyBox(Hotkey initial)
        {
            Value = new Hotkey(initial.Mods, initial.Key);
            ReadOnly = true;
            BackColor = SystemColors.Window;
            ShortcutsEnabled = false;
            TextAlign = HorizontalAlignment.Center;
            Text = Value.ToString();
        }

        protected override bool IsInputKey(Keys keyData) { return true; }
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            HandleKey(keyData & Keys.KeyCode, keyData & Keys.Modifiers);
            return true;
        }

        void HandleKey(Keys key, Keys modifiers)
        {
            int mods = 0;
            if ((modifiers & Keys.Control) != 0) mods |= Hotkey.CTRL;
            if ((modifiers & Keys.Alt) != 0) mods |= Hotkey.ALT;
            if ((modifiers & Keys.Shift) != 0) mods |= Hotkey.SHIFT;
            if (Native.IsDown(0x5B) || Native.IsDown(0x5C)) mods |= Hotkey.WIN;

            if (key == Keys.ControlKey || key == Keys.ShiftKey || key == Keys.Menu || key == Keys.LWin || key == Keys.RWin)
            {
                Text = new Hotkey(mods, Keys.None).ToString().Replace("(none)", "") + (mods != 0 ? "+..." : "");
                if (mods == 0) Text = Value.ToString();
                return;
            }
            if (mods == 0 && (key == Keys.Back || key == Keys.Delete))
            {
                Value = new Hotkey(0, Keys.None);
                Text = Value.ToString();
                return;
            }
            bool fkey = key >= Keys.F1 && key <= Keys.F24;
            if (mods == 0 && !fkey && key != Keys.Pause && key != Keys.Scroll)
            {
                Text = "Add Ctrl / Alt / Shift / Win";
                return;
            }
            Value = new Hotkey(mods, key);
            Text = Value.ToString();
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (Value != null && (Text.EndsWith("+...") || Text.StartsWith("Add "))) Text = Value.ToString();
            base.OnKeyUp(e);
        }

        protected override void OnLeave(EventArgs e) { Text = Value.ToString(); base.OnLeave(e); }
    }

    class SettingsForm : Form
    {
        readonly Settings s;
        readonly HotkeyBox hkSpot, hkZoom, hkDraw;
        readonly NumericUpDown numRadius, numDim, numZoom, numPen;
        readonly CheckBox chkStartup;

        public SettingsForm(Settings s)
        {
            this.s = s;
            Text = "FocusKit settings";
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Font;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            TopMost = true;
            Padding = new Padding(12);

            float sc;
            using (var g = CreateGraphics()) sc = g.DpiX / 96f;
            int boxW = (int)(200 * sc);

            var t = new TableLayoutPanel();
            t.AutoSize = true;
            t.ColumnCount = 2;
            t.Dock = DockStyle.Fill;

            hkSpot = new HotkeyBox(s.Spotlight) { Width = boxW };
            hkZoom = new HotkeyBox(s.Zoom) { Width = boxW };
            hkDraw = new HotkeyBox(s.Draw) { Width = boxW };
            numRadius = Num(20, 2000, s.SpotRadius, 0, 10, boxW);
            numDim = Num(10, 95, s.SpotDim, 0, 5, boxW);
            numZoom = Num(1.25m, 16, (decimal)s.ZoomLevel, 2, 0.25m, boxW);
            numPen = Num(1, 60, s.PenWidth, 0, 1, boxW);
            chkStartup = new CheckBox { Text = "Start FocusKit when Windows starts", AutoSize = true, Checked = Settings.RunAtStartup };

            Header(t, "Hotkeys  (click a box, press the new combination; Backspace clears)");
            Row(t, "Spotlight", hkSpot);
            Row(t, "Zoom", hkZoom);
            Row(t, "Draw", hkDraw);
            Header(t, "Spotlight");
            Row(t, "Radius (px)  -  Ctrl+wheel while active", numRadius);
            Row(t, "Darkness outside (%)", numDim);
            Header(t, "Zoom & draw");
            Row(t, "Initial zoom level (x)", numZoom);
            Row(t, "Pen width (px)", numPen);
            t.Controls.Add(chkStartup);
            t.SetColumnSpan(chkStartup, 2);
            chkStartup.Margin = new Padding(3, 12, 3, 3);

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 12, 0, 0) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
            cancel.Click += delegate { Close(); }; // form is shown modeless, so close explicitly
            var ok = new Button { Text = "Save", AutoSize = true };
            ok.Click += OnSave;
            var reset = new Button { Text = "Default hotkeys", AutoSize = true };
            reset.Click += delegate
            {
                var d = new Settings();
                hkSpot.Value = d.Spotlight; hkSpot.Text = d.Spotlight.ToString();
                hkZoom.Value = d.Zoom; hkZoom.Text = d.Zoom.ToString();
                hkDraw.Value = d.Draw; hkDraw.Text = d.Draw.ToString();
            };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            buttons.Controls.Add(reset);
            t.Controls.Add(buttons);
            t.SetColumnSpan(buttons, 2);

            var help = new Label
            {
                AutoSize = true,
                MaximumSize = new Size((int)(460 * sc), 0),
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(3, 10, 3, 0),
                Text = "Press a hotkey again (or Esc / right-click) to exit a mode.\n" +
                       "Zoom: wheel or Up/Down changes zoom; left-click (or the Draw hotkey) starts drawing on the zoomed view.\n" +
                       "Draw: Shift line, Ctrl rectangle, Tab ellipse, Ctrl+Shift arrow, R G B Y O P colours, H highlighter, " +
                       "Ctrl+Z undo, E erase, W/K board, Ctrl+C copy, Ctrl+S save."
            };
            t.Controls.Add(help);
            t.SetColumnSpan(help, 2);

            Controls.Add(t);
            AcceptButton = ok;
            CancelButton = cancel;
        }

        static NumericUpDown Num(decimal min, decimal max, decimal val, int decimals, decimal inc, int w)
        {
            return new NumericUpDown
            {
                Minimum = min, Maximum = max, DecimalPlaces = decimals, Increment = inc, Width = w,
                Value = Math.Max(min, Math.Min(max, val))
            };
        }

        static void Header(TableLayoutPanel t, string text)
        {
            var l = new Label { Text = text, AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold), Margin = new Padding(0, 10, 3, 4) };
            t.Controls.Add(l);
            t.SetColumnSpan(l, 2);
        }

        static void Row(TableLayoutPanel t, string label, Control c)
        {
            t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(12, 6, 12, 6) });
            t.Controls.Add(c);
        }

        void OnSave(object sender, EventArgs e)
        {
            var hs = new[] { hkSpot.Value, hkZoom.Value, hkDraw.Value };
            for (int i = 0; i < 3; i++)
                for (int j = i + 1; j < 3; j++)
                    if (hs[i].SameAs(hs[j]))
                    {
                        MessageBox.Show(this, "Two modes use the same hotkey (" + hs[i] + "). Please choose different ones.", "FocusKit");
                        return;
                    }
            s.Spotlight = hkSpot.Value;
            s.Zoom = hkZoom.Value;
            s.Draw = hkDraw.Value;
            s.SpotRadius = (int)numRadius.Value;
            s.SpotDim = (int)numDim.Value;
            s.ZoomLevel = (float)numZoom.Value;
            s.PenWidth = (int)numPen.Value;
            try { Settings.RunAtStartup = chkStartup.Checked; } catch { }
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
