using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace DeadBand
{
    /// <summary>
    /// 像素时钟。
    ///
    /// 不用系统字体，直接用 5×7 点阵字模手绘：
    /// 每个"字模像素"等于 N 个网格单元（N≥1 且取整），
    /// 于是数字本身就踩在栅格上，和坏线、竖线是同一套格子。
    ///
    /// 字号（ClockScale）和位置（ClockOffsetX/Y）都能调 —— 因为面板上的坏线
    /// 不一定均匀，有时候某条特别亮，就得把数字挪开或缩小避开。
    /// </summary>
    internal sealed class ClockProvider : IProvider
    {
        // 5×7 点阵，'1' = 填
        private static readonly string[][] Digits = new string[][]
        {
            new string[] { "01110","10001","10011","10101","11001","10001","01110" }, // 0
            new string[] { "00100","01100","00100","00100","00100","00100","01110" }, // 1
            new string[] { "01110","10001","00001","00010","00100","01000","11111" }, // 2
            new string[] { "11111","00010","00100","00010","00001","10001","01110" }, // 3
            new string[] { "00010","00110","01010","10010","11111","00010","00010" }, // 4
            new string[] { "11111","10000","11110","00001","00001","10001","01110" }, // 5
            new string[] { "00110","01000","10000","11110","10001","10001","01110" }, // 6
            new string[] { "11111","00001","00010","00100","01000","01000","01000" }, // 7
            new string[] { "01110","10001","10001","01110","10001","10001","01110" }, // 8
            new string[] { "01110","10001","10001","01111","00001","00010","01100" }  // 9
        };

        // 星期缩写需要的字母（5×7）。点阵字模画不了汉字，所以用国际通用的 MON/TUE…
        private static readonly System.Collections.Generic.Dictionary<char, string[]> Letters =
            new System.Collections.Generic.Dictionary<char, string[]>()
        {
            { 'A', new string[] { "01110","10001","10001","11111","10001","10001","10001" } },
            { 'D', new string[] { "11110","10001","10001","10001","10001","10001","11110" } },
            { 'E', new string[] { "11111","10000","10000","11110","10000","10000","11111" } },
            { 'F', new string[] { "11111","10000","10000","11110","10000","10000","10000" } },
            { 'H', new string[] { "10001","10001","10001","11111","10001","10001","10001" } },
            { 'M', new string[] { "10001","11011","10101","10101","10001","10001","10001" } },
            { 'N', new string[] { "10001","11001","10101","10011","10001","10001","10001" } },
            { 'O', new string[] { "01110","10001","10001","10001","10001","10001","01110" } },
            { 'R', new string[] { "11110","10001","10001","11110","10100","10010","10001" } },
            { 'S', new string[] { "01111","10000","10000","01110","00001","00001","11110" } },
            { 'T', new string[] { "11111","00100","00100","00100","00100","00100","00100" } },
            { 'U', new string[] { "10001","10001","10001","10001","10001","10001","01110" } },
            { 'W', new string[] { "10001","10001","10001","10101","10101","11011","10001" } }
        };

        private static readonly string[] Space = new string[]
        { "000","000","000","000","000","000","000" };

        private static readonly string[] Colon = new string[]
        { "00","11","11","00","00","11","11" };

        private static readonly string[] Dash = new string[]
        { "00000","00000","00000","11111","00000","00000","00000" };

        public string Id { get { return "clock"; } }
        public string DisplayName { get { return "像素时钟"; } }
        public Color Background { get { return Color.Black; } }

        public void Render(Graphics g, int w, int h, ZoneProfile p, double t)
        {
            if (w <= 0 || h <= 0) return;

            DateTime now = DateTime.Now;
            int cell = p.GridEnabled ? p.GridCell : 0;

            int px, gap, smallPx;
            Metrics(h, p, cell, out px, out gap, out smallPx);

            string timeStr = now.ToString("HH:mm:ss");
            string dateStr = now.ToString("MM-dd") + " " + WeekdayEn(now);

            int timeW = Measure(timeStr, px, gap);
            int dateW = Measure(dateStr, smallPx, Math.Max(1, smallPx / 2));
            int rightMargin = Math.Max(24, w / 40);

            bool showDate = timeW + dateW + rightMargin * 2 + px * 2 <= w;

            int blockW = showDate ? timeW + dateW + rightMargin * 2 : timeW;
            int x = (w - blockW) / 2 + p.ClockOffsetX;
            int y = (h - 7 * px) / 2 + p.ClockOffsetY;

            if (cell >= 2)
            {
                x = Snap(x, cell);
                y = Snap(y, cell);
            }
            if (x < 0) x = 0;
            if (y < 0) y = 0;
            if (y + 7 * px > h) y = Math.Max(0, h - 7 * px);

            Color solid = p.ClockAutoColor ? AutoColor(p) : p.ClockColor;
            bool blink = !p.ClockBlink || now.Millisecond < 500;   // 关掉闪烁 = 常亮

            // 日期位置先算好，渐变笔刷要覆盖"时间 + 日期"整块，
            // 不然日期会是纯色、和主体（尤其彩虹渐变时）对不上。
            int dateX = 0, dateY = 0;
            if (showDate)
            {
                dateX = x + timeW + rightMargin;
                dateY = (h - 7 * smallPx) / 2;
                if (cell >= 2) { dateX = Snap(dateX, cell); dateY = Snap(dateY, cell); }
                if (dateX + dateW > w) dateX = w - dateW;
                if (dateY < 0) dateY = 0;
            }

            int blockRight = showDate ? dateX + dateW : x + timeW;
            Brush mainBrush = MakeBrush(p, solid, x, y, Math.Max(1, blockRight - x), 7 * px);
            try
            {
                DrawText(g, mainBrush, timeStr, x, y, px, gap, blink);
                if (showDate)
                    DrawText(g, mainBrush, dateStr, dateX, dateY, smallPx, Math.Max(1, smallPx / 2), true);
            }
            finally { if (mainBrush != null) mainBrush.Dispose(); }
        }

        /// <summary>字号计算。Render 和 NaturalWidth 共用，避免两处算法漂移。</summary>
        private static void Metrics(int h, ZoneProfile p, int cell, out int px, out int gap, out int smallPx)
        {
            if (p.ClockPx >= 2)
            {
                px = p.ClockPx;
                if (cell >= 2) px = (px / cell) * cell;
                if (px < 2) px = 2;
            }
            else
            {
                if (cell >= 2)
                {
                    int n = (int)Math.Floor(h * 0.94 / (7.0 * cell));
                    if (n < 1) n = 1;
                    px = n * cell;
                }
                else
                {
                    px = Math.Max(2, (int)Math.Floor(h * 0.94 / 7.0));
                }

                double scale = p.ClockScale <= 0 ? 1.0 : p.ClockScale / 100.0;
                px = (int)Math.Round(px * scale);

                if (cell >= 2)
                {
                    px = (px / cell) * cell;
                    if (px < cell) px = cell;
                }
                if (px < 2) px = 2;
            }
            if (7 * px > h) px = Math.Max(1, h / 7);

            gap = px * 2;
            smallPx = cell >= 2 ? Math.Max(cell, px / 2) : Math.Max(2, px / 2);
            if (smallPx >= px) smallPx = Math.Max(2, px / 2);
            if (cell >= 2) smallPx = (smallPx / cell) * cell;
            if (smallPx < 2) smallPx = 2;
        }

        /// <summary>时钟块的自然宽度。合成器用它算右对齐位置、给字幕让出多少空间。</summary>
        public int NaturalWidth(int w, int h, ZoneProfile p)
        {
            int cell = p.GridEnabled ? p.GridCell : 0;
            int px, gap, smallPx;
            Metrics(h, p, cell, out px, out gap, out smallPx);

            DateTime now = DateTime.Now;
            int timeW = Measure(now.ToString("HH:mm:ss"), px, gap);
            int dateW = Measure(now.ToString("MM-dd") + " " + WeekdayEn(now), smallPx, Math.Max(1, smallPx / 2));
            int rightMargin = Math.Max(24, w / 40);
            bool showDate = timeW + dateW + rightMargin * 2 + px * 2 <= w;
            return showDate ? timeW + dateW + rightMargin * 2 : timeW;
        }

        private static string WeekdayEn(DateTime d)
        {
            switch (d.DayOfWeek)
            {
                case DayOfWeek.Monday: return "MON";
                case DayOfWeek.Tuesday: return "TUE";
                case DayOfWeek.Wednesday: return "WED";
                case DayOfWeek.Thursday: return "THU";
                case DayOfWeek.Friday: return "FRI";
                case DayOfWeek.Saturday: return "SAT";
                default: return "SUN";
            }
        }

        private static int Snap(int v, int cell)
        {
            if (cell <= 1) return v;
            int r = (int)Math.Round((double)v / cell) * cell;
            return r;
        }

        private static Brush MakeBrush(ZoneProfile p, Color solid, int x, int y, int w, int h)
        {
            if (!p.ClockGradient)
                return new SolidBrush(solid);

            var rect = new Rectangle(x, y, Math.Max(2, w), Math.Max(2, h));
            var lg = new LinearGradientBrush(rect, Color.White, Color.White, LinearGradientMode.Horizontal);
            var cb = new ColorBlend(7);
            cb.Colors = new Color[]
            {
                Color.FromArgb(255, 0, 96),
                Color.FromArgb(255, 122, 0),
                Color.FromArgb(255, 214, 10),
                Color.FromArgb(72, 255, 120),
                Color.FromArgb(0, 200, 255),
                Color.FromArgb(122, 92, 255),
                Color.FromArgb(255, 64, 200)
            };
            cb.Positions = new float[] { 0f, 1f / 6f, 2f / 6f, 3f / 6f, 4f / 6f, 5f / 6f, 1f };
            lg.InterpolationColors = cb;
            return lg;
        }

        /// <summary>同色系：网格色提亮；网格关掉时用青色。</summary>
        private static Color AutoColor(ZoneProfile p)
        {
            if (!p.GridEnabled) return Color.FromArgb(0, 216, 255);
            Color c = p.GridColor;
            int r = Math.Min(255, (int)(c.R * 1.75f) + 40);
            int gg = Math.Min(255, (int)(c.G * 1.75f) + 40);
            int b = Math.Min(255, (int)(c.B * 1.75f) + 40);
            return Color.FromArgb(r, gg, b);
        }

        private static int GlyphWidth(char ch)
        {
            if (ch >= '0' && ch <= '9') return 5;
            if (ch == ':') return 2;
            if (ch == ' ') return 3;
            return 5;
        }

        /// <summary>
        /// 取一个字符的 5×7 点阵字模（每行一个字符串，'1' = 填）。
        /// internal 是为了让番茄钟复用同一套字形，整个区带看起来才是一个系统。
        /// </summary>
        internal static string[] GlyphOf(char ch)
        {
            if (ch >= '0' && ch <= '9') return Digits[ch - '0'];
            if (ch == ':') return Colon;
            if (ch == ' ') return Space;
            string[] g;
            if (Letters.TryGetValue(char.ToUpperInvariant(ch), out g)) return g;
            return Dash;
        }

        private static int Measure(string s, int px, int gap)
        {
            int total = 0;
            for (int i = 0; i < s.Length; i++)
                total += GlyphWidth(s[i]) * px + (i == s.Length - 1 ? 0 : gap);
            return total;
        }

        private static void DrawText(Graphics g, Brush brush, string s,
            int x, int y, int px, int gap, bool showColon)
        {
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                int gw = GlyphWidth(ch);
                if (ch != ':' || showColon)
                    DrawGlyph(g, brush, GlyphOf(ch), gw, x, y, px);
                x += gw * px;
                if (i != s.Length - 1) x += gap;
            }
        }

        private static void DrawGlyph(Graphics g, Brush brush, string[] rows,
            int gw, int x, int y, int px)
        {
            for (int r = 0; r < rows.Length; r++)
            {
                string row = rows[r];
                int c = 0;
                while (c < gw)
                {
                    if (row[c] != '1') { c++; continue; }
                    int c0 = c;
                    while (c < gw && row[c] == '1') c++;
                    g.FillRectangle(brush, x + c0 * px, y + r * px, (c - c0) * px, px);
                }
            }
        }
    }
}
