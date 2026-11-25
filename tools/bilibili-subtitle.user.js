// ==UserScript==
// @name         DeadBand 字幕桥 · 哔哩哔哩
// @namespace    deadband.local
// @version      2.3.0
// @description  把 B 站播放器当前这一句字幕，推到 DeadBand 的底部区带；顺手把播放器自带的那条藏掉，免得重复显示
// @author       DeadBand
// @match        https://www.bilibili.com/*
// @match        https://player.bilibili.com/*
// @grant        GM_xmlhttpRequest
// @grant        unsafeWindow
// @connect      127.0.0.1
// @connect      api.bilibili.com
// @connect      *.hdslb.com
// @connect      *.bilibili.com
// @updateURL    http://127.0.0.1:41723/userscript.user.js
// @downloadURL  http://127.0.0.1:41723/userscript.user.js
// @run-at       document-start
// ==/UserScript==

/*
 * 安装 / 更新
 *   不要复制粘贴。DeadBand 托盘菜单 →「字幕」→「在浏览器里安装/更新脚本」，
 *   或者直接在浏览器打开 http://127.0.0.1:41723/userscript.user.js
 *
 *   装好之后，以后 DeadBand 这边改了脚本，Tampermonkey 面板里点「检查更新」就能拿到，
 *   不用再手动粘代码。
 *
 * 用法
 *   1. DeadBand 托盘菜单 →「内容」→「字幕（浏览器推送）」
 *   2. 装好本脚本，打开任意 B 站视频
 *   3. **在播放器里把字幕打开**（右下角字幕按钮）。没开字幕 = 页面上没有字幕文本 = 没有东西可推。
 *
 * 隐藏播放器自带字幕
 *   默认开着 —— 否则同一句话会在画面上和底部区带里各显示一次。
 *   藏法是给字幕元素加 opacity: 0（不是 display: none）：
 *   文字仍然留在渲染树里，脚本照样读得到。
 *   开关：DeadBand 托盘菜单 →「字幕」→「隐藏播放器自带字幕」（脚本 5 秒内跟上）。
 *
 * 排查
 *   接收端日志：%LOCALAPPDATA%\DeadBand\feed-log.txt
 *   - 只有 ping / hb   → 脚本活着，但没在页面上找到字幕元素（选择器问题）
 *   - 完全没有记录     → 脚本没跑，或者请求没发出去（Tampermonkey 没装好 / 没授权 @connect）
 *   - 有 feed 但 text=0 → 找到了元素但是空的
 *
 *   浏览器控制台（F12）里能看到 [DeadBand] 开头的日志。
 */

(function () {
    'use strict';

    // ==== 端口：如果 DeadBand 换了端口，改这里 ====
    const PORT = 41723;

    const BASE = 'http://127.0.0.1:' + PORT;
    const FEED = BASE + '/feed';
    const PING = BASE + '/ping';
    const CONFIG = BASE + '/config';

    const POLL_MS = 120;        // 轮询间隔
    const CLEAR_DELAY_MS = 700;      // 页面可见时，字幕消失多久才清空（防句间空隙闪一下）
    const HIDDEN_HOLD_MS = 120000;   // 页面不可见时最多保持多久，防止视频早停了一直挂着
    const HEARTBEAT_MS = 15000; // 心跳：证明脚本还活着
    const CONFIG_MS = 5000;     // 多久去问一次"要不要藏自带字幕"
    const DEBUG = true;

    // 拿到 DeadBand 的配置之前先按默认走（和 DeadBand 的默认值一致）
    let hideSubtitle = true;

    // 字幕容器类名里通常含这些词。不依赖完整类名，B 站改版也不容易废。
    const CANDIDATE_SEL = '[class*="subtitle" i],[class*="caption" i]';
    const PLAYER_ROOTS = [
        '.bpx-player-container',
        '#bilibili-player',
        '.bilibili-player-video',
        '.bpx-player-primary-area',
    ];

    // 播放器 UI 上的字，不是字幕
    const UI_WORDS = /^(字幕|字幕设置|关闭字幕|开启字幕|选择字幕|中文|英文|双语|AI\s*字幕|自动生成|简体中文|繁體中文|English|CC)$/i;

    const STYLE_ID = 'deadband-hide-subtitle';
    const HIDE_ATTR = 'data-deadband-hide';

    // 关掉过渡和动画：B站的字幕常常带淡入，只设 opacity:0 的话
    // 它会从 1 渐变到 0，看起来还是"闪一下"。
    const HIDE_DECL = '{ opacity: 0 !important;' +
                      ' transition: none !important;' +
                      ' animation: none !important; }';

    // 已经学到并写进样式表的类名
    const learnedClasses = {};
    let styleEl = null;

    let lastSent = null;
    let emptySince = 0;
    let lastGoodAt = 0;
    let lastProbe = 0;
    let lastHeartbeat = 0;
    let reportedEmpty = false;
    let lastSource = '';
    let lastUsedClass = '';
    let lastHeal = 0;

    function learnedReport() {
        const parts = [];
        for (const k in learnedClasses) parts.push(k + '=' + learnedClasses[k]);
        return parts.slice(0, 12);
    }

    function currentRules() {
        try {
            const s = styleEl && styleEl.sheet;
            if (!s) return [hidingOff()];
            const out = [];
            for (let i = 0; i < s.cssRules.length && i < 12; i++) {
                out.push(s.cssRules[i].cssText.replace(/\s+/g, ' ').slice(0, 80));
            }
            return out;
        } catch (e) { return ['?']; }
    }

    function hidingOff() { return hideSubtitle ? 'style-missing' : 'hide-off'; }

    function log() {
        if (!DEBUG) return;
        try {
            const args = Array.prototype.slice.call(arguments);
            args.unshift('[DeadBand]');
            console.log.apply(console, args);
        } catch (e) { /* 忽略 */ }
    }

    function get(url, onload) {
        GM_xmlhttpRequest({
            method: 'GET',
            url: url,
            timeout: 2500,
            onload: onload,
            onerror: function () { log('请求失败（DeadBand 没开？端口不对？）', url); }
        });
    }

    // ---------------- 藏掉播放器自带的字幕 ----------------
    // 关键教训：选择器**绝不能**只按 "class 里同时含 subtitle 和 text" 去匹配，
    // 那会把字幕设置面板里的语言选项一起盖掉，用户就没法开关字幕了。
    //
    // 现在只认两种：
    //   1. 已知的"叠在画面上的那一行"的类名
    //   2. 我们扫出来、并且不在任何菜单/弹层容器里的元素（打 data 属性）
    const HIDE_CSS =
        '.bpx-player-subtitle-panel-text,' +
        '.bili-subtitle-x-subtitle-panel-text,' +
        '.bpx-player-subtitle-inner .bpx-player-subtitle-item,' +
        '[' + HIDE_ATTR + '] ' + HIDE_DECL;

    // 这些容器里的东西是播放器 UI（设置面板、下拉菜单、**控制栏按钮**），一律不读也不藏。
    // "ctrl" 这条是血的教训：B站的「字幕」按钮类名是 bpx-player-ctrl-subtitle，
    // 含 subtitle，不排除的话会被当成字幕文字一起藏掉，用户就没法开关字幕了。
    const UI_CONTAINER_SEL =
        '[class*="ctrl" i],[class*="options" i],[class*="menu" i],[class*="popup" i],' +
        '[class*="dropdown" i],[class*="tooltip" i],[class*="panel-options" i]';

    function isUiElement(el) {
        try { return !!(el && el.closest && el.closest(UI_CONTAINER_SEL)); }
        catch (e) { return false; }
    }

    // ---------------- 几何判据 ----------------
    // 光看类名不够：页面上可能有别的元素也"长得像"字幕，
    // 结果在字幕空隙里冒出「名字姓名昵称」这种页面文字。
    // 真正的字幕一定叠在 <video> 上面，所以要求元素中心点落在视频矩形里。
    function videoBox() {
        try {
            const v = document.querySelector('video');
            if (!v) return null;
            const r = v.getBoundingClientRect();
            if (r.width < 64 || r.height < 64) return null;
            return r;
        } catch (e) { return null; }
    }

    /// 页面不可见时布局不可靠（rect 可能是 0），这时跳过几何判断。
    function onVideoOrHidden(el, vr) {
        try { if (document.hidden) return true; } catch (e) { }
        return onVideo(el, vr);
    }

    function onVideo(el, vr) {
        if (!vr) return true;              // 找不到 video（比如 iframe 播放器）就不做这个判断
        try {
            const r = el.getBoundingClientRect();
            if (r.width <= 0 || r.height <= 0) return false;
            const cx = r.left + r.width / 2;
            const cy = r.top + r.height / 2;
            return cx >= vr.left && cx <= vr.right && cy >= vr.top && cy <= vr.bottom;
        } catch (e) { return false; }
    }

    function ensureStyle() {
        if (styleEl && styleEl.parentNode) return styleEl;
        styleEl = document.createElement('style');
        styleEl.id = STYLE_ID;
        styleEl.textContent = HIDE_CSS;
        (document.head || document.documentElement).appendChild(styleEl);
        return styleEl;
    }

    function applyHideStyle() { ensureStyle(); }

    function dropHideStyle() {
        const s = document.getElementById(STYLE_ID);
        if (s && s.parentNode) s.parentNode.removeChild(s);
        styleEl = null;
        for (const k in learnedClasses) delete learnedClasses[k];
        const marked = document.querySelectorAll('[' + HIDE_ATTR + ']');
        for (let i = 0; i < marked.length; i++) marked[i].removeAttribute(HIDE_ATTR);
    }

    /**
     * 学到真字幕的类名，立刻写一条 CSS 规则进样式表。
     *
     * 这一步是消掉"每句闪一下"的关键：
     * 靠 120ms 轮询打 data 属性，新一句字出现的头 120ms 里还是可见的；
     * 一旦类名进了样式表，浏览器在**绘制前**就把它盖住了，跟轮询时序无关。
     *
     * 除了元素自己，还往上学几层祖先：
     * 如果每句字幕的元素都是新建的，只学它本身，规则会随着旧元素一起失效；
     * 而容器通常是复用的，把容器藏掉才是根治。
     * 祖先必须"不含任何 UI 容器"才学 —— 否则又会把设置面板连带盖掉。
     */
    function learnClass(el) {
        if (!el || el.nodeType !== 1) return;

        let cur = el;
        for (let depth = 0; depth < 5 && cur && cur.nodeType === 1; depth++) {
            if (depth > 0) {
                let hasUi = false;
                try { hasUi = !!cur.querySelector(UI_CONTAINER_SEL); } catch (e) { hasUi = true; }
                if (hasUi) break;      // 再往上就走宽了，停
            }
            learnFromNode(cur);
            cur = cur.parentElement;
        }
    }

    function learnFromNode(el) {
        if (!el || !el.classList || !el.classList.length) return;
        let sheet = null;
        try {
            const s = ensureStyle();
            sheet = s.sheet;
        } catch (e) { return; }
        if (!sheet) return;

        for (let i = 0; i < el.classList.length; i++) {
            const c = el.classList[i];
            if (!/subtitle|caption/i.test(c)) continue;
            if (learnedClasses[c]) continue;

            // 这个类名如果也出现在菜单/弹层里，就绝不能学 ——
            // 会重演"设置面板被盖掉、没法开关字幕"的事故。
            let conflict = false;
            try {
                const same = document.querySelectorAll('.' + CSS.escape(c));
                for (let k = 0; k < same.length; k++) {
                    if (isUiElement(same[k])) { conflict = true; break; }
                }
            } catch (e) { conflict = true; }

            if (conflict) { learnedClasses[c] = 'skip:conflict'; continue; }

            try {
                sheet.insertRule('.' + CSS.escape(c) + ' ' + HIDE_DECL, sheet.cssRules.length);
                learnedClasses[c] = 'ok';
                log('学到字幕类名：.' + c);
            } catch (e) {
                learnedClasses[c] = 'skip:' + (e && e.name ? e.name : 'error');
            }
        }
    }

    function markElements(els) {
        if (!els) return;
        for (let i = 0; i < els.length; i++) {
            // 只有"叠在画面上的纯字块"才藏；按钮、菜单、弹层留着，否则用户没法开关字幕
            if (isUiElement(els[i])) continue;
            if (!isPlainText(els[i])) continue;
            if (!hideSubtitle) continue;
            learnClass(els[i]);
            try { els[i].setAttribute(HIDE_ATTR, '1'); } catch (e) { /* 忽略 */ }
        }
    }

    // ---------------- 即时隐藏（关键） ----------------
    // 轮询是 120ms 一次，新插入的元素在这之前是可见的 —— 这就是"闪一下"。
    // MutationObserver 的回调在**浏览器下一次绘制之前**就跑完，
    // 所以在这里打标记，元素根本没机会被画出来。
    function hideJustAdded(n) {
        if (!hideSubtitle || !n || n.nodeType !== 1) return;

        const list = [];
        try {
            if (n.matches && n.matches(CANDIDATE_SEL)) list.push(n);
            const inner = n.querySelectorAll ? n.querySelectorAll(CANDIDATE_SEL) : null;
            if (inner) {
                for (let i = 0; i < inner.length; i++) list.push(inner[i]);
            }
        } catch (e) { return; }

        for (let i = 0; i < list.length; i++) {
            const el = list[i];
            if (isUiElement(el)) continue;            // 控制栏按钮、菜单、弹层绝不动
            if (!isPlainText(el)) continue;           // 可交互元素绝不动
            try { el.setAttribute(HIDE_ATTR, '1'); } catch (e) { }
        }
    }

    /** 只有"纯文字块"才可能是字幕。按钮、链接、可点元素一律排除。 */
    function isPlainText(el) {
        try {
            if (el.tagName === 'BUTTON' || el.tagName === 'A' || el.tagName === 'INPUT') return false;
            if (el.hasAttribute && el.hasAttribute('role')) {
                const role = String(el.getAttribute('role')).toLowerCase();
                if (role === 'button' || role === 'link' || role === 'menuitem') return false;
            }
            if (el.querySelector && el.querySelector('button,a,input,[role="button"]')) return false;
            return true;
        } catch (e) { return false; }
    }

    /**
     * 自愈：把误标在 UI 元素上的隐藏标记摘掉。
     * 万一某个版本的类名又超出了预期，最多两秒它自己会恢复可见。
     */
    function healUiAttrs() {
        try {
            const els = document.querySelectorAll('[' + HIDE_ATTR + ']');
            for (let i = 0; i < els.length; i++) {
                if (isUiElement(els[i]) || !isPlainText(els[i])) {
                    els[i].removeAttribute(HIDE_ATTR);
                }
            }
        } catch (e) { }
    }

    function startObserver() {
        if (typeof MutationObserver !== 'function') return;
        try {
            const obs = new MutationObserver(function (records) {
                if (!hideSubtitle) return;
                for (let i = 0; i < records.length; i++) {
                    const added = records[i].addedNodes;
                    if (!added || added.length === 0) continue;
                    if (added.length > 200) continue;   // 页面在干大事，别添乱
                    for (let k = 0; k < added.length; k++) hideJustAdded(added[k]);
                }
            });
            obs.observe(document.documentElement, { childList: true, subtree: true });
        } catch (e) { log('MutationObserver 启动失败', e); }
    }

    function setHide(on) {
        hideSubtitle = !!on;
        if (hideSubtitle) applyHideStyle();
        else dropHideStyle();
        log('隐藏播放器字幕 =', hideSubtitle);
    }

    function markElements(els) {
        if (!hideSubtitle || !els) return;
        for (let i = 0; i < els.length; i++) {
            // 只有"叠在画面上的字"才藏；菜单里的选项留着，否则用户没法开关字幕
            if (isUiElement(els[i])) continue;
            try { els[i].setAttribute(HIDE_ATTR, '1'); } catch (e) { /* 忽略 */ }
        }
    }

    // ---------------- DOM 结构上报 ----------------
    // 找不到字幕时把候选元素清单发给 DeadBand，落到 dom.txt。
    // 这样对真实类名不用让用户去 F12 抄。
    let lastDomReport = 0;

    function reportDom(reason) {
        const now = Date.now();
        if (now - lastDomReport < 12000) return;
        lastDomReport = now;

        try {
            const vr = videoBox();
            const seen = {};
            const rows = [];
            let nodes;
            try { nodes = document.querySelectorAll(CANDIDATE_SEL); }
            catch (e) { nodes = document.querySelectorAll('[class*="subtitle"]'); }

            // 没有 video 又没有候选元素的页面（比如 B站的那些内部跳转页），
            // 报上去只会把视频页的报告冲掉，直接闭嘴。
            if (!vr && nodes.length === 0) return;

            for (let i = 0; i < nodes.length && rows.length < 60; i++) {
                const el = nodes[i];
                const cls = String(el.className || '').replace(/\s+/g, ' ').trim();
                const key = cls + '#' + el.tagName;
                if (seen[key]) continue;
                seen[key] = 1;

                const txt = (el.innerText || el.textContent || '').replace(/\s+/g, ' ').trim();
                let rr = { left: 0, top: 0, width: 0, height: 0 };
                try { rr = el.getBoundingClientRect(); } catch (e) { }

                rows.push(cls + ' || ' + el.tagName +
                          ' || len=' + txt.length +
                          ' || rect=' + Math.round(rr.left) + ',' + Math.round(rr.top) + ',' +
                                        Math.round(rr.width) + 'x' + Math.round(rr.height) +
                          ' || vid=' + (onVideo(el, vr) ? 1 : 0) +
                          ' || ui=' + (isUiElement(el) ? 1 : 0) +
                          ' || ' + txt.slice(0, 30));
            }

            const payload = JSON.stringify({
                reason: reason,
                url: location.href.slice(0, 120),
                page: location.pathname.slice(0, 60),
                video: vr ? (Math.round(vr.left) + ',' + Math.round(vr.top) + ',' +
                             Math.round(vr.width) + 'x' + Math.round(vr.height)) : 'none',
                hide: hideSubtitle,
                src: lastSource,
                cue: cueStatus,
                xhr: xhrSeen,
                used: lastUsedClass,
                learned: learnedReport(),
                rules: currentRules(),
                count: nodes.length,
                leaves: rows
            });

            GM_xmlhttpRequest({
                method: 'POST',
                url: BASE + '/diag',
                headers: { 'Content-Type': 'application/json' },
                data: payload,
                timeout: 3000,
                onload: function () { log('已上报 DOM 快照', rows.length, '条'); }
            });
        } catch (e) { log('DOM 上报失败', e); }
    }

    // ---------------- 找字幕文本 ----------------
    function isLikelyUiText(t) {
        if (!t) return true;
        if (t.length > 300) return true;
        if (UI_WORDS.test(t.trim())) return true;
        return false;
    }

    /** 已知类名快速路径 */
    function readBySelector(vr) {
        const sels = [
            '.bpx-player-subtitle-panel-text',
            '.bpx-player-subtitle-panel .bpx-player-subtitle-panel-text',
        ];
        for (let i = 0; i < sels.length; i++) {
            const el = document.querySelector(sels[i]);
            if (!el) continue;
            // 这里是已知的精确类名，不再用 UI 启发式判断（避免被 ctrl 之类的排除规则误伤）
            if (!onVideoOrHidden(el, vr)) continue;
            const t = (el.innerText || el.textContent || '').trim();
            if (t) return { text: t, els: [el] };
        }
        return null;
    }

    /** 播放器对象兜底 */
    function readByPlayerApi() {
        try {
            const p = window.player;
            if (p && typeof p.getSubtitle === 'function') {
                const s = p.getSubtitle();
                if (typeof s === 'string' && s.trim()) return s;
            }
        } catch (e) { /* 播放器还没准备好 */ }
        return '';
    }

    /**
     * 不写死类名的兜底：凡是 class 里带 subtitle/caption 的元素都算候选，
     * 只取"内部没有更深的同类非空元素"的叶子，按出现顺序返回。
     * 原文和译文通常就是两个叶子。
     */
    function leafElements(root, vr) {
        let nodes;
        try {
            nodes = (root || document).querySelectorAll(CANDIDATE_SEL);
        } catch (e) {
            nodes = (root || document).querySelectorAll('[class*="subtitle"]');
        }

        const out = [];
        const seen = {};
        for (let i = 0; i < nodes.length; i++) {
            const el = nodes[i];
            if (isUiElement(el)) continue;      // 设置面板/菜单里的字不是字幕
            if (!onVideoOrHidden(el, vr)) continue;     // 不在视频画面上的，不是字幕
            const txt = (el.innerText || el.textContent || '').replace(/\s+/g, ' ').trim();
            if (!txt || isLikelyUiText(txt)) continue;

            // 有更深的同类非空元素 → 这是容器，跳过
            let hasDeeper = false;
            const kids = el.querySelectorAll(CANDIDATE_SEL);
            for (let k = 0; k < kids.length; k++) {
                if ((kids[k].innerText || '').trim()) { hasDeeper = true; break; }
            }
            if (hasDeeper) continue;

            if (seen[txt]) continue;
            seen[txt] = 1;
            out.push({ el: el, text: txt });
        }
        return out;
    }

    // ---------------- 字幕轨（时间轴驱动） ----------------
    // 后台时 B站 播放器会停止更新字幕元素（rAF 被浏览器暂停），
    // 但 video.currentTime 还在走 —— 所以把整条字幕轨拿下来自己算当前句，
    // 这样最小化/切走都不影响。
    let cues = [];          // [{from, to, content}]
    let cuesKey = '';
    let cueStatus = 'idle';
    let lastLoadTry = 0;

    // ---------------- 抓包：直接截播放器自己拉的字幕 JSON ----------------
    // 跨域直连 CDN 会被拦（cue-net），但播放器自己拉是成功的 ——
    // 拦它自己的请求，比我们绕过去稳得多。
    (function hookSubtitleNetwork() {
        var W = (typeof unsafeWindow !== 'undefined') ? unsafeWindow : window;

        function accept(url, text) {
            try {
                if (!text || text.length < 40) return;
                if (text.indexOf('"content"') < 0) return;   // 便宜的先排除
                var j = JSON.parse(text);
                if (j && j.body && j.body.length && j.body[0] && j.body[0].content !== undefined) {
                    cues = j.body;
                    cueStatus = 'sniff:' + cues.length;
                    log('截获字幕轨：' + cues.length + ' 句');
                }
            } catch (e) { }
        }

        try {
            var XO = W.XMLHttpRequest.prototype.open;
            var XS = W.XMLHttpRequest.prototype.send;
            W.XMLHttpRequest.prototype.open = function (m, u) {
                this.__dbUrl = u;
                return XO.apply(this, arguments);
            };
            W.XMLHttpRequest.prototype.send = function () {
                var self = this;
                self.addEventListener('load', function () {
                    xhrSeen++;
                    try {
                        var rt = self.responseType;
                        var txt = null;
                        if (rt === '' || rt === 'text' || rt === undefined) txt = self.responseText;
                        else if (rt === 'json') { try { txt = JSON.stringify(self.response); } catch (e) { } }
                        if (txt) accept(String(self.__dbUrl || ''), txt);
                    } catch (e) { }
                });
                return XS.apply(self, arguments);
            };
        } catch (e) { }

        try {
            var of = W.fetch;
            if (typeof of === 'function') {
                W.fetch = function () {
                    var args = arguments;
                    var url = (typeof args[0] === 'string') ? args[0] : (args[0] && args[0].url);
                    return of.apply(W, args).then(function (res) {
                        try {
                            if (/subtitle/i.test(String(url || ''))) {
                                res.clone().text().then(function (t) { accept(String(url || ''), t); });
                            }
                        } catch (e) { }
                        return res;
                    });
                };
            }
        } catch (e) { }
    })();

    let pendingCid = 0;
    let xhrSeen = 0;

    function bvidFromUrl() {
        const m = location.pathname.match(/\/(BV[0-9A-Za-z]{10})/);
        return m ? m[1] : '';
    }

    function videoMeta() {
        try {
            const s = window.__INITIAL_STATE__;
            if (s) {
                const aid = s.aid || (s.videoData && s.videoData.aid);
                const cid = s.cid || (s.videoData && s.videoData.cid);
                const bvid = s.bvid || (s.videoData && s.videoData.bvid);
                if (cid) return { aid: aid, cid: cid, bvid: bvid || '' };
            }
        } catch (e) { }
        try {
            const pl = window.player;
            if (pl && typeof pl.getVideoMessage === 'function') {
                const m = pl.getVideoMessage();
                if (m && m.cid) return { aid: m.aid, cid: m.cid, bvid: m.bvid || '' };
            }
        } catch (e) { }
        if (pendingCid) return { aid: 0, cid: pendingCid, bvid: bvidFromUrl() };
        return null;
    }

    /** 播放器里当前选中的字幕语言（"中文" / "English" …） */
    function activeLangLabel() {
        try {
            const el = document.querySelector('.bpx-player-ctrl-subtitle-language-item.bpx-state-active');
            if (el) {
                const t = (el.innerText || el.textContent || '').trim();
                if (t) return t;
            }
        } catch (e) { }
        return '';
    }

    /// 失败一律清掉 cuesKey，否则 key 相同就永远不再重试（这是个 bug，踩过）
    function cueFail(reason) {
        cueStatus = reason;
        cuesKey = '';
        cues = [];
        log('字幕轨失败：' + reason);
    }

    function loadCues() {
        const meta = videoMeta();
        if (!meta || !meta.cid) {
            // 拿不到 cid：用 bvid 去换（曲线救场）
            const bv = bvidFromUrl();
            if (!bv) { cueStatus = 'no-meta'; return; }
            const pkey = 'pl|' + bv;
            if (cuesKey === pkey) return;
            cuesKey = pkey;
            GM_xmlhttpRequest({
                method: 'GET',
                url: 'https://api.bilibili.com/x/player/pagelist?bvid=' + bv,
                timeout: 8000,
                onload: function (r) {
                    try {
                        const j = JSON.parse(r.responseText);
                        const first = j && j.data && j.data[0];
                        if (!first || !first.cid) { cueFail('pagelist-empty'); return; }
                        pendingCid = first.cid;
                        cuesKey = '';
                        cueStatus = 'pagelist-ok';
                    } catch (e) { cueFail('pagelist-parse'); }
                },
                onerror: function () { cueFail('pagelist-net'); }
            });
            return;
        }

        const key = meta.cid + '|' + activeLangLabel();
        if (key === cuesKey) return;
        if (Date.now() - lastLoadTry < 8000) return;   // 失败时别死循环
        lastLoadTry = Date.now();
        cuesKey = key;

        let api = 'https://api.bilibili.com/x/player/v2?cid=' + meta.cid;
        if (meta.aid) api += '&aid=' + meta.aid;
        if (meta.bvid) api += '&bvid=' + meta.bvid;

        GM_xmlhttpRequest({
            method: 'GET',
            url: api,
            timeout: 8000,
            onload: function (r) {
                try {
                    const j = JSON.parse(r.responseText);
                    const subs = (j && j.data && j.data.subtitle && j.data.subtitle.subtitles) || [];
                    if (!subs.length) { cueFail('no-subs'); return; }

                    // 列表里有些条目是「暂无字幕 / 登录可享」，subtitle_url 是空的，先滤掉
                    let usable = [];
                    for (let si = 0; si < subs.length; si++) {
                        if (subs[si] && subs[si].subtitle_url) usable.push(subs[si]);
                    }
                    if (!usable.length) { cueFail('no-url'); return; }

                    let pick = usable[0];
                    const label = activeLangLabel();
                    if (label) {
                        for (let i = 0; i < usable.length; i++) {
                            if (usable[i].lan_doc === label) { pick = usable[i]; break; }
                        }
                    }

                    let u = pick.subtitle_url || '';
                    if (u.indexOf('//') === 0) u = 'https:' + u;
                    if (!u) { cueFail('no-url'); return; }

                    // GM_xmlhttpRequest 走的是脚本沙箱，CDN 会拒（cue-net）。
                    // 改用页面自己的 fetch —— 它和播放器同处一个 CORS 上下文，
                    // 播放器能拿到，它就能拿到。
                    (function (target) {
                        var W = (typeof unsafeWindow !== 'undefined') ? unsafeWindow : window;
                        if (typeof W.fetch !== 'function') { cueFail('no-fetch'); return; }
                        W.fetch(target, { credentials: 'omit' })
                            .then(function (res) {
                                if (!res || !res.ok) { cueFail('fetch-http'); return null; }
                                return res.text();
                            })
                            .then(function (t) {
                                if (t === null) return;
                                try {
                                    const d = JSON.parse(t);
                                    if (d && d.body && d.body.length) {
                                        cues = d.body;
                                        cueStatus = 'fetch:' + cues.length;
                                        log('页面 fetch 拿到字幕轨：' + cues.length + ' 句');
                                    } else { cueFail('fetch-empty'); }
                                } catch (e) { cueFail('fetch-parse'); }
                            })
                            .catch(function () { cueFail('fetch-err'); });
                    })(u);
                } catch (e) { cueFail('api-parse'); }
            },
            onerror: function () { cueFail('api-net'); }
        });
    }

    /** 二分找当前时间点该显示哪一句 */
    function cueAt(t) {
        let lo = 0, hi = cues.length - 1;
        while (lo <= hi) {
            const mid = (lo + hi) >> 1;
            const c = cues[mid];
            if (t < c.from) hi = mid - 1;
            else if (t >= c.to) lo = mid + 1;
            else return c;
        }
        return null;
    }

    function readFromCue() {
        const vid = document.querySelector('video');
        if (!vid || !cues.length) return null;
        const c = cueAt(vid.currentTime);
        if (!c || !c.content) return { text: '', sub: '', els: [], src: 'cue-gap' };
        const s = String(c.content).replace(/\r/g, '');
        const lines = s.split('\n')
            .map(function (x) { return x.replace(/\s+/g, ' ').trim(); })
            .filter(function (x) { return x.length > 0; });
        return {
            text: lines.length ? lines[0] : '',
            sub: lines.slice(1).join(' '),
            els: [],                       // 不依赖 DOM，也就不需要隐藏
            src: 'cue'
        };
    }

    function readCaption() {
        // 首选：字幕轨 + currentTime。这条路在后台也能继续走。
        const byCue = readFromCue();
        if (byCue) return byCue;

        const vr = videoBox();

        const fast = readBySelector(vr);
        if (fast) {
            const lines = fast.text.split('\n')
                .map(function (s) { return s.replace(/\s+/g, ' ').trim(); })
                .filter(function (s) { return s.length > 0; });
            return {
                text: lines.length ? lines[0] : '',
                sub: lines.slice(1).join(' '),
                els: fast.els,
                src: 'selector',
            };
        }

        const api = readByPlayerApi();
        if (api) {
            const lines = api.split('\n')
                .map(function (s) { return s.replace(/\s+/g, ' ').trim(); })
                .filter(function (s) { return s.length > 0; });
            return { text: lines[0] || '', sub: lines.slice(1).join(' '), els: [], src: 'player-api' };
        }

        // 先在播放器容器里找，找不到再全局找
        let leaves = [];
        for (let i = 0; i < PLAYER_ROOTS.length && leaves.length === 0; i++) {
            const root = document.querySelector(PLAYER_ROOTS[i]);
            if (root) leaves = leafElements(root, vr);
        }
        if (leaves.length === 0) leaves = leafElements(document, vr);

        if (!leaves.length) return { text: '', sub: '', els: [], src: 'none' };

        const els = leaves.map(function (x) { return x.el; });
        const texts = leaves.map(function (x) { return x.text; });
        return { text: texts[0], sub: texts.slice(1).join(' '), els: els, src: 'leaf-scan' };
    }

    // ---------------- 发送 ----------------
    function send(text, sub) {
        const key = text + '\u0001' + sub;
        if (key === lastSent) return;
        lastSent = key;

        log(text ? '字幕 → ' + text + (sub ? ' / ' + sub : '') : '清空');

        get(FEED +
            '?text=' + encodeURIComponent(text) +
            '&sub=' + encodeURIComponent(sub) +
            '&id=' + encodeURIComponent(location.href + '#' + Date.now()));
    }

    // ---------------- 配置轮询 ----------------
    function pollConfig() {
        get(CONFIG, function (r) {
            try {
                const j = JSON.parse(r.responseText);
                if (typeof j.hideSubtitle === 'boolean' && j.hideSubtitle !== hideSubtitle) {
                    setHide(j.hideSubtitle);
                }
            } catch (e) { /* 忽略 */ }
        });
    }

    // ---------------- 启动与主循环 ----------------
    log('脚本已加载', location.href);
    applyHideStyle();          // 默认先藏，避免第一句就重复显示
    startObserver();           // 新插入的字幕元素在下一次绘制前就被藏掉
    pollConfig();
    setInterval(pollConfig, CONFIG_MS);
    get(PING + '?src=bili&start=1');

    setInterval(function () {
        // 心跳：哪怕一直没有字幕，也能在 feed-log 里看到脚本是活的
        const now = Date.now();
        if (now - lastHeartbeat > HEARTBEAT_MS) {
            lastHeartbeat = now;
            get(PING + '?src=bili&hb=1');
        }

        loadCues();          // 首次/换集/换语言时自己会重新拉，内部有节流

        const c = readCaption();

        // 每 12 秒报一次当前页面的候选元素清单，
        // 这样出问题时我看 dom.txt 就知道是哪个元素在捣鬼，不用你去 F12。
        reportDom('periodic');

        // 每秒自愈一次：误标到 UI 上的隐藏标记会被摘掉
        if (now - lastHeal > 1000) {
            lastHeal = now;
            healUiAttrs();
        }

        if (c.text || c.sub) {
            emptySince = 0;
            reportedEmpty = false;
            lastGoodAt = now;
            lastSource = c.src || '';
            lastUsedClass = c.els && c.els.length
                ? String(c.els[0].className || '').replace(/\s+/g, ' ').trim().slice(0, 80)
                : '';
            markElements(c.els);
            send(c.text, c.sub);
            return;
        }

        if (!reportedEmpty) {
            reportedEmpty = true;
            log('当前页面上没找到字幕文本（播放器里字幕开了吗？）');
            reportDom('no-caption');
        }

        // 空档怎么处理：
        //   页面可见 —— 等 CLEAR_DELAY_MS 再清，避免句间空隙闪一下
        //   页面不可见（切到别的窗口/标签，浏览器判定 hidden）——
        //     B站播放器常常在这时把字幕元素拆掉，读了也是空。
        //     这时候清屏就会"字幕消失"，所以保持上一句不动。
        if (!emptySince) emptySince = now;

        // 走字幕轨时读数可靠，不管前后台都按正常节奏清屏
        var reliable = (lastSource === 'cue' || lastSource === 'cue-gap');

        var pageHidden = (typeof document.hidden === 'boolean') ? document.hidden : false;

        // 后台探针：每 5 秒报一次"页面隐藏时视频还在不在走、字幕文本变没变"。
        // 用来判断后台到底是"元素不更新了"还是"元素被拆了"——
        // 两种情况修法完全不同，不能靠猜。
        if (pageHidden && (now - lastProbe) > 5000) {
            lastProbe = now;
            var vid = document.querySelector('video');
            GM_xmlhttpRequest({
                method: 'POST',
                url: BASE + '/diag',
                headers: { 'Content-Type': 'application/json' },
                data: JSON.stringify({
                    reason: 'hidden-probe',
                    hidden: true,
                    t: vid ? Math.round(vid.currentTime * 10) / 10 : -1,
                    paused: vid ? !!vid.paused : null,
                    now: lastSent === null ? '(还没发过)' : (lastSent === '' ? '(已清空)' : lastSent.slice(0, 40)),
                    sinceGood: Math.round((now - lastGoodAt) / 1000),
                    els: document.querySelectorAll(CANDIDATE_SEL).length
                }),
                timeout: 3000
            });
        }

        if (lastSent !== '') {
            if (reliable || !pageHidden) {
                if ((now - emptySince) > CLEAR_DELAY_MS) send('', '');
            } else if ((now - lastGoodAt) > HIDDEN_HOLD_MS) {
                // 长时间挂着不动（比如视频早停了），总算不再显示
                send('', '');
            }
        }
    }, POLL_MS);

    // 切回页面时重置计时，别一回来就立刻清屏
    document.addEventListener('visibilitychange', function () {
        emptySince = 0;
        if (!document.hidden) log('页面重新可见，恢复跟随');
    });
})();
