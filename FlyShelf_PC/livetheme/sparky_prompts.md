# Sparky Electric — Sprite Generation Prompts
# Optimized for AI image generators (Imagen, DALL-E, Midjourney)
# These prompts produce consistent results across all animation states.

## Character Bible (PASTE THIS FIRST IN EVERY PROMPT)

```
CHARACTER DESIGN (must be identical in every frame):
- Round head (60% of body), small stubby body
- Electric blue (#4FC3F7) fur/body color
- 2 large round eyes: white with small cyan pupils, friendly expression
- 2 pointy fox ears on top, tipped with darker blue (#1976D2)
- Small orange (#FF9800) circular cheek marks (2 dots, one each side)
- A small zigzag lightning bolt tail in bright yellow (#FFD600)
- Tiny stubby arms and legs, dark blue (#0D47A1) outline
- 1 pixel black outline around entire character
```

## Style Anchor (PASTE THIS LAST IN EVERY PROMPT)

```
Style: Pixel art, 8-bit retro game sprite, chibi proportions, NES-era aesthetic,
NO anti-aliasing, NO gradients, crisp clean pixels. Professional game asset quality.
```

---

## Prompt 1: IDLE (6 frames, looping)

```
A horizontal sprite sheet of exactly 6 frames for a tiny cute chibi mascot character
animation. Each frame is 48x48 pixels, arranged in a single horizontal row
(total image: 288x48 pixels). Clean transparent/white background.

[PASTE CHARACTER DESIGN]

ANIMATION - IDLE BOUNCE (gentle loop):
Frame 1: Standing neutral, arms at sides
Frame 2: Slight upward bounce, ears perking up slightly
Frame 3: Peak of bounce, feet slightly off ground, happy smile
Frame 4: Coming back down
Frame 5: Small tail wag (tail shifts left)
Frame 6: Tail wag (tail shifts right), back to neutral

[PASTE STYLE ANCHOR]
```

**Conversion:**
```bash
python livetheme/sprite2gif.py idle_sheet.png sprites/idle.gif --frames 6 --fps 8
```

---

## Prompt 2: DELETE (8 frames, one-shot)

```
A horizontal sprite sheet of exactly 8 frames for a tiny cute chibi mascot character
performing a lightning attack animation. Each frame is 48x48 pixels, arranged in a
single horizontal row (total image: 384x48 pixels). Clean transparent/white background.

[PASTE CHARACTER DESIGN]

ANIMATION - DELETE ATTACK (dramatic, one-shot):
Frame 1: Alert stance, eyes wide, bracing
Frame 2: Crouching down, charging energy, cheeks glowing brighter orange
Frame 3: Eyes squeezed shut, small yellow spark pixels near cheeks
Frame 4: POWER RELEASE - arms thrust forward, 3-4 bright cyan lightning bolts from hands
Frame 5: Maximum energy - lightning bolts bigger, body glowing with cyan outline
Frame 6: Lightning bolts extending further, impact star-burst pixels at edge
Frame 7: Energy fading, lightning getting smaller
Frame 8: Standing again, slight exhaustion pose, one eye open

[PASTE STYLE ANCHOR]
The lightning VFX should be bright cyan and yellow pixels.
```

**Conversion:**
```bash
python livetheme/sprite2gif.py delete_sheet.png sprites/delete.gif --frames 8 --fps 12 --no-loop
```

---

## Prompt 3: INSERT (6 frames, one-shot)

```
A horizontal sprite sheet of exactly 6 frames for a tiny cute chibi mascot character
performing a happy celebration animation. Each frame is 48x48 pixels, arranged in a
single horizontal row (total image: 288x48 pixels). Clean transparent/white background.

[PASTE CHARACTER DESIGN]

ANIMATION - INSERT/CELEBRATE (happy greeting, one-shot):
Frame 1: Surprised look upward, ears perked up high
Frame 2: Eyes sparkling (star shapes in eyes), excited expression
Frame 3: Jumping up with both arms raised high, BIG happy smile, sparkle pixels around
Frame 4: Peak of jump, waving one paw enthusiastically
Frame 5: Landing with happy bounce, small yellow star pixels around
Frame 6: Standing happy with one arm waving, cheerful expression

[PASTE STYLE ANCHOR]
Add small sparkle/star pixel effects around the character in celebration frames.
```

**Conversion:**
```bash
python livetheme/sprite2gif.py insert_sheet.png sprites/insert.gif --frames 6 --fps 10 --no-loop
```

---

## Wallpaper Prompt

```
A dark premium wallpaper for a clipboard app with an electric blue theme.
Dark navy (#0D1B2A) to deep blue-purple (#1B2838) smooth gradient.
Subtle electric blue (#4FC3F7) energy veins branching across like a neural network,
very faint (10-15% opacity). Tiny cyan sparkle dots scattered like stars.
No characters, no text. Clean, minimal, premium dark aesthetic.
Suitable as a subtle background behind UI cards.
```

---

## Tips for Consistency

1. **Always paste the Character Bible first** — this locks the design
2. **Regenerate if the character looks different** — AI will sometimes drift
3. **Compare frame 1 of each animation** — they should look like the same character
4. **The smaller the sprite, the better** — 32-48px chibi characters are more forgiving
5. **If AI gives 1024x1024 images**, use sprite2gif.py with `--frames N` to auto-slice
6. **Test at actual display size** (48x48) — some details invisible at that scale are wasted

## Reusing for Different Characters

To create a new character theme:
1. Copy this file
2. Replace the Character Bible with your new character's design
3. Keep the animation descriptions (idle/delete/insert) the same
4. Generate new sprite sheets
5. Convert with sprite2gif.py
6. Copy manifest.json, update name/paths
