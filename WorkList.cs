// WorkList - a floating ball that stays on top of every window.
// Click the ball to drop down your task list. Drag the ball to move it.
//
// Build (no install needed, uses the C# compiler that ships with Windows):
//   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /out:WorkList.exe
//       /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll WorkList.cs
//
// Tasks are saved to %APPDATA%\WorkList\tasks.json

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Reflection;

[assembly: AssemblyTitle("Work List")]
[assembly: AssemblyProduct("Work List")]
[assembly: AssemblyDescription("Work List")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace WorkList
{
    // ------------------------------------------------------------------
    // Data
    // ------------------------------------------------------------------
    public class TaskItem
    {
        public long Id { get; set; }
        public string Title { get; set; }
        public string WorkDay { get; set; }   // yyyy-MM-dd, or empty
        public string Deadline { get; set; }  // yyyy-MM-dd, or empty
        public string DueTime { get; set; }   // HH:mm (24h), or empty = no time
        public bool Remind { get; set; }      // notify before / at the due date + time
        public int RemindBefore { get; set; } // minutes before the due time (0 = at the due time)
        public bool Notified { get; set; }    // reminder already shown
        public bool Done { get; set; }
    }

    public enum Filter { All, DoToday, DueToday, Overdue }

    static class Store
    {
        static string Dir
        {
            get
            {
#if TESTHOOKS
                // Test builds only: lets tests use a throwaway folder. Not in the version you install.
                string over = Environment.GetEnvironmentVariable("WORKLIST_DATA_DIR");
                if (!string.IsNullOrEmpty(over)) return over;
#endif
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WorkList");
            }
        }
        static string FilePath { get { return Path.Combine(Dir, "tasks.json"); } }
        static string PosPath { get { return Path.Combine(Dir, "ball-position.txt"); } }

        public static List<TaskItem> Load()
        {
            if (!File.Exists(FilePath)) return new List<TaskItem>();
            try
            {
                var list = new JavaScriptSerializer().Deserialize<List<TaskItem>>(File.ReadAllText(FilePath));
                return list ?? new List<TaskItem>();
            }
            catch
            {
                // Keep a copy of an unreadable file instead of silently overwriting it.
                try { File.Copy(FilePath, FilePath + ".broken-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true); } catch { }
                return new List<TaskItem>();
            }
        }

        public static void Save(List<TaskItem> tasks)
        {
            Directory.CreateDirectory(Dir);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, new JavaScriptSerializer().Serialize(tasks));
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
            else File.Move(tmp, FilePath);
        }

        public static Point? LoadBallPosition()
        {
            try
            {
                string[] parts = File.ReadAllText(PosPath).Trim().Split(',');
                return new Point(int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture));
            }
            catch { return null; }
        }

        public static void SaveBallPosition(Point? pt)
        {
            try
            {
                if (!pt.HasValue) { if (File.Exists(PosPath)) File.Delete(PosPath); return; }
                Directory.CreateDirectory(Dir);
                File.WriteAllText(PosPath, pt.Value.X.ToString(CultureInfo.InvariantCulture) + "," + pt.Value.Y.ToString(CultureInfo.InvariantCulture));
            }
            catch { }
        }
    }

    static class App
    {
        public static List<TaskItem> Tasks = new List<TaskItem>();
        public static event Action Changed;

        public static string Today { get { return Key(DateTime.Today); } }
        public static string Key(DateTime d) { return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }

        public static DateTime? Parse(string key)
        {
            DateTime d;
            if (string.IsNullOrEmpty(key) || !DateTime.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) return null;
            return d;
        }

        public static TimeSpan? ParseTime(string hhmm)
        {
            DateTime d;
            if (string.IsNullOrEmpty(hhmm) || !DateTime.TryParseExact(hhmm, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) return null;
            return d.TimeOfDay;
        }

        public static string TimeKey(TimeSpan t) { return DateTime.Today.Add(t).ToString("HH:mm", CultureInfo.InvariantCulture); }

        // e.g. "3:00 PM" (follows the Windows time format)
        public static string FormatTime(TimeSpan t) { return DateTime.Today.Add(t).ToString("t"); }

        public static DateTime? DueAt(TaskItem t)
        {
            DateTime? d = Parse(t.Deadline);
            TimeSpan? time = ParseTime(t.DueTime);
            if (!d.HasValue || !time.HasValue) return null;
            return d.Value.Add(time.Value);
        }

        // "Thu, Nov 5" or "Thu, Nov 5, 3:00 PM"
        public static string FriendlyDue(TaskItem t)
        {
            DateTime? d = Parse(t.Deadline);
            if (!d.HasValue) return "";
            TimeSpan? time = ParseTime(t.DueTime);
            return Friendly(d) + (time.HasValue ? ", " + FormatTime(time.Value) : "");
        }

        // When the reminder should pop up: the due time minus "remind before".
        public static DateTime? RemindAt(TaskItem t)
        {
            DateTime? at = DueAt(t);
            if (!at.HasValue) return null;
            return at.Value.AddMinutes(-Math.Max(0, t.RemindBefore));
        }

        // Choices offered in the "Remind me" menu (minutes before the due time).
        public static readonly int[] RemindChoices = { 0, 5, 15, 30, 60, 120, 1440, 2 * 1440, 3 * 1440, 5 * 1440, 7 * 1440 };

        // "At due time", "15 min before", "1 hour before", "1 day before"
        public static string RemindLabel(int minutes)
        {
            if (minutes <= 0) return "At due time";
            return Span(minutes) + " before";
        }

        // "15 min", "1 hour", "2 hours", "1 day"
        public static string Span(int minutes)
        {
            if (minutes < 60) return minutes + " min";
            if (minutes < 1440) { int h = (int)Math.Round(minutes / 60.0); return h + (h == 1 ? " hour" : " hours"); }
            if (minutes % (7 * 1440) == 0) { int w = minutes / (7 * 1440); return w + (w == 1 ? " week" : " weeks"); }
            int d = (int)Math.Round(minutes / 1440.0);
            return d + (d == 1 ? " day" : " days");
        }

        // Reminders that are due now (or were missed while the app was closed). Marks them as shown.
        public static List<TaskItem> TakeDueReminders(DateTime now)
        {
            var due = new List<TaskItem>();
            foreach (TaskItem t in Tasks)
            {
                if (!t.Remind || t.Notified || t.Done) continue;
                DateTime? at = RemindAt(t);
                if (at.HasValue && now >= at.Value) { t.Notified = true; due.Add(t); }
            }
            return due;
        }

        public static void Save()
        {
            try { Store.Save(Tasks); }
            catch (Exception ex) { MessageBox.Show("Could not save tasks:\n" + ex.Message, "Work List"); }
            if (Changed != null) Changed();
        }

        public static bool Matches(TaskItem t, Filter f)
        {
            string today = Today;
            switch (f)
            {
                case Filter.DoToday: return !t.Done && t.WorkDay == today;
                case Filter.DueToday: return !t.Done && t.Deadline == today;
                case Filter.Overdue: return !t.Done && !string.IsNullOrEmpty(t.Deadline) && string.CompareOrdinal(t.Deadline, today) < 0;
                default: return true;
            }
        }

        public static int Count(Filter f) { return Tasks.Count(t => Matches(t, f)); }

        // "Today", "Tomorrow", "Yesterday", or "Thu, Nov 5"
        public static string Friendly(DateTime? d)
        {
            if (!d.HasValue) return "";
            int diff = (int)(d.Value.Date - DateTime.Today).TotalDays;
            if (diff == 0) return "Today";
            if (diff == 1) return "Tomorrow";
            if (diff == -1) return "Yesterday";
            return d.Value.ToString("ddd, MMM d");
        }

        public static string FilterName(Filter f)
        {
            switch (f)
            {
                case Filter.DoToday: return "Do today";
                case Filter.DueToday: return "Due today";
                case Filter.Overdue: return "Overdue";
                default: return "All";
            }
        }
    }

    // ------------------------------------------------------------------
    // Look & feel
    // ------------------------------------------------------------------
    class Theme
    {
        public bool Dark;
        public Color Bg, Field, Text, Muted, Line, Hover, Accent, AccentSoft, Warn, Bad;

        // Always light, regardless of the Windows light/dark setting.
        public static Theme Current()
        {
            return new Theme
            {
                Dark = false,
                Bg = Color.FromArgb(248, 248, 250),
                Field = Color.FromArgb(236, 236, 241),
                Text = Color.FromArgb(28, 28, 32),
                Muted = Color.FromArgb(132, 134, 145),
                Line = Color.FromArgb(222, 223, 230),
                Hover = Color.FromArgb(238, 238, 243),
                Accent = Color.FromArgb(79, 107, 237),
                AccentSoft = Color.FromArgb(228, 233, 253),
                Warn = Color.FromArgb(214, 112, 20),
                Bad = Color.FromArgb(220, 60, 60),
            };
        }
    }

    static class Native
    {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern int GetWindowThreadProcessId(IntPtr hWnd, out int pid);

        // Windows 11 rounded corners: 2 = round, 3 = small round
        public static void RoundCorners(IntPtr h, bool small)
        {
            try { int v = small ? 3 : 2; DwmSetWindowAttribute(h, 33, ref v, sizeof(int)); } catch { }
        }

        public static bool ForegroundIsOurs()
        {
            int pid;
            GetWindowThreadProcessId(GetForegroundWindow(), out pid);
            return pid == System.Diagnostics.Process.GetCurrentProcess().Id;
        }
    }

    static class Draw
    {
        public static GraphicsPath Round(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRound(Graphics g, Color c, RectangleF r, float radius)
        {
            using (var b = new SolidBrush(c))
            using (GraphicsPath p = Round(r, radius))
                g.FillPath(b, p);
        }
    }

    class MenuColors : ProfessionalColorTable
    {
        readonly Theme t;
        public MenuColors(Theme theme) { t = theme; UseSystemColors = false; }
        public override Color MenuItemSelected { get { return t.Hover; } }
        public override Color MenuItemBorder { get { return t.Hover; } }
        public override Color MenuItemSelectedGradientBegin { get { return t.Hover; } }
        public override Color MenuItemSelectedGradientEnd { get { return t.Hover; } }
        public override Color MenuBorder { get { return t.Line; } }
        public override Color ToolStripDropDownBackground { get { return t.Bg; } }
        public override Color ImageMarginGradientBegin { get { return t.Bg; } }
        public override Color ImageMarginGradientMiddle { get { return t.Bg; } }
        public override Color ImageMarginGradientEnd { get { return t.Bg; } }
        public override Color SeparatorDark { get { return t.Line; } }
        public override Color SeparatorLight { get { return t.Bg; } }
    }

    // ------------------------------------------------------------------
    // The floating ball
    // ------------------------------------------------------------------
    class Ball : Form
    {
        const int WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_TOPMOST = 0x8, WS_EX_NOACTIVATE = 0x08000000;
        const int WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3;

        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }
        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int CX, CY; public SIZE(int cx, int cy) { CX = cx; CY = cy; } }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pprSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hDC);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr hObject);

        readonly float sc;
        readonly int D;
        readonly TaskPanel panel;
        readonly ToolTip tip = new ToolTip();
        readonly Timer timer = new Timer();
        readonly Timer reminderTimer = new Timer();
        bool mouseDown, dragging, hover;
        Point dragStartCursor, dragStartLoc;

        public Ball()
        {
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) sc = g.DpiX / 96f;
            D = (int)Math.Round(48 * sc);

            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(D, D);
            Cursor = Cursors.Hand;
            Text = "Work List";
            MoveToSavedOrDefault();

            tip.ShowAlways = true;
            panel = new TaskPanel(sc);
            panel.ResetBallRequested += delegate
            {
                Store.SaveBallPosition(null);
                MoveToSavedOrDefault();
                if (panel.Visible) panel.PlaceNear(Bounds);
            };
            App.Changed += Render;

            timer.Interval = 60 * 1000; // keeps "today" right after midnight, and follows light/dark changes
            timer.Tick += delegate { Render(); panel.RefreshIfVisible(); };
            timer.Start();

            reminderTimer.Interval = 10 * 1000;
            reminderTimer.Tick += delegate { CheckReminders(); };
            reminderTimer.Start();
        }

        void CheckReminders()
        {
            List<TaskItem> due = App.TakeDueReminders(DateTime.Now);
            if (due.Count == 0) return;
            App.Save();
            foreach (TaskItem t in due) ReminderToast.Pop(t, sc);
            ReminderToast.PlaySound();
        }

        void MoveToSavedOrDefault()
        {
            Point? saved = Store.LoadBallPosition();
            if (saved.HasValue && Screen.AllScreens.Any(s => s.WorkingArea.Contains(new Rectangle(saved.Value, Size))))
            {
                Location = saved.Value;
                return;
            }
            // Top-right corner, just below where window title bars / close buttons sit.
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Right - D - (int)(16 * sc), wa.Top + (int)(64 * sc));
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        // Clicking the ball should not steal focus from whatever you're working in.
        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_MOUSEACTIVATE) { m.Result = (IntPtr)MA_NOACTIVATE; return; }
            base.WndProc(ref m);
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Render(); }
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Render();
            CheckReminders(); // reminders missed while the app wasn't running
        }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Render(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Render(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                mouseDown = true;
                dragging = false;
                dragStartCursor = Cursor.Position;
                dragStartLoc = Location;
                Capture = true;
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (mouseDown)
            {
                Point c = Cursor.Position;
                int dx = c.X - dragStartCursor.X, dy = c.Y - dragStartCursor.Y;
                if (!dragging && (Math.Abs(dx) > 4 || Math.Abs(dy) > 4)) dragging = true;
                if (dragging)
                {
                    Location = new Point(dragStartLoc.X + dx, dragStartLoc.Y + dy);
                    if (panel.Visible) panel.PlaceNear(Bounds);
                }
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && mouseDown)
            {
                mouseDown = false;
                Capture = false;
                if (dragging)
                {
                    KeepOnScreen();
                    Store.SaveBallPosition(Location);
                    if (panel.Visible) panel.PlaceNear(Bounds);
                }
                else if (panel.Visible) panel.Hide();
                else panel.ShowNear(Bounds);
            }
            base.OnMouseUp(e);
        }

        void KeepOnScreen()
        {
            Rectangle wa = Screen.FromRectangle(Bounds).WorkingArea;
            int x = Math.Max(wa.Left, Math.Min(Left, wa.Right - Width));
            int y = Math.Max(wa.Top, Math.Min(Top, wa.Bottom - Height));
            Location = new Point(x, y);
        }

        void Render()
        {
            if (!IsHandleCreated) return;
            Theme t = Theme.Current();

            int doToday = App.Count(Filter.DoToday);
            int dueToday = App.Count(Filter.DueToday);
            int today = App.Tasks.Count(x => App.Matches(x, Filter.DoToday) || App.Matches(x, Filter.DueToday));
            int overdue = App.Count(Filter.Overdue);

            using (var bmp = new Bitmap(D, D, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.Clear(Color.Transparent);

                    float m = 5 * sc;
                    var r = new RectangleF(m, m, D - 2 * m, D - 2 * m);

                    // soft shadow
                    for (int i = 3; i >= 1; i--)
                        using (var sh = new SolidBrush(Color.FromArgb(14, 0, 0, 0)))
                            g.FillEllipse(sh, r.X - i * sc, r.Y - i * sc + 1.5f * sc, r.Width + 2 * i * sc, r.Height + 2 * i * sc);

                    // frosted, semi-transparent body
                    int alpha = hover ? 235 : 200;
                    using (var body = new SolidBrush(Color.FromArgb(alpha, t.Bg)))
                        g.FillEllipse(body, r);
                    using (var ring = new Pen(Color.FromArgb(hover ? 200 : 140, t.Dark ? Color.FromArgb(90, 92, 104) : Color.White), 1f * sc))
                        g.DrawEllipse(ring, r);

                    if (today > 0)
                    {
                        string s = today > 99 ? "99+" : today.ToString();
                        float px = (s.Length == 1 ? 17 : s.Length == 2 ? 14 : 11) * sc;
                        using (var f = new Font("Segoe UI Semibold", px, FontStyle.Regular, GraphicsUnit.Pixel))
                        using (var b = new SolidBrush(t.Accent))
                        using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                            g.DrawString(s, f, b, new RectangleF(r.X, r.Y + 0.5f * sc, r.Width, r.Height), sf);
                    }
                    else
                    {
                        // three thin lines = "list"
                        float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, w = 7 * sc, gap = 4.5f * sc;
                        using (var pen = new Pen(t.Muted, 1.6f * sc) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                            for (int i = -1; i <= 1; i++)
                                g.DrawLine(pen, cx - w, cy + i * gap, cx + w - (i == 1 ? 4 * sc : 0), cy + i * gap);
                    }

                    if (overdue > 0)
                    {
                        float br = 3.5f * sc;
                        float bx = r.X + r.Width * 0.85f, by = r.Y + r.Height * 0.15f;
                        using (var red = new SolidBrush(t.Bad)) g.FillEllipse(red, bx - br, by - br, 2 * br, 2 * br);
                    }
                }
                SetBitmap(bmp);
            }

            var parts = new List<string>();
            parts.Add(doToday + " to do today");
            parts.Add(dueToday + " due today");
            if (overdue > 0) parts.Add(overdue + " overdue");
            tip.SetToolTip(this, string.Join(" \u00b7 ", parts.ToArray()));
        }

        void SetBitmap(Bitmap bmp)
        {
            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = CreateCompatibleDC(screenDc);
            IntPtr hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
            IntPtr old = SelectObject(memDc, hBitmap);
            try
            {
                var size = new SIZE(bmp.Width, bmp.Height);
                var src = new POINT(0, 0);
                var dst = new POINT(Left, Top);
                var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
                UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, 2);
            }
            finally
            {
                SelectObject(memDc, old);
                DeleteObject(hBitmap);
                DeleteDC(memDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
    }

    // ------------------------------------------------------------------
    // Filter tabs: "All 5   Do today 1   Due today 1   Overdue 1"
    // ------------------------------------------------------------------
    class TabsBar : Control
    {
        public Theme T;
        public Filter Current = Filter.All;
        public event Action<Filter> Picked;
        readonly float sc;
        readonly Filter[] filters = { Filter.All, Filter.DoToday, Filter.DueToday, Filter.Overdue };
        readonly Rectangle[] rects = new Rectangle[4];
        int hover = -1;
        Font bold;

        public TabsBar(float scale)
        {
            sc = scale;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Cursor = Cursors.Hand;
        }

        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); bold = new Font(Font.FontFamily, Font.Size, FontStyle.Bold); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(T.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (bold == null) bold = new Font(Font, FontStyle.Bold);

            int x = 0, padX = (int)(10 * sc), gap = (int)(2 * sc);
            for (int i = 0; i < filters.Length; i++)
            {
                Filter f = filters[i];
                bool on = f == Current;
                string name = App.FilterName(f);
                string count = App.Count(f).ToString();
                Font nameFont = on ? bold : Font;
                var flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;
                int nameW = TextRenderer.MeasureText(g, name, nameFont, Size.Empty, flags).Width;
                int countW = TextRenderer.MeasureText(g, count, Font, Size.Empty, flags).Width;
                int w = padX + nameW + (int)(5 * sc) + countW + padX;
                var rect = new Rectangle(x, 0, w, Height);
                rects[i] = rect;

                if (on) Draw.FillRound(g, T.AccentSoft, rect, Height / 2f);
                else if (i == hover) Draw.FillRound(g, T.Hover, rect, Height / 2f);

                TextRenderer.DrawText(g, name, nameFont, new Rectangle(x + padX, 0, nameW + 2, Height), on ? T.Accent : T.Text, flags);
                TextRenderer.DrawText(g, count, Font, new Rectangle(x + padX + nameW + (int)(5 * sc), 0, countW + 2, Height), on ? T.Accent : T.Muted, flags);
                x += w + gap;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = Array.FindIndex(rects, r => r.Contains(e.Location));
            if (h != hover) { hover = h; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { hover = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            int i = Array.FindIndex(rects, r => r.Contains(e.Location));
            if (i >= 0 && Picked != null) Picked(filters[i]);
            base.OnMouseClick(e);
        }
    }

    // ------------------------------------------------------------------
    // The task rows (custom drawn so it can be clean and minimal)
    // ------------------------------------------------------------------
    class TaskRows : Control
    {
        public Theme T;
        public List<TaskItem> Items = new List<TaskItem>();
        public string EmptyText = "";
        public event Action<TaskItem> ToggleDone;
        public event Action<TaskItem> Delete;
        public event Action<TaskItem> EditRequested;
        public event Action<TaskItem, Point> MenuRequested;
        public TaskItem Editing;   // highlighted while it's being edited

        public readonly int RowH;
        readonly float sc;
        readonly Font titleFont, strikeFont, metaFont, statusFont;
        int scroll, hover = -1;
        bool hoverDelete, hoverCircle;

        public TaskRows(float scale)
        {
            sc = scale;
            RowH = (int)Math.Round(52 * sc);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            titleFont = new Font("Segoe UI", 10.5f);
            strikeFont = new Font("Segoe UI", 10.5f, FontStyle.Strikeout);
            metaFont = new Font("Segoe UI", 8.5f);
            statusFont = new Font("Segoe UI Semibold", 8.5f);
        }

        int S(float v) { return (int)Math.Round(v * sc); }

        public void ResetScroll() { scroll = 0; }
        int MaxScroll { get { Relayout(); return Math.Max(0, total - Height); } }

        // ----- row heights: a long title wraps onto a second line, so its row is taller -----
        const int MaxTitleLines = 2;
        int[] tops = new int[0], heights = new int[0];
        int total;

        int TextLeft { get { return S(42); } }

        // The title gets all the width except what the status word on the right needs
        // (and always room for the delete "x" that appears on hover).
        int TextWidthFor(TaskItem t)
        {
            string status = StatusOf(t, App.Today).Text;
            int right = status.Length == 0 ? 0 : TextRenderer.MeasureText(status, statusFont, Size.Empty, TextFormatFlags.NoPadding).Width + S(22);
            right = Math.Max(S(42), right);
            return Math.Max(S(40), Width - TextLeft - right);
        }
        int LineH { get { return TextRenderer.MeasureText("Ag", titleFont, Size.Empty, TextFormatFlags.NoPadding).Height; } }
        const TextFormatFlags TitleFlags = TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

        int TitleLines(TaskItem t)
        {
            if (string.IsNullOrEmpty(t.Title)) return 1;
            int h = TextRenderer.MeasureText(t.Title, titleFont, new Size(TextWidthFor(t), int.MaxValue), TitleFlags).Height;
            int lines = (int)Math.Ceiling(h / (double)LineH - 0.2);
            return Math.Max(1, Math.Min(MaxTitleLines, lines));
        }

        // Height of the title block: one line keeps the original row size.
        int TitleH(int lines) { return Math.Max(S(21), lines * LineH + S(2)); }

        void Relayout()
        {
            int n = Items.Count;
            if (tops.Length != n) { tops = new int[n]; heights = new int[n]; }
            int y = 0;
            for (int i = 0; i < n; i++)
            {
                tops[i] = y;
                heights[i] = RowH + (TitleH(TitleLines(Items[i])) - S(21));
                y += heights[i];
            }
            total = y;
        }

        // How tall the list should be to show up to maxRows rows (at least two rows tall).
        public int HeightFor(int maxRows)
        {
            Relayout();
            if (Items.Count == 0) return 2 * RowH;
            int n = Math.Min(maxRows, Items.Count);
            return Math.Max(2 * RowH, tops[n - 1] + heights[n - 1]);
        }

        struct Status { public string Text; public Color Color; }

        Status StatusOf(TaskItem t, string today)
        {
            if (t.Done) return new Status { Text = "", Color = T.Muted };
            bool late = !string.IsNullOrEmpty(t.Deadline) && string.CompareOrdinal(t.Deadline, today) < 0;
            bool dueT = t.Deadline == today, doT = t.WorkDay == today;
            if (late) return new Status { Text = "Overdue", Color = T.Bad };
            if (dueT)
            {
                TimeSpan? time = App.ParseTime(t.DueTime);
                return new Status { Text = time.HasValue ? "Due " + App.FormatTime(time.Value) : "Due today", Color = T.Warn };
            }
            if (doT) return new Status { Text = "Today", Color = T.Accent };
            return new Status { Text = "", Color = T.Muted };
        }

        Rectangle CircleRect(int top, int h) { int cs = S(18); return new Rectangle(S(10), top + (h - cs) / 2, cs, cs); }
        Rectangle DeleteRect(int top, int h) { return new Rectangle(Width - S(38), top, S(32), h); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(T.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            if (Items.Count == 0)
            {
                TextRenderer.DrawText(g, EmptyText, metaFont, ClientRectangle, T.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                return;
            }

            string today = App.Today;
            var one = TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
            Relayout();

            for (int i = 0; i < Items.Count; i++)
            {
                int top = tops[i] - scroll, rowH = heights[i];
                if (top + rowH < 0 || top > Height) continue;
                TaskItem t = Items[i];
                Status st = StatusOf(t, today);

                if (t == Editing)
                    Draw.FillRound(g, T.AccentSoft, new RectangleF(0, top + S(2), Width, rowH - S(4)), S(10));
                else if (i == hover)
                    Draw.FillRound(g, T.Hover, new RectangleF(0, top + S(2), Width, rowH - S(4)), S(10));

                // round checkbox
                Rectangle c = CircleRect(top, rowH);
                if (t.Done)
                {
                    using (var b = new SolidBrush(T.Accent)) g.FillEllipse(b, c);
                    using (var p = new Pen(T.Bg, 1.8f * sc) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                        g.DrawLines(p, new[] {
                            new PointF(c.X + c.Width * 0.28f, c.Y + c.Height * 0.52f),
                            new PointF(c.X + c.Width * 0.44f, c.Y + c.Height * 0.68f),
                            new PointF(c.X + c.Width * 0.73f, c.Y + c.Height * 0.36f) });
                }
                else
                {
                    Color ring = (i == hover && hoverCircle) ? T.Accent : T.Muted;
                    using (var p = new Pen(ring, 1.5f * sc)) g.DrawEllipse(p, c);
                }

                // text: the title wraps onto a second line if it's long ("..." only after two lines)
                int tx = TextLeft;
                int rightW = S(96);
                int textW = TextWidthFor(t);
                int lines = TitleLines(t);
                int titleH = TitleH(lines);
                var titleRect = new Rectangle(tx, top + S(8) + (lines == 1 ? (S(21) - LineH) / 2 : 0), textW, lines * LineH);
                TextRenderer.DrawText(g, t.Title ?? "", t.Done ? strikeFont : titleFont, titleRect, t.Done ? T.Muted : T.Text, TitleFlags);
                int metaTop = top + S(8) + titleH;

                var meta = new List<string>();
                DateTime? wd = App.Parse(t.WorkDay), dl = App.Parse(t.Deadline);
                if (wd.HasValue) meta.Add("Work " + App.Friendly(wd));
                if (dl.HasValue) meta.Add("Due " + App.FriendlyDue(t) + (t.Remind && !t.Done ? "  \U0001F514" : ""));
                if (meta.Count > 0)
                    TextRenderer.DrawText(g, string.Join("  \u00b7  ", meta.ToArray()), metaFont,
                        new Rectangle(tx, metaTop, textW, S(16)), T.Muted, one | TextFormatFlags.VerticalCenter);

                // right side: status word, or a delete "x" while hovering
                if (i == hover)
                {
                    Rectangle d = DeleteRect(top, rowH);
                    float cx = d.X + d.Width / 2f, cy = d.Y + d.Height / 2f, k = 4.5f * sc;
                    if (hoverDelete) Draw.FillRound(g, T.Field, new RectangleF(cx - 12 * sc, cy - 12 * sc, 24 * sc, 24 * sc), 12 * sc);
                    using (var p = new Pen(hoverDelete ? T.Bad : T.Muted, 1.5f * sc) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    {
                        g.DrawLine(p, cx - k, cy - k, cx + k, cy + k);
                        g.DrawLine(p, cx - k, cy + k, cx + k, cy - k);
                    }
                }
                else if (st.Text.Length > 0)
                {
                    TextRenderer.DrawText(g, st.Text, statusFont, new Rectangle(Width - rightW, top, rightW - S(12), rowH), st.Color,
                        one | TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
                }
            }

            // thin scroll indicator when the list is longer than the view
            if (MaxScroll > 0)
            {
                float h = Math.Max(S(24), Height * Height / (float)total);
                float y = (Height - h) * scroll / MaxScroll;
                Draw.FillRound(g, T.Line, new RectangleF(Width - S(3), y, S(3), h), S(1.5f));
            }
        }

        int RowAt(Point p)
        {
            if (p.Y < 0 || p.Y >= Height) return -1;
            Relayout();
            int y = p.Y + scroll;
            for (int i = 0; i < Items.Count; i++)
                if (y >= tops[i] && y < tops[i] + heights[i]) return i;
            return -1;
        }

        Rectangle DeleteRectFor(int i) { return DeleteRect(tops[i] - scroll, heights[i]); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = RowAt(e.Location);
            bool del = false, circ = false;
            if (h >= 0)
            {
                del = DeleteRectFor(h).Contains(e.Location);
                circ = e.X < S(38);
            }
            if (h != hover || del != hoverDelete || circ != hoverCircle)
            {
                hover = h; hoverDelete = del; hoverCircle = circ;
                Cursor = (del || circ) ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hover = -1; hoverDelete = false; hoverCircle = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            int i = RowAt(e.Location);
            if (i >= 0)
            {
                TaskItem t = Items[i];
                if (e.Button == MouseButtons.Right) { if (MenuRequested != null) MenuRequested(t, PointToScreen(e.Location)); }
                else if (DeleteRectFor(i).Contains(e.Location)) { if (Delete != null) Delete(t); }
                else if (e.X < S(38)) { if (ToggleDone != null) ToggleDone(t); }
            }
            base.OnMouseClick(e);
        }

        // Double-click a task's text to edit it.
        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            int i = RowAt(e.Location);
            if (i >= 0 && e.Button == MouseButtons.Left && e.X >= S(38) && !DeleteRectFor(i).Contains(e.Location))
                if (EditRequested != null) EditRequested(Items[i]);
            base.OnMouseDoubleClick(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            scroll = Math.Max(0, Math.Min(MaxScroll, scroll - e.Delta / 120 * RowH));
            Invalidate();
            base.OnMouseWheel(e);
        }
    }

    // ------------------------------------------------------------------
    // The drop-down panel
    // ------------------------------------------------------------------
    class TaskPanel : Form
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

        public event Action ResetBallRequested;

        readonly float sc;
        Theme t = Theme.Current();
        readonly Label lblDate, lblSummary, btnMore, chipWork, chipDue, lnkSave, lnkCancel;
        TaskItem editing;   // the task being edited, or null when adding a new one
        readonly TextBox txtTitle;
        readonly TabsBar tabs;
        readonly TaskRows rows;
        Rectangle fieldRect;
        Rectangle lastBall;
        DateTime? workDay = DateTime.Today, dueDay;
        TimeSpan? dueTime;
        bool remind;
        int remindBefore;
        int openMenus;
        const int MaxVisibleRows = 7;

        int S(float v) { return (int)Math.Round(v * sc); }

        public TaskPanel(float scale)
        {
            sc = scale;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Font = new Font("Segoe UI", 9.5f);
            KeyPreview = true;
            Text = "Work List";
            Opacity = 0.94;
            DoubleBuffered = true;

            int W = S(372), p = S(20), y = S(18);

            lblDate = new Label { AutoSize = true, Font = new Font("Segoe UI Semibold", 13f), Location = new Point(p - S(2), y) };
            btnMore = new Label { Text = "\u2022\u2022\u2022", AutoSize = false, TextAlign = ContentAlignment.MiddleCenter, Size = new Size(S(32), S(26)), Cursor = Cursors.Hand, Font = new Font("Segoe UI", 7f) };
            btnMore.Location = new Point(W - p - btnMore.Width + S(8), y + S(2));
            btnMore.Click += delegate { ShowMoreMenu(); };
            y += S(30);
            lblSummary = new Label { AutoSize = true, Location = new Point(p, y), Font = new Font("Segoe UI", 9f) };
            y += S(32);

            fieldRect = new Rectangle(p, y, W - 2 * p, S(40));
            txtTitle = new TextBox { BorderStyle = BorderStyle.None, Font = new Font("Segoe UI", 10.5f), Width = fieldRect.Width - S(28) };
            txtTitle.Location = new Point(fieldRect.X + S(14), fieldRect.Y + (fieldRect.Height - txtTitle.Height) / 2);
            txtTitle.HandleCreated += delegate { SendMessage(txtTitle.Handle, 0x1501, (IntPtr)1, "Add a task\u2026"); };
            txtTitle.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) { AddTask(); e.Handled = true; e.SuppressKeyPress = true; }
            };

            // "Save" / "Cancel" appear inside the box while editing a task.
            lnkCancel = new ChipLabel { Text = "Cancel", AutoSize = true, Cursor = Cursors.Hand, Font = new Font("Segoe UI", 9.5f), Visible = false };
            lnkSave = new ChipLabel { Text = "Save", AutoSize = true, Cursor = Cursors.Hand, Font = new Font("Segoe UI Semibold", 9.5f), Visible = false };
            lnkCancel.Click += delegate { EndEdit(); txtTitle.Focus(); };
            lnkSave.Click += delegate { AddTask(); txtTitle.Focus(); };
            y = fieldRect.Bottom + S(8);

            chipWork = Chip();
            chipWork.Location = new Point(p + S(2), y);
            chipWork.Click += delegate { ShowDateMenu(chipWork, true); };
            chipDue = Chip();
            chipDue.Click += delegate { ShowDateMenu(chipDue, false); };
            y += S(34);

            tabs = new TabsBar(sc) { Location = new Point(p - S(8), y), Size = new Size(W - 2 * p + S(16), S(30)) };
            tabs.Picked += delegate(Filter f) { tabs.Current = f; rows.ResetScroll(); RefreshAll(); };
            y += S(30) + S(10);

            rows = new TaskRows(sc) { Location = new Point(p - S(10), y), Width = W - 2 * p + S(20) };
            rows.ToggleDone += delegate(TaskItem x) { x.Done = !x.Done; App.Save(); RefreshAll(); };
            rows.Delete += delegate(TaskItem x) { if (x == editing) EndEdit(); App.Tasks.Remove(x); App.Save(); RefreshAll(); };
            rows.EditRequested += BeginEdit;
            rows.MenuRequested += ShowRowMenu;

            Controls.AddRange(new Control[] { lblDate, btnMore, lblSummary, txtTitle, lnkSave, lnkCancel, chipWork, chipDue, tabs, rows });
            Width = W;
            ApplyTheme();
            UpdateChips();
            App.Changed += delegate { if (Visible) RefreshAll(); }; // e.g. "Mark done" from a reminder
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible) return;
            foreach (Form f in OwnedForms) f.Close(); // close an open date picker too
            if (editing != null) EndEdit();           // closing the panel cancels an unsaved edit
        }

        // ----- editing an existing task --------------------------------

        void BeginEdit(TaskItem x)
        {
            editing = x;
            txtTitle.Text = x.Title ?? "";
            workDay = App.Parse(x.WorkDay);
            dueDay = App.Parse(x.Deadline);
            dueTime = App.ParseTime(x.DueTime);
            remind = x.Remind && dueTime.HasValue;
            remindBefore = x.RemindBefore;

            // Put Save / Cancel at the right end of the box and shorten the text field to fit.
            lnkCancel.Location = new Point(fieldRect.Right - S(12) - lnkCancel.PreferredWidth, fieldRect.Y + (fieldRect.Height - lnkCancel.PreferredHeight) / 2);
            lnkSave.Location = new Point(lnkCancel.Left - S(12) - lnkSave.PreferredWidth, fieldRect.Y + (fieldRect.Height - lnkSave.PreferredHeight) / 2);
            txtTitle.Width = lnkSave.Left - S(10) - txtTitle.Left;
            lnkSave.Visible = lnkCancel.Visible = true;

            rows.Editing = x;
            UpdateChips();
            RefreshAll();
            txtTitle.Focus();
            txtTitle.SelectionStart = txtTitle.TextLength;
        }

        void SaveEdit()
        {
            TaskItem x = editing;
            string title = txtTitle.Text.Trim();
            if (x == null || title.Length == 0) return;
            x.Title = title;
            x.WorkDay = workDay.HasValue ? App.Key(workDay.Value) : "";
            x.Deadline = dueDay.HasValue ? App.Key(dueDay.Value) : "";
            x.DueTime = dueDay.HasValue && dueTime.HasValue ? App.TimeKey(dueTime.Value) : "";
            x.Remind = dueDay.HasValue && dueTime.HasValue && remind;
            x.RemindBefore = remindBefore;
            // Re-arm the reminder for the new time (but don't pop one for a time that's already past).
            DateTime? at = App.RemindAt(x);
            x.Notified = x.Remind && at.HasValue && at.Value <= DateTime.Now;
            App.Save();
            EndEdit();
        }

        void EndEdit()
        {
            editing = null;
            rows.Editing = null;
            lnkSave.Visible = lnkCancel.Visible = false;
            txtTitle.Width = fieldRect.Width - S(28);
            ResetInputs();
            RefreshAll();
        }

        void ResetInputs()
        {
            txtTitle.Text = "";
            workDay = DateTime.Today;
            dueDay = null;
            dueTime = null;
            remind = false;
            remindBefore = 0;
            UpdateChips();
        }

        // A label drawn with the same text renderer as the task list, so the bell symbol shows up.
        class ChipLabel : Label
        {
            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.Clear(BackColor);
                Rectangle r = new Rectangle(Padding.Left, Padding.Top, Width - Padding.Horizontal, Height - Padding.Vertical);
                TextRenderer.DrawText(e.Graphics, Text, Font, r, ForeColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            }
        }

        Label Chip()
        {
            var l = new ChipLabel { AutoSize = true, Cursor = Cursors.Hand, Padding = new Padding(S(2), S(3), S(2), S(3)), Font = new Font("Segoe UI", 9f) };
            l.MouseEnter += delegate { l.ForeColor = t.Text; };
            l.MouseLeave += delegate { l.ForeColor = t.Muted; };
            return l;
        }

        void ApplyTheme()
        {
            t = Theme.Current();
            BackColor = t.Bg;
            ForeColor = t.Text;
            lblDate.ForeColor = t.Text;
            lblSummary.ForeColor = t.Muted;
            btnMore.ForeColor = t.Muted;
            txtTitle.BackColor = t.Field;
            txtTitle.ForeColor = t.Text;
            chipWork.ForeColor = t.Muted;
            chipDue.ForeColor = t.Muted;
            foreach (Control c in Controls) if (c is Label) c.BackColor = t.Bg;
            lnkSave.BackColor = lnkCancel.BackColor = t.Field;
            lnkSave.ForeColor = t.Accent;
            lnkCancel.ForeColor = t.Muted;
            tabs.T = t; tabs.BackColor = t.Bg;
            rows.T = t; rows.BackColor = t.Bg;
            Invalidate(true);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.RoundCorners(Handle, false);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Draw.FillRound(e.Graphics, t.Field, fieldRect, S(10));
        }

        // Behave like a drop-down: close when you click somewhere else.
        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (openMenus > 0) return;
            Hide();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                if (editing != null) EndEdit(); // Esc cancels an edit first
                else Hide();
                e.Handled = true;
                return;
            }
            base.OnKeyDown(e);
        }

        public void ShowNear(Rectangle ball)
        {
            lastBall = ball;
            ApplyTheme();
            RefreshAll();
            Show();
            Activate();
            txtTitle.Focus();
        }

        public void PlaceNear(Rectangle ball)
        {
            lastBall = ball;
            Rectangle wa = Screen.FromRectangle(ball).WorkingArea;
            bool ballOnRight = ball.Left + ball.Width / 2 > wa.Left + wa.Width / 2;
            int x = ballOnRight ? ball.Right - Width : ball.Left;
            int y = ball.Bottom + S(6);
            if (y + Height > wa.Bottom) y = ball.Top - Height - S(6);
            x = Math.Max(wa.Left, Math.Min(x, wa.Right - Width));
            y = Math.Max(wa.Top, Math.Min(y, wa.Bottom - Height));
            Location = new Point(x, y);
        }

        public void RefreshIfVisible()
        {
            if (!Visible) return;
            ApplyTheme();
            RefreshAll();
        }

        void RefreshAll()
        {
            lblDate.Text = DateTime.Today.ToString("dddd, MMMM d");

            int doT = App.Count(Filter.DoToday), dueT = App.Count(Filter.DueToday), late = App.Count(Filter.Overdue);
            var parts = new List<string>();
            if (doT > 0) parts.Add(doT + " to do today");
            if (dueT > 0) parts.Add(dueT + " due today");
            if (late > 0) parts.Add(late + " overdue");
            lblSummary.Text = App.Tasks.Count == 0 ? "No tasks yet"
                : parts.Count == 0 ? "Nothing scheduled for today"
                : string.Join("  \u00b7  ", parts.ToArray());

            rows.Items = App.Tasks
                .Where(x => App.Matches(x, tabs.Current))
                .OrderBy(x => x.Done)
                .ThenBy(x => string.IsNullOrEmpty(x.Deadline) ? "9999" : x.Deadline, StringComparer.Ordinal)
                .ThenBy(x => string.IsNullOrEmpty(x.WorkDay) ? "9999" : x.WorkDay, StringComparer.Ordinal)
                .ToList();
            rows.EmptyText = App.Tasks.Count == 0 ? "Type a task above and press Enter" : "Nothing here";

            rows.Height = rows.HeightFor(MaxVisibleRows);
            ClientSize = new Size(ClientSize.Width, rows.Bottom + S(12));

            tabs.Invalidate();
            rows.Invalidate();
            if (lastBall != Rectangle.Empty) PlaceNear(lastBall);
        }

        void UpdateChips()
        {
            chipWork.Text = "Work on: " + (workDay.HasValue ? App.Friendly(workDay) : "any day") + "  \u25BE";
            string due = "none";
            if (dueDay.HasValue)
                due = App.Friendly(dueDay) + (dueTime.HasValue ? ", " + App.FormatTime(dueTime.Value) : "") + (remind ? "  \U0001F514" : "");
            chipDue.Text = "Due: " + due + "  \u25BE";
            chipDue.Location = new Point(chipWork.Right + S(14), chipWork.Top);
        }

        void AddTask()
        {
            if (editing != null) { SaveEdit(); return; }
            string title = txtTitle.Text.Trim();
            if (title.Length == 0) return;
            var task = new TaskItem
            {
                Id = DateTime.Now.Ticks,
                Title = title,
                WorkDay = workDay.HasValue ? App.Key(workDay.Value) : "",
                Deadline = dueDay.HasValue ? App.Key(dueDay.Value) : "",
                DueTime = dueDay.HasValue && dueTime.HasValue ? App.TimeKey(dueTime.Value) : "",
                Remind = dueDay.HasValue && dueTime.HasValue && remind,
                RemindBefore = remindBefore,
                Done = false,
            };
            // A reminder for a time that has already passed shouldn't pop up right away.
            DateTime? at = App.RemindAt(task);
            task.Notified = task.Remind && at.HasValue && at.Value <= DateTime.Now;
            App.Tasks.Add(task);
            App.Save();
            ResetInputs();
            RefreshAll();
        }

        // ----- menus ---------------------------------------------------

        ContextMenuStrip NewMenu()
        {
            var m = new ContextMenuStrip
            {
                ShowImageMargin = false,
                Renderer = new ToolStripProfessionalRenderer(new MenuColors(t)) { RoundedEdges = false },
                Font = new Font("Segoe UI", 9.5f),
                BackColor = t.Bg,
                ForeColor = t.Text,
                Padding = new Padding(0, S(4), 0, S(4)),
            };
            Track(m);
            m.Closed += delegate { BeginInvoke(new Action(() => m.Dispose())); };
            return m;
        }

        void Track(ToolStripDropDown m)
        {
            m.HandleCreated += delegate { Native.RoundCorners(m.Handle, true); };
            m.Opened += delegate { openMenus++; };
            m.Closed += delegate
            {
                openMenus = Math.Max(0, openMenus - 1);
                // If the menu was dismissed by clicking another app, close the panel too.
                BeginInvoke(new Action(() =>
                {
                    if (openMenus == 0 && Visible && !Native.ForegroundIsOurs()) Hide();
                }));
            };
        }

        ToolStripMenuItem Item(string text, Action onClick)
        {
            var it = new ToolStripMenuItem(text) { ForeColor = t.Text, Padding = new Padding(S(4), S(3), S(4), S(3)) };
            it.Click += delegate { onClick(); };
            return it;
        }

        void ShowDateMenu(Label chip, bool isWork)
        {
            ContextMenuStrip m = NewMenu();
            // Quick choices set just the day (no time, no reminder).
            Action<DateTime?> set = d =>
            {
                if (isWork) workDay = d;
                else { dueDay = d; dueTime = null; remind = false; }
                UpdateChips();
                txtTitle.Focus();
            };
            m.Items.Add(Item("Today", () => set(DateTime.Today)));
            m.Items.Add(Item("Tomorrow", () => set(DateTime.Today.AddDays(1))));
            m.Items.Add(Item("In a week", () => set(DateTime.Today.AddDays(7))));
            m.Items.Add(Item(isWork ? "Pick a date\u2026" : "Pick date & time\u2026", () => BeginInvoke(new Action(() => ShowPicker(chip, isWork)))));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(Item(isWork ? "Any day" : "No deadline", () => set(null)));
            m.Show(chip, new Point(0, chip.Height));
        }

        void ShowPicker(Control anchor, bool isWork)
        {
            var pop = new DateTimePopup(sc, t, isWork ? workDay : dueDay, isWork ? null : dueTime, !isWork && remind, remindBefore, !isWork);
            pop.Owner = this;
            openMenus++;
            pop.FormClosed += delegate
            {
                openMenus = Math.Max(0, openMenus - 1);
                if (pop.Committed)
                {
                    if (isWork) workDay = pop.Date;
                    else { dueDay = pop.Date; dueTime = pop.Time; remind = pop.Remind && pop.Time.HasValue; remindBefore = pop.RemindBefore; }
                    UpdateChips();
                }
                BeginInvoke(new Action(() =>
                {
                    if (!Visible) return;
                    if (Native.ForegroundIsOurs()) { Activate(); txtTitle.Focus(); }
                    else Hide(); // closed by clicking another app
                }));
            };
            pop.ShowAt(anchor.PointToScreen(new Point(0, anchor.Height + S(4))));
        }

        void ShowRowMenu(TaskItem x, Point screen)
        {
            ContextMenuStrip m = NewMenu();
            m.Items.Add(Item("Edit", () => BeginEdit(x)));
            m.Items.Add(Item(x.Done ? "Mark as not done" : "Mark as done", () => { x.Done = !x.Done; App.Save(); RefreshAll(); }));
            m.Items.Add(Item("Work on it today", () => { x.WorkDay = App.Today; App.Save(); RefreshAll(); }));
            m.Items.Add(Item("Work on it tomorrow", () => { x.WorkDay = App.Key(DateTime.Today.AddDays(1)); App.Save(); RefreshAll(); }));
            m.Items.Add(new ToolStripSeparator());
            var del = Item("Delete", () => { if (x == editing) EndEdit(); App.Tasks.Remove(x); App.Save(); RefreshAll(); });
            del.ForeColor = t.Bad;
            m.Items.Add(del);
            m.Show(screen);
        }

        void ShowMoreMenu()
        {
            ContextMenuStrip m = NewMenu();
            m.Items.Add(Item("Clear finished tasks", () => { App.Tasks.RemoveAll(x => x.Done); App.Save(); RefreshAll(); }));
            m.Items.Add(Item("Move ball back to the corner", () => { if (ResetBallRequested != null) ResetBallRequested(); }));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(Item("Quit Work List", () => Application.Exit()));
            m.Show(btnMore, new Point(btnMore.Width - m.PreferredSize.Width, btnMore.Height));
        }
    }

    // ------------------------------------------------------------------
    // Date (and time) picker, drawn in the app's own style
    // ------------------------------------------------------------------
    class DateTimePopup : Form
    {
        public DateTime? Date;
        public TimeSpan? Time;
        public bool Remind;
        public int RemindBefore;   // minutes before the due time
        public bool Committed;

        readonly bool withTime;
        bool menuOpen;
        readonly Theme t;
        readonly float sc;
        readonly bool h24 = CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern.Contains("H");
        readonly Font fMonth, fSmall, fDay, fDayBold, fTime, fBody, fBtn;
        readonly Dictionary<string, Rectangle> hit = new Dictionary<string, Rectangle>();
        readonly int pad, cell, rowH, headTop, weekTop, gridTop, gridBottom;
        DateTime month;
        string hover;
        int seg;          // selected time part: 0 hour, 1 minute, 2 AM/PM
        string typed = "";
        bool closing;

        int S(float v) { return (int)Math.Round(v * sc); }

        public DateTimePopup(float scale, Theme theme, DateTime? date, TimeSpan? time, bool remind, int remindBefore, bool withTime)
        {
            sc = scale; t = theme; this.withTime = withTime;
            Date = date; Time = time; Remind = remind && time.HasValue; RemindBefore = remindBefore;
            DateTime start = date ?? DateTime.Today;
            month = new DateTime(start.Year, start.Month, 1);

            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            KeyPreview = true;
            DoubleBuffered = true;
            BackColor = t.Bg;

            fMonth = new Font("Segoe UI Semibold", 11f);
            fSmall = new Font("Segoe UI", 8.5f);
            fDay = new Font("Segoe UI", 9.5f);
            fDayBold = new Font("Segoe UI Semibold", 9.5f);
            fTime = new Font("Segoe UI", 11f);
            fBody = new Font("Segoe UI", 9.5f);
            fBtn = new Font("Segoe UI Semibold", 9.5f);

            pad = S(14); cell = S(36); rowH = S(34);
            headTop = S(12);
            weekTop = headTop + S(34);
            gridTop = weekTop + S(24);
            gridBottom = gridTop + 6 * rowH;
            int h = withTime ? gridBottom + S(10) + S(38) + S(38) + S(12) + S(32) + S(14) : gridBottom + S(10);
            ClientSize = new Size(2 * pad + 7 * cell, h);
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Native.RoundCorners(Handle, false); }

        public void ShowAt(Point p)
        {
            Rectangle wa = Screen.FromPoint(p).WorkingArea;
            int x = Math.Max(wa.Left, Math.Min(p.X, wa.Right - Width));
            int y = Math.Max(wa.Top, Math.Min(p.Y, wa.Bottom - Height));
            Location = new Point(x, y);
            Show();
            Activate();
        }

        // Clicking anywhere else closes the picker without changing anything.
        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (closing || menuOpen) return;
            closing = true;
            BeginInvoke(new Action(Close));
        }

        void ShowRemindMenu()
        {
            var m = new ContextMenuStrip
            {
                ShowImageMargin = false,
                Renderer = new ToolStripProfessionalRenderer(new MenuColors(t)) { RoundedEdges = false },
                Font = new Font("Segoe UI", 9.5f),
                BackColor = t.Bg,
                Padding = new Padding(0, S(4), 0, S(4)),
            };
            Action<string, bool, int> add = (text, on, minutes) =>
            {
                bool current = on ? (Remind && RemindBefore == minutes) : !Remind;
                var it = new ToolStripMenuItem(text) { ForeColor = current ? t.Accent : t.Text, Padding = new Padding(S(4), S(3), S(4), S(3)) };
                if (current) it.Font = new Font(m.Font, FontStyle.Bold);
                it.Click += delegate { Remind = on; if (on) RemindBefore = minutes; Invalidate(); };
                m.Items.Add(it);
            };
            add("Off", false, 0);
            m.Items.Add(new ToolStripSeparator());
            foreach (int minutes in App.RemindChoices) add(App.RemindLabel(minutes), true, minutes);

            m.HandleCreated += delegate { Native.RoundCorners(m.Handle, true); };
            m.Opened += delegate { menuOpen = true; };
            m.Closed += delegate
            {
                menuOpen = false;
                BeginInvoke(new Action(() =>
                {
                    m.Dispose();
                    if (!closing && Form.ActiveForm != this && !Native.ForegroundIsOurs()) { closing = true; Close(); }
                }));
            };
            Rectangle r = hit["remind"];
            m.Show(this, new Point(r.Right - m.PreferredSize.Width, r.Bottom));
        }

        protected override void OnFormClosing(FormClosingEventArgs e) { closing = true; base.OnFormClosing(e); }

        void Commit()
        {
            if (withTime && Time.HasValue && !Date.HasValue) Date = DateTime.Today;
            if (!Time.HasValue) Remind = false;
            Committed = true;
            closing = true;
            Close();
        }

        DateTime FirstCell()
        {
            int fdow = (int)CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
            int offset = ((int)month.DayOfWeek - fdow + 7) % 7;
            return month.AddDays(-offset);
        }

        int TimeRowTop { get { return gridBottom + S(10); } }
        int RemindRowTop { get { return TimeRowTop + S(38); } }
        int FooterTop { get { return RemindRowTop + S(38) + S(12); } }

        string[] TimeParts()
        {
            DateTime dt = DateTime.Today.Add(Time ?? TimeSpan.Zero);
            if (h24) return new[] { dt.ToString("HH"), dt.ToString("mm") };
            string ampm = dt.ToString("tt", CultureInfo.CurrentCulture);
            if (string.IsNullOrEmpty(ampm)) ampm = dt.Hour < 12 ? "AM" : "PM";
            return new[] { (dt.Hour % 12 == 0 ? 12 : dt.Hour % 12).ToString(), dt.ToString("mm"), ampm };
        }

        void BuildHits()
        {
            hit.Clear();
            int W = ClientSize.Width;
            hit["prev"] = new Rectangle(W - pad - S(64), headTop, S(30), S(30));
            hit["next"] = new Rectangle(W - pad - S(32), headTop, S(30), S(30));
            for (int i = 0; i < 42; i++)
                hit["d" + i] = new Rectangle(pad + (i % 7) * cell, gridTop + (i / 7) * rowH, cell, rowH);

            if (!withTime) return;

            int y = TimeRowTop, rowHgt = S(38);
            if (!Time.HasValue)
            {
                Size sz = TextRenderer.MeasureText("Add time", fBody);
                hit["addtime"] = new Rectangle(W - pad - sz.Width - S(16), y + S(4), sz.Width + S(16), rowHgt - S(8));
            }
            else
            {
                int xR = W - pad;
                hit["cleartime"] = new Rectangle(xR - S(26), y + (rowHgt - S(26)) / 2, S(26), S(26));
                xR -= S(30);
                hit["up"] = new Rectangle(xR - S(22), y + S(3), S(22), rowHgt / 2 - S(3));
                hit["down"] = new Rectangle(xR - S(22), y + rowHgt / 2, S(22), rowHgt / 2 - S(3));
                xR -= S(26);

                string[] parts = TimeParts();
                var flags = TextFormatFlags.NoPadding;
                int colonW = TextRenderer.MeasureText(":", fTime, Size.Empty, flags).Width + S(2);
                var widths = parts.Select(s => TextRenderer.MeasureText(s, fTime, Size.Empty, flags).Width + S(12)).ToArray();
                int total = widths.Sum() + colonW + (parts.Length == 3 ? S(4) : 0);
                int x = xR - total, segH = S(30), segY = y + (rowHgt - segH) / 2;
                hit["seg0"] = new Rectangle(x, segY, widths[0], segH); x += widths[0];
                hit["colon"] = new Rectangle(x, segY, colonW, segH); x += colonW;
                hit["seg1"] = new Rectangle(x, segY, widths[1], segH); x += widths[1];
                if (parts.Length == 3) { x += S(4); hit["seg2"] = new Rectangle(x, segY, widths[2], segH); }
            }

            hit["remind"] = new Rectangle(pad, RemindRowTop, W - 2 * pad, S(38));
            hit["done"] = new Rectangle(W - pad - S(80), FooterTop, S(80), S(32));
            hit["cancel"] = new Rectangle(hit["done"].X - S(8) - S(76), FooterTop, S(76), S(32));
        }

        string HitAt(Point p)
        {
            foreach (var kv in hit)
                if (kv.Key != "colon" && kv.Value.Contains(p)) return kv.Key;
            return null;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(t.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            BuildHits();
            int W = ClientSize.Width;
            var mid = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
            var left = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;

            // Month header with arrows
            TextRenderer.DrawText(g, month.ToString("MMMM yyyy"), fMonth, new Rectangle(pad + S(8), headTop, W, S(30)), t.Text, left);
            foreach (string id in new[] { "prev", "next" })
            {
                Rectangle r = hit[id];
                if (hover == id) using (var b = new SolidBrush(t.Hover)) g.FillEllipse(b, r);
                float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, k = 3.5f * sc, dir = id == "prev" ? 1 : -1;
                using (var p = new Pen(t.Muted, 1.6f * sc) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                    g.DrawLines(p, new[] { new PointF(cx + dir * k / 2, cy - k), new PointF(cx - dir * k / 2, cy), new PointF(cx + dir * k / 2, cy + k) });
            }

            // Weekday names
            DateTimeFormatInfo dtf = CultureInfo.CurrentCulture.DateTimeFormat;
            int fdow = (int)dtf.FirstDayOfWeek;
            for (int i = 0; i < 7; i++)
                TextRenderer.DrawText(g, dtf.ShortestDayNames[(fdow + i) % 7], fSmall,
                    new Rectangle(pad + i * cell, weekTop, cell, S(24)), t.Muted, mid);

            // Days
            DateTime first = FirstCell();
            Color faint = Color.FromArgb(190, 192, 200);
            for (int i = 0; i < 42; i++)
            {
                DateTime d = first.AddDays(i);
                Rectangle r = hit["d" + i];
                int cs = S(30);
                var circle = new Rectangle(r.X + (r.Width - cs) / 2, r.Y + (r.Height - cs) / 2, cs, cs);
                bool sel = Date.HasValue && d == Date.Value.Date;
                bool today = d == DateTime.Today;
                bool inMonth = d.Month == month.Month;
                if (sel) using (var b = new SolidBrush(t.Accent)) g.FillEllipse(b, circle);
                else if (hover == "d" + i) using (var b = new SolidBrush(t.Hover)) g.FillEllipse(b, circle);
                Color c = sel ? Color.White : today ? t.Accent : inMonth ? t.Text : faint;
                TextRenderer.DrawText(g, d.Day.ToString(), sel || today ? fDayBold : fDay, circle, c, mid);
            }

            if (!withTime) return;

            using (var line = new Pen(t.Line)) g.DrawLine(line, pad, TimeRowTop, W - pad, TimeRowTop);

            // Time row
            int y = TimeRowTop, rowHgt = S(38);
            TextRenderer.DrawText(g, "Time", fBody, new Rectangle(pad + S(6), y, S(80), rowHgt), t.Text, left);
            if (!Time.HasValue)
            {
                Rectangle r = hit["addtime"];
                if (hover == "addtime") Draw.FillRound(g, t.AccentSoft, r, r.Height / 2f);
                TextRenderer.DrawText(g, "Add time", fBody, r, t.Accent, mid);
            }
            else
            {
                string[] parts = TimeParts();
                for (int i = 0; i < parts.Length; i++)
                {
                    Rectangle r = hit["seg" + i];
                    if (seg == i) Draw.FillRound(g, t.AccentSoft, r, S(8));
                    else if (hover == "seg" + i) Draw.FillRound(g, t.Hover, r, S(8));
                    TextRenderer.DrawText(g, parts[i], fTime, r, seg == i ? t.Accent : t.Text, mid);
                }
                TextRenderer.DrawText(g, ":", fTime, hit["colon"], t.Text, mid);

                foreach (string id in new[] { "up", "down" })
                {
                    Rectangle r = hit[id];
                    float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, k = 3.5f * sc, dir = id == "up" ? 1 : -1;
                    using (var p = new Pen(hover == id ? t.Accent : t.Muted, 1.6f * sc) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                        g.DrawLines(p, new[] { new PointF(cx - k, cy + dir * k / 2), new PointF(cx, cy - dir * k / 2), new PointF(cx + k, cy + dir * k / 2) });
                }

                Rectangle x = hit["cleartime"];
                if (hover == "cleartime") using (var b = new SolidBrush(t.Hover)) g.FillEllipse(b, x);
                float xc = x.X + x.Width / 2f, yc = x.Y + x.Height / 2f, kk = 3.5f * sc;
                using (var p = new Pen(t.Muted, 1.5f * sc) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawLine(p, xc - kk, yc - kk, xc + kk, yc + kk);
                    g.DrawLine(p, xc - kk, yc + kk, xc + kk, yc - kk);
                }
            }

            // "Remind me" + chosen option (opens a small menu)
            Rectangle rr = hit["remind"];
            bool canRemind = Time.HasValue;
            if (canRemind && hover == "remind") Draw.FillRound(g, t.Hover, rr, S(8));
            TextRenderer.DrawText(g, "Remind me", fBody, new Rectangle(rr.X + S(6), rr.Y, rr.Width, rr.Height), canRemind ? t.Text : faint, left);
            string val = !canRemind ? "Add a time first" : (Remind ? App.RemindLabel(RemindBefore) : "Off") + "  ▾";
            Color valColor = !canRemind ? faint : Remind ? t.Accent : t.Muted;
            TextRenderer.DrawText(g, val, fBody, new Rectangle(rr.X, rr.Y, rr.Width - S(8), rr.Height), valColor,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);

            // Buttons
            Rectangle cancel = hit["cancel"], done = hit["done"];
            if (hover == "cancel") Draw.FillRound(g, t.Hover, cancel, S(8));
            TextRenderer.DrawText(g, "Cancel", fBody, cancel, t.Muted, mid);
            Draw.FillRound(g, hover == "done" ? Color.FromArgb(66, 92, 220) : t.Accent, done, S(8));
            TextRenderer.DrawText(g, "Done", fBtn, done, Color.White, mid);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            BuildHits();
            string h = HitAt(e.Location);
            if (h == "remind" && !Time.HasValue) h = null;
            if (h != hover)
            {
                hover = h;
                Cursor = h != null ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { hover = null; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            BuildHits();
            string id = HitAt(e.Location);
            if (id == null) return;
            typed = "";
            if (id == "prev") month = month.AddMonths(-1);
            else if (id == "next") month = month.AddMonths(1);
            else if (id[0] == 'd' && char.IsDigit(id[1]))
            {
                DateTime d = FirstCell().AddDays(int.Parse(id.Substring(1)));
                Date = d;
                if (!withTime) { Commit(); return; }
                if (d.Month != month.Month) month = new DateTime(d.Year, d.Month, 1);
            }
            else if (id == "addtime")
            {
                Time = new TimeSpan(Math.Min(23, DateTime.Now.Hour + 1), 0, 0); // next full hour
                seg = 0;
            }
            else if (id.StartsWith("seg")) seg = id[3] - '0';
            else if (id == "up") Adjust(1, false);
            else if (id == "down") Adjust(-1, false);
            else if (id == "cleartime") { Time = null; Remind = false; }
            else if (id == "remind") { if (Time.HasValue) ShowRemindMenu(); return; }
            else if (id == "cancel") { closing = true; Close(); return; }
            else if (id == "done") { Commit(); return; }
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            BuildHits();
            int dir = e.Delta > 0 ? 1 : -1;
            string id = HitAt(e.Location);
            if (Time.HasValue && id != null && (id.StartsWith("seg") || id == "up" || id == "down"))
            {
                if (id.StartsWith("seg")) seg = id[3] - '0';
                Adjust(dir, true);
            }
            else if (e.Y >= weekTop && e.Y < gridBottom) month = month.AddMonths(-dir);
            Invalidate();
            base.OnMouseWheel(e);
        }

        void Adjust(int d, bool wheel)
        {
            if (!Time.HasValue) return;
            int h = Time.Value.Hours, m = Time.Value.Minutes;
            if (seg == 0)
            {
                if (h24) h = (h + d + 24) % 24;
                else
                {
                    bool pm = h >= 12;
                    int h12 = h % 12 == 0 ? 12 : h % 12;
                    h12 = ((h12 - 1 + d) % 12 + 12) % 12 + 1;
                    h = (h12 % 12) + (pm ? 12 : 0);
                }
            }
            else if (seg == 1)
            {
                if (wheel) m = d > 0 ? (m - m % 5 + 5) % 60 : (m % 5 == 0 ? (m + 55) % 60 : m - m % 5);
                else m = (m + d + 60) % 60;
            }
            else h = (h + 12) % 24;
            Time = new TimeSpan(h, m, 0);
            Invalidate();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { closing = true; Close(); return; }
            if (e.KeyCode == Keys.Enter) { if (withTime) Commit(); return; }
            if (!Time.HasValue) return;
            int maxSeg = h24 ? 1 : 2;
            if (e.KeyCode == Keys.Up) Adjust(1, false);
            else if (e.KeyCode == Keys.Down) Adjust(-1, false);
            else if (e.KeyCode == Keys.Right || e.KeyCode == Keys.Tab) { seg = Math.Min(maxSeg, seg + 1); typed = ""; }
            else if (e.KeyCode == Keys.Left) { seg = Math.Max(0, seg - 1); typed = ""; }
            else return;
            e.Handled = true;
            Invalidate();
        }

        protected override bool ProcessDialogKey(Keys keyData)
        {
            if (keyData == Keys.Tab && Time.HasValue) { OnKeyDown(new KeyEventArgs(keyData)); return true; }
            return base.ProcessDialogKey(keyData);
        }

        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            if (!Time.HasValue) return;
            char c = char.ToLowerInvariant(e.KeyChar);
            int h = Time.Value.Hours, m = Time.Value.Minutes;
            if (!h24 && (c == 'a' || c == 'p'))
            {
                if (c == 'a' && h >= 12) h -= 12;
                if (c == 'p' && h < 12) h += 12;
                Time = new TimeSpan(h, m, 0);
            }
            else if (char.IsDigit(c) && seg < 2)
            {
                typed += c;
                int v = int.Parse(typed);
                int max = seg == 0 ? (h24 ? 23 : 12) : 59;
                if (v > max || typed.Length > 2) { typed = c.ToString(); v = c - '0'; }
                if (seg == 0)
                {
                    if (h24) h = v;
                    else if (v >= 1) h = (v % 12) + (h >= 12 ? 12 : 0);
                }
                else m = v;
                Time = new TimeSpan(h, m, 0);
                if (typed.Length == 2 || v * 10 > max) { seg = seg == 0 ? 1 : (h24 ? 1 : 2); typed = ""; }
            }
            else return;
            e.Handled = true;
            Invalidate();
        }
    }

    // ------------------------------------------------------------------
    // Reminder notification (bottom-right corner, same style as the app)
    // ------------------------------------------------------------------
    class ReminderToast : Form
    {
        const int WS_EX_TOOLWINDOW = 0x80, WS_EX_TOPMOST = 0x8, WS_EX_NOACTIVATE = 0x08000000;
        const int WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3;

        static readonly List<ReminderToast> open = new List<ReminderToast>();

        readonly TaskItem task;
        readonly Theme t = Theme.Current();
        readonly float sc;
        readonly Font fSmall, fTitle, fBody, fBtn;
        readonly Rectangle doneRect, closeRect;
        string hover;

        int S(float v) { return (int)Math.Round(v * sc); }

        public static void Pop(TaskItem task, float sc)
        {
            var f = new ReminderToast(task, sc);
            open.Add(f);
            Restack();
            f.Show();
        }

        public static void PlaySound()
        {
            try
            {
                string media = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media");
                string wav = Path.Combine(media, "Windows Notify Calendar.wav");
                if (!File.Exists(wav)) wav = Path.Combine(media, "Windows Notify System Generic.wav");
                if (File.Exists(wav)) new System.Media.SoundPlayer(wav).Play();
                else System.Media.SystemSounds.Asterisk.Play();
            }
            catch { }
        }

        static void Restack()
        {
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            int y = wa.Bottom;
            foreach (ReminderToast f in open)
            {
                int margin = f.S(16);
                y -= margin + f.Height;
                f.Location = new Point(wa.Right - margin - f.Width, y);
            }
        }

        ReminderToast(TaskItem task, float scale)
        {
            this.task = task;
            sc = scale;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
            BackColor = t.Bg;
            Opacity = 0.94;
            Text = "Work List reminder";

            fSmall = new Font("Segoe UI", 8.5f);
            fTitle = new Font("Segoe UI Semibold", 11.5f);
            fBody = new Font("Segoe UI", 9.5f);
            fBtn = new Font("Segoe UI Semibold", 9.5f);

            ClientSize = new Size(S(330), S(112));
            doneRect = new Rectangle(ClientSize.Width - S(16) - S(100), ClientSize.Height - S(14) - S(32), S(100), S(32));
            closeRect = new Rectangle(doneRect.X - S(8) - S(80), doneRect.Y, S(80), S(32));
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        // Don't steal focus from what you're typing.
        protected override bool ShowWithoutActivation { get { return true; } }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_MOUSEACTIVATE) { m.Result = (IntPtr)MA_NOACTIVATE; return; }
            base.WndProc(ref m);
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Native.RoundCorners(Handle, false); }

        string WhenText()
        {
            DateTime? at = App.DueAt(task);
            if (!at.HasValue) return "Due now";
            double minutesLeft = (at.Value - DateTime.Now).TotalMinutes;
            if (minutesLeft >= 1) return "Due in " + App.Span((int)Math.Ceiling(minutesLeft)) + " · " + App.FriendlyDue(task);
            if (minutesLeft > -2) return "Due now · " + App.FormatTime(at.Value.TimeOfDay);
            return "Was due " + App.FriendlyDue(task);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(t.Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var left = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            var mid = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;

            using (var b = new SolidBrush(t.Accent)) g.FillEllipse(b, S(18), S(20), S(8), S(8));
            TextRenderer.DrawText(g, "Reminder  ·  " + WhenText(), fSmall, new Rectangle(S(34), S(14), ClientSize.Width - S(50), S(20)), t.Muted, left);
            TextRenderer.DrawText(g, task.Title ?? "", fTitle, new Rectangle(S(18), S(36), ClientSize.Width - S(36), S(26)), t.Text, left);

            if (hover == "close") Draw.FillRound(g, t.Hover, closeRect, S(8));
            TextRenderer.DrawText(g, "Dismiss", fBody, closeRect, t.Muted, mid);
            Draw.FillRound(g, hover == "done" ? Color.FromArgb(66, 92, 220) : t.Accent, doneRect, S(8));
            TextRenderer.DrawText(g, "Mark done", fBtn, doneRect, Color.White, mid);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            string h = doneRect.Contains(e.Location) ? "done" : closeRect.Contains(e.Location) ? "close" : null;
            if (h != hover) { hover = h; Cursor = h != null ? Cursors.Hand : Cursors.Default; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { hover = null; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (doneRect.Contains(e.Location)) { task.Done = true; App.Save(); CloseToast(); }
            else if (closeRect.Contains(e.Location)) CloseToast();
            base.OnMouseClick(e);
        }

        void CloseToast()
        {
            open.Remove(this);
            Close();
            Restack();
        }
    }

    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        [STAThread]
        static void Main()
        {
            bool isFirst;
            using (var mutex = new System.Threading.Mutex(true, "WorkListFloatingBall_SingleInstance", out isFirst))
            {
                if (!isFirst) return; // already running
                try { SetProcessDPIAware(); } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                App.Tasks = Store.Load();
                Application.Run(new Ball());
            }
        }
    }
}
