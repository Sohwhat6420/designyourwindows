// designyourwindows: a clock behind the subject of your own wallpaper, frosted-glass widgets and a macOS-style dock,
// in one small native exe. No browser, no web view, no live-wallpaper engine: the wallpaper is an ordinary bitmap,
// recomposed once a minute; the dock is one DWM-blurred window.
//
// Built by build.ps1 with the .NET Framework compiler that ships with Windows (C# 5).
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
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32;

[assembly: AssemblyTitle("designyourwindows")]
[assembly: AssemblyProduct("designyourwindows")]
[assembly: AssemblyCopyright("Copyright (c) 2026 Ethan Soh. MIT licence.")]
[assembly: AssemblyVersion("1.0.0.0")]

namespace DesignYourWindows
{
    static class Program
    {
        const string Name = "designyourwindows";
        const string MutexName = @"Local\designyourwindows.run";
        const string StopName = @"Local\designyourwindows.stop";
        internal static string StateDir;
        internal static bool Verbose;

        [STAThread]
        static int Main(string[] args)
        {
            Native.AttachConsole(-1);
            try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch (EntryPointNotFoundException) { }
            StateDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name);
            Directory.CreateDirectory(StateDir);

            var a = new Args(args);
            Verbose = a.Has("--verbose");
            if (a.Has("--help") || a.Has("-h") || a.Has("/?")) { Say(Help); return 0; }
            if (a.Has("--stop")) return Stop();

            Cfg cfg;
            try { cfg = Cfg.Load(a.Value("--config")); }
            catch (Exception e) { Say("config: " + e.Message); return 2; }

            try
            {
                if (a.Has("--uninstall")) return Install.Remove(cfg);
                if (a.Has("--install")) return Install.Add(cfg, a.Has("--no-clock"), a.Has("--no-dock"));
                var r = new Renderer(cfg);
                if (a.Value("--render") != null) return RenderToFile(cfg, r, a);
                if (a.Has("--no-clock")) return Once(cfg, r);
                return Run(cfg, r, !a.Has("--no-dock") && cfg.Bool("dock.enabled", true), !a.Has("--no-apply"));
            }
            catch (Exception e) { Log("error: " + e); Say(e.Message); return 1; }
        }

        const string Help =
@"designyourwindows: depth-effect clock wallpaper, glass widgets and a dock.

  designyourwindows.exe                 run: recompose the wallpaper every minute (and show the dock)
  designyourwindows.exe --no-clock      render once with the static text and exit (no resident process)
  designyourwindows.exe --no-dock       run without the dock
  designyourwindows.exe --render F.png [--time 2026-11-11T09:41] [--dock-preview]
                                        write one frame to a file; leaves the wallpaper alone
  designyourwindows.exe --install [--no-clock] [--no-dock]
                                        start at sign-in (a per-user scheduled task) and start now
  designyourwindows.exe --uninstall     stop, remove the task, put the previous wallpaper back
  designyourwindows.exe --stop          stop a running copy
  --config PATH                         config.json to use (default: next to the exe, or one folder up)";

        // ---------- modes ----------

        static int RenderToFile(Cfg cfg, Renderer r, Args a)
        {
            DateTime now = DateTime.Now;
            string t = a.Value("--time");
            if (t != null) now = DateTime.Parse(t, CultureInfo.InvariantCulture);
            if (cfg.Bool("weather.enabled", false)) r.Weather = Weather.Fetch(cfg);
            List<DockApp> dock = a.Has("--dock-preview") ? DockApp.Load(cfg) : null;
            var sw = Stopwatch.StartNew();
            using (Bitmap f = r.Compose(now, r.BigText(now, true), true, dock))
            {
                string path = Path.GetFullPath(a.Value("--render"));
                f.Save(path, path.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase) ? ImageFormat.Bmp : ImageFormat.Png);
                Say("wrote " + path + " (" + f.Width + "x" + f.Height + ", " + sw.ElapsedMilliseconds + " ms)");
            }
            return 0;
        }

        static int Once(Cfg cfg, Renderer r)
        {
            DateTime now = DateTime.Now;
            using (Bitmap f = r.Compose(now, r.BigText(now, false), false, null)) Wallpaper.Apply(cfg, f, true);
            Log("rendered once (no clock)");
            return 0;
        }

        static int Run(Cfg cfg, Renderer r, bool withDock, bool apply)
        {
            bool fresh;
            using (var mutex = new Mutex(true, MutexName, out fresh))
            {
                if (!fresh) { Say("already running"); return 3; }
                using (var stop = new EventWaitHandle(false, EventResetMode.AutoReset, StopName))
                {
                    IntPtr timer = Native.CreateWaitableTimerW(IntPtr.Zero, false, null);
                    Dock dock = null;
                    if (withDock)
                    {
                        dock = new Dock(cfg, delegate { stop.Set(); });
                        dock.Show();
                    }
                    bool weatherOn = cfg.Bool("weather.enabled", false);
                    double refresh = Math.Max(10, cfg.Num("weather.refreshMinutes", 30));
                    DateTime lastFetch = DateTime.MinValue;
                    int fetching = 0;
                    bool first = true;
                    var handles = new IntPtr[] { timer, stop.SafeWaitHandle.DangerousGetHandle() };
                    Log("running" + (withDock ? " with dock" : "") + (apply ? "" : " (not applying)"));
                    while (true)
                    {
                        DateTime now = DateTime.Now;
                        if (weatherOn && (now - lastFetch).TotalMinutes >= refresh && Interlocked.CompareExchange(ref fetching, 1, 0) == 0)
                        {
                            lastFetch = now;
                            ThreadPool.QueueUserWorkItem(delegate
                            {
                                Weather w = Weather.Fetch(cfg);
                                if (w != null) { r.Weather = w; }
                                Interlocked.Exchange(ref fetching, 0);
                            });
                        }
                        var sw = Stopwatch.StartNew();
                        using (Bitmap f = r.Compose(now, r.BigText(now, true), true, null))
                            if (apply) Wallpaper.Apply(cfg, f, first); else Wallpaper.Save(f);
                        if (Verbose) Log("frame " + now.ToString("HH:mm:ss") + " in " + sw.ElapsedMilliseconds + " ms");
                        first = false;
                        Trim();

                        // Sleep to the next minute boundary on an absolute waitable timer: it fires at the right wall-clock
                        // time after sleep, hibernation or a clock change, and costs nothing while it waits.
                        DateTime next = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, DateTimeKind.Local).AddMinutes(1);
                        long due = next.ToUniversalTime().ToFileTimeUtc();
                        Native.SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false);
                        if (!Wait(handles)) break;
                    }
                    if (dock != null) dock.Close();
                    Native.CloseHandle(timer);
                    Log("stopped");
                }
                mutex.ReleaseMutex();
            }
            return 0;
        }

        /// <summary>Waits for the timer (true), the stop event (false), pumping the dock's messages meanwhile.</summary>
        static bool Wait(IntPtr[] handles)
        {
            while (true)
            {
                uint w = Native.MsgWaitForMultipleObjectsEx((uint)handles.Length, handles, 0xFFFFFFFF, 0x04FF /*QS_ALLINPUT*/, 0x4 /*MWMO_INPUTAVAILABLE*/);
                if (w == 0) return true;
                if (w == 1) return false;
                if (w != 2) return false;
                Native.MSG m;
                while (Native.PeekMessageW(out m, IntPtr.Zero, 0, 0, 1))
                {
                    if (m.message == 0x0012 /*WM_QUIT*/) return false;
                    Native.TranslateMessage(ref m);
                    Native.DispatchMessageW(ref m);
                }
            }
        }

        static int Stop()
        {
            EventWaitHandle e;
            if (!EventWaitHandle.TryOpenExisting(StopName, out e)) { Say("not running"); return 0; }
            using (e) e.Set();
            Mutex m;
            for (int i = 0; i < 50; i++)
            {
                if (!Mutex.TryOpenExisting(MutexName, out m)) { Say("stopped"); return 0; }
                m.Dispose();
                Thread.Sleep(100);
            }
            Say("asked it to stop");
            return 0;
        }

        /// <summary>Give the frame's memory back between minutes: the process idles at its smallest.</summary>
        internal static void Trim()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Native.SetProcessWorkingSetSize(Process.GetCurrentProcess().Handle, new IntPtr(-1), new IntPtr(-1));
        }

        internal static void Say(string s) { try { Console.WriteLine(s); } catch (IOException) { } Log(s); }

        internal static void Log(string s)
        {
            try
            {
                string p = Path.Combine(StateDir, "log.txt");
                if (File.Exists(p) && new FileInfo(p).Length > 256 * 1024) File.Delete(p);
                File.AppendAllText(p, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + s + Environment.NewLine);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    class Args
    {
        readonly List<string> a;
        public Args(string[] args) { a = new List<string>(args); }
        public bool Has(string k) { return a.Exists(delegate (string x) { return string.Equals(x, k, StringComparison.OrdinalIgnoreCase); }); }
        public string Value(string k)
        {
            int i = a.FindIndex(delegate (string x) { return string.Equals(x, k, StringComparison.OrdinalIgnoreCase); });
            return i >= 0 && i + 1 < a.Count ? a[i + 1] : null;
        }
    }

    // ---------- config ----------

    class Cfg
    {
        public Dictionary<string, object> Root;
        public string Dir;
        public string FilePath;

        public static Cfg Load(string path)
        {
            if (path == null)
            {
                string exeDir = AppDomain.CurrentDomain.BaseDirectory;
                path = Path.Combine(exeDir, "config.json");
                if (!File.Exists(path)) path = Path.GetFullPath(Path.Combine(exeDir, "..", "config.json"));
            }
            path = Path.GetFullPath(path);
            if (!File.Exists(path)) throw new FileNotFoundException("no config.json at " + path);
            var c = new Cfg();
            c.FilePath = path;
            c.Dir = Path.GetDirectoryName(path);
            c.Root = Json.Parse(File.ReadAllText(path)) as Dictionary<string, object>;
            if (c.Root == null) throw new InvalidDataException("config.json is not a JSON object");
            return c;
        }

        public object Get(string path)
        {
            object cur = Root;
            foreach (string k in path.Split('.'))
            {
                var d = cur as Dictionary<string, object>;
                if (d == null || !d.TryGetValue(k, out cur)) return null;
            }
            return cur;
        }
        public string Str(string p, string def) { object v = Get(p); return v == null ? def : Convert.ToString(v, CultureInfo.InvariantCulture); }
        public double Num(string p, double def)
        {
            object v = Get(p);
            if (v == null) return def;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch (FormatException) { return def; }
        }
        public bool Bool(string p, bool def) { object v = Get(p); return v is bool ? (bool)v : def; }
        public string PathOf(string p, string def)
        {
            string s = Str(p, def);
            if (string.IsNullOrEmpty(s)) return s;
            s = Environment.ExpandEnvironmentVariables(s);
            return Path.IsPathRooted(s) ? s : Path.GetFullPath(Path.Combine(Dir, s));
        }
    }

    static class Json
    {
        public static object Parse(string s) { return new JavaScriptSerializer().DeserializeObject(s); }
        public static string Write(object o) { return new JavaScriptSerializer().Serialize(o); }
        public static Dictionary<string, object> Obj(object o) { return o as Dictionary<string, object> ?? new Dictionary<string, object>(); }
        public static double Num(object o, double def)
        {
            if (o == null) return def;
            try { return Convert.ToDouble(o, CultureInfo.InvariantCulture); } catch (FormatException) { return def; }
        }
    }

    // ---------- fonts ----------

    static class Fonts
    {
        static FontFamily[] installed;
        static readonly Dictionary<string, FontFamily> cache = new Dictionary<string, FontFamily>();
        static PrivateFontCollection user;

        /// <summary>A family by name and weight ("Light", "Semilight", "Regular", "Semibold", "Bold"). Windows lists each
        /// weight of a variable face as its own family, cut to 31 characters; Segoe UI is the fallback on Windows 10.</summary>
        public static FontFamily Get(string family, string weight)
        {
            string key = family + "|" + weight;
            FontFamily f;
            if (cache.TryGetValue(key, out f)) return f;
            if (installed == null) installed = new InstalledFontCollection().Families;
            string w = (weight ?? "").Trim();
            if (w.Equals("regular", StringComparison.OrdinalIgnoreCase) || w.Equals("bold", StringComparison.OrdinalIgnoreCase)) w = "";
            foreach (string b in new[] { family, "Segoe UI" })
            {
                if (w.Length > 0 && (f = Find(b + " " + w)) != null) break;
                if ((f = Find(b)) != null) break;
            }
            if (f == null) f = FontFamily.GenericSansSerif;
            cache[key] = f;
            return f;
        }

        public static FontStyle Style(string weight) { return "bold".Equals(weight, StringComparison.OrdinalIgnoreCase) ? FontStyle.Bold : FontStyle.Regular; }

        public static FontFamily FromFile(string path)
        {
            if (user == null) user = new PrivateFontCollection();
            user.AddFontFile(path);
            return user.Families[user.Families.Length - 1];
        }

        static FontFamily Find(string name)
        {
            string cut = name.Length > 31 ? name.Substring(0, 31) : name;
            foreach (FontFamily x in installed)
                if (x.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || x.Name.Equals(cut, StringComparison.OrdinalIgnoreCase)) return x;
            return null;
        }
    }

    // ---------- the frame ----------

    class Renderer
    {
        readonly Cfg cfg;
        readonly string layers;
        int LW, LH;
        Rectangle clockBox;
        Color ink;
        public volatile Weather Weather;
        readonly Dictionary<string, Glass> glassCache = new Dictionary<string, Glass>();
        int cacheW, cacheH;

        class Glass { public Bitmap Bmp; public bool Dark; }

        public Renderer(Cfg c)
        {
            cfg = c;
            layers = c.PathOf("layers", "layers");
            string lp = Path.Combine(layers, "layout.json");
            if (!File.Exists(lp)) throw new FileNotFoundException("no layout.json in " + layers + ": run make-layers.py first");
            var lay = Json.Obj(Json.Parse(File.ReadAllText(lp)));
            var size = lay["size"] as object[];
            LW = (int)Json.Num(size[0], 1920);
            LH = (int)Json.Num(size[1], 1080);
            var cb = Json.Obj(lay["clock"]);
            clockBox = new Rectangle((int)Json.Num(cb["x"], 0), (int)Json.Num(cb["y"], 0), (int)Json.Num(cb["w"], 0), (int)Json.Num(cb["h"], 0));
            string inkHex = c.Str("clock.ink", "");
            if (inkHex.Length == 0) inkHex = Convert.ToString(lay["ink"]);
            ink = ColorTranslator.FromHtml(inkHex);
        }

        /// <summary>The text behind the subject: the time, or the configured text, or (with no clock) the date.</summary>
        public string BigText(DateTime now, bool clock)
        {
            string text = cfg.Str("clock.text", "");
            if (text.Length > 0) return text;
            return clock ? now.ToString(cfg.Str("clock.format", "HH:mm"), CultureInfo.CurrentCulture)
                         : now.ToString(cfg.Str("clock.noClockFormat", "d MMMM"), CultureInfo.CurrentCulture);
        }

        static Bitmap LoadImage(string path)
        {
            // Read through a stream so the file is never locked between frames.
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var img = Image.FromStream(fs, false, false))
                return new Bitmap(img);
        }

        public Bitmap Compose(DateTime now, string big, bool live, List<DockApp> dockPreview)
        {
            int W = Native.GetSystemMetrics(0), H = Native.GetSystemMetrics(1);
            if (W != cacheW || H != cacheH) { foreach (Glass g0 in glassCache.Values) g0.Bmp.Dispose(); glassCache.Clear(); cacheW = W; cacheH = H; }
            // Cover the screen with the layers (they are made at screen size, so this is normally 1:1).
            float k = Math.Max(W / (float)LW, H / (float)LH);
            float ox = (W - LW * k) / 2, oy = (H - LH * k) / 2;
            var dst = new RectangleF(ox, oy, LW * k, LH * k);
            float s = Native.UiScaleOfPrimary() * (float)cfg.Num("widgets.scale", 1.0);

            var frame = new Bitmap(W, H, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(frame))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                using (Bitmap plate = LoadImage(Path.Combine(layers, "plate.png"))) g.DrawImage(plate, dst);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAlias;
                if (!string.IsNullOrEmpty(big))
                {
                    var box = new RectangleF(ox + clockBox.X * k, oy + clockBox.Y * k, clockBox.Width * k, clockBox.Height * k);
                    DrawBig(g, big, box, now);
                }
                using (Bitmap fg = LoadImage(Path.Combine(layers, "fg.png"))) g.DrawImage(fg, dst);
                Widgets(g, now, live, s, W, H, dst);
                if (dockPreview != null && dockPreview.Count > 0) DockPreview(g, dockPreview, s, W, H, dst);
            }
            return frame;
        }

        // ---- the big text ----

        void DrawBig(Graphics g, string text, RectangleF box, DateTime now)
        {
            FontFamily fam;
            string file = cfg.PathOf("clock.fontFile", "");
            string weight = cfg.Str("clock.weight", "Semibold");
            fam = !string.IsNullOrEmpty(file) && File.Exists(file) ? Fonts.FromFile(file) : Fonts.Get(cfg.Str("clock.font", "Segoe UI Variable Display"), weight);
            int style = (int)Fonts.Style(weight);
            var tf = StringFormat.GenericTypographic;

            // Size from a reference with every digit as 0: Segoe's figures are tabular, so the size never jumps between minutes.
            string fmt = cfg.Str("clock.format", "HH:mm");
            string reference = Zeros(text);
            if (cfg.Str("clock.text", "").Length == 0)
            {
                string a = Zeros(new DateTime(2000, 1, 1, 22, 58, 0).ToString(fmt, CultureInfo.CurrentCulture));
                string b = Zeros(new DateTime(2000, 1, 1, 10, 58, 0).ToString(fmt, CultureInfo.CurrentCulture));
                reference = a.Length >= b.Length ? a : b;
                if (reference.Length < Zeros(text).Length) reference = Zeros(text);
            }
            RectangleF rb;
            using (var p = new GraphicsPath()) { p.AddString(reference, fam, style, 100f, PointF.Empty, tf); rb = p.GetBounds(); }
            if (rb.Width <= 0 || rb.Height <= 0) return;
            float em = 100f * Math.Min(box.Width / rb.Width, box.Height / rb.Height);
            float kk = em / 100f;
            float top = box.Y + (box.Height - rb.Height * kk) / 2;   // where the digits' tops sit

            int alpha = (int)(255 * Math.Max(0.2, Math.Min(1, cfg.Num("clock.opacity", 0.92))));
            using (var p = new GraphicsPath())
            using (var br = new SolidBrush(Color.FromArgb(alpha, ink)))
            {
                p.AddString(text, fam, style, em, PointF.Empty, tf);
                RectangleF b = p.GetBounds();
                using (var m = new Matrix())
                {
                    m.Translate(box.X + box.Width / 2 - (b.X + b.Width / 2), top - rb.Y * kk);
                    p.Transform(m);
                }
                g.FillPath(br, p);
            }

            // The date sits above the time and is never covered, as on a lock screen.
            if (cfg.Bool("date.show", true) && cfg.Str("clock.text", "").Length == 0)
            {
                string d = now.ToString(cfg.Str("date.format", "dddd d MMMM"), CultureInfo.CurrentCulture);
                FontFamily df = Fonts.Get("Segoe UI Variable Display", "Semibold");
                float px = em * 0.115f;
                Typo.TextBase(g, d, df, FontStyle.Regular, px, Color.FromArgb((int)(alpha * 0.92), ink), box.X + box.Width / 2, top - em * 0.075f, StringAlignment.Center);
            }
        }

        static string Zeros(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s) sb.Append(char.IsDigit(c) ? '0' : c);
            return sb.ToString();
        }

        // ---- glass ----

        /// <summary>A glass panel: soft shadow, the pre-blurred wallpaper cut to the panel, a tint and a hairline.
        /// Returns true when the glass is dark (light text goes on it).</summary>
        bool Panel(Graphics g, RectangleF r, float s, RectangleF dst, float radius)
        {
            for (int i = 8; i >= 1; i--)
            {
                float e = i * 1.7f * s;
                using (GraphicsPath sp = Typo.Round(new RectangleF(r.X - e * 0.5f, r.Y - e * 0.5f + 4 * s, r.Width + e, r.Height + e), radius + e * 0.5f))
                using (var b = new SolidBrush(Color.FromArgb(7, 0, 0, 0))) g.FillPath(b, sp);
            }
            Glass gl = GlassFor(r, dst);
            using (GraphicsPath p = Typo.Round(r, radius))
            {
                using (var tb = new TextureBrush(gl.Bmp, WrapMode.Clamp))
                {
                    tb.TranslateTransform((float)Math.Floor(r.X), (float)Math.Floor(r.Y));
                    g.FillPath(tb, p);
                }
                using (var b = new SolidBrush(gl.Dark ? Color.FromArgb(46, 10, 14, 20) : Color.FromArgb(84, 255, 255, 255))) g.FillPath(b, p);
                if (gl.Dark) using (var b = new SolidBrush(Color.FromArgb(14, 255, 255, 255))) g.FillPath(b, p);
            }
            using (GraphicsPath p = Typo.Round(new RectangleF(r.X + 0.5f * s, r.Y + 0.5f * s, r.Width - s, r.Height - s), radius - 0.5f * s))
            using (var lg = new LinearGradientBrush(new RectangleF(r.X, r.Y - 1, r.Width, r.Height + 2),
                       Color.FromArgb(gl.Dark ? 70 : 150, 255, 255, 255), Color.FromArgb(gl.Dark ? 18 : 60, 255, 255, 255), 90f))
            using (var pen = new Pen(lg, Math.Max(1f, s)))
                g.DrawPath(pen, p);
            return gl.Dark;
        }

        Glass GlassFor(RectangleF r, RectangleF dst)
        {
            string key = r.ToString();
            Glass gl;
            if (glassCache.TryGetValue(key, out gl)) return gl;
            int w = (int)Math.Ceiling(r.Right) - (int)Math.Floor(r.X) + 1, h = (int)Math.Ceiling(r.Bottom) - (int)Math.Floor(r.Y) + 1;
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (Bitmap glass = LoadImage(Path.Combine(layers, "glass.png")))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                float k = dst.Width / glass.Width;
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.DrawImage(glass, new RectangleF(dst.X - (float)Math.Floor(r.X), dst.Y - (float)Math.Floor(r.Y), dst.Width, dst.Height));
            }
            double lum = 0; int n = 0;
            for (int y = 0; y < h; y += 4)
                for (int x = 0; x < w; x += 4)
                {
                    Color c = bmp.GetPixel(x, y);
                    lum += (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0; n++;
                }
            gl = new Glass { Bmp = bmp, Dark = lum / Math.Max(1, n) < 0.6 };
            glassCache[key] = gl;
            return gl;
        }

        // ---- widgets ----

        void Widgets(Graphics g, DateTime now, bool live, float s, int W, int H, RectangleF dst)
        {
            bool right = !"left".Equals(cfg.Str("widgets.side", "right"), StringComparison.OrdinalIgnoreCase);
            float m = 28 * s, gap = 16 * s, sm = 158 * s;
            float y = m;
            if (cfg.Bool("widgets.calendar", true))
            {
                float cw = 338 * s, ch = 158 * s;
                var r = new RectangleF(right ? W - m - cw : m, y, cw, ch);
                Calendar(g, r, now, s, dst);
                y += ch + gap;
            }
            var smalls = new List<int>();
            Native.SYSTEM_POWER_STATUS ps;
            bool battery = live && cfg.Bool("widgets.battery", true) && Native.GetSystemPowerStatus(out ps) && (ps.BatteryFlag & 128) == 0 && ps.BatteryLifePercent <= 100;
            Weather wx = Weather;
            bool weather = live && cfg.Bool("weather.enabled", false) && wx != null && (now - wx.Read).TotalHours < 3;
            if (battery) smalls.Add(0);
            if (weather) smalls.Add(1);
            float total = smalls.Count * sm + Math.Max(0, smalls.Count - 1) * gap;
            float x = right ? W - m - total : m;
            foreach (int kind in smalls)
            {
                var r = new RectangleF(x, y, sm, sm);
                if (kind == 0) Battery(g, r, s, dst); else WeatherPanel(g, r, s, dst, wx);
                x += sm + gap;
            }
        }

        struct Pal
        {
            public Color Primary, Secondary, Tertiary, Red, Green;
            public static Pal For(bool dark)
            {
                var p = new Pal();
                Color b = dark ? Color.White : Color.FromArgb(29, 29, 31);
                p.Primary = b;
                p.Secondary = Color.FromArgb(dark ? 170 : 160, b);
                p.Tertiary = Color.FromArgb(dark ? 115 : 110, b);
                p.Red = dark ? Color.FromArgb(255, 105, 97) : Color.FromArgb(215, 0, 21);
                p.Green = dark ? Color.FromArgb(48, 209, 88) : Color.FromArgb(36, 138, 61);
                return p;
            }
        }

        static FontFamily Display(string w) { return Fonts.Get("Segoe UI Variable Display", w); }
        static FontFamily TextF(string w) { return Fonts.Get("Segoe UI Variable Text", w); }
        static FontFamily Small(string w) { return Fonts.Get("Segoe UI Variable Small", w); }

        void Calendar(Graphics g, RectangleF r, DateTime now, float s, RectangleF dst)
        {
            Pal p = Pal.For(Panel(g, r, s, dst, 22 * s));
            CultureInfo ci = CultureInfo.CurrentCulture;
            float pad = 16 * s;
            float lx = r.X + pad + 2 * s;
            Typo.TextTop(g, now.ToString("dddd", ci).ToUpper(ci), Small("Semibold"), 11 * s, p.Red, lx, r.Y + pad + 2 * s, StringAlignment.Near);
            Typo.TextTop(g, now.Day.ToString(ci), Display("Semilight"), 54 * s, p.Primary, lx - 2 * s, r.Y + pad + 24 * s, StringAlignment.Near);
            Typo.TextBase(g, now.ToString("MMMM", ci), TextF("Semibold"), 13 * s, p.Primary, lx, r.Bottom - pad - 19 * s, StringAlignment.Near);
            Typo.TextBase(g, "Week " + IsoWeek(now).ToString(ci), TextF("Regular"), 12 * s, p.Secondary, lx, r.Bottom - pad - 2 * s, StringAlignment.Near);

            // the month
            bool monday = cfg.Bool("widgets.weekStartsMonday", true);
            float gx0 = r.X + 136 * s, gx1 = r.Right - pad + 2 * s;
            float cw = (gx1 - gx0) / 7;
            string[] names = ci.DateTimeFormat.ShortestDayNames;
            for (int i = 0; i < 7; i++)
            {
                int dow = monday ? (i + 1) % 7 : i;
                string n = names[dow];
                n = n.Substring(0, 1).ToUpper(ci);
                bool weekend = dow == 0 || dow == 6;
                Typo.TextTop(g, n, Small("Semibold"), 9.5f * s, weekend ? p.Tertiary : p.Secondary, gx0 + cw * (i + 0.5f), r.Y + pad + 2 * s, StringAlignment.Center);
            }
            var first = new DateTime(now.Year, now.Month, 1);
            int lead = ((int)first.DayOfWeek - (monday ? 1 : 0) + 7) % 7;
            int days = DateTime.DaysInMonth(now.Year, now.Month);
            int rows = (lead + days + 6) / 7;
            float top = r.Y + pad + 17 * s, bottom = r.Bottom - pad + 4 * s;
            float rh = (bottom - top) / rows;
            float numPx = 11 * s;
            for (int d = 1; d <= days; d++)
            {
                int cell = lead + d - 1, col = cell % 7, row = cell / 7;
                float cx = gx0 + cw * (col + 0.5f), cy = top + rh * (row + 0.5f);
                int dow = monday ? (col + 1) % 7 : col;
                bool weekend = dow == 0 || dow == 6;
                Color c = weekend ? p.Secondary : p.Primary;
                if (d == now.Day)
                {
                    float dia = Math.Min(rh, cw) * 0.98f;
                    using (var b = new SolidBrush(p.Red)) g.FillEllipse(b, cx - dia / 2, cy - dia / 2, dia, dia);
                    c = Color.White;
                }
                Typo.TextMid(g, d.ToString(ci), TextF(d == now.Day ? "Semibold" : "Regular"), numPx, c, cx, cy, StringAlignment.Center);
            }
        }

        static int IsoWeek(DateTime d)
        {
            int day = (int)d.DayOfWeek; if (day == 0) day = 7;
            return (d.AddDays(4 - day).DayOfYear - 1) / 7 + 1;
        }

        void Battery(Graphics g, RectangleF r, float s, RectangleF dst)
        {
            Native.SYSTEM_POWER_STATUS ps;
            Native.GetSystemPowerStatus(out ps);
            Pal p = Pal.For(Panel(g, r, s, dst, 22 * s));
            float pad = 16 * s;
            int pct = ps.BatteryLifePercent;
            bool charging = (ps.BatteryFlag & 8) != 0, ac = ps.ACLineStatus == 1;
            Typo.TextTop(g, "BATTERY", Small("Semibold"), 11 * s, p.Secondary, r.X + pad + 2 * s, r.Y + pad + 2 * s, StringAlignment.Near);
            float cx = r.X + r.Width / 2, cy = r.Y + 78 * s, rad = 31 * s, stroke = 6.5f * s;
            using (var track = new Pen(Color.FromArgb(46, p.Primary), stroke)) g.DrawEllipse(track, cx - rad, cy - rad, rad * 2, rad * 2);
            Color arc = charging ? p.Green : (pct <= 20 && !ac ? p.Red : p.Primary);
            using (var pen = new Pen(arc, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                if (pct > 0) g.DrawArc(pen, cx - rad, cy - rad, rad * 2, rad * 2, -90, 360f * pct / 100f);
            Typo.TextMid(g, pct.ToString(CultureInfo.CurrentCulture) + "%", Display("Semibold"), 17 * s, p.Primary, cx, cy, StringAlignment.Center);
            string status;
            if (charging) status = "Charging";
            else if (ac) status = "Plugged in";
            else if (ps.BatteryLifeTime > 0 && ps.BatteryLifeTime < 24 * 3600)
            {
                int mins = ps.BatteryLifeTime / 60;
                status = mins >= 60 ? (mins / 60) + " h " + (mins % 60) + " min left" : mins + " min left";
            }
            else status = "On battery";
            Typo.TextBase(g, status, TextF("Semibold"), 12 * s, p.Secondary, cx, r.Bottom - pad - 1 * s, StringAlignment.Center);
        }

        void WeatherPanel(Graphics g, RectangleF r, float s, RectangleF dst, Weather w)
        {
            Pal p = Pal.For(Panel(g, r, s, dst, 22 * s));
            float pad = 16 * s, lx = r.X + pad + 2 * s;
            CultureInfo ci = CultureInfo.CurrentCulture;
            string place = cfg.Str("weather.place", "");
            Typo.TextTop(g, place.Length > 0 ? place : "Weather", TextF("Semibold"), 13 * s, p.Primary, lx, r.Y + pad + 1 * s, StringAlignment.Near);
            Typo.TextTop(g, Math.Round(w.Temp).ToString(ci) + "°", Display("Light"), 40 * s, p.Primary, lx - 1 * s, r.Y + pad + 22 * s, StringAlignment.Near);
            Typo.TextBase(g, w.Condition, TextF("Semibold"), 12 * s, p.Primary, lx, r.Bottom - pad - 32 * s, StringAlignment.Near);
            Typo.TextBase(g, "H " + Math.Round(w.Max).ToString(ci) + "°  L " + Math.Round(w.Min).ToString(ci) + "°", TextF("Regular"), 12 * s, p.Secondary, lx, r.Bottom - pad - 16 * s, StringAlignment.Near);
            Typo.TextBase(g, "Updated " + w.Read.ToString("HH:mm", ci), Small("Regular"), 10 * s, p.Tertiary, lx, r.Bottom - pad - 0 * s, StringAlignment.Near);
        }

        // ---- dock preview (for --render only; the live dock is a real window, see Dock) ----

        void DockPreview(Graphics g, List<DockApp> apps, float s, int W, int H, RectangleF dst)
        {
            s = Native.UiScaleOfPrimary() * (float)cfg.Num("dock.scale", 1.0);
            Rectangle work = Native.WorkAreaOfPrimary();
            var d = Dock.Measure(apps.Count, s, work);
            bool dark = Panel(g, d, s, dst, 8 * s);
            using (GraphicsPath p = Typo.Round(d, 8 * s))
            using (var b = new SolidBrush(dark ? Color.FromArgb(40, 32, 32, 32) : Color.FromArgb(30, 255, 255, 255))) g.FillPath(b, p);
            int px = (int)Math.Round(Dock.IconDip * s);
            for (int i = 0; i < apps.Count; i++)
            {
                Bitmap ic = apps[i].IconAt(px);
                if (ic == null) continue;
                RectangleF ir = Dock.IconRect(i, s);
                g.DrawImage(ic, d.X + ir.X, d.Y + ir.Y, ir.Width, ir.Height);
            }
        }
    }

    static class Typo
    {
        public static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = Math.Max(0.1f, Math.Min(rad * 2, Math.Min(r.Width, r.Height)));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        static float Ascent(FontFamily f, FontStyle st, float px) { return px * f.GetCellAscent(st) / f.GetEmHeight(st); }
        const float Cap = 0.70f; // Segoe's cap and figure height, as a share of the em

        /// <summary>Draw with the baseline at y.</summary>
        public static void TextBase(Graphics g, string s, FontFamily f, FontStyle st, float px, Color c, float x, float y, StringAlignment a)
        {
            using (var font = new Font(f, px, st, GraphicsUnit.Pixel))
            using (var b = new SolidBrush(c))
            using (var sf = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                sf.Alignment = a;
                sf.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces;
                g.DrawString(s, font, b, new PointF(x, y - Ascent(f, st, px)), sf);
            }
        }
        public static void TextBase(Graphics g, string s, FontFamily f, float px, Color c, float x, float y, StringAlignment a) { TextBase(g, s, f, FontStyle.Regular, px, c, x, y, a); }
        /// <summary>Draw with the tops of the capitals at y.</summary>
        public static void TextTop(Graphics g, string s, FontFamily f, float px, Color c, float x, float y, StringAlignment a) { TextBase(g, s, f, FontStyle.Regular, px, c, x, y + px * Cap, a); }
        /// <summary>Draw with the capitals centred on y.</summary>
        public static void TextMid(Graphics g, string s, FontFamily f, float px, Color c, float x, float y, StringAlignment a) { TextBase(g, s, f, FontStyle.Regular, px, c, x, y + px * Cap / 2, a); }
    }

    // ---------- weather (opt-in, keyless: Open-Meteo) ----------

    class Weather
    {
        public double Temp, Max, Min;
        public string Condition;
        public DateTime Read;

        public static Weather Fetch(Cfg cfg)
        {
            try
            {
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2
                CultureInfo inv = CultureInfo.InvariantCulture;
                bool f = "fahrenheit".Equals(cfg.Str("weather.units", "celsius"), StringComparison.OrdinalIgnoreCase);
                string url = "https://api.open-meteo.com/v1/forecast?latitude=" + cfg.Num("weather.latitude", 0).ToString("0.####", inv)
                    + "&longitude=" + cfg.Num("weather.longitude", 0).ToString("0.####", inv)
                    + "&current=temperature_2m,weather_code&daily=temperature_2m_max,temperature_2m_min&timezone=auto&forecast_days=1"
                    + (f ? "&temperature_unit=fahrenheit" : "");
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Timeout = 10000;
                req.UserAgent = "designyourwindows/1.0";
                string body;
                using (var resp = req.GetResponse())
                using (var rd = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) body = rd.ReadToEnd();
                var o = Json.Obj(Json.Parse(body));
                var cur = Json.Obj(o["current"]);
                var daily = Json.Obj(o["daily"]);
                var w = new Weather();
                w.Temp = Json.Num(cur["temperature_2m"], 0);
                w.Condition = Describe((int)Json.Num(cur["weather_code"], -1));
                w.Max = Json.Num(((object[])daily["temperature_2m_max"])[0], 0);
                w.Min = Json.Num(((object[])daily["temperature_2m_min"])[0], 0);
                w.Read = DateTime.Now;
                Program.Log("weather read");
                return w;
            }
            catch (Exception e) { Program.Log("weather: " + e.Message); return null; }
        }

        static string Describe(int c)
        {
            switch (c)
            {
                case 0: return "Clear";
                case 1: return "Mainly clear";
                case 2: return "Partly cloudy";
                case 3: return "Overcast";
                case 45: case 48: return "Fog";
                case 51: case 53: case 55: return "Drizzle";
                case 56: case 57: return "Freezing drizzle";
                case 61: return "Light rain";
                case 63: return "Rain";
                case 65: return "Heavy rain";
                case 66: case 67: return "Freezing rain";
                case 71: case 73: case 75: case 77: return "Snow";
                case 80: case 81: case 82: return "Showers";
                case 85: case 86: return "Snow showers";
                case 95: return "Thunderstorm";
                case 96: case 99: return "Thunder and hail";
                default: return "";
            }
        }
    }

    // ---------- the wallpaper ----------

    static class Wallpaper
    {
        const uint SPI_SETDESKWALLPAPER = 0x14, SPIF_UPDATEINIFILE = 1, SPIF_SENDCHANGE = 2;
        const string DesktopKey = @"Control Panel\Desktop";

        public static string FramePath { get { return Path.Combine(Program.StateDir, "wallpaper.bmp"); } }

        public static void Save(Bitmap frame)
        {
            string tmp = FramePath + ".tmp";
            frame.Save(tmp, ImageFormat.Bmp);
            File.Copy(tmp, FramePath, true);
            File.Delete(tmp);
        }

        /// <summary>Write the frame and hand it to Windows. The first call of a run persists it and tells other apps;
        /// the per-minute calls after it only repaint (no registry write, no WM_SETTINGCHANGE broadcast to every window).</summary>
        public static void Apply(Cfg cfg, Bitmap frame, bool first)
        {
            if (first) Backup();
            Save(frame);
            uint flags = first ? (SPIF_UPDATEINIFILE | SPIF_SENDCHANGE) : (uint)cfg.Num("advanced.minuteFlags", 0);
            if (first) using (RegistryKey k = Registry.CurrentUser.OpenSubKey(DesktopKey, true))
                {
                    k.SetValue("WallpaperStyle", "10"); // fill: the frame is already screen-sized
                    k.SetValue("TileWallpaper", "0");
                }
            if (!Native.SystemParametersInfoW(SPI_SETDESKWALLPAPER, 0, FramePath, flags))
                Program.Log("SystemParametersInfo failed: " + Marshal.GetLastWin32Error());
        }

        static string BackupJson { get { return Path.Combine(Program.StateDir, "previous.json"); } }

        /// <summary>Remember the wallpaper we are replacing, once: its path and style, plus a copy of what Windows was
        /// showing (TranscodedWallpaper) in case the original file has gone by the time you uninstall.</summary>
        static void Backup()
        {
            if (File.Exists(BackupJson)) return;
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(DesktopKey))
            {
                string wp = Convert.ToString(k.GetValue("WallPaper", ""));
                if (wp.Equals(FramePath, StringComparison.OrdinalIgnoreCase)) return;
                var d = new Dictionary<string, object>();
                d["WallPaper"] = wp;
                d["WallpaperStyle"] = Convert.ToString(k.GetValue("WallpaperStyle", "10"));
                d["TileWallpaper"] = Convert.ToString(k.GetValue("TileWallpaper", "0"));
                string tw = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Themes\TranscodedWallpaper");
                if (File.Exists(tw))
                {
                    string copy = Path.Combine(Program.StateDir, "previous-wallpaper.jpg");
                    File.Copy(tw, copy, true);
                    d["Copy"] = copy;
                }
                File.WriteAllText(BackupJson, Json.Write(d));
                Program.Log("saved the previous wallpaper: " + wp);
            }
        }

        public static bool Restore()
        {
            if (!File.Exists(BackupJson)) return false;
            var d = Json.Obj(Json.Parse(File.ReadAllText(BackupJson)));
            string wp = Convert.ToString(d["WallPaper"]);
            object copyObj;
            string copy = d.TryGetValue("Copy", out copyObj) ? Convert.ToString(copyObj) : "";
            string path = wp;
            if ((string.IsNullOrEmpty(wp) || !File.Exists(wp)) && copy.Length > 0 && File.Exists(copy)) path = copy;
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(DesktopKey, true))
            {
                k.SetValue("WallpaperStyle", Convert.ToString(d["WallpaperStyle"]));
                k.SetValue("TileWallpaper", Convert.ToString(d["TileWallpaper"]));
            }
            Native.SystemParametersInfoW(SPI_SETDESKWALLPAPER, 0, path ?? "", SPIF_UPDATEINIFILE | SPIF_SENDCHANGE);
            File.Delete(BackupJson);
            if (!path.Equals(copy, StringComparison.OrdinalIgnoreCase))
                try { File.Delete(Path.Combine(Program.StateDir, "previous-wallpaper.jpg")); } catch (IOException) { }
            Program.Log("restored " + path);
            return true;
        }
    }

    // ---------- install / uninstall (a per-user scheduled task; no admin rights) ----------

    static class Install
    {
        const string Task = "designyourwindows";

        public static int Add(Cfg cfg, bool noClock, bool noDock)
        {
            string exe = Process.GetCurrentProcess().MainModule.FileName;
            string args = "--config \"" + cfg.FilePath + "\"" + (noClock ? " --no-clock" : "") + (noDock ? " --no-dock" : "");
            string user = Environment.UserDomainName + "\\" + Environment.UserName;
            var x = new StringBuilder();
            x.Append("<?xml version=\"1.0\" encoding=\"UTF-16\"?>\n<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\n");
            x.Append(" <RegistrationInfo><Description>designyourwindows wallpaper</Description></RegistrationInfo>\n <Triggers>\n");
            x.Append("  <LogonTrigger><Enabled>true</Enabled><UserId>" + Esc(user) + "</UserId></LogonTrigger>\n");
            if (noClock) // the date and calendar change at midnight: re-render then, and on waking if the PC slept through it
                x.Append("  <CalendarTrigger><StartBoundary>2026-01-01T00:00:30</StartBoundary><Enabled>true</Enabled><ScheduleByDay><DaysInterval>1</DaysInterval></ScheduleByDay></CalendarTrigger>\n");
            x.Append(" </Triggers>\n <Principals><Principal id=\"Author\"><UserId>" + Esc(user) + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>\n");
            x.Append(" <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>"
                   + "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><StartWhenAvailable>true</StartWhenAvailable><ExecutionTimeLimit>PT0S</ExecutionTimeLimit>"
                   + "<Priority>7</Priority><IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd></IdleSettings></Settings>\n");
            x.Append(" <Actions Context=\"Author\"><Exec><Command>" + Esc(exe) + "</Command><Arguments>" + Esc(args) + "</Arguments><WorkingDirectory>"
                   + Esc(Path.GetDirectoryName(exe)) + "</WorkingDirectory></Exec></Actions>\n</Task>\n");
            string xml = Path.Combine(Path.GetTempPath(), "designyourwindows-task.xml");
            File.WriteAllText(xml, x.ToString(), Encoding.Unicode);
            int rc = Schtasks("/Create /TN " + Task + " /XML \"" + xml + "\" /F");
            File.Delete(xml);
            if (rc != 0) { Program.Say("could not create the sign-in task (schtasks exit " + rc + ")"); return rc; }
            Schtasks("/Run /TN " + Task);
            Program.Say("installed: starts at sign-in" + (noClock ? " and at midnight" : "") + ", and is running now");
            return 0;
        }

        public static int Remove(Cfg cfg)
        {
            EventWaitHandle e;
            if (EventWaitHandle.TryOpenExisting(@"Local\designyourwindows.stop", out e)) using (e) e.Set();
            for (int i = 0; i < 30; i++)
            {
                Mutex m;
                if (!Mutex.TryOpenExisting(@"Local\designyourwindows.run", out m)) break;
                m.Dispose();
                Thread.Sleep(100);
            }
            int rc = Schtasks("/Query /TN " + Task);
            if (rc == 0) Schtasks("/Delete /TN " + Task + " /F");
            bool restored = Wallpaper.Restore();
            try { File.Delete(Wallpaper.FramePath); } catch (IOException) { }
            Program.Say((rc == 0 ? "removed the sign-in task; " : "no sign-in task; ") + (restored ? "previous wallpaper restored" : "no saved wallpaper to restore"));
            return 0;
        }

        static string Esc(string s) { return System.Security.SecurityElement.Escape(s); }

        static int Schtasks(string args)
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            using (Process p = Process.Start(psi))
            {
                string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (Program.Verbose) Program.Log("schtasks " + args + ": " + o.Trim());
                return p.ExitCode;
            }
        }
    }

    // ---------- the dock ----------

    class DockApp
    {
        public string Name, Path, AppId, Args;
        Bitmap icon; int iconPx;

        public static List<DockApp> Load(Cfg cfg)
        {
            var list = new List<DockApp>();
            string f = cfg.PathOf("dock.file", "dock.json");
            if (!File.Exists(f)) return list;
            var o = Json.Obj(Json.Parse(File.ReadAllText(f)));
            object appsObj;
            if (!o.TryGetValue("apps", out appsObj)) return list;
            foreach (object a in (object[])appsObj)
            {
                var d = Json.Obj(a);
                var x = new DockApp();
                object v;
                if (d.TryGetValue("name", out v)) x.Name = Convert.ToString(v);
                if (d.TryGetValue("path", out v)) x.Path = Environment.ExpandEnvironmentVariables(Convert.ToString(v));
                if (d.TryGetValue("appId", out v)) x.AppId = Convert.ToString(v);
                if (d.TryGetValue("args", out v)) x.Args = Convert.ToString(v);
                if (x.Path != null || x.AppId != null) list.Add(x);
            }
            return list;
        }

        string Parsing { get { return AppId != null ? @"shell:AppsFolder\" + AppId : Path; } }

        /// <summary>The app's own icon from the shell, at the size asked (cached until the size changes).</summary>
        public Bitmap IconAt(int px)
        {
            if (icon != null && iconPx == px) return icon;
            if (icon != null) icon.Dispose();
            icon = Shell.Icon(Parsing, px);
            iconPx = px;
            return icon;
        }

        public void Launch()
        {
            Native.AllowSetForegroundWindow(-1);
            try
            {
                if (AppId != null)
                {
                    var psi = new ProcessStartInfo(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), @"shell:AppsFolder\" + AppId);
                    psi.UseShellExecute = false;
                    Process.Start(psi).Dispose();
                }
                else
                {
                    var psi = new ProcessStartInfo(Path, Args ?? "");
                    psi.UseShellExecute = true;
                    string dir = System.IO.Path.GetDirectoryName(Path);
                    if (!string.IsNullOrEmpty(dir)) psi.WorkingDirectory = dir;
                    using (Process p = Process.Start(psi)) { }
                }
            }
            catch (Exception e) { Program.Log("launch " + Name + ": " + e.Message); }
        }
    }

    class Dock
    {
        internal const float IconDip = 48, PadDip = 9, GapDip = 10, MarginDip = 10;
        readonly Cfg cfg;
        readonly List<DockApp> apps;
        readonly Action quit;
        readonly Native.WndProc proc;
        IntPtr hwnd, tip;
        float s = 1;
        int hover = -1, press = -1;
        bool fullScreen;
        const uint CallbackMsg = 0x8000 + 7;

        public Dock(Cfg c, Action onQuit)
        {
            cfg = c;
            quit = onQuit;
            apps = DockApp.Load(c);
            proc = WndProc;
        }

        public static RectangleF Measure(int n, float s, Rectangle work)
        {
            float w = (n * IconDip + Math.Max(0, n - 1) * GapDip + 2 * PadDip) * s;
            float h = (IconDip + 2 * PadDip) * s;
            return new RectangleF((float)Math.Round(work.X + (work.Width - w) / 2), (float)Math.Round(work.Bottom - h - MarginDip * s), (float)Math.Round(w), (float)Math.Round(h));
        }

        public static RectangleF IconRect(int i, float s)
        {
            return new RectangleF((PadDip + i * (IconDip + GapDip)) * s, PadDip * s, IconDip * s, IconDip * s);
        }

        public void Show()
        {
            if (apps.Count == 0) { Program.Log("dock: no apps in dock.json"); return; }
            var icc = new Native.INITCOMMONCONTROLSEX { dwSize = 8, dwICC = 0x8 /*ICC_BAR_CLASSES*/ };
            Native.InitCommonControlsEx(ref icc);
            var wc = new Native.WNDCLASSEX();
            wc.cbSize = Marshal.SizeOf(typeof(Native.WNDCLASSEX));
            wc.lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc);
            wc.hInstance = Native.GetModuleHandleW(null);
            wc.hCursor = Native.LoadCursorW(IntPtr.Zero, new IntPtr(32512));
            wc.lpszClassName = "designyourwindows.dock";
            Native.RegisterClassExW(ref wc);
            const uint WS_POPUP = 0x80000000;
            const uint WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
            hwnd = Native.CreateWindowExW(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, wc.lpszClassName, "designyourwindows dock",
                                          WS_POPUP, 0, 0, 10, 10, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
            Backdrop();
            // Hidden while a full-screen app (a game, a video, a presentation) is in front: the shell tells app bars.
            var abd = new Native.APPBARDATA { cbSize = Marshal.SizeOf(typeof(Native.APPBARDATA)), hWnd = hwnd, uCallbackMessage = CallbackMsg };
            Native.SHAppBarMessage(0 /*ABM_NEW*/, ref abd);
            int state;
            if (Native.SHQueryUserNotificationState(out state) == 0 && (state == 2 || state == 3 || state == 4)) fullScreen = true;
            Layout();
        }

        void Backdrop()
        {
            int round = 2; // DWMWCP_ROUND
            Native.DwmSetWindowAttribute(hwnd, 33, ref round, 4);
            string mode = cfg.Str("dock.backdrop", "acrylic").ToLowerInvariant();
            bool dark = DarkTheme();
            if (mode == "system")
            {
                var m = new Native.MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
                Native.DwmExtendFrameIntoClientArea(hwnd, ref m);
                int on = dark ? 1 : 0;
                Native.DwmSetWindowAttribute(hwnd, 20, ref on, 4);
                int type = 3; // DWMSBT_TRANSIENTWINDOW: acrylic
                Native.DwmSetWindowAttribute(hwnd, 38, ref type, 4);
                return;
            }
            // Acrylic through the accent policy: it stays frosted when the window is not active, which a never-activated
            // dock always is. Colour is AABBGGRR.
            var accent = new Native.AccentPolicy();
            accent.AccentState = mode == "blur" ? 3 : 4;
            accent.GradientColor = dark ? 0x58201C1Cu : 0x40F2F2F2u;
            int size = Marshal.SizeOf(accent);
            IntPtr p = Marshal.AllocHGlobal(size);
            Marshal.StructureToPtr(accent, p, false);
            var data = new Native.WindowCompositionAttributeData { Attribute = 19, Data = p, SizeOfData = size };
            Native.SetWindowCompositionAttribute(hwnd, ref data);
            Marshal.FreeHGlobal(p);
        }

        static bool DarkTheme()
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                return k == null || Convert.ToInt32(k.GetValue("AppsUseLightTheme", 0)) == 0;
        }

        void Layout()
        {
            s = Native.UiScaleOf(hwnd) * (float)cfg.Num("dock.scale", 1.0);
            Rectangle work = Native.WorkAreaOf(hwnd);
            RectangleF r = Measure(apps.Count, s, work);
            const uint SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40, SWP_HIDEWINDOW = 0x80;
            // SetWindowPos rather than ShowWindow: the first ShowWindow of a process obeys how it was launched.
            Native.SetWindowPos(hwnd, new IntPtr(-1) /*HWND_TOPMOST*/, (int)r.X, (int)r.Y, (int)r.Width, (int)r.Height,
                                SWP_NOACTIVATE | (fullScreen ? SWP_HIDEWINDOW : SWP_SHOWWINDOW));
            Tooltips();
            Native.InvalidateRect(hwnd, IntPtr.Zero, false);
        }

        void Tooltips()
        {
            if (tip != IntPtr.Zero) Native.DestroyWindow(tip);
            tip = Native.CreateWindowExW(0x8 /*TOPMOST*/, "tooltips_class32", null, 0x80000000 | 0x1 | 0x2 /*POPUP|ALWAYSTIP|NOPREFIX*/,
                                         0, 0, 0, 0, hwnd, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            for (int i = 0; i < apps.Count; i++)
            {
                RectangleF ir = IconRect(i, s);
                var ti = new Native.TOOLINFO();
                ti.cbSize = Marshal.SizeOf(typeof(Native.TOOLINFO));
                ti.uFlags = 0x10; // TTF_SUBCLASS
                ti.hwnd = hwnd;
                ti.uId = new IntPtr(i + 1);
                ti.rect = new Native.RECT { left = (int)ir.Left, top = (int)ir.Top, right = (int)ir.Right, bottom = (int)ir.Bottom };
                ti.lpszText = apps[i].Name ?? "";
                Native.SendMessageW(tip, 0x0432 /*TTM_ADDTOOLW*/, IntPtr.Zero, ref ti);
            }
        }

        int HitTest(int x, int y)
        {
            for (int i = 0; i < apps.Count; i++)
            {
                RectangleF r = IconRect(i, s);
                r.Inflate(GapDip * s / 2, PadDip * s);
                if (r.Contains(x, y)) return i;
            }
            return -1;
        }

        IntPtr WndProc(IntPtr h, uint msg, IntPtr w, IntPtr l)
        {
            switch (msg)
            {
                case 0x0021: return new IntPtr(3); // WM_MOUSEACTIVATE: MA_NOACTIVATE
                case 0x000F: Paint(h); return IntPtr.Zero; // WM_PAINT
                case 0x0014: return new IntPtr(1);       // WM_ERASEBKGND: painted in WM_PAINT
                case 0x0200: // WM_MOUSEMOVE
                    {
                        int i = HitTest((short)(l.ToInt64() & 0xFFFF), (short)((l.ToInt64() >> 16) & 0xFFFF));
                        if (i != hover)
                        {
                            hover = i;
                            var tme = new Native.TRACKMOUSEEVENT { cbSize = Marshal.SizeOf(typeof(Native.TRACKMOUSEEVENT)), dwFlags = 2 /*TME_LEAVE*/, hwndTrack = h };
                            Native.TrackMouseEvent(ref tme);
                            Native.InvalidateRect(h, IntPtr.Zero, false);
                        }
                        return IntPtr.Zero;
                    }
                case 0x02A3: hover = -1; press = -1; Native.InvalidateRect(h, IntPtr.Zero, false); return IntPtr.Zero; // WM_MOUSELEAVE
                case 0x0201: press = hover; Native.InvalidateRect(h, IntPtr.Zero, false); return IntPtr.Zero; // WM_LBUTTONDOWN
                case 0x0202: // WM_LBUTTONUP
                    {
                        int i = press;
                        press = -1;
                        Native.InvalidateRect(h, IntPtr.Zero, false);
                        if (i >= 0 && i == hover) apps[i].Launch();
                        return IntPtr.Zero;
                    }
                case 0x0205: Menu(h); return IntPtr.Zero; // WM_RBUTTONUP
                case 0x02E0: // WM_DPICHANGED
                case 0x007E: // WM_DISPLAYCHANGE
                    Layout(); return IntPtr.Zero;
                case 0x001A: // WM_SETTINGCHANGE: the work area moved (taskbar resized or auto-hidden)
                    if (w.ToInt64() == 0x2F) Layout();
                    return IntPtr.Zero;
                case CallbackMsg:
                    if (w.ToInt64() == 2 /*ABN_FULLSCREENAPP*/) { fullScreen = l != IntPtr.Zero; Layout(); }
                    return IntPtr.Zero;
            }
            return Native.DefWindowProcW(h, msg, w, l);
        }

        void Menu(IntPtr h)
        {
            IntPtr m = Native.CreatePopupMenu();
            Native.AppendMenuW(m, 0, new IntPtr(1), "Edit dock.json");
            Native.AppendMenuW(m, 0x800, IntPtr.Zero, null);
            Native.AppendMenuW(m, 0, new IntPtr(2), "Quit designyourwindows");
            Native.POINT pt;
            Native.GetCursorPos(out pt);
            Native.SetForegroundWindow(h);
            int cmd = Native.TrackPopupMenu(m, 0x100 /*RETURNCMD*/ | 0x20 /*BOTTOMALIGN*/, pt.x, pt.y, 0, h, IntPtr.Zero);
            Native.DestroyMenu(m);
            if (cmd == 1)
            {
                var psi = new ProcessStartInfo(cfg.PathOf("dock.file", "dock.json")) { UseShellExecute = true, Verb = "open" };
                try { Process.Start(psi); } catch (Exception e) { Program.Log("open dock.json: " + e.Message); }
            }
            else if (cmd == 2) quit();
        }

        void Paint(IntPtr h)
        {
            Native.PAINTSTRUCT ps;
            IntPtr hdc = Native.BeginPaint(h, out ps);
            Native.RECT rc;
            Native.GetClientRect(h, out rc);
            int w = rc.right, hh = rc.bottom;
            if (w > 0 && hh > 0)
            {
                using (var bmp = new Bitmap(w, hh, PixelFormat.Format32bppPArgb))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.Clear(Color.Transparent);
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        int px = (int)Math.Round(IconDip * s);
                        for (int i = 0; i < apps.Count; i++)
                        {
                            RectangleF r = IconRect(i, s);
                            if (i == hover)
                            {
                                RectangleF t = r; t.Inflate(4 * s, 4 * s);
                                using (GraphicsPath p = Typo.Round(t, 10 * s))
                                using (var b = new SolidBrush(Color.FromArgb(i == press ? 52 : 34, 255, 255, 255))) g.FillPath(b, p);
                            }
                            Bitmap ic = apps[i].IconAt(px);
                            if (ic != null) g.DrawImage(ic, r.X, r.Y, r.Width, r.Height);
                        }
                    }
                    Native.BlitPremultiplied(hdc, bmp);
                }
            }
            Native.EndPaint(h, ref ps);
        }

        public void Close()
        {
            if (hwnd == IntPtr.Zero) return;
            var abd = new Native.APPBARDATA { cbSize = Marshal.SizeOf(typeof(Native.APPBARDATA)), hWnd = hwnd };
            Native.SHAppBarMessage(1 /*ABM_REMOVE*/, ref abd);
            if (tip != IntPtr.Zero) Native.DestroyWindow(tip);
            Native.DestroyWindow(hwnd);
            hwnd = IntPtr.Zero;
        }
    }

    // ---------- shell icons ----------

    static class Shell
    {
        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItemImageFactory { [PreserveSig] int GetImage(Native.SIZE size, int flags, out IntPtr phbm); }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
                                                       [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory item);

        /// <summary>The icon Windows itself shows for an exe or a Start-menu app, with its alpha intact.</summary>
        public static Bitmap Icon(string parsingName, int px)
        {
            IShellItemImageFactory f = null;
            IntPtr hbm = IntPtr.Zero;
            try
            {
                SHCreateItemFromParsingName(parsingName, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out f);
                if (f.GetImage(new Native.SIZE { cx = px, cy = px }, 0x4 /*SIIGBF_ICONONLY*/, out hbm) != 0) return null;
                return Native.BitmapFromHbitmap(hbm);
            }
            catch (Exception e) { Program.Log("icon " + parsingName + ": " + e.Message); return null; }
            finally
            {
                if (hbm != IntPtr.Zero) Native.DeleteObject(hbm);
                if (f != null) Marshal.ReleaseComObject(f);
            }
        }
    }

    // ---------- Win32 ----------

    static class Native
    {
        [DllImport("kernel32.dll")] public static extern bool AttachConsole(int pid);
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SystemParametersInfoW(uint action, uint param, string pv, uint winIni);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr CreateWaitableTimerW(IntPtr sa, bool manualReset, string name);
        [DllImport("kernel32.dll")] public static extern bool SetWaitableTimer(IntPtr h, ref long due, int period, IntPtr cb, IntPtr arg, bool resume);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")] public static extern bool SetProcessWorkingSetSize(IntPtr p, IntPtr min, IntPtr max);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandleW(string name);
        [DllImport("user32.dll")] public static extern uint MsgWaitForMultipleObjectsEx(uint n, IntPtr[] h, uint ms, uint wake, uint flags);
        [DllImport("user32.dll")] public static extern bool PeekMessageW(out MSG m, IntPtr h, uint min, uint max, uint remove);
        [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG m);
        [DllImport("user32.dll")] public static extern IntPtr DispatchMessageW(ref MSG m);
        [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(int pid);

        [StructLayout(LayoutKind.Sequential)]
        public struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }
        [DllImport("kernel32.dll")] public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);

        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int left, top, right, bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }
        [StructLayout(LayoutKind.Sequential)] public struct MARGINS { public int cxLeftWidth, cxRightWidth, cyTopHeight, cyBottomHeight; }

        // monitors and DPI
        [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
        [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
        [DllImport("user32.dll")] static extern bool GetMonitorInfoW(IntPtr m, ref MONITORINFO mi);
        [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr m, int type, out uint x, out uint y);

        static IntPtr Primary() { return MonitorFromPoint(new POINT(), 1 /*MONITOR_DEFAULTTOPRIMARY*/); }

        /// <summary>Pixels per design unit on a monitor: its display scaling, but never smaller than its height over 1100,
        /// so a 1440p screen left at 100% still gets widgets and a dock in proportion, as a Mac's would be.</summary>
        public static float UiScale(IntPtr mon)
        {
            float dpi = 1f;
            uint x, y;
            try { if (GetDpiForMonitor(mon, 0, out x, out y) == 0) dpi = x / 96f; } catch (DllNotFoundException) { }
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            GetMonitorInfoW(mon, ref mi);
            return Math.Max(dpi, (mi.rcMonitor.bottom - mi.rcMonitor.top) / 1100f);
        }
        public static float UiScaleOfPrimary() { return UiScale(Primary()); }
        public static float UiScaleOf(IntPtr h) { return UiScale(MonitorFromWindow(h, 1)); }
        static Rectangle Work(IntPtr mon)
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            GetMonitorInfoW(mon, ref mi);
            return Rectangle.FromLTRB(mi.rcWork.left, mi.rcWork.top, mi.rcWork.right, mi.rcWork.bottom);
        }
        public static Rectangle WorkAreaOfPrimary() { return Work(Primary()); }
        public static Rectangle WorkAreaOf(IntPtr h) { return Work(MonitorFromWindow(h, 1)); }

        // windows
        public delegate IntPtr WndProc(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WNDCLASSEX
        {
            public int cbSize; public uint style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
            public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string lpszMenuName, lpszClassName; public IntPtr hIconSm;
        }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern ushort RegisterClassExW(ref WNDCLASSEX wc);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateWindowExW(uint ex, string cls, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
        [DllImport("user32.dll")] public static extern IntPtr DefWindowProcW(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool InvalidateRect(IntPtr h, IntPtr r, bool erase);
        [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern IntPtr LoadCursorW(IntPtr inst, IntPtr id);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr CreatePopupMenu();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool AppendMenuW(IntPtr m, uint flags, IntPtr id, string text);
        [DllImport("user32.dll")] public static extern int TrackPopupMenu(IntPtr m, uint flags, int x, int y, int r, IntPtr h, IntPtr rect);
        [DllImport("user32.dll")] public static extern bool DestroyMenu(IntPtr m);

        [StructLayout(LayoutKind.Sequential)] public struct TRACKMOUSEEVENT { public int cbSize; public uint dwFlags; public IntPtr hwndTrack; public uint dwHoverTime; }
        [DllImport("user32.dll")] public static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT t);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct TOOLINFO { public int cbSize; public uint uFlags; public IntPtr hwnd, uId; public RECT rect; public IntPtr hinst; public string lpszText; public IntPtr lParam, lpReserved; }
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr SendMessageW(IntPtr h, uint msg, IntPtr w, ref TOOLINFO l);
        [StructLayout(LayoutKind.Sequential)] public struct INITCOMMONCONTROLSEX { public int dwSize; public uint dwICC; }
        [DllImport("comctl32.dll")] public static extern bool InitCommonControlsEx(ref INITCOMMONCONTROLSEX i);

        [StructLayout(LayoutKind.Sequential)]
        public struct PAINTSTRUCT { public IntPtr hdc; public bool fErase; public RECT rcPaint; public bool fRestore, fIncUpdate; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved; }
        [DllImport("user32.dll")] public static extern IntPtr BeginPaint(IntPtr h, out PAINTSTRUCT ps);
        [DllImport("user32.dll")] public static extern bool EndPaint(IntPtr h, ref PAINTSTRUCT ps);

        // DWM
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
        [DllImport("dwmapi.dll")] public static extern int DwmExtendFrameIntoClientArea(IntPtr h, ref MARGINS m);
        [StructLayout(LayoutKind.Sequential)] public struct AccentPolicy { public int AccentState, AccentFlags; public uint GradientColor; public int AnimationId; }
        [StructLayout(LayoutKind.Sequential)] public struct WindowCompositionAttributeData { public int Attribute; public IntPtr Data; public int SizeOfData; }
        [DllImport("user32.dll")] public static extern int SetWindowCompositionAttribute(IntPtr h, ref WindowCompositionAttributeData d);

        // shell
        [StructLayout(LayoutKind.Sequential)]
        public struct APPBARDATA { public int cbSize; public IntPtr hWnd; public uint uCallbackMessage, uEdge; public RECT rc; public IntPtr lParam; }
        [DllImport("shell32.dll")] public static extern IntPtr SHAppBarMessage(uint msg, ref APPBARDATA d);
        [DllImport("shell32.dll")] public static extern int SHQueryUserNotificationState(out int state);

        // GDI
        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }
        [StructLayout(LayoutKind.Sequential)] struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public short bmPlanes, bmBitsPixel; public IntPtr bmBits; }
        [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bi, uint usage, out IntPtr bits, IntPtr sec, uint off);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr o);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, uint rop);
        [DllImport("gdi32.dll")] static extern int GetObject(IntPtr h, int size, out BITMAP b);
        [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER bi, uint usage);

        /// <summary>Copy a premultiplied 32-bit frame into a window DC, alpha included, so DWM blends it over the blur.</summary>
        public static void BlitPremultiplied(IntPtr hdc, Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height;
            var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            IntPtr bits;
            IntPtr dib = CreateDIBSection(hdc, ref bi, 0, out bits, IntPtr.Zero, 0);
            BitmapData d = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            var row = new byte[w * 4];
            for (int y = 0; y < h; y++)
            {
                Marshal.Copy(new IntPtr(d.Scan0.ToInt64() + (long)y * d.Stride), row, 0, row.Length);
                Marshal.Copy(row, 0, new IntPtr(bits.ToInt64() + (long)y * w * 4), row.Length);
            }
            bmp.UnlockBits(d);
            IntPtr mem = CreateCompatibleDC(hdc);
            IntPtr old = SelectObject(mem, dib);
            BitBlt(hdc, 0, 0, w, h, mem, 0, 0, 0x00CC0020 /*SRCCOPY*/);
            SelectObject(mem, old);
            DeleteDC(mem);
            DeleteObject(dib);
        }

        /// <summary>A 32-bit HBITMAP from the shell as a Bitmap with its alpha. Shell images are usually premultiplied;
        /// if any colour channel exceeds its alpha the image is straight alpha and is read as such.</summary>
        public static Bitmap BitmapFromHbitmap(IntPtr hbm)
        {
            BITMAP b;
            GetObject(hbm, Marshal.SizeOf(typeof(BITMAP)), out b);
            int w = b.bmWidth, h = Math.Abs(b.bmHeight);
            var bi = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
            var px = new byte[w * h * 4];
            IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
            GetDIBits(dc, hbm, 0, (uint)h, px, ref bi, 0);
            DeleteDC(dc);
            bool straight = false, anyAlpha = false;
            for (int i = 0; i < px.Length; i += 4)
            {
                byte a = px[i + 3];
                if (a != 0) anyAlpha = true;
                if (px[i] > a || px[i + 1] > a || px[i + 2] > a) straight = true;
            }
            if (!anyAlpha) for (int i = 3; i < px.Length; i += 4) px[i] = 255; // an icon with no alpha channel is opaque
            var bmp = new Bitmap(w, h, straight || !anyAlpha ? PixelFormat.Format32bppArgb : PixelFormat.Format32bppPArgb);
            BitmapData d = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, bmp.PixelFormat);
            for (int y = 0; y < h; y++) Marshal.Copy(px, y * w * 4, new IntPtr(d.Scan0.ToInt64() + (long)y * d.Stride), w * 4);
            bmp.UnlockBits(d);
            return bmp;
        }
    }
}
