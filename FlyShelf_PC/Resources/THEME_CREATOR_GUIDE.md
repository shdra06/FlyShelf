# 🎨 FlyShelf Theme Creator Guide

Create your own animated mascot themes for FlyShelf! Themes are **hot-loadable** — no recompilation needed.

---

## Quick Start

1. Navigate to `%AppData%/FlyShelf/Themes/`
2. Create a new folder with your theme name (e.g., `sparky-lightning/`)
3. Add a `manifest.json` and your sprite GIFs
4. FlyShelf will auto-detect your theme immediately!

## Folder Structure

```
my-awesome-theme/
├── manifest.json           # Required: theme metadata + animation config
├── preview.png             # Optional: 256×256 preview for theme picker
├── README.md               # Optional: description for the community
└── sprites/
    ├── idle.gif            # Always-playing mascot animation
    ├── delete.gif          # Plays when user deletes an item
    ├── copy.gif            # Plays when content is copied
    ├── search.gif          # Plays while search bar is active
    └── running.gif         # Corner running character animation
```

## manifest.json Reference

```json
{
  "name": "My Theme Name",
  "author": "Your Name",
  "version": "1.0.0",
  "description": "A short description of your theme",
  "license": "CC-BY-4.0",
  "character": "Character Name",
  "tags": ["pixel-art", "anime", "gaming"],

  "animations": {
    "idle": {
      "file": "sprites/idle.gif",
      "width": 48,
      "height": 48,
      "placement": "header-right",
      "loop": true
    },
    "delete": {
      "file": "sprites/delete.gif",
      "width": 64,
      "height": 64,
      "placement": "center-overlay",
      "loop": false,
      "trigger": "on-delete",
      "durationMs": 800
    },
    "copy": {
      "file": "sprites/copy.gif",
      "width": 48,
      "height": 48,
      "placement": "header-right",
      "loop": false,
      "trigger": "on-copy",
      "durationMs": 600
    },
    "search": {
      "file": "sprites/search.gif",
      "width": 48,
      "height": 48,
      "placement": "header-left",
      "loop": true,
      "trigger": "on-search"
    },
    "running": {
      "file": "sprites/running.gif",
      "width": 32,
      "height": 32,
      "placement": "bottom-scroll",
      "loop": true,
      "speed": 1.5
    }
  },

  "placements": {
    "header-right": { "anchor": "top-right", "offsetX": -60, "offsetY": 4 },
    "header-left": { "anchor": "top-left", "offsetX": 8, "offsetY": 4 },
    "center-overlay": { "anchor": "center", "offsetX": 0, "offsetY": 0 },
    "bottom-scroll": { "anchor": "bottom-left", "offsetX": 10, "offsetY": -10 }
  }
}
```

## Animation Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `file` | string | **required** | Relative path to GIF or PNG file |
| `width` | int | 48 | Display width in pixels |
| `height` | int | 48 | Display height in pixels |
| `placement` | string | "header-right" | Named placement from placements dict |
| `loop` | bool | true | Loop continuously or play once |
| `trigger` | string | "" | Event trigger (see below) |
| `durationMs` | int | 0 | Duration for one-shot anims (0 = auto) |
| `speed` | float | 1.0 | Playback speed multiplier |
| `flipOnEdge` | bool | false | Mirror sprite at boundaries |

## Triggers

| Trigger Name | When It Fires |
|-------------|---------------|
| *(empty)* | Always playing (idle) |
| `on-delete` | User deletes a clipboard item |
| `on-copy` | User copies content to clipboard |
| `on-search` | Search bar is active |

## Placement Anchors

| Anchor | Position |
|--------|----------|
| `top-left` | Left side of toolbar |
| `top-right` | Right side of toolbar (beside search) |
| `center` | Center of clipboard window |
| `bottom-left` | Bottom-left corner |
| `bottom-right` | Bottom-right corner |

## GIF Specifications

For best results:

- **Size**: 32×32 to 64×64 pixels
- **Style**: Pixel art with transparency works great
- **Format**: GIF with alpha channel
- **FPS**: 8–15 frames per second
- **File Size**: Keep each GIF under 500KB
- **Scaling**: NearestNeighbor (pixel-perfect) is used — crisp pixels!

## Sharing Your Theme

1. Zip your theme folder
2. Rename the `.zip` extension to `.flyshelf-theme`
3. Share the file!

### Installing Themes

Users can install your theme by:
- **Dragging** the `.flyshelf-theme` file onto FlyShelf
- **Pasting** it into FlyShelf
- **Placing** the folder directly in `%AppData%/FlyShelf/Themes/`

Themes appear instantly — no restart needed!

## Tips

- **Start simple**: Just create an `idle.gif` — that's all you need!
- **Test fast**: Edit GIFs and save — FlyShelf hot-reloads automatically
- **Preview**: Add a `preview.png` (256×256) for the theme picker
- **Memory**: Keep total sprite memory under 2MB for smooth performance
- **Transparency**: GIF transparency works — use it for floating characters!

## Ideas for Themes

- 🐱 Cat napping on the clipboard, waking up on copy
- ⚡ Sparky with lightning cheeks, thunderbolt on delete
- 💚 Hulk smashing the ground when items are deleted  
- 🌸 Cherry blossoms falling while searching
- 🤖 Robot assistant with typing animation on copy
- 🎮 Mario running across the bottom of the clipboard
- 🦊 Fox mascot with seasonal variations

---

*Built with ❤️ by the FlyShelf community. Make something awesome!*
