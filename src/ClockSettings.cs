using System;
using System.Drawing;
using System.Windows.Forms;

namespace DeadBand
{
    /// <summary>
    /// 像素时钟的大小与位置。
    ///
    /// 刻意**不放在标定界面里**：标定是一次性的、敏感的，混入日常微调项容易误触。
    ///
    /// 关键：这里的调节单位是**网格单元格**，不是像素。
    /// 因为绘制时字号和位置都被吸附到 cell 的整数倍，按 1px 调是看不出变化的
    /// （按好几下才跨过一格）。所以按钮直接按"格"步进，一下动一格。
    /// </summary>
    internal sealed class ClockSettingsForm : Form
    {
        private const int PadX = 24;
        private const int RowY0 = 22;
        private const int RowGap = 50;
        private const int ClientW = 600;
        private const int ClientH = 300;

        private readonly ZoneProfile work;
        private readonly Action<ZoneProfile> onChange;
        private readonly int origPx, origDx, origDy;

        private Label lbPx, lbDx, lbDy;
        private int cell;
        private int zoneH;
        private bool snap;
        private int unit;

        public ClockSettingsForm(ZoneProfile profile, int zoneHeight, Action<ZoneProfile> applyCallback)
        {
            work = profile;
            zoneH = zoneHeight > 0 ? zoneHeight : 99;
            onChange = applyCallback;
            origPx = profile.ClockPx;
            origDx = profile.ClockOffsetX;
            origDy = profile.ClockOffsetY;

            // 只有栅格开着时，绘制才会吸附到格子；关掉时是精确像素，没有吸附。
            // 所以调节单位必须跟着实际行为走，不能用写死的兜底值骗人。
            snap = profile.GridEnabled;
            cell = snap ? profile.GridCell : 1;
            if (cell < 1) cell = 1;
            if (snap && cell < 2) cell = 2;
            unit = cell;

            Text = "DeadBand — 像素时钟 大小与位置";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            ClientSize = new Size(ClientW, ClientH);
            BackColor = Color.FromArgb(24, 26, 32);
            ForeColor = Color.FromArgb(230, 234, 242);
            KeyPreview = true;

            try
            {
                var scr = Screen.PrimaryScreen.WorkingArea;
                Location = new Point(scr.Left + (scr.Width - Width) / 2, scr.Top + 70);
            }
            catch { }

            var note = new Label();
            note.Location = new Point(PadX, 18);
            note.Size = new Size(ClientW - PadX * 2, 46);
            note.ForeColor = Color.FromArgb(150, 166, 188);
            note.Font = new Font("Microsoft YaHei UI", 9f);
            note.Text = snap
                ? ("调节单位是网格单元（当前 " + cell + " px 一格），所以每按一下都会" +
                   "看到数字动一格。\r\n绘制时字号和位置都会吸附到格子，按像素微调是看不出变化的。")
                : "网格未启用：不做吸附，按像素精确调节，每按一下都会看到变化。";
            Controls.Add(note);

            AddRow("字号", 78, out lbPx);
            AddRow("水平位移", 128, out lbDx);
            AddRow("垂直位移", 178, out lbDy);

            var reset = MakeButton("恢复默认", PadX, 238, 110);
            reset.Click += new EventHandler(delegate
            {
                work.ClockPx = 0;
                work.ClockOffsetX = 0;
                work.ClockOffsetY = 0;
                Refresh1();
                ApplyNow();
            });

            var cancel = MakeButton("取消", ClientW - PadX - 92, 238, 92);
            cancel.Click += new EventHandler(delegate
            {
                work.ClockPx = origPx;
                work.ClockOffsetX = origDx;
                work.ClockOffsetY = origDy;
                ApplyNow();
                DialogResult = DialogResult.Cancel;
                Close();
            });

            var ok = MakeButton("确定", ClientW - PadX - 92 - 100, 238, 92);
            ok.Click += new EventHandler(delegate { DialogResult = DialogResult.OK; Close(); });

            Refresh1();
        }

        private void AddRow(string name, int y, out Label valueLabel)
        {
            var lb = new Label();
            lb.Text = name;
            lb.Location = new Point(PadX, y + 6);
            lb.Size = new Size(96, 22);
            lb.ForeColor = Color.FromArgb(200, 210, 226);
            Controls.Add(lb);

            int idx = name == "字号" ? 0 : (name == "水平位移" ? 1 : 2);

            string u = snap ? " 格" : " px";
            if (idx == 0)
            {
                StepButton("-1" + u, 128, y, 72, idx, -1);
                StepButton("+1" + u, 206, y, 72, idx, +1);
            }
            else
            {
                StepButton("-5" + u, 128, y, 66, idx, -5);
                StepButton("-1" + u, 198, y, 66, idx, -1);
                StepButton("+1" + u, 268, y, 66, idx, +1);
                StepButton("+5" + u, 338, y, 66, idx, +5);
            }

            var vl = new Label();
            vl.Location = new Point(414, y + 6);
            vl.Size = new Size(160, 26);
            vl.Font = new Font("Consolas", 10.5f);
            vl.ForeColor = Color.FromArgb(0, 216, 255);
            vl.TextAlign = ContentAlignment.MiddleLeft;
            Controls.Add(vl);

            valueLabel = vl;
        }

        private Button StepButton(string text, int x, int y, int w, int idx, int cells)
        {
            var b = new Button();
            b.Text = text;
            b.Location = new Point(x, y);
            b.Size = new Size(w, 30);
            b.Click += new EventHandler(delegate { Step(idx, cells); });
            Controls.Add(b);
            return b;
        }

        private void Step(int idx, int cells)
        {
            int delta = cells * unit;

            if (idx == 0)
            {
                int cur = work.ClockPx >= 2 ? work.ClockPx : AutoPx();
                cur += delta;
                if (cur < cell) cur = cell;
                if (cur > 240) cur = 240;
                work.ClockPx = cur;
            }
            else if (idx == 1)
            {
                work.ClockOffsetX = Clamp(work.ClockOffsetX + delta, -2000, 2000);
            }
            else
            {
                work.ClockOffsetY = Clamp(work.ClockOffsetY + delta, -400, 400);
            }

            Refresh1();
            ApplyNow();
        }

        private int AutoPx()
        {
            // 和 ClockProvider 里的自动尺寸算法保持一致
            if (!snap) return Math.Max(2, (int)Math.Floor(zoneH * 0.94 / 7.0));
            int n = (int)Math.Floor(zoneH * 0.94 / (7.0 * cell));
            if (n < 1) n = 1;
            return n * cell;
        }

        private static int Clamp(int v, int lo, int hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
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

        private void Refresh1()
        {
            if (snap)
            {
                lbPx.Text = work.ClockPx >= 2
                    ? work.ClockPx + " px  (" + (work.ClockPx / cell) + " 格)"
                    : "自动";
                lbDx.Text = (work.ClockOffsetX / cell) + " 格   (" + work.ClockOffsetX + " px)";
                lbDy.Text = (work.ClockOffsetY / cell) + " 格   (" + work.ClockOffsetY + " px)";
            }
            else
            {
                lbPx.Text = work.ClockPx >= 2 ? work.ClockPx + " px" : "自动";
                lbDx.Text = work.ClockOffsetX + " px";
                lbDy.Text = work.ClockOffsetY + " px";
            }
        }

        private void ApplyNow()
        {
            if (onChange != null) onChange(work);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape)
            {
                work.ClockPx = origPx;
                work.ClockOffsetX = origDx;
                work.ClockOffsetY = origDy;
                ApplyNow();
                DialogResult = DialogResult.Cancel;
                Close();
            }
            base.OnKeyDown(e);
        }

        /// <summary>开发期自检：子控件两两相交 / Label 文字被裁，写进文件。</summary>
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
                    sb.AppendLine("OVERLAP  [" + Controls[i].GetType().Name + " " + Controls[i].Text +
                                  " " + a + "] x [" + Controls[j].GetType().Name + " " +
                                  Controls[j].Text + " " + b + "]");
                }
            }
            foreach (Control c in Controls)
            {
                var lb = c as Label;
                if (lb == null || string.IsNullOrEmpty(lb.Text)) continue;
                Size need = TextRenderer.MeasureText(lb.Text, lb.Font, new Size(int.MaxValue, int.MaxValue),
                    TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                need.Width += 4; need.Height += 4;
                if (need.Width > lb.Width || need.Height > lb.Height)
                {
                    bad++;
                    sb.AppendLine("CLIPPED  [" + lb.Text + "] 需要 " + need + " 实际 " + lb.Size);
                }
            }
            sb.AppendLine("overlaps=" + bad);
            try { System.IO.File.WriteAllText(path, sb.ToString()); } catch { }
        }
    }
}
