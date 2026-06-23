"""
sprite_engine/libresprite/bridge.py
Python ↔ LibreSprite bridge.
Sends commands to LibreSprite via:
  1. CLI: libresprite --batch --script ai_bridge.js
  2. JSON queue file (watched by the ai_bridge.js plugin)
"""
from __future__ import annotations
import os
import json
import subprocess
import tempfile
import time
from typing import List, Dict, Any, Optional

QUEUE_FILE  = os.path.join(tempfile.gettempdir(), "sprite_engine_queue.json")
RESULT_FILE = os.path.join(tempfile.gettempdir(), "sprite_engine_result.json")


class LibreSpriteCommand:
    """Represents a single drawing command for LibreSprite."""

    @staticmethod
    def put_pixel(x: int, y: int, r: int, g: int, b: int, a: int = 255) -> Dict:
        return {"op": "put_pixel", "x": x, "y": y, "r": r, "g": g, "b": b, "a": a}

    @staticmethod
    def draw_rect(x: int, y: int, w: int, h: int, r: int, g: int, b: int,
                  a: int = 255, filled: bool = False) -> Dict:
        return {"op": "draw_rect", "x": x, "y": y, "w": w, "h": h,
                "r": r, "g": g, "b": b, "a": a, "filled": filled}

    @staticmethod
    def clear(r: int = 0, g: int = 0, b: int = 0, a: int = 0) -> Dict:
        return {"op": "clear", "color": {"r": r, "g": g, "b": b, "a": a}}

    @staticmethod
    def save(path: str) -> Dict:
        return {"op": "save", "path": path}

    @staticmethod
    def resize(width: int, height: int) -> Dict:
        return {"op": "resize", "width": width, "height": height}


class LibreSpritebridge:
    """
    Bridge between Python sprite_engine and LibreSprite application.

    Usage:
        bridge = LibreSpritebridge(exe_path=r"C:\Program Files\LibreSprite\LibreSprite.exe")
        bridge.open_file("my_sprite.aseprite")  # Open in GUI
        bridge.run_commands([                    # Execute via CLI
            LibreSpriteCommand.put_pixel(10, 10, 255, 0, 0),
            LibreSpriteCommand.save("output.png"),
        ], sprite_file="base.aseprite")
    """

    def __init__(self, exe_path: Optional[str] = None):
        self.exe = exe_path or self._find_libresprite()
        self.script_path = os.path.join(
            os.path.dirname(__file__), "ai_bridge.js"
        )

    def _find_libresprite(self) -> str:
        """Auto-detect LibreSprite installation."""
        candidates = [
            r"C:\Program Files\LibreSprite\LibreSprite.exe",
            r"C:\Program Files (x86)\LibreSprite\LibreSprite.exe",
            os.path.expanduser("~/AppData/Local/LibreSprite/LibreSprite.exe"),
            "/usr/bin/libresprite",
            "/usr/local/bin/libresprite",
            "/Applications/LibreSprite.app/Contents/MacOS/LibreSprite",
            "libresprite",  # If it's on PATH
        ]
        for path in candidates:
            if os.path.isfile(path):
                return path
        return "libresprite"  # Fall back to PATH

    def is_available(self) -> bool:
        """Check if LibreSprite executable is accessible."""
        try:
            subprocess.run(
                [self.exe, "--version"],
                capture_output=True, timeout=5
            )
            return True
        except (FileNotFoundError, subprocess.TimeoutExpired):
            return False

    def run_commands(
        self,
        commands: List[Dict[str, Any]],
        sprite_file: Optional[str] = None,
        sprite_width: int = 64,
        sprite_height: int = 64,
        timeout: int = 30,
    ) -> Dict:
        """
        Execute drawing commands in LibreSprite via CLI batch mode.
        Writes JSON queue, runs LibreSprite --batch --script, reads result.
        """
        # Write command queue
        queue = {
            "sprite_width":  sprite_width,
            "sprite_height": sprite_height,
            "commands": commands,
        }
        with open(QUEUE_FILE, "w") as f:
            json.dump(queue, f, indent=2)

        # Remove old result
        if os.path.exists(RESULT_FILE):
            os.remove(RESULT_FILE)

        # Build CLI args
        args = [self.exe, "--batch"]
        if sprite_file and os.path.isfile(sprite_file):
            args.append(sprite_file)
        args += ["--script", self.script_path]

        print(f"🎨 Running LibreSprite: {' '.join(args)}")
        try:
            result = subprocess.run(
                args,
                capture_output=True,
                text=True,
                timeout=timeout,
            )
            print(f"   stdout: {result.stdout[:200]}")
            if result.returncode != 0:
                print(f"   stderr: {result.stderr[:200]}")
        except subprocess.TimeoutExpired:
            return {"status": "timeout", "commands_executed": 0}
        except FileNotFoundError:
            return {"status": "not_found", "error": f"LibreSprite not found at: {self.exe}"}

        # Read results
        if os.path.exists(RESULT_FILE):
            with open(RESULT_FILE) as f:
                return json.load(f)
        return {"status": "no_result"}

    def open_file(self, path: str) -> None:
        """Open a file in LibreSprite GUI (non-blocking)."""
        subprocess.Popen([self.exe, path])
        print(f"✅ Opened in LibreSprite: {path}")

    def export_cli(
        self,
        input_path: str,
        output_path: str,
        scale: int = 1,
        sheet: bool = False,
    ) -> bool:
        """
        Use LibreSprite CLI to export a sprite to PNG/GIF/etc.
        (Works only if LibreSprite supports --batch mode)
        """
        args = [self.exe, "--batch", input_path]
        if scale > 1:
            args += ["--scale", str(scale)]
        if sheet:
            json_path = output_path.rsplit(".", 1)[0] + ".json"
            args += ["--sheet", output_path, "--data", json_path]
        else:
            args += ["--save-as", output_path]

        try:
            result = subprocess.run(args, capture_output=True, text=True, timeout=30)
            if result.returncode == 0:
                print(f"✅ Exported: {output_path}")
                return True
            print(f"❌ Export failed: {result.stderr}")
            return False
        except Exception as e:
            print(f"❌ Export error: {e}")
            return False

    def install_plugin(self, libresprite_install_dir: str) -> bool:
        """Copy ai_bridge.js to LibreSprite's scripts folder."""
        scripts_dir = os.path.join(libresprite_install_dir, "data", "scripts")
        if not os.path.isdir(scripts_dir):
            print(f"❌ Scripts dir not found: {scripts_dir}")
            return False
        import shutil
        dst = os.path.join(scripts_dir, "ai_bridge.js")
        shutil.copy(self.script_path, dst)
        print(f"✅ Plugin installed: {dst}")
        print(f"   Restart LibreSprite, then: Scripts > Run 'ai_bridge.js'")
        return True
