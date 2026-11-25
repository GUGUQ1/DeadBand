using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace DeadBand
{
    /// <summary>
    /// 推送进来的那一行文本。字幕、歌词、通知都走这里，只有一个"当前值"。
    /// 写在静态上是因为：来源（浏览器脚本）和渲染（区带窗口）不在同一条调用链上。
    /// </summary>
    internal static class CaptionFeed
    {
        private static readonly object gate = new object();
        private static string text = "";
        private static string sub = "";
        private static string id = "";
        private static DateTime stamp = DateTime.Now;

        public static string Text { get { lock (gate) { return Override != null ? Override : text; } } }
        public static string Sub { get { lock (gate) { return Override != null ? "" : sub; } } }
        public static string Id { get { lock (gate) { return id; } } }
        public static DateTime Stamp { get { lock (gate) { return stamp; } } }

        /// <summary>
        /// 预览用的覆盖文本。非 null 时 Text 返回它 ——
        /// 设置窗口里的「预览模式」靠这个显示示例字幕，不影响真实推送。
        /// </summary>
        public static string Override;

        public static void Set(string newText, string newSub, string newId)
        {
            lock (gate)
            {
                text = OneLine(newText);
                sub = OneLine(newSub);
                id = newId == null ? "" : newId;
                stamp = DateTime.Now;
            }
        }

        public static void Clear() { Set("", "", "clear"); }

        private static string OneLine(string s)
        {
            if (s == null) return "";
            return s.Replace("\r", " ").Replace("\n", " ").Trim();
        }
    }

    /// <summary>
    /// 应用级选项（不分显示器，跟 profile 分开存）。
    /// hideplayersubtitle = 要不要把播放器自带的字幕藏起来
    /// onboarded          = 首次设置引导走过没有
    /// </summary>
    internal static class AppOptions
    {
        public static bool HidePlayerSubtitle = true;
        public static bool Onboarded;

        private static string FilePath
        {
            get { return System.IO.Path.Combine(ProfileStore.DataDir, "options.ini"); }
        }

        public static void Load()
        {
            try
            {
                if (!System.IO.File.Exists(FilePath)) return;
                string[] lines = System.IO.File.ReadAllLines(FilePath);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                    string v = line.Substring(eq + 1).Trim().ToLowerInvariant();
                    if (k == "hideplayersubtitle") HidePlayerSubtitle = !(v == "0" || v == "false");
                    else if (k == "onboarded") Onboarded = !(v == "0" || v == "false");
                }
            }
            catch { }
        }

        public static void Save()
        {
            try
            {
                string text = "# DeadBand 应用级选项（不分显示器）\n" +
                              "hideplayersubtitle=" + (HidePlayerSubtitle ? "1" : "0") + "\n" +
                              "onboarded=" + (Onboarded ? "1" : "0") + "\n";
                System.IO.File.WriteAllText(FilePath, text, new UTF8Encoding(false));
            }
            catch { }
        }
    }

    /// <summary>
    /// 一个极小的本地 HTTP 服务，只认 /feed。
    ///
    /// 故意不用 HttpListener：那个要先注册 URL ACL（等于要管理员权限），
    /// TcpListener 绑 127.0.0.1 什么都不用，用户双击就能跑。
    /// </summary>
    internal sealed class FeedServer
    {
        public const int DefaultPort = 41723;
        private const int PortTries = 10;

        /// <summary>
        /// 要对外提供的用户脚本路径。启动时算好。
        ///
        /// 之所以由 DeadBand 自己发脚本：省掉用户"复制粘贴进 Tampermonkey"这一步，
        /// 而且可以在发送时把端口改写成真实端口（默认端口被占时不会指向错的地方）。
        /// </summary>
        public static string UserScriptFile = "";

        public static void LocateUserScript()
        {
            var candidates = new List<string>();
            try
            {
                string exeDir = System.IO.Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location);
                if (!string.IsNullOrEmpty(exeDir))
                {
                    candidates.Add(System.IO.Path.Combine(exeDir, "tools", "bilibili-subtitle.user.js"));
                    candidates.Add(System.IO.Path.Combine(exeDir, "..", "tools", "bilibili-subtitle.user.js"));
                }
            }
            catch { }
            candidates.Add(System.IO.Path.Combine(ProfileStore.DataDir, "userscript", "bilibili-subtitle.user.js"));

            UserScriptFile = "";
            for (int i = 0; i < candidates.Count; i++)
            {
                try
                {
                    if (!System.IO.File.Exists(candidates[i])) continue;
                    UserScriptFile = System.IO.Path.GetFullPath(candidates[i]);
                    return;
                }
                catch { }
            }
        }

        private TcpListener listener;
        private Thread thread;
        private volatile bool running;

        /// <summary>实际绑上的端口（默认端口被占时会往后挪）。</summary>
        public int Port { get; private set; }
        public bool Running { get { return running; } }
        public string Url { get { return "http://127.0.0.1:" + Port + "/feed"; } }

        public bool Start(int wantPort)
        {
            if (running) return true;
            if (wantPort <= 0) wantPort = DefaultPort;

            for (int i = 0; i < PortTries; i++)
            {
                int p = wantPort + i;
                try
                {
                    var l = new TcpListener(IPAddress.Loopback, p);
                    l.Start();
                    listener = l;
                    Port = p;
                    break;
                }
                catch { }
            }
            if (listener == null) return false;

            running = true;
            thread = new Thread(Loop);
            thread.IsBackground = true;
            thread.Start();
            return true;
        }

        public void Stop()
        {
            running = false;
            try { if (listener != null) listener.Stop(); } catch { }
            listener = null;
        }

        private void Loop()
        {
            while (running)
            {
                TcpClient client = null;
                try { client = listener.AcceptTcpClient(); }
                catch { if (!running) return; continue; }

                try { HandleOne(client); }
                catch { }
                finally { try { client.Close(); } catch { } }
            }
        }

        // ---------------- 一条连接 ----------------
        private void HandleOne(TcpClient client)
        {
            try { client.ReceiveTimeout = 3000; client.SendTimeout = 3000; } catch { }

            using (NetworkStream ns = client.GetStream())
            {
                byte[] buf = new byte[24576];
                int total = 0;
                int headerEnd = -1;

                while (total < buf.Length)
                {
                    int n = ns.Read(buf, total, buf.Length - total);
                    if (n <= 0) break;
                    total += n;
                    headerEnd = FindHeaderEnd(buf, total);
                    if (headerEnd >= 0) break;
                }
                if (headerEnd < 0) { Write(ns, 400, "{\"ok\":false}"); return; }

                string head = Encoding.UTF8.GetString(buf, 0, headerEnd);
                string[] lines = head.Split(new string[] { "\r\n" }, StringSplitOptions.None);

                string method = "GET";
                string target = "/";
                if (lines.Length > 0)
                {
                    string[] parts = lines[0].Split(' ');
                    if (parts.Length > 0) method = parts[0].ToUpperInvariant();
                    if (parts.Length > 1) target = parts[1];
                }

                int contentLength = 0;
                for (int i = 1; i < lines.Length; i++)
                {
                    int colon = lines[i].IndexOf(':');
                    if (colon <= 0) continue;
                    string k = lines[i].Substring(0, colon).Trim();
                    if (!string.Equals(k, "Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
                    int.TryParse(lines[i].Substring(colon + 1).Trim(), out contentLength);
                }

                int bodyStart = headerEnd + 4;
                while (total - bodyStart < contentLength && total < buf.Length)
                {
                    int n = ns.Read(buf, total, buf.Length - total);
                    if (n <= 0) break;
                    total += n;
                }
                string body = "";
                if (contentLength > 0 && total > bodyStart)
                {
                    int len = Math.Min(contentLength, total - bodyStart);
                    body = Encoding.UTF8.GetString(buf, bodyStart, len);
                }

                if (method == "OPTIONS") { Log(method, target, "preflight"); Write(ns, 204, ""); return; }

                // 配置查询：浏览器脚本靠这个知道要不要藏掉播放器自带的字幕。
                // 不写日志，否则每 5 秒一条把日志冲爆了。
                if (target.StartsWith("/config", StringComparison.OrdinalIgnoreCase))
                {
                    Write(ns, 200, "{\"hideSubtitle\":" + (AppOptions.HidePlayerSubtitle ? "true" : "false") + "}");
                    return;
                }

                // 用户脚本本体：浏览器打开这个地址就直接进 Tampermonkey 的安装页。
                if (target.StartsWith("/userscript", StringComparison.OrdinalIgnoreCase) ||
                    target.StartsWith("/bilibili-subtitle", StringComparison.OrdinalIgnoreCase))
                {
                    ServeUserScript(ns);
                    return;
                }

                // 页面 DOM 结构上报：脚本找不到字幕时把候选元素清单发过来，
                // 用来对真实类名，省得让用户去 F12 抄。
                if (target.StartsWith("/diag", StringComparison.OrdinalIgnoreCase))
                {
                    SaveDomReport(body);
                    Write(ns, 200, "{\"ok\":true}");
                    return;
                }

                if (target.StartsWith("/feed", StringComparison.OrdinalIgnoreCase))
                {
                    ApplyFeed(target, body);
                    Log(method, target, "text=" + CaptionFeed.Text.Length +
                                        " sub=" + CaptionFeed.Sub.Length +
                                        (CaptionFeed.Text.Length == 0 ? "  (空)" : "  「" + Clip(CaptionFeed.Text, 40) + "」"));
                    Write(ns, 200, "{\"ok\":true,\"text\":\"" + JsonEscape(CaptionFeed.Text) + "\"}");
                    return;
                }
                if (target.StartsWith("/ping", StringComparison.OrdinalIgnoreCase))
                {
                    Log(method, target, "ping");
                    Write(ns, 200, "{\"ok\":true,\"app\":\"DeadBand\"}");
                    return;
                }
                if (target.StartsWith("/state", StringComparison.OrdinalIgnoreCase))
                {
                    Log(method, target, "state");
                    Write(ns, 200, "{\"text\":\"" + JsonEscape(CaptionFeed.Text) +
                                   "\",\"sub\":\"" + JsonEscape(CaptionFeed.Sub) + "\"}");
                    return;
                }
                Log(method, target, "404");
                Write(ns, 404, "{\"ok\":false}");
            }
        }

        private static void ApplyFeed(string target, string body)
        {
            string query = "";
            int q = target.IndexOf('?');
            if (q >= 0) query = target.Substring(q + 1);

            string text = Param(query, "text");
            string sub = Param(query, "sub");
            string id = Param(query, "id");
            string clear = Param(query, "clear");

            if (clear == "1") { CaptionFeed.Clear(); return; }

            // 没给 text 参数但带了 body：先试 JSON，不行就当纯文本
            if (text == null && body.Length > 0)
            {
                string jt = JsonGet(body, "text");
                if (jt != null)
                {
                    text = jt;
                    string js = JsonGet(body, "sub");
                    if (js != null) sub = js;
                    string ji = JsonGet(body, "id");
                    if (ji != null) id = ji;
                }
                else text = body;
            }

            if (text == null) return;
            CaptionFeed.Set(text, sub == null ? "" : sub, id == null ? "" : id);
        }

        // ---------------- 小工具 ----------------
        /// <summary>
        /// 每条进来的请求都记一行。排查"字幕没显示"时这是唯一能分清
        /// 「脚本没发」还是「发了但没找到字」的证据。
        /// </summary>
        public static void Log(string method, string target, string note)
        {
            try
            {
                string path = System.IO.Path.Combine(ProfileStore.DataDir, "feed-log.txt");
                try
                {
                    var fi = new System.IO.FileInfo(path);
                    if (fi.Exists && fi.Length > 200 * 1024) fi.Delete();
                }
                catch { }

                if (target != null && target.Length > 160) target = target.Substring(0, 160) + "…";
                string line = "at=" + DateTime.Now.ToString("HH:mm:ss.fff") +
                              " " + method + " " + target +
                              " => " + note + Environment.NewLine;
                System.IO.File.AppendAllText(path, line, new UTF8Encoding(false));
            }
            catch { }
        }

        private static int FindHeaderEnd(byte[] buf, int len)
        {
            for (int i = 0; i + 3 < len; i++)
                if (buf[i] == 13 && buf[i + 1] == 10 && buf[i + 2] == 13 && buf[i + 3] == 10)
                    return i;
            return -1;
        }

        /// <summary>取查询参数；没有这个键返回 null（区别"没给"和"给了空串"）。</summary>
        private static string Param(string query, string key)
        {
            if (string.IsNullOrEmpty(query)) return null;
            string[] pairs = query.Split('&');
            for (int i = 0; i < pairs.Length; i++)
            {
                string pr = pairs[i];
                int eq = pr.IndexOf('=');
                string k = eq < 0 ? pr : pr.Substring(0, eq);
                if (!string.Equals(Uri.UnescapeDataString(k.Replace("+", " ")), key, StringComparison.OrdinalIgnoreCase))
                    continue;
                string v = eq < 0 ? "" : pr.Substring(eq + 1);
                return Uri.UnescapeDataString(v.Replace("+", " "));
            }
            return null;
        }

        private static void Write(NetworkStream ns, int code, string json)
        {
            WriteRaw(ns, code, "application/json; charset=utf-8",
                     Encoding.UTF8.GetBytes(json == null ? "" : json));
        }

        private static void WriteRaw(NetworkStream ns, int code, string contentType, byte[] bodyBytes)
        {
            string reason = code == 200 ? "OK"
                          : (code == 204 ? "No Content"
                          : (code == 404 ? "Not Found"
                          : (code == 500 ? "Internal Server Error" : "Bad Request")));

            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(code).Append(' ').Append(reason).Append("\r\n");
            sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
            sb.Append("Access-Control-Allow-Origin: *\r\n");
            sb.Append("Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n");
            sb.Append("Access-Control-Allow-Headers: *\r\n");
            sb.Append("Access-Control-Allow-Private-Network: true\r\n");
            sb.Append("Cache-Control: no-store\r\n");
            sb.Append("Content-Length: ").Append(bodyBytes.Length).Append("\r\n");
            sb.Append("Connection: close\r\n\r\n");

            byte[] headBytes = Encoding.ASCII.GetBytes(sb.ToString());
            ns.Write(headBytes, 0, headBytes.Length);
            if (bodyBytes.Length > 0) ns.Write(bodyBytes, 0, bodyBytes.Length);
            ns.Flush();
        }

        /// <summary>
        /// 把用户脚本发出去，顺手把端口改成真实端口。
        ///
        /// 这样浏览器打开 http://127.0.0.1:端口/userscript.user.js 就直接进
        /// Tampermonkey 的安装页，脚本里的 @updateURL 也永远指向对的端口 ——
        /// 以后改脚本不用再让用户复制粘贴。
        /// </summary>
        private void ServeUserScript(NetworkStream ns)
        {
            string path = UserScriptFile;
            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
            {
                byte[] miss = Encoding.UTF8.GetBytes(
                    "// 找不到 bilibili-subtitle.user.js\n" +
                    "// 应该在 <DeadBand 安装目录>\\tools\\ 或者配置目录\\userscript\\ 下\n");
                WriteRaw(ns, 404, "text/javascript; charset=utf-8", miss);
                return;
            }

            string body;
            try { body = System.IO.File.ReadAllText(path, Encoding.UTF8); }
            catch
            {
                WriteRaw(ns, 500, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("read failed"));
                return;
            }

            body = RewritePort(body);
            Log("GET", "/userscript.user.js", "已发出（端口 " + Port + "）");
            WriteRaw(ns, 200, "text/javascript; charset=utf-8", Encoding.UTF8.GetBytes(body));
        }

        private string RewritePort(string body)
        {
            string origin = "http://127.0.0.1:" + Port + "/userscript.user.js";
            body = Regex.Replace(body, @"const PORT = \d+;", "const PORT = " + Port + ";");
            body = Regex.Replace(body, @"// @(updateURL|downloadURL)\s+\S+", "// @$1 " + origin);
            return body;
        }

        /// <summary>
        /// 接收浏览器侧的 DOM 快照，落到 dom.txt。
        /// 每次都覆盖 —— 要的是"现在长什么样"，不是历史。
        /// </summary>
        private static void SaveDomReport(string body)
        {
            try
            {
                string path = System.IO.Path.Combine(ProfileStore.DataDir, "dom.txt");

                // 追加而不是覆盖：浏览器可能同时开着好几个 B站标签页，
                // 覆盖的话视频页那份会被别的页面冲掉（踩过两次）。
                try
                {
                    var fi = new System.IO.FileInfo(path);
                    if (fi.Exists && fi.Length > 256 * 1024) fi.Delete();
                }
                catch { }

                var sb = new StringBuilder();
                sb.AppendLine("---------- " + DateTime.Now.ToString("HH:mm:ss.fff") + " ----------");
                sb.AppendLine(body);
                System.IO.File.AppendAllText(path, sb.ToString(), new UTF8Encoding(false));
                Log("POST", "/diag", "dom 快照已保存 " + (body == null ? 0 : body.Length) + " 字节");
            }
            catch { }
        }

        private static string Clip(string s, int max)        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        private static string JsonEscape(string s)        {
            if (s == null) return "";
            var sb = new StringBuilder(s.Length + 8);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c < ' ') sb.Append(' ');
                else sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>从扁平 JSON 里抠一个字符串字段（够用就行，不引 JSON 库）。</summary>
        private static string JsonGet(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return null;
            string k = "\"" + key + "\"";
            int i = json.IndexOf(k, StringComparison.Ordinal);
            if (i < 0) return null;
            i += k.Length;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i >= json.Length || json[i] != ':') return null;
            i++;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i >= json.Length || json[i] != '"') return null;
            i++;

            var sb = new StringBuilder();
            while (i < json.Length)
            {
                char c = json[i++];
                if (c == '"') break;
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= json.Length) break;
                char e = json[i++];
                if (e == 'n') sb.Append('\n');
                else if (e == 't') sb.Append('\t');
                else if (e == 'r') sb.Append('\r');
                else if (e == 'u')
                {
                    if (i + 4 <= json.Length)
                    {
                        int code;
                        if (int.TryParse(json.Substring(i, 4), NumberStyles.HexNumber,
                                         CultureInfo.InvariantCulture, out code))
                            sb.Append((char)code);
                        i += 4;
                    }
                }
                else sb.Append(e);
            }
            return sb.ToString();
        }
    }

    // =====================================================================
    //  内容提供者
    // =====================================================================

    /// <summary>
    /// 什么都不画。区带退成一块干净背景板（底色 + 网格，坏线照样被收纳），
    /// 用户自己把第三方桌面歌词拖到这块位置上来。
    ///
    /// 注意：这个 provider 也是唯一一个不抢顶层窗口的内容，
    /// 详见 ZoneHost.ReassertTopmost —— 抢顶层会把人家拖过来的歌词盖住。
    /// </summary>
    internal sealed class EmptyProvider : IProvider
    {
        public string Id { get { return "none"; } }
        public string DisplayName { get { return "空（自己拖桌面歌词进来）"; } }
        public Color Background { get { return Color.Black; } }
        public void Render(Graphics g, int w, int h, ZoneProfile p, double t) { }
    }

    /// <summary>
    /// 字幕 / 歌词：显示浏览器或任何程序推过来的那一行字。
    /// 两种画法：
    ///   pixel  = 按网格栅格化再二值化，方块字，和坏线咬合
    ///   normal = GDI 抗锯齿直接画，清晰但不是像素风
    /// </summary>
    internal sealed class CaptionProvider : IProvider
    {
        private const string UiFamily = "Microsoft YaHei UI";

        public string Id { get { return "caption"; } }
        public string DisplayName { get { return "字幕（浏览器推送）"; } }
        public Color Background { get { return Color.FromArgb(unchecked((int)0xFF05060A)); } }

        // 点阵模式下重排一次不便宜（要测量 + 渲染 + 抠像素），
        // 所以按「文本 + 尺寸 + 参数」缓存，内容没变就直接复用。
        private string cacheKey = "";
        private Bitmap mainBmp;
        private Bitmap subBmp;
        private int mainTop;
        private int subTop;

        public void Render(Graphics g, int w, int h, ZoneProfile p, double t)
        {
            if (w <= 0 || h <= 0) return;

            string text = CaptionFeed.Text;
            string sub = CaptionFeed.Sub;
            if (text.Length == 0 && sub.Length == 0) return;

            Color color = p.CaptionColor;
            int cell = p.GridCell;
            if (cell < 2) cell = 2;

            bool pixelMode = !string.Equals(p.CaptionFont, "normal", StringComparison.OrdinalIgnoreCase);

            if (pixelMode) RenderPixel(g, text, sub, w, h, cell, color);
            else RenderNormal(g, text, sub, w, h, color);
        }

        // ---------------- 正常字体 ----------------
        private void RenderNormal(Graphics g, string text, string sub, int w, int h, Color color)
        {
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            int margin = Math.Max(12, w / 100);
            float maxW = w - margin * 2f;

            if (sub.Length == 0)
            {
                DrawLineFit(g, text, color, 0, h, w, (float)h * 0.62f, maxW, true);
                return;
            }

            int mainH = (int)(h * 0.58);
            DrawLineFit(g, text, color, 0, mainH, w, mainH * 0.68f, maxW, true);
            DrawLineFit(g, sub, color, mainH, h - mainH, w, (h - mainH) * 0.66f, maxW, true);
        }

        private void DrawLineFit(Graphics g, string s, Color color, int bandTop, int bandH,
                                 int w, float startPx, float maxW, bool shadow)
        {
            if (string.IsNullOrEmpty(s) || bandH <= 0) return;

            float px = startPx;
            Font font = null;
            SizeF sz = SizeF.Empty;
            while (px > 7f)
            {
                if (font != null) font.Dispose();
                font = new Font(UiFamily, px, FontStyle.Regular, GraphicsUnit.Pixel);
                sz = g.MeasureString(s, font, int.MaxValue, StringFormat.GenericTypographic);
                if (sz.Width <= maxW) break;
                px -= Math.Max(1f, px * 0.07f);
            }
            if (font == null) return;

            try
            {
                float x = (w - sz.Width) / 2f;
                float y = bandTop + (bandH - sz.Height) / 2f;

                if (shadow)
                {
                    using (var sh = new SolidBrush(Color.FromArgb(170, 0, 0, 0)))
                        g.DrawString(s, font, sh, x + 1f, y + 2f, StringFormat.GenericTypographic);
                }
                using (var br = new SolidBrush(color))
                    g.DrawString(s, font, br, x, y, StringFormat.GenericTypographic);
            }
            finally { font.Dispose(); }
        }

        // ---------------- 像素字体 ----------------
        private void RenderPixel(Graphics g, string text, string sub, int w, int h, int cell, Color color)
        {
            string key = text + "|" + sub + "|" + w + "|" + h + "|" + cell + "|" + color.ToArgb();
            if (key != cacheKey)
            {
                cacheKey = key;
                Rebuild(text, sub, w, h, cell, color);
            }
            if (mainBmp == null && subBmp == null) return;

            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.SmoothingMode = SmoothingMode.None;

            if (mainBmp != null)
            {
                int dx = (w - mainBmp.Width * cell) / 2;
                g.DrawImage(mainBmp, new Rectangle(dx, mainTop, mainBmp.Width * cell, mainBmp.Height * cell));
            }
            if (subBmp != null)
            {
                int dx = (w - subBmp.Width * cell) / 2;
                g.DrawImage(subBmp, new Rectangle(dx, subTop, subBmp.Width * cell, subBmp.Height * cell));
            }
        }

        private void Rebuild(string text, string sub, int w, int h, int cell, Color color)
        {
            if (mainBmp != null) { mainBmp.Dispose(); mainBmp = null; }
            if (subBmp != null) { subBmp.Dispose(); subBmp = null; }

            int totalRows = h / cell;                 // 99/4 = 24 格
            if (totalRows < 8) totalRows = 8;
            int maxCols = (w - 2 * cell) / cell;

            int mainRows, subRows;
            if (sub.Length == 0)
            {
                mainRows = totalRows;
                subRows = 0;
            }
            else
            {
                mainRows = (int)(totalRows * 0.62);
                subRows = totalRows - mainRows - 1;
            }

            int usedMain = 0, usedSub = 0;
            mainBmp = BuildCellLine(text, mainRows, maxCols, cell, color, out usedMain);
            if (subRows >= 5) subBmp = BuildCellLine(sub, subRows, maxCols, cell, color, out usedSub);

            int mainH = mainBmp != null ? mainBmp.Height * cell : 0;
            int subH = subBmp != null ? subBmp.Height * cell : 0;

            if (subBmp == null)
            {
                mainTop = (h - mainH) / 2;
            }
            else
            {
                int gap = cell;
                int block = mainH + gap + subH;
                int top = (h - block) / 2;
                if (top < 0) top = 0;
                mainTop = top;
                subTop = top + mainH + gap;
            }
            if (subBmp != null && mainBmp == null) subTop = (h - subH) / 2;
        }

        /// <summary>
        /// 把一行字栅格化成"格子分辨率"的位图：宽高都以网格单元为单位，
        /// 点亮 = 该填色，透明 = 留空。之后整块按 cell 最近邻放大就是方块字。
        ///
        /// 注意这里 rows 是**墨迹高度预算**（不是字号）：字号从大往小试，
        /// 取第一个既塞得进宽度又不超过高度预算的。直接拿字号当预算会白白矮一截，
        /// 因为汉字的 ink 高度只有 em 的 ~0.78。
        /// </summary>
        private static Bitmap BuildCellLine(string s, int budgetRows, int maxCols, int cell,
                                            Color color, out int usedCols)
        {
            usedCols = 0;
            if (string.IsNullOrEmpty(s) || budgetRows < 5 || maxCols < 4) return null;

            float em = budgetRows * 1.35f;
            for (int guard = 0; guard < 40 && em >= 5f; guard++)
            {
                using (var f = new Font(UiFamily, em, FontStyle.Regular, GraphicsUnit.Pixel))
                using (var probe = new Bitmap(4, 4, PixelFormat.Format32bppArgb))
                using (var pg = Graphics.FromImage(probe))
                {
                    pg.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
                    SizeF sz = pg.MeasureString(s, f, int.MaxValue, StringFormat.GenericTypographic);
                    int needCols = (int)Math.Ceiling(sz.Width);

                    if (needCols > maxCols || needCols < 1)
                    {
                        em *= 0.90f;
                        continue;
                    }

                    int canvasH = (int)Math.Ceiling(em * 1.9f) + 4;
                    Bitmap bmp = Rasterize(s, f, canvasH, needCols + 4, color);
                    if (bmp == null) { em *= 0.90f; continue; }
                    if (bmp.Height > budgetRows || bmp.Width > maxCols)
                    {
                        bmp.Dispose();
                        em *= 0.90f;
                        continue;
                    }
                    usedCols = bmp.Width;
                    return bmp;
                }
            }
            return null;
        }

        private static Bitmap Rasterize(string s, Font f, int canvasH, int cols, Color color)
        {
            Bitmap mask = null;
            try
            {
                mask = new Bitmap(cols, canvasH, PixelFormat.Format32bppArgb);
                using (var mg = Graphics.FromImage(mask))
                {
                    mg.Clear(Color.Black);
                    mg.SmoothingMode = SmoothingMode.None;
                    mg.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
                    using (var br = new SolidBrush(Color.White))
                        mg.DrawString(s, f, br, 0f, 0f, StringFormat.GenericTypographic);
                }

                // 抠出真正有墨迹的范围：测量出来的宽高含留白，
                // 直接放大上下左右都会偏，所以裁到墨迹再居中。
                int minX = cols, minY = canvasH, maxX = -1, maxY = -1;
                for (int y = 0; y < canvasH; y++)
                {
                    for (int x = 0; x < cols; x++)
                    {
                        Color c = mask.GetPixel(x, y);
                        if (c.R + c.G + c.B < 300) continue;
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
                if (maxX < minX || maxY < minY) return null;

                int bw = maxX - minX + 1;
                int bh = maxY - minY + 1;

                var outBmp = new Bitmap(bw, bh, PixelFormat.Format32bppArgb);
                for (int y = 0; y < bh; y++)
                {
                    for (int x = 0; x < bw; x++)
                    {
                        Color c = mask.GetPixel(minX + x, minY + y);
                        if (c.R + c.G + c.B < 300) continue;
                        outBmp.SetPixel(x, y, color);
                    }
                }
                return outBmp;
            }
            catch { return null; }
            finally { if (mask != null) mask.Dispose(); }
        }
    }
}
