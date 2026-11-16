# 生成推文用对比图（PNG）。数据来自 bench/out/exp/compare-tcp/compare-tcp.csv 的同轮交替测量。
import os
from PIL import Image, ImageDraw, ImageFont

W, H = 1200, 800
M = 60
BG = (255, 255, 255)
FG = (32, 32, 32)
GRAY = (120, 120, 120)
LIGHT = (230, 230, 230)
FT = (47, 126, 216)
FRP = (217, 83, 79)

FONT_DIR = r"C:\Windows\Fonts"
def font(size, bold=False):
    name = "msyhbd.ttc" if bold else "msyh.ttc"
    return ImageFont.truetype(os.path.join(FONT_DIR, name), size)

title_f = font(30, True)
sub_f = font(15)
label_f = font(14)
value_f = font(14, True)
legend_f = font(16)
note_f = font(14)

levels = ["5k", "10k", "30k", "60k", "90k"]
ft_ach = [4973, 9975, 29907, 42131, 42862]
frp_ach = [4978, 9953, 29995, 32132, 33783]
ft_p99 = [7.9, 10.0, 11.1, 9.6, 9.8]
frp_p99 = [3.4, 12.8, 10.0, 16.0, 10.1]

img = Image.new("RGB", (W, H), BG)
d = ImageDraw.Draw(img)

d.text((M, 34), "本地 HTTP 穿透性能对照：FastTunnel vs frp 0.72.0", font=title_f, fill=FG)
d.text((M, 76), "IIS:8080 静态页(1KB) · 256 并发 keep-alive · 两侧明文+关日志 · 16 核 · 同轮交替测",
       font=sub_f, fill=GRAY)

# ---------------- 面板 1：达成 RPS ----------------
top, bottom = 150, 470
left, right = M + 50, W - M - 10
max_v = 50000

d.text((M, 112), "达成请求速率（RPS，目标越高越接近机器上限）", font=legend_f, fill=FG)

for v in range(0, max_v + 1, 10000):
    y = bottom - (bottom - top) * v / max_v
    d.line([(left, y), (right, y)], fill=LIGHT, width=1)
    d.text((M - 8, y - 8), f"{v // 1000}k", font=label_f, fill=GRAY, anchor="ra")

group_w = (right - left) / len(levels)
bar_w = group_w * 0.26
for i, lv in enumerate(levels):
    cx = left + group_w * (i + 0.5)
    for j, (val, color) in enumerate(((ft_ach[i], FT), (frp_ach[i], FRP))):
        x0 = cx - bar_w - 4 if j == 0 else cx + 4
        x1 = x0 + bar_w
        y0 = bottom - (bottom - top) * val / max_v
        d.rectangle([x0, y0, x1, bottom], fill=color)
        d.text(((x0 + x1) / 2, y0 - 20), f"{val/1000:.1f}k", font=value_f, fill=color, anchor="ma")
    d.text((cx, bottom + 8), lv, font=label_f, fill=GRAY, anchor="ma")

# 图例
ly = 112
d.rectangle([right - 330, ly + 2, right - 316, ly + 16], fill=FT)
d.text((right - 308, ly), "FastTunnel", font=legend_f, fill=FG)
d.rectangle([right - 150, ly + 2, right - 136, ly + 16], fill=FRP)
d.text((right - 128, ly), "frp type=tcp", font=legend_f, fill=FG)

# ---------------- 面板 2：p99 ----------------
top2, bottom2 = 560, 720
max2 = 20.0
d.text((M, 522), "p99 延迟（ms，越低越好）", font=legend_f, fill=FG)

for v in range(0, int(max2) + 1, 5):
    y = bottom2 - (bottom2 - top2) * v / max2
    d.line([(left, y), (right, y)], fill=LIGHT, width=1)
    d.text((M - 8, y - 8), f"{v}", font=label_f, fill=GRAY, anchor="ra")

def points(values):
    return [(left + group_w * (i + 0.5), bottom2 - (bottom2 - top2) * v / max2) for i, v in enumerate(values)]

for values, color, name in ((ft_p99, FT, "FastTunnel"), (frp_p99, FRP, "frp-tcp")):
    pts = points(values)
    d.line(pts, fill=color, width=3)
    for (x, y), v in zip(pts, values):
        d.ellipse([x - 4, y - 4, x + 4, y + 4], fill=color)
        d.text((x, y - 20), f"{v:.1f}", font=value_f, fill=color, anchor="ma")

for i, lv in enumerate(levels):
    d.text((left + group_w * (i + 0.5), bottom2 + 8), lv, font=label_f, fill=GRAY, anchor="ma")

d.text((M, 750),
       "错误率：两条链路全程 0%。超过上限后表现为「达成速率不再跟随目标 + 延迟抬升」，不是报错。",
       font=note_f, fill=GRAY)
d.text((M, 772),
       "本机上限：FastTunnel ≈ 43k RPS；frp(tcp) ≈ 34k RPS。内存合计 frp 高约 1.25~1.4 倍。",
       font=note_f, fill=GRAY)

out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "tweet-chart.png")
img.save(out, "PNG")
print("saved", out, img.size)
