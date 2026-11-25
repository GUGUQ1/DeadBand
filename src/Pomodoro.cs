using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Text;

namespace DeadBand
{
    /// <summary>
    /// 番茄钟的持久化。存在配置目录下的 pomodoro.ini，跟显示器配置分开 ——
    /// 计时器是全局的，不该跟着某台显示器走。
    ///
    /// 运行中会把「保存时刻」一起写下去：DeadBand 崩溃/重启后，
    /// 按真实流逝时间把剩余时间往前推，不会把已经过掉的时间当成没走。
    /// </summary>
    internal static class PomodoroStore
    {
        private static string FilePath
        {
            get { return Path.Combine(ProfileStore.DataDir, "pomodoro.ini"); }
        }

        public static void Save(int minutes, double remain, bool running)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# DeadBand 番茄钟");
                sb.AppendLine("minutes=" + minutes.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("remain=" + remain.ToString("0.###", CultureInfo.InvariantCulture));
                sb.AppendLine("running=" + (running ? "1" : "0"));
                sb.AppendLine("savedat=" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        public static void Load(out int minutes, out double remain, out bool running)
        {
            minutes = 25;
            remain = 25 * 60;
            running = false;

            try
            {
                if (!File.Exists(FilePath)) return;

                DateTime savedAt = DateTime.UtcNow;
                string[] lines = File.ReadAllLines(FilePath);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;

                    string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string v = line.Substring(eq + 1).Trim();

                    if (k == "minutes")
                    {
                        int m;
                        if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out m)) minutes = m;
                    }
                    else if (k == "remain")
                    {
                        double r;
                        if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out r)) remain = r;
                    }
                    else if (k == "running")
                    {
                        running = (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase));
                    }
                    else if (k == "savedat")
                    {
                        DateTime t;
                        if (DateTime.TryParse(v, CultureInfo.InvariantCulture,
                                              DateTimeStyles.RoundtripKind, out t))
                            savedAt = t;
                    }
                }

                if (minutes < 1) minutes = 1;
                if (minutes > 180) minutes = 180;

                if (running)
                {
                    // 按真实流逝时间往前推
                    double elapsed = (DateTime.UtcNow - savedAt).TotalSeconds;
                    if (elapsed > 0) remain -= elapsed;
                    if (remain <= 0) { remain = 0; running = false; }
                }
                if (remain > minutes * 60.0) remain = minutes * 60.0;
            }
            catch { }
        }
    }

    /// <summary>
    /// 番茄钟：所有操作都做在区带上，不弹窗、不进托盘。
    ///
    /// 版式（2560×99 为例）：
    ///   [🍅] [▶] [↻]  24:37                                    [⚙]
    ///   点齿轮后才展开时长调节：
    ///   [🍅] [▶] [↻]  24:37              15 25 45 60 [−][+]  [⚙]
    ///
    ///   背景是「水位」进度条，从 x=0 铺满整屏，随倒计时从左往右涨。
    ///
    /// 点击：窗口层做了 WM_NCHITTEST，只有按钮矩形返回 HTCLIENT，
    /// 其余地方穿透到下面的窗口 —— 不会把屏幕底部 99px 全占了。
    /// </summary>
    internal sealed class PomodoroProvider : IProvider, IProviderInput
    {
        private const int BtnSize = 64;      // 播放/重置
        private const int BtnSmall = 56;     // 齿轮/微调
        private const int Gap = 10;
        private const int MarginX = 16;
        private const int PresetW = 100;
        private const int IconBox = 40;
        private const int TomatoW = 44;

        // 水位用浅蓝；番茄图标是红绿，压在蓝底上反而更跳
        private static readonly Color Water = Color.FromArgb(0x4F, 0xC3, 0xF7);      // 浅蓝
        private static readonly Color WaterLit = Color.FromArgb(0x9B, 0xE1, 0xFF);   // 亮边
        private static readonly Color WaterDim = Color.FromArgb(0x1B, 0x5C, 0x7A);   // 顶部暗边

        // 番茄图标：红果 + 绿蒂。纯单色时会看不出是番茄，颜色是它唯一的身份。
        private static readonly Color FruitRed = Color.FromArgb(0xFF, 0x45, 0x36);
        private static readonly Color LeafGreen = Color.FromArgb(0x55, 0xC8, 0x5A);

        private static readonly int[] Presets = new int[] { 15, 25, 45, 60 };

        private int minutes = 25;
        private double remainSeconds = 25 * 60;
        private bool running;
        private DateTime endUtc;
        private DateTime finishedAt = DateTime.MinValue;
        private DateTime lastSaveUtc = DateTime.MinValue;

        private int hoverX = -1, hoverY = -1;
        private string hoverId = "";

        // 时长调节面板：默认收起，点齿轮展开，闲置几秒自动收回
        private bool adjustOpen;
        private DateTime adjustDeadline = DateTime.MinValue;
        private const double AdjustHoldSeconds = 6.0;

        private sealed class Spot
        {
            public string Id;
            public Rectangle Rect;
        }

        public PomodoroProvider()
        {
            int m;
            double r;
            bool run;
            PomodoroStore.Load(out m, out r, out run);
            minutes = m;
            remainSeconds = r;
            running = run;
            if (running)
            {
                endUtc = DateTime.UtcNow.AddSeconds(remainSeconds);
                if (remainSeconds <= 0) running = false;
            }
        }

        public string Id { get { return "pomodoro"; } }
        public string DisplayName { get { return "番茄钟"; } }
        public Color Background { get { return Color.Black; } }

        // ---------------- 状态 ----------------
        private double TotalSeconds { get { return Math.Max(1, minutes * 60.0); } }

        private void Tick()
        {
            if (!running) return;
            remainSeconds = (endUtc - DateTime.UtcNow).TotalSeconds;
            if (remainSeconds > 0) return;

            remainSeconds = 0;
            running = false;
            finishedAt = DateTime.UtcNow;
            PomodoroStore.Save(minutes, remainSeconds, false);
            return;
        }

        private void Reset()
        {
            remainSeconds = minutes * 60.0;
            running = false;
            finishedAt = DateTime.MinValue;
            PomodoroStore.Save(minutes, remainSeconds, false);
        }

        private void TogglePlay()
        {
            if (running)
            {
                Tick();
                running = false;
                PomodoroStore.Save(minutes, remainSeconds, false);
                return;
            }

            // 完成后再按开始 = 重来一轮
            if (remainSeconds <= 0.5)
            {
                remainSeconds = minutes * 60.0;
                finishedAt = DateTime.MinValue;
            }
            running = true;
            endUtc = DateTime.UtcNow.AddSeconds(remainSeconds);
            PomodoroStore.Save(minutes, remainSeconds, true);
        }

        private void SetMinutes(int m)
        {
            if (m < 1) m = 1;
            if (m > 180) m = 180;
            minutes = m;
            remainSeconds = m * 60.0;
            running = false;
            finishedAt = DateTime.MinValue;
            PomodoroStore.Save(minutes, remainSeconds, false);
        }

        private void KeepAdjustOpen()
        {
            adjustDeadline = DateTime.UtcNow.AddSeconds(AdjustHoldSeconds);
        }

        // ---------------- 版式 ----------------
        private List<Spot> BuildSpots(int w, int h, bool showAdjust)
        {
            var list = new List<Spot>();
            int y = (h - BtnSize) / 2;
            if (y < 0) y = 0;
            int sy = (h - BtnSmall) / 2;
            if (sy < 0) sy = 0;

            // 左：番茄图标（纯装饰）占位后是播放/重置
            int x = MarginX + TomatoW + 12;
            list.Add(MakeSpot("toggle", x, y, BtnSize, BtnSize));
            x += BtnSize + Gap;
            list.Add(MakeSpot("reset", x, y, BtnSize, BtnSize));

            // 右：齿轮永远在
            int gearX = w - MarginX - BtnSmall;
            list.Add(MakeSpot("gear", gearX, sy, BtnSmall, BtnSmall));

            if (!showAdjust) return list;

            int plusX = gearX - 8 - BtnSmall;
            int minusX = plusX - 8 - BtnSmall;
            list.Add(MakeSpot("plus", plusX, sy, BtnSmall, BtnSmall));
            list.Add(MakeSpot("minus", minusX, sy, BtnSmall, BtnSmall));

            int px = minusX - 12;
            for (int i = Presets.Length - 1; i >= 0; i--)
            {
                px -= PresetW;
                list.Add(MakeSpot("preset:" + Presets[i].ToString(CultureInfo.InvariantCulture),
                                  px, y, PresetW, BtnSize));
                px -= 8;
            }
            return list;
        }

        private static Spot MakeSpot(string id, int x, int y, int width, int height)
        {
            var s = new Spot();
            s.Id = id;
            s.Rect = new Rectangle(x, y, width, height);
            return s;
        }

        private static Spot FindSpot(List<Spot> spots, int x, int y)
        {
            for (int i = 0; i < spots.Count; i++)
                if (spots[i].Rect.Contains(x, y)) return spots[i];
            return null;
        }

        // ---------------- 交互 ----------------
        public bool HitTest(int x, int y)
        {
            return FindSpot(BuildSpots(LastW, LastH, adjustOpen), x, y) != null;
        }

        public void OnClick(int x, int y)
        {
            Spot s = FindSpot(BuildSpots(LastW, LastH, adjustOpen), x, y);
            if (s == null) return;

            if (s.Id == "gear")
            {
                adjustOpen = !adjustOpen;
                if (adjustOpen) KeepAdjustOpen();
                return;
            }
            if (s.Id == "toggle") { TogglePlay(); return; }
            if (s.Id == "reset") { Reset(); return; }
            if (s.Id == "plus") { SetMinutes(minutes + 1); KeepAdjustOpen(); return; }
            if (s.Id == "minus") { SetMinutes(minutes - 1); KeepAdjustOpen(); return; }

            if (s.Id.StartsWith("preset:", StringComparison.Ordinal))
            {
                int m;
                if (int.TryParse(s.Id.Substring(7), NumberStyles.Integer, CultureInfo.InvariantCulture, out m))
                {
                    SetMinutes(m);
                    KeepAdjustOpen();
                }
            }
        }

        public void SetHover(int x, int y)
        {
            hoverX = x;
            hoverY = y;
        }

        private int LastW = 1, LastH = 1;
        private int lastGridCell = 4;

        /// <summary>共存模式：水位半透明，控件不透明。</summary>
        public bool Coexist;

        /// <summary>共存时左边被占掉的宽度（番茄图标 + 播放 + 重置 + 倒计时）。</summary>
        public int ReservedLeft(int w, int h, ZoneProfile p)
        {
            if (!Coexist) return 0;
            int px = TimePx(h, CellOf(p));
            int gap = px * 2;
            int tw = MeasureText(CountdownText(), px, gap);
            int left = MarginX + TomatoW + 12 + BtnSize + Gap + BtnSize;
            return left + 40 + tw;
        }

        /// <summary>共存时右边被闹钟占掉的宽度。</summary>
        public int ReservedRight(int w, int h)
        {
            if (!Coexist) return 0;
            return MarginX + BtnSmall + 12;
        }

        private string CountdownText()
        {
            int totalSec = (int)Math.Ceiling(remainSeconds);
            if (totalSec < 0) totalSec = 0;
            return (totalSec / 60).ToString("00", CultureInfo.InvariantCulture) + ":" +
                   (totalSec % 60).ToString("00", CultureInfo.InvariantCulture);
        }

        private static int CellOf(ZoneProfile p)
        {
            int cell = p != null && p.GridEnabled ? p.GridCell : 4;
            if (cell < 2) cell = 4;
            return cell;
        }

        private static int TimePx(int h, int cell)
        {
            if (cell < 2) cell = 4;
            int px = Math.Max(2, (h - 4) / 7);
            px = (px / cell) * cell;
            if (px < cell) px = cell;
            return px;
        }

        // ---------------- 分层用：拆成"水位"和"控件" ----------------
        /// <summary>水位 + 完成闪烁。独占时画在最上层；共存时画在所有层下面当背景。</summary>
        public void RenderWater(Graphics g, int w, int h, ZoneProfile p)
        {
            if (w <= 0 || h <= 0) return;
            LastW = w;
            LastH = h;
            Tick();

            int cell = CellOf(p);
            lastGridCell = cell;

            DrawWaterLevel(g, w, h, cell);
            DrawFlash(g, w, h);
        }

        /// <summary>按钮和倒计时。</summary>
        public void RenderControls(Graphics g, int w, int h, ZoneProfile p)
        {
            if (w <= 0 || h <= 0) return;
            LastW = w;
            LastH = h;
            lastGridCell = CellOf(p);

            Color ink = p.ClockColor;
            if (adjustOpen && DateTime.UtcNow > adjustDeadline) adjustOpen = false;

            List<Spot> spots = BuildSpots(w, h, adjustOpen);
            Spot hovered = FindSpot(spots, hoverX, hoverY);
            hoverId = hovered == null ? "" : hovered.Id;
            if (adjustOpen && hovered != null && IsAdjustSpot(hovered.Id)) KeepAdjustOpen();

            DrawTomato(g, w, h, p.BaseColor);
            for (int i = 0; i < spots.Count; i++)
                DrawSpot(g, spots[i], spots[i].Id == hoverId, ink);
            DrawTime(g, w, h, ink, spots);
        }

        // ---------------- 渲染（独占） ----------------
        public void Render(Graphics g, int w, int h, ZoneProfile p, double t)
        {
            if (w <= 0 || h <= 0) return;
            Tick();
            MaybePeriodicSave();

            RenderWater(g, w, h, p);
            RenderControls(g, w, h, p);
        }

        private static bool IsAdjustSpot(string id)
        {
            if (id == "plus" || id == "minus") return true;
            return id.StartsWith("preset:", StringComparison.Ordinal);
        }

        private void MaybePeriodicSave()
        {
            if (!running) return;
            if ((DateTime.UtcNow - lastSaveUtc).TotalSeconds < 20) return;
            lastSaveUtc = DateTime.UtcNow;
            PomodoroStore.Save(minutes, remainSeconds, true);
        }

        /// <summary>进度水位：从 x=0 铺满整屏，随倒计时从左往右涨。</summary>
        private void DrawWaterLevel(Graphics g, int w, int h, int cell)
        {
            double progress = 1.0 - (remainSeconds / TotalSeconds);
            if (progress < 0) progress = 0;
            if (progress > 1) progress = 1;

            int fillW = (int)Math.Round(progress * w);
            if (cell >= 2) fillW = (fillW / cell) * cell;   // 对齐网格，避免锯齿
            if (fillW > w) fillW = w;
            if (fillW <= 0) return;

            // 主体：浅蓝。alpha 太低会被黑底拉成"钢蓝"，所以给到 190 才真的是浅蓝。
            // 共存模式下压到 70，免得把底下的时钟和字幕糊掉。
            int fillAlpha = Coexist ? 70 : 190;
            using (var b = new SolidBrush(Color.FromArgb(fillAlpha, Water)))
                g.FillRectangle(b, 0, 0, fillW, h);

            // 顶部一条稍暗的边，让水位有厚度
            using (var b = new SolidBrush(Color.FromArgb(150, WaterDim)))
                g.FillRectangle(b, 0, 0, fillW, Math.Max(1, cell / 2));

            // 边界上的亮边：一眼能看出还剩多少
            int edge = Math.Max(3, cell);
            if (fillW < w)
            {
                int ex = fillW - edge;
                if (ex < 0) ex = 0;
                using (var b = new SolidBrush(Color.FromArgb(240, WaterLit)))
                    g.FillRectangle(b, ex, 0, Math.Min(edge, w - ex), h);
            }
        }

        /// <summary>完成时整条闪三下。只闪，不出声。</summary>
        private void DrawFlash(Graphics g, int w, int h)
        {
            if (finishedAt == DateTime.MinValue) return;
            double ms = (DateTime.UtcNow - finishedAt).TotalMilliseconds;
            if (ms > 1800) return;
            if (((int)(ms / 300)) % 2 != 0) return;

            using (var b = new SolidBrush(Color.FromArgb(120, Water)))
                g.FillRectangle(b, 0, 0, w, h);
        }

        // ---------------- 番茄图标 ----------------
        // 11×10 的点阵：G = 绿蒂绿叶，R = 红果。
        // 上半部是蒂和叶子，下半部是果实 —— 番茄能认出来靠的就是这两块颜色。
        private static readonly string[] TomatoIcon = new string[]
        {
            ".....G.....",
            "..G..G..G..",
            "...GGGGG...",
            "..RRRRRRR..",
            ".RRRRRRRRR.",
            "RRRRRRRRRRR",
            "RRRRRRRRRRR",
            ".RRRRRRRRR.",
            "..RRRRRRR..",
            "...RRRRR..."
        };

        private void DrawTomato(Graphics g, int w, int h, Color baseColor)
        {
            int cols = TomatoIcon[0].Length;
            int rows = TomatoIcon.Length;
            int s = Math.Max(1, Math.Min(TomatoW / cols, (h - 10) / rows));
            int bw = cols * s, bh = rows * s;
            int ox = MarginX + (TomatoW - bw) / 2;
            int oy = (h - bh) / 2;

            // 先铺一层深色投影：水位是暗红的，红果直接画上去会糊成一片
            using (var shadow = new SolidBrush(baseColor))
            {
                for (int y = 0; y < rows; y++)
                {
                    string line = TomatoIcon[y];
                    for (int x = 0; x < line.Length; x++)
                    {
                        if (line[x] == '.') continue;
                        g.FillRectangle(shadow, ox + (x + 1) * s, oy + (y + 1) * s, s, s);
                    }
                }
            }

            using (var body = new SolidBrush(FruitRed))
            using (var leaf = new SolidBrush(LeafGreen))
            {
                for (int y = 0; y < rows; y++)
                {
                    string line = TomatoIcon[y];
                    for (int x = 0; x < line.Length; x++)
                    {
                        char ch = line[x];
                        if (ch == '.') continue;
                        Brush b = (ch == 'G') ? leaf : body;
                        g.FillRectangle(b, ox + x * s, oy + y * s, s, s);
                    }
                }
            }
        }

        // ---------------- 按钮 ----------------
        private void DrawSpot(Graphics g, Spot s, bool hover, Color ink)
        {
            Rectangle r = s.Rect;

            if (hover)
            {
                using (var b = new SolidBrush(Color.FromArgb(46, ink)))
                    g.FillRectangle(b, r);
                using (var pen = new Pen(Color.FromArgb(190, ink), 2f))
                    g.DrawRectangle(pen, r.X + 1, r.Y + 1, r.Width - 3, r.Height - 3);
            }

            int box = Math.Min(IconBox, Math.Min(r.Width, r.Height) - 14);
            if (box < 12) box = Math.Min(r.Width, r.Height);
            Rectangle ib = new Rectangle(r.X + (r.Width - box) / 2, r.Y + (r.Height - box) / 2, box, box);

            if (s.Id == "toggle")
            {
                if (running) DrawPauseIcon(g, ib, ink);
                else DrawPlayIcon(g, ib, ink);
            }
            else if (s.Id == "reset") DrawResetIcon(g, ib, ink);
            else if (s.Id == "gear") DrawAlarmIcon(g, ib, ink);
            else if (s.Id == "plus") DrawPlusMinus(g, ib, ink, true);
            else if (s.Id == "minus") DrawPlusMinus(g, ib, ink, false);
            else if (s.Id.StartsWith("preset:", StringComparison.Ordinal))
            {
                int m;
                if (!int.TryParse(s.Id.Substring(7), NumberStyles.Integer, CultureInfo.InvariantCulture, out m)) return;

                if (m == minutes)
                {
                    using (var b = new SolidBrush(Color.FromArgb(30, ink)))
                        g.FillRectangle(b, r.X + 1, r.Y + 1, r.Width - 2, r.Height - 2);
                    using (var pen = new Pen(Color.FromArgb(200, ink), 2f))
                        g.DrawRectangle(pen, r.X + 1, r.Y + 1, r.Width - 3, r.Height - 3);
                }

                string txt = m.ToString(CultureInfo.InvariantCulture);
                int px = 8;
                if (px * 5 * txt.Length + px * (txt.Length - 1) > r.Width - 14) px = 6;
                int gap = px;
                int tw = MeasureText(txt, px, gap);
                int th = 7 * px;
                DrawText(g, txt, r.X + (r.Width - tw) / 2, r.Y + (r.Height - th) / 2, px, gap, ink);
            }
        }

        private void DrawTime(Graphics g, int w, int h, Color ink, List<Spot> spots)
        {
            string txt = CountdownText();
            int px = TimePx(h, lastGridCell);
            int gap = px * 2;
            int tw = MeasureText(txt, px, gap);

            // 左边紧跟着重置按钮，不要飘到屏幕中间去
            int leftEnd = MarginX + TomatoW + 12 + BtnSize + Gap + BtnSize;
            int rightStart = w - MarginX - BtnSmall;      // 至少给齿轮让位
            for (int i = 0; i < spots.Count; i++)
            {
                string id = spots[i].Id;
                if (id == "gear" || id == "plus" || id == "minus" ||
                    id.StartsWith("preset:", StringComparison.Ordinal))
                {
                    if (spots[i].Rect.X < rightStart) rightStart = spots[i].Rect.X;
                }
            }

            int x = leftEnd + 40;
            if (x + tw > rightStart - 16) x = Math.Max(leftEnd + 8, rightStart - 16 - tw);

            int y = (h - 7 * px) / 2;
            DrawText(g, txt, x, y, px, gap, ink);
        }

        // ---------------- 点阵字形 ----------------
        private static int MeasureText(string s, int px, int gap)
        {
            int total = 0;
            for (int i = 0; i < s.Length; i++)
            {
                string[] rows = ClockProvider.GlyphOf(s[i]);
                if (rows == null) continue;
                total += rows[0].Length * px;
                if (i < s.Length - 1) total += gap;
            }
            return total;
        }

        private static void DrawText(Graphics g, string s, int x, int y, int px, int gap, Color color)
        {
            using (var b = new SolidBrush(color))
            {
                int cx = x;
                for (int i = 0; i < s.Length; i++)
                {
                    string[] rows = ClockProvider.GlyphOf(s[i]);
                    if (rows == null) continue;
                    for (int gy = 0; gy < rows.Length; gy++)
                    {
                        string line = rows[gy];
                        for (int gx = 0; gx < line.Length; gx++)
                        {
                            if (line[gx] != '1') continue;
                            g.FillRectangle(b, cx + gx * px, y + gy * px, px, px);
                        }
                    }
                    cx += rows[0].Length * px + gap;
                }
            }
        }

        // ---------------- 图标（手绘点阵） ----------------
        private static void FillCell(Graphics g, Brush b, int ox, int oy, int s, int gx, int gy)
        {
            g.FillRectangle(b, ox + gx * s, oy + gy * s, s, s);
        }

        private static int IconOrigin(Rectangle box, int n, out int s)
        {
            s = Math.Max(1, Math.Min(box.Width, box.Height) / n);
            return box.X + (box.Width - n * s) / 2;
        }

        private static void DrawPlayIcon(Graphics g, Rectangle box, Color color)
        {
            const int n = 7;
            int s;
            int ox = IconOrigin(box, n, out s);
            int oy = box.Y + (box.Height - n * s) / 2;
            using (var b = new SolidBrush(color))
            {
                for (int y = 0; y < n; y++)
                {
                    // 5 格宽的三角形：窄 → 宽 → 窄，指向右
                    int wCells = (y <= 3) ? y + 2 : 9 - y;
                    if (wCells > 5) wCells = 5;
                    for (int x = 0; x < wCells; x++) FillCell(g, b, ox, oy, s, x, y);
                }
            }
        }

        private static void DrawPauseIcon(Graphics g, Rectangle box, Color color)
        {
            const int n = 7;
            int s;
            int ox = IconOrigin(box, n, out s);
            int oy = box.Y + (box.Height - n * s) / 2;
            using (var b = new SolidBrush(color))
            {
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                        if ((x >= 1 && x <= 2) || (x >= 4 && x <= 5))
                            FillCell(g, b, ox, oy, s, x, y);
            }
        }

        /// <summary>重置：一个带缺口的圆环 + 缺口处的箭头。</summary>
        private static void DrawResetIcon(Graphics g, Rectangle box, Color color)
        {
            const int n = 9;
            int s;
            int ox = IconOrigin(box, n, out s);
            int oy = box.Y + (box.Height - n * s) / 2;

            double c = (n - 1) / 2.0;
            double r = c - 0.15;

            using (var b = new SolidBrush(color))
            {
                for (int gy = 0; gy < n; gy++)
                {
                    for (int gx = 0; gx < n; gx++)
                    {
                        double dx = gx - c, dy = gy - c;
                        double d = Math.Sqrt(dx * dx + dy * dy);
                        if (Math.Abs(d - r) > 0.65) continue;

                        double ang = Math.Atan2(dy, dx) * 180.0 / Math.PI;   // y 向下：-90 = 正上
                        if (ang > -72 && ang < 8) continue;                  // 右上开缺口
                        FillCell(g, b, ox, oy, s, gx, gy);
                    }
                }

                // 缺口右端一个小三角，表示"转回去"
                FillCell(g, b, ox, oy, s, 5, 1);
                FillCell(g, b, ox, oy, s, 6, 1);
                FillCell(g, b, ox, oy, s, 7, 1);
                FillCell(g, b, ox, oy, s, 5, 2);
                FillCell(g, b, ox, oy, s, 7, 2);
            }
        }

        /// <summary>
        /// 闹钟：圆表盘 + 指针 + 顶上两只铃耳。
        /// 一开始画的是齿轮 / 滑杆，但这个分辨率下都是糊的；
        /// 闹钟形状简单，钟面 + 铃耳一眼就能认出"这是计时相关"。
        /// </summary>
        private static void DrawAlarmIcon(Graphics g, Rectangle box, Color color)
        {
            const int n = 13;
            int s;
            int ox = IconOrigin(box, n, out s);
            int oy = box.Y + (box.Height - n * s) / 2;

            double cx = 6.0, cy = 7.0, r = 4.4;

            using (var b = new SolidBrush(color))
            {
                // 钟面：圆环
                for (int gy = 0; gy < n; gy++)
                {
                    for (int gx = 0; gx < n; gx++)
                    {
                        double dx = gx - cx, dy = gy - cy;
                        double d = Math.Sqrt(dx * dx + dy * dy);
                        if (Math.Abs(d - r) > 0.7) continue;
                        FillCell(g, b, ox, oy, s, gx, gy);
                    }
                }

                // 指针：时针朝上，分针朝右
                for (int y = 4; y <= 6; y++) FillCell(g, b, ox, oy, s, 6, y);
                for (int x = 7; x <= 9; x++) FillCell(g, b, ox, oy, s, x, 7);

                // 两只铃耳
                FillCell(g, b, ox, oy, s, 2, 1);
                FillCell(g, b, ox, oy, s, 1, 2);
                FillCell(g, b, ox, oy, s, 10, 1);
                FillCell(g, b, ox, oy, s, 11, 2);
            }
        }

        private static void DrawPlusMinus(Graphics g, Rectangle box, Color color, bool plus)
        {
            const int n = 7;
            int s;
            int ox = IconOrigin(box, n, out s);
            int oy = box.Y + (box.Height - n * s) / 2;
            using (var b = new SolidBrush(color))
            {
                for (int x = 1; x <= 5; x++) FillCell(g, b, ox, oy, s, x, 3);
                if (plus)
                    for (int y = 1; y <= 5; y++) FillCell(g, b, ox, oy, s, 3, y);
            }
        }
    }
}
