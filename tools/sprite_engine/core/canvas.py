"""
sprite_engine/core/canvas.py
Pixel-perfect drawing canvas — foundation of the AI sprite engine.
All drawing uses integer-only coordinates (no sub-pixel, no antialiasing).
"""
from __future__ import annotations
from typing import Tuple
from PIL import Image


RGBA = Tuple[int, int, int, int]  # (r, g, b, a)


class Canvas:
    """
    A pixel-perfect RGBA canvas with drawing primitives.
    All operations are integer-coordinate — zero antialiasing.
    """

    def __init__(self, width: int, height: int, bg: RGBA = (0, 0, 0, 0)):
        self.width = width
        self.height = height
        self._img = Image.new("RGBA", (width, height), bg)

    # ─────────────────────────────────────────────
    # Pixel access
    # ─────────────────────────────────────────────

    def put_pixel(self, x: int, y: int, color: RGBA) -> None:
        """Place a single pixel. Alpha-blends if alpha < 255."""
        if not (0 <= x < self.width and 0 <= y < self.height):
            return
        r, g, b, a = color
        if a == 255:
            self._img.putpixel((x, y), (r, g, b, a))
        elif a > 0:
            br, bg_, bb, ba = self._img.getpixel((x, y))
            af = a / 255.0
            nr = int(r * af + br * (1 - af))
            ng = int(g * af + bg_ * (1 - af))
            nb = int(b * af + bb * (1 - af))
            na = min(255, ba + a)
            self._img.putpixel((x, y), (nr, ng, nb, na))

    def get_pixel(self, x: int, y: int) -> RGBA:
        if not (0 <= x < self.width and 0 <= y < self.height):
            return (0, 0, 0, 0)
        return self._img.getpixel((x, y))

    def clear(self, color: RGBA = (0, 0, 0, 0)) -> None:
        self._img = Image.new("RGBA", (self.width, self.height), color)

    # ─────────────────────────────────────────────
    # Shapes — pixel-perfect Bresenham algorithms
    # ─────────────────────────────────────────────

    def draw_line(self, x0: int, y0: int, x1: int, y1: int, color: RGBA) -> None:
        """Bresenham line — zero antialiasing, pixel perfect."""
        dx, dy = abs(x1 - x0), abs(y1 - y0)
        sx = 1 if x0 < x1 else -1
        sy = 1 if y0 < y1 else -1
        err = dx - dy
        while True:
            self.put_pixel(x0, y0, color)
            if x0 == x1 and y0 == y1:
                break
            e2 = 2 * err
            if e2 > -dy:
                err -= dy; x0 += sx
            if e2 < dx:
                err += dx; y0 += sy

    def draw_rect(self, x: int, y: int, w: int, h: int, color: RGBA, filled: bool = False) -> None:
        if filled:
            for py in range(y, y + h):
                self.draw_line(x, py, x + w - 1, py, color)
        else:
            self.draw_line(x, y, x + w - 1, y, color)
            self.draw_line(x, y + h - 1, x + w - 1, y + h - 1, color)
            self.draw_line(x, y, x, y + h - 1, color)
            self.draw_line(x + w - 1, y, x + w - 1, y + h - 1, color)

    def draw_circle(self, cx: int, cy: int, r: int, color: RGBA, filled: bool = False) -> None:
        """Midpoint circle — pixel perfect."""
        if r <= 0:
            self.put_pixel(cx, cy, color); return
        x, y = 0, r
        d = 1 - r
        while x <= y:
            if filled:
                self.draw_line(cx - x, cy + y, cx + x, cy + y, color)
                self.draw_line(cx - x, cy - y, cx + x, cy - y, color)
                self.draw_line(cx - y, cy + x, cx + y, cy + x, color)
                self.draw_line(cx - y, cy - x, cx + y, cy - x, color)
            else:
                for px, py in [(cx+x,cy+y),(cx-x,cy+y),(cx+x,cy-y),(cx-x,cy-y),
                               (cx+y,cy+x),(cx-y,cy+x),(cx+y,cy-x),(cx-y,cy-x)]:
                    self.put_pixel(px, py, color)
            d += (2 * x + 3) if d < 0 else (2 * (x - y) + 5); x += 1
            if d >= 0: y -= 1; d -= 2 * y

    def draw_ellipse(self, cx: int, cy: int, rx: int, ry: int, color: RGBA, filled: bool = False) -> None:
        """Bresenham ellipse algorithm."""
        x, y = 0, ry
        a2, b2 = rx * rx, ry * ry
        fa2 = 4 * a2
        sigma = 2 * b2 + a2 * (1 - 2 * ry)
        while b2 * x <= a2 * y:
            if filled:
                self.draw_line(cx - x, cy + y, cx + x, cy + y, color)
                self.draw_line(cx - x, cy - y, cx + x, cy - y, color)
            else:
                for px, py in [(cx+x,cy+y),(cx-x,cy+y),(cx+x,cy-y),(cx-x,cy-y)]:
                    self.put_pixel(px, py, color)
            if sigma >= 0:
                sigma += fa2 * (1 - y); y -= 1
            sigma += b2 * (4 * x + 6); x += 1
        x, y = rx, 0
        fb2 = 4 * b2
        sigma = 2 * a2 + b2 * (1 - 2 * rx)
        while a2 * y <= b2 * x:
            if filled:
                self.draw_line(cx - x, cy + y, cx + x, cy + y, color)
                self.draw_line(cx - x, cy - y, cx + x, cy - y, color)
            else:
                for px, py in [(cx+x,cy+y),(cx-x,cy+y),(cx+x,cy-y),(cx-x,cy-y)]:
                    self.put_pixel(px, py, color)
            if sigma >= 0:
                sigma += fb2 * (1 - x); x -= 1
            sigma += a2 * (4 * y + 6); y += 1

    def fill_bucket(self, x: int, y: int, color: RGBA) -> None:
        """4-connected flood fill."""
        if not (0 <= x < self.width and 0 <= y < self.height):
            return
        target = self.get_pixel(x, y)
        if target == color:
            return
        stack = [(x, y)]
        visited: set = set()
        while stack:
            px, py = stack.pop()
            if (px, py) in visited: continue
            if not (0 <= px < self.width and 0 <= py < self.height): continue
            if self.get_pixel(px, py) != target: continue
            visited.add((px, py))
            self.put_pixel(px, py, color)
            stack += [(px+1,py),(px-1,py),(px,py+1),(px,py-1)]

    # ─────────────────────────────────────────────
    # Pixel art utilities
    # ─────────────────────────────────────────────

    def add_outline(self, outline_color: RGBA = (0, 0, 0, 255), thickness: int = 1) -> "Canvas":
        """Add dark outline around all non-transparent pixels."""
        result = self.clone()
        offsets = []
        for t in range(1, thickness + 1):
            for dx in range(-t, t + 1):
                for dy in range(-t, t + 1):
                    if abs(dx) == t or abs(dy) == t:
                        offsets.append((dx, dy))
        for y in range(self.height):
            for x in range(self.width):
                if self.get_pixel(x, y)[3] > 0:
                    for dx, dy in offsets:
                        nx, ny = x + dx, y + dy
                        if 0 <= nx < self.width and 0 <= ny < self.height:
                            if self.get_pixel(nx, ny)[3] == 0:
                                result.put_pixel(nx, ny, outline_color)
        return result

    def squash(self, amount: int = 2) -> "Canvas":
        """Squash: shorter + wider. Animation principle — landing/impact."""
        new_w = min(self.width + amount * 2, self.width + 12)
        new_h = max(self.height - amount, 4)
        c = Canvas(self.width, self.height)
        resized = self._img.resize((new_w, new_h), Image.NEAREST)
        x_off = max(0, (self.width - new_w) // 2)
        y_off = self.height - new_h
        c._img.alpha_composite(resized, dest=(x_off, max(0, y_off)))
        return c

    def stretch(self, amount: int = 2) -> "Canvas":
        """Stretch: taller + narrower. Animation principle — jump/anticipation."""
        new_w = max(self.width - amount * 2, 4)
        new_h = min(self.height + amount, self.height + 12)
        c = Canvas(self.width, self.height)
        resized = self._img.resize((new_w, new_h), Image.NEAREST)
        x_off = max(0, (self.width - new_w) // 2)
        c._img.alpha_composite(resized, dest=(x_off, 0))
        return c

    def highlight(self, highlight_color: RGBA, shadow_color: RGBA) -> "Canvas":
        """Add highlight (top-left) and shadow (bottom-right) for depth."""
        result = self.clone()
        for y in range(self.height):
            for x in range(self.width):
                r, g, b, a = self.get_pixel(x, y)
                if a > 0:
                    # Top-left highlight
                    if y < self.height * 0.3 and x < self.width * 0.4:
                        result.put_pixel(x, y, highlight_color)
                    # Bottom-right shadow
                    elif y > self.height * 0.7 and x > self.width * 0.6:
                        result.put_pixel(x, y, shadow_color)
        return result

    # ─────────────────────────────────────────────
    # Transform
    # ─────────────────────────────────────────────

    def flip_h(self) -> "Canvas":
        c = Canvas(self.width, self.height)
        c._img = self._img.transpose(Image.FLIP_LEFT_RIGHT)
        return c

    def flip_v(self) -> "Canvas":
        c = Canvas(self.width, self.height)
        c._img = self._img.transpose(Image.FLIP_TOP_BOTTOM)
        return c

    def scale(self, factor: int) -> "Canvas":
        """Nearest-neighbor upscale — pixel perfect, no blur."""
        c = Canvas(self.width * factor, self.height * factor)
        c._img = self._img.resize((c.width, c.height), Image.NEAREST)
        return c

    def crop(self, x: int, y: int, w: int, h: int) -> "Canvas":
        c = Canvas(w, h)
        c._img = self._img.crop((x, y, x + w, y + h))
        return c

    def blit(self, other: "Canvas", ox: int = 0, oy: int = 0) -> None:
        """Alpha-composite another canvas onto this one."""
        self._img.alpha_composite(other._img, dest=(ox, oy))

    def clone(self) -> "Canvas":
        c = Canvas(self.width, self.height)
        c._img = self._img.copy()
        return c

    # ─────────────────────────────────────────────
    # I/O
    # ─────────────────────────────────────────────

    def to_pil(self) -> Image.Image:
        return self._img.copy()

    def save(self, path: str) -> None:
        self._img.save(path)

    @classmethod
    def from_pil(cls, img: Image.Image) -> "Canvas":
        img = img.convert("RGBA")
        c = cls(img.width, img.height)
        c._img = img
        return c

    @classmethod
    def from_file(cls, path: str) -> "Canvas":
        return cls.from_pil(Image.open(path))

    def __repr__(self) -> str:
        return f"Canvas({self.width}x{self.height})"
