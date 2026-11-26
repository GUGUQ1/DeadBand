using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace DeadBand
{
    /// <summary>
    /// 全局热键。用 RegisterHotKey —— 注册在区带窗口上，
    /// 所以**别的程序在前台时也能触发**，这正是"随手把区带收起来"需要的。
    ///
    /// 组合键在配置里存成人类可读的字符串（"Ctrl+Alt+H"），
    /// 这里负责字符串 ↔ (修饰键, 虚拟键) 的互转。
    /// </summary>
    internal static class Hotkey
    {
        private const int Id_ = 0xB0DB;

        public const int WM_HOTKEY = 0x0312;

        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_WIN = 0x0008;
        private const uint MOD_NOREPEAT = 0x4000;   // 按住不放别连续触发

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public static int Id { get { return Id_; } }

        /// <summary>解析 "Ctrl+Alt+H"。空串或无效返回 false。</summary>
        public static bool TryParse(string spec, out uint mods, out uint vk)
        {
            mods = 0;
            vk = 0;
            if (string.IsNullOrEmpty(spec)) return false;

            string[] parts = spec.Split('+');
            for (int i = 0; i < parts.Length; i++)
            {
                string t = parts[i].Trim().ToLowerInvariant();
                if (t.Length == 0) return false;

                if (i < parts.Length - 1)
                {
                    if (t == "ctrl" || t == "control") mods |= MOD_CONTROL;
                    else if (t == "alt") mods |= MOD_ALT;
                    else if (t == "shift") mods |= MOD_SHIFT;
                    else if (t == "win") mods |= MOD_WIN;
                    else return false;
                }
                else
                {
                    vk = KeyCode(t);
                    if (vk == 0) return false;
                }
            }

            // 必须至少有一个修饰键：没有修饰键的全局热键会把正常打字全抢走
            return vk != 0 && (mods & 0x000F) != 0;
        }

        private static uint KeyCode(string t)
        {
            if (t.Length == 1)
            {
                char c = char.ToUpperInvariant(t[0]);
                if (c >= 'A' && c <= 'Z') return (uint)c;
                if (c >= '0' && c <= '9') return (uint)c;
                return 0;
            }
            if (t.Length <= 3 && t[0] == 'f')
            {
                int n;
                if (int.TryParse(t.Substring(1), out n) && n >= 1 && n <= 24) return (uint)(0x70 + n - 1);
            }
            switch (t)
            {
                case "space": return 0x20;
                case "tab": return 0x09;
                case "enter": return 0x0D;
                case "insert": case "ins": return 0x2D;
                case "delete": case "del": return 0x2E;
                case "home": return 0x24;
                case "end": return 0x23;
                case "pageup": case "pgup": return 0x21;
                case "pagedown": case "pgdn": return 0x22;
                case "up": return 0x26;
                case "down": return 0x28;
                case "left": return 0x25;
                case "right": return 0x27;
                case "minus": return 0xBD;
                case "plus": return 0xBB;
            }
            return 0;
        }

        /// <summary>
        /// 录制用：把一次按键翻译成规范字符串。只按了修饰键、或者结果不合法都返回 ""。
        /// </summary>
        public static string FromKey(Keys key, Keys modifiers)
        {
            if (key == Keys.None) return "";
            if (key == Keys.ControlKey || key == Keys.LControlKey || key == Keys.RControlKey ||
                key == Keys.Menu || key == Keys.LMenu || key == Keys.RMenu ||
                key == Keys.ShiftKey || key == Keys.LShiftKey || key == Keys.RShiftKey ||
                key == Keys.LWin || key == Keys.RWin) return "";

            var sb = new StringBuilder();
            if ((modifiers & Keys.Control) != 0) sb.Append("Ctrl+");
            if ((modifiers & Keys.Alt) != 0) sb.Append("Alt+");
            if ((modifiers & Keys.Shift) != 0) sb.Append("Shift+");
            if ((modifiers & Keys.LWin) != 0 || (modifiers & Keys.RWin) != 0) sb.Append("Win+");

            string name = KeyName(key);
            if (name.Length == 0) return "";

            sb.Append(name);
            string result = sb.ToString();
            uint mods, vk;
            return TryParse(result, out mods, out vk) ? result : "";
        }

        private static string KeyName(Keys key)
        {
            if (key >= Keys.A && key <= Keys.Z) return ((char)('A' + ((int)key - (int)Keys.A))).ToString();
            if (key >= Keys.D0 && key <= Keys.D9) return ((char)('0' + ((int)key - (int)Keys.D0))).ToString();
            if (key >= Keys.NumPad0 && key <= Keys.NumPad9) return "Num" + ((int)key - (int)Keys.NumPad0);
            if (key >= Keys.F1 && key <= Keys.F24) return "F" + ((int)key - (int)Keys.F1 + 1);
            switch (key)
            {
                case Keys.Space: return "Space";
                case Keys.Tab: return "Tab";
                case Keys.Enter: return "Enter";
                case Keys.Insert: return "Insert";
                case Keys.Delete: return "Delete";
                case Keys.Home: return "Home";
                case Keys.End: return "End";
                case Keys.PageUp: return "PageUp";
                case Keys.PageDown: return "PageDown";
                case Keys.Up: return "Up";
                case Keys.Down: return "Down";
                case Keys.Left: return "Left";
                case Keys.Right: return "Right";
                case Keys.OemMinus: return "Minus";
                case Keys.Oemplus: return "Plus";
            }
            return "";
        }

        /// <summary>注册。会先注销旧的。返回是否成功（可能被别的程序占用）。</summary>
        public static bool Register(IntPtr hwnd, string spec)
        {
            uint mods, vk;
            if (hwnd == IntPtr.Zero) return false;
            if (!TryParse(spec, out mods, out vk)) return false;
            Unregister(hwnd);
            return RegisterHotKey(hwnd, Id_, mods | MOD_NOREPEAT, vk);
        }

        public static void Unregister(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            try { UnregisterHotKey(hwnd, Id_); }
            catch { }
        }
    }
}
