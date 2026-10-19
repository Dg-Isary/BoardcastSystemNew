#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
生成「广播系统手控端」的 **Android 标准启动图标**（字形取自 Font Awesome，位图用 Pillow 离屏渲染）。

这一轮方向：**清亮通透** —— 浅色玻璃渐变底 + 饱和但干净的字形（上一版高饱和霓虹被否：太"土/闷"）。
字形也换成 tower-broadcast（信号塔）试一版，脚本同时输出多套候选的对比预览。

产物（写进 app/src/main/res）：
  · mipmap-anydpi-v26/ic_launcher.xml、ic_launcher_round.xml   —— 自适应图标（Android 8+）
  · mipmap-*/ic_launcher_foreground.png、ic_launcher_monochrome.png
  · mipmap-*/ic_launcher.png、ic_launcher_round.png            —— 老系统方形/圆形
  · values/ic_launcher_background.xml                          —— 自适应背景色
  · dist/icon-preview.png                                      —— 全部候选配色对比图

用法：python BroadcastApp/make-icon.py
"""
import os
from PIL import Image, ImageDraw, ImageFont

ROOT = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(ROOT)
RES = os.path.join(ROOT, "android", "app", "src", "main", "res")
FONT = os.path.join(RES, "font", "fa_solid_900.otf")
BULLHORN = "\uf0a1"       # bullhorn
TOWER = "\uf519"          # tower-broadcast

# (名字, 字形, 渐变起点, 渐变终点, 字形色, 描边色, 声波色, 自适应背景色)
PALETTES = [
    ("清透天蓝·信号塔", TOWER, (0xFF, 0xFF, 0xFF), (0x38, 0xBD, 0xF8), (0x02, 0x5F, 0x9E), (0x00, 0x27, 0x45), (0x0E, 0xA5, 0xE9), "#8FD8FB"),
    ("清透天蓝·喇叭", BULLHORN, (0xFF, 0xFF, 0xFF), (0x38, 0xBD, 0xF8), (0x02, 0x5F, 0x9E), (0x00, 0x27, 0x45), (0x0E, 0xA5, 0xE9), "#8FD8FB"),
    ("玻璃青·信号塔", TOWER, (0xF2, 0xFE, 0xFF), (0x2A, 0xDC, 0xF0), (0x0A, 0x6E, 0x8A), (0x00, 0x33, 0x42), (0x06, 0xB6, 0xD4), "#B5F5FA"),
    ("白玻璃·信号塔", TOWER, (0xFF, 0xFF, 0xFF), (0xDF, 0xE7, 0xF2), (0x25, 0x63, 0xEB), (0x14, 0x2C, 0x6E), (0x60, 0xA5, 0xFA), "#F1F5FA"),
    ("亮紫清透·信号塔", TOWER, (0xFB, 0xF7, 0xFF), (0xC9, 0xBB, 0xFD), (0x6D, 0x28, 0xD9), (0x40, 0x14, 0x86), (0x8B, 0x5C, 0xF6), "#E4DBFE"),
]
USE = 3                    # 打包用哪套（3 = 白玻璃·信号塔）
DENS = {"mdpi": 1.0, "hdpi": 1.5, "xhdpi": 2.0, "xxhdpi": 3.0, "xxxhdpi": 4.0}


def cjk_font(px=20):
    """预览里的中文说明要用系统字体（PIL 自带字体没汉字，会画成方块；只影响预览图）。"""
    for cand in (r"C:\Windows\Fonts\msyh.ttc", r"C:\Windows\Fonts\simhei.ttf", r"C:\Windows\Fonts\msyhbd.ttc"):
        if os.path.exists(cand):
            try:
                return ImageFont.truetype(cand, px)
            except Exception:
                pass
    return None


def gradient_bg(size, c0, c1):
    """浅色对角渐变底（清亮通透的关键：底色要亮、要有层次，但不能脏）。"""
    img = Image.new("RGBA", (size, size))
    px = img.load()
    n = max(1, size - 1)
    for y in range(size):
        for x in range(size):
            t = min(1.0, max(0.0, (x * 0.35 + y * 0.65) / n))
            k = t * t * (3 - 2 * t)                      # smoothstep：过渡自然
            px[x, y] = (
                int(c0[0] + (c1[0] - c0[0]) * k),
                int(c0[1] + (c1[1] - c0[1]) * k),
                int(c0[2] + (c1[2] - c0[2]) * k),
                255,
            )
    return img


def draw_glyph(size, canvas_ratio, glyph, horn, edge, wave, waves=True):
    """透明画布上画居中的字形（+ 可选声波）。按包围盒精确居中，不靠"看起来居中"。"""
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    target = size * canvas_ratio
    fs = int(target)
    for _ in range(14):
        f = ImageFont.truetype(FONT, fs)
        b = f.getbbox(glyph)
        h = b[3] - b[1]
        if h <= 0 or abs(h - target) <= max(1, target * 0.01):
            break
        fs = max(8, int(fs * target / h))
    f = ImageFont.truetype(FONT, fs)
    b = f.getbbox(glyph)
    w, h = b[2] - b[0], b[3] - b[1]
    gx = (size - w) / 2 - b[0] - (size * 0.030 if waves else 0)
    gy = (size - h) / 2 - b[1]
    for dx, dy in ((-1, 0), (1, 0), (0, -1), (0, 1)):          # 描边：小尺寸下轮廓更清楚
        d.text((gx + dx, gy + dy), glyph, font=f, fill=edge + (255,))
    d.text((gx, gy), glyph, font=f, fill=horn + (255,))
    if waves:
        cx = gx + b[0] + w * 0.96 + size * 0.008
        cy = gy + b[1] + h * 0.50
        for i in range(3):
            r = size * 0.070 + size * 0.052 * i
            d.arc((cx - r, cy - r, cx + r, cy + r), start=-48, end=48,
                  fill=wave + (255,), width=max(2, int(size * 0.04)))
    return img


def rounded(img, radius_ratio, circle=False):
    size = img.width
    mask = Image.new("L", (size, size), 0)
    d = ImageDraw.Draw(mask)
    if circle:
        d.ellipse((0, 0, size - 1, size - 1), fill=255)
    else:
        d.rounded_rectangle((0, 0, size - 1, size - 1), radius=int(size * radius_ratio), fill=255)
    out = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    out.paste(img, (0, 0), mask)
    return out


def full_icon(px, pal):
    _name, glyph, c0, c1, horn, edge, wave, _bg = pal
    base = gradient_bg(px, c0, c1)
    base.alpha_composite(draw_glyph(px, 0.46, glyph, horn, edge, wave))
    return base


def ensure(path):
    os.makedirs(path, exist_ok=True)


def main():
    if not os.path.exists(FONT):
        raise SystemExit("找不到字体：" + FONT)
    pal = PALETTES[USE]
    glyph = pal[1]

    # ---- 自适应图标前景（字形 + 声波，约 46/108，落在 66/108 安全区内）；老系统整图同步生成 ----
    for dens, scale in DENS.items():
        px = int(round(108 * scale))
        out = os.path.join(RES, "mipmap-" + dens)
        ensure(out)
        draw_glyph(px, 31.0 / 108.0, glyph, pal[4], pal[5], pal[6]).save(
            os.path.join(out, "ic_launcher_foreground.png"))
        # 单色层（Android 13 主题图标）：系统只取 alpha，所以给白色剪影
        draw_glyph(px, 31.0 / 108.0, glyph, (255, 255, 255), (255, 255, 255), (255, 255, 255)).save(
            os.path.join(out, "ic_launcher_monochrome.png"))
        ipx = int(round(48 * scale))
        base = full_icon(ipx, pal)
        rounded(base, 0.22).save(os.path.join(out, "ic_launcher.png"))
        rounded(base, 0.0, circle=True).save(os.path.join(out, "ic_launcher_round.png"))

    # ---- 自适应 XML（背景色 + 前景 + 单色层）----
    anydpi = os.path.join(RES, "mipmap-anydpi-v26")
    ensure(anydpi)
    xml = (
        '<?xml version="1.0" encoding="utf-8"?>\n'
        '<adaptive-icon xmlns:android="http://schemas.android.com/apk/res/android">\n'
        '    <background android:drawable="@color/ic_launcher_background" />\n'
        '    <foreground android:drawable="@mipmap/ic_launcher_foreground" />\n'
        '    <monochrome android:drawable="@mipmap/ic_launcher_monochrome" />\n'
        '</adaptive-icon>\n'
    )
    for n in ("ic_launcher.xml", "ic_launcher_round.xml"):
        with open(os.path.join(anydpi, n), "w", encoding="utf-8") as f:
            f.write(xml)
    values = os.path.join(RES, "values")
    ensure(values)
    with open(os.path.join(values, "ic_launcher_background.xml"), "w", encoding="utf-8") as f:
        f.write('<?xml version="1.0" encoding="utf-8"?>\n<resources>\n'
                '    <color name="ic_launcher_background">' + pal[7] + '</color>\n</resources>\n')

    # ---- 预览：每套一行（方形 + 圆形 + 72/48px 小尺寸 + 色卡），当前打包那行带 [当前] 标记 ----
    capf = cjk_font(20)
    row_h, pad = 264, 16
    W = 1120
    H = pad + len(PALETTES) * (row_h + pad)
    prev = Image.new("RGBA", (W, H), (247, 248, 251, 255))
    d = ImageDraw.Draw(prev)
    big = 200
    for i, p in enumerate(PALETTES):
        y = pad + i * (row_h + pad)
        d.text((pad, y + 6), ("[当前] " if i == USE else "        ") + p[0],
               fill=(35, 38, 45, 255), font=capf)
        yy = y + 42
        prev.alpha_composite(rounded(full_icon(big, p), 0.22), (pad, yy))
        prev.alpha_composite(rounded(full_icon(big, p), 0.0, circle=True), (pad + big + 16, yy))
        x2 = pad + 2 * (big + 16)
        prev.alpha_composite(rounded(full_icon(72, p), 0.22), (x2, yy + 58))
        prev.alpha_composite(rounded(full_icon(48, p), 0.22), (x2 + 90, yy + 70))
        d.text((x2, yy + 140), "72px / 48px", fill=(90, 96, 105, 255), font=capf)
        bx = x2 + 210
        for j, c in enumerate((p[2], p[3], p[4], p[6])):
            d.rectangle((bx + j * 56, yy + 70, bx + j * 56 + 48, yy + 118), fill=c + (255,))
    dist = os.path.join(REPO, "dist")
    ensure(dist)
    prev.save(os.path.join(dist, "icon-preview.png"))
    print("图标已生成（打包用：{}）→ mipmap-*/ic_launcher*，预览 dist/icon-preview.png".format(pal[0]))


if __name__ == "__main__":
    main()
