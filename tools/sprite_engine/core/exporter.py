"""
sprite_engine/core/exporter.py
Multi-format export pipeline: GIF, PNG spritesheet, APNG, and FlyShelf theme folder.
"""
from __future__ import annotations
import os
import json
from typing import List, Dict, Optional
from PIL import Image

from .animation import Mascot, Animation


class Exporter:
    """Export animations to various formats ready for use in FlyShelf."""

    # ─────────────────────────────────────────────
    # GIF Export
    # ─────────────────────────────────────────────

    @staticmethod
    def export_gif(animation: Animation, path: str, scale: int = 1) -> str:
        """
        Export a single animation as an animated GIF.
        Uses per-frame timing for perfection.
        Returns the output path.
        """
        if not animation.frames:
            raise ValueError(f"Animation '{animation.name}' has no frames")

        os.makedirs(os.path.dirname(path) or ".", exist_ok=True)

        frames_pil: List[Image.Image] = []
        durations: List[int] = []

        for frame in animation.frames:
            img = frame.canvas.to_pil()
            if scale > 1:
                img = img.resize(
                    (img.width * scale, img.height * scale),
                    Image.NEAREST
                )
            # Convert to RGBA for GIF (with palette quantization)
            frames_pil.append(img)
            durations.append(frame.duration_ms)

        # Save animated GIF
        # Convert each frame to palette mode for GIF compatibility
        gif_frames = []
        for img in frames_pil:
            # FASTOCTREE is the only quantize method supporting RGBA in Pillow
            palette_img = img.quantize(
                colors=256, method=Image.Quantize.FASTOCTREE, dither=0
            )
            gif_frames.append(palette_img)

        gif_frames[0].save(
            path,
            format="GIF",
            save_all=True,
            append_images=gif_frames[1:],
            duration=durations,
            loop=0 if animation.loop else 1,
            disposal=2,  # Clear frame before next (important for transparency)
        )
        print(f"  [OK] GIF: {path} ({len(animation.frames)} frames)")
        return path

    # ─────────────────────────────────────────────
    # PNG Spritesheet Export
    # ─────────────────────────────────────────────

    @staticmethod
    def export_spritesheet(
        animation: Animation,
        path: str,
        scale: int = 1,
        export_json: bool = True,
    ) -> Dict:
        """
        Export animation as a horizontal PNG spritesheet + JSON atlas.
        Standard format for game engines and LibreSprite import.
        """
        if not animation.frames:
            raise ValueError("No frames")

        os.makedirs(os.path.dirname(path) or ".", exist_ok=True)

        w = animation.frames[0].canvas.width * scale
        h = animation.frames[0].canvas.height * scale
        n = len(animation.frames)

        sheet = Image.new("RGBA", (w * n, h), (0, 0, 0, 0))
        atlas = {"frames": [], "meta": {"name": animation.name, "loop": animation.loop}}

        for i, frame in enumerate(animation.frames):
            img = frame.canvas.to_pil()
            if scale > 1:
                img = img.resize((w, h), Image.NEAREST)
            sheet.paste(img, (i * w, 0))
            atlas["frames"].append({
                "frame": {"x": i * w, "y": 0, "w": w, "h": h},
                "duration": frame.duration_ms,
                "filename": f"{animation.name}_{i:02d}.png",
            })

        sheet.save(path)

        if export_json:
            json_path = path.rsplit(".", 1)[0] + ".json"
            with open(json_path, "w") as f:
                json.dump(atlas, f, indent=2)
            print(f"  ✅ Spritesheet: {path} + {json_path}")
        else:
            print(f"  ✅ Spritesheet: {path}")

        return atlas

    # ─────────────────────────────────────────────
    # APNG Export (better quality than GIF)
    # ─────────────────────────────────────────────

    @staticmethod
    def export_apng(animation: Animation, path: str, scale: int = 1) -> str:
        """
        Export as APNG (Animated PNG) — full 24-bit color, proper transparency.
        Better quality than GIF; supported by modern WPF libraries.
        """
        if not animation.frames:
            raise ValueError("No frames")

        os.makedirs(os.path.dirname(path) or ".", exist_ok=True)

        frames_pil = []
        for frame in animation.frames:
            img = frame.canvas.to_pil()
            if scale > 1:
                img = img.resize(
                    (img.width * scale, img.height * scale), Image.NEAREST
                )
            frames_pil.append(img)

        # Save APNG — Pillow 9.4+ supports APNG natively
        durations = [f.duration_ms for f in animation.frames]
        frames_pil[0].save(
            path,
            format="PNG",
            save_all=True,
            append_images=frames_pil[1:],
            duration=durations,
            loop=0 if animation.loop else 1,
        )
        print(f"  ✅ APNG: {path}")
        return path

    # ─────────────────────────────────────────────
    # FlyShelf Theme Export
    # ─────────────────────────────────────────────

    @staticmethod
    def export_flyshelf(
        mascot: Mascot,
        theme_dir: str,
        scale: int = 2,
        format: str = "gif",
    ) -> Dict[str, str]:
        """
        Export all mascot states to a FlyShelf theme folder.

        Output structure:
          themes/[mascot-name]/sprites/
            idle.gif
            running.gif  (or walk.gif)
            falling.gif
            copy.gif     (= react/happy)
            delete.gif   (= sad)
            search.gif
            sleep.gif
            wake.gif
            think.gif
            dance.gif

        Returns dict of {state: output_path}
        """
        sprites_dir = os.path.join(theme_dir, "sprites")
        os.makedirs(sprites_dir, exist_ok=True)

        # State name aliases — what FlyShelf calls each state
        aliases = {
            "idle":    "idle",
            "walk":    "running",  # FlyShelf uses 'running.gif'
            "run":     "running",
            "fall":    "falling",
            "land":    "land",
            "sleep":   "sleep",
            "wake":    "wake",
            "react":   "copy",     # FlyShelf: copy = happy reaction
            "sad":     "delete",   # FlyShelf: delete = sad reaction
            "think":   "think",
            "search":  "search",
            "drag":    "drag",
            "dance":   "dance",
        }

        outputs = {}

        for state_name, anim in mascot.animations.items():
            out_name = aliases.get(state_name, state_name)
            out_path = os.path.join(sprites_dir, f"{out_name}.{format}")

            try:
                if format == "gif":
                    Exporter.export_gif(anim, out_path, scale=scale)
                elif format == "png":
                    Exporter.export_spritesheet(anim, out_path, scale=scale)
                elif format == "apng":
                    Exporter.export_apng(anim, out_path, scale=scale)
                outputs[state_name] = out_path
            except Exception as e:
                print(f"  ⚠️  Skipping '{state_name}': {e}")

        # Write mascot metadata
        meta = {
            "name": mascot.name,
            "width": mascot.width * scale,
            "height": mascot.height * scale,
            "states": list(mascot.animations.keys()),
            "format": format,
            "scale": scale,
        }
        meta_path = os.path.join(theme_dir, "mascot.json")
        with open(meta_path, "w") as f:
            json.dump(meta, f, indent=2)

        print(f"\n🎉 FlyShelf export complete → {theme_dir}")
        print(f"   States: {list(outputs.keys())}")
        return outputs

    # ─────────────────────────────────────────────
    # Individual PNG frames
    # ─────────────────────────────────────────────

    @staticmethod
    def export_frames(animation: Animation, directory: str, scale: int = 1) -> List[str]:
        """Export each frame as an individual PNG file."""
        os.makedirs(directory, exist_ok=True)
        paths = []
        for i, frame in enumerate(animation.frames):
            path = os.path.join(directory, f"{animation.name}_{i:03d}.png")
            img = frame.canvas.to_pil()
            if scale > 1:
                img = img.resize(
                    (img.width * scale, img.height * scale), Image.NEAREST
                )
            img.save(path)
            paths.append(path)
        print(f"  ✅ Frames: {directory} ({len(paths)} PNGs)")
        return paths
