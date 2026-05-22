# FlyShelf Mascot Animation Blueprint
# Reusable Template System for Character Theme Creation

This document defines the **complete animation state machine** for FlyShelf clipboard mascots.
Every character theme MUST implement these animation states. Use this as a blueprint when
creating new character themes — just swap the character-specific descriptions.

---

## Architecture: How Animations Map to Code

```
USER ACTION          TRIGGER SERVICE             SPRITE ANIMATOR        MANIFEST KEY
-----------          ---------------             ---------------        ------------
Copy to clipboard -> OnCopy()               ->  MascotCopy           -> "copy"
Delete item       -> OnDelete()             ->  MascotDelete         -> "delete"
                     (simultaneous)         ->  MascotIdle           -> "header_reaction"
Search opened     -> OnSearch(true)         ->  MascotSearch         -> "search"
Search closed     -> OnSearch(false)        ->  MascotIdle           -> "idle"
Nothing (2-3 sec) -> IdleTimer fires        ->  MascotIdle           -> "idle"
App launched      -> Initialize()           ->  MascotIdle           -> "idle"
Item inserted     -> OnInsert()             ->  MascotCopy           -> "insert"
                     (simultaneous)         ->  WallpaperFlash       -> (code-driven)
```

---

## Animation States (The State Machine)

Every character has these states. The mascot transitions between them based on user actions:

```
                    +---> [SEARCH] ---(close search)---> [IDLE_DANCE]
                    |
[SLEEPING] ---(any action)---> [WAKE_UP] ---> [IDLE_DANCE]
    ^                                              |
    |                                              |
    +----(2-3s no activity)----+                   |
                               |                   |
                    [IDLE_DANCE] <--(return)--------+
                         |
                    (user action)
                         |
          +--------------+--------------+
          |              |              |
     [DELETE_VFX]   [COPY_VFX]    [INSERT_VFX]
          |              |              |
          +--------------+--------------+
                         |
                    [IDLE_DANCE]
                         |
                    (2-3s timeout)
                         |
                    [SLEEPING]
```

---

## REQUIRED Animation Strips Per Character

Each character theme MUST provide these sprite strips:

### 1. IDLE_DANCE (Zone A: Header — loops forever)
- **File:** `sprites/idle.gif`
- **Frames:** 8-12
- **FPS:** 8-10
- **Loop:** Yes (infinite)
- **Size:** 48x48 per frame
- **Purpose:** Default state. Character is alive, dancing, bobbing, fidgeting.

### 2. SLEEPING (Zone A: Header — loops)
- **File:** `sprites/sleeping.gif`
- **Frames:** 4-6
- **FPS:** 4 (slow breathing)
- **Loop:** Yes
- **Size:** 48x48
- **Purpose:** After 2-3 seconds of no user activity, character falls asleep.

### 3. WAKE_UP (Zone A: Header — one-shot)
- **File:** `sprites/wake_up.gif`
- **Frames:** 4-6
- **FPS:** 12
- **Loop:** No (one-shot)
- **Size:** 48x48
- **Purpose:** Transition from sleeping to active. Plays once, then switches to idle_dance.

### 4. DELETE_VFX (Zone OVERLAY: Center — one-shot)
- **File:** `sprites/delete.gif`
- **Frames:** 10-15
- **FPS:** 15 (fast, dramatic)
- **Loop:** No
- **Size:** 72x72 (larger for impact)
- **Purpose:** The main "power move" VFX that strikes the card being deleted.

### 5. HEADER_REACTION (Zone A: Header — one-shot, simultaneous with delete)
- **File:** `sprites/header_reaction.gif`
- **Frames:** 8-10
- **FPS:** 12
- **Loop:** No
- **Size:** 48x48
- **Purpose:** Character's power-up pose while the delete VFX plays. Shows effort/exertion.

### 6. COPY_VFX (Zone A: Header-right — one-shot)
- **File:** `sprites/copy.gif`
- **Frames:** 6-8
- **FPS:** 12
- **Loop:** No
- **Size:** 36x36
- **Purpose:** Quick sparkle/effect when user copies something.

### 7. INSERT_VFX (Zone A: Header — one-shot)
- **File:** `sprites/insert.gif`
- **Frames:** 8-10
- **FPS:** 10
- **Loop:** No
- **Size:** 48x48
- **Purpose:** Character reacts happily when new item is added to clipboard.

### 8. SEARCH (Zone A: Header — loops while searching)
- **File:** `sprites/search.gif`
- **Frames:** 6-8
- **FPS:** 8
- **Loop:** Yes
- **Size:** 42x42
- **Purpose:** Character is searching/looking through magnifying glass.

---

## CHARACTER: Sparky (Electric Spark Mascot)

### Visual Identity
```
Body:       Electric blue (#4FC3F7)
Cheeks:     Small orange (#FF9800) circular cheek marks (2 dots, one each side) that glow bright orange (#FF8C00) when charged
Eyes:       Large white with small cyan pupils, friendly expression
Ears:       Pointy fox ears on top, tipped with darker blue (#1976D2)
Tail:       Small zigzag lightning bolt tail in bright yellow (#FFD600)
Lightning:  Cyan (#00E5FF) and bright yellow (#FFFF00) bolts
```

### State: IDLE_DANCE
```
Frame Layout: 12 frames, horizontal strip, 48x48 each
Total image:  576 x 48 px

Frame  1: Standing, arms at sides, slight smile
Frame  2: Bouncing up slightly, ears perking
Frame  3: Peak of bounce, happy expression
Frame  4: Landing, slight squish
Frame  5: Waving right paw (paw up)
Frame  6: Waving right paw (paw mid)
Frame  7: Waving right paw (paw down)
Frame  8: Tail wagging left
Frame  9: Tail wagging right
Frame 10: Small cheek spark (1px yellow dots near cheeks)
Frame 11: Bigger cheek spark (2-3px lightning bolts from cheeks)
Frame 12: Spark fading, return to frame 1 pose
```

**AI Generation Prompt:**
```
Pixel art sprite sheet, 576x48 pixels total, 12 frames in a single horizontal
strip, each frame exactly 48x48 pixels. 8-bit retro game style, chibi proportions,
crisp clean pixels, NO anti-aliasing, limited 16-color palette, 1px black outline
around character, transparent background.

Character: Cute chibi Sparky (electric blue fur body, small orange cheek marks, pointy fox ears tipped with darker blue, small yellow zigzag lightning bolt tail). 12 frame dance/idle animation sequence:

Frames 1-4: Gentle bounce cycle (standing -> up -> peak -> land with squish)
Frames 5-7: Right paw wave (up -> mid -> down)
Frames 8-9: Tail wag (tail swings left then right)
Frames 10-12: Cheek sparks (small orange dots -> bigger cyan lightning bolts -> fade)

Each frame must show the SAME character from the SAME angle with only the described
pose change. Keep the body position consistent between frames.
```

### State: SLEEPING
```
Frame Layout: 6 frames, 48x48 each
Total image:  288 x 48 px

Frame 1: Curled up on side, eyes closed, "Z" above head
Frame 2: Same pose, "Z z" above head (second Z appearing)
Frame 3: Same pose, belly slightly expanding (breathing in)
Frame 4: Same pose, "Z z Z" (third Z appearing)
Frame 5: Same pose, belly contracting (breathing out)
Frame 6: Same as frame 1, "Z" resetting
```

**AI Generation Prompt:**
```
Pixel art sprite sheet, 288x48 pixels, 6 frames horizontal strip, each 48x48.
8-bit retro style, chibi, clean pixels, black outline, transparent background.

Sleeping Sparky animation: Electric blue fox mascot character curled up in a ball with eyes
closed, yellow lightning tail wrapped around body. Slow breathing cycle with belly expanding/contracting.
Pixel "Z z Z" letters floating above in white, appearing one by one across frames.
Orange cheek marks visible. Ears flopped down relaxed.
```

### State: WAKE_UP (Transition: sleeping -> active)
```
Frame Layout: 6 frames, 48x48 each
Total image:  288 x 48 px

Frame 1: Still curled up (same as sleeping frame 1)
Frame 2: One eye opening, ear twitching up
Frame 3: Both eyes open wide (startled/alert expression), sitting up
Frame 4: Standing up, stretching arms up with a yawn
Frame 5: Shaking body (motion blur lines), fully awake
Frame 6: Standing alert, ready pose (transition to idle_dance frame 1)
```

**AI Generation Prompt:**
```
Pixel art sprite sheet, 288x48 pixels, 6 frames horizontal strip, each 48x48.
8-bit retro style, chibi, clean pixels, black outline, transparent background.

Sparky waking up sequence: Cute chibi electric blue fox mascot waking up. Frame 1 curled sleeping. Frame 2 one eye peeking open,
ear perking. Frame 3 both eyes wide open surprised, sitting up quickly. Frame 4
standing and stretching arms up with mouth open yawn. Frame 5 whole body shake with
tiny motion lines. Frame 6 fully standing alert and ready, happy expression.
```

### State: DELETE_VFX (The Lightning Strike)
```
Frame Layout: 15 frames, 72x72 each (LARGE for dramatic impact)
Total image:  1080 x 72 px

This is the SIGNATURE MOVE. When user presses delete:

Phase 1 — CHARGE (Frames 1-4):
Frame  1: Empty frame (nothing visible yet)
Frame  2: Small yellow spark at top of frame (lightning starting)
Frame  3: Spark growing, zigzag line forming downward
Frame  4: Multiple zigzag branches forming, bright cyan core

Phase 2 — STRIKE (Frames 5-8):
Frame  5: FULL LIGHTNING BOLT — thick zigzag from top to bottom, bright white center
Frame  6: Lightning at maximum brightness, small star-burst at impact point (bottom)
Frame  7: Impact explosion — circle of yellow/white pixel particles at bottom
Frame  8: Secondary bolts branching from main bolt, debris flying

Phase 3 — DISSIPATE (Frames 9-12):
Frame  9: Lightning fading, bolt getting thinner
Frame 10: Only the impact glow remaining at bottom
Frame 11: Scattered sparks floating upward (residual energy)
Frame 12: Sparks fading out

Phase 4 — AFTERGLOW (Frames 13-15):
Frame 13: Very faint glow at center
Frame 14: Almost invisible wisps
Frame 15: Empty frame (clean exit)
```

**AI Generation Prompt:**
```
Pixel art sprite sheet, 1080x72 pixels, 15 frames horizontal strip, each 72x72.
8-bit retro style, clean pixels, black outline, transparent background.

Lightning bolt strike VFX effect animation (NOT a character, just the lightning):

Frames 1-4: Lightning CHARGING — small bright spark at top growing into zigzag
line pointing downward, electric blue (#00E5FF) and yellow (#FFFF00)
Frames 5-8: Lightning STRIKE — full thick zigzag bolt from top to bottom,
bright white (#FFFFFF) center with cyan edges, star-burst explosion at bottom
impact point with small pixel debris particles flying outward
Frames 9-12: Lightning DISSIPATING — bolt getting thinner, glow remaining at
impact point, scattered sparks floating upward
Frames 13-15: AFTERGLOW — faint wisps fading to nothing

The bolt should be 3-4 pixels wide at its thickest, with a sharp zigzag pattern.
Use bright saturated colors against transparent background.
```

### State: HEADER_REACTION (Plays on Sparky while delete VFX plays on overlay)
```
Frame Layout: 10 frames, 48x48 each
Total image:  480 x 48 px

Frame  1: Sparky alert, bracing (slight crouch)
Frame  2: Eyes squeezing shut with effort
Frame  3: Cheeks glowing BRIGHT orange/red (charging up)
Frame  4: CHEEK SQUEEZE — body tensing, 2 small lightning bolts from cheeks
Frame  5: MAXIMUM POWER — 4 lightning bolts from cheeks, body vibrating
Frame  6: Lightning bolts shooting upward and rightward from cheeks
Frame  7: Power sustaining (for sequential deletes)
Frame  8: Power fading, bolts getting smaller
Frame  9: Relaxing, cheeks returning to normal red
Frame 10: Standing normally again, slight pant/exhaustion
```

**AI Generation Prompt:**
```
Pixel art sprite sheet, 480x48 pixels, 10 frames horizontal strip, each 48x48.
8-bit retro style, chibi, clean pixels, black outline, transparent background.

Sparky powering up and firing cheek lightning sequence:

Frame 1: Alert stance, slightly crouching. Frame 2: Eyes squeezing shut with
effort lines. Frame 3: Orange cheek marks glowing bright orange, body tensing. Frames 4-5:
Cheeks EXPLODING with cyan lightning bolts (4 zigzag bolts shooting from both
cheeks). Frame 6: Lightning bolts shooting upward from cheeks like an electric blast.
Frame 7: Sustained power pose. Frame 8: Lightning getting smaller. Frame 9:
Relaxing, cheek marks back to normal orange. Frame 10: Standing with tired/satisfied expression.
```

### State: INSERT_VFX (New Item Added to Clipboard)
```
Frame Layout: 10 frames, 48x48 each
Total image:  480 x 48 px

Frame  1: Sparky looking up excitedly, ears perking
Frame  2: Eyes sparkling (star shapes in eyes)
Frame  3: Jumping up with both arms raised, mouth wide open saying "HI!"
Frame  4: Peak of jump, sparkles around body
Frame  5: Landing, still waving
Frame  6: Waving both paws excitedly (left paw up)
Frame  7: Waving both paws (right paw up)
Frame  8: Waving both paws (left paw up — repeat wave cycle)
Frame  9: Waving both paws (right paw up — repeat wave cycle)
Frame 10: Happy standing, one paw up, big smile
```

**AI Generation Prompt:**
```
Pixel art sprite sheet, 480x48 pixels, 10 frames horizontal strip, each 48x48.
8-bit retro style, chibi, clean pixels, black outline, transparent background.

Sparky excited happy greeting sequence:

Frame 1: Looking up with perked ears excited. Frame 2: Star-shaped sparkles in
eyes. Frame 3: Jumping high with both arms raised, mouth open "HI!" text in small
pixels. Frame 4: Peak of jump with sparkle particles around body. Frame 5: Landing
still waving. Frames 6-9: Enthusiastic wave cycle (alternating paws up/down rapidly).
Frame 10: Standing happy with one paw raised in greeting.
```

### State: SEARCH
```
Frame Layout: 8 frames, 42x42 each
Total image:  336 x 42 px

Frame 1: Holding magnifying glass, looking left
Frame 2: Looking through magnifying glass (lens glint)
Frame 3: Looking right through magnifying glass
Frame 4: Magnifying glass lens sparkling
Frame 5: Looking down through magnifying glass
Frame 6: Looking up, curious expression
Frame 7: Tapping chin thoughtfully
Frame 8: Back to frame 1 pose (loop reset)
```

**AI Generation Prompt:**
```
Pixel art sprite sheet, 336x42 pixels, 8 frames horizontal strip, each 42x42.
8-bit retro style, chibi, clean pixels, black outline, transparent background.

Sparky searching with magnifying glass: Holding oversized magnifying glass in
right paw. Frame 1: looking left through lens. Frame 2: lens glint/sparkle.
Frame 3: looking right. Frame 4: lens sparkle. Frame 5: looking down. Frame 6:
looking up curious. Frame 7: tapping chin with free paw. Frame 8: back to
looking left.
```

---

## Wallpaper Integration

Each theme can include a wallpaper that auto-applies to the clipboard background:

```
File:     wallpaper.png (in theme root)
Size:     512 x 512 px minimum (will be scaled to fit)
Style:    Dark, atmospheric, matches character theme
Opacity:  Applied at 25% via WallpaperBg layer in XAML
Overlay:  Auto-generated radial gradient from dominant color
Frost:    Gaussian blur header overlay auto-generated
```

### Sparky Wallpaper Prompt:
```
Dark atmospheric background, 512x512 pixels. Deep dark purple (#1a1a2e) and
navy blue (#16213e) gradient. Subtle electric lightning vein patterns in
cyan (#00E5FF) and yellow (#FFD700) scattered across like neural network
connections. Small pixel-art stars and electric sparkle dots. Dark stormy
clouds at top. No characters, no text. Premium cyberpunk electric aesthetic.
```

---

## Full Manifest Template

```json
{
  "name": "[CHARACTER_NAME] Theme",
  "author": "FlyShelf",
  "version": "1.0.0",
  "description": "[Description of the theme]",
  "license": "Personal Use Only",
  "character": "[CHARACTER_NAME]",
  "tags": ["[tag1]", "[tag2]", "pixel-art", "mascot"],
  "wallpaper": "wallpaper.png",
  "animations": {
    "idle": {
      "file": "sprites/idle.gif",
      "width": 48, "height": 48,
      "placement": "header-left",
      "loop": true
    },
    "sleeping": {
      "file": "sprites/sleeping.gif",
      "width": 48, "height": 48,
      "placement": "header-left",
      "loop": true
    },
    "wake_up": {
      "file": "sprites/wake_up.gif",
      "width": 48, "height": 48,
      "placement": "header-left",
      "loop": false,
      "durationMs": 500
    },
    "delete": {
      "file": "sprites/delete.gif",
      "width": 72, "height": 72,
      "placement": "center-overlay",
      "loop": false,
      "trigger": "on-delete",
      "durationMs": 1000
    },
    "header_reaction": {
      "file": "sprites/header_reaction.gif",
      "width": 48, "height": 48,
      "placement": "header-left",
      "loop": false,
      "trigger": "on-delete",
      "durationMs": 1000
    },
    "copy": {
      "file": "sprites/copy.gif",
      "width": 36, "height": 36,
      "placement": "header-right",
      "loop": false,
      "trigger": "on-copy",
      "durationMs": 500
    },
    "insert": {
      "file": "sprites/insert.gif",
      "width": 48, "height": 48,
      "placement": "header-left",
      "loop": false,
      "trigger": "on-insert",
      "durationMs": 800
    },
    "search": {
      "file": "sprites/search.gif",
      "width": 42, "height": 42,
      "placement": "header-left",
      "loop": true,
      "trigger": "on-search"
    }
  },
  "placements": {
    "header-left":     { "anchor": "top-left",  "offsetX": 8,   "offsetY": 6  },
    "header-right":    { "anchor": "top-right", "offsetX": -80, "offsetY": 4  },
    "center-overlay":  { "anchor": "center",    "offsetX": 0,   "offsetY": 0  },
    "bottom-scroll":   { "anchor": "bottom-left","offsetX": 10, "offsetY": -10 }
  }
}
```

---

## Conversion Pipeline

After generating sprite sheets with AI or Aseprite:

```bash
# Convert each sprite sheet to animated GIF
python livetheme/sprite2gif.py sprites/idle_sheet.png      sprites/idle.gif      --frames 12 --fps 8
python livetheme/sprite2gif.py sprites/sleeping_sheet.png   sprites/sleeping.gif  --frames 6  --fps 4
python livetheme/sprite2gif.py sprites/wake_up_sheet.png    sprites/wake_up.gif   --frames 6  --fps 12 --no-loop
python livetheme/sprite2gif.py sprites/delete_sheet.png     sprites/delete.gif    --frames 15 --fps 15 --no-loop
python livetheme/sprite2gif.py sprites/reaction_sheet.png   sprites/header_reaction.gif --frames 10 --fps 12 --no-loop
python livetheme/sprite2gif.py sprites/copy_sheet.png       sprites/copy.gif      --frames 8  --fps 12 --no-loop
python livetheme/sprite2gif.py sprites/insert_sheet.png     sprites/insert.gif    --frames 10 --fps 10 --no-loop
python livetheme/sprite2gif.py sprites/search_sheet.png     sprites/search.gif    --frames 8  --fps 8

# Scale up tiny sprites for crisp display:
python livetheme/sprite2gif.py sprites/idle_sheet.png sprites/idle.gif --frames 12 --fps 8 --scale 2
```

---

## Creating a New Character Theme (Checklist)

1. [ ] Choose character and define visual identity (colors, features)
2. [ ] Write frame-by-frame descriptions for ALL 8 animation states above
3. [ ] Generate sprite sheets (AI or hand-drawn)
4. [ ] Convert sprite sheets to GIFs using `sprite2gif.py`
5. [ ] Create wallpaper image
6. [ ] Create `manifest.json` from template above
7. [ ] Create `preview.png` (256x256 theme picker thumbnail)
8. [ ] Place everything in `%AppData%/FlyShelf/Themes/[theme-name]/`
9. [ ] Test: activate theme in HubWindow -> Mascot Themes
10. [ ] Test all triggers: delete, copy, insert, search, idle timeout

---

## Planned Characters

| Character | Style | Signature Move (Delete) | Status |
|-----------|-------|------------------------|--------|
| Sparky    | Electric Spark | Thunderbolt lightning strike | Testing |
| Golem     | Rock Giant | Ground smash with shockwave | Planned |
| Fire Mage | Wizard | Fireball throw | Planned |
| Shinobi   | Ninja | Shadow energy blast | Planned |
| Aura Fighter| Martial Artist | Energy beam blast | Planned |
| Cat       | Pet | Angry paw swipe with scratch marks | Planned |
| Robot     | Sci-fi | Laser beam from eye | Planned |
| Mage      | Fantasy | Magic spell with sparkles | Planned |

---

## Quality Checklist for Sprites

- [ ] All frames same canvas size (no shifting)
- [ ] Character position consistent across frames (no jitter)
- [ ] 1px black outline on character (defines edges on any background)
- [ ] Transparent background (alpha channel in PNG, transparent index in GIF)
- [ ] Limited color palette (16-32 colors for cohesive look)
- [ ] No anti-aliasing (keeps pixel art crisp at any scale)
- [ ] Clean pixel grid (no sub-pixel artifacts)
- [ ] Motion feels natural (ease-in/out on movements)
- [ ] Delete VFX is dramatic and satisfying
- [ ] Idle animation is subtle and not distracting
