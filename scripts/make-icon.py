"""
生成应用图标：DeepSeek 鲸鱼标志 + 右下角 TTS 角标。

产出：
  assets/logo.ico        多尺寸 Windows 图标（16~256）
  assets/icon-256.png    安装包/展示用大图
  assets/banner.bmp      Inno Setup 安装向导横幅
  assets/wizard.bmp      Inno Setup 欢迎/完成页大图
"""

from PIL import Image, ImageDraw, ImageFont
import os

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ASSETS = os.path.join(ROOT, "assets")

BLUE = (79, 124, 255)          # DeepSeek 品牌蓝（与界面主题一致）
BLUE_DARK = (44, 86, 214)
WHITE = (255, 255, 255)

# ---- 1. 裁出鲸鱼本体 ----
src = Image.open(os.path.join(ASSETS, "logo.png")).convert("RGBA")

# logo.png 是"鲸鱼 + deepseek 文字"的横向组合，透明背景。
# 按列扫描找到第一段连续的非空列（即鲸鱼），再在竖直方向收紧边界。
_px = src.load()
_w, _h = src.size
_cols = [sum(1 for y in range(_h) if _px[x, y][3] > 20) for x in range(_w)]

_whale_x1 = 0
for x in range(_w):
    if _cols[x] == 0 and x > 0:
        _whale_x1 = x - 1
        break

whale = src.crop((0, 0, _whale_x1 + 1, _h))
_bbox = whale.getbbox()
whale = whale.crop(_bbox)
print("鲸鱼区域:", _bbox, "尺寸:", whale.size)

# ---- 2. 合成图标：渐变圆角底 + 鲸鱼 + TTS 角标 ----
def make_icon(size: int) -> Image.Image:
    S = size * 4  # 4x 超采样，最后缩小以获得平滑边缘
    canvas = Image.new("RGBA", (S, S), (0, 0, 0, 0))

    # 圆角矩形渐变底
    bg = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    grad = Image.new("RGBA", (S, S))
    gd = ImageDraw.Draw(grad)
    for y in range(S):
        t = y / max(1, S - 1)
        c = tuple(int(BLUE[i] + (BLUE_DARK[i] - BLUE[i]) * t) for i in range(3))
        gd.line([(0, y), (S, y)], fill=(*c, 255))

    mask = Image.new("L", (S, S), 0)
    md = ImageDraw.Draw(mask)
    radius = int(S * 0.22)
    md.rounded_rectangle([0, 0, S - 1, S - 1], radius=radius, fill=255)
    canvas.paste(grad, (0, 0), mask)

    # 鲸鱼：放在左上，留出右下角给角标
    target_w = int(S * 0.60)
    ratio = target_w / whale.width
    ww, wh = int(whale.width * ratio), int(whale.height * ratio)
    whale_r = whale.resize((ww, wh), Image.LANCZOS)

    # 着色为白色（蓝底上更清晰）
    tinted = Image.new("RGBA", whale_r.size, WHITE)
    tinted.putalpha(whale_r.getchannel("A"))

    wx = int(S * 0.09)
    wy = int(S * 0.10)
    canvas.paste(tinted, (wx, wy), tinted)

    # 右下角 TTS 角标
    badge_size = int(S * 0.36)
    bx = S - badge_size - int(S * 0.045)
    by = S - badge_size - int(S * 0.045)

    draw = ImageDraw.Draw(canvas)

    # 角标底：白色圆角块
    draw.rounded_rectangle(
        [bx, by, bx + badge_size, by + badge_size],
        radius=int(badge_size * 0.26),
        fill=WHITE,
    )

    # 字体：优先 Segoe UI Bold
    font = None
    for size_px in range(int(badge_size * 1.1), 8, -1):
        for path in (
            r"C:\Windows\Fonts\seguibl.ttf",
            r"C:\Windows\Fonts\segoeuib.ttf",
            r"C:\Windows\Fonts\arialbd.ttf",
        ):
            try:
                f = ImageFont.truetype(path, size_px)
                bbox_f = f.getbbox("TTS")
                tw = bbox_f[2] - bbox_f[0]
                if tw <= badge_size * 0.80:
                    font = f
                    break
            except OSError:
                continue
        if font:
            break

    if font is None:
        font = ImageFont.load_default()

    bbox_f = draw.textbbox((0, 0), "TTS", font=font)
    tw = bbox_f[2] - bbox_f[0]
    th = bbox_f[3] - bbox_f[1]
    tx = bx + (badge_size - tw) / 2 - bbox_f[0]
    ty = by + (badge_size - th) / 2 - bbox_f[1]
    draw.text((tx, ty), "TTS", font=font, fill=BLUE_DARK)

    return canvas.resize((size, size), Image.LANCZOS)


# ---- 3. 输出各尺寸 ----
sizes = [16, 24, 32, 48, 64, 128, 256]
images = [make_icon(s) for s in sizes]

icon_256 = make_icon(256)
icon_256.save(os.path.join(ASSETS, "icon-256.png"))
print("已生成 icon-256.png")

ico_path = os.path.join(ASSETS, "logo.ico")
images[-1].save(
    ico_path,
    format="ICO",
    sizes=[(s, s) for s in sizes],
)
print("已生成 logo.ico")

# 单独存一份 64px 便于查看（任务栏大小）
make_icon(64).save(os.path.join(ASSETS, "icon-64.png"))

# ---- 4. Inno Setup 向导图 ----
# 右侧大图 164x314（Inno 的 WizardImageFile 尺寸）
wiz_w, wiz_h = 164, 314
wizard = Image.new("RGB", (wiz_w, wiz_h), (20, 26, 38))
wd = ImageDraw.Draw(wizard)
for y in range(wiz_h):
    t = y / (wiz_h - 1)
    c = tuple(int(20 + (36 - 20) * t) for _ in range(3))
    wd.line([(0, y), (wiz_w, y)], fill=(c[0], c[1], int(38 + (66 - 38) * t)))

big = make_icon(120)
wizard.paste(big, ((wiz_w - 120) // 2, 70), big)
wizard.save(os.path.join(ASSETS, "wizard.bmp"))
print("已生成 wizard.bmp")

# 顶部横幅 497x58
ban_w, ban_h = 497, 58
banner = Image.new("RGB", (ban_w, ban_h), (255, 255, 255))
bd = ImageDraw.Draw(banner)
bd.rectangle([0, ban_h - 3, ban_w, ban_h], fill=BLUE)
small = make_icon(44)
banner.paste(small, (ban_w - 54, 7), small)
banner.save(os.path.join(ASSETS, "banner.bmp"))
print("已生成 banner.bmp")
