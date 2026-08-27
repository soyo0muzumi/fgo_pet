from __future__ import annotations

import math
from collections import deque
from statistics import median

from PIL import Image, ImageFilter


def remove_edge_background(
    image: Image.Image, *, tolerance: int = 32, feather: int = 2
) -> Image.Image:
    rgba = image.convert("RGBA")
    width, height = rgba.size
    pixels = rgba.load()
    border = [
        pixels[x, y][:3]
        for x, y in (
            [(x, 0) for x in range(width)]
            + [(x, height - 1) for x in range(width)]
            + [(0, y) for y in range(height)]
            + [(width - 1, y) for y in range(height)]
        )
        if pixels[x, y][3] > 0
    ]
    if not border:
        return rgba
    background = tuple(int(median(channel)) for channel in zip(*border))

    def matches(x: int, y: int) -> bool:
        red, green, blue, alpha = pixels[x, y]
        if alpha == 0:
            return True
        return math.dist((red, green, blue), background) <= tolerance

    queue = deque(
        [(x, 0) for x in range(width)]
        + [(x, height - 1) for x in range(width)]
        + [(0, y) for y in range(height)]
        + [(width - 1, y) for y in range(height)]
    )
    removed: set[tuple[int, int]] = set()
    while queue:
        x, y = queue.popleft()
        if (x, y) in removed or not matches(x, y):
            continue
        removed.add((x, y))
        for next_x, next_y in ((x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1)):
            if 0 <= next_x < width and 0 <= next_y < height:
                queue.append((next_x, next_y))

    mask = Image.new("L", rgba.size, 255)
    mask_pixels = mask.load()
    for x, y in removed:
        mask_pixels[x, y] = 0
    if feather > 0:
        mask = mask.filter(ImageFilter.GaussianBlur(radius=feather / 2))
    original_alpha = rgba.getchannel("A")
    alpha = Image.frombytes(
        "L",
        rgba.size,
        bytes(min(a, b) for a, b in zip(original_alpha.tobytes(), mask.tobytes())),
    )
    rgba.putalpha(alpha)
    return rgba
