# -*- coding: utf-8 -*-
"""把 docs/tutorial.md 打成 docs/DeadBand使用教程.pdf。

链路：Markdown → HTML(A4 打印样式) → Edge headless --print-to-pdf。
不用 Word/LaTeX，因为排版完全由 CSS 控制，改一版重跑一次就行。

用法：
    python tools/make-pdf.py

依赖：本机装了 Edge（Win10/11 都有）。图片按 docs/images/ 的相对路径引用。
"""
import html
import os
import re
import subprocess
import sys
import tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "docs", "tutorial.md")
OUT = os.path.join(ROOT, "docs", "DeadBand使用教程.pdf")
EDGE_CANDIDATES = [
    r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
    r"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
]

CSS = """
@page { size: A4; margin: 16mm 17mm 18mm 17mm; }
* { box-sizing: border-box; }
body { margin: 0; font-family: "Microsoft YaHei UI", "Microsoft YaHei", sans-serif;
       font-size: 10.5pt; line-height: 1.78; color: #1E242E; }
p { margin: 0 0 9pt; }
strong { color: #0A0F16; }

/* 封面 */
.cover { height: 258mm; display: flex; flex-direction: column; justify-content: center;
         page-break-after: always; }
.cover .kicker { font-size: 9.5pt; letter-spacing: 3px; color: #00A2C4; font-weight: bold; }
.cover h1 { font-size: 34pt; margin: 6pt 0 4pt; letter-spacing: 1px; }
.cover .sub { font-size: 12.5pt; color: #55606F; margin-bottom: 22pt; }
.cover .hero { border: 1px solid #DDE2E8; border-radius: 6px; width: 100%; }
.cover .meta { margin-top: 22pt; font-size: 9.5pt; color: #7A8595; }

/* 标题 */
h2 { font-size: 16pt; margin: 20pt 0 10pt; padding: 2pt 0 2pt 10pt;
     border-left: 4px solid #00A2C4; page-break-after: avoid; break-after: avoid; }
h2:first-of-type { margin-top: 0; }
h3 { font-size: 11.5pt; margin: 13pt 0 5pt; color: #0A0F16;
     page-break-after: avoid; break-after: avoid; }
h2 + p, h3 + p { margin-top: 0; }
p { orphans: 2; widows: 2; }

/* 图片 */
img { display: block; max-width: 100%; margin: 10pt auto 12pt;
      border: 1px solid #DDE2E8; border-radius: 5px; }
.caption { text-align: center; font-size: 9pt; color: #7A8595; margin: -8pt 0 12pt; }

/* 表格 */
table { width: 100%; border-collapse: collapse; margin: 8pt 0 12pt; font-size: 9.5pt;
        page-break-inside: avoid; }
th, td { border: 1px solid #DDE2E8; padding: 6pt 8pt; text-align: left; vertical-align: top; }
th { background: #F2F4F7; font-weight: bold; }
td code, p code, li code { font-family: Consolas, monospace; font-size: 9.5pt;
        background: #F2F4F7; padding: 1px 4px; border-radius: 3px; }

/* 引用块 */
blockquote { margin: 10pt 0; padding: 8pt 12pt; background: #F5FAFC;
             border-left: 3px solid #00A2C4; color: #33404F; }
blockquote p { margin: 0 0 5pt; }
blockquote p:last-child { margin-bottom: 0; }

/* 代码块 */
pre { background: #0D1016; color: #E6EDF6; padding: 10pt 12pt; border-radius: 5px;
      font-family: Consolas, monospace; font-size: 9pt; line-height: 1.6;
      white-space: pre-wrap; page-break-inside: avoid; }

/* 列表 */
ul, ol { margin: 0 0 9pt; padding-left: 20pt; }
li { margin-bottom: 3pt; }
hr { border: 0; height: 0; }
"""


def inline(text):
    """行内格式：**粗体**、`代码`、[链接](url)。"""
    text = html.escape(text, quote=False)
    text = re.sub(r"`([^`]+)`", r"<code>\1</code>", text)
    text = re.sub(r"\*\*([^*]+)\*\*", r"<strong>\1</strong>", text)
    text = re.sub(r"\[([^\]]+)\]\(([^)]+)\)", r"\1", text)
    return text


def render_table(lines):
    rows = []
    for ln in lines:
        cells = [c.strip() for c in ln.strip().strip("|").split("|")]
        rows.append(cells)
    if len(rows) >= 2 and set(rows[1][0]) <= set("-: "):
        head, body = rows[0], rows[2:]
    else:
        head, body = None, rows
    out = ["<table>"]
    if head:
        out.append("<thead><tr>" + "".join("<th>%s</th>" % inline(c) for c in head) + "</tr></thead>")
    out.append("<tbody>")
    for r in body:
        out.append("<tr>" + "".join("<td>%s</td>" % inline(c) for c in r) + "</tr>")
    out.append("</tbody></table>")
    return "\n".join(out)


def render_body(md):
    lines = md.split("\n")
    out, i = [], 0
    para = []

    def flush():
        if para:
            out.append("<p>%s</p>" % inline(" ".join(para)))
            del para[:]

    while i < len(lines):
        ln = lines[i]
        s = ln.strip()

        if not s:
            flush(); i += 1; continue
        if s == "---":
            flush(); i += 1; continue
        if s.startswith("```"):
            flush()
            code = []
            i += 1
            while i < len(lines) and not lines[i].strip().startswith("```"):
                code.append(lines[i]); i += 1
            i += 1
            out.append("<pre>%s</pre>" % html.escape("\n".join(code), quote=False))
            continue
        if s.startswith("> "):
            flush()
            buf = []
            while i < len(lines) and lines[i].strip().startswith(">"):
                buf.append(lines[i].strip()[1:].strip()); i += 1
            paras = "<p>%s</p>" % "</p><p>".join(inline(p) for p in " ".join(buf).split("\n") if p)
            out.append("<blockquote>%s</blockquote>" % inline(" ".join(buf)))
            continue
        m = re.match(r"^(#{1,4})\s+(.*)$", s)
        if m:
            flush()
            level, text = len(m.group(1)), m.group(2)
            if level == 1:
                out.append("<h1>%s</h1>" % inline(text))
            else:
                out.append("<h%d>%s</h%d>" % (level, inline(text), level))
            i += 1
            continue
        if s.startswith("|"):
            flush()
            tbl = []
            while i < len(lines) and lines[i].strip().startswith("|"):
                tbl.append(lines[i]); i += 1
            out.append(render_table(tbl))
            continue
        m = re.match(r"^!\[([^\]]*)\]\(([^)]+)\)$", s)
        if m:
            flush()
            alt, src = m.group(1), m.group(2)
            out.append('<img src="%s" alt="%s">' % (src, html.escape(alt)))
            if alt:
                out.append('<div class="caption">图 · %s</div>' % html.escape(alt))
            i += 1
            continue
        if re.match(r"^(\d+\.|[-*])\s+", s):
            flush()
            ordered = bool(re.match(r"^\d+\.", s))
            items = []
            while i < len(lines):
                t = lines[i].strip()
                mm = re.match(r"^(\d+\.|[-*])\s+(.*)$", t)
                if not mm:
                    break
                items.append(mm.group(2)); i += 1
            tag = "ol" if ordered else "ul"
            out.append("<%s>%s</%s>" % (tag, "".join("<li>%s</li>" % inline(x) for x in items), tag))
            continue

        para.append(s); i += 1

    flush()
    return "\n".join(out)


COVER = """
<div class="cover">
  <div class="kicker">WINDOWS 10 / 11 · 免安装</div>
  <h1>DeadBand</h1>
  <div class="sub">把坏掉的屏幕边缘，变成一条有用的信息条</div>
  <img class="hero" src="images/compare.jpg" alt="改造前后对比">
  <div class="meta">使用教程　·　版本 1.0.0　·　© 2026 GUGUQ1</div>
</div>
"""


def main():
    md = open(SRC, encoding="utf-8").read()
    # 封面之后的正文：从第一个二级标题开始
    idx = md.find("\n## ")
    body_md = md[idx:] if idx >= 0 else md
    # 标题本身做成封面，不再在正文里重复
    body_md = re.sub(r"^## 一、这是什么\s*$", "", body_md, count=1).lstrip()

    doc = """<!doctype html>
<html lang="zh-CN"><head><meta charset="utf-8">
<title>DeadBand 使用教程</title>
<style>%s</style></head><body>
%s
%s
</body></html>""" % (CSS, COVER, render_body(body_md))

    # HTML 必须放在 docs/ 下，图片才能按 images/ 找到
    html_path = os.path.join(ROOT, "docs", "_tutorial_print.html")
    with open(html_path, "w", encoding="utf-8") as f:
        f.write(doc)

    edge = next((p for p in EDGE_CANDIDATES if os.path.exists(p)), None)
    if not edge:
        print("找不到 msedge.exe", file=sys.stderr)
        return 1

    if os.path.exists(OUT):
        os.remove(OUT)
    cmd = [edge, "--headless=new", "--disable-gpu", "--no-pdf-header-footer",
           "--print-to-pdf=" + OUT, "file:///" + html_path.replace("\\", "/")]
    r = subprocess.run(cmd, capture_output=True, text=True)
    os.remove(html_path)

    if not os.path.exists(OUT):
        print("PDF 没生成：\n" + (r.stderr or "")[-800:], file=sys.stderr)
        return 1
    print("PDF: %s  %.0f KB" % (OUT, os.path.getsize(OUT) / 1024))
    return 0


if __name__ == "__main__":
    sys.exit(main())
