#!/usr/bin/env python3
"""Иконка EliteRec: корона и волна из docs/brand/eliterec-mark.png на подложке как у EliteSIP.

Подложка — скруглённый квадрат 424/512 с радиусом 96 и вертикальным градиентом
#36363F → #16161E (сняты с иконки EliteSIP). Корона — золото с градиентом сверху вниз;
волна под короной двухцветная, как в главном окне: левая половина золотая (Вы),
правая голубая (Система), центральная капсула разделена по оси.
Пишет src/EliteRec.App/Assets/eliterec.png (512) и docs/brand/eliterec-<размер>.png.
"""
from pathlib import Path
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
S = 1024  # рисуем вдвое крупнее и уменьшаем — ровные края
GOLD = ((232, 196, 112), (184, 140, 58))
BLUE = ((170, 205, 245), (95, 145, 205))
CONTENT_WIDTH = .74  # доля ширины иконки: знак заполняет подложку, мелким не выглядит


def gradient(size, top, bottom):
    column = Image.new("RGB", (1, size[1]))
    for y in range(size[1]):
        k = y / (size[1] - 1)
        column.putpixel((0, y), tuple(round(a + (b - a) * k) for a, b in zip(top, bottom)))
    return column.resize(size)


def main():
    mark = Image.open(ROOT / "docs/brand/eliterec-mark.png").convert("RGB")
    # Маска знака: насколько пиксель золотой относительно почти чёрного фона.
    mask = mark.getchannel("R").point(lambda r: max(0, min(255, (r - 14) * 255 // 200)))
    box = mask.point(lambda a: 255 if a > 40 else 0).getbbox()
    mask = mask.crop(box)
    w = round(S * CONTENT_WIDTH)
    h = round(mask.height * w / mask.width)
    mask = mask.resize((w, h), Image.LANCZOS)

    # Волна — нижняя часть знака между остриями короны; правее оси — голубая.
    scale = w / (box[2] - box[0])
    wave_top = round((755 - box[1]) * scale)
    wave_left, wave_right = round((350 - box[0]) * scale), round((899 - box[0]) * scale)
    axis = round((627 - box[0]) * scale)
    blue = Image.new("L", (w, h), 0)
    # Правая нога короны спускается до y≈815 правее x≈840 — крайнюю капсулу берём ниже неё.
    leg, low = round((840 - box[0]) * scale), round((870 - box[1]) * scale)
    ImageDraw.Draw(blue).rectangle((axis, wave_top, leg, h), fill=255)
    ImageDraw.Draw(blue).rectangle((leg, low, wave_right, h), fill=255)
    blue_mask = Image.composite(mask, Image.new("L", (w, h), 0), blue)

    icon = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    plate = Image.new("L", (S, S), 0)
    inset = round(S * 44 / 512)
    ImageDraw.Draw(plate).rounded_rectangle((inset, inset, S - inset - 1, S - inset - 1), radius=round(S * 96 / 512), fill=255)
    icon.paste(gradient((S, S), (54, 54, 63), (22, 22, 30)), (0, 0), plate)

    x, y = (S - w) // 2, (S - h) // 2 + round(S * .01)
    icon.paste(gradient((w, h), *GOLD), (x, y), mask)
    icon.paste(gradient((w, h), *BLUE).crop((0, wave_top, w, h)), (x, y + wave_top), blue_mask.crop((0, wave_top, w, h)))
    assert wave_left < axis < wave_right

    icon.resize((512, 512), Image.LANCZOS).save(ROOT / "src/EliteRec.App/Assets/eliterec.png")
    for size in (16, 32, 48, 64, 128, 256, 512):
        icon.resize((size, size), Image.LANCZOS).save(ROOT / f"docs/brand/eliterec-{size}.png")


if __name__ == "__main__":
    main()
