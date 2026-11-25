# -*- coding: utf-8 -*-
"""把两张手机实拍做成 README 用的「改造前 / 改造后」对照图。

用法：
    python tools/make-compare.py <改造前照片> <改造后照片> <输出目录>

为什么要这个脚本：手机拍屏幕必然是斜的，直接用会显得歪。这里不去旋转（旋转会
带出白角和桌面边缘的二次倾斜），而是沿 y 做切变，把「屏幕底边 -> 外框」这条线
压平，再裁掉上方界面内容和下方桌面杂物。参数都是对着这两张图量出来的，换新图要重调。
"""
import os
import sys
import numpy as np
from PIL import Image, ImageDraw, ImageFont

FONT = r"C:\Windows\Fonts\msyhbd.ttc"
WIDTH = 1200          # 输出统一宽度
GAP = 14              # 两张图之间的空隙
PAPER = (246, 246, 248)
QUALITY = 90


def bezel_slope(img, y_start, y_end, dark=50, run=18):
    """每列找「屏幕内容持续变暗」的第一行（= 外框上沿），最小二乘拟合斜率。"""
    a = np.asarray(img.convert("L"), dtype=np.float32)
    h, w = a.shape
    xs, ys = [], []
    for x in range(int(w * 0.06), int(w * 0.94)):
        col = a[max(0, y_start):min(h, y_end), x]
        for i in range(len(col) - run):
            if col[i:i + run].mean() < dark:
                xs.append(x); ys.append(max(0, y_start) + i)
                break
    xs = np.array(xs, float); ys = np.array(ys, float)
    for _ in range(3):
        k, b = np.polyfit(xs, ys, 1)
        resid = ys - (k * xs + b)
        keep = np.abs(resid) < max(3.0, np.std(resid) * 2.5)
        xs, ys = xs[keep], ys[keep]
    k, b = np.polyfit(xs, ys, 1)
    return float(k), float(b)


def shear_level(img, k):
    """输出图 y_out = y_in - k*x，把斜率为 k 的线压平，且不产生空白角。"""
    w, h = img.size
    out_h = int(round(h + abs(k) * (w - 1)))
    f = -k * (w - 1) if k < 0 else 0.0
    return img.transform((w, out_h), Image.AFFINE, (1, 0, 0, k, 1, f), resample=Image.BICUBIC)


def pill(img, text):
    """左下角（外框上）一枚半透明黑底白字标签。"""
    layer = Image.new("RGBA", img.size, (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    font = ImageFont.truetype(FONT, 24)
    box = d.textbbox((0, 0), text, font=font)
    tw, th = box[2] - box[0], box[3] - box[1]
    pad_x, pad_y = 14, 7
    x, h = 18, th + pad_y * 2 + 4
    y = img.size[1] - h - 16
    d.rounded_rectangle((x, y, x + tw + pad_x * 2, y + h), radius=8, fill=(12, 14, 18, 165))
    d.text((x + pad_x, y + pad_y - box[1] + 2), text, font=font, fill=(255, 255, 255, 255))
    return Image.alpha_composite(img.convert("RGBA"), layer).convert("RGB")


def prep(src, scan_range, crop, label, out_path):
    img = Image.open(src).convert("RGB")
    k, b = bezel_slope(img, scan_range[0], scan_range[1])
    flat = shear_level(img, k)
    part = flat.crop(crop)
    h = round(part.size[1] * WIDTH / part.size[0])
    part = part.resize((WIDTH, h), Image.LANCZOS)
    part.save(out_path, quality=QUALITY)
    print("%-7s slope=%+.4f bezel_y=%.0f crop=%s -> %dx%d %s"
          % (label, k, b, crop, WIDTH, h, out_path))
    return part


def main(before_src, after_src, outdir):
    os.makedirs(outdir, exist_ok=True)

    # 裁切框是对着这两张照片量的：
    #   改造前 1179x277，外框上沿在 y≈171
    #   改造后 1388x1209（切变后），区带上沿 y≈375、外框上沿 y≈545、桌面杂物 y>650
    before = prep(before_src, (60, 250), (0, 58, 1179, 277), "before",
                  os.path.join(outdir, "before-strip.jpg"))
    after = prep(after_src, (300, 900), (0, 366, 1388, 648), "after",
                 os.path.join(outdir, "after-strip.jpg"))

    before = pill(before, "改造前")
    after = pill(after, "改造后")

    canvas = Image.new("RGB", (WIDTH, before.size[1] + GAP + after.size[1]), PAPER)
    canvas.paste(before, (0, 0))
    canvas.paste(after, (0, before.size[1] + GAP))
    out = os.path.join(outdir, "compare.jpg")
    canvas.save(out, quality=QUALITY)
    print("compare %dx%d -> %s (%.0f KB)"
          % (canvas.size[0], canvas.size[1], out, os.path.getsize(out) / 1024))


if __name__ == "__main__":
    if len(sys.argv) != 4:
        print(__doc__)
        sys.exit(2)
    main(sys.argv[1], sys.argv[2], sys.argv[3])
