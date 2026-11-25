using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Text;

namespace DeadBand
{
    /// <summary>
    /// 坏线模型。坐标以「距屏幕底边的行数」为准（0 = 最底下一行），
    /// 这样面板上的坏线位置是固定的，不会因为区域高度改变而漂移。
    /// </summary>
    internal sealed class LineModel
    {
        public bool Enabled;
        public int Spacing = 7;    // 每隔几行一条坏线
        public int Offset;         // 相位：第一条坏线距屏幕底边的行数
        public int Thickness = 1;  // 一条坏线占几行

        public bool IsBad(int distFromBottom)
        {
            if (!Enabled || Spacing <= 0) return false;
            int t = Thickness < 1 ? 1 : Thickness;
            int m = ((distFromBottom - Offset) % Spacing + Spacing) % Spacing;
            return m < t;
        }

        public LineModel Clone()
        {
            var c = new LineModel();
            c.Enabled = Enabled; c.Spacing = Spacing; c.Offset = Offset; c.Thickness = Thickness;
            return c;
        }

        public string Describe()
        {
            if (!Enabled) return "坏线模型：关";
            return "坏线模型：每 " + Spacing + " 行一条，相位 " + Offset + "，厚 " + Thickness;
        }
    }

    /// <summary>一台显示器上的一条坏屏区域配置。</summary>
    internal sealed class ZoneProfile
    {
        public string MonitorId = "";
        public string Description = "";
        // 屏幕设备名（"\\.\DISPLAY5"）。多显示器时靠它精确定位是哪一块，
        // 光靠分辨率会认错（两块同分辨率的屏很常见）。
        public string ScreenDevice = "";
        public int ScreenWidth;
        public int ScreenHeight;
        public int ZoneHeight = 100;            // 预留区高度（像素，贴屏幕底边）
        public int BaseColorArgb = unchecked((int)0xFF000000);   // 区带背景色，也是坏线行填充色
        public LineModel Lines = new LineModel();
        // ---- 分层开关（取代原来「内容」单选）----
        // 三层各自独立，可以同时开。没有内容时该层什么都不画。
        public bool ClockEnabled = true;      // 时钟默认开
        public bool SpectrumEnabled;          // 频谱默认关
        public bool CaptionEnabled;           // 字幕默认关

        // 有音频时怎么和时钟共处：dim = 频谱降透明度垫在后面；hideclock = 藏掉时钟
        public string SpectrumWithClock = "dim";
        // 垫在后面时的不透明度（百分比）。100 = 跟平时一样亮，值越小越淡。
        public int SpectrumDim = 40;
        // 有字幕时：right = 时钟靠右；hide = 藏掉时钟
        public string ClockWithCaption = "right";
        // 番茄钟：off | exclusive | coexist
        public string PomodoroMode = "off";

        // 旧字段：只为兼容老配置保留，新代码不再读它
        public string Provider = "";
        public string SpectrumAxis = "fitlow";   // fixed | fitlow | fitboth

        // 背景网格：纯黑底 + 同色系竖线，把面板坏线"收编"成栅格的一部分
        public bool GridEnabled;
        // 是否把内容从坏线上"挖空"。开着时坏行会被清成底色，内容看起来像被切开；
        // 关掉则让内容铺满，坏线变成穿越内容的栅格线（配合网格更自然）。
        public bool LineBlank = true;

        // 像素时钟的字色：可以跟随网格色（同色系），也可以自己指定
        public bool ClockAutoColor = true;
        public int ClockColorArgb = unchecked((int)0xFF00D8FF);
        public int ClockScale = 100;        // 字号百分比（Auto 模式下用）
        public int ClockPx;                 // 点阵像素尺寸（0 = 自动）；必须是网格单元的整数倍
        public int ClockOffsetX;             // 水平位移（像素）
        public int ClockOffsetY;             // 垂直位移（像素）
        public bool ClockGradient;           // 横向彩虹渐变
        public bool ClockBlink = true;   // 冒号闪烁（关掉 = 常亮）

        // 字幕 / 歌词（由浏览器脚本或任何程序推送进来）
        public string CaptionFont = "pixel";                          // pixel | normal
        public int CaptionColorArgb = unchecked((int)0xFFFFFFFF);
        public int FeedPort = FeedServer.DefaultPort;                 // 本地推送端口

        // 全屏时的处理：
        //   avoid   = 主动把全屏窗口压到区带上沿（视频让开，区带不被遮）
        //   overlay = 不缩放窗口，区带盖在画面最底下（程序死活不让时的逃生档）
        public string FullscreenMode = "avoid";
        public bool GridFollowLines = true;                // 竖线间距/粗细直接跟随坏线（正方形栅格）
        public int GridSpacing = 8;                        // 不跟随时用的竖线间距（像素）
        public int GridThickness = 1;                      // 每条竖线宽度（像素）
        public int GridColorArgb = unchecked((int)0xFF3A3A46);

        /// <summary>
        /// 网格单元边长（像素）。跟随坏线时间距就是坏线的行间距，
        /// 这样栅格才是正方形的，内容也才能和横线对齐。
        /// </summary>
        public int GridCell
        {
            get
            {
                if (GridFollowLines)
                {
                    int s = Lines != null ? Lines.Spacing : 4;
                    return s > 0 ? s : 4;
                }
                return GridSpacing > 0 ? GridSpacing : 8;
            }
        }
        public bool Enabled = true;

        public Color BaseColor
        {
            get { return Color.FromArgb(BaseColorArgb); }
            set { BaseColorArgb = value.ToArgb(); }
        }

        public Color ClockColor
        {
            get { return Color.FromArgb(ClockColorArgb); }
            set { ClockColorArgb = value.ToArgb(); }
        }

        public Color GridColor
        {
            get { return Color.FromArgb(GridColorArgb); }
            set { GridColorArgb = value.ToArgb(); }
        }

        public Color CaptionColor
        {
            get { return Color.FromArgb(CaptionColorArgb); }
            set { CaptionColorArgb = value.ToArgb(); }
        }

        public ZoneProfile Clone()
        {
            var c = new ZoneProfile();
            c.MonitorId = MonitorId; c.Description = Description;
            c.ScreenDevice = ScreenDevice;
            c.ScreenWidth = ScreenWidth; c.ScreenHeight = ScreenHeight;
            c.ZoneHeight = ZoneHeight; c.BaseColorArgb = BaseColorArgb;
            c.Lines = Lines.Clone(); c.Provider = Provider; c.Enabled = Enabled; c.SpectrumAxis = SpectrumAxis;
            c.GridEnabled = GridEnabled; c.LineBlank = LineBlank; c.ClockAutoColor = ClockAutoColor; c.ClockColorArgb = ClockColorArgb;
            c.ClockScale = ClockScale; c.ClockPx = ClockPx; c.ClockOffsetX = ClockOffsetX; c.ClockOffsetY = ClockOffsetY; c.ClockGradient = ClockGradient; c.ClockBlink = ClockBlink; c.FullscreenMode = FullscreenMode; c.GridFollowLines = GridFollowLines; c.GridSpacing = GridSpacing; c.GridThickness = GridThickness; c.GridColorArgb = GridColorArgb;
            c.CaptionFont = CaptionFont; c.CaptionColorArgb = CaptionColorArgb; c.FeedPort = FeedPort;
            c.ClockEnabled = ClockEnabled; c.SpectrumEnabled = SpectrumEnabled; c.CaptionEnabled = CaptionEnabled;
            c.SpectrumWithClock = SpectrumWithClock; c.SpectrumDim = SpectrumDim; c.ClockWithCaption = ClockWithCaption;
            c.PomodoroMode = PomodoroMode;
            return c;
        }
    }

    /// <summary>配置读写。用 key=value 纯文本，用户可以直接改。</summary>
    internal static class ProfileStore
    {
        public static string DataDir
        {
            get
            {
                string b = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string d = Path.Combine(b, "DeadBand");
                try { Directory.CreateDirectory(d); } catch { }
                return d;
            }
        }

        public static string ProfileDir
        {
            get
            {
                string d = Path.Combine(DataDir, "profiles");
                try { Directory.CreateDirectory(d); } catch { }
                return d;
            }
        }

        public static string SafeName(string monitorId)
        {
            if (string.IsNullOrEmpty(monitorId)) return "unknown";
            var sb = new StringBuilder();
            foreach (char ch in monitorId)
                sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
            string s = sb.ToString();
            while (s.IndexOf("__") >= 0) s = s.Replace("__", "_");
            if (s.Length > 80) s = s.Substring(0, 80);
            return s;
        }

        public static string ProfilePath(string monitorId)
        {
            return Path.Combine(ProfileDir, SafeName(monitorId) + ".ini");
        }

        public static ZoneProfile Load(string monitorId)
        {
            ZoneProfile p = LoadFromPath(ProfilePath(monitorId));
            if (p != null) p.MonitorId = monitorId;
            return p;
        }

        public static ZoneProfile LoadFromPath(string path)
        {
            var p = new ZoneProfile();
            if (!File.Exists(path)) return null;
            p.Lines = new LineModel();
            bool sawLayers = false;
            try
            {
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string v = line.Substring(eq + 1).Trim();
                    int iv;
                    switch (k)
                    {
                        case "monitorid": p.MonitorId = v; break;
                        case "description": p.Description = v; break;
                        case "screendevice": p.ScreenDevice = v; break;
                        case "screenwidth": if (int.TryParse(v, out iv)) p.ScreenWidth = iv; break;
                        case "screenheight": if (int.TryParse(v, out iv)) p.ScreenHeight = iv; break;
                        case "zoneheight": if (int.TryParse(v, out iv)) p.ZoneHeight = iv; break;
                        case "basecolor":
                        case "gapcolor":              // 兼容旧配置名
                            try { p.BaseColor = ColorTranslator.FromHtml(v); } catch { }
                            break;
                        case "lineenabled": p.Lines.Enabled = (v == "1" || v.ToLowerInvariant() == "true"); break;
                        case "linespacing": if (int.TryParse(v, out iv)) p.Lines.Spacing = iv; break;
                        case "lineoffset": if (int.TryParse(v, out iv)) p.Lines.Offset = iv; break;
                        case "linethickness": if (int.TryParse(v, out iv)) p.Lines.Thickness = iv; break;
                        case "provider": p.Provider = v; break;
                        case "spectrumaxis": p.SpectrumAxis = v; break;
                        case "gridenabled": p.GridEnabled = (v == "1" || v.ToLowerInvariant() == "true"); break;
                        case "clockautocolor": p.ClockAutoColor = (v == "1" || v.ToLowerInvariant() == "true"); break;
                        case "clockpx": if (int.TryParse(v, out iv)) p.ClockPx = iv; break;
                        case "clockscale": if (int.TryParse(v, out iv)) p.ClockScale = iv; break;
                        case "clockoffsetx": if (int.TryParse(v, out iv)) p.ClockOffsetX = iv; break;
                        case "clockoffsety": if (int.TryParse(v, out iv)) p.ClockOffsetY = iv; break;
                        case "fullscreenmode": p.FullscreenMode = ParseFullscreenMode(v); break;
                        case "clockblink": p.ClockBlink = (v == "1" || v.ToLowerInvariant() == "true"); break;
                        case "clockgradient": p.ClockGradient = (v == "1" || v.ToLowerInvariant() == "true"); break;
                        case "clockcolor": try { p.ClockColor = ColorTranslator.FromHtml(v); } catch { } break;
                        case "lineblank": p.LineBlank = (v == "1" || v.ToLowerInvariant() == "true"); break;
                        case "gridfollowlines": p.GridFollowLines = (v == "1" || v.ToLowerInvariant() == "true"); break;
                        case "gridspacing": if (int.TryParse(v, out iv)) p.GridSpacing = iv; break;
                        case "gridthickness": if (int.TryParse(v, out iv)) p.GridThickness = iv; break;
                        case "gridcolor": try { p.GridColor = ColorTranslator.FromHtml(v); } catch { } break;
                        case "enabled": p.Enabled = !(v == "0" || v.ToLowerInvariant() == "false"); break;
                        case "captionfont": p.CaptionFont = v; break;
                        case "captioncolor": try { p.CaptionColor = ColorTranslator.FromHtml(v); } catch { } break;
                        case "feedport": if (int.TryParse(v, out iv)) p.FeedPort = iv; break;
                        case "clockenabled":
                            p.ClockEnabled = (v == "1" || v.ToLowerInvariant() == "true");
                            sawLayers = true;
                            break;
                        case "spectrumenabled":
                            p.SpectrumEnabled = (v == "1" || v.ToLowerInvariant() == "true");
                            sawLayers = true;
                            break;
                        case "captonenabled":
                            p.CaptionEnabled = (v == "1" || v.ToLowerInvariant() == "true");
                            sawLayers = true;
                            break;
                        case "spectrumwithclock": p.SpectrumWithClock = v; break;
                        case "spectrumdim": if (int.TryParse(v, out iv)) p.SpectrumDim = iv; break;
                        case "clockwithcaption": p.ClockWithCaption = v; break;
                        case "pomodoromode": p.PomodoroMode = v; sawLayers = true; break;
                    }
                }
            }
            catch { return null; }

            // 老配置（只有 provider=xxx 那种）自动升级成分层开关
            if (!sawLayers) MigrateLegacyProvider(p);
            return p;
        }

        /// <summary>
        /// 把「内容单选」的旧配置翻译成分层开关。
        /// 旧的 pixelwave / solid / none 在新模型里没有对应物，全部关掉即可。
        /// </summary>
        private static void MigrateLegacyProvider(ZoneProfile p)
        {
            string id = p.Provider == null ? "" : p.Provider.Trim().ToLowerInvariant();

            p.ClockEnabled = false;
            p.SpectrumEnabled = false;
            p.CaptionEnabled = false;
            p.PomodoroMode = "off";

            if (id == "clock") p.ClockEnabled = true;
            else if (id == "spectrum") p.SpectrumEnabled = true;
            else if (id == "caption") p.CaptionEnabled = true;
            else if (id == "pomodoro") p.PomodoroMode = "exclusive";
            else if (id.Length == 0) p.ClockEnabled = true;   // 全新配置只开时钟
        }

        /// <summary>
        /// 全屏模式取值。旧配置里的 keep / shrink 一律迁到 avoid：
        /// keep 是当初那个"全屏时其实会被隐藏"的默认值，
        /// shrink 是现在这个 avoid 的老名字。只有显式写 overlay 才是覆盖档。
        /// </summary>
        private static string ParseFullscreenMode(string v)
        {
            if (string.IsNullOrEmpty(v)) return "avoid";
            string s = v.Trim().ToLowerInvariant();
            if (s == "overlay") return "overlay";
            return "avoid";
        }

        public static void Save(ZoneProfile p)
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# DeadBand 显示器配置");
                sb.AppendLine("# zoneheight   = 底部预留区高度（像素）");
                sb.AppendLine("# lineenabled  = 是否启用坏线避让（1/0）");
                sb.AppendLine("# linespacing  = 每隔几行一条坏线");
                sb.AppendLine("# lineoffset   = 第一条坏线距屏幕底边的行数");
                sb.AppendLine("# linethickness= 一条坏线占几行");
                sb.AppendLine("# provider     = 内容提供者 id");
                sb.AppendLine("monitorid=" + p.MonitorId);
                sb.AppendLine("description=" + p.Description);
                sb.AppendLine("screendevice=" + p.ScreenDevice);
                sb.AppendLine("screenwidth=" + p.ScreenWidth.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("screenheight=" + p.ScreenHeight.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("zoneheight=" + p.ZoneHeight.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("basecolor=" + ColorTranslator.ToHtml(p.BaseColor));
                sb.AppendLine("lineenabled=" + (p.Lines.Enabled ? "1" : "0"));
                sb.AppendLine("linespacing=" + p.Lines.Spacing.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("lineoffset=" + p.Lines.Offset.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("linethickness=" + p.Lines.Thickness.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("provider=" + p.Provider);
                sb.AppendLine("spectrumaxis=" + p.SpectrumAxis);
                sb.AppendLine("gridenabled=" + (p.GridEnabled ? "1" : "0"));
                sb.AppendLine("lineblank=" + (p.LineBlank ? "1" : "0"));
                sb.AppendLine("clockautocolor=" + (p.ClockAutoColor ? "1" : "0"));
                sb.AppendLine("clockcolor=" + ColorTranslator.ToHtml(p.ClockColor));
                sb.AppendLine("clockpx=" + p.ClockPx.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("clockscale=" + p.ClockScale.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("clockoffsetx=" + p.ClockOffsetX.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("clockoffsety=" + p.ClockOffsetY.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("clockgradient=" + (p.ClockGradient ? "1" : "0"));
                sb.AppendLine("clockblink=" + (p.ClockBlink ? "1" : "0"));
                sb.AppendLine("fullscreenmode=" + p.FullscreenMode);
                sb.AppendLine("gridfollowlines=" + (p.GridFollowLines ? "1" : "0"));
                sb.AppendLine("gridspacing=" + p.GridSpacing.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("gridthickness=" + p.GridThickness.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("gridcolor=" + ColorTranslator.ToHtml(p.GridColor));
                sb.AppendLine("captionfont=" + p.CaptionFont);
                sb.AppendLine("captioncolor=" + ColorTranslator.ToHtml(p.CaptionColor));
                sb.AppendLine("feedport=" + p.FeedPort.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("clockenabled=" + (p.ClockEnabled ? "1" : "0"));
                sb.AppendLine("spectrumenabled=" + (p.SpectrumEnabled ? "1" : "0"));
                sb.AppendLine("captonenabled=" + (p.CaptionEnabled ? "1" : "0"));
                sb.AppendLine("spectrumwithclock=" + p.SpectrumWithClock);
                sb.AppendLine("spectrumdim=" + p.SpectrumDim.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("clockwithcaption=" + p.ClockWithCaption);
                sb.AppendLine("pomodoromode=" + p.PomodoroMode);
                sb.AppendLine("enabled=" + (p.Enabled ? "1" : "0"));
                File.WriteAllText(ProfilePath(p.MonitorId), sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }
    }

    // =====================================================================
    //  内容提供者
    // =====================================================================

    internal interface IProvider
    {
        string Id { get; }
        string DisplayName { get; }
        Color Background { get; }
        void Render(Graphics g, int width, int height, ZoneProfile p, double t);
    }

    /// <summary>
    /// 可选生命周期：需要占用资源的内容（比如采集系统声音）实现它，
    /// 切走或被挂起时会被通知去释放，别在后台空转。
    /// </summary>
    internal interface IProviderLifecycle
    {
        void Activate();
        void Deactivate();
    }

    /// <summary>
    /// 可选交互：内容要能被鼠标点。区带窗口会把命中测试和点击转交给它。
    ///
    /// 没实现的 provider 整块区域都是穿透的（HTTRANSPARENT），
    /// 点了等于点在下面的窗口上 —— 这是默认行为，别改。
    /// </summary>
    internal interface IProviderInput
    {
        /// <summary>这个坐标上有没有可点的东西。返回 false 就让鼠标穿透。</summary>
        bool HitTest(int x, int y);

        /// <summary>点在可点区域上时回调。</summary>
        void OnClick(int x, int y);

        /// <summary>每帧告知鼠标在窗口内的位置，用来做悬停高亮。(-1,-1) = 不在窗口里。</summary>
        void SetHover(int x, int y);
    }

    /// <summary>纯色填充，用来先确认区域位置。</summary>
    internal sealed class SolidProvider : IProvider
    {
        public string Id { get { return "solid"; } }
        public string DisplayName { get { return "纯色"; } }
        public Color Background { get { return Color.FromArgb(unchecked((int)0xFF0A0C10)); } }
        public void Render(Graphics g, int w, int h, ZoneProfile p, double t)
        {
        }
    }

    /// <summary>
    /// 像素波形：横向滚动的等宽光柱。故意做得"有节奏"，
    /// 好让被坏线切开的空隙看起来像设计的一部分。
    /// </summary>
    internal sealed class PixelWaveProvider : IProvider
    {
        public string Id { get { return "pixelwave"; } }
        public string DisplayName { get { return "像素波形"; } }
        public Color Background { get { return Color.FromArgb(unchecked((int)0xFF07090D)); } }

        public void Render(Graphics g, int w, int h, ZoneProfile p, double t)
        {
            if (w <= 0 || h <= 0) return;

            int barW = 6, gapPx = 3, pitch = barW + gapPx;
            if (p.GridEnabled)
            {
                int cell = p.GridCell;
                if (cell < 2) cell = 2;
                int cells = Math.Max(2, (int)Math.Round(9.0 / cell));
                pitch = cells * cell;
                barW = Math.Max(2, pitch - cell);
                gapPx = cell;
            }
            int count = w / pitch + 1;
            int usable = h - 4;
            if (usable < 2) usable = h;

            g.SmoothingMode = SmoothingMode.None;

            for (int i = 0; i < count; i++)
            {
                double x = i * pitch;
                double u = x / (double)w;

                double ph = u * 11.0 - t * 1.15;
                double a = Math.Sin(ph) * 0.5 + 0.5;
                double b = Math.Sin(ph * 2.31 + 1.7) * 0.5 + 0.5;
                double c = Math.Sin(ph * 0.47 - t * 0.31 + 2.1) * 0.5 + 0.5;
                double v = 0.46 * a + 0.30 * b + 0.24 * c;

                double pulse = Math.Sin(t * 0.9 - u * 6.0) * 0.5 + 0.5;
                if (pulse > 0.93) v *= 1.25;
                if (v > 1.0) v = 1.0;
                if (v < 0.05) continue;

                int bh;
                if (p.GridEnabled)
                {
                    int cell = p.GridCell;
                    bh = (int)Math.Round(v * usable / cell) * cell;
                    if (bh < cell) bh = cell;
                }
                else
                {
                    bh = (int)Math.Round(v * usable);
                    if (bh < 1) bh = 1;
                }

                int hue = (int)(190 + 150 * u);      // 青 → 紫 → 品红
                if (hue > 360) hue -= 360;
                Color col = HsvToRgb(hue, 0.72, 0.55 + 0.45 * v);

                using (var br = new SolidBrush(col))
                    g.FillRectangle(br, (float)x, (float)(h - bh), barW, bh);
            }

            // 底部一条细细的高光，被坏线切开后会有"编织"的观感
            using (var pen = new Pen(Color.FromArgb(90, 200, 235, 255), 1f))
                g.DrawLine(pen, 0, h - 1, w, h - 1);
        }

        private static Color HsvToRgb(double h, double s, double v)
        {
            double c = v * s;
            double x = c * (1 - Math.Abs(((h / 60.0) % 2) - 1));
            double m = v - c;
            double r = 0, g2 = 0, b = 0;
            if (h < 60) { r = c; g2 = x; }
            else if (h < 120) { r = x; g2 = c; }
            else if (h < 180) { g2 = c; b = x; }
            else if (h < 240) { g2 = x; b = c; }
            else if (h < 300) { r = x; b = c; }
            else { r = c; b = x; }
            return Color.FromArgb(
                Clamp255((r + m) * 255),
                Clamp255((g2 + m) * 255),
                Clamp255((b + m) * 255));
        }

        private static int Clamp255(double v)
        {
            int i = (int)Math.Round(v);
            if (i < 0) return 0;
            if (i > 255) return 255;
            return i;
        }
    }

    internal static class ProviderRegistry
    {
        private static readonly IProvider[] All = new IProvider[]
        {
            new ClockProvider(),
            new SpectrumProvider(),
            new CaptionProvider(),
            new PomodoroProvider()
        };

        public static IProvider[] AllProviders { get { return All; } }

        public static IProvider Get(string id)
        {
            foreach (var p in All)
                if (string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase)) return p;
            return All[0];
        }
    }
}
