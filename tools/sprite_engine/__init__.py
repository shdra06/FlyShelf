"""
sprite_engine — AI-powered sprite generator for LibreSprite/FlyShelf.

Public API:
    from sprite_engine import Canvas, Animation, Mascot, Exporter
    from sprite_engine.mascots.fox import create_kira
    from sprite_engine.core.palette import FLYSHELF, get_palette
"""
from .core.canvas import Canvas
from .core.animation import Animation, Mascot, Frame, Timing
from .core.palette import FLYSHELF, get_palette, RGBA
from .core.exporter import Exporter

__version__ = "1.0.0"
__all__ = [
    "Canvas", "Animation", "Mascot", "Frame", "Timing",
    "FLYSHELF", "get_palette", "RGBA",
    "Exporter",
]
