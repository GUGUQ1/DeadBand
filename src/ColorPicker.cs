using System;
using System.Drawing;
using System.Windows.Forms;

namespace DeadBand
{
    /// <summary>
    /// 通用取色器：背景色和网格色共用。拖动滑块实时应用，边拖边看。
    ///
    /// 布局约定：所有控件按纵向分带排列，任何两块的矩形都不许相交。
    /// 改坐标后请跑一次 `DeadBand.exe --ui-check`，别靠肉眼。
    /// </summary>
    internal sealed class ColorPickerForm : Form
    {
        /// <summary>灰阶预设：背景/网格用。</summary>
        public static Color[] GraySwatches()
        {
            int[] v = new int[] { 0x00,0x14,0x20,0x30,0x40,0x50,0x60,0x70,
                                  0x80,0x90,0xA0,0xB0,0xC0,0xD0,0xE0,0xFF };
            var a = new Color[v.Length];
            for (int i = 0; i < v.Length; i++) a[i] = Color.FromArgb(v[i], v[i], v[i]);
            return a;
        }

        /// <summary>像素时钟常用色：高饱和纯色，而不是灰阶。</summary>
        public static Color[] ClockSwatches()
        {
            return new Color[]
            {
                Color.FromArgb(0x00,0xD8,0xFF),  // 青
                Color.FromArgb(0x18,0xF0,0x7A),  // 荧光绿
                Color.FromArgb(0xFF,0xD6,0x0A),  // 琥珀
                Color.FromArgb(0xFF,0x8A,0x00),  // 橙
                Color.FromArgb(0xFF,0x3B,0x30),  // 红
                Color.FromArgb(0xFF,0x2D,0x95),  // 洋红
                Color.FromArgb(0xA0,0x6C,0xFF),  // 紫
                Color.FromArgb(0x3C,0x7B,0xFF),  // 蓝
                Color.FromArgb(0xFF,0xFF,0xFF),  // 白
                Color.FromArgb(0xB0,0xB6,0xC0),  // 灰白
                Color.FromArgb(0x5A,0xFF,0xE0),  // 冰蓝
                Color.FromArgb(0xE8,0xF0,0x50)   // 亮黄
            };
        }

        // ---- 版面（全部硬编码在这里，便于一次性核对）----
        private const int PadX = 24;
        private const int ContentW = 592;
        private const int RowSwatchY = 18;
        private const int SwatchH = 96;
        private const int HintY = 18;
        private const int HintH = 130;
        private const int SliderRow1 = 158;
        private const int SliderRowGap = 60;
        private const int PresetLabelY = 344;
        private const int PresetRowY = 366;
        private const int ButtonY = 424;

        private readonly Action<Color> onChange;
        private readonly Color original;

        private TrackBar tbR, tbG, tbB;
        private Label lbR, lbG, lbB;
        private Panel swatch;
        private Label lbHex;
        private bool suppress;

        public ColorPickerForm(string title, string hintText, Color initial, Color[] swatches, Action<Color> applyCallback)
        {
            onChange = applyCallback;
            original = initial;
            if (swatches == null) swatches = GraySwatches();

            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            ClientSize = new Size(PadX * 2 + ContentW, 480);
            BackColor = Color.FromArgb(24, 26, 32);
            ForeColor = Color.FromArgb(230, 234, 242);
            KeyPreview = true;

            try
            {
                var scr = Screen.PrimaryScreen.WorkingArea;
                Location = new Point(scr.Left + (scr.Width - Width) / 2, scr.Top + 70);
            }
            catch { }

            // ---- 第 1 带：预览色块（左）----
            swatch = new Panel();
            swatch.Location = new Point(PadX, RowSwatchY);
            swatch.Size = new Size(240, SwatchH);
            swatch.BorderStyle = BorderStyle.FixedSingle;
            Controls.Add(swatch);

            lbHex = new Label();
            lbHex.Location = new Point(PadX, RowSwatchY + SwatchH + 8);
            lbHex.Size = new Size(240, 28);
            lbHex.Font = new Font("Consolas", 11f);
            lbHex.ForeColor = Color.FromArgb(0, 216, 255);
            Controls.Add(lbHex);

            // ---- 第 1 带：说明文字（右）----
            var hint = new Label();
            hint.Location = new Point(PadX + 262, HintY);
            hint.Size = new Size(ContentW - 262, HintH);
            hint.Font = new Font("Microsoft YaHei UI", 9f);
            hint.ForeColor = Color.FromArgb(150, 164, 184);
            hint.Text = hintText;
            Controls.Add(hint);

            // ---- 第 2 带：三个滑条 ----
            tbR = AddSlider("R", SliderRow1, out lbR);
            tbG = AddSlider("G", SliderRow1 + SliderRowGap, out lbG);
            tbB = AddSlider("B", SliderRow1 + SliderRowGap * 2, out lbB);

            // ---- 第 3 带：灰阶预设 ----
            var presetLabel = new Label();
            presetLabel.Location = new Point(PadX, PresetLabelY);
            presetLabel.Size = new Size(140, 20);
            presetLabel.ForeColor = Color.FromArgb(150, 164, 184);
            presetLabel.Text = "预设色：";
            Controls.Add(presetLabel);

            var flow = new FlowLayoutPanel();
            flow.Location = new Point(PadX, PresetRowY);
            flow.Size = new Size(ContentW, 34);
            flow.WrapContents = false;
            Controls.Add(flow);
            foreach (Color sw in swatches)
            {
                Color val = sw;
                var btn = new Button();
                btn.Size = new Size(30, 28);
                btn.Margin = new Padding(1);
                btn.FlatStyle = FlatStyle.Flat;
                btn.BackColor = val;
                btn.FlatAppearance.BorderColor = Color.FromArgb(90, 100, 120);
                btn.Text = "";
                var tip = new ToolTip();
                tip.SetToolTip(btn, string.Format("#{0:X2}{1:X2}{2:X2}", val.R, val.G, val.B));
                btn.Click += new EventHandler(delegate { SetRgb(val.R, val.G, val.B); });
                flow.Controls.Add(btn);
            }

            // ---- 第 4 带：按钮 ----
            var reset = MakeButton("恢复默认", PadX, ButtonY, 110);
            reset.Click += new EventHandler(delegate { SetRgb(original.R, original.G, original.B); });

            var cancel = MakeButton("取消", PadX + ContentW - 92, ButtonY, 92);
            cancel.Click += new EventHandler(delegate { Restore(); DialogResult = DialogResult.Cancel; Close(); });

            var ok = MakeButton("确定", PadX + ContentW - 200, ButtonY, 92);
            ok.Click += new EventHandler(delegate { DialogResult = DialogResult.OK; Close(); });

            SetRgb(initial.R, initial.G, initial.B);
        }

        private Button MakeButton(string text, int x, int y, int w)
        {
            var b = new Button();
            b.Text = text;
            b.Location = new Point(x, y);
            b.Size = new Size(w, 32);
            Controls.Add(b);
            return b;
        }

        private TrackBar AddSlider(string name, int y, out Label valueLabel)
        {
            var lb = new Label();
            lb.Text = name;
            lb.Location = new Point(PadX, y + 4);
            lb.Size = new Size(20, 22);
            lb.ForeColor = Color.FromArgb(200, 210, 226);
            Controls.Add(lb);

            var tb = new TrackBar();
            tb.Minimum = 0;
            tb.Maximum = 255;
            tb.TickStyle = TickStyle.None;
            tb.Location = new Point(PadX + 32, y);
            tb.Size = new Size(440, 26);
            tb.SmallChange = 1;
            tb.LargeChange = 8;
            tb.Scroll += new EventHandler(delegate { Apply(); });
            Controls.Add(tb);

            var vl = new Label();
            vl.Location = new Point(PadX + 484, y + 4);
            vl.Size = new Size(60, 26);
            vl.Font = new Font("Consolas", 10f);
            vl.ForeColor = Color.FromArgb(0, 216, 255);
            Controls.Add(vl);

            valueLabel = vl;
            return tb;
        }

        private void SetRgb(int r, int g, int b)
        {
            suppress = true;
            tbR.Value = Clamp255(r);
            tbG.Value = Clamp255(g);
            tbB.Value = Clamp255(b);
            suppress = false;
            Apply();
        }

        private static int Clamp255(int v)
        {
            if (v < 0) return 0;
            if (v > 255) return 255;
            return v;
        }

        private void Apply()
        {
            if (suppress) return;
            Color c = Color.FromArgb(tbR.Value, tbG.Value, tbB.Value);
            swatch.BackColor = c;
            lbHex.Text = string.Format("#{0:X2}{1:X2}{2:X2}     R{0} G{1} B{2}", c.R, c.G, c.B);
            lbR.Text = c.R.ToString();
            lbG.Text = c.G.ToString();
            lbB.Text = c.B.ToString();
            if (onChange != null) onChange(c);
        }

        private void Restore()
        {
            if (onChange != null) onChange(original);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { Restore(); DialogResult = DialogResult.Cancel; Close(); }
            base.OnKeyDown(e);
        }

        /// <summary>
        /// 开发期自检：任两个直接子控件矩形相交、或 Label 文字被裁，都写进 ui-check.txt。
        /// 用 DeadBand.exe --ui-check 触发。
        /// </summary>
        public void DumpOverlaps(string path)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("client=" + ClientSize);
            int bad = 0;
            for (int i = 0; i < Controls.Count; i++)
            {
                for (int j = i + 1; j < Controls.Count; j++)
                {
                    Rectangle a = Controls[i].Bounds;
                    Rectangle b = Controls[j].Bounds;
                    if (!a.IntersectsWith(b)) continue;
                    bad++;
                    sb.AppendLine("OVERLAP  [" + Describe(Controls[i]) + " " + a + "]  x  [" +
                                  Describe(Controls[j]) + " " + b + "]");
                }
            }
            foreach (Control c in Controls)
            {
                sb.AppendLine("ctrl " + Describe(c) + " " + c.Bounds + " visible=" + c.Visible);
                var lb = c as Label;
                if (lb != null && !string.IsNullOrEmpty(lb.Text))
                {
                    Size need = TextRenderer.MeasureText(lb.Text, lb.Font, new Size(int.MaxValue, int.MaxValue),
                        TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                    need.Width += 4;
                    need.Height += 4;
                    if (need.Width > lb.Width || need.Height > lb.Height)
                    {
                        bad++;
                        sb.AppendLine("CLIPPED  [" + Describe(c) + "] 需要 " + need + " 实际 " + lb.Size);
                    }
                }
            }
            sb.AppendLine("overlaps=" + bad);
            try { System.IO.File.WriteAllText(path, sb.ToString()); } catch { }
        }

        private static string Describe(Control c)
        {
            string t = c.Text == null ? "" : c.Text.Replace("\r", " ").Replace("\n", " ");
            if (t.Length > 12) t = t.Substring(0, 12);
            return c.GetType().Name + "{" + t + "}";
        }
    }
}
