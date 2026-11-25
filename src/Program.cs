using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace DeadBand
{
    internal sealed class App : ApplicationContext
    {
        private readonly ZoneHost host = new ZoneHost();
        private readonly NotifyIcon tray;
        private readonly System.Windows.Forms.Timer watcher = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer fsWatcher = new System.Windows.Forms.Timer();

        // 分层开关（打开菜单时按当前配置同步对号）
        private ToolStripMenuItem miClock, miSpectrum, miCaption;
        private readonly System.Collections.Generic.List<ToolStripMenuItem> miPomoModes =
            new System.Collections.Generic.List<ToolStripMenuItem>();
        private readonly System.Collections.Generic.List<ToolStripMenuItem> miFsModes =
            new System.Collections.Generic.List<ToolStripMenuItem>();
        private readonly FeedServer feed = new FeedServer();
        private ZoneProfile profile;
        private string activeMonitorId;
        private string lastReason = "";
        private readonly string signalPath = Path.Combine(ProfileStore.DataDir, "signal.txt");

        // 引导正在开着（防止托盘连点开出两个）
        private bool onboardingOpen;

        /// <summary>
        /// 托盘图标。优先用随 exe 分发的 deadband.ico，
        /// 找不到就退回系统默认 —— 图标丢了不该让程序起不来。
        /// </summary>
        private static Icon LoadTrayIcon()
        {
            try
            {
                string exeDir = Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location);
                if (!string.IsNullOrEmpty(exeDir))
                {
                    string[] tries = new string[]
                    {
                        Path.Combine(exeDir, "deadband.ico"),
                        Path.Combine(exeDir, "..", "assets", "deadband.ico"),
                    };
                    for (int i = 0; i < tries.Length; i++)
                    {
                        if (File.Exists(tries[i])) return new Icon(tries[i]);
                    }
                }
            }
            catch { }
            return SystemIcons.Application;
        }

        public App(bool startupMode, bool forceOnboarding)
        {
            AppOptions.Load();

            // exe 被搬到别的目录之后，HKCU 里的启动项会指向旧路径 —— 每次启动顺手修掉
            Autostart.SyncPath();

            // 开机自启动：等任务栏出来再量 work area，否则 AppBar 会贴错位置
            if (startupMode) WaitForShell();

            tray = new NotifyIcon();
            tray.Icon = LoadTrayIcon();
            tray.Text = "DeadBand —— 坏屏信息区";
            tray.Visible = true;

            var menu = new ContextMenuStrip();

            miClock = new ToolStripMenuItem("显示时钟");
            miClock.Click += new EventHandler(delegate
            {
                if (profile == null) return;
                profile.ClockEnabled = !profile.ClockEnabled;
                ProfileStore.Save(profile);
                host.Apply(profile);
                LogState("时钟 " + (profile.ClockEnabled ? "开" : "关"));
            });
            menu.Items.Add(miClock);

            miSpectrum = new ToolStripMenuItem("显示频谱");
            miSpectrum.Click += new EventHandler(delegate
            {
                if (profile == null) return;
                profile.SpectrumEnabled = !profile.SpectrumEnabled;
                ProfileStore.Save(profile);
                host.Apply(profile);
                LogState("频谱 " + (profile.SpectrumEnabled ? "开" : "关"));
            });
            menu.Items.Add(miSpectrum);

            miCaption = new ToolStripMenuItem("显示字幕");
            miCaption.Click += new EventHandler(delegate
            {
                if (profile == null) return;
                profile.CaptionEnabled = !profile.CaptionEnabled;
                ProfileStore.Save(profile);
                host.Apply(profile);
                LogState("字幕 " + (profile.CaptionEnabled ? "开" : "关"));
            });
            menu.Items.Add(miCaption);

            var pomoMenu = new ToolStripMenuItem("番茄钟");
            string[] pomoIds = new string[] { "off", "exclusive", "coexist" };
            string[] pomoNames = new string[] { "关", "独占（盖住其他内容）", "共存（叠加在最上面）" };
            for (int i = 0; i < pomoIds.Length; i++)
            {
                string id = pomoIds[i];
                var it = new ToolStripMenuItem(pomoNames[i], null, new EventHandler(delegate
                {
                    if (profile == null) return;
                    profile.PomodoroMode = id;
                    ProfileStore.Save(profile);
                    host.Apply(profile);
                    LogState("番茄钟 = " + id);
                }));
                it.Tag = id;
                miPomoModes.Add(it);
                pomoMenu.DropDownItems.Add(it);
            }
            menu.Items.Add(pomoMenu);

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("设置…", null, new EventHandler(delegate { OpenSettings(); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出", null, new EventHandler(delegate { Quit(); }));
            menu.Opening += delegate { SyncMenuChecks(); };
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += new EventHandler(delegate { OpenSettings(); });

            host.FormClosed += new FormClosedEventHandler(delegate { Quit(); });

            DumpDisplays();
            ResolveProfile();

            // 本地推送端口：字幕来源（浏览器脚本）往这里发。
            // 不管当前内容是什么都开着 —— 免得用户切到「字幕」时脚本已经连失败过一轮。
            FeedServer.LocateUserScript();
            int wantPort = profile != null ? profile.FeedPort : FeedServer.DefaultPort;
            if (feed.Start(wantPort))
            {
                try
                {
                    File.WriteAllText(Path.Combine(ProfileStore.DataDir, "feed-url.txt"),
                        "推送：  " + feed.Url + Environment.NewLine +
                        "脚本：  http://127.0.0.1:" + feed.Port + "/userscript.user.js" + Environment.NewLine +
                        "脚本文件：" + (FeedServer.UserScriptFile.Length == 0 ? "(没找到)" : FeedServer.UserScriptFile) +
                        Environment.NewLine, new UTF8Encoding(false));
                }
                catch { }
            }
            else
            {
                LogState("字幕推送端口启动失败（" + wantPort + " 起连续 10 个端口都被占用）");
            }

            watcher.Interval = 2000;
            watcher.Tick += new EventHandler(delegate { WatchDisplays(); });
            watcher.Start();

            // 浏览器全屏（F11 / 视频全屏）不是系统级独占全屏，
            // AppBar 的 ABN_FULLSCREENAPP 不一定来，得自己盯着前台窗口。
            // 250ms 一轮：既够快（肉眼几乎看不出跳动），开销又只有两次 P/Invoke。
            fsWatcher.Interval = 250;
            fsWatcher.Tick += new EventHandler(delegate { WatchFullscreen(); });
            fsWatcher.Start();

            StartSignalListener();

            // 首次使用：等消息泵转起来再弹（ShowDialog 自己需要消息循环）。
            // 开机自启动时不弹 —— 登录瞬间不该有窗口抢屏幕。
            if (!startupMode && (forceOnboarding || NeedsOnboarding())) StartOnboardingSoon();
        }

        // ---------------- 开机自启动 ----------------
        /// <summary>
        /// 登录后立刻起来时，explorer / 任务栏可能还没建好。
        /// 这时候 Screen.WorkingArea 是过期的，AppBar 会被贴到错误的位置。
        /// 最多等 15 秒，但至少等 2.5 秒让 shell 稳定下来。
        /// </summary>
        private void WaitForShell()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                while (sw.ElapsedMilliseconds < 15000)
                {
                    if (Win32.ShellTrayReady() && sw.ElapsedMilliseconds > 2500) break;
                    Thread.Sleep(250);
                }
            }
            catch { }
            LogState("自启动：等任务栏 " + (int)sw.Elapsed.TotalMilliseconds + " ms");
        }

        /// <summary>有没有任何一份显示器配置。有 = 老用户，不该再弹引导。</summary>
        private static bool HasAnyProfile()
        {
            try
            {
                string[] f = Directory.GetFiles(ProfileStore.ProfileDir, "*.ini");
                return f.Length > 0;
            }
            catch { return false; }
        }

        /// <summary>
        /// 该不该弹引导。
        /// 已经配置过的老版本用户直接当"走过引导"处理 —— 他们本来就能用，别打扰。
        /// </summary>
        private static bool NeedsOnboarding()
        {
            if (AppOptions.Onboarded) return false;
            if (HasAnyProfile())
            {
                AppOptions.Onboarded = true;
                AppOptions.Save();
                return false;
            }
            return true;
        }

        // ---------------- 状态日志 ----------------
        private void LogState(string reason)
        {
            lastReason = reason;
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("at=" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                sb.AppendLine("reason=" + reason);
                sb.AppendLine("profileMonitor=" + (profile == null ? "(null)" : profile.MonitorId));
                sb.AppendLine("profileNorm=" + Win32.NormalizeMonitorId(profile == null ? "" : profile.MonitorId));
                sb.AppendLine("profileFile=" + (profile == null ? "" : ProfileStore.ProfilePath(profile.MonitorId)));
                sb.AppendLine("zoneHeight=" + (profile == null ? "-1" : profile.ZoneHeight.ToString()));
                sb.AppendLine("suspended=" + host.IsSuspended);
                sb.AppendLine("present=" + string.Join(",", PresentMonitorIds().ToArray()));
                sb.AppendLine("hostBounds=" + host.Bounds);
                sb.AppendLine("bottomGap=" + host.BottomGap);
                try
                {
                    IntPtr fg = Win32.GetForegroundWindow();
                    Win32.RECT fr;
                    Win32.GetWindowRect(fg, out fr);
                    sb.AppendLine("fgRect=" + fr.left + "," + fr.top + "," + fr.right + "," + fr.bottom);
                    sb.AppendLine("fgFullscreen=" + ForegroundCoversScreen());
                }
                catch { }
                File.WriteAllText(Path.Combine(ProfileStore.DataDir, "last.txt"), sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        // ---------------- 底部被占用提醒 ----------------
        private bool bottomGapWarned;

        private void WarnIfBottomGap()
        {
            if (host.BottomGap <= 0) { bottomGapWarned = false; return; }
            if (bottomGapWarned) return;
            bottomGapWarned = true;
            try
            {
                tray.ShowBalloonTip(6000, "DeadBand",
                    "底部还被占用 " + host.BottomGap + " px（通常是任务栏）。\n" +
                    "预留区被顶到了任务栏上方，和标定预览的位置不一致。\n" +
                    "请在系统设置里把任务栏移到顶部或设为自动隐藏。",
                    ToolTipIcon.Warning);
            }
            catch { }
        }

        /// <summary>造一组互斥的单选项，点中后写回 profile 的对应字段。</summary>
        private void AddChoice(ToolStripMenuItem parent,
                               System.Collections.Generic.List<ToolStripMenuItem> list,
                               string[] ids, string[] names, string field, string logName)
        {
            for (int i = 0; i < ids.Length; i++)
            {
                string id = ids[i];
                var it = new ToolStripMenuItem(names[i], null, new EventHandler(delegate
                {
                    if (profile == null) return;
                    if (field == "SpectrumWithClock") profile.SpectrumWithClock = id;
                    else if (field == "ClockWithCaption") profile.ClockWithCaption = id;
                    ProfileStore.Save(profile);
                    host.Apply(profile);
                    LogState(logName + " = " + id);
                }));
                it.Tag = id;
                list.Add(it);
                parent.DropDownItems.Add(it);
            }
        }

        // ---------------- 设置窗口 ----------------
        private void OpenSettings()
        {
            if (profile == null) return;

            string scriptUrl = "http://127.0.0.1:" + feed.Port + "/userscript.user.js";
            bool wantOnboard = false;
            using (var dlg = new SettingsForm(
                profile,
                scriptUrl,
                delegate { ProfileStore.Save(profile); host.Apply(profile); },
                OpenCalibrator,
                OpenClockSettings,
                OpenColorPicker,
                OpenInstallPage,
                OpenConfigDir,
                delegate { return host.PreviewMode; },
                delegate(bool on)
                {
                    host.PreviewMode = on;
                    CaptionFeed.Override = on ? "这是一行示例字幕，用来预览叠加效果" : null;
                    host.Invalidate();
                },
                delegate { wantOnboard = true; }))
            {
                dlg.ShowDialog();
            }

            // 预览只服务于设置过程，关窗就撤掉
            host.PreviewMode = false;
            CaptionFeed.Override = null;
            host.Invalidate();

            // 关窗后再同步一次：可能改过字号、网格之类影响布局的东西
            ProfileStore.Save(profile);
            host.Apply(profile);

            // 引导得等设置窗口真的关掉再开 —— 两个模态窗口叠着，
            // 用户中途退出引导会连程序一起退，留着设置窗口会很尴尬
            if (wantOnboard) BeginOnboarding();
        }
        private void OpenInstallPage()
        {
            try
            {
                System.Diagnostics.Process.Start("http://127.0.0.1:" + feed.Port + "/userscript.user.js");
            }
            catch (Exception ex) { LogState("打开脚本安装页失败：" + ex.Message); }
        }

        private void OpenConfigDir()
        {
            try { System.Diagnostics.Process.Start("explorer.exe", ProfileStore.DataDir); } catch { }
        }

        // ---------------- 菜单对号 ----------------
        /// <summary>每次打开菜单时按当前配置刷新对号，避免显示和实际不一致。</summary>
        private void SyncMenuChecks()
        {
            if (profile == null) return;

            if (miClock != null) miClock.Checked = profile.ClockEnabled;
            if (miSpectrum != null) miSpectrum.Checked = profile.SpectrumEnabled;
            if (miCaption != null) miCaption.Checked = profile.CaptionEnabled;

            string pm = profile.PomodoroMode == null ? "" : profile.PomodoroMode.Trim().ToLowerInvariant();
            if (pm.Length == 0) pm = "off";
            foreach (var it in miPomoModes)
                it.Checked = string.Equals(it.Tag as string, pm, StringComparison.OrdinalIgnoreCase);

            // 其余细节设置都在设置窗口里，托盘只同步上面这几项
        }

        // ---------------- 前台全屏检测 ----------------
        private bool lastFs;
        private string lastFgSnap = "";

        // 避让重试：同一个窗口连续压了几次、什么时候开始"干净"（没压着区带）
        private const int MaxAvoidAttempts = 8;
        private IntPtr avoidHwnd = IntPtr.Zero;
        private int avoidAttempts;
        private IntPtr avoidWarnedHwnd = IntPtr.Zero;
        private DateTime avoidCleanSince = DateTime.MinValue;

        /// <summary>当前是不是"让全屏窗口避让"模式。</summary>
        private bool IsAvoidMode()
        {
            string m = profile == null ? "" : profile.FullscreenMode;
            if (string.IsNullOrEmpty(m)) m = "avoid";
            m = m.Trim().ToLowerInvariant();
            return m == "avoid" || m == "shrink";   // shrink 是旧名字，按避让处理
        }

        private bool IsFullscreenRect(Rectangle r)
        {
            if (r.Width <= 0 || r.Height <= 0) return false;
            Screen scr = host.TargetScreen();
            Rectangle b = scr.Bounds;
            const int tol = 2;
            return r.Left <= b.Left + tol && r.Top <= b.Top + tol &&
                   r.Right >= b.Right - tol && r.Bottom >= b.Bottom - tol;
        }

        private bool IsOwnWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return true;
            if (hwnd == host.Handle) return true;
            try
            {
                uint pid;
                Win32.GetWindowThreadProcessId(hwnd, out pid);
                return pid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            }
            catch { return false; }
        }

        private static Rectangle RectOf(Win32.RECT r)
        {
            return Rectangle.FromLTRB(r.left, r.top, r.right, r.bottom);
        }

        /// <summary>
        /// 把窗口压到区带上沿。只用 SetWindowPos，不 hook、不改窗口样式。
        /// 返回值是"调用有没有发出去"，不代表对方一定听话 —— 服从情况靠下一轮复查判断。
        /// </summary>
        private bool AvoidWindow(IntPtr hwnd)
        {
            try
            {
                Screen scr = host.TargetScreen();
                Rectangle b = scr.Bounds;
                int zoneH = host.Bounds.Height;
                if (zoneH <= 0 || zoneH >= b.Height) return false;

                int top = b.Top;
                int height = (b.Bottom - zoneH) - top;
                if (height <= 0) return false;

                return Win32.SetWindowPos(hwnd, IntPtr.Zero,
                    b.Left, top, b.Width, height,
                    Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
            }
            catch { return false; }
        }

        private void LogFullscreen(IntPtr hwnd, Rectangle orig, Rectangle zone, int attempt, string result)
        {
            try
            {
                string path = Path.Combine(ProfileStore.DataDir, "fullscreen.txt");
                // 别让它无限长，超过 200KB 就从头来
                try
                {
                    var fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > 200 * 1024) fi.Delete();
                }
                catch { }

                string line =
                    "at=" + DateTime.Now.ToString("HH:mm:ss.fff") +
                    " hwnd=0x" + hwnd.ToInt64().ToString("X") +
                    " proc=" + Win32.ProcessNameOf(hwnd) +
                    " class=" + Win32.ClassNameOf(hwnd) +
                    " origRect=" + orig.Left + "," + orig.Top + "," + orig.Right + "," + orig.Bottom +
                    " zone=" + zone.Left + "," + zone.Top + "," + zone.Right + "," + zone.Bottom +
                    " attempt=" + attempt +
                    " result=" + result +
                    Environment.NewLine;
                File.AppendAllText(path, line, new UTF8Encoding(false));
            }
            catch { }
        }

        private void WarnAvoidFailed(IntPtr hwnd)
        {
            try
            {
                string who = Win32.ProcessNameOf(hwnd);
                if (string.IsNullOrEmpty(who)) who = "这个程序";
                tray.ShowBalloonTip(6000, "DeadBand",
                    who + " 不肯让开底部区域，试了 " + MaxAvoidAttempts + " 次。\n" +
                    "已改为覆盖显示：区带会压住画面最底下 " + host.Bounds.Height + " px。\n" +
                    "细节记在配置目录的 fullscreen.txt。",
                    ToolTipIcon.Info);
            }
            catch { }
        }

        private void WatchFullscreen()
        {
            if (profile == null || host.IsSuspended) return;

            IntPtr fg = Win32.GetForegroundWindow();
            if (IsOwnWindow(fg)) fg = IntPtr.Zero;

            Rectangle fr = Rectangle.Empty;
            Win32.RECT rr;
            if (fg != IntPtr.Zero && Win32.GetWindowRect(fg, out rr)) fr = RectOf(rr);

            Rectangle zr = host.Bounds;
            bool fs = fg != IntPtr.Zero && IsFullscreenRect(fr);
            // 全屏窗口压到了区带上（多留 2px 容差，避免边缘抖动反复触发）
            bool covered = fs && zr.Height > 0 && fr.Bottom > zr.Top + 2 && fr.Top < zr.Bottom;

            if (IsAvoidMode())
            {
                if (covered)
                {
                    avoidCleanSince = DateTime.MinValue;
                    if (avoidHwnd != fg) { avoidHwnd = fg; avoidAttempts = 0; }

                    if (avoidAttempts < MaxAvoidAttempts)
                    {
                        avoidAttempts++;
                        AvoidWindow(fg);
                        LogFullscreen(fg, fr, zr, avoidAttempts, "try");

                        if (avoidAttempts >= MaxAvoidAttempts && avoidWarnedHwnd != fg)
                        {
                            avoidWarnedHwnd = fg;
                            LogFullscreen(fg, fr, zr, avoidAttempts, "refused");
                            WarnAvoidFailed(fg);
                        }
                    }
                }
                else
                {
                    // 已经让开了。连续 5 秒不压着区带才把重试计数清零，
                    // 这样"缩一下又弹回来"会把次数累积起来，不会无限互相拉扯。
                    if (avoidCleanSince == DateTime.MinValue) avoidCleanSince = DateTime.Now;
                    else if ((DateTime.Now - avoidCleanSince).TotalSeconds > 5)
                    {
                        avoidAttempts = 0;
                        avoidHwnd = IntPtr.Zero;
                        avoidWarnedHwnd = IntPtr.Zero;
                    }
                }
            }

            // 兜底：前台窗口只要压着区带，就把自己重新推到顶层栈最上面。
            // 避让成功时这里是空转；避让失败时它保证你至少还看得见内容，
            // 而不是像以前那样整条消失。
            if (fg != IntPtr.Zero && zr.Height > 0 && fr.IntersectsWith(zr)) host.ReassertTopmost();

            // 诊断：前台矩形 / 判定结果变化时才写
            try
            {
                Screen sc = host.TargetScreen();
                Rectangle sbb = sc.Bounds;
                string snap = fr.Left + "," + fr.Top + "," + fr.Right + "," + fr.Bottom +
                              "|" + sbb.Left + "," + sbb.Top + "," + sbb.Right + "," + sbb.Bottom +
                              "|" + fs + "|" + covered + "|" + avoidAttempts + "|" + host.IsSuspended;
                if (snap != lastFgSnap)
                {
                    lastFgSnap = snap;
                    System.IO.File.WriteAllText(
                        System.IO.Path.Combine(ProfileStore.DataDir, "fg.txt"),
                        "at=" + DateTime.Now.ToString("HH:mm:ss.fff") +
                        "\r\nfgRect=" + fr.Left + "," + fr.Top + "," + fr.Right + "," + fr.Bottom +
                        "\r\nscreen=" + sbb.Left + "," + sbb.Top + "," + sbb.Right + "," + sbb.Bottom +
                        "\r\nzone=" + host.Bounds +
                        "\r\nmode=" + (profile.FullscreenMode == null ? "" : profile.FullscreenMode) +
                        "\r\nfullscreen=" + fs +
                        "\r\ncovered=" + covered +
                        "\r\nattempt=" + avoidAttempts +
                        "\r\nsuspended=" + host.IsSuspended +
                        "\r\n", new System.Text.UTF8Encoding(false));
                }
            }
            catch { }
            if (fs != lastFs)
            {
                lastFs = fs;
                LogState(fs ? "检测到前台全屏（模式 " + profile.FullscreenMode + "）" : "退出全屏");
            }
        }

        private bool ForegroundCoversScreen()
        {
            try
            {
                IntPtr fg = Win32.GetForegroundWindow();
                if (fg == IntPtr.Zero || fg == host.Handle) return false;

                uint pid;
                Win32.GetWindowThreadProcessId(fg, out pid);
                if (pid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id) return false;

                Win32.RECT r;
                if (!Win32.GetWindowRect(fg, out r)) return false;

                return IsFullscreenRect(Rectangle.FromLTRB(r.left, r.top, r.right, r.bottom));
            }
            catch { return false; }
        }

        // ---------------- 显示器档案 ----------------
        private void DumpDisplays()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# DeadBand 显示器诊断 —— " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                foreach (Screen s in Screen.AllScreens)
                {
                    sb.AppendLine("screen=" + s.DeviceName + " primary=" + s.Primary +
                                  " bounds=" + s.Bounds);
                }
                foreach (MonitorInfo m in Win32.GetMonitors())
                {
                    sb.AppendLine("monitor screen=" + m.ScreenDevice +
                                  " desc=" + m.Description +
                                  " id=" + m.MonitorId +
                                  " norm=" + Win32.NormalizeMonitorId(m.MonitorId));
                }
                File.WriteAllText(Path.Combine(ProfileStore.DataDir, "displays.txt"), sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        private static string MonitorIdForScreen(Screen s)
        {
            foreach (MonitorInfo m in Win32.GetMonitors())
                if (string.Equals(m.ScreenDevice, s.DeviceName, StringComparison.OrdinalIgnoreCase))
                    return m.MonitorId;
            return "";
        }

        private static List<string> PresentMonitorIds()
        {
            var list = new List<string>();
            foreach (MonitorInfo m in Win32.GetMonitors())
                list.Add(Win32.NormalizeMonitorId(m.MonitorId));
            return list;
        }

        private void ResolveProfile()
        {
            List<string> present = PresentMonitorIds();
            string[] files = new string[0];
            try { files = Directory.GetFiles(ProfileStore.ProfileDir, "*.ini"); } catch { }

            profile = null;
            activeMonitorId = "";

            // 1) 找一台「现在插着」的、有配置的显示器
            foreach (string f in files)
            {
                ZoneProfile p = ProfileStore.LoadFromPath(f);
                if (p == null) continue;
                if (present.Contains(Win32.NormalizeMonitorId(p.MonitorId)))
                {
                    profile = p;
                    activeMonitorId = p.MonitorId;
                    break;
                }
            }

            if (profile != null)
            {
                // 老配置里没有 screendevice，补上 —— 两块同分辨率的屏只能靠它区分
                if (string.IsNullOrEmpty(profile.ScreenDevice))
                {
                    foreach (MonitorInfo m in Win32.GetMonitors())
                    {
                        if (string.Equals(m.MonitorId, profile.MonitorId, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(Win32.NormalizeMonitorId(m.MonitorId),
                                          Win32.NormalizeMonitorId(profile.MonitorId),
                                          StringComparison.OrdinalIgnoreCase))
                        {
                            profile.ScreenDevice = m.ScreenDevice;
                            ProfileStore.Save(profile);
                            break;
                        }
                    }
                }
                host.Apply(profile);
                host.Resume();
                WarnIfBottomGap();
                LogState("已启用：" + profile.Description);
                return;
            }

            // 2) 一个配置都没有 → 给当前主屏做一份草稿（不动屏幕，等用户标定）
            if (files.Length == 0)
            {
                var p = new ZoneProfile();
                p.MonitorId = MonitorIdForScreen(Screen.PrimaryScreen);
                p.Description = Screen.PrimaryScreen.DeviceName;
                p.ScreenWidth = Screen.PrimaryScreen.Bounds.Width;
                p.ScreenHeight = Screen.PrimaryScreen.Bounds.Height;
                profile = p;
                activeMonitorId = p.MonitorId;
                LogState("还没有配置，正在进入首次设置");
                return;
            }

            // 3) 有配置但目标显示器不在（比如拔了外接屏）→ 什么都不做
            LogState("目标显示器未连接，功能已停用");
            host.Suspend();
        }

        private void WatchDisplays()
        {
            // 引导期间区带是我们**故意**挂起的（预检要量干净的 work area），
            // 这里要是照常"发现它挂着就恢复"，两秒后就把预检搞砸了。
            if (onboardingOpen) return;

            List<string> present = PresentMonitorIds();
            string want = Win32.NormalizeMonitorId(activeMonitorId);
            bool isPresent = want.Length > 0 && present.Contains(want);

            if (profile == null) return;

            if (isPresent && host.IsSuspended)
            {
                // 显示器回来了：重新贴边
                profile.ScreenWidth = Screen.PrimaryScreen.Bounds.Width;
                profile.ScreenHeight = Screen.PrimaryScreen.Bounds.Height;
                host.Apply(profile);
                host.Resume();
                WarnIfBottomGap();
                LogState("已恢复：" + profile.Description);
            }
            else if (!isPresent && !host.IsSuspended)
            {
                host.Suspend();
                LogState("目标显示器已断开，功能自动停用");
            }
        }

        // ---------------- 时钟大小与位置 ----------------
        private void OpenClockSettings()
        {
            if (profile == null) return;
            using (var dlg = new ClockSettingsForm(profile, host.Bounds.Height, delegate(ZoneProfile p)
            {
                host.Apply(p);
                ProfileStore.Save(p);
            }))
            {
                dlg.ShowDialog();
            }
            host.Apply(profile);
            LogState("时钟 字号" + profile.ClockScale + "% 位移(" +
                profile.ClockOffsetX + "," + profile.ClockOffsetY + ")");
        }

        // ---------------- 取色 ----------------
        private void OpenColorPicker(int which)
        {
            if (profile == null) return;

            string title, hint;
            Color init;
            if (which == 1)
            {
                title = "DeadBand — 网格颜色";
                hint = "把网格色调到接近面板坏线的颜色\r\n（那几条横向灰线的观感色）。\r\n\r\n竖线和横线同色同间距，\r\n就会连成一张栅格，坏线被收编进去。\r\n\r\n拖动滑块会立刻应用到底部区带。";
                init = profile.GridColor;
            }
            else if (which == 2)
            {
                title = "DeadBand — 时钟字色";
                hint = "像素时钟的字色。\r\n\r\n用托盘菜单的「时钟跟随网格色」\r\n可以回到同色系自动配色。\r\n\r\n拖动滑块会立刻应用到底部区带。\r\n（切到「像素时钟」内容才看得到）";
                init = profile.ClockColor;
            }
            else if (which == 3)
            {
                title = "DeadBand — 字幕字色";
                hint = "浏览器推过来的字幕用什么颜色。\r\n\r\n白色最好读；\r\n想让字和网格更一体，\r\n可以调成接近网格色的灰。\r\n\r\n拖动滑块会立刻应用到底部区带。\r\n（切到「字幕」内容才看得到）";
                init = profile.CaptionColor;
            }
            else
            {
                title = "DeadBand — 区带背景色";
                hint = "区带背景色。\r\n\r\n配合「背景网格」使用：\r\n纯黑底 + 同色竖线 + 面板自己的横线\r\n= 一张栅格，坏线看起来是设计的一部分。\r\n\r\n拖动滑块会立刻应用到底部区带。";
                init = profile.BaseColor;
            }

            Color[] swatches = (which == 2 || which == 3)
                ? ColorPickerForm.ClockSwatches()
                : ColorPickerForm.GraySwatches();
            using (var dlg = new ColorPickerForm(title, hint, init, swatches, delegate(Color c)
            {
                if (which == 1) profile.GridColor = c;
                else if (which == 2) { profile.ClockColor = c; profile.ClockAutoColor = false; }
                else if (which == 3) profile.CaptionColor = c;
                else profile.BaseColor = c;
                host.Apply(profile);
                ProfileStore.Save(profile);
            }))
            {
                dlg.ShowDialog();
            }
            host.Apply(profile);
            Color now = which == 1 ? profile.GridColor
                      : (which == 2 ? profile.ClockColor
                      : (which == 3 ? profile.CaptionColor : profile.BaseColor));
            LogState((which == 1 ? "网格色 " : (which == 2 ? "时钟色 " : (which == 3 ? "字幕色 " : "背景色 "))) +
                string.Format("#{0:X2}{1:X2}{2:X2}", now.R, now.G, now.B));
        }

        // ---------------- 标定 ----------------
        public void InvokeSettings() { OpenSettings(); }

        /// <summary>预览模式开关：用示例内容代替真实频谱/字幕，方便调叠加规则。</summary>
        public void TogglePreview()
        {
            host.PreviewMode = !host.PreviewMode;
            CaptionFeed.Override = host.PreviewMode ? "这是一行示例字幕，用来预览叠加效果" : null;
            host.Invalidate();
            LogState("预览模式 " + (host.PreviewMode ? "开" : "关"));
        }

        public void InvokeCalibrate()
        {
            OpenCalibrator();
        }

        public void InvokeOnboarding()
        {
            BeginOnboarding();
        }

        /// <summary>信号/托盘传来的标定请求，不关心结果。</summary>
        private void OpenCalibrator()
        {
            RunCalibrator();
        }

        /// <summary>
        /// 进整屏标定。成功保存返回新的 profile，取消返回 null。
        /// 引导需要这个返回值来接着往下走，所以拆成了返回值的版本。
        /// </summary>
        private ZoneProfile RunCalibrator()
        {
            if (profile == null) return null;

            Screen target = host.TargetScreen();
            if (target == null) target = Screen.PrimaryScreen;

            bool wasSuspended = host.IsSuspended;
            host.Hide();

            ZoneProfile result = null;
            using (var cal = new Calibrator(profile, target))
            {
                cal.ShowDialog();
                if (cal.Accepted)
                {
                    ZoneProfile r = cal.Result;
                    r.MonitorId = profile.MonitorId;
                    r.Description = profile.Description;
                    r.ScreenDevice = target.DeviceName;
                    r.ScreenWidth = target.Bounds.Width;
                    r.ScreenHeight = target.Bounds.Height;
                    profile = r;
                    activeMonitorId = r.MonitorId;
                    ProfileStore.Save(profile);
                    host.Apply(profile);
                    result = profile;
                }
            }

            if (!wasSuspended) host.Resume();
            else host.Suspend();

            if (result != null) LogState("已标定：预留区 " + result.ZoneHeight + " px");
            return result;
        }

        // ---------------- 首次使用引导 ----------------
        private void StartOnboardingSoon()
        {
            var t = new System.Windows.Forms.Timer();
            t.Interval = 320;
            t.Tick += delegate
            {
                t.Stop();
                t.Dispose();
                BeginOnboarding();
            };
            t.Start();
        }

        private void BeginOnboarding()
        {
            if (onboardingOpen) return;
            onboardingOpen = true;
            try { RunOnboarding(); }
            finally { onboardingOpen = false; }
        }

        /// <summary>引导里改完立即生效：先存盘，再让区带重画。</summary>
        private void ApplyProfile(ZoneProfile p)
        {
            if (p == null) return;
            profile = p;
            activeMonitorId = p.MonitorId;
            ProfileStore.Save(p);
            host.Apply(p);
        }

        /// <summary>
        /// 取某台显示器的配置：有就读出来，没有就基于它造一份草稿。
        /// 引导里换显示器走这里，这样已经在用的那台不会丢设置。
        /// </summary>
        private ZoneProfile LoadOrCreateFor(MonitorInfo m)
        {
            if (m == null) return profile;

            ZoneProfile p = ProfileStore.Load(m.MonitorId);
            if (p == null)
            {
                p = new ZoneProfile();
                p.MonitorId = m.MonitorId;
            }
            if (string.IsNullOrEmpty(p.MonitorId)) p.MonitorId = m.MonitorId;
            if (string.IsNullOrEmpty(p.Description)) p.Description = m.Description;
            p.ScreenDevice = m.ScreenDevice;

            Screen s = null;
            foreach (Screen sc in Screen.AllScreens)
                if (string.Equals(sc.DeviceName, m.ScreenDevice, StringComparison.OrdinalIgnoreCase)) s = sc;
            if (s != null)
            {
                p.ScreenWidth = s.Bounds.Width;
                p.ScreenHeight = s.Bounds.Height;
            }
            return p;
        }

        private void RunOnboarding()
        {
            if (profile == null)
            {
                // 极端情况：连草稿都没有。造一份，否则引导没有东西可改。
                var p0 = new ZoneProfile();
                if (Screen.PrimaryScreen != null)
                {
                    p0.MonitorId = MonitorIdForScreen(Screen.PrimaryScreen);
                    p0.ScreenDevice = Screen.PrimaryScreen.DeviceName;
                    p0.Description = Screen.PrimaryScreen.DeviceName;
                    p0.ScreenWidth = Screen.PrimaryScreen.Bounds.Width;
                    p0.ScreenHeight = Screen.PrimaryScreen.Bounds.Height;
                }
                profile = p0;
                activeMonitorId = p0.MonitorId;
            }

            // 先把区带收起来：预检要量一个干净的 work area，
            // 否则会把自己占用的那一条当成"屏幕底部被别的程序占了"。
            // 预检通过之后由引导里的 showZone 回调重新贴回去。
            host.Suspend();

            var list = new List<MonitorInfo>(Win32.GetMonitors());

            bool ok = false;
            try
            {
                using (var dlg = new OnboardingForm(
                    profile,
                    list.ToArray(),
                    new Action<ZoneProfile>(ApplyProfile),
                    new Func<ZoneProfile>(RunCalibrator),
                    new Func<MonitorInfo, ZoneProfile>(LoadOrCreateFor),
                    delegate { ApplyProfile(profile); host.Resume(); },
                    delegate { host.Suspend(); },
                    delegate { return (host.Visible && !host.IsSuspended) ? host.Bounds.Height : 0; },
                    new Action<int>(OpenColorPicker),
                    delegate { OpenInstallPage(); },
                    delegate { OpenSettings(); },
                    delegate { OpenConfigDir(); },
                    delegate(bool on)
                    {
                        host.PreviewMode = on;
                        CaptionFeed.Override = on ? "这是一行示例字幕，用来预览叠加效果" : null;
                        host.Invalidate();
                    }))
                {
                    dlg.ShowDialog();
                    ok = dlg.Completed;
                }
            }
            catch (Exception ex)
            {
                LogState("引导窗口出错：" + ex.Message);
                ok = false;
            }

            host.PreviewMode = false;
            CaptionFeed.Override = null;
            host.Invalidate();

            if (!ok)
            {
                // 用户在引导里退出了 —— 直接关掉程序，不留一个什么都不做的托盘图标
                LogState("引导被取消，退出程序");
                Quit();
                return;
            }

            AppOptions.Onboarded = true;
            AppOptions.Save();
            if (profile != null)
            {
                ProfileStore.Save(profile);
                host.Apply(profile);
                host.Resume();
                WarnIfBottomGap();
                LogState("引导完成：" + profile.Description);
            }
        }

        // ---------------- 单实例信号 ----------------
        public static void SignalRunning(string what)
        {
            try
            {
                File.WriteAllText(Path.Combine(ProfileStore.DataDir, "signal.txt"), what, new UTF8Encoding(false));
                using (var ev = EventWaitHandle.OpenExisting("DeadBand.Signal")) ev.Set();
            }
            catch { }
        }

        private void StartSignalListener()
        {
            var t = new Thread(delegate ()
            {
                try
                {
                    using (var ev = new EventWaitHandle(false, EventResetMode.AutoReset, "DeadBand.Signal"))
                    {
                        while (ev.WaitOne())
                        {
                            string what = "";
                            try { if (File.Exists(signalPath)) { what = File.ReadAllText(signalPath).Trim(); File.Delete(signalPath); } } catch { }
                            host.BeginInvoke((MethodInvoker)delegate
                            {
                                if (what == "off") Quit();
                                else if (what == "color") OpenColorPicker(0);
                                else if (what == "clock") OpenClockSettings();
                                else if (what == "calibrate") OpenCalibrator();
                                else if (what == "settings") OpenSettings();
                                else if (what == "preview") TogglePreview();
                                else if (what == "onboard") BeginOnboarding();
                            });
                        }
                    }
                }
                catch { }
            });
            t.IsBackground = true;
            t.Start();
        }

        private void Quit()
        {
            fsWatcher.Stop();
            watcher.Stop();
            feed.Stop();
            tray.Visible = false;
            tray.Dispose();
            host.Close();
            ExitThread();
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            try { Win32.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
            try { Directory.CreateDirectory(ProfileStore.DataDir); } catch { }

            bool calibrate = false;
            bool off = false;
            bool color = false;
            bool settings = false;
            bool preview = false;
            bool uiCheck = false;
            bool startup = false;
            bool onboard = false;
            foreach (string a in args)
            {
                if (string.Equals(a, "--calibrate", StringComparison.OrdinalIgnoreCase)) calibrate = true;
                else if (string.Equals(a, "--color", StringComparison.OrdinalIgnoreCase)) color = true;
                else if (string.Equals(a, "--clock", StringComparison.OrdinalIgnoreCase)) color = true;
                else if (string.Equals(a, "--ui-check", StringComparison.OrdinalIgnoreCase)) uiCheck = true;
                else if (string.Equals(a, "--off", StringComparison.OrdinalIgnoreCase)) off = true;
                else if (string.Equals(a, "--settings", StringComparison.OrdinalIgnoreCase)) settings = true;
                else if (string.Equals(a, "--preview", StringComparison.OrdinalIgnoreCase)) preview = true;
                else if (string.Equals(a, Autostart.StartupArg, StringComparison.OrdinalIgnoreCase)) startup = true;
                else if (string.Equals(a, "--onboard", StringComparison.OrdinalIgnoreCase)) onboard = true;
            }

            // 开发期自检：不启动主程序，只构造窗口并检查控件重叠
            if (uiCheck)
            {
                var probe = new ZoneProfile();
                probe.ScreenWidth = Screen.PrimaryScreen != null ? Screen.PrimaryScreen.Bounds.Width : 1920;
                probe.ScreenHeight = Screen.PrimaryScreen != null ? Screen.PrimaryScreen.Bounds.Height : 1080;
                using (var dlg = new ColorPickerForm("DeadBand — 取色器", "自检用", probe.BaseColor, null, null))
                {
                    dlg.DumpOverlaps(Path.Combine(ProfileStore.DataDir, "ui-check.txt"));
                }
                using (var dlg2 = new ClockSettingsForm(probe, 99, null))
                {
                    dlg2.DumpOverlaps(Path.Combine(ProfileStore.DataDir, "ui-check-clock.txt"));
                }
                // 引导窗口是手写坐标最多的一个，重点查
                using (var dlg3 = new OnboardingForm(probe,
                           Win32.GetMonitors().ToArray(),
                           null, null, null, null, null, null, null, null, null, null, null))
                {
                    dlg3.DumpOverlaps(Path.Combine(ProfileStore.DataDir, "ui-check-onboard.txt"));
                }
                return;
            }

            bool created;
            using (var mutex = new Mutex(true, "DeadBand.SingleInstance", out created))
            {
                if (!created)
                {
                    App.SignalRunning(off ? "off"
                        : (color ? "clock"
                        : (settings ? "settings"
                        : (preview ? "preview"
                        : (onboard ? "onboard" : "calibrate")))));
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                var app = new App(startup, onboard);
                if (calibrate) app.InvokeCalibrate();
                else if (settings) app.InvokeSettings();
                Application.Run(app);
            }
        }
    }
}
