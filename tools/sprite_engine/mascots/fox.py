"""
sprite_engine/mascots/fox.py
Kira the Fox — FlyShelf's recommended mascot.
Fully code-generated pixel art at 32x32 (exported 2x to 64x64).

Color palette: FlyShelf purple/indigo/lavender theme.
Style: Chibi pixel art — big head, small body, expressive.
"""
from __future__ import annotations
import math
from ..core.canvas import Canvas
from ..core.animation import Animation, Mascot, Timing
from ..core.palette import (
    TRANSPARENT, NEAR_BLACK, DARK_BG, MID_PURPLE, VIOLET, LAVENDER,
    LIGHT_LAVENDER, NEAR_WHITE, WHITE, INDIGO, AMBER, RED, GREEN, OUTLINE,
    FLYSHELF
)

# ─────────────────────────────────────────────────────────
# Kira color shortcuts
# ─────────────────────────────────────────────────────────
BODY_MAIN    = LAVENDER          # Main fur: lavender #A78BFA
BODY_DARK    = MID_PURPLE        # Shadow fur / inner ear
BODY_LIGHT   = LIGHT_LAVENDER    # Highlight fur
EYE_WHITE    = (240, 240, 255, 255)
EYE_DARK     = (30, 20, 50, 255)
EYE_SHINE    = WHITE
NOSE         = (200, 100, 140, 255)  # pink-purple nose
CHEEK        = (220, 160, 200, 80)   # subtle cheek blush (semi-transparent)
TAIL_TIP     = NEAR_WHITE
BELLY        = (215, 200, 245, 255)  # lighter belly
CLIPBOARD    = (200, 185, 240, 255)  # clipboard on back
CLIPBOARD_DK = INDIGO
BLACK        = OUTLINE


SIZE = 32  # Draw at 32x32, export at 2x = 64x64


def _make_canvas() -> Canvas:
    return Canvas(SIZE, SIZE, TRANSPARENT)


# ─────────────────────────────────────────────────────────
# Base body drawing helpers
# ─────────────────────────────────────────────────────────

def _draw_base_kira(c: Canvas, y_offset: int = 0) -> None:
    """
    Draw Kira's base form centered at bottom of 32x32 canvas.
    Chibi proportion: head 60% of height, body 40%.
    Draws at y + y_offset to animate up/down movement.
    """
    cx = SIZE // 2  # center x = 16

    # === HEAD (big chibi head) ===
    # Head is ~14px wide, ~13px tall, centered at (16, 10+offset)
    hy = 9 + y_offset
    c.draw_ellipse(cx, hy, 7, 6, BODY_MAIN, filled=True)
    # Outline
    c.draw_ellipse(cx, hy, 7, 6, BLACK, filled=False)

    # === EARS ===
    # Left ear
    c.draw_line(8,  hy - 5, 6,  hy - 10, BODY_MAIN)
    c.draw_line(9,  hy - 5, 7,  hy - 10, BODY_MAIN)
    c.draw_line(10, hy - 4, 8,  hy - 9,  BODY_MAIN)
    c.draw_line(8,  hy - 5, 6,  hy - 10, BLACK)  # outline
    c.draw_line(10, hy - 4, 8,  hy - 9,  BLACK)
    # Inner ear
    c.draw_line(9,  hy - 6, 7,  hy - 9,  BODY_DARK)
    # Right ear
    c.draw_line(22, hy - 5, 24, hy - 10, BODY_MAIN)
    c.draw_line(23, hy - 5, 25, hy - 10, BODY_MAIN)
    c.draw_line(24, hy - 4, 26, hy - 9,  BODY_MAIN)
    c.draw_line(22, hy - 5, 24, hy - 10, BLACK)
    c.draw_line(24, hy - 4, 26, hy - 9,  BLACK)
    c.draw_line(23, hy - 6, 25, hy - 9,  BODY_DARK)

    # === FACE DETAILS ===
    # Eyes (3x2 pixel eyes — big chibi eyes)
    # Left eye
    c.draw_rect(cx - 5, hy - 2, 3, 2, EYE_DARK, filled=True)
    c.put_pixel(cx - 5, hy - 2, EYE_SHINE)   # top-left shine
    # Right eye
    c.draw_rect(cx + 2, hy - 2, 3, 2, EYE_DARK, filled=True)
    c.put_pixel(cx + 2, hy - 2, EYE_SHINE)

    # Nose (1 pixel, pink)
    c.put_pixel(cx, hy + 1, NOSE)
    c.put_pixel(cx - 1, hy + 1, NOSE)

    # Mouth (tiny W shape)
    c.put_pixel(cx - 2, hy + 3, BLACK)
    c.put_pixel(cx - 1, hy + 2, BLACK)
    c.put_pixel(cx,     hy + 3, BLACK)
    c.put_pixel(cx + 1, hy + 2, BLACK)
    c.put_pixel(cx + 2, hy + 3, BLACK)

    # Cheek blush (subtle)
    c.put_pixel(cx - 4, hy + 1, CHEEK)
    c.put_pixel(cx - 5, hy + 2, CHEEK)
    c.put_pixel(cx + 4, hy + 1, CHEEK)
    c.put_pixel(cx + 5, hy + 2, CHEEK)

    # Forehead highlight
    c.put_pixel(cx - 2, hy - 4, BODY_LIGHT)
    c.put_pixel(cx - 1, hy - 5, BODY_LIGHT)

    # === BODY (small chibi body) ===
    by = hy + 7  # body center y
    c.draw_ellipse(cx, by, 5, 4, BODY_MAIN, filled=True)
    c.draw_ellipse(cx, by, 5, 4, BLACK, filled=False)
    # Belly
    c.draw_ellipse(cx, by + 1, 3, 2, BELLY, filled=True)

    # === LEGS ===
    # Two small rounded feet at the bottom
    c.draw_ellipse(cx - 3, by + 5, 3, 2, BODY_MAIN, filled=True)
    c.draw_ellipse(cx - 3, by + 5, 3, 2, BLACK, filled=False)
    c.draw_ellipse(cx + 3, by + 5, 3, 2, BODY_MAIN, filled=True)
    c.draw_ellipse(cx + 3, by + 5, 3, 2, BLACK, filled=False)

    # === TAIL ===
    # Curvy tail to the right
    c.draw_line(cx + 5, by,     cx + 9, by - 2, BODY_MAIN)
    c.draw_line(cx + 9, by - 2, cx + 11, by - 5, BODY_MAIN)
    c.draw_line(cx + 11, by - 5, cx + 10, by - 8, BODY_MAIN)
    # Tail tip (white)
    c.draw_circle(cx + 10, by - 8, 2, TAIL_TIP, filled=True)
    c.draw_circle(cx + 10, by - 8, 2, BLACK, filled=False)

    # === CLIPBOARD (backpack accessory) ===
    bx = cx - 9
    c.draw_rect(bx, by - 3, 4, 5, CLIPBOARD, filled=True)
    c.draw_rect(bx, by - 3, 4, 5, CLIPBOARD_DK, filled=False)
    # Clipboard clip
    c.draw_rect(bx + 1, by - 4, 2, 2, CLIPBOARD_DK, filled=True)
    # Lines on clipboard (content)
    c.draw_line(bx + 1, by - 1, bx + 2, by - 1, INDIGO)
    c.draw_line(bx + 1, by,     bx + 2, by,     INDIGO)


def _draw_walk_legs(c: Canvas, frame: int, y_offset: int = 0) -> None:
    """Draw walking legs — frame 0-5 cycle."""
    cx = SIZE // 2
    by = 16 + y_offset + 7  # body bottom y

    if frame % 2 == 0:
        # Left foot forward
        c.draw_ellipse(cx - 5, by + 5, 3, 2, BODY_MAIN, filled=True)
        c.draw_ellipse(cx - 5, by + 5, 3, 2, BLACK, filled=False)
        c.draw_ellipse(cx + 2, by + 4, 3, 2, BODY_MAIN, filled=True)
        c.draw_ellipse(cx + 2, by + 4, 3, 2, BLACK, filled=False)
    else:
        # Right foot forward
        c.draw_ellipse(cx - 2, by + 4, 3, 2, BODY_MAIN, filled=True)
        c.draw_ellipse(cx - 2, by + 4, 3, 2, BLACK, filled=False)
        c.draw_ellipse(cx + 4, by + 5, 3, 2, BODY_MAIN, filled=True)
        c.draw_ellipse(cx + 4, by + 5, 3, 2, BLACK, filled=False)


# ─────────────────────────────────────────────────────────
# Animation Generators
# ─────────────────────────────────────────────────────────

def make_idle() -> Animation:
    """4-frame gentle breathing idle."""
    anim = Animation("idle", loop=True)
    # Breathe: slightly up and down
    for y_off, dur in zip([0, -1, -1, 0], Timing.IDLE_4F):
        c = _make_canvas()
        _draw_base_kira(c, y_offset=y_off)
        anim.add_frame(c, dur)
    return anim


def make_walk() -> Animation:
    """6-frame walk cycle with bobbing head."""
    anim = Animation("walk", loop=True)
    y_bobs = [0, -1, 0, 0, -1, 0]
    for i in range(6):
        c = _make_canvas()
        y_off = y_bobs[i]
        _draw_base_kira(c, y_offset=y_off)
        # Clear default legs, draw walking legs
        cx = SIZE // 2
        by = 16 + y_off + 7
        # Erase default legs (overwrite with body color then redraw walking)
        c.draw_ellipse(cx - 3, by + 5, 3, 2, BODY_MAIN, filled=True)
        c.draw_ellipse(cx + 3, by + 5, 3, 2, BODY_MAIN, filled=True)
        _draw_walk_legs(c, frame=i, y_offset=y_off)
        anim.add_frame(c, Timing.WALK_6F[i])
    return anim


def make_run() -> Animation:
    """6-frame run — faster walk with bigger leg swing."""
    anim = Animation("run", loop=True)
    for i in range(6):
        c = _make_canvas()
        y_off = -1 if i in (1, 4) else 0
        _draw_base_kira(c, y_offset=y_off)
        cx = SIZE // 2
        by = 16 + y_off + 7
        c.draw_ellipse(cx - 3, by + 5, 3, 2, BODY_MAIN, filled=True)
        c.draw_ellipse(cx + 3, by + 5, 3, 2, BODY_MAIN, filled=True)
        _draw_walk_legs(c, frame=i, y_offset=y_off)
        anim.add_frame(c, Timing.RUN_6F[i])
    return anim


def make_fall() -> Animation:
    """2-frame falling — surprised expression, flailing."""
    anim = Animation("fall", loop=True)
    for i in range(2):
        c = _make_canvas()
        _draw_base_kira(c, y_offset=0)
        cx = SIZE // 2
        # Surprised eyes (bigger)
        c.draw_rect(cx - 5, 7, 4, 3, EYE_DARK, filled=True)
        c.draw_rect(cx + 1, 7, 4, 3, EYE_DARK, filled=True)
        c.put_pixel(cx - 5, 7, EYE_SHINE)
        c.put_pixel(cx + 1, 7, EYE_SHINE)
        # Open mouth (O shape)
        c.draw_circle(cx, 12, 2, BLACK, filled=False)
        # Flailing arms (alternating)
        if i == 0:
            c.draw_line(cx - 5, 17, cx - 8, 13, BODY_MAIN)
            c.draw_line(cx + 5, 17, cx + 8, 19, BODY_MAIN)
        else:
            c.draw_line(cx - 5, 17, cx - 8, 19, BODY_MAIN)
            c.draw_line(cx + 5, 17, cx + 8, 13, BODY_MAIN)
        anim.add_frame(c, 100)
    return anim


def make_land() -> Animation:
    """3-frame landing — impact squash then settle."""
    anim = Animation("land", loop=False)

    # Frame 0: HARD squash on impact
    c0 = _make_canvas()
    _draw_base_kira(c0, y_offset=2)
    squashed = c0.squash(3)
    anim.add_frame(squashed, 40)

    # Frame 1: Recover — slightly stretched
    c1 = _make_canvas()
    _draw_base_kira(c1, y_offset=0)
    stretched = c1.stretch(1)
    anim.add_frame(stretched, 80)

    # Frame 2: Settle back to normal
    c2 = _make_canvas()
    _draw_base_kira(c2, y_offset=0)
    anim.add_frame(c2, 120)

    return anim


def make_sleep() -> Animation:
    """3-frame sleep with ZZZ particles."""
    anim = Animation("sleep", loop=True)

    for i in range(3):
        c = _make_canvas()
        # Draw kira sitting/slouched
        _draw_base_kira(c, y_offset=1)
        cx = SIZE // 2

        # Closed eyes (curved lines)
        c.draw_rect(cx - 5, 7, 3, 1, BLACK, filled=True)
        c.draw_rect(cx + 2, 7, 3, 1, BLACK, filled=True)

        # ZZZ particles at different positions per frame
        zx = cx + 6 + i * 3
        zy = 5 - i * 2
        c.put_pixel(zx,     zy,     INDIGO)
        c.put_pixel(zx + 1, zy - 1, INDIGO)
        c.put_pixel(zx + 2, zy,     INDIGO)
        if i >= 1:
            # Second Z
            c.put_pixel(zx - 2, zy + 3, LIGHT_LAVENDER)
            c.put_pixel(zx - 1, zy + 2, LIGHT_LAVENDER)
            c.put_pixel(zx,     zy + 3, LIGHT_LAVENDER)

        anim.add_frame(c, Timing.SLEEP_3F[i])

    return anim


def make_react() -> Animation:
    """4-frame happy reaction — bounce + sparkles. For clipboard copy events."""
    anim = Animation("react", loop=False)

    # Frame 0: anticipation — slight squat
    c0 = _make_canvas()
    _draw_base_kira(c0, y_offset=1)
    anim.add_frame(c0.squash(1), 50)

    # Frame 1: launch up — stretch
    c1 = _make_canvas()
    _draw_base_kira(c1, y_offset=-2)
    anim.add_frame(c1.stretch(2), 60)

    # Frame 2: peak — happy face (^_^ eyes)
    c2 = _make_canvas()
    _draw_base_kira(c2, y_offset=-3)
    cx = SIZE // 2
    # Overwrite eyes with happy ^ shape
    c2.draw_line(cx - 6, 8, cx - 4, 6, BLACK)
    c2.draw_line(cx + 3, 6, cx + 5, 8, BLACK)
    # Sparkles
    for sx, sy in [(cx + 8, 3), (cx - 8, 4), (cx, 0)]:
        if 0 <= sx < SIZE and 0 <= sy < SIZE:
            c2.put_pixel(sx, sy, AMBER)
            if sx > 0: c2.put_pixel(sx - 1, sy, AMBER)
            if sx < SIZE - 1: c2.put_pixel(sx + 1, sy, AMBER)
    anim.add_frame(c2, 100)

    # Frame 3: land — squash settle
    c3 = _make_canvas()
    _draw_base_kira(c3, y_offset=0)
    anim.add_frame(c3.squash(2), 150)

    return anim


def make_sad() -> Animation:
    """4-frame sad droopy animation. For clipboard delete events."""
    anim = Animation("sad", loop=False)

    for i, (y_off, dur) in enumerate(zip([0, 1, 1, 2], Timing.SAD_4F)):
        c = _make_canvas()
        _draw_base_kira(c, y_offset=y_off)
        cx = SIZE // 2
        hy = 9 + y_off

        # Sad eyes (downward curved)
        c.draw_line(cx - 6, hy - 1, cx - 4, hy - 2, BLACK)
        c.draw_line(cx + 3, hy - 2, cx + 5, hy - 1, BLACK)

        # Sad mouth (downward curve)
        c.put_pixel(cx - 2, hy + 3, BLACK)
        c.put_pixel(cx - 1, hy + 4, BLACK)
        c.put_pixel(cx,     hy + 4, BLACK)
        c.put_pixel(cx + 1, hy + 4, BLACK)
        c.put_pixel(cx + 2, hy + 3, BLACK)

        # Tears (frame 2+)
        if i >= 2:
            c.put_pixel(cx - 5, hy + 2, (100, 160, 255, 200))
            c.put_pixel(cx - 5, hy + 3, (100, 160, 255, 200))

        anim.add_frame(c, dur)

    return anim


def make_think() -> Animation:
    """3-frame thinking animation — paw to chin."""
    anim = Animation("think", loop=True)

    for i in range(3):
        c = _make_canvas()
        _draw_base_kira(c, y_offset=0)
        cx = SIZE // 2
        hy = 9

        # Thinking eyes (looking sideways)
        off = [0, 1, 1][i]
        c.put_pixel(cx - 5 + off, hy - 1, EYE_DARK)
        c.put_pixel(cx + 2 + off, hy - 1, EYE_DARK)

        # Question mark at top-right (frame 1+)
        if i >= 1:
            c.put_pixel(cx + 9, 2, INDIGO)
            c.put_pixel(cx + 9, 4, INDIGO)
            c.put_pixel(cx + 10, 1, INDIGO)
            c.put_pixel(cx + 11, 2, INDIGO)
            c.put_pixel(cx + 10, 3, INDIGO)

        anim.add_frame(c, Timing.THINK_3F[i])

    return anim


def make_search() -> Animation:
    """4-frame searching — head looks left and right."""
    anim = Animation("search", loop=True)
    turns = [-2, 0, 2, 0]   # head tilt offset

    for i, x_off in enumerate(turns):
        c = _make_canvas()
        _draw_base_kira(c, y_offset=0)
        cx = SIZE // 2
        hy = 9

        # Shift eyes to simulate looking direction
        # Left eye
        c.draw_rect(cx - 5 + x_off, hy - 2, 3, 2, EYE_DARK, filled=True)
        c.put_pixel(cx - 5 + x_off, hy - 2, EYE_SHINE)
        # Right eye
        c.draw_rect(cx + 2 + x_off, hy - 2, 3, 2, EYE_DARK, filled=True)
        c.put_pixel(cx + 2 + x_off, hy - 2, EYE_SHINE)

        # Magnifying glass (on frame 1 and 3)
        if i in (1, 3):
            gx, gy = cx + 10, hy - 3
            c.draw_circle(gx, gy, 3, NEAR_WHITE, filled=False)
            c.draw_line(gx + 2, gy + 2, gx + 5, gy + 5, NEAR_WHITE)

        anim.add_frame(c, Timing.SEARCH_4F[i])

    return anim


def make_dance() -> Animation:
    """6-frame happy dance. Random rare animation."""
    anim = Animation("dance", loop=False)

    for i in range(6):
        c = _make_canvas()
        y_off = [-1, -2, -1, 0, -1, -2][i]
        _draw_base_kira(c, y_offset=y_off)
        cx = SIZE // 2
        hy = 9 + y_off

        # Happy eyes on dance frames
        c.draw_line(cx - 6, hy - 1, cx - 4, hy - 3, BLACK)
        c.draw_line(cx + 3, hy - 3, cx + 5, hy - 1, BLACK)

        # Music notes
        if i % 2 == 0:
            c.put_pixel(cx + 10, 1, AMBER)
            c.put_pixel(cx + 11, 2, AMBER)
            c.put_pixel(cx + 10, 3, AMBER)
        else:
            c.put_pixel(cx - 11, 2, VIOLET)
            c.put_pixel(cx - 10, 1, VIOLET)
            c.put_pixel(cx - 11, 3, VIOLET)

        anim.add_frame(c, Timing.DANCE_8F[i % 8])

    return anim


def make_drag() -> Animation:
    """1-frame drag — dangling expression."""
    anim = Animation("drag", loop=True)
    c = _make_canvas()
    _draw_base_kira(c, y_offset=0)
    cx = SIZE // 2
    hy = 9
    # X eyes for dramatic effect
    c.draw_line(cx - 6, hy - 3, cx - 4, hy - 1, BLACK)
    c.draw_line(cx - 6, hy - 1, cx - 4, hy - 3, BLACK)
    c.draw_line(cx + 2, hy - 3, cx + 4, hy - 1, BLACK)
    c.draw_line(cx + 2, hy - 1, cx + 4, hy - 3, BLACK)
    anim.add_frame(c, 100)
    return anim


# ─────────────────────────────────────────────────────────
# Main mascot factory
# ─────────────────────────────────────────────────────────

def create_kira() -> Mascot:
    """
    Create the complete Kira Fox mascot with all animation states.

    Usage:
        mascot = create_kira()
        from sprite_engine.core.exporter import Exporter
        Exporter.export_flyshelf(mascot, r"path/to/themes/kira/", scale=2)
    """
    mascot = Mascot("kira", SIZE, SIZE)

    print("🦊 Generating Kira the Fox mascot...")
    for name, factory in [
        ("idle",   make_idle),
        ("walk",   make_walk),
        ("run",    make_run),
        ("fall",   make_fall),
        ("land",   make_land),
        ("sleep",  make_sleep),
        ("react",  make_react),
        ("sad",    make_sad),
        ("think",  make_think),
        ("search", make_search),
        ("dance",  make_dance),
        ("drag",   make_drag),
    ]:
        try:
            anim = factory()
            mascot.add(anim)
            print(f"  ✓ {name}: {len(anim)} frames, {anim.total_duration_ms()}ms")
        except Exception as e:
            print(f"  ✗ {name}: {e}")

    return mascot
