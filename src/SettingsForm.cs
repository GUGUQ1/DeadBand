using System;
using System.Drawing;
using System.Windows.Forms;

namespace DeadBand
{
    /// <summary>
    /// 全部细节设置都在这一个窗口里。改动**即时生效**，没有确定/取消 ——
    /// 这样可以一边调一边看底部区带的真实效果。
    ///
    /// 窗口本身不碰文件、不碰窗口宿主，只改传进来的 profile 并回调 changed()，
    /// 由 App 负责保存和应用。
    /// </summary>
    internal sealed class SettingsForm : Form
    {
        private readonly ZoneProfile p;
        private readonly Action changed;
        private readonly Action onCalibrate, onClock, onInstall, onOpenDir;
        private readonly Action<int> onColor;
        private readonly Func<bool> getPreview;
        private readonly Action<bool> setPreview;
        private readonly Action onOnboard;
        private readonly string feedUrl;

        private const int PadX = 18;
        private const int PadY = 16;
        private const int RowH = 32;
        private const int LabelW = 110;

        public SettingsForm(ZoneProfile profile, string feedUrl,
                            Action changed, Action onCalibrate, Action onClock,
                            Action<int> onColor, Action onInstall, Action onOpenDir,
                            Func<bool> getPreview, Action<bool> setPreview,
                            Action onOnboard)
        {
            this.p = profile;
            this.feedUrl = feedUrl == null ? "" : feedUrl;
            this.changed = changed;
            this.onCalibrate = onCalibrate;
            this.onClock = onClock;
            this.onColor = onColor;
            this.onInstall = onInstall;
            this.onOpenDir = onOpenDir;
            this.getPreview = getPreview;
            this.setPreview = setPreview;
            this.onOnboard = onOnboard;

            Text = "DeadBand 设置";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;
            ClientSize = new Size(680, 620);
            BuildUi();
        }

        // ---------------- 布局小工具 ----------------
        private static Panel Page(TabControl tabs, string title)
        {
            var page = new TabPage(title);
            page.UseVisualStyleBackColor = true;
            tabs.TabPages.Add(page);

            var panel = new Panel();
            panel.Dock = DockStyle.Fill;
            panel.AutoScroll = true;
            page.Controls.Add(panel);
            return panel;
        }

        /// <summary>写一行灰色说明文字，返回下一行该用的 y。</summary>
        private static int Hint(Control parent, int y, string text)
        {
            var l = new Label();
            l.Text = text;
            l.Bounds = new Rectangle(PadX, y, 620, 20);
            l.ForeColor = Color.DimGray;
            parent.Controls.Add(l);
            return y + 26;
        }

        private static CheckBox Check(Control parent, ref int y, string text, bool value, Action on)
        {
            var c = new CheckBox();
            c.Text = text;
            c.Bounds = new Rectangle(PadX, y, 620, 24);
            c.Checked = value;
            c.CheckedChanged += delegate { if (on != null) on(); };
            parent.Controls.Add(c);
            y += 28;
            return c;
        }

        private static Button Btn(Control parent, ref int y, string text, Action on)
        {
            var b = new Button();
            b.Text = text;
            b.Bounds = new Rectangle(PadX, y, 320, 30);
            b.Click += delegate { if (on != null) on(); };
            parent.Controls.Add(b);
            y += 38;
            return b;
        }

        private static void Combo(Control parent, ref int y, string label,
                                  string[] items, int selected, Action<int> on)
        {
            var lab = new Label();
            lab.Text = label;
            lab.Bounds = new Rectangle(PadX, y + 4, LabelW, 20);
            parent.Controls.Add(lab);

            var cb = new ComboBox();
            cb.DropDownStyle = ComboBoxStyle.DropDownList;
            cb.Bounds = new Rectangle(PadX + LabelW, y, 340, 24);
            cb.Items.AddRange(items);
            if (selected < 0) selected = 0;
            if (selected >= items.Length) selected = items.Length - 1;
            cb.SelectedIndex = selected;      // 先设好再挂事件，避免初始化时触发保存
            cb.SelectedIndexChanged += delegate { if (on != null) on(cb.SelectedIndex); };
            parent.Controls.Add(cb);
            y += RowH;
        }

        private static void Num(Control parent, ref int y, string label,
                                int value, int min, int max, Action<int> on)
        {
            var lab = new Label();
            lab.Text = label;
            lab.Bounds = new Rectangle(PadX, y + 4, LabelW, 20);
            parent.Controls.Add(lab);

            var n = new NumericUpDown();
            n.Bounds = new Rectangle(PadX + LabelW, y, 80, 24);
            n.Minimum = min;
            n.Maximum = max;
            n.Value = Math.Min(max, Math.Max(min, value));
            n.ValueChanged += delegate { if (on != null) on((int)n.Value); };
            parent.Controls.Add(n);
            y += RowH;
        }

        private static int IndexOf(string[] ids, string value, int fallback)
        {
            if (value == null) return fallback;
            for (int i = 0; i < ids.Length; i++)
                if (string.Equals(ids[i], value.Trim(), StringComparison.OrdinalIgnoreCase)) return i;
            return fallback;
        }

        private void BuildUi()
        {
            var tabs = new TabControl();
            tabs.Dock = DockStyle.Fill;
            Controls.Add(tabs);

            BuildContentTab(tabs);
            BuildClockTab(tabs);
            BuildCaptionTab(tabs);
            BuildMiscTab(tabs);

            var hint = new Label();
            hint.Text = "改动立即生效，关掉窗口即可。";
            hint.Dock = DockStyle.Bottom;
            hint.Height = 24;
            hint.TextAlign = ContentAlignment.MiddleLeft;
            hint.ForeColor = Color.DimGray;
            hint.Padding = new Padding(PadX, 0, 0, 0);
            Controls.Add(hint);
            hint.BringToFront();
        }

        // ---------------- ① 内容 ----------------
        private void BuildContentTab(TabControl tabs)
        {
            Panel pg = Page(tabs, "内容");
            int y = PadY;

            y = Hint(pg, y, "三层内容可以同时开。没有内容时该层什么都不画。");

            // 没有声音/字幕时，叠加规则和「变淡到」根本看不出效果，所以给个预览开关
            var cbPrev = new CheckBox();
            cbPrev.Text = "预览模式：用示例频谱和示例字幕显示，方便调叠加";
            cbPrev.Bounds = new Rectangle(PadX, y, 620, 24);
            cbPrev.Checked = (getPreview != null) && getPreview();
            cbPrev.CheckedChanged += delegate
            {
                if (setPreview != null) setPreview(cbPrev.Checked);
            };
            pg.Controls.Add(cbPrev);
            y += 32;

            Check(pg, ref y, "显示时钟", p.ClockEnabled, delegate
            {
                p.ClockEnabled = !p.ClockEnabled; Fire();
            });
            Check(pg, ref y, "显示频谱（需要系统里有声音）", p.SpectrumEnabled, delegate
            {
                p.SpectrumEnabled = !p.SpectrumEnabled; Fire();
            });
            Check(pg, ref y, "显示字幕（来自浏览器推送）", p.CaptionEnabled, delegate
            {
                p.CaptionEnabled = !p.CaptionEnabled; Fire();
            });

            y += 14;
            y = Hint(pg, y, "叠加规则 —— 多层同时有内容时谁让谁");

            string[] pomoIds = new string[] { "off", "exclusive", "coexist" };
            Combo(pg, ref y, "番茄钟",
                  new string[] { "关", "独占（盖住其他内容）", "共存（叠加在最上面）" },
                  IndexOf(pomoIds, p.PomodoroMode, 0),
                  delegate(int i) { p.PomodoroMode = pomoIds[i]; Fire(); });

            string[] audioIds = new string[] { "dim", "hideclock" };
            Combo(pg, ref y, "有音频时",
                  new string[] { "频谱变淡垫在时钟后", "隐藏时钟" },
                  IndexOf(audioIds, p.SpectrumWithClock, 0),
                  delegate(int i) { p.SpectrumWithClock = audioIds[i]; Fire(); });

            // 垫在后面时淡到什么程度。时钟和字幕两种叠加都吃这个值。
            Num(pg, ref y, "变淡到 (%)", p.SpectrumDim, 5, 100, delegate(int v)
            {
                p.SpectrumDim = v; Fire();
            });
            string[] capIds = new string[] { "right", "hide" };
            Combo(pg, ref y, "有字幕时",
                  new string[] { "时钟移到右边", "隐藏时钟" },
                  IndexOf(capIds, p.ClockWithCaption, 0),
                  delegate(int i) { p.ClockWithCaption = capIds[i]; Fire(); });

            y += 10;
            y = Hint(pg, y, "频谱");

            string[] axisIds = new string[] { "fitlow", "fixed", "fitboth" };
            Combo(pg, ref y, "横轴范围",
                  new string[] { "跟随高端（默认）", "写死 40Hz–16.5kHz", "两端都跟随" },
                  IndexOf(axisIds, p.SpectrumAxis, 0),
                  delegate(int i) { p.SpectrumAxis = axisIds[i]; Fire(); });
        }

        // ---------------- ② 时钟 ----------------
        private void BuildClockTab(TabControl tabs)
        {
            Panel pg = Page(tabs, "时钟");
            int y = PadY;

            y = Hint(pg, y, "字号、位置、颜色。位置只在「居中」时生效，有字幕时会自动靠右。");

            Btn(pg, ref y, "大小与位置…", delegate
            {
                if (onClock != null) onClock();
            });
            Btn(pg, ref y, "字色…", delegate
            {
                if (onColor != null) onColor(2);
                Fire();
            });

            y += 8;
            Check(pg, ref y, "跟随网格色（自动同色系）", p.ClockAutoColor, delegate
            {
                p.ClockAutoColor = !p.ClockAutoColor; Fire();
            });
            Check(pg, ref y, "冒号闪烁", p.ClockBlink, delegate
            {
                p.ClockBlink = !p.ClockBlink; Fire();
            });
            Check(pg, ref y, "彩虹渐变", p.ClockGradient, delegate
            {
                p.ClockGradient = !p.ClockGradient; Fire();
            });
        }

        // ---------------- ③ 字幕 ----------------
        private void BuildCaptionTab(TabControl tabs)
        {
            Panel pg = Page(tabs, "字幕");
            int y = PadY;

            y = Hint(pg, y, "字幕目前只支持 B站 网页视频（其它播放器还接不了）。需要在播放器里把字幕打开。");

            string[] fontIds = new string[] { "pixel", "normal" };
            Combo(pg, ref y, "字体",
                  new string[] { "像素字体（和网格咬合）", "正常字体（抗锯齿）" },
                  IndexOf(fontIds, p.CaptionFont, 0),
                  delegate(int i) { p.CaptionFont = fontIds[i]; Fire(); });

            Btn(pg, ref y, "字色…", delegate
            {
                if (onColor != null) onColor(3);
                Fire();
            });

            y += 8;
            Check(pg, ref y, "隐藏播放器自带字幕（否则同一句会显示两遍）", AppOptions.HidePlayerSubtitle,
                  delegate
                  {
                      AppOptions.HidePlayerSubtitle = !AppOptions.HidePlayerSubtitle;
                      AppOptions.Save();
                  });

            y += 14;
            y = Hint(pg, y, "浏览器脚本");

            var tb = new TextBox();
            tb.ReadOnly = true;
            tb.Bounds = new Rectangle(PadX, y, 430, 24);
            tb.Text = feedUrl;
            pg.Controls.Add(tb);

            var copy = new Button();
            copy.Text = "复制地址";
            copy.Bounds = new Rectangle(PadX + 442, y - 2, 100, 28);
            copy.Click += delegate
            {
                try { Clipboard.SetText(feedUrl); } catch { }
            };
            pg.Controls.Add(copy);
            y += 40;

            Btn(pg, ref y, "在浏览器里安装 / 更新脚本", delegate
            {
                if (onInstall != null) onInstall();
            });

            y = Hint(pg, y, "先装篡改猴（Tampermonkey）扩展，再点上面的按钮装脚本；以后更新也点它。");
        }

        // ---------------- ④ 外观与其他 ----------------
        private void BuildMiscTab(TabControl tabs)
        {
            Panel pg = Page(tabs, "外观与其他");
            int y = PadY;

            y = Hint(pg, y, "区带");

            Btn(pg, ref y, "区带背景色…", delegate
            {
                if (onColor != null) onColor(0);
                Fire();
            });

            Check(pg, ref y, "背景网格（把面板坏线收编成栅格）", p.GridEnabled, delegate
            {
                p.GridEnabled = !p.GridEnabled; Fire();
            });
            Check(pg, ref y, "坏线挖空（坏线所在行清成底色）", p.LineBlank, delegate
            {
                p.LineBlank = !p.LineBlank; Fire();
            });

            Btn(pg, ref y, "网格色…", delegate
            {
                if (onColor != null) onColor(1);
                Fire();
            });

            Check(pg, ref y, "网格跟随坏线（正方形栅格）", p.GridFollowLines, delegate
            {
                p.GridFollowLines = !p.GridFollowLines; Fire();
            });

            Num(pg, ref y, "网格间距", p.GridSpacing, 2, 64, delegate(int v)
            {
                p.GridSpacing = v; Fire();
            });
            Num(pg, ref y, "网格粗细", p.GridThickness, 1, 16, delegate(int v)
            {
                p.GridThickness = v; Fire();
            });

            y += 14;
            y = Hint(pg, y, "标定（区带高度和坏线位置，标定一次就不用再动）");

            Btn(pg, ref y, "重新标定区带与坏线…", delegate
            {
                if (onCalibrate != null) onCalibrate();
            });

            y += 14;
            y = Hint(pg, y, "全屏");

            string[] fsIds = new string[] { "avoid", "overlay" };
            Combo(pg, ref y, "全屏时",
                  new string[] { "让全屏窗口避让（视频停在区带上方）", "只覆盖显示（不缩放窗口）" },
                  IndexOf(fsIds, p.FullscreenMode, 0),
                  delegate(int i) { p.FullscreenMode = fsIds[i]; Fire(); });

            y += 16;
            Btn(pg, ref y, "打开配置目录", delegate
            {
                if (onOpenDir != null) onOpenDir();
            });

            y += 14;
            y = Hint(pg, y, "启动与引导");

            // 自启动的真实状态在注册表里，这里直接读它 —— 只在 ini 里记一份会和任务管理器打架
            bool autoOn = false;
            try { autoOn = Autostart.IsOn(); } catch { }
            Check(pg, ref y, "开机自动启动", autoOn, delegate
            {
                bool want = !Autostart.IsOn();
                if (!Autostart.Set(want))
                {
                    // 注册表被策略锁住之类：别让界面显示成一个假状态
                    MessageBox.Show("没能写入启动项。可能被组策略或安全软件拦住了。",
                        "DeadBand", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            });

            Btn(pg, ref y, "重新运行首次设置引导…", delegate
            {
                if (onOnboard == null) return;
                // 先把这个窗口关掉：引导是模态的，叠着开会让用户以为点空了。
                // 引导本身由 App 在 ShowDialog 返回之后启动。
                Close();
                onOnboard();
            });
        }

        private void Fire()
        {
            if (changed != null) changed();
        }
    }
}
