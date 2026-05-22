# Mochi StarPuff — Sprite Generation Prompts
# Original character, copyright-free, kawaii aesthetic

## Character Bible

```
CHARACTER DESIGN (must be identical in every frame):
- Perfectly round puffy cloud/marshmallow body, very cute and squishy looking
- Pastel pink (#FF80AB) body with white (#FFFFFF) belly patch
- 2 large sparkling star-shaped eyes in deep pink (#E91E63) with white star highlights
- Tiny cute cat-mouth (small "w" shape), always smiling
- A curly swirl antenna on top of head with a small golden star (#FFD700) at the tip
- 2 small cloud-puff wings on sides (lighter pink #F8BBD0)
- Rosy circle cheek blush marks (#FF4081) on both sides
- Tiny stubby feet at bottom
- 1 pixel dark pink (#880E4F) outline around character
```

## Style Anchor

```
Style: Pixel art, 8-bit retro game sprite, chibi proportions, NES-era aesthetic,
NO anti-aliasing, NO gradients, crisp clean pixels. Professional game asset quality.
Very cute kawaii aesthetic.
```

---

## Prompt 1: IDLE — Float (6 frames, looping)

```
[CHARACTER DESIGN]

ANIMATION - IDLE FLOAT (gentle loop):
Frame 1: Floating neutral, wings at rest
Frame 2: Slight upward float, wings flapping down
Frame 3: Peak of float, sparkle effect near star antenna
Frame 4: Coming back down, wings flapping up
Frame 5: Gentle body squish (gets slightly wider/shorter)
Frame 6: Body bounces back to round, twinkle in eyes

[STYLE ANCHOR]
```

Conversion: `python sprite2gif.py idle_sheet.png sprites/idle.gif --frames 6 --fps 8`

---

## Prompt 2: DELETE — Star Burst (8 frames, one-shot)

```
[CHARACTER DESIGN]

ANIMATION - STAR BURST DELETE ATTACK (dramatic, one-shot):
Frame 1: Alert surprised face, eyes wide
Frame 2: Puffing up body larger (inflating), cheeks puffing out
Frame 3: Body fully puffed, antenna star glowing bright golden, eyes determined
Frame 4: STAR BURST - shooting a shower of golden star pixels from antenna
Frame 5: Maximum power - multiple star projectiles flying in all directions, body glowing pink
Frame 6: Stars spreading outward, impact sparkle effects
Frame 7: Stars fading, body deflating back to normal size
Frame 8: Tired but proud expression, one eye winking, small sweat drop pixel

[STYLE ANCHOR]
The star VFX should be bright golden yellow pixels.
```

Conversion: `python sprite2gif.py delete_sheet.png sprites/delete.gif --frames 8 --fps 12 --no-loop`

---

## Prompt 3: INSERT — Sparkle Dance (6 frames, one-shot)

```
[CHARACTER DESIGN]

ANIMATION - HAPPY CELEBRATE (excited greeting, one-shot):
Frame 1: Eyes become huge hearts/stars, mouth open in excited "O"
Frame 2: Bouncing up high with wings spread wide, radiating small pink heart pixels
Frame 3: Peak of bounce, spinning with trail of sparkle star pixels around body
Frame 4: Landing with a happy squish, confetti-like colored pixel dots falling around
Frame 5: Dancing side to side with wings flapping, golden sparkle pixels from antenna
Frame 6: Final pose: both wings up waving, biggest smile, eyes as happy crescents

[STYLE ANCHOR]
Add colorful sparkle/heart/star pixel effects around the character.
```

Conversion: `python sprite2gif.py insert_sheet.png sprites/insert.gif --frames 6 --fps 10 --no-loop`

---

## Wallpaper

```
Dark premium wallpaper, deep dark purple (#1A0A2E) to dark magenta (#2D1B4E) gradient.
Subtle pastel pink (#FF80AB) constellation lines and tiny star dots like a dreamy night sky.
Small golden (#FFD700) star shapes twinkling faintly. Very subtle crescent moon in pale pink.
Soft cloud wisps in very faint pink at the bottom. No characters, no text.
Clean, minimal, premium dark kawaii aesthetic.
```
