using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace DeadBand
{
    /// <summary>
    /// 预留区窗口：注册成 AppBar 占住桌面底部 N 像素，
    /// 把内容提供者画进去，再把坏线所在的行清空。
    /// </summary>
    internal sealed class ZoneHost : Form
    {
        private const int WM_USER_CB = 0x0400 + 201;
        private const int WM_NCHITTEST = 0x0084;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int HTCLIENT = 1;
        private const int HTTRANSPARENT = -1;

        /// <summary>
        /// WM_NCHITTEST 的 lParam 是**屏幕坐标**，要转成客户区。
        /// （注意 WM_LBUTTONDOWN 的 lParam 本来就是客户区坐标，别再转一次。）
        /// </summary>
        private Point ScreenToClientPoint(IntPtr lParam)
        {
            long lp = lParam.ToInt64();
            int sx = unchecked((short)(lp & 0xFFFF));
            int sy = unchecked((short)((lp >> 16) & 0xFFFF));
            try { return PointToClient(new Point(sx, sy)); }
            catch { return new Point(-1, -1); }
        }

        /// <summary>WM_LBUTTONDOWN / MOUSEMOVE 的 lParam 直接就是客户区坐标。</summary>
        private static Point ClientPoint(IntPtr lParam)
        {
            long lp = lParam.ToInt64();
            int x = unchecked((short)(lp & 0xFFFF));
            int y = unchecked((short)((lp >> 16) & 0xFFFF));
            return new Point(x, y);
        }

        private ZoneProfile profile;

        // 四层内容各自一个实例。谁能画、画在哪、多透明，由 ComposeLayers 决定。
        private readonly ClockProvider clockLayer = new ClockProvider();
        private readonly SpectrumProvider specLayer = new SpectrumProvider();
        private readonly CaptionProvider capLayer = new CaptionProvider();
        private readonly PomodoroProvider pomoLayer = new PomodoroProvider();

        private bool specActive;
        private DateTime lastAudioAt = DateTime.MinValue;
        private DateTime lastCaptionAt = DateTime.MinValue;

        /// <summary>
        /// 预览模式：用合成的频谱和示例字幕代替真实内容。
        /// 调「变淡到」「叠加规则」这类只在有内容时才看得出来的设置时，
        /// 没声音没字幕就等于盲调 —— 打开这个就能一边调一边看。
        /// </summary>
        public bool PreviewMode;

        /// <summary>全局热键被按下时回调给 App。区带隐藏时窗口句柄仍在，消息照收。</summary>
        public Action HotkeyPressed;

        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly Timer tick;
        private Bitmap backbuffer;
        private bool appBarRegistered;
        private bool placing;
        private bool suspended;

        public ZoneHost()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Color.Black;
            Text = "DeadBand";
            Cursor = Cursors.Hand;    // 只有可点区域才会用到（其余地方穿透）
            Bounds = new Rectangle(0, 0, 200, 100);
            Visible = false;

            tick = new Timer();
            tick.Interval = 33;
            tick.Tick += new EventHandler(OnTick);
            tick.Start();
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x80;          // WS_EX_TOOLWINDOW
                cp.ExStyle |= 0x08000000;    // WS_EX_NOACTIVATE
                return cp;
            }
        }

        /// <summary>
        /// 把自己重新推到顶层窗口栈的最上面。
        ///
        /// 全屏窗口自身也是 topmost，顶层栈内部按激活顺序排，
        /// 所以光在构造时设一次 TopMost 不够 —— 全屏窗口一激活就压到我们头上。
        /// 这里只重排 z 序：不改位置、不改尺寸、不抢焦点。
        /// </summary>
        /// <summary>
        /// 有没有我们自己的对话框开着。
        /// 开着的时候别抢顶层 —— 否则设置窗口压到底部会被区带盖住按钮。
        /// </summary>
        public static bool AnyDialogOpen()
        {
            try
            {
                FormCollection forms = Application.OpenForms;
                for (int i = 0; i < forms.Count; i++)
                {
                    Form f = forms[i];
                    if (f == null || !f.Visible) continue;
                    if (f is ZoneHost) continue;
                    return true;
                }
            }
            catch { }
            return false;
        }

        public void ReassertTopmost()
        {
            try
            {
                if (!IsHandleCreated || !Visible) return;
                if (AnyDialogOpen()) return;

                // 全部层都关掉时，区带只是一块背景板，用户要往上面拖第三方桌面歌词。
                // 这时候还抢顶层就把人家盖住了 —— 干脆让出去，
                // 靠 AppBar 占位仍然能保证普通窗口不进来。
                if (!AnyLayerEnabled(profile)) return;

                Win32.SetWindowPos(Handle, Win32.HWND_TOPMOST, 0, 0, 0, 0,
                    Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
            }
            catch { }
        }

        public ZoneProfile Profile { get { return profile; } }
        public bool IsSuspended { get { return suspended; } }

        /// <summary>
        /// 预留区底边离屏幕底边还有多少像素。
        /// 0 = 已经贴住屏幕最底边（理想状态）；&gt;0 说明底部被别的东西占了（通常是任务栏），
        /// 此时标定时看到的预览位置和实际落位会不一致，需要提醒用户。
        /// </summary>
        public int BottomGap { get; private set; }

        // ---------------- 应用配置 ----------------
        public void Apply(ZoneProfile p)
        {
            profile = p;

            // 全部层关掉时不占顶层，把位置让给用户自己的桌面歌词
            bool wantTop = AnyLayerEnabled(p);
            if (TopMost != wantTop) { try { TopMost = wantTop; } catch { } }

            SyncSpectrum();

            if (!suspended)
            {
                EnsureRegistered();
                Place();
                Invalidate();
            }
        }

        private static bool AnyLayerEnabled(ZoneProfile p)
        {
            if (p == null) return false;
            if (p.ClockEnabled || p.SpectrumEnabled || p.CaptionEnabled) return true;
            string m = p.PomodoroMode == null ? "" : p.PomodoroMode.Trim().ToLowerInvariant();
            return m == "exclusive" || m == "coexist";
        }

        /// <summary>频谱要采集系统声音，只有它开着且没挂起才让采集线程跑。</summary>
        private void SyncSpectrum()
        {
            bool want = profile != null && profile.SpectrumEnabled && !suspended;
            if (want == specActive) return;
            specActive = want;
            try
            {
                if (want) specLayer.Activate();
                else specLayer.Deactivate();
            }
            catch { }
        }

        private static string ModeOf(ZoneProfile p)
        {
            if (p == null || p.PomodoroMode == null) return "off";
            return p.PomodoroMode.Trim().ToLowerInvariant();
        }

        /// <summary>当前接管鼠标的那一层（现在只有番茄钟要鼠标）。</summary>
        private IProviderInput CurrentInput()
        {
            if (profile == null) return null;
            string m = ModeOf(profile);
            if (m == "exclusive" || m == "coexist") return pomoLayer;
            return null;
        }

        public Screen TargetScreen()
        {
            if (profile != null)
            {
                // 1) 设备名优先：两块同分辨率的屏靠分辨率是分不开的
                if (!string.IsNullOrEmpty(profile.ScreenDevice))
                {
                    foreach (Screen s in Screen.AllScreens)
                        if (string.Equals(s.DeviceName, profile.ScreenDevice,
                                          StringComparison.OrdinalIgnoreCase)) return s;
                }
                // 2) 退回按分辨率找（老配置里没有 screendevice）
                foreach (Screen s in Screen.AllScreens)
                {
                    Rectangle b = s.Bounds;
                    if (b.Width == profile.ScreenWidth && b.Height == profile.ScreenHeight) return s;
                }
            }
            return Screen.PrimaryScreen;
        }

        // ---------------- 挂起 / 恢复 ----------------
        public void Suspend()
        {
            if (suspended) return;
            suspended = true;
            SyncSpectrum();          // 挂起时把采集线程停掉
            UnregisterAppBar();
            Hide();
        }

        public void Resume()
        {
            if (!suspended) return;
            suspended = false;
            if (profile == null) return;
            SyncSpectrum();
            EnsureRegistered();
            Place();
            Show();
            Invalidate();
        }

        // ---------------- AppBar ----------------
        private Win32.APPBARDATA NewData()
        {
            var d = new Win32.APPBARDATA();
            d.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Win32.APPBARDATA));
            d.hWnd = Handle;
            d.uCallbackMessage = WM_USER_CB;
            return d;
        }

        private void EnsureRegistered()
        {
            if (appBarRegistered) return;
            IntPtr h = Handle;          // 访问 Handle 会强制创建窗口句柄
            if (h == IntPtr.Zero) return;
            var d = NewData();
            Win32.SHAppBarMessage(Win32.ABM_NEW, ref d);
            appBarRegistered = true;
        }

        private void UnregisterAppBar()
        {
            if (!appBarRegistered) return;
            var d = NewData();
            Win32.SHAppBarMessage(Win32.ABM_REMOVE, ref d);
            appBarRegistered = false;
        }

        private void Place()
        {
            if (placing || !appBarRegistered || profile == null) return;
            placing = true;
            try
            {
                Screen scr = TargetScreen();
                Rectangle b = scr.Bounds;
                int h = profile.ZoneHeight;
                if (h < 1) h = 1;
                if (h > b.Height - 1) h = b.Height - 1;

                var d = NewData();
                d.uEdge = Win32.ABE_BOTTOM;
                d.rc.left = b.Left;
                d.rc.right = b.Right;
                d.rc.bottom = b.Bottom;
                d.rc.top = b.Bottom - h;
                Win32.SHAppBarMessage(Win32.ABM_QUERYPOS, ref d);
                d.rc.top = d.rc.bottom - h;
                Win32.SHAppBarMessage(Win32.ABM_SETPOS, ref d);

                Bounds = Rectangle.FromLTRB(d.rc.left, d.rc.top, d.rc.right, d.rc.bottom);
                BottomGap = b.Bottom - Bounds.Bottom;
                if (!Visible) Show();
            }
            finally { placing = false; }
        }

        protected override void WndProc(ref Message m)
        {
            // ---- 鼠标命中测试 ----
            // 只有内容自己声明的可点区域返回 HTCLIENT，其余一律 HTTRANSPARENT。
            // 这样区带能点按钮，但从旁边空白处点过去的事件会穿透到下面的窗口，
            // 不会把屏幕底部这 99px 全占了。
            if (m.Msg == WM_NCHITTEST)
            {
                IProviderInput input = CurrentInput();
                if (input != null && Visible && !suspended)
                {
                    Point pt = ScreenToClientPoint(m.LParam);
                    bool hit = input.HitTest(pt.X, pt.Y);
                    m.Result = hit ? (IntPtr)HTCLIENT : (IntPtr)HTTRANSPARENT;
                    return;
                }
            }
            else if (m.Msg == WM_LBUTTONDOWN)
            {
                IProviderInput input = CurrentInput();
                if (input != null && Visible && !suspended)
                {
                    Point pt = ClientPoint(m.LParam);
                    if (input.HitTest(pt.X, pt.Y))
                    {
                        input.OnClick(pt.X, pt.Y);
                        Invalidate();
                        return;         // 吞掉，别再往下传
                    }
                }
            }

            if (m.Msg == Hotkey.WM_HOTKEY && m.WParam.ToInt64() == Hotkey.Id)
            {
                if (HotkeyPressed != null) HotkeyPressed();
                return;
            }

            if (m.Msg == WM_USER_CB)
            {
                uint code = (uint)m.WParam.ToInt64();
                if (code == Win32.ABN_POSCHANGED || code == Win32.ABN_WINDOWARRANGE)
                {
                    Place();
                }
                else if (code == Win32.ABN_FULLSCREENAPP)
                {
                    // 有全屏应用出现/消失时 Windows 会通知 AppBar。
                    // 系统给 AppBar 的默认语义是"让开"，但我们要的是**留下**：
                    // 区带本来就是一块坏掉的像素，让开就白留了。
                    // 所以这里绝不 Hide，只保证可见 + 位置正确 + 压回顶层。
                    if (!suspended)
                    {
                        if (!Visible) Show();
                        Place();
                    }
                    ReassertTopmost();
                }
                return;
            }
            base.WndProc(ref m);
        }

        /// <summary>退出前把占用资源的内容关掉（例如音频采集线程）。</summary>
        public void DeactivateProvider()
        {
            specActive = false;
            try { specLayer.Deactivate(); } catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            DeactivateProvider();
            tick.Stop();
            UnregisterAppBar();
            base.OnFormClosing(e);
        }

        // ---------------- 渲染 ----------------
        private void OnTick(object sender, EventArgs e)
        {
            if (!Visible || suspended || profile == null) return;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            int w = ClientSize.Width;
            int h = ClientSize.Height;
            if (w <= 0 || h <= 0 || profile == null) return;

            if (backbuffer == null || backbuffer.Width != w || backbuffer.Height != h)
            {
                if (backbuffer != null) backbuffer.Dispose();
                backbuffer = new Bitmap(w, h);
            }

            using (Graphics g = Graphics.FromImage(backbuffer))
            {
                g.SmoothingMode = SmoothingMode.None;
                g.InterpolationMode = InterpolationMode.NearestNeighbor;

                // 背景层：底色 + 竖向网格线。内容提供者只负责画内容，不再自己清背景。
                using (var bg = new SolidBrush(profile.BaseColor))
                    g.FillRectangle(bg, 0, 0, w, h);

                // 悬停高亮：命中测试是穿透的，窗口收不到鼠标移动消息，
                // 所以直接读系统光标位置来算，跟穿透互不影响。
                IProviderInput input = CurrentInput();
                if (input != null)
                {
                    Point mp;
                    try { mp = PointToClient(System.Windows.Forms.Cursor.Position); }
                    catch { mp = new Point(-1, -1); }
                    if (mp.X < 0 || mp.Y < 0 || mp.X >= w || mp.Y >= h) mp = new Point(-1, -1);
                    input.SetHover(mp.X, mp.Y);
                }

                try { ComposeLayers(g, w, h); }
                catch { }

                // 竖线画在内容**上面**：数字连续几格的地方也能看见网格，
                // 整块图和坏线（面板侧横线）才是一张完整的栅格。
                if (profile.GridEnabled)
                    DrawVerticalLines(g, w, profile, 0, h);

                // 把坏线所在的行清空 —— 让空隙正好落在坏线上
                if (profile.Lines.Enabled && profile.LineBlank)
                {
                    Screen scr = TargetScreen();
                    // 必须用窗口真实位置：AppBar 会被系统顶到任务栏上方，
                    // 不等于「屏幕底边减去高度」。
                    int top = PointToScreen(Point.Empty).Y;
                    using (var gap = new SolidBrush(profile.BaseColor))
                    {
                        for (int y = 0; y < h; y++)
                        {
                            int screenY = top + y;
                            int distFromBottom = scr.Bounds.Bottom - 1 - screenY;
                            if (distFromBottom < 0) continue;
                            if (profile.Lines.IsBad(distFromBottom))
                            {
                                g.FillRectangle(gap, 0, y, w, 1);
                                // 坏行上的竖线要补回来，否则栅格会在这些行断掉
                                if (profile.GridEnabled)
                                    DrawVerticalLines(g, w, profile, y, y + 1);
                            }
                        }
                    }
                }
            }

            e.Graphics.DrawImageUnscaled(backbuffer, 0, 0);
        }

        /// <summary>
        /// 分层合成。
        ///
        /// 顺序（从下到上）：
        ///   底色 → 番茄钟水位（仅共存） → 频谱 → 时钟 → 字幕 → 番茄钟控件 → 网格 → 坏线挖空
        ///
        /// 可见性规则：
        ///   时钟       = 开着，且没被"有音频 / 有字幕"的规则藏掉
        ///   频谱       = 开着；时钟也显示时压到 40% 透明度垫在下面
        ///   字幕       = 开着且有文本；时钟也显示时宽度收窄
        ///   番茄钟独占 = 上面三层全部不画
        /// </summary>
        private void ComposeLayers(Graphics g, int w, int h)
        {
            ZoneProfile p = profile;
            double t = clock.Elapsed.TotalSeconds;
            DateTime now = DateTime.UtcNow;

            string mode = ModeOf(p);
            pomoLayer.Coexist = (mode == "coexist");

            if (mode == "exclusive")
            {
                pomoLayer.Render(g, w, h, p, t);
                return;
            }

            // ---- 有没有内容：带迟滞，别让句子之间的空隙把时钟拽来拽去 ----
            if (specActive && specLayer.HasSignal) lastAudioAt = now;

            bool capHasText = p.CaptionEnabled &&
                              (CaptionFeed.Text.Length > 0 || CaptionFeed.Sub.Length > 0);
            if (capHasText) lastCaptionAt = now;

            bool audioLive = PreviewMode ||
                             (specActive && (now - lastAudioAt).TotalSeconds < 1.5);
            bool capLive = capHasText || (now - lastCaptionAt).TotalSeconds < 2.5;

            bool hideClockForAudio = p.ClockEnabled && audioLive &&
                string.Equals(p.SpectrumWithClock == null ? "" : p.SpectrumWithClock,
                              "hideclock", StringComparison.OrdinalIgnoreCase);
            bool hideClockForCaption = p.ClockEnabled && capLive &&
                string.Equals(p.ClockWithCaption == null ? "" : p.ClockWithCaption,
                              "hide", StringComparison.OrdinalIgnoreCase);
            bool clockVisible = p.ClockEnabled && !hideClockForAudio && !hideClockForCaption;

            // ---- 共存时给番茄钟的按钮让出左右两边 ----
            int leftPad = 0, rightPad = 0;
            if (pomoLayer.Coexist)
            {
                leftPad = pomoLayer.ReservedLeft(w, h, p) + 16;
                rightPad = pomoLayer.ReservedRight(w, h);
            }
            if (leftPad < 0) leftPad = 0;
            if (rightPad < 0) rightPad = 0;
            if (leftPad > w / 2) leftPad = w / 2;
            if (leftPad + rightPad > w - 160) rightPad = Math.Max(0, w - 160 - leftPad);
            Rectangle free = Rectangle.FromLTRB(leftPad, 0, w - rightPad, h);

            // ---- 1. 番茄钟水位（共存时当背景）----
            if (pomoLayer.Coexist) pomoLayer.RenderWater(g, w, h, p);

            // ---- 2. 频谱 ----
            if (p.SpectrumEnabled)
            {
                // 上面只要有东西压着（时钟或字幕），频谱就降到用户设定的不透明度
                bool somethingOnTop = clockVisible || capHasText;
                int dimPct = p.SpectrumDim;
                if (dimPct < 5) dimPct = 5;
                if (dimPct > 100) dimPct = 100;
                specLayer.Dim = somethingOnTop ? (dimPct / 100f) : 1.0f;
                GraphicsState st = g.Save();
                try
                {
                    g.SetClip(free);
                    if (PreviewMode) DrawPreviewSpectrum(g, free, specLayer.Dim, t);
                    else specLayer.Render(g, w, h, p, t);
                }
                finally { g.Restore(st); }
            }

            // ---- 3. 时钟（有字幕时靠右）----
            int clockW = 0;
            if (clockVisible)
            {
                clockW = clockLayer.NaturalWidth(w, h, p);
                bool alignRight = capLive &&
                    string.Equals(p.ClockWithCaption == null ? "" : p.ClockWithCaption,
                                  "right", StringComparison.OrdinalIgnoreCase);

                double targetCenter = alignRight
                    ? free.Right - 24 - clockW / 2.0
                    : free.Left + free.Width / 2.0;

                // ClockProvider 是相对 w 居中的，用偏移量把它推到目标位置
                ZoneProfile shifted = p.Clone();
                shifted.ClockOffsetX = p.ClockOffsetX + (int)Math.Round(targetCenter - w / 2.0);

                GraphicsState st = g.Save();
                try
                {
                    g.SetClip(free);
                    clockLayer.Render(g, w, h, shifted, t);
                }
                finally { g.Restore(st); }
            }

            // ---- 4. 字幕（有时钟就让出右边）----
            if (capHasText)
            {
                Rectangle cr = free;
                if (clockVisible)
                {
                    int rightLimit = free.Right - 24 - clockW - 24;
                    cr.Width = Math.Max(240, rightLimit - free.Left);
                }

                GraphicsState st = g.Save();
                try
                {
                    g.SetClip(cr);
                    g.TranslateTransform(cr.X, 0);
                    capLayer.Render(g, cr.Width, h, p, t);
                }
                finally { g.Restore(st); }
            }

            // ---- 5. 番茄钟控件 ----
            if (pomoLayer.Coexist) pomoLayer.RenderControls(g, w, h, p);
        }

        /// <summary>
        /// 预览用的合成频谱：一组随正弦摆动的竖条。
        /// 不接真实音频，只是让「变淡到」这类设置能当场看出效果。
        /// </summary>
        private void DrawPreviewSpectrum(Graphics g, Rectangle area, float dim, double t)
        {
            int h = area.Height;
            if (h <= 0 || area.Width <= 0) return;

            int cell = (profile != null && profile.GridEnabled) ? profile.GridCell : 4;
            if (cell < 2) cell = 4;

            const int bars = 96;
            int pitch = Math.Max(3, area.Width / bars);
            int barW = Math.Max(1, pitch - Math.Max(1, pitch / 4));

            int alpha = (int)Math.Round(255 * dim);
            if (alpha < 0) alpha = 0;
            if (alpha > 255) alpha = 255;

            for (int i = 0; i < bars; i++)
            {
                double u = i / (double)bars;
                double s1 = Math.Sin(u * 21.0 + t * 2.1);
                double s2 = Math.Sin(u * 5.3 - t * 0.9);
                double v = 0.55 + 0.42 * s1 * s2;
                if (v < 0.08) v = 0.08;
                if (v > 1.0) v = 1.0;

                int bh = (int)Math.Round(v * h / cell) * cell;
                if (bh < cell) bh = cell;
                if (bh > h) bh = h;

                int hue = (int)(190 + 150 * u);
                if (hue > 360) hue -= 360;
                Color c = HsvToRgb(hue, 0.72f, 0.95f);

                using (var b = new SolidBrush(Color.FromArgb(alpha, c)))
                    g.FillRectangle(b, area.Left + i * pitch, area.Top + (h - bh), barW, bh);
            }
        }

        private static Color HsvToRgb(double h, double s, double v)
        {
            double c = v * s;
            double x = c * (1 - Math.Abs(((h / 60.0) % 2) - 1));
            double m = v - c;
            double r = 0, gg = 0, bb = 0;
            if (h < 60) { r = c; gg = x; }
            else if (h < 120) { r = x; gg = c; }
            else if (h < 180) { gg = c; bb = x; }
            else if (h < 240) { gg = x; bb = c; }
            else if (h < 300) { r = x; bb = c; }
            else { r = c; bb = x; }
            return Color.FromArgb(
                Clamp255((int)Math.Round((r + m) * 255)),
                Clamp255((int)Math.Round((gg + m) * 255)),
                Clamp255((int)Math.Round((bb + m) * 255)));
        }

        private static int Clamp255(int v)
        {
            if (v < 0) return 0;
            if (v > 255) return 255;
            return v;
        }

        /// <summary>画一组竖线，行范围 [y0, y1)。</summary>
        private static void DrawVerticalLines(Graphics g, int w, ZoneProfile p, int y0, int y1)
        {
            int sp = p.GridCell;
            if (sp < 1) sp = 1;
            int th = p.GridThickness;
            if (th < 1) th = 1;
            if (y1 <= y0) return;
            using (var b = new SolidBrush(p.GridColor))
            {
                for (int x = 0; x < w; x += sp)
                {
                    int ww = th;
                    if (x + ww > w) ww = w - x;
                    if (ww <= 0) break;
                    g.FillRectangle(b, x, y0, ww, y1 - y0);
                }
            }
        }
    }
}
