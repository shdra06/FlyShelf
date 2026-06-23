#!/usr/bin/env python3
"""
sprite_engine/__main__.py
CLI entry point — AI agents use this to generate sprites from the command line.

Usage:
    python -m sprite_engine kira --output ./output/kira --scale 2
    python -m sprite_engine kira --flyshelf --theme-dir e:/exeapps/FlyShelf/FlyShelf_PC/Resources/Mascot/Kira
    python -m sprite_engine demo --output ./output/demo.gif
    python -m sprite_engine palettes
"""
import sys
import os
import argparse


def cmd_kira(args):
    """Generate Kira the Fox mascot."""
    from sprite_engine.mascots.fox import create_kira
    from sprite_engine.core.exporter import Exporter

    mascot = create_kira()

    if args.flyshelf:
        # Export to FlyShelf theme folder
        theme_dir = args.theme_dir or os.path.join(
            os.path.dirname(__file__), "..", "..", "..",
            "FlyShelf_PC", "Resources", "Mascot", "Kira"
        )
        Exporter.export_flyshelf(mascot, theme_dir, scale=args.scale, format=args.format)
    else:
        # Export individual state GIFs to output directory
        out_dir = args.output or "./output/kira"
        for state, anim in mascot.animations.items():
            path = os.path.join(out_dir, f"{state}.{args.format}")
            os.makedirs(out_dir, exist_ok=True)
            if args.format == "gif":
                Exporter.export_gif(anim, path, scale=args.scale)
            elif args.format == "png":
                Exporter.export_spritesheet(anim, path, scale=args.scale)
            elif args.format == "apng":
                Exporter.export_apng(anim, path, scale=args.scale)
        print(f"\n✅ Kira exported to: {out_dir}")


def cmd_demo(args):
    """Generate a demo canvas to test the drawing engine."""
    from sprite_engine.core.canvas import Canvas
    from sprite_engine.core.animation import Animation
    from sprite_engine.core.exporter import Exporter
    from sprite_engine.core.palette import VIOLET, LAVENDER, INDIGO, AMBER, OUTLINE, WHITE

    print("🎨 Generating demo sprite...")
    anim = Animation("demo", loop=True)
    SIZE = 32

    for i in range(4):
        c = Canvas(SIZE, SIZE)

        # Background gradient (simple)
        for y in range(SIZE):
            alpha = int(20 + y * 2)
            c.draw_line(0, y, SIZE - 1, y, (30, 20, 60, alpha))

        # Bouncing circle
        cy = 10 + (i % 2) * 4
        c.draw_circle(SIZE // 2, cy, 8, VIOLET, filled=True)
        c.draw_circle(SIZE // 2, cy, 8, OUTLINE)
        # Highlight
        c.put_pixel(SIZE // 2 - 3, cy - 3, WHITE)
        c.put_pixel(SIZE // 2 - 2, cy - 4, WHITE)

        # Star sparkle (rotating per frame)
        offsets = [(0, -12), (-12, 0), (0, 12), (12, 0)]
        sx, sy = offsets[i]
        px, py = SIZE // 2 + sx, cy + sy
        if 0 <= px < SIZE and 0 <= py < SIZE:
            c.put_pixel(px, py, AMBER)
            if px > 0: c.put_pixel(px - 1, py, AMBER)
            if px < SIZE - 1: c.put_pixel(px + 1, py, AMBER)

        anim.add_frame(c, [150, 100, 150, 200][i])

    path = args.output or "./demo.gif"
    Exporter.export_gif(anim, path, scale=args.scale)
    print(f"✅ Demo saved: {path}")


def cmd_palettes(args):
    """List available palettes and their colors."""
    from sprite_engine.core.palette import PALETTES
    for name, palette in PALETTES.items():
        print(f"\n🎨 {name.upper()} ({len(palette)} colors):")
        for i, c in enumerate(palette.colors):
            r, g, b, a = c
            bar = "█" * 3
            print(f"  [{i:2d}] #{r:02X}{g:02X}{b:02X} (a={a}) {bar}")


def cmd_draw(args):
    """
    Programmatic drawing from a JSON command file.
    Command file format:
    {
        "width": 32, "height": 32,
        "output": "output.gif",
        "scale": 2,
        "loop": true,
        "frames": [
            {
                "duration": 100,
                "commands": [
                    {"op": "put_pixel", "x": 10, "y": 10, "r": 255, "g": 0, "b": 0, "a": 255},
                    {"op": "draw_rect", "x": 5, "y": 5, "w": 10, "h": 10, "r": 200, "g": 100, "b": 255, "filled": true},
                    {"op": "draw_circle", "cx": 16, "cy": 16, "r": 8, "r_col": 100, "g_col": 50, "b_col": 200, "filled": false},
                    {"op": "draw_line", "x0": 0, "y0": 0, "x1": 31, "y1": 31, "r": 255, "g": 255, "b": 255}
                ]
            }
        ]
    }
    """
    import json
    from sprite_engine.core.canvas import Canvas
    from sprite_engine.core.animation import Animation
    from sprite_engine.core.exporter import Exporter

    if not args.file:
        print("ERROR: --file is required for 'draw' command")
        sys.exit(1)

    with open(args.file) as f:
        spec = json.load(f)

    width  = spec.get("width", 32)
    height = spec.get("height", 32)
    output = spec.get("output", args.output or "output.gif")
    scale  = spec.get("scale", args.scale)
    loop   = spec.get("loop", True)

    anim = Animation("custom", loop=loop)

    for frame_spec in spec.get("frames", []):
        c = Canvas(width, height)
        dur = frame_spec.get("duration", 100)

        for cmd in frame_spec.get("commands", []):
            op = cmd.get("op")
            try:
                if op == "put_pixel":
                    c.put_pixel(cmd["x"], cmd["y"], (cmd["r"], cmd["g"], cmd["b"], cmd.get("a", 255)))
                elif op == "draw_rect":
                    c.draw_rect(cmd["x"], cmd["y"], cmd["w"], cmd["h"],
                               (cmd["r"], cmd["g"], cmd["b"], cmd.get("a", 255)),
                               filled=cmd.get("filled", False))
                elif op == "draw_circle":
                    c.draw_circle(cmd["cx"], cmd["cy"], cmd["r"],
                                 (cmd["r_col"], cmd["g_col"], cmd["b_col"], cmd.get("a", 255)),
                                 filled=cmd.get("filled", False))
                elif op == "draw_line":
                    c.draw_line(cmd["x0"], cmd["y0"], cmd["x1"], cmd["y1"],
                               (cmd["r"], cmd["g"], cmd["b"], cmd.get("a", 255)))
                elif op == "clear":
                    c.clear((cmd.get("r", 0), cmd.get("g", 0), cmd.get("b", 0), cmd.get("a", 0)))
                elif op == "fill":
                    c.fill_bucket(cmd["x"], cmd["y"],
                                 (cmd["r"], cmd["g"], cmd["b"], cmd.get("a", 255)))
                else:
                    print(f"  Unknown op: {op}")
            except KeyError as e:
                print(f"  Missing key {e} in command: {cmd}")

        if cmd.get("add_outline"):
            c = c.add_outline()
        anim.add_frame(c, dur)

    fmt = output.rsplit(".", 1)[-1].lower()
    if fmt == "gif":
        Exporter.export_gif(anim, output, scale=scale)
    elif fmt == "png":
        Exporter.export_spritesheet(anim, output, scale=scale)
    elif fmt == "apng":
        Exporter.export_apng(anim, output, scale=scale)
    else:
        Exporter.export_gif(anim, output, scale=scale)


def cmd_libresprite(args):
    """LibreSprite integration commands."""
    from sprite_engine.libresprite.bridge import LibreSpritebridge
    bridge = LibreSpritebridge(exe_path=args.exe)

    if args.install:
        bridge.install_plugin(args.install)
    elif args.open:
        bridge.open_file(args.open)
    elif args.check:
        if bridge.is_available():
            print(f"✅ LibreSprite found at: {bridge.exe}")
        else:
            print(f"❌ LibreSprite not found at: {bridge.exe}")
            print("   Install from: https://github.com/LibreSprite/LibreSprite/releases")


def main():
    parser = argparse.ArgumentParser(
        prog="sprite_engine",
        description="🎨 AI Sprite Engine — Generate pixel art sprites for FlyShelf",
    )
    parser.add_argument("--version", action="version", version="sprite_engine 1.0.0")
    sub = parser.add_subparsers(dest="command", required=True)

    # ── kira ──────────────────────────────────────────────
    p_kira = sub.add_parser("kira", help="Generate Kira the Fox mascot")
    p_kira.add_argument("--output",    "-o", default=None, help="Output directory")
    p_kira.add_argument("--scale",     "-s", type=int, default=2, help="Scale factor (default: 2x)")
    p_kira.add_argument("--format",    "-f", default="gif", choices=["gif", "png", "apng"])
    p_kira.add_argument("--flyshelf",  action="store_true", help="Export directly to FlyShelf theme folder")
    p_kira.add_argument("--theme-dir", default=None, help="FlyShelf theme directory override")
    p_kira.set_defaults(func=cmd_kira)

    # ── demo ──────────────────────────────────────────────
    p_demo = sub.add_parser("demo", help="Generate a demo animation to test the engine")
    p_demo.add_argument("--output", "-o", default="./demo.gif")
    p_demo.add_argument("--scale",  "-s", type=int, default=4)
    p_demo.set_defaults(func=cmd_demo)

    # ── palettes ──────────────────────────────────────────
    p_pal = sub.add_parser("palettes", help="List available color palettes")
    p_pal.set_defaults(func=cmd_palettes)

    # ── draw ──────────────────────────────────────────────
    p_draw = sub.add_parser("draw", help="Draw from a JSON command file")
    p_draw.add_argument("--file",   "-f", required=True, help="JSON command spec file")
    p_draw.add_argument("--output", "-o", default=None)
    p_draw.add_argument("--scale",  "-s", type=int, default=2)
    p_draw.set_defaults(func=cmd_draw)

    # ── libresprite ───────────────────────────────────────
    p_ls = sub.add_parser("libresprite", help="LibreSprite integration")
    p_ls.add_argument("--exe",     default=None, help="Path to LibreSprite.exe")
    p_ls.add_argument("--install", default=None, metavar="DIR",
                      help="Install ai_bridge.js plugin to LibreSprite data/scripts/")
    p_ls.add_argument("--open",    default=None, metavar="FILE", help="Open file in LibreSprite GUI")
    p_ls.add_argument("--check",   action="store_true", help="Check if LibreSprite is accessible")
    p_ls.set_defaults(func=cmd_libresprite)

    args = parser.parse_args()
    args.func(args)


if __name__ == "__main__":
    main()
