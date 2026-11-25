using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace DeadBand
{
    /// <summary>
    /// 彩色频谱条。
    ///
    /// 目标不是"准确的频谱分析"，而是"好看、会跳"。几处刻意的取舍：
    ///
    ///  1. 柱宽 —— 按屏幕宽度定成 ~150 根，每根 12~14px。太细的话低频相邻柱
    ///     会连成一整块，看起来就是"左边又宽又平"。
    ///  2. 每柱独立自动量程 —— 用全局峰值归一化的话，音乐能量永远堆在低频，
    ///     结果左半边常年顶到头。这里在**固定的高分辨率频率网格**上做自动量程，
    ///     再按当前横轴重采样到显示柱。
    ///  3. 跨柱平滑 —— 一阶 1-2-1 卷积两遍，让相邻柱连成"浪"而不是孤立尖刺。
    ///  4. 自适应横轴 —— 见下。
    ///
    /// 为什么自动量程要做在固定网格上：
    ///   横轴会随音乐伸缩，如果每根显示柱各自记峰值，映射一变柱 i 对应的频率
    ///   就换了，旧参考立刻失效，画面会闪。改成固定网格上记峰值、每帧重采样，
    ///   缩放时参考值是连续的。
    ///
    /// 横轴三档（spectrumaxis）：
    ///   fixed   写死 40Hz–16.5kHz。有频率参考，但内容集中在中低频时右边常年空着。
    ///   fitlow  低端锁 40Hz，高端 fHi 跟随实际最高频率。默认。
    ///   fitboth 两端都跟随，整条带子像橡皮筋一样伸缩。
    /// </summary>
    internal sealed class SpectrumProvider : IProvider, IProviderLifecycle
    {
        // 8192 点：低频 bin 宽约 5.9Hz。2048 点时 40Hz 和 54Hz 落在同一个 bin 里，
        // 低频区相邻的柱必然等高（差异不足 1px），看起来就是一堵墙。
        private const int FftSize = 8192;
        private const int Bars = 150;          // 显示柱数
        private const int Grid = 512;          // 固定频率网格（自动量程挂在这上面）
        private const float Gate = 0.0012f;    // 绝对噪声门

        private const float MinSpanOctaves = 2f;   // 最小跨度
        private const float ExpandAlpha = 0.35f;   // 扩展速度
        private const float ContractAlpha = 0.07f; // 收缩速度（原来是 0.04，右边空白停留太久）
        private const float ActiveRatio = 0.02f;   // 活动判定：全局峰值的 2%

        private readonly LoopbackCapture capture = new LoopbackCapture();
        private readonly float[] re = new float[FftSize];
        private readonly float[] im = new float[FftSize];
        private readonly float[] mag = new float[FftSize / 2];
        private readonly float[] window = new float[FftSize];

        // 固定网格上的数据
        private readonly float[] gRaw = new float[Grid];
        private readonly float[] gTmp = new float[Grid];
        private readonly float[] gPeak = new float[Grid];
        private readonly float[] gLevel = new float[Grid];
        private readonly float[] gEnv = new float[Grid];
        private readonly int[] gBinLo = new int[Grid];
        private readonly int[] gBinHi = new int[Grid];
        private readonly double[] gFreq = new double[Grid];

        // 显示柱
        private float[] levels = new float[0];
        private float[] peaks = new float[0];

        // 横轴状态
        private float fLo = 40f;
        private float fHi = 16500f;
        private float fCap = 16500f;
        private int lastDevCheck;

        public SpectrumProvider()
        {
            for (int i = 0; i < FftSize; i++)
                window[i] = (float)(0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / (FftSize - 1)));
        }

        public string Id { get { return "spectrum"; } }
        public string DisplayName { get { return "彩色频谱"; } }
        public Color Background { get { return Color.FromArgb(unchecked((int)0xFF06070A)); } }

        /// <summary>
        /// 整体透明度系数。合成器在"频谱要垫在时钟/字幕后面"时把它压到 0.4 左右。
        /// </summary>
        public float Dim = 1.0f;

        /// <summary>这一帧有没有声音。合成器靠它决定时钟要不要让位。</summary>
        public bool HasSignal { get { return axisMaxAmp > 0.012f; } }

        private Color Fade(Color c)
        {
            if (Dim >= 0.999f) return c;
            int a = (int)Math.Round(c.A * Dim);
            if (a < 0) a = 0;
            if (a > 255) a = 255;
            return Color.FromArgb(a, c.R, c.G, c.B);
        }

        public void Activate() { capture.Start(); }
        public void Deactivate() { capture.Stop(); }

        private static int ModeOf(string s)
        {
            if (string.Equals(s, "fixed", StringComparison.OrdinalIgnoreCase)) return 0;
            if (string.Equals(s, "fitboth", StringComparison.OrdinalIgnoreCase)) return 2;
            return 1;   // fitlow
        }

        public void Render(Graphics g, int w, int h, ZoneProfile p, double t)
        {
            if (w <= 0 || h <= 0) return;

            if (!capture.IsRunning) capture.Start();
            else WatchDevice();

            int mode = ModeOf(p.SpectrumAxis);
            modeName = mode == 0 ? "fixed" : (mode == 2 ? "fitboth" : "fitlow");

            int pitch, barW, count;
            if (p.GridEnabled)
            {
                // 和栅格对齐：柱宽 = N 个网格单元，柱间正好空一格，
                // 柱子踩在网格线上，高度也按单元取整 —— 整块图都是"格子"拼出来的
                int cell = p.GridCell;
                if (cell < 2) cell = 2;
                int barCells = Math.Max(2, (int)Math.Round((double)w / (Bars * cell)));
                pitch = barCells * cell;
                barW = pitch - cell;
                if (barW < 2) barW = 2;
                count = Math.Max(1, w / pitch);
            }
            else
            {
                pitch = Math.Max(12, w / Bars);
                int gap = Math.Max(3, pitch / 5);
                barW = Math.Max(2, pitch - gap);
                count = Math.Max(1, w / pitch);
            }
            if (levels.Length != count)
            {
                levels = new float[count];
                peaks = new float[count];
            }

            // ---------- 1. FFT ----------
            float[] snap = capture.Snapshot(FftSize);
            for (int i = 0; i < FftSize; i++)
            {
                re[i] = snap[i] * window[i];
                im[i] = 0f;
            }
            FftCore.Magnitude(re, im, FftSize, mag);

            int sr = capture.SampleRate > 0 ? capture.SampleRate : 48000;
            double fMin = 40.0;
            fCap = (float)Math.Min(16500.0, sr / 2.0);
            double span = Math.Log(fCap / fMin);
            float norm = FftSize / 4f;

            // ---------- 2. 投到固定网格 ----------
            for (int gi = 0; gi < Grid; gi++)
            {
                double f0 = fMin * Math.Exp(span * gi / Grid);
                double f1 = fMin * Math.Exp(span * (gi + 1) / Grid);
                double fc = Math.Sqrt(f0 * f1);
                gFreq[gi] = fc;
                double posCenter = fc * FftSize / sr;

                int b0 = (int)(f0 * FftSize / sr);
                int b1 = (int)(f1 * FftSize / sr);
                if (b1 <= b0) b1 = b0 + 1;
                if (b0 < 1) b0 = 1;
                if (b1 > mag.Length) b1 = mag.Length;
                if (b0 >= mag.Length) b0 = mag.Length - 1;
                gBinLo[gi] = b0;
                gBinHi[gi] = b1;

                float m;
                if (b1 - b0 <= 1)
                {
                    // 这个频带比一个 FFT bin 还窄（低频几乎全是这种情况）。
                    // 直接取 bin 的话，相邻好几根柱会读到同一个 bin，
                    // 表现就是"左边几根柱永远等高、同步跳动"。
                    // 改在相邻两 bin 之间按对数幅度插值，让每根柱都有自己的值。
                    int bi = (int)posCenter;
                    if (bi < 1) bi = 1;
                    if (bi > mag.Length - 2) bi = mag.Length - 2;
                    double frac = posCenter - bi;
                    if (frac < 0) frac = 0;
                    if (frac > 1) frac = 1;
                    double la = Math.Log(mag[bi] + 1e-9);
                    double lb = Math.Log(mag[bi + 1] + 1e-9);
                    m = (float)Math.Exp(la * (1 - frac) + lb * frac);
                }
                else
                {
                    m = 0f;
                    for (int b = b0; b < b1; b++) if (mag[b] > m) m = mag[b];
                }
                gRaw[gi] = m / norm;
            }

            // ---------- 3. 网格上跨带平滑（两遍 1-2-1）----------
            for (int pass = 0; pass < 2; pass++)
            {
                for (int gi = 0; gi < Grid; gi++)
                {
                    float l = gRaw[gi > 0 ? gi - 1 : 0];
                    float c = gRaw[gi];
                    float r2 = gRaw[gi < Grid - 1 ? gi + 1 : Grid - 1];
                    gTmp[gi] = (l + 2f * c + r2) * 0.25f;
                }
                Array.Copy(gTmp, gRaw, Grid);
            }

            // ---------- 4. 固定网格上的自动量程 + 包络 ----------
            float maxAmp = 0f;
            for (int gi = 0; gi < Grid; gi++) if (gRaw[gi] > maxAmp) maxAmp = gRaw[gi];

            // 全部改成"相对峰值"的比例，不再用绝对幅度：
            // 绝对常数会随系统音量变化而改变实际门限（音量低时反而砍得更狠）。
            float floorRef = Math.Max(maxAmp * 0.020f, 1e-6f);
            float gate = Math.Max(maxAmp * 0.006f, 1e-7f);

            for (int gi = 0; gi < Grid; gi++)
            {
                float a = gRaw[gi];

                if (a > gPeak[gi]) gPeak[gi] = a;
                else gPeak[gi] *= 0.9975f;
                if (gPeak[gi] < floorRef) gPeak[gi] = floorRef;

                float v = 0f;
                if (a >= gate)
                {
                    v = a / gPeak[gi];
                    v = (float)Math.Pow(v, 0.72);
                    v *= 0.95f;

                    // 频谱形状调制。
                    // 只做自动量程的话，被同一路内容驱动的相邻频段会因为"成比例"
                    // 而被归一成完全一样的高度 —— 左边那一堵等高墙就是这么来的。
                    // 这里再乘一个当前相对占比的压缩值，让柱高带上真实频谱形状，
                    // 同时用 sqrt 压缩，避免弱频段直接被压没。
                    float shape = maxAmp > 0f ? a / maxAmp : 0f;
                    v *= 0.15f + 0.85f * (float)Math.Sqrt(shape);

                    if (v > 1f) v = 1f;
                }

                if (v > gLevel[gi]) gLevel[gi] += (v - gLevel[gi]) * 0.55f;
                else gLevel[gi] += (v - gLevel[gi]) * 0.16f;
                if (gLevel[gi] < 0.002f) gLevel[gi] = 0f;

                // 滑动平均（不是取最大值）。
                // 取最大值的话，一下 70ms 的镲片能把 8–16kHz 一直顶住，
                // 而它占的时长不到 4% —— 横轴就被定在 16kHz，右边长期空着。
                gEnv[gi] += (a - gEnv[gi]) * 0.12f;
            }

            // ---------- 5. 自适应横轴 ----------
            if (mode == 0)
            {
                fLo = (float)fMin;
                fHi = fCap;
            }
            else
            {
                // 高端：取"平均幅度 > 全局峰值 2%"的最高频段。
                // 用幅度而不是能量占比 —— 人耳对 30dB 以下仍有感知，能量占比会把
                // 高频整条判掉；而滑动平均又能把一闪而过的镲片剔除。
                double totalPower = 0;
                for (int gi = 0; gi < Grid; gi++) totalPower += (double)gRaw[gi] * gRaw[gi];

                float hiTarget = fHi;
                if (maxAmp > 1e-6f)
                {
                    float hiTh = Math.Max(maxAmp * 0.02f, 1e-7f);
                    hiTarget = (float)fMin;
                    for (int gi = Grid - 1; gi >= 0; gi--)
                    {
                        if (gEnv[gi] > hiTh) { hiTarget = (float)gFreq[gi]; break; }
                    }
                    hiTarget *= 1.15f;                   // 末尾留一点余量
                }

                float loTarget = (float)fMin;
                if (mode == 2 && maxAmp > 1e-6f)
                {
                    float loTh = Math.Max(maxAmp * 0.02f, 1e-7f);
                    loTarget = fCap;
                    for (int gi = 0; gi < Grid; gi++)
                    {
                        if (gEnv[gi] > loTh) { loTarget = (float)gFreq[gi]; break; }
                    }
                    if (loTarget < (float)fMin) loTarget = (float)fMin;
                }

                // 最小跨度
                float minRatio = (float)Math.Pow(2.0, MinSpanOctaves);
                if (hiTarget < loTarget * minRatio) hiTarget = loTarget * minRatio;
                if (hiTarget > fCap) hiTarget = fCap;
                if (loTarget > fCap / minRatio) loTarget = fCap / minRatio;

                fHi = SmoothLog(fHi, hiTarget);
                if (mode == 2) fLo = SmoothLog(fLo, loTarget);
                else fLo = (float)fMin;

                if (fHi <= fLo * 1.05f) fHi = fLo * 1.05f;
                if (fHi > fCap) fHi = fCap;
            }

            // ---------- 6. 重采样到显示柱 ----------
            double dspan = Math.Log(fHi / fLo);
            for (int i = 0; i < count; i++)
            {
                double a0 = fLo * Math.Exp(dspan * i / count);
                double a1 = fLo * Math.Exp(dspan * (i + 1) / count);

                int gi0 = FreqToGrid(a0, fMin, span);
                int gi1 = FreqToGrid(a1, fMin, span);
                if (gi1 <= gi0) gi1 = gi0 + 1;
                if (gi0 < 0) gi0 = 0;
                if (gi1 > Grid) gi1 = Grid;
                if (gi0 >= Grid) gi0 = Grid - 1;

                float v = 0f;
                for (int gi = gi0; gi < gi1; gi++) if (gLevel[gi] > v) v = gLevel[gi];

                // 去同步纹理。
                // 低频那一段柱宽只有 0.6Hz，而 8192 点 FFT 的 bin 是 5.9Hz，
                // 一个 bin 里塞了约 9 根柱 —— 柱高必然相同，取整后就是一堵等高墙。
                // 这是分辨率下限，不是 bug。叠一层缓慢流动的细小增益起伏，
                // 让这一片看起来是"有纹理的"而不是一块死板。
                // 注意：这是纯视觉手段，不属于频谱测量结果。
                v *= 1f + 0.13f * (float)(Math.Sin(i * 1.9 + t * 1.3) * Math.Sin(i * 0.37 - t * 0.7));
                if (v > 1f) v = 1f;

                if (v > levels[i]) levels[i] += (v - levels[i]) * 0.60f;
                else levels[i] += (v - levels[i]) * 0.22f;
                if (levels[i] < 0.002f) levels[i] = 0f;

                if (levels[i] > peaks[i]) peaks[i] = levels[i];
                else peaks[i] = Math.Max(levels[i], peaks[i] - 0.006f);
            }

            // ---------- 7. 绘制 ----------
            int usable = h - 3;
            if (usable < 4) usable = h;

            for (int i = 0; i < count; i++)
            {
                int x = i * pitch;
                if (x + barW > w) break;

                float u = count <= 1 ? 0f : (float)i / (count - 1);
                float hue = 8f + 300f * u;
                if (hue >= 360f) hue -= 360f;

                // 未点亮的底柱：网格开着的时候不画。
                // 网格本身已经提供了"结构感"，再叠一层有色底柱会把
                // 纯黑底 + 竖线 的干净像素风冲淡。
                if (!p.GridEnabled)
                {
                    Color tint = Hsv(hue, 0.85f, 1f);
                    Color dim = Blend(p.BaseColor, tint, 0.16f);
                    using (var b = new SolidBrush(Fade(dim)))
                        g.FillRectangle(b, x, h - usable, barW, usable);
                }

                float tilt = 0.90f + 0.55f * u;
                float lv = levels[i] * tilt;
                if (lv > 1f) lv = 1f;
                if (lv <= 0f) continue;

                int bh;
                if (p.GridEnabled)
                {
                    int cell = p.GridCell;
                    if (cell < 2) cell = 2;
                    bh = (int)Math.Round(lv * usable / cell) * cell;
                    if (bh < cell) bh = cell;
                }
                else
                {
                    bh = (int)Math.Round(lv * usable);
                    if (bh < 1) bh = 1;
                }

                int top = h - bh;
                Color lit = Hsv(hue, 0.82f, 0.55f + 0.45f * lv);
                using (var b = new SolidBrush(Fade(lit)))
                    g.FillRectangle(b, x, top, barW, bh);

                Color cap = Hsv(hue, 0.28f, 1f);
                using (var b = new SolidBrush(Fade(cap)))
                    g.FillRectangle(b, x, top, barW, 2);

                int py = h - (int)Math.Round(peaks[i] * tilt * usable) - 3;
                if (py < 0) py = 0;
                if (peaks[i] > 0.02f)
                {
                    using (var b = new SolidBrush(Fade(Color.FromArgb(170, cap))))
                        g.FillRectangle(b, x, py, barW, 2);
                }
            }

            if (capture.Failed)
            {
                using (var f = new Font("Microsoft YaHei UI", 9f))
                using (var b = new SolidBrush(Color.FromArgb(235, 255, 120, 120)))
                    g.DrawString("音频捕获未启动：" + capture.LastError, f, b, 12, 6);
            }

            axisMaxAmp = maxAmp;
            var bd = new System.Text.StringBuilder();
            for (int gi = 0; gi < Grid; gi += 32)
            {
                bd.Append(gFreq[gi].ToString("F0")).Append("Hz:");
                bd.Append(maxAmp > 0f ? (gLevel[gi]).ToString("F3") : "0");
                bd.Append("  ");
            }
            axisBands = bd.ToString();

            DumpAxis();
        }

        private int lastAxisDump;

        /// <summary>把当前横轴写进 spectrum.txt，排查"右边为什么空着"时看这个。</summary>
        private void DumpAxis()
        {
            if (Environment.TickCount - lastAxisDump < 2000) return;
            lastAxisDump = Environment.TickCount;
            try
            {
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(ProfileStore.DataDir, "spectrum.txt"),
                    "at=" + DateTime.Now.ToString("HH:mm:ss") +
                    "\r\nfLo=" + fLo.ToString("F0") + " Hz" +
                    "\r\nfHi=" + fHi.ToString("F0") + " Hz" +
                    "\r\nfCap=" + fCap.ToString("F0") + " Hz" +
                    "\r\nfHi/fLo=" + (fHi / fLo).ToString("F2") + " 倍" +
                    "\r\n模式=" + modeName +
                    "\r\nmaxAmp=" + axisMaxAmp.ToString("F5") +
                    "\r\n包络采样(每 32 格一格, 归一化到 maxAmp)=" + axisBands +
                    "\r\n", new System.Text.UTF8Encoding(false));
            }
            catch { }
        }

        private string modeName = "";
        private float axisMaxAmp;
        private string axisBands = "";

        /// <summary>
        /// 设备看门狗：声音换到别的输出（插耳机、切蓝牙、播放器改输出）之后，
        /// 回环采集会一直停在旧端点上收不到任何数据，必须重新绑定。
        /// </summary>
        private void WatchDevice()
        {
            if (Environment.TickCount - lastDevCheck < 2000) return;
            lastDevCheck = Environment.TickCount;

            string cur = LoopbackCapture.GetDefaultDeviceId();
            if (cur.Length == 0) return;
            string bound = capture.DeviceId;
            if (bound == null || bound.Length == 0) return;
            if (string.Equals(cur, bound, StringComparison.OrdinalIgnoreCase)) return;

            capture.Stop();
            System.Threading.Thread.Sleep(150);
            capture.Start();
        }

        private static int FreqToGrid(double f, double fMin, double span)        {
            if (f <= fMin) return 0;
            double pos = Math.Log(f / fMin) / span * Grid;
            int gi = (int)pos;
            if (gi < 0) gi = 0;
            if (gi >= Grid) gi = Grid - 1;
            return gi;
        }

        /// <summary>对数域非对称平滑：扩展快、收缩慢。</summary>
        private static float SmoothLog(float current, float target)
        {
            if (target <= 0f) target = 1f;
            if (current <= 0f) current = target;
            double lc = Math.Log(current);
            double lt = Math.Log(target);
            double alpha = lt > lc ? ExpandAlpha : ContractAlpha;
            return (float)Math.Exp(lc + (lt - lc) * alpha);
        }

        private static Color Blend(Color baseColor, Color tint, float amount)
        {
            float a = amount;
            int r = (int)(baseColor.R * (1 - a) + tint.R * a);
            int gg = (int)(baseColor.G * (1 - a) + tint.G * a);
            int b = (int)(baseColor.B * (1 - a) + tint.B * a);
            return Color.FromArgb(Clamp(r), Clamp(gg), Clamp(b));
        }

        private static Color Hsv(float h, float s, float v)
        {
            float c = v * s;
            float x = c * (1 - Math.Abs(((h / 60f) % 2f) - 1));
            float m = v - c;
            float r = 0, gg = 0, b = 0;
            if (h < 60) { r = c; gg = x; }
            else if (h < 120) { r = x; gg = c; }
            else if (h < 180) { gg = c; b = x; }
            else if (h < 240) { gg = x; b = c; }
            else if (h < 300) { r = x; b = c; }
            else { r = c; b = x; }
            return Color.FromArgb(Clamp((int)Math.Round((r + m) * 255f)),
                                  Clamp((int)Math.Round((gg + m) * 255f)),
                                  Clamp((int)Math.Round((b + m) * 255f)));
        }

        private static int Clamp(int v)
        {
            if (v < 0) return 0;
            if (v > 255) return 255;
            return v;
        }
    }
}
