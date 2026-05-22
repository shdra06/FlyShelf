# 🎮 FlyShelf Mascot Sprite Generation Guide

## 📍 Placement Zones on Your Clipboard

Based on your screenshot, here are the **3 animation zones**:

![Placement Mockup](C:/Users/Shivendra/.gemini/antigravity-ide/brain/efa145d2-0f62-46cd-9fb9-7b75c8f08914/clipboard_mascot_placement_1779363862367.png)

| Zone | Location | Size | Animation Type |
|------|----------|------|---------------|
| **A — Header Mascot** | Top-left, before search icon | 28×28 px | Idle loop: dance, spark, wave |
| **B — Edge Runner** | Bottom edge or top edge | 24×24 px | Walk/run cycle left→right |
| **C — Corner Accent** | Bottom-right corner | 20×20 px | Peek-in, sit, sleep |

---

## 🎨 Generated Samples

### Sparky Sprite Sheet
![Sparky Sprites](C:/Users/Shivendra/.gemini/antigravity/brain/d725c76d-b6a4-48b8-9730-246f8fcffbc7/cat_idle_sheet_1779427294478.png)

### Hulk Sprite Sheet
![Hulk Sprites](C:/Users/Shivendra/.gemini/antigravity-ide/brain/efa145d2-0f62-46cd-9fb9-7b75c8f08914/hulk_pixel_sprite_1779363836486.png)

---

## 🔑 Master Style Prompt (Use This as Base)

Copy this **style anchor** at the start of EVERY prompt to keep all characters consistent:

```
Pixel art, 32x32 pixel grid per frame, 8-bit retro game style, 
chibi proportions (large head, small body), crisp clean pixels, 
NO anti-aliasing, NO gradients, limited 16-color palette, 
dark outline (1px black border around character), 
transparent/white background, NES/SNES era aesthetic, 
professional game sprite asset quality, each frame clearly 
separated in a horizontal strip
```

---

## ⚡ Character-Specific Prompts

### 1. Sparky — Header Idle Animation (Zone A)

**Prompt for AI Image Generator:**
```
[PASTE STYLE ANCHOR ABOVE] —

Cute chibi Sparky character sprite sheet, 6 frames horizontal strip:

Frame 1: Standing idle, ears up, looking forward
Frame 2: Cheeks sparking — tiny yellow lightning bolts shooting from red cheek circles  
Frame 3: Cheeks sparking more intensely — larger bolts, eyes squinting with effort
Frame 4: Happy bounce — feet off ground, arms out, big smile
Frame 5: Waving right paw side to side
Frame 6: Sleepy — eyes half closed, head tilting down

Color palette: bright yellow (#FFD700) body, red (#FF0000) cheeks, 
black eyes, brown (#8B4513) tail stripes, yellow (#FFFF00) lightning
```

### 2. Hulk — Ground Smash Delete Animation

**Prompt:**
```
[PASTE STYLE ANCHOR] —

Cute chibi green muscular character in torn purple shorts, 
6 frames horizontal strip:

Frame 1: Standing idle, fists clenched at sides, angry eyebrows
Frame 2: Wind-up — both arms raised high above head
Frame 3: SMASH impact — fists hitting ground, 4 small debris particles flying up
Frame 4: Shockwave — circular impact lines radiating from ground contact point
Frame 5: Standing up from smash, dust settling
Frame 6: Victory roar — mouth wide open, fists pumped

Color palette: bright green (#00CC00) skin, dark green (#006600) shadows,
purple (#800080) shorts, white eyes, gray debris particles
```

### 3. Mario — Edge Runner (Zone B)

**Prompt:**
```
[PASTE STYLE ANCHOR] —

Cute chibi Mario character run cycle sprite sheet, 8 frames horizontal strip:

Frame 1-2: Walk right cycle (alternating legs)
Frame 3-4: Run right cycle (faster stride, cap flowing back)  
Frame 5: Jump up (knees bent, arms up)
Frame 6: Jump peak (spread arms)
Frame 7-8: Walk LEFT cycle (mirrored of frames 1-2)

Color palette: red (#FF0000) cap and shirt, blue (#0000FF) overalls,
brown (#8B4513) shoes, skin (#FFCC99) face and hands, 
white gloves, black mustache
```

### 4. Naruto — Shadow Clone Idle

**Prompt:**
```
[PASTE STYLE ANCHOR] —

Cute chibi Naruto character sprite sheet in orange jumpsuit with 
blonde spiky hair and blue headband, 6 frames horizontal strip:

Frame 1: Standing idle, arms crossed confidently
Frame 2: Making hand sign — fingers interlocked (shadow clone jutsu)  
Frame 3: Blue chakra aura appearing around body (2px glow outline)
Frame 4: Shadow clone poof — small white cloud burst
Frame 5: Two characters standing side by side (clone appeared)
Frame 6: Thumbs up pose with big grin, whisker marks on cheeks

Color palette: orange (#FF6600) jumpsuit, blonde (#FFD700) hair,
blue (#0066FF) headband, light blue (#66CCFF) chakra glow
```

### 5. Goku — Power-Up Corner (Zone C)

**Prompt:**
```
[PASTE STYLE ANCHOR] —

Cute chibi Goku character in orange gi sprite sheet, 
6 frames horizontal strip:

Frame 1: Standing idle in fighting stance
Frame 2: Powering up — crouching, fists clenched
Frame 3: Aura appearing — yellow energy lines around body
Frame 4: Super Saiyan transformation — hair turns yellow/spiky, golden aura
Frame 5: Kamehameha charge — hands cupped at side, blue orb growing
Frame 6: Kamehameha blast — arms forward, blue beam shooting right

Color palette: orange (#FF6600) gi, black (#000000) hair (frames 1-3),
golden yellow (#FFD700) super saiyan hair, blue (#0066FF) energy,
skin (#FFCC99)
```

### 6. Cat Pet — Ambient Corner

**Prompt:**
```
[PASTE STYLE ANCHOR] —

Cute chibi pixel cat character sprite sheet, 6 frames horizontal strip:

Frame 1: Sitting idle, tail gently swaying left
Frame 2: Sitting idle, tail swaying right  
Frame 3: Paw licking (grooming pose, one paw to face)
Frame 4: Stretching (front paws forward, butt up)
Frame 5: Curled up sleeping, "Z z z" text above
Frame 6: Startled jump — back arched, fur puffed, wide eyes

Color palette: orange (#FF8800) tabby stripes, cream (#FFE4B5) belly,
pink (#FF69B4) nose and inner ears, green (#00CC00) eyes
```

---

## 🌐 Where to Find Pre-Made High Quality Sprites

If AI generation doesn't give perfect results, these sites have **exactly this style**:

| Source | URL | Best For |
|--------|-----|----------|
| **itch.io** | `itch.io/game-assets/tag-pixel-art/tag-sprites` | Free/paid sprite packs, huge variety |
| **OpenGameArt** | `opengameart.org` | Free CC0 sprites, great quality |
| **Kenney.nl** | `kenney.nl/assets` | Free professional game assets |
| **Sprite Database** | `spritedatabase.net` | Ripped game sprites (reference only) |
| **The Spriters Resource** | `spriters-resource.com` | Official game sprite rips |

**Search terms for consistency:**
```
"chibi pixel art sprite sheet 32x32"
"16-bit character animation idle run"  
"pixel art mascot transparent PNG"
"retro game pet companion sprite"
```

---

## 🔧 Technical Specs for WPF Integration

### File Format Requirements
- **Format:** Animated GIF (for `XamlAnimatedGif`) or PNG sprite strip
- **Size per frame:** 32×32 or 48×48 pixels (will display at 24-28px in the header)
- **Frame rate:** 8-12 FPS for idle, 12-16 FPS for action
- **Background:** Transparent (alpha channel)
- **Scaling:** Use `RenderOptions.BitmapScalingMode="NearestNeighbor"` in XAML for crisp pixels

### Animated GIF Specs
```
Frame count: 4-8 frames per animation  
Frame delay: 80-120ms per frame (idle), 60-80ms (action)
Loop: Infinite for idle, 1x for action triggers
Canvas size: 32×32 or 48×48
Colors: ≤ 256 (GIF limitation)
Transparency: Yes
```

### If Using PNG Sprite Strip
```
Width: 32px × number_of_frames (e.g., 192px for 6 frames)
Height: 32px
Your code will crop to one frame at a time using CroppedBitmap
```

---

## 🎯 Recommended Character Set for V1

Start with these **3 characters** to cover all zones:

1. **⚡ Sparky** → Header idle (Zone A) — cheek sparks loop
2. **🏃 Mario/Sonic** → Edge runner (Zone B) — run cycle across bottom
3. **🐱 Cat** → Corner pet (Zone C) — sleeping/grooming ambient

These give you variety (action hero, runner, ambient pet) without overwhelming the UI.
