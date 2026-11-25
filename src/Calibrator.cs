using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DeadBand
{
    /// <summary>
    /// 标定界面：整屏不透明纯色底（可换色，方便看清坏线），
    /// 洋红线实时标出系统认为的坏线位置，对着屏幕真实的灰线调到位。
    /// </summary>
    internal sealed class Calibrator : Form
    {
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr pid);
        [DllImport("user32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        private static readonly string[] ParamNames =
            { "预留区高度", "坏线间距", "坏线相位", "坏线粗细", "网格间距", "网格粗细" };

        // 纯色底调色板：灰色 / 白 / 黑 / 三原色 / 近黑
        private static readonly Color[] Palette = new Color[]
        {
            Color.FromArgb(0x80, 0x80, 0x80),
            Color.FromArgb(0xFF, 0xFF, 0xFF),
            Color.FromArgb(0x00, 0x00, 0x00),
            Color.FromArgb(0xFF, 0x00, 0x00),
            Color.FromArgb(0x00, 0xFF, 0x00),
            Color.FromArgb(0x00, 0x00, 0xFF),
            Color.FromArgb(0x00, 0xFF, 0xFF),
            Color.FromArgb(0xFF, 0x00, 0xFF),
            Color.FromArgb(0xFF, 0xFF, 0x00),
            Color.FromArgb(0x24, 0x24, 0x24)
        };

        private readonly ZoneProfile work;
        private readonly IProvider provider;
        private readonly Screen screen;
        private readonly Timer tick = new Timer();

        private int editIndex;
        // 默认白底：坏线通常比白色暗，白底上灰线比中灰底更清楚
        private int bgIndex = 1;
        private bool showPreview;          // 默认关：先看清真实坏线
        private bool showMarkers = true;
        private bool dragging;

        public bool Accepted;
        public ZoneProfile Result { get { return work; } }

        public Calibrator(ZoneProfile profile, Screen target)
        {
            work = profile.Clone();
            screen = target;
            provider = ProviderRegistry.Get(work.Provider);

            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = target.Bounds;
            TopMost = true;
            ShowInTaskbar = true;
            BackColor = Palette[bgIndex];
            Opacity = 1.0;
            Text = "DeadBand 标定";
            KeyPreview = true;

            tick.Interval = 33;
            tick.Tick += new EventHandler(delegate { Invalidate(); });
            tick.Start();
        }

        private int ZoneTopLocal { get { return ClientSize.Height - work.ZoneHeight; } }
        private Color BackgroundColor { get { return Palette[bgIndex]; } }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            tick.Stop();
            tick.Dispose();
            base.OnFormClosed(e);
        }

        // ---------------- 键盘 ----------------
        protected override void OnKeyDown(KeyEventArgs e)
        {
            int step = 1;
            if ((e.Modifiers & Keys.Shift) == Keys.Shift) step = 10;
            else if ((e.Modifiers & Keys.Control) == Keys.Control) step = 5;

            switch (e.KeyCode)
            {
                case Keys.Escape:
                    Accepted = false;
                    Close();
                    e.Handled = true;
                    return;
                case Keys.Enter:
                    Accepted = true;
                    Close();
                    e.Handled = true;
                    return;
                case Keys.Tab:
                    if ((e.Modifiers & Keys.Shift) == Keys.Shift)
                        editIndex = (editIndex + ParamNames.Length - 1) % ParamNames.Length;
                    else
                        editIndex = (editIndex + 1) % ParamNames.Length;
                    Invalidate();
                    e.Handled = true;
                    return;
                case Keys.C:
                    bgIndex = (bgIndex + 1) % Palette.Length;
                    Invalidate();
                    e.Handled = true;
                    return;
                case Keys.P:
                    showPreview = !showPreview;
                    Invalidate();
                    e.Handled = true;
                    return;
                case Keys.M:
                    showMarkers = !showMarkers;
                    Invalidate();
                    e.Handled = true;
                    return;
                case Keys.G:
                    work.GridEnabled = !work.GridEnabled;
                    Invalidate();
                    e.Handled = true;
                    return;
                case Keys.Oemcomma:
                    work.GridEnabled = true;
                    work.GridColor = ShiftColor(work.GridColor, -8);
                    Invalidate();
                    e.Handled = true;
                    return;
                case Keys.OemPeriod:
                    work.GridEnabled = true;
                    work.GridColor = ShiftColor(work.GridColor, +8);
                    Invalidate();
                    e.Handled = true;
                    return;
                case Keys.Up:
                    Adjust(+step);
                    e.Handled = true;
                    return;
                case Keys.Down:
                    Adjust(-step);
                    e.Handled = true;
                    return;
            }
            base.OnKeyDown(e);
        }

        private void Adjust(int delta)
        {
            switch (editIndex)
            {
                case 0:
                    work.ZoneHeight = Clamp(work.ZoneHeight + delta, 1, ClientSize.Height - 60);
                    break;
                case 1:
                    work.Lines.Enabled = true;
                    work.Lines.Spacing = Clamp(work.Lines.Spacing + delta, 2, 200);
                    break;
                case 2:
                    work.Lines.Enabled = true;
                    work.Lines.Offset = Clamp(work.Lines.Offset + delta, -2000, 2000);
                    break;
                case 3:
                    work.Lines.Enabled = true;
                    work.Lines.Thickness = Clamp(work.Lines.Thickness + delta, 1, 40);
                    break;
                case 4:
                    work.GridEnabled = true;
                    work.GridSpacing = Clamp(work.GridSpacing + delta, 2, 64);
                    break;
                case 5:
                    work.GridEnabled = true;
                    work.GridThickness = Clamp(work.GridThickness + delta, 1, 12);
                    break;

            }
            Invalidate();
        }

        private static int Clamp(int v, int lo, int hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            Adjust(e.Delta > 0 ? 1 : -1);
        }

        // ---------------- 拖拽 ----------------
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (Form.ActiveForm != this) ForceForeground();
            if (Math.Abs(e.Y - ZoneTopLocal) <= 18) dragging = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging)
            {
                work.ZoneHeight = Clamp(ClientSize.Height - e.Y, 1, ClientSize.Height - 60);
                editIndex = 0;
                Invalidate();
            }
            else
            {
                Cursor = Math.Abs(e.Y - ZoneTopLocal) <= 18 ? Cursors.SizeNS : Cursors.Default;
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            dragging = false;
        }

        // ---------------- 绘制 ----------------
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.None;

            int w = ClientSize.Width;
            int h = ClientSize.Height;
            int zoneH = work.ZoneHeight;
            int zoneTop = h - zoneH;

            // 1) 整屏不透明纯色底 —— 坏线在这种背景上最清楚
            using (var bg = new SolidBrush(BackgroundColor))
                g.FillRectangle(bg, 0, 0, w, h);

            // 2) 预留区：底色 + 竖向网格 +（可选）内容
            if (zoneH > 0)
            {
                using (var buf = new Bitmap(w, zoneH))
                {
                    using (Graphics bgr = Graphics.FromImage(buf))
                    {
                        bgr.SmoothingMode = SmoothingMode.None;
                        using (var b0 = new SolidBrush(work.BaseColor))
                            bgr.FillRectangle(b0, 0, 0, w, zoneH);
                        if (work.GridEnabled) DrawGrid(bgr, w, zoneH);
                        if (showPreview)
                        {
                            try { provider.Render(bgr, w, zoneH, work, DateTime.Now.TimeOfDay.TotalSeconds); }
                            catch { }
                        }
                    }
                    g.DrawImageUnscaled(buf, 0, zoneTop);
                }
            }

            // 3) 坏线标记：始终独立绘制，和预览开关无关，
            //    这样关掉预览就能拿纯色底 + 洋红线去对齐真实灰线
            if (work.Lines.Enabled && showMarkers && zoneH > 0)
            {
                using (var mg = new SolidBrush(Color.FromArgb(255, 255, 0, 150)))
                {
                    for (int y = 0; y < zoneH; y++)
                    {
                        int distFromBottom = zoneH - 1 - y;
                        if (work.Lines.IsBad(distFromBottom))
                            g.FillRectangle(mg, 0, zoneTop + y, w, 1);
                    }
                }
            }

            // 4) 区域上边界线 + 两端抓手
            using (var pen = new Pen(Color.FromArgb(255, 0, 216, 255), 2f))
                g.DrawLine(pen, 0, zoneTop, w, zoneTop);
            using (var br = new SolidBrush(Color.FromArgb(255, 0, 216, 255)))
            {
                g.FillRectangle(br, 0, zoneTop - 9, 110, 18);
                g.FillRectangle(br, w - 110, zoneTop - 9, 110, 18);
            }

            // 5) 提示面板，跟着区域上沿走
            DrawHud(g, w, zoneTop);
        }

        /// <summary>整条网格色加/减一个量，用来对着屏幕真实的坏线灰度微调。</summary>
        private static Color ShiftColor(Color c, int delta)
        {
            int r = c.R + delta; if (r < 0) r = 0; if (r > 255) r = 255;
            int g = c.G + delta; if (g < 0) g = 0; if (g > 255) g = 255;
            int b = c.B + delta; if (b < 0) b = 0; if (b > 255) b = 255;
            return Color.FromArgb(r, g, b);
        }

        /// <summary>在预留区里画竖向网格，标定时也能看到最终效果。</summary>
        private void DrawGrid(Graphics g, int w, int h)
        {
            int sp = work.GridCell;
            if (sp < 1) sp = 1;
            int th = work.GridThickness;
            if (th < 1) th = 1;
            using (var b = new SolidBrush(work.GridColor))
            {
                for (int x = 0; x < w; x += sp)
                {
                    int ww = th;
                    if (x + ww > w) ww = w - x;
                    if (ww <= 0) break;
                    g.FillRectangle(b, x, 0, ww, h);
                }
            }
        }

        private string ValueText(int idx)
        {
            switch (idx)
            {
                case 0: return work.ZoneHeight + " px";
                case 1: return work.Lines.Spacing + " 行";
                case 2: return work.Lines.Offset.ToString();
                case 3: return work.Lines.Thickness + " 行";
                case 4: return work.GridSpacing + " px";
                default: return work.GridThickness + " px";
            }
        }

        /// <summary>标定的四个步骤 —— 顺序就是第一次标定时该走的顺序。</summary>
        private static readonly string[] StepNames =
            { "预留区高度", "坏线间距", "坏线相位", "坏线粗细" };
        private static readonly string[] StepMarks = { "①", "②", "③", "④" };

        /// <summary>当前 Tab 落在第几步。4/5（网格）不属于标定主线，返回 -1。</summary>
        private int CurrentStep
        {
            get { return editIndex <= 3 ? editIndex : -1; }
        }

        /// <summary>这一步具体要做什么。用户进来时最缺的就是这一句。</summary>
        private string NowText()
        {
            switch (editIndex)
            {
                case 0:
                    return "现在做：拖动顶部那条青色线，把带的顶边拖到最上面那条灰线的上方 —— 带太矮会漏掉更高的灰线。";
                case 1:
                    return "现在做：让洋红线的疏密和屏幕上的灰线一致（数一下相邻两条灰线之间隔几行）。";
                case 2:
                    return "现在做：用相位把洋红线移到正好压住灰线；间距对了却错开半格，就是相位还差一点。";
                case 3:
                    return "现在做：灰线如果比 1 行粗，把粗细调成相同行数；多数屏是 1 行，1 行就不用动。";
                default:
                    return "现在做（可选）：竖线间距与粗细 —— 竖线和横线同色同间距，才会连成一张栅格。";
            }
        }

        private string StepValue(int i)
        {
            switch (i)
            {
                case 0: return work.ZoneHeight + " px";
                case 1: return work.Lines.Spacing + " 行";
                case 2: return work.Lines.Offset.ToString();
                default: return work.Lines.Thickness + " 行";
            }
        }

        /// <summary>四步进度条：已走过的亮、当前项高亮、还没到的暗。</summary>
        private void DrawSteps(Graphics g, int x, int y, Font f,
                               Brush dim, Brush bright, Brush accent)
        {
            int now = CurrentStep;
            int cx = x;
            for (int i = 0; i < StepNames.Length; i++)
            {
                string txt = StepMarks[i] + " " + StepNames[i] + " " + StepValue(i);
                SizeF sz = g.MeasureString(txt, f);
                bool cur = (i == now);
                if (cur)
                {
                    using (var bg = new SolidBrush(Color.FromArgb(46, 0, 216, 255)))
                        g.FillRectangle(bg, cx - 6, y - 5, sz.Width + 12, sz.Height + 6);
                    g.DrawString(txt, f, accent, cx, y);
                    using (var bar = new SolidBrush(Color.FromArgb(255, 0, 216, 255)))
                        g.FillRectangle(bar, cx - 6, y + sz.Height + 1, sz.Width + 12, 2);
                }
                else
                {
                    g.DrawString(txt, f, (now >= 0 && i < now) ? bright : dim, cx, y);
                }
                cx += (int)sz.Width + 26;
            }
        }

        private void DrawHud(Graphics g, int w, int zoneTop)
        {
            int pad = 24;
            int panelW = Math.Min(1020, w - 80);
            int panelH = 372;                       // 按内容行数算出来的，别随手改小
            int x0 = (w - panelW) / 2;
            int y0 = zoneTop - panelH - 28;
            if (y0 < 20) y0 = 20;

            using (var panelBg = new SolidBrush(Color.FromArgb(250, 13, 16, 22)))
            {
                g.FillRectangle(panelBg, x0, y0, panelW, panelH);
            }
            using (var panelBorder = new Pen(Color.FromArgb(120, 0, 216, 255), 1.5f))
                g.DrawRectangle(panelBorder, x0, y0, panelW, panelH);

            using (var fTitle = new Font("Microsoft YaHei UI", 12f))
            using (var fBig = new Font("Microsoft YaHei UI", 27f, FontStyle.Bold))
            using (var fMid = new Font("Microsoft YaHei UI", 12f))
            using (var fSmall = new Font("Microsoft YaHei UI", 10.5f))
            using (var bDim = new SolidBrush(Color.FromArgb(235, 138, 156, 182)))
            using (var bBright = new SolidBrush(Color.FromArgb(255, 240, 246, 255)))
            using (var bAccent = new SolidBrush(Color.FromArgb(255, 0, 216, 255)))
            using (var bMagenta = new SolidBrush(Color.FromArgb(255, 255, 96, 196)))
            {
                int x = x0 + pad;
                int y = y0 + 14;

                g.DrawString("DeadBand 标定 —— 一共四步，做完按 Enter 保存", fTitle, bDim, x, y);
                y += 28;

                // 当前项：名字小、数值大
                g.DrawString(ParamNames[editIndex], fMid, bDim, x, y + 16);
                SizeF nameSz = g.MeasureString(ParamNames[editIndex], fMid);
                g.DrawString(ValueText(editIndex), fBig, bAccent, x + nameSz.Width + 16, y);
                y += 52;

                // 四步进度条（替代原来那行"其余三项"）
                DrawSteps(g, x, y, fMid, bDim, bBright, bAccent);
                y += 36;

                string gridLine = "网格 " + (work.GridEnabled ? "开" : "关") +
                                  "      间距 " + work.GridSpacing + " px" +
                                  "      粗细 " + work.GridThickness + " px" +
                                  "      网格色 #" + work.GridColor.R.ToString("X2") +
                                  work.GridColor.G.ToString("X2") + work.GridColor.B.ToString("X2") +
                                  "      G 键开启/关闭";
                g.DrawString(gridLine, fMid, work.GridEnabled ? bBright : bDim, x, y);
                y += 34;

                using (var line = new Pen(Color.FromArgb(70, 120, 140, 170), 1f))
                    g.DrawLine(line, x, y, x0 + panelW - pad, y);
                y += 12;

                g.DrawString("Tab 切换步骤      ↑↓ 调 1          Shift+↑↓ 调 10          Ctrl+↑↓ 调 5",
                    fSmall, bBright, x, y);
                y += 24;
                g.DrawString("拖顶部青色线也能调高度      滚轮微调当前项",
                    fSmall, bBright, x, y);
                y += 24;

                // 背景色 + 开关
                g.DrawString("C 换背景色", fSmall, bBright, x, y);
                SizeF cSz = g.MeasureString("C 换背景色", fSmall);
                int swX = x + (int)cSz.Width + 12;
                using (var sw = new SolidBrush(BackgroundColor))
                using (var swBorder = new Pen(Color.FromArgb(160, 200, 210, 230), 1f))
                {
                    g.FillRectangle(sw, swX, y, 22, 16);
                    g.DrawRectangle(swBorder, swX, y, 22, 16);
                }
                g.DrawString(string.Format("#{0:X2}{1:X2}{2:X2}", BackgroundColor.R, BackgroundColor.G, BackgroundColor.B),
                    fSmall, bDim, swX + 30, y);

                g.DrawString("P 预览 " + (showPreview ? "开" : "关") +
                             "      M 洋红线 " + (showMarkers ? "开" : "关") +
                             "      Enter 保存      Esc 取消",
                    fSmall, bBright, swX + 130, y);
                y += 30;

                // 这一步具体要干什么 —— 整块面板里最该被读到的一句
                using (var fmt = new StringFormat())
                {
                    fmt.FormatFlags = StringFormatFlags.LineLimit;
                    var area = new RectangleF(x, y, panelW - 2 * pad, panelH - (y - y0) - 12);
                    g.DrawString(NowText(), fMid, bMagenta, area, fmt);
                }
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            BringToFront();
            ForceForeground();
            var retry = new Timer();
            retry.Interval = 180;
            retry.Tick += new EventHandler(delegate
            {
                retry.Stop();
                retry.Dispose();
                if (Form.ActiveForm != this) ForceForeground();
            });
            retry.Start();
        }

        /// <summary>抢前台焦点：不抢到的话方向键会落到别的窗口去。</summary>
        private void ForceForeground()
        {
            try
            {
                IntPtr fg = GetForegroundWindow();
                uint fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, IntPtr.Zero);
                uint myThread = GetCurrentThreadId();
                bool attached = false;
                if (fgThread != 0 && fgThread != myThread)
                    attached = AttachThreadInput(myThread, fgThread, true);
                SetForegroundWindow(Handle);
                Activate();
                Focus();
                if (attached) AttachThreadInput(myThread, fgThread, false);
            }
            catch { }
        }
    }
}
