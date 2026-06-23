"""
sprite_engine/core/palette.py
Color palette management — FlyShelf theme colors + pixel art helpers.
"""
from __future__ import annotations
from typing import List, Tuple, Dict

RGBA = Tuple[int, int, int, int]

# ─────────────────────────────────────────────────────────
# Built-in Palettes
# ─────────────────────────────────────────────────────────

class Palette:
    def __init__(self, name: str, colors: List[RGBA]):
        self.name = name
        self.colors = colors

    def __getitem__(self, key: str | int) -> RGBA:
        if isinstance(key, int):
            return self.colors[key]
        return getattr(self, key)

    def closest(self, r: int, g: int, b: int) -> RGBA:
        """Return the closest color in this palette (Euclidean RGB distance)."""
        best = self.colors[0]
        best_d = float("inf")
        for c in self.colors:
            d = (c[0]-r)**2 + (c[1]-g)**2 + (c[2]-b)**2
            if d < best_d:
                best_d = d
                best = c
        return best

    def __len__(self) -> int:
        return len(self.colors)


# ─────────────────────────────────────────────────────────
# FlyShelf — matches the app's purple/indigo theme
# ─────────────────────────────────────────────────────────
FLYSHELF = Palette("flyshelf", [
    (0,   0,   0,   0),    # [0]  transparent
    (10,  8,   18,  255),  # [1]  near-black (darkest bg)
    (30,  25,  50,  255),  # [2]  dark purple bg
    (50,  40,  85,  255),  # [3]  mid-dark purple
    (99,  78,  168, 255),  # [4]  purple mid
    (139, 92,  246, 255),  # [5]  violet accent (#8B5CF6)
    (167, 139, 250, 255),  # [6]  lavender (#A78BFA)
    (196, 181, 253, 255),  # [7]  light lavender
    (232, 225, 255, 255),  # [8]  near-white lavender
    (255, 255, 255, 255),  # [9]  white
    (99,  102, 241, 255),  # [10] indigo (#6366F1)
    (79,  70,  229, 255),  # [11] deeper indigo
    (237, 233, 254, 255),  # [12] very light purple
    (251, 191, 36,  255),  # [13] amber (accent, star/sparkle)
    (239, 68,  68,  255),  # [14] red (error/sad)
    (34,  197, 94,  255),  # [15] green (success/happy)
])

# Named shortcuts for FlyShelf palette
TRANSPARENT    = FLYSHELF.colors[0]
NEAR_BLACK     = FLYSHELF.colors[1]
DARK_BG        = FLYSHELF.colors[2]
MID_PURPLE     = FLYSHELF.colors[4]
VIOLET         = FLYSHELF.colors[5]
LAVENDER       = FLYSHELF.colors[6]
LIGHT_LAVENDER = FLYSHELF.colors[7]
NEAR_WHITE     = FLYSHELF.colors[8]
WHITE          = FLYSHELF.colors[9]
INDIGO         = FLYSHELF.colors[10]
AMBER          = FLYSHELF.colors[13]
RED            = FLYSHELF.colors[14]
GREEN          = FLYSHELF.colors[15]
OUTLINE        = (15, 12, 30, 255)    # Very dark purple outline

# ─────────────────────────────────────────────────────────
# Monokai — classic dark coding theme
# ─────────────────────────────────────────────────────────
MONOKAI = Palette("monokai", [
    (0,   0,   0,   0),
    (39,  40,  34,  255),  # bg
    (102, 217, 239, 255),  # cyan
    (166, 226, 46,  255),  # green
    (249, 38,  114, 255),  # pink/red
    (253, 151, 31,  255),  # orange
    (174, 129, 255, 255),  # purple
    (255, 255, 255, 255),  # white
    (117, 113, 94,  255),  # comment grey
    (248, 248, 242, 255),  # near-white fg
])

# ─────────────────────────────────────────────────────────
# Soft Pastel — cute/kawaii pet style
# ─────────────────────────────────────────────────────────
PASTEL = Palette("pastel", [
    (0,   0,   0,   0),
    (255, 182, 193, 255),  # light pink
    (255, 218, 185, 255),  # peach
    (255, 255, 186, 255),  # light yellow
    (186, 255, 201, 255),  # mint
    (186, 225, 255, 255),  # sky blue
    (230, 190, 255, 255),  # lavender
    (255, 255, 255, 255),  # white
    (80,  50,  60,  255),  # dark outline
    (200, 150, 160, 255),  # shadow pink
    (160, 130, 170, 255),  # mid shadow
])

# ─────────────────────────────────────────────────────────
# NES — 8 colors max, retro look
# ─────────────────────────────────────────────────────────
NES = Palette("nes", [
    (0,   0,   0,   0),
    (0,   0,   0,   255),
    (108, 108, 108, 255),
    (188, 188, 188, 255),
    (255, 255, 255, 255),
    (248, 56,  0,   255),
    (88,  216, 84,  255),
    (0,   88,  248, 255),
    (248, 120, 248, 255),
    (0,   232, 216, 255),
    (248, 216, 0,   255),
])

# ─────────────────────────────────────────────────────────
# Registry
# ─────────────────────────────────────────────────────────
PALETTES: Dict[str, Palette] = {
    "flyshelf": FLYSHELF,
    "monokai":  MONOKAI,
    "pastel":   PASTEL,
    "nes":      NES,
}

def get_palette(name: str) -> Palette:
    p = PALETTES.get(name.lower())
    if p is None:
        raise ValueError(f"Unknown palette '{name}'. Available: {list(PALETTES.keys())}")
    return p
