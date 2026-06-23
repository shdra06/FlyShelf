# sprite_engine — AI Sprite Generator for FlyShelf

A Python package that lets AI agents generate pixel-perfect sprite animations
programmatically — no manual drawing required.

## Quick Start

`ash
# From e:\exeapps\FlyShelf\tools\ directory:

# Generate Kira the Fox (all 12 animation states) at 4x scale:
python -m sprite_engine kira --output ./output/kira --scale 4

# Export directly to FlyShelf's mascot theme folder (2x = 64x64):
python -m sprite_engine kira --flyshelf --scale 2

# Generate a demo animation to test the engine:
python -m sprite_engine demo --output demo.gif --scale 6

# List all color palettes:
python -m sprite_engine palettes

# Draw from a JSON command file (for AI agents):
python -m sprite_engine draw --file my_commands.json --output result.gif
`

## Architecture

`
sprite_engine/
├── core/
│   ├── canvas.py      # Pixel canvas — Bresenham drawing, squash/stretch
│   ├── animation.py   # Frame management — variable timing, Mascot class
│   ├── palette.py     # Color palettes — FlyShelf, Monokai, Pastel, NES
│   └── exporter.py    # Export — GIF, APNG, PNG spritesheet, FlyShelf folder
├── mascots/
│   └── fox.py         # Kira the Fox — 12 animation states, fully code-generated
├── libresprite/
│   ├── bridge.py      # Python -> LibreSprite CLI bridge
│   └── ai_bridge.js   # LibreSprite JS plugin (install in data/scripts/)
├── __init__.py        # Public API
└── __main__.py        # CLI entry point
`

## Python API

`python
import sys
sys.path.insert(0, r"e:\exeapps\FlyShelf\tools")

from sprite_engine import Canvas, Animation, Exporter
from sprite_engine.mascots.fox import create_kira
from sprite_engine.core.palette import VIOLET, AMBER

# Generate complete Kira mascot
mascot = create_kira()

# Export all states to FlyShelf theme folder
Exporter.export_flyshelf(
    mascot,
    theme_dir=r"e:\exeapps\FlyShelf\FlyShelf_PC\Resources\Mascot\Kira",
    scale=2,      # 32x32 -> 64x64
    format="gif"
)
`

## Drawing Custom Sprites

`python
from sprite_engine import Canvas, Animation, Exporter
from sprite_engine.core.palette import VIOLET, AMBER, OUTLINE

# Create a 32x32 canvas
c = Canvas(32, 32)

# Draw with pixel-perfect primitives
c.draw_circle(16, 16, 12, VIOLET, filled=True)
c.draw_circle(16, 16, 12, OUTLINE)
c.put_pixel(12, 12, (255, 255, 255, 255))  # highlight

# Add animation frame
anim = Animation("my_sprite", loop=True)
anim.add_frame(c, duration_ms=120)

# Export at 4x scale (128x128 for FlyShelf mascot window)
Exporter.export_gif(anim, "output.gif", scale=4)
`

## JSON Command Format (for AI agents)

`json
{
  "width": 32, "height": 32,
  "output": "output.gif",
  "scale": 4,
  "loop": true,
  "frames": [
    {
      "duration": 150,
      "commands": [
        {"op": "draw_circle", "cx": 16, "cy": 16, "r": 10, "r_col": 139, "g_col": 92, "b_col": 246, "filled": true},
        {"op": "put_pixel", "x": 13, "y": 13, "r": 255, "g": 255, "b": 255, "a": 255}
      ]
    }
  ]
}
`

## FlyShelf Integration

The exporter maps mascot states to FlyShelf's expected filenames:

| Mascot State | FlyShelf Filename |
|---|---|
| idle          | idle.gif          |
| walk          | running.gif       |
| fall          | falling.gif       |
| react (happy) | copy.gif          |
| sad           | delete.gif        |
| search        | search.gif        |
| sleep         | sleep.gif         |

## LibreSprite Bridge

`ash
# Check if LibreSprite is installed:
python -m sprite_engine libresprite --check

# Install the AI bridge plugin into LibreSprite:
python -m sprite_engine libresprite --install "C:\Program Files\LibreSprite"

# Open a generated sprite in LibreSprite GUI for manual editing:
python -m sprite_engine libresprite --open output/kira/idle.gif
`

After installing, in LibreSprite: Scripts > Run "ai_bridge.js"

## Animation States (Kira the Fox)

| State  | Frames | Duration | Loop | Trigger          |
|--------|--------|----------|------|------------------|
| idle   | 4      | 650ms    | yes  | default          |
| walk   | 6      | 380ms    | yes  | random patrol    |
| run    | 6      | 250ms    | yes  | excited          |
| fall   | 2      | 200ms    | yes  | after drag drop  |
| land   | 3      | 240ms    | no   | after fall       |
| sleep  | 3      | 1300ms   | yes  | 10min idle       |
| react  | 4      | 360ms    | no   | item copied      |
| sad    | 4      | 530ms    | no   | item deleted     |
| think  | 3      | 700ms    | yes  | AI processing    |
| search | 4      | 400ms    | yes  | search bar open  |
| dance  | 6      | 360ms    | no   | rare (5 min)     |
| drag   | 1      | 100ms    | yes  | user drags pet   |
