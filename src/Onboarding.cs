using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text;
using System.Windows.Forms;

namespace DeadBand
{
    /// <summary>
    /// 首次使用引导。八步：
    ///   欢迎 → 环境预检 → 标定 → 显示什么 → 怎么叠 → 长什么样 → 全屏与开机启动 → 完成
    ///
    /// 两个刻意的设计：
    ///  1) 预检是**硬门槛**。任务栏在底部时不让往下走 —— 否则区带会被顶到任务栏上方，
    ///     用户第一眼就是「位置对不上」，后面怎么调都是错。
    ///  2) 第 4~6 步自动打开预览模式。叠加规则和「变淡到」在没有声音没有字幕时
    ///     调了也看不见，等于盲调。
    ///
    /// 窗口本身不碰文件、不碰区带宿主，只改传进来的 profile 并回调，
    /// 由 App 负责保存、应用、以及退出程序。
    /// </summary>
    internal sealed class OnboardingForm : Form
    {
        // ---------------- 回调（全部由 App 提供） ----------------
        private readonly Action<ZoneProfile> apply;               // 立即保存并应用到区带
        private readonly Func<ZoneProfile> calibrate;             // 进全屏标定，成功返回新 profile
        private readonly Func<MonitorInfo, ZoneProfile> switchTo;  // 换目标显示器，返回它的配置
        private readonly Action showZone;                         // 让区带显示出来
        private readonly Action hideZone;                         // 把区带收起来
        private readonly Func<int> ownZoneHeight;                 // 区带当前占了多少 px（0 = 没显示）
        private readonly Action<int> onColor;
        private readonly Action onInstall;
        private readonly Action onSettings;
        private readonly Action onOpenDir;
        private readonly Action<bool> setPreview;

        private readonly MonitorInfo[] monitors;
        private ZoneProfile p;

        /// <summary>走完了（点了「完成」）。false 且窗口关了 = 用户中途退出。</summary>
        public bool Completed;

        private const int StepCount = 8;
        private int step;
        private bool previewOn;

        // 布局
        private Panel header, content, footer;
        private Panel[] pages = new Panel[StepCount];
        private Button btnBack, btnNext, btnCancel;

        // ② 预检
        private ComboBox cbMonitor;
        private Label[] preRows;
        private bool blocked;

        // ③ 标定
        private Label lblCalib;
        private bool calibrated;

        // ④ 显示什么
        private CheckBox cbClock, cbSpec, cbCap;
        private Panel capExtra;

        // ⑤ 怎么叠
        private ComboBox cbAudio, cbCapRule;
        private NumericUpDown numDim;
        private Label lbAudio, lbDim, lbDimUnit, lbCapRule;

        // ⑥ 外观
        private CheckBox cbGrid, cbFollow, cbBlank;

        // ⑦ 全屏与自启
        private ComboBox cbFs;
        private CheckBox cbAutostart;

        // ⑧ 完成
        private Label lblSummary;

        // ---------------- 配色 ----------------
        private static readonly Color Ink = Color.FromArgb(0x1E, 0x24, 0x2E);
        private static readonly Color Dim = Color.FromArgb(0x6B, 0x77, 0x88);
        private static readonly Color Ok = Color.FromArgb(0x1F, 0x8A, 0x4C);
        private static readonly Color Bad = Color.FromArgb(0xC0, 0x39, 0x2B);
        private static readonly Color Warn = Color.FromArgb(0xA8, 0x6A, 0x00);
        private static readonly Color Paper = Color.FromArgb(0xF6, 0xF7, 0xF9);
        private static readonly Color Line = Color.FromArgb(0xE0, 0xE3, 0xE8);

        private static readonly string[] Titles = new string[]
        {
            "欢迎使用 DeadBand",
            "环境预检",
            "标定坏线",
            "显示什么",
            "怎么叠",
            "长什么样",
            "全屏与开机启动",
            "完成"
        };

        private static readonly string[] Subs = new string[]
        {
            "先花两分钟把位置和内容定下来",
            "确认屏幕最底下这一条现在是空的",
            "让 DeadBand 的横线正好压住你屏上的真实灰线",
            "三层内容可以同时开",
            "多层同时有内容时谁让谁",
            "把坏线收编成栅格的一部分",
            "全屏视频怎么办、登录后要不要自动起",
            "随时可以在设置窗口里改"
        };

        private static readonly string[] AxisIds = { "fitlow", "fixed", "fitboth" };
        private static readonly string[] AudioIds = { "dim", "hideclock" };
        private static readonly string[] CapIds = { "right", "hide" };
        private static readonly string[] FsIds = { "avoid", "overlay" };

        public OnboardingForm(
            ZoneProfile profile,
            MonitorInfo[] monitors,
            Action<ZoneProfile> apply,
            Func<ZoneProfile> calibrate,
            Func<MonitorInfo, ZoneProfile> switchTo,
            Action showZone,
            Action hideZone,
            Func<int> ownZoneHeight,
            Action<int> onColor,
            Action onInstall,
            Action onSettings,
            Action onOpenDir,
            Action<bool> setPreview)
        {
            p = profile != null ? profile : new ZoneProfile();
            this.monitors = monitors != null ? monitors : new MonitorInfo[0];
            this.apply = apply;
            this.calibrate = calibrate;
            this.switchTo = switchTo;
            this.showZone = showZone;
            this.hideZone = hideZone;
            this.ownZoneHeight = ownZoneHeight;
            this.onColor = onColor;
            this.onInstall = onInstall;
            this.onSettings = onSettings;
            this.onOpenDir = onOpenDir;
            this.setPreview = setPreview;

            Text = "DeadBand 首次设置";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            ClientSize = new Size(720, 580);
            BackColor = Paper;
            Font = Yahei(9.5f);
            KeyPreview = true;

            BuildUi();
            EnterStep(0);
        }

        // =====================================================================
        //  布局骨架
        // =====================================================================
        private void BuildUi()
        {
            content = new Panel();
            content.Dock = DockStyle.Fill;
            content.BackColor = Paper;
            Controls.Add(content);

            footer = new Panel();
            footer.Dock = DockStyle.Bottom;
            footer.Height = 62;
            footer.BackColor = Color.FromArgb(0xFA, 0xFB, 0xFC);
            footer.Paint += delegate(object s, PaintEventArgs e)
            {
                using (var pen = new Pen(Line, 1f))
                    e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
            };
            Controls.Add(footer);

            header = new Panel();
            header.Dock = DockStyle.Top;
            header.Height = 84;
            header.Paint += delegate(object s, PaintEventArgs e) { PaintHeader(e.Graphics); };
            Controls.Add(header);

            for (int i = 0; i < StepCount; i++) pages[i] = NewPage();
            BuildWelcome();
            BuildPrecheck();
            BuildCalibrate();
            BuildContent();
            BuildOverlay();
            BuildLook();
            BuildStartup();
            BuildDone();

            // ---- 底部按钮 ----
            btnCancel = new Button();
            btnCancel.Text = "退出";
            btnCancel.Bounds = new Rectangle(26, 15, 88, 32);
            btnCancel.Click += delegate { Close(); };
            footer.Controls.Add(btnCancel);

            btnNext = new Button();
            btnNext.Text = "下一步";
            btnNext.Bounds = new Rectangle(720 - 26 - 104, 15, 104, 32);
            btnNext.FlatStyle = FlatStyle.Flat;
            btnNext.FlatAppearance.BorderColor = Color.FromArgb(0x00, 0x86, 0xA4);
            btnNext.BackColor = Color.FromArgb(0x00, 0xA2, 0xC4);
            btnNext.ForeColor = Color.White;
            btnNext.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
            btnNext.Click += delegate { GoNext(); };
            footer.Controls.Add(btnNext);

            btnBack = new Button();
            btnBack.Text = "上一步";
            btnBack.Bounds = new Rectangle(720 - 26 - 104 - 12 - 100, 15, 100, 32);
            btnBack.Click += delegate { GoBack(); };
            footer.Controls.Add(btnBack);
        }

        private Panel NewPage()
        {
            var pg = new Panel();
            pg.Dock = DockStyle.Fill;
            pg.BackColor = Paper;
            pg.Visible = false;
            content.Controls.Add(pg);
            return pg;
        }

        private int PageIndex(Panel pg)
        {
            for (int i = 0; i < pages.Length; i++) if (pages[i] == pg) return i;
            return -1;
        }


        // ---------------- 绘制 ----------------
        private void PaintHeader(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.None;
            using (var b = new SolidBrush(Color.FromArgb(0x0D, 0x10, 0x16)))
                g.FillRectangle(b, 0, 0, header.Width, header.Height);
            using (var b = new SolidBrush(Color.FromArgb(0x00, 0xD8, 0xFF)))
                g.FillRectangle(b, 0, 0, header.Width, 2);

            using (var f1 = new Font("Microsoft YaHei UI", 14.5f, FontStyle.Bold))
            using (var f2 = Yahei(9.5f))
            using (var f3 = new Font("Consolas", 9f))
            using (var b1 = new SolidBrush(Color.FromArgb(0xF0, 0xF6, 0xFF)))
            using (var b2 = new SolidBrush(Color.FromArgb(0x85, 0x97, 0xB1)))
            {
                g.DrawString(Titles[step], f1, b1, 26, 15);
                g.DrawString(Subs[step], f2, b2, 28, 51);

                string idx = (step + 1) + " / " + StepCount;
                SizeF sz = g.MeasureString(idx, f3);
                g.DrawString(idx, f3, b2, header.Width - 26 - sz.Width, 20);

                int bw = 13, gap = 5;
                int total = StepCount * bw + (StepCount - 1) * gap;
                int x0 = header.Width - 26 - total;
                for (int i = 0; i < StepCount; i++)
                {
                    Color c;
                    if (i < step) c = Color.FromArgb(0x2B, 0x6E, 0x8A);
                    else if (i == step) c = Color.FromArgb(0x00, 0xD8, 0xFF);
                    else c = Color.FromArgb(0x22, 0x28, 0x34);
                    using (var b = new SolidBrush(c))
                        g.FillRectangle(b, x0 + i * (bw + gap), 58, bw, 7);
                }
            }
        }

        // =====================================================================
        //  小工具
        // =====================================================================
        private static Font Yahei(float pt)
        {
            return new Font("Microsoft YaHei UI", pt);
        }

        /// <summary>
        /// 段落文字。用 AutoSize + MaximumSize 让控件自己算换行后的高度。
        ///
        /// 以前是手动 TextRenderer.MeasureText 算高度再赋给 Height —— 那条路和 Label
        /// 自己的排版（多了内边距、flag 不完全一致）会差一行，结果就是文字溢出、
        /// 被下面的控件压住，而我自己的自检也量不出这个差。
        /// </summary>
        private static Label Para(Control parent, int x, int y, int w, string text, float pt, Color color)
        {
            var l = new Label();
            l.UseMnemonic = false;
            l.Text = text;
            l.Font = Yahei(pt);
            l.ForeColor = color;
            l.AutoSize = true;
            l.MaximumSize = new Size(w, 0);
            l.Location = new Point(x, y);
            parent.Controls.Add(l);
            return l;
        }

        private static Label Bold(Control parent, int x, int y, int w, string text)
        {
            var l = new Label();
            l.AutoSize = false;
            l.UseMnemonic = false;
            l.Text = text;
            l.Font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold);
            l.ForeColor = Ink;
            l.Bounds = new Rectangle(x, y, w, 26);
            parent.Controls.Add(l);
            return l;
        }

        private static CheckBox Chk(Control parent, int x, int y, int w, string text, bool value, EventHandler on)
        {
            var c = new CheckBox();
            c.Text = text;
            c.Font = Yahei(10f);
            c.ForeColor = Ink;
            c.Bounds = new Rectangle(x, y, w, 26);
            c.Checked = value;
            if (on != null) c.CheckedChanged += on;
            parent.Controls.Add(c);
            return c;
        }

        private static Button Btn(Control parent, int x, int y, int w, int h, string text, EventHandler on)
        {
            var b = new Button();
            b.Text = text;
            b.Bounds = new Rectangle(x, y, w, h);
            if (on != null) b.Click += on;
            parent.Controls.Add(b);
            return b;
        }

        private static ComboBox Cmb(Control parent, int x, int y, int w, string[] items, int sel, EventHandler on)
        {
            var cb = new ComboBox();
            cb.DropDownStyle = ComboBoxStyle.DropDownList;
            cb.Bounds = new Rectangle(x, y, w, 24);
            cb.Items.AddRange(items);
            if (sel < 0) sel = 0;
            if (sel >= items.Length) sel = items.Length - 1;
            cb.SelectedIndex = sel;
            if (on != null) cb.SelectedIndexChanged += on;
            parent.Controls.Add(cb);
            return cb;
        }

        private static int Idx(string[] ids, string value, int fallback)
        {
            if (value == null) return fallback;
            for (int i = 0; i < ids.Length; i++)
                if (string.Equals(ids[i], value.Trim(), StringComparison.OrdinalIgnoreCase)) return i;
            return fallback;
        }

        private void Fire()
        {
            if (apply != null) apply(p);
        }

        private void SetPreview(bool on)
        {
            previewOn = on;
            try { if (setPreview != null) setPreview(on); } catch { }
        }

        // =====================================================================
        //  ① 欢迎
        // =====================================================================
        private void BuildWelcome()
        {
            Panel pg = pages[0];
            int x = 26, w = 668;

            Label l = Para(pg, x, 20, w,
                "DeadBand 把屏幕最底下一小条区域向系统占下来（跟任务栏一个机制），在里面显示时钟、频谱或字幕。",
                10.5f, Ink);
            l = Para(pg, x, l.Bottom + 14, w,
                "它服务的是这样一种坏屏：画面整体正常，但底部有横向的灰线或者淡淡的条纹。" +
                "这条区域反正已经不能正常用了 —— 与其让它空着，不如拿来放点有用的东西。",
                10f, Ink);

            int y = l.Bottom + 22;
            Bold(pg, x, y, w, "开始之前确认这三件事：");
            y += 30;
            Para(pg, x + 6, y, w - 6, "·  系统是 Windows 10 或 Windows 11", 10f, Ink); y += 26;
            Para(pg, x + 6, y, w - 6, "·  任务栏不在屏幕底部 —— DeadBand 要占最底下一整条", 10f, Ink); y += 26;
            Para(pg, x + 6, y, w - 6, "·  这条区域能正常显示，只是有横向灰线（不是全黑、也不是花屏）", 10f, Ink); y += 34;

            Para(pg, x, y, w,
                "全程大约两分钟。中途点「退出」会直接关掉 DeadBand；已经改过的设置会留着，下次打开还会重新问一遍。",
                9.5f, Dim);
        }

        // =====================================================================
        //  ② 环境预检
        // =====================================================================
        private void BuildPrecheck()
        {
            Panel pg = pages[1];
            int x = 26, w = 668;

            Label l = Para(pg, x, 18, w,
                "DeadBand 要占最底下一整条，所以底部必须是空的。下面这几项自动检查，" +
                "不通过就先处理掉 —— 否则区带会被顶到别的东西上方，和标定的位置对不上。",
                10f, Ink);

            int y = l.Bottom + 16;
            if (monitors.Length > 1)
            {
                Para(pg, x, y + 3, 70, "显示器", 10f, Ink);
                var cb = new ComboBox();
                cb.DropDownStyle = ComboBoxStyle.DropDownList;
                cb.Bounds = new Rectangle(x + 74, y, 470, 24);
                for (int i = 0; i < monitors.Length; i++) cb.Items.Add(MonitorLabel(monitors[i]));
                int sel = 0;
                for (int i = 0; i < monitors.Length; i++)
                    if (string.Equals(monitors[i].MonitorId, p.MonitorId, StringComparison.OrdinalIgnoreCase)) sel = i;
                cb.SelectedIndex = sel;
                cb.SelectedIndexChanged += delegate { OnMonitorChanged(); };
                pg.Controls.Add(cb);
                cbMonitor = cb;
                y += 34;
            }
            else
            {
                string only = monitors.Length == 1 ? MonitorLabel(monitors[0]) : "没有检测到显示器";
                Para(pg, x, y + 3, w, "显示器：" + only, 10f, Ink);
                y += 30;
            }

            preRows = new Label[3];
            for (int i = 0; i < preRows.Length; i++)
            {
                // 这三行要固定占两行高，下面的按钮才不会随文字长短跳动
                preRows[i] = Para(pg, x, y, w, " ", 10f, Ink);
                preRows[i].AutoSize = false;
                preRows[i].Bounds = new Rectangle(x, y, w, 46);
                y = preRows[i].Bottom + 8;
            }

            y += 6;
            Btn(pg, x, y, 104, 30, "重新检测", delegate { RunPrecheck(); });
            Btn(pg, x + 116, y, 150, 30, "打开任务栏设置",
                delegate { OpenTaskbarSettings(); });
            y += 42;

            Para(pg, x, y, w,
                "屏幕底部被占用时，「区带位置看起来不对」几乎一定是这个原因。任务栏挪到顶部（或设成自动隐藏）之后再点「重新检测」。",
                9.5f, Dim);
        }

        private static string MonitorLabel(MonitorInfo m)
        {
            if (m == null) return "(未知)";
            string name = string.IsNullOrEmpty(m.Description) ? "(未命名显示器)" : m.Description;
            Screen s = ScreenOf(m);
            string res = s != null ? " — " + s.Bounds.Width + "×" + s.Bounds.Height : "";
            string pri = (s != null && s.Primary) ? "（主显示器）" : "";
            return name + res + pri;
        }

        private static Screen ScreenOf(MonitorInfo m)
        {
            if (m == null) return Screen.PrimaryScreen;
            foreach (Screen s in Screen.AllScreens)
                if (string.Equals(s.DeviceName, m.ScreenDevice, StringComparison.OrdinalIgnoreCase)) return s;
            return null;
        }

        private MonitorInfo SelectedMonitor()
        {
            if (monitors.Length == 0) return null;
            if (monitors.Length == 1 || cbMonitor == null) return monitors[0];
            int i = cbMonitor.SelectedIndex;
            if (i < 0 || i >= monitors.Length) return monitors[0];
            return monitors[i];
        }

        private void OnMonitorChanged()
        {
            MonitorInfo m = SelectedMonitor();
            if (m == null || switchTo == null) return;
            ZoneProfile np = switchTo(m);
            if (np == null) return;
            p = np;
            SyncFromProfile();
            RunPrecheck();
        }

        private void OpenTaskbarSettings()
        {
            try { System.Diagnostics.Process.Start("ms-settings:taskbar"); }
            catch { }
        }

        private void SetRow(int i, string mark, string text, Color color)
        {
            if (preRows == null || i < 0 || i >= preRows.Length) return;
            Label r = preRows[i];
            r.Text = mark + "  " + text;
            r.ForeColor = color;
        }

        private void RunPrecheck()
        {
            Screen s = ScreenOf(SelectedMonitor());
            if (s == null) s = Screen.PrimaryScreen;
            if (s == null) return;

            Rectangle b = s.Bounds;
            Rectangle wa = s.WorkingArea;
            int rawGap = b.Bottom - wa.Bottom;

            // 区带一旦显示出来，它自己也占着 work area 的底部 ——
            // 那是我们自己，别当成"底部被别的程序占了"。
            int own = 0;
            try { if (ownZoneHeight != null) own = ownZoneHeight(); }
            catch { }
            int bottomGap = rawGap;
            if (own > 0 && rawGap >= own - 3) bottomGap = rawGap - own;

            int workH = wa.Height + (rawGap - bottomGap);
            int topGap = wa.Top - b.Top;
            blocked = bottomGap > 2;

            if (blocked)
                SetRow(0, "×", "屏幕底部被占用了 " + bottomGap +
                    " px —— 大概率是任务栏。区带会被挤到它上面，位置和标定对不上。", Bad);
            else
                SetRow(0, "√", "屏幕底部是空的" +
                    (topGap > 2 ? "（任务栏在顶部，占 " + topGap + " px，不冲突）" : ""), Ok);

            if (blocked)
                SetRow(1, "×", "可用工作区只有 " + wa.Width + "×" + workH +
                    "（整块屏幕是 " + b.Width + "×" + b.Height + "）", Bad);
            else
                SetRow(1, "√", "可用工作区 " + wa.Width + "×" + workH + "，底部整条可用", Ok);

            if (blocked)
                SetRow(2, "×", "预留区暂时贴不到屏幕最底边，先处理掉底部占用", Bad);
            else
                SetRow(2, "√", "预留区可以注册在屏幕最底边（当前高度 " + p.ZoneHeight + " px）", Ok);

            btnNext.Enabled = !blocked;

            // 预检通过就把区带显示出来 —— 让用户在标定之前先看到「它会占掉这么一条」
            try
            {
                if (blocked) { if (hideZone != null) hideZone(); }
                else { if (showZone != null) showZone(); }
            }
            catch { }
        }

        // =====================================================================
        //  ③ 标定
        // =====================================================================
        private void BuildCalibrate()
        {
            Panel pg = pages[2];
            int x = 26, w = 668;

            Label l = Para(pg, x, 16, w,
                "点下面的按钮会整屏进入标定界面。一共四步，进去以后屏幕上会一直标着现在在第几步：",
                10f, Ink);

            int y = l.Bottom + 12;
            y = StepLine(pg, x, y, w, "① 预留区高度",
                "拖动顶部青线，把带的顶边拖到最上面那条灰线的上方");
            y = StepLine(pg, x, y, w, "② 坏线间距",
                "让洋红线的疏密和灰线一致（数一下相邻灰线隔几行）");
            y = StepLine(pg, x, y, w, "③ 坏线相位",
                "用相位把洋红线移到正好压住灰线，而不是错开半格");
            y = StepLine(pg, x, y, w, "④ 保存",
                "按 Enter；Esc 取消，什么都不改");

            y += 4;
            l = Para(pg, x, y, w,
                "Tab 切换步骤 · ↑↓ 调 1 · Shift+↑↓ 调 10 · 滚轮微调",
                9.5f, Dim);

            y = l.Bottom + 12;
            Button big = Btn(pg, x, y, 190, 38, "开始标定…", delegate { DoCalibrate(); });
            big.Font = new Font("Microsoft YaHei UI", 10.5f, FontStyle.Bold);

            y += 48;
            lblCalib = Para(pg, x, y, w, "还没有标定。", 10f, Dim);
            lblCalib.AutoSize = false;
            lblCalib.Bounds = new Rectangle(x, y, w, 46);   // 摘要文本会变长，固定留两行
            lblCalib.Text = "还没有标定。";

            y = lblCalib.Bottom + 10;
            Para(pg, x, y, w,
                "标定界面默认白底（灰线在白底上最清楚），按 C 可换底色。跳过也行，会用默认值。",
                9.5f, Dim);
        }

        /// <summary>一行「步骤名 + 这一步要做什么」，标定那一页用。</summary>
        private static int StepLine(Control parent, int x, int y, int w, string name, string desc)
        {
            var l = new Label();
            l.AutoSize = false;
            l.UseMnemonic = false;
            l.Text = name;
            l.Font = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold);
            l.ForeColor = Color.FromArgb(0x00, 0x6C, 0x8A);
            l.Bounds = new Rectangle(x, y, 132, 22);
            parent.Controls.Add(l);

            Label d = new Label();
            d.AutoSize = false;
            d.UseMnemonic = false;
            d.Text = desc;
            d.Font = Yahei(9.5f);
            d.ForeColor = Ink;
            d.Bounds = new Rectangle(x + 138, y + 1, w - 138, 20);
            Size need = TextRenderer.MeasureText(desc, d.Font, new Size(w - 138, int.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            d.Height = need.Height + 4;
            parent.Controls.Add(d);

            int bottom = Math.Max(l.Bottom, d.Bottom);
            return bottom + 6;
        }

        private void DoCalibrate()
        {
            if (calibrate == null) return;
            Hide();
            ZoneProfile np = null;
            try { np = calibrate(); }
            catch { }
            Show();
            BringToFront();
            Activate();

            if (np != null)
            {
                p = np;
                calibrated = true;
                SyncFromProfile();
                lblCalib.ForeColor = Ok;
                lblCalib.Text = "已保存：预留区 " + p.ZoneHeight + " px，坏线每 " +
                    p.Lines.Spacing + " 行一条（相位 " + p.Lines.Offset +
                    "，粗细 " + p.Lines.Thickness + " 行）";
            }
            else
            {
                lblCalib.ForeColor = Warn;
                lblCalib.Text = "这次没有保存标定，仍然用默认值。";
            }
            if (header != null) header.Invalidate();
        }

        // =====================================================================
        //  ④ 显示什么
        // =====================================================================
        private void BuildContent()
        {
            Panel pg = pages[3];
            int x = 26, w = 668;

            Label l = Para(pg, x, 18, w,
                "三层内容各自独立，可以同时开。没有内容时那一层什么都不画。", 10f, Ink);

            int y = l.Bottom + 14;
            cbClock = Chk(pg, x, y, w, "时钟 —— 像素风的大字时间", p.ClockEnabled, delegate
            {
                p.ClockEnabled = cbClock.Checked; Fire();
            });
            y = cbClock.Bottom + 2;
            cbSpec = Chk(pg, x, y, w, "频谱 —— 系统有声音时显示（默认开）", p.SpectrumEnabled, delegate
            {
                p.SpectrumEnabled = cbSpec.Checked; Fire(); RefreshOverlayRows();
            });
            y = cbSpec.Bottom + 2;
            cbCap = Chk(pg, x, y, w, "字幕 —— B站 视频的字幕 / 歌词（需要浏览器脚本）", p.CaptionEnabled, delegate
            {
                p.CaptionEnabled = cbCap.Checked; Fire();
                RefreshCaptionExtra(); RefreshOverlayRows();
            });

            // 勾了字幕才用得上的一块，位置固定，只是显示/隐藏
            y = cbCap.Bottom + 6;
            capExtra = new Panel();
            capExtra.Bounds = new Rectangle(x + 22, y, w - 22, 92);   // 高度在下面按实际内容重算
            capExtra.BackColor = Paper;
            pg.Controls.Add(capExtra);

            Label note = new Label();
            note.UseMnemonic = false;
            note.Text = "字幕来自浏览器，目前只支持 B站 视频（网页播放器那种，其它播放器还接不了）。" +
                        "播放器把当前这句推给 DeadBand，只在本机通信。" +
                        "要先装浏览器扩展「篡改猴 / Tampermonkey」，再装 DeadBand 的脚本 —— " +
                        "没装篡改猴就点脚本，浏览器只会给你看一段代码。";
            note.Font = Yahei(9.5f);
            note.ForeColor = Dim;
            note.AutoSize = true;
            note.MaximumSize = new Size(w - 32, 0);
            note.Location = new Point(0, 0);
            capExtra.Controls.Add(note);

            Button b1 = Btn(capExtra, 0, note.Bottom + 8, 140, 30, "先装篡改猴…",
                delegate { OpenTampermonkey(); });
            Button b2 = Btn(capExtra, 152, note.Bottom + 8, 200, 30, "装 DeadBand 脚本…",
                delegate { if (onInstall != null) onInstall(); });
            capExtra.Height = b2.Bottom + 4;

            y = capExtra.Bottom + 10;
            Para(pg, x, y, w,
                "引导期间区带上显示的是示例频谱和示例字幕，方便你看到叠加效果；走完就恢复成真实内容。",
                9.5f, Dim);

            RefreshCaptionExtra();
        }

        /// <summary>
        /// 篡改猴官网：会根据浏览器给出对应的商店链接（Chrome / Edge / Firefox / Safari）。
        /// 直接给商店链接会有浏览器不对的风险，给官网更稳。
        /// </summary>
        private void OpenTampermonkey()
        {
            try { System.Diagnostics.Process.Start("https://www.tampermonkey.net/"); }
            catch { }
        }

        private void RefreshCaptionExtra()
        {
            if (capExtra != null) capExtra.Visible = (cbCap != null && cbCap.Checked);
        }

        // =====================================================================
        //  ⑤ 怎么叠
        // =====================================================================
        private void BuildOverlay()
        {
            Panel pg = pages[4];
            int x = 26, w = 668, labelW = 112;

            Label l = Para(pg, x, 18, w,
                "三层同时有内容时的规则。改动会立刻反映在底部区带上。", 10f, Ink);
            int y = l.Bottom + 16;

            lbAudio = Para(pg, x, y + 4, labelW, "有音频时", 10f, Ink);
            cbAudio = Cmb(pg, x + labelW, y, 360,
                new string[] { "频谱变淡垫在时钟后面", "隐藏时钟" },
                Idx(AudioIds, p.SpectrumWithClock, 0), null);
            cbAudio.SelectedIndexChanged += delegate
            {
                p.SpectrumWithClock = AudioIds[cbAudio.SelectedIndex]; Fire();
            };
            y += 36;

            lbDim = Para(pg, x, y + 4, labelW, "变淡到", 10f, Ink);
            numDim = new NumericUpDown();
            numDim.Bounds = new Rectangle(x + labelW, y, 80, 24);
            numDim.Minimum = 5;
            numDim.Maximum = 100;
            numDim.Value = Math.Min(100, Math.Max(5, p.SpectrumDim));
            numDim.ValueChanged += delegate { p.SpectrumDim = (int)numDim.Value; Fire(); };
            pg.Controls.Add(numDim);
            lbDimUnit = Para(pg, x + labelW + 88, y + 4, 40, "%", 10f, Ink);
            y += 36;

            lbCapRule = Para(pg, x, y + 4, labelW, "有字幕时", 10f, Ink);
            cbCapRule = new ComboBox();
            cbCapRule.DropDownStyle = ComboBoxStyle.DropDownList;
            cbCapRule.Bounds = new Rectangle(x + labelW, y, 360, 24);
            cbCapRule.Items.AddRange(new string[] { "时钟移到右边", "隐藏时钟" });
            cbCapRule.SelectedIndex = Idx(CapIds, p.ClockWithCaption, 0);            cbCapRule.SelectedIndexChanged += delegate
            {
                p.ClockWithCaption = CapIds[cbCapRule.SelectedIndex]; Fire();
            };
            pg.Controls.Add(cbCapRule);
            y += 46;

            Para(pg, x, y, w,
                "「变淡到」只在频谱垫在别的层下面时生效。多层同时出现时，底下的层太亮会看不清上面的字，" +
                "所以给一个统一的不透明度。",
                9.5f, Dim);

            RefreshOverlayRows();
        }

        /// <summary>按当前开关把不相关的行置灰，而不是藏起来 —— 位置固定，用户不会觉得界面在跳。</summary>
        private void RefreshOverlayRows()
        {
            if (cbAudio == null) return;
            bool spec = cbSpec != null && cbSpec.Checked;
            bool cap = cbCap != null && cbCap.Checked;
            bool dimRule = string.Equals(p.SpectrumWithClock == null ? "" : p.SpectrumWithClock.Trim(),
                                         "dim", StringComparison.OrdinalIgnoreCase);

            cbAudio.Enabled = spec;
            lbAudio.ForeColor = spec ? Ink : Color.Silver;
            lbDim.ForeColor = (spec && dimRule) ? Ink : Color.Silver;
            numDim.Enabled = spec && dimRule;
            lbDimUnit.ForeColor = (spec && dimRule) ? Ink : Color.Silver;
            cbCapRule.Enabled = cap;
            lbCapRule.ForeColor = cap ? Ink : Color.Silver;
        }

        // =====================================================================
        //  ⑥ 长什么样
        // =====================================================================
        private void BuildLook()
        {
            Panel pg = pages[5];
            int x = 26, w = 668;

            Label l = Para(pg, x, 18, w,
                "坏线是一条条横着的灰线。如果在同样的位置再画上一条条同色的竖线，" +
                "它们连起来就是一张栅格 —— 看起来是设计的一部分，而不是坏点。",
                10f, Ink);

            int y = l.Bottom + 16;
            cbGrid = Chk(pg, x, y, w, "背景网格（把面板坏线收编成栅格）", p.GridEnabled, delegate
            {
                p.GridEnabled = cbGrid.Checked; Fire(); RefreshLookRows();
            });
            y = cbGrid.Bottom + 2;
            cbFollow = Chk(pg, x, y, w, "网格跟随坏线（正方形栅格，推荐）", p.GridFollowLines, delegate
            {
                p.GridFollowLines = cbFollow.Checked; Fire();
            });
            y = cbFollow.Bottom + 2;
            cbBlank = Chk(pg, x, y, w, "坏线挖空（坏线所在的行清成底色）", p.LineBlank, delegate
            {
                p.LineBlank = cbBlank.Checked; Fire();
            });

            y = cbBlank.Bottom + 14;
            Btn(pg, x, y, 130, 30, "网格色…", delegate { if (onColor != null) onColor(1); });
            Btn(pg, x + 142, y, 150, 30, "区带背景色…", delegate { if (onColor != null) onColor(0); });
            Btn(pg, x + 304, y, 120, 30, "时钟字色…", delegate { if (onColor != null) onColor(2); });

            y += 44;
            Para(pg, x, y, w,
                "建议先勾上「背景网格」再调网格色：拿取色器把网格色调到和坏线观感接近，" +
                "竖向栅格和横向坏线就会连成一张网，灰线看不出来了。",
                9.5f, Dim);

            RefreshLookRows();
        }

        private void RefreshLookRows()
        {
            if (cbFollow == null) return;
            bool on = cbGrid.Checked;
            cbFollow.Enabled = on;
            cbBlank.Enabled = true;   // 坏线挖空跟网格是两件事
        }

        // =====================================================================
        //  ⑦ 全屏与开机启动
        // =====================================================================
        private void BuildStartup()
        {
            Panel pg = pages[6];
            int x = 26, w = 668;

            Bold(pg, x, 18, w, "全屏视频");
            int y = 46;
            Para(pg, x, y + 4, 112, "全屏时", 10f, Ink);
            cbFs = Cmb(pg, x + 112, y, 420,
                new string[] { "让全屏窗口避让（视频停在区带上方）", "只覆盖显示（不缩放窗口）" },
                Idx(FsIds, p.FullscreenMode, 0), null);
            cbFs.SelectedIndexChanged += delegate { p.FullscreenMode = FsIds[cbFs.SelectedIndex]; Fire(); };
            y += 38;
            Para(pg, x, y, w,
                "避让 = 把全屏窗口压到区带上沿，画面完整可见；覆盖 = 不动对方窗口，" +
                "区带压住画面最底下那条。有些播放器不吃避让，会自动退到覆盖。",
                9.5f, Dim);

            y += 46;
            Bold(pg, x, y, w, "开机启动");
            y += 28;
            cbAutostart = Chk(pg, x, y, w, "登录 Windows 后自动启动 DeadBand", AutostartDefault(), null);
            y = cbAutostart.Bottom + 8;
            Para(pg, x, y, w,
                "写一条当前用户的启动项（HKCU\\…\\Run），不需要管理员权限。" +
                "关掉的办法：设置窗口里的同一个勾，或者任务管理器的「启动」页。",
                9.5f, Dim);
        }

        /// <summary>
        /// 首次运行时注册表里还没有这一项 —— 那就默认勾上（这个工具的定位是常驻）。
        /// 已经明确存在、但被关掉或禁用的，就照实显示成没勾。
        /// </summary>
        private static bool AutostartDefault()
        {
            try
            {
                if (Autostart.HasEntry()) return Autostart.IsOn();
                return true;
            }
            catch { return true; }
        }

        // =====================================================================
        //  ⑧ 完成
        // =====================================================================
        private void BuildDone()
        {
            Panel pg = pages[7];
            int x = 26, w = 668;

            Label l = Para(pg, x, 18, w, "设置好了。下面是这次的摘要：", 10f, Ink);

            lblSummary = new Label();
            lblSummary.AutoSize = false;
            lblSummary.UseMnemonic = false;
            lblSummary.Font = Yahei(10f);
            lblSummary.ForeColor = Ink;
            lblSummary.Bounds = new Rectangle(x, l.Bottom + 14, w, 180);
            pg.Controls.Add(lblSummary);

            int y = lblSummary.Bottom + 10;
            Btn(pg, x, y, 180, 32, "打开设置继续调…", delegate { if (onSettings != null) onSettings(); });
            Btn(pg, x + 192, y, 150, 32, "打开配置目录", delegate { if (onOpenDir != null) onOpenDir(); });

            y += 44;
            Para(pg, x, y, w,
                "点右下角「完成」结束引导。以后想重新走一遍：设置 → 外观与其他 → 重新运行首次设置引导。",
                9.5f, Dim);
        }

        private void BuildSummary()
        {
            var sb = new StringBuilder();
            sb.AppendLine("预留区：" + p.ZoneHeight + " px（屏幕 " + p.ScreenWidth + "×" + p.ScreenHeight + "）");
            if (p.Lines.Enabled)
                sb.AppendLine("坏线：每 " + p.Lines.Spacing + " 行一条，相位 " + p.Lines.Offset +
                              "，粗细 " + p.Lines.Thickness + " 行" +
                              (calibrated ? "" : "（沿用原配置，本次没有重新标定）"));
            else
                sb.AppendLine("坏线：没启用 —— 屏幕没坏线的话这样是对的");
            sb.AppendLine("内容：" + LayerText());
            if (p.SpectrumEnabled || p.CaptionEnabled)
            {
                string a = string.Equals(p.SpectrumWithClock == null ? "" : p.SpectrumWithClock.Trim(),
                                         "hideclock", StringComparison.OrdinalIgnoreCase)
                    ? "有音频时隐藏时钟" : "有音频时频谱变淡到 " + p.SpectrumDim + "%";
                string b = string.Equals(p.ClockWithCaption == null ? "" : p.ClockWithCaption.Trim(),
                                         "hide", StringComparison.OrdinalIgnoreCase)
                    ? "有字幕时隐藏时钟" : "有字幕时时钟靠右";
                sb.AppendLine("叠加：" + a + "；" + b);
            }
            sb.AppendLine("外观：" + (p.GridEnabled
                ? ("背景网格 开" + (p.GridFollowLines ? "（跟随坏线）" : "（间距 " + p.GridSpacing + " px）"))
                : "背景网格 关"));
            sb.AppendLine("全屏：" + (string.Equals(p.FullscreenMode == null ? "" : p.FullscreenMode.Trim(),
                                                     "overlay", StringComparison.OrdinalIgnoreCase)
                ? "只覆盖显示" : "让窗口避让"));
            sb.AppendLine("开机启动：" + (cbAutostart != null && cbAutostart.Checked ? "开启" : "关闭"));
            lblSummary.Text = sb.ToString().TrimEnd();
        }

        private string LayerText()
        {
            var parts = new List<string>();
            if (p.ClockEnabled) parts.Add("时钟");
            if (p.SpectrumEnabled) parts.Add("频谱");
            if (p.CaptionEnabled) parts.Add("字幕");
            string pm = p.PomodoroMode == null ? "" : p.PomodoroMode.Trim().ToLowerInvariant();
            if (pm == "exclusive") parts.Add("番茄钟（独占）");
            else if (pm == "coexist") parts.Add("番茄钟（共存）");
            if (parts.Count == 0) return "都没开 —— 现在只是一条背景板";
            return string.Join(" + ", parts.ToArray());
        }

        // =====================================================================
        //  步骤切换
        // =====================================================================
        private void EnterStep(int n)
        {
            if (n < 0) n = 0;
            if (n >= StepCount) n = StepCount - 1;
            step = n;

            for (int i = 0; i < pages.Length; i++)
                if (pages[i] != null) pages[i].Visible = (i == n);

            header.Invalidate();

            btnBack.Enabled = n > 0;
            btnNext.Text = (n == StepCount - 1) ? "完成" : "下一步";
            btnNext.Enabled = !(n == 1 && blocked);

            // 第 4~6 步开着预览：不然叠加规则根本看不出效果
            bool wantPreview = (n >= 3 && n <= 6);
            if (wantPreview != previewOn) SetPreview(wantPreview);

            if (n == 1) RunPrecheck();
            else if (n == 2) Activate();
            else if (n == 3) { RefreshCaptionExtra(); RefreshOverlayRows(); }
            else if (n == 4) RefreshOverlayRows();
            else if (n == 6) { /* 自启勾选保持原样 */ }
            else if (n == 7) BuildSummary();
        }

        private void GoNext()
        {
            if (step == StepCount - 1) { Finish(); return; }
            EnterStep(step + 1);
        }

        private void GoBack()
        {
            if (step == 0) return;
            EnterStep(step - 1);
        }

        private void Finish()
        {
            try
            {
                bool want = cbAutostart != null && cbAutostart.Checked;
                if (want != Autostart.IsOn()) Autostart.Set(want);
            }
            catch { }
            SetPreview(false);
            Completed = true;
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            try { SetPreview(false); } catch { }
            base.OnFormClosing(e);
        }

        // =====================================================================
        //  换显示器 / 标定之后把控件状态同步回 profile
        // =====================================================================
        private void SyncFromProfile()
        {
            cbClock.Checked = p.ClockEnabled;
            cbSpec.Checked = p.SpectrumEnabled;
            cbCap.Checked = p.CaptionEnabled;
            cbAudio.SelectedIndex = Idx(AudioIds, p.SpectrumWithClock, 0);
            int dim = p.SpectrumDim;
            if (dim < 5) dim = 5;
            if (dim > 100) dim = 100;
            numDim.Value = dim;
            cbCapRule.SelectedIndex = Idx(CapIds, p.ClockWithCaption, 0);
            cbGrid.Checked = p.GridEnabled;
            cbFollow.Checked = p.GridFollowLines;
            cbBlank.Checked = p.LineBlank;
            cbFs.SelectedIndex = Idx(FsIds, p.FullscreenMode, 0);
            RefreshCaptionExtra();
            RefreshOverlayRows();
            RefreshLookRows();
        }

        // =====================================================================
        //  开发期自检：控件重叠 / 文字被裁
        // =====================================================================
        public void DumpOverlaps(string path)
        {
            // 先摆到"最坏情况"：可选的块都显出来，文本都填满，否则查不到真实布局
            try
            {
                cbCap.Checked = true;
                cbSpec.Checked = true;
                cbGrid.Checked = true;
                RunPrecheck();
                BuildSummary();
            }
            catch { }

            var sb = new StringBuilder();
            sb.AppendLine("client=" + ClientSize);

            var all = new Control[] { header, footer };
            int bad = 0;
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] == null) continue;
                sb.AppendLine("--- " + all[i].GetType().Name + " ---");
                Scan(all[i], sb, ref bad);
            }
            for (int i = 0; i < pages.Length; i++)
            {
                if (pages[i] == null) continue;
                sb.AppendLine("--- page " + (i + 1) + " " + Titles[i] + " ---");
                Scan(pages[i], sb, ref bad);
            }

            sb.AppendLine("overlaps=" + bad);
            try { System.IO.File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false)); }
            catch { }
        }

        private static void Scan(Control parent, StringBuilder sb, ref int bad)
        {
            var kids = new List<Control>();
            foreach (Control c in parent.Controls) kids.Add(c);

            for (int i = 0; i < kids.Count; i++)
            {
                for (int j = i + 1; j < kids.Count; j++)
                {
                    if (!kids[j].Visible || !kids[i].Visible) continue;
                    Rectangle a = kids[i].Bounds;
                    Rectangle b = kids[j].Bounds;
                    if (!a.IntersectsWith(b)) continue;
                    bad++;
                    sb.AppendLine("OVERLAP [" + kids[i].GetType().Name + " " + kids[i].Text +
                                  " " + a + "] x [" + kids[j].GetType().Name + " " + kids[j].Text +
                                  " " + b + "]");
                }
            }

            foreach (Control c in kids)
            {
                // 被父容器压住 = 用户根本看不到，和"被裁"一样是坏的
                if (c.Visible && c.Bottom > parent.Height + 2)
                {
                    bad++;
                    sb.AppendLine("OVERFLOW [" + c.GetType().Name + " " + Short(c.Text) +
                                  "] bottom=" + c.Bottom + " 容器高=" + parent.Height);
                }

                var lb = c as Label;
                // AutoSize 的标签自己算高度，不会裁；只查定高的那些
                if (lb != null && !lb.AutoSize && !string.IsNullOrEmpty(lb.Text))
                {
                    Size need = TextRenderer.MeasureText(lb.Text, lb.Font, new Size(lb.Width, int.MaxValue),
                        TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                    if (need.Height > lb.Height + 2)
                    {
                        bad++;
                        sb.AppendLine("CLIPPED [" + Short(lb.Text) + "] 需要高 " +
                                      need.Height + " 实际 " + lb.Height);
                    }
                }
                // 面板里面还有一层（比如字幕那一块），一起查
                if (c is Panel) Scan(c, sb, ref bad);
            }
        }

        private static string Short(string s)
        {
            if (s == null) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length <= 28 ? s : s.Substring(0, 28) + "…";
        }
    }
}
