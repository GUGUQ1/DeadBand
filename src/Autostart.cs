using System;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DeadBand
{
    /// <summary>
    /// 开机自启动。用 HKCU 的 Run 键 —— 不需要管理员，
    /// 任务管理器的「启动」页里也能看到并禁用。
    ///
    /// 三个坑都在这里处理掉：
    ///   1) 程序被搬到别的目录后，Run 项里的路径会失效
    ///      -> SyncPath() 在每次启动时自动修正
    ///   2) 用户在任务管理器里禁用之后，Run 项仍然存在
    ///      -> IsOn() 会去读 StartupApproved 里的禁用位
    ///   3) 启用时要清掉禁用位，否则设了也不启动
    ///      -> Set(true) 顺手删掉那条 Approved
    /// </summary>
    internal static class Autostart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ApprovedKey =
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        private const string ValueName = "DeadBand";

        /// <summary>启动时带这个参数：等任务栏就绪后再注册 AppBar，且不弹引导。</summary>
        public const string StartupArg = "--startup";

        public static string ExePath
        {
            get
            {
                try { return Application.ExecutablePath; }
                catch { return ""; }
            }
        }

        /// <summary>要写进注册表的完整命令行。</summary>
        private static string Command
        {
            get { return "\"" + ExePath + "\" " + StartupArg; }
        }

        /// <summary>Run 键里有没有这一项（不管有没有被任务管理器禁用）。</summary>
        public static bool HasEntry()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (k == null) return false;
                    return k.GetValue(ValueName) != null;
                }
            }
            catch { return false; }
        }

        /// <summary>任务管理器里是不是被用户关掉了（禁用位在 StartupApproved 里）。</summary>
        public static bool DisabledByTaskManager()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(ApprovedKey, false))
                {
                    if (k == null) return false;
                    byte[] b = k.GetValue(ValueName) as byte[];
                    if (b == null || b.Length == 0) return false;
                    return (b[0] & 1) == 1;
                }
            }
            catch { return false; }
        }

        /// <summary>现在真的会开机启动吗。</summary>
        public static bool IsOn()
        {
            return HasEntry() && !DisabledByTaskManager();
        }

        /// <summary>Run 项里存的原始命令行，诊断用。</summary>
        public static string StoredCommand()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (k == null) return "";
                    object v = k.GetValue(ValueName);
                    return v == null ? "" : v.ToString();
                }
            }
            catch { return ""; }
        }

        /// <summary>注册表里的路径和当前 exe 是不是同一份。</summary>
        public static bool PathMatches()
        {
            string s = StoredCommand();
            string exe = ExePath;
            if (s.Length == 0 || exe.Length == 0) return false;
            return s.IndexOf(exe, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>写或者删 Run 项。返回是否成功（注册表可能被策略锁住）。</summary>
        public static bool Set(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (k == null) return false;
                    if (on) k.SetValue(ValueName, Command, RegistryValueKind.String);
                    else k.DeleteValue(ValueName, false);
                }

                if (on)
                {
                    // 清掉「被任务管理器禁用」的痕迹，否则设了也不会启动
                    try
                    {
                        using (RegistryKey a = Registry.CurrentUser.OpenSubKey(ApprovedKey, true))
                            if (a != null) a.DeleteValue(ValueName, false);
                    }
                    catch { }
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>exe 搬家了就改成新路径。只在已经开着自启动时才动。</summary>
        public static void SyncPath()
        {
            try
            {
                if (!IsOn()) return;
                if (PathMatches()) return;
                Set(true);
            }
            catch { }
        }
    }
}
