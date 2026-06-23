"""
sprite_engine/core/animation.py
Frame-based animation system with per-frame timing.
Implements proper animation principles: variable timing, tags, loops.
"""
from __future__ import annotations
from typing import List, Optional, Tuple
from dataclasses import dataclass, field
from .canvas import Canvas


@dataclass
class Frame:
    canvas: Canvas
    duration_ms: int = 100  # Per-frame duration in milliseconds

    def clone(self) -> "Frame":
        return Frame(self.canvas.clone(), self.duration_ms)


class Animation:
    """
    A named sequence of frames with per-frame timing.
    Implements the core of animation principle: variable timing.
    """

    def __init__(self, name: str, loop: bool = True):
        self.name = name
        self.loop = loop
        self.frames: List[Frame] = []

    def add_frame(self, canvas: Canvas, duration_ms: int = 100) -> "Animation":
        """Add a frame. Return self for chaining."""
        self.frames.append(Frame(canvas.clone(), duration_ms))
        return self

    def add_squash_frame(self, canvas: Canvas, amount: int = 2, duration_ms: int = 60) -> "Animation":
        """Add a squash (landing/impact) frame automatically."""
        self.frames.append(Frame(canvas.squash(amount), duration_ms))
        return self

    def add_stretch_frame(self, canvas: Canvas, amount: int = 2, duration_ms: int = 50) -> "Animation":
        """Add a stretch (jump/anticipation) frame."""
        self.frames.append(Frame(canvas.stretch(amount), duration_ms))
        return self

    def clone_reversed(self) -> "Animation":
        """Return a reversed copy — useful for boomerang/ping-pong loops."""
        anim = Animation(self.name + "_rev", self.loop)
        anim.frames = [f.clone() for f in reversed(self.frames)]
        return anim

    def make_pingpong(self) -> "Animation":
        """Extend with reversed frames for seamless ping-pong loop."""
        anim = Animation(self.name, self.loop)
        anim.frames = [f.clone() for f in self.frames]
        if len(self.frames) > 2:
            for f in reversed(self.frames[1:-1]):
                anim.frames.append(f.clone())
        return anim

    def set_uniform_timing(self, duration_ms: int) -> None:
        for f in self.frames:
            f.duration_ms = duration_ms

    def set_timing(self, durations: List[int]) -> None:
        """Set per-frame durations. Will cycle if shorter than frame count."""
        for i, f in enumerate(self.frames):
            f.duration_ms = durations[i % len(durations)]

    def total_duration_ms(self) -> int:
        return sum(f.duration_ms for f in self.frames)

    def __len__(self) -> int:
        return len(self.frames)

    def __repr__(self) -> str:
        return f"Animation('{self.name}', {len(self.frames)} frames, {self.total_duration_ms()}ms)"


class Mascot:
    """
    A collection of named animations for a single character.
    Manages all states: idle, walk, sleep, react, sad, etc.
    """

    # Standard FlyShelf mascot animation states
    STANDARD_STATES = [
        "idle",    # Default breathing loop
        "walk",    # Walking/patrol cycle
        "run",     # Run (faster walk)
        "fall",    # Falling (used while in air)
        "land",    # Landing impact (one-shot)
        "sleep",   # Sleep ZZZ loop
        "wake",    # Wake up (one-shot)
        "react",   # Happy reaction — item copied (one-shot)
        "sad",     # Sad reaction — item deleted (one-shot)
        "think",   # Thinking — AI processing (loop)
        "search",  # Searching look — search bar (loop)
        "drag",    # Being dragged by user
        "dance",   # Rare fun animation (one-shot)
    ]

    def __init__(self, name: str, width: int, height: int):
        self.name = name
        self.width = width
        self.height = height
        self.animations: dict[str, Animation] = {}

    def add(self, anim: Animation) -> "Mascot":
        self.animations[anim.name] = anim
        return self

    def get(self, state: str) -> Optional[Animation]:
        # Return exact match or fall back to 'idle'
        return self.animations.get(state) or self.animations.get("idle")

    def states(self) -> List[str]:
        return list(self.animations.keys())

    def __repr__(self) -> str:
        return f"Mascot('{self.name}', states={self.states()})"


# ─────────────────────────────────────────────────────────
# Standard timing presets (in milliseconds per frame)
# Implements variable timing for animation perfection
# ─────────────────────────────────────────────────────────

class Timing:
    """Pre-defined timing sequences for common animations."""

    # Idle breathing — slow, relaxed
    IDLE_4F  = [150, 150, 200, 150]       # 4-frame idle
    IDLE_6F  = [150, 150, 200, 150, 150, 300]  # 6-frame idle with hold

    # Walk cycle — medium pace, hold on contact frames
    WALK_6F  = [80, 60, 50, 80, 60, 50]
    WALK_8F  = [80, 60, 50, 40, 80, 60, 50, 40]

    # Run — fast with quick transitions
    RUN_6F   = [50, 40, 35, 50, 40, 35]
    RUN_8F   = [50, 40, 35, 30, 50, 40, 35, 30]

    # Reaction — starts fast, settles slowly
    REACT_4F = [50, 80, 100, 150]         # Fast pop, slow settle
    REACT_5F = [40, 60, 80, 120, 180]

    # Sad — starts slow, gets slower (droopy)
    SAD_4F   = [80, 100, 150, 200]
    SAD_5F   = [80, 100, 120, 150, 250]

    # Sleep — very slow, peaceful
    SLEEP_2F = [400, 600]
    SLEEP_3F = [300, 400, 600]

    # Landing — impact frames are short, settle is longer
    LAND_3F  = [40, 80, 120]             # Impact, recover, hold
    LAND_4F  = [35, 60, 100, 150]

    # Search — steady scanning motion
    SEARCH_4F = [120, 80, 120, 80]
    SEARCH_6F = [120, 80, 60, 120, 80, 60]

    # Think — slow thoughtful motion
    THINK_3F = [200, 300, 200]
    THINK_4F = [150, 200, 300, 200]

    # Dance — energetic, variable
    DANCE_8F = [60, 50, 60, 80, 60, 50, 60, 100]
