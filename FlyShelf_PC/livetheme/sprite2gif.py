#!/usr/bin/env python3
"""
sprite2gif.py v3 — AI Sprite Sheet → Transparent Animated GIF
For FlyShelf Mascot Theme Engine

Fixes: proper horizontal strip detection, no empty frames, clean transparency.
"""

import argparse
import sys
import os
from pathlib import Path
from collections import deque, Counter

try:
    from PIL import Image
except ImportError:
    print("ERROR: Pillow not installed. Run: pip install Pillow")
    sys.exit(1)


def sample_background_color(img):
    """Sample the dominant background color from corners and edges."""
    w, h = img.size
    samples = []
    cs = min(8, w // 6, h // 6)
    
    regions = [
        (0, 0, cs, cs), (w-cs, 0, w, cs),
        (0, h-cs, cs, h), (w-cs, h-cs, w, h),
        (w//2-cs, 0, w//2+cs, cs), (w//2-cs, h-cs, w//2+cs, h),
        (0, h//2-cs, cs, h//2+cs), (w-cs, h//2-cs, w, h//2+cs),
    ]
    
    rgba = img.convert("RGBA")
    for r in regions:
        crop = rgba.crop(r)
        for px in crop.getdata():
            if px[3] > 200:
                samples.append(px[:3])
    
    if not samples:
        return (255, 255, 255)
    
    rounded = [(r//4*4, g//4*4, b//4*4) for r, g, b in samples]
    counter = Counter(rounded)
    most_common = counter.most_common(1)[0][0]
    matching = [s for s in samples if all(abs(a-b) < 16 for a, b in zip(s, most_common))]
    if matching:
        return tuple(sum(c)//len(matching) for c in zip(*matching))
    return most_common


def remove_background(img, bg_color=None, tolerance=30):
    """Remove solid background using flood-fill from edges."""
    rgba = img.convert("RGBA")
    w, h = rgba.size
    if bg_color is None:
        bg_color = sample_background_color(rgba)
    
    pixels = rgba.load()
    
    def cdist(c1, c2):
        return sum((a-b)**2 for a, b in zip(c1, c2)) ** 0.5
    
    bg_mask = [[False]*w for _ in range(h)]
    queue = deque()
    
    for x in range(w):
        for y in [0, h-1]:
            px = pixels[x, y]
            if px[3] < 128 or cdist(px[:3], bg_color) < tolerance:
                if not bg_mask[y][x]:
                    bg_mask[y][x] = True
                    queue.append((x, y))
    for y in range(h):
        for x in [0, w-1]:
            px = pixels[x, y]
            if px[3] < 128 or cdist(px[:3], bg_color) < tolerance:
                if not bg_mask[y][x]:
                    bg_mask[y][x] = True
                    queue.append((x, y))
    
    while queue:
        cx, cy = queue.popleft()
        for dx, dy in [(-1,0),(1,0),(0,-1),(0,1)]:
            nx, ny = cx+dx, cy+dy
            if 0 <= nx < w and 0 <= ny < h and not bg_mask[ny][nx]:
                px = pixels[nx, ny]
                if px[3] < 128 or cdist(px[:3], bg_color) < tolerance:
                    bg_mask[ny][nx] = True
                    queue.append((nx, ny))
    
    for y in range(h):
        for x in range(w):
            if bg_mask[y][x]:
                pixels[x, y] = (0, 0, 0, 0)
    
    return rgba


def find_character_blobs(img):
    """
    Find isolated character blobs in a sprite sheet by looking for
    connected non-transparent regions after background removal.
    Returns list of (left, top, right, bottom) bounding boxes.
    """
    rgba = img.convert("RGBA")
    w, h = rgba.size
    pixels = rgba.load()
    
    # Create binary mask: True = has content (non-transparent)
    visited = [[False]*w for _ in range(h)]
    blobs = []
    
    for y in range(h):
        for x in range(w):
            if visited[y][x]:
                continue
            px = pixels[x, y]
            if px[3] < 100:
                visited[y][x] = True
                continue
            
            # BFS to find connected blob
            queue = deque([(x, y)])
            visited[y][x] = True
            min_x, min_y = x, y
            max_x, max_y = x, y
            pixel_count = 0
            
            while queue:
                cx, cy = queue.popleft()
                pixel_count += 1
                min_x = min(min_x, cx)
                min_y = min(min_y, cy)
                max_x = max(max_x, cx)
                max_y = max(max_y, cy)
                
                for dx, dy in [(-1,0),(1,0),(0,-1),(0,1),(-1,-1),(1,-1),(-1,1),(1,1)]:
                    nx, ny = cx+dx, cy+dy
                    if 0 <= nx < w and 0 <= ny < h and not visited[ny][nx]:
                        npx = pixels[nx, ny]
                        if npx[3] >= 100:
                            visited[ny][nx] = True
                            queue.append((nx, ny))
                        else:
                            visited[ny][nx] = True
            
            # Only keep blobs big enough to be a character
            if pixel_count >= 500:
                blobs.append((min_x, min_y, max_x+1, max_y+1, pixel_count))
    
    # Filter: keep only blobs at least 10% the size of the largest
    if blobs:
        max_count = max(b[4] for b in blobs)
        blobs = [b for b in blobs if b[4] >= max_count * 0.10]
    
    # Sort by x position (left to right), then y (top to bottom)
    blobs.sort(key=lambda b: (b[1] // (h//3 + 1), b[0]))
    return blobs


def extract_frames_smart(img, num_frames=0):
    """
    Smart frame extraction: removes background first, finds character blobs,
    and extracts each as a frame. Handles any grid layout.
    """
    bg = sample_background_color(img)
    print(f"  Background: RGB({bg[0]}, {bg[1]}, {bg[2]})")
    
    clean = remove_background(img, bg, tolerance=30)
    blobs = find_character_blobs(clean)
    
    if not blobs:
        print("  WARNING: No character blobs found!")
        return []
    
    print(f"  Found {len(blobs)} character blobs")
    
    # If num_frames specified, take only that many
    if num_frames > 0 and len(blobs) > num_frames:
        blobs = blobs[:num_frames]
    
    # Extract each blob as a frame
    frames = []
    for left, top, right, bottom, count in blobs:
        frame = clean.crop((left, top, right, bottom))
        if frame.getbbox():
            frames.append(frame)
    
    return frames


def normalize_frames(frames, target_size=48, padding=2):
    """
    Normalize all frames to the same square canvas.
    Centers each character consistently.
    """
    if not frames:
        return frames
    
    # Find max content dimensions across all frames
    max_w = max(f.width for f in frames)
    max_h = max(f.height for f in frames)
    
    out_size = target_size
    
    # Scale factor to fit in target_size - padding
    available = out_size - padding * 2
    scale = min(available / max_w, available / max_h)
    if scale > 3:
        scale = 3  # Cap upscale for pixel art
    
    normalized = []
    for f in frames:
        # Scale
        new_w = max(1, int(f.width * scale))
        new_h = max(1, int(f.height * scale))
        resample = Image.NEAREST if scale >= 1.5 else Image.LANCZOS
        scaled = f.resize((new_w, new_h), resample)
        
        # Center on canvas
        canvas = Image.new("RGBA", (out_size, out_size), (0, 0, 0, 0))
        paste_x = (out_size - new_w) // 2
        paste_y = (out_size - new_h) // 2
        canvas.paste(scaled, (paste_x, paste_y), scaled)
        
        # Verify frame isn't empty
        if canvas.getbbox() is None:
            continue
        
        normalized.append(canvas)
    
    return normalized


def make_gif(frames, output_path, fps=10, loop=True):
    """Create an animated GIF with proper transparency and disposal."""
    if not frames:
        print("  ERROR: No frames!")
        return False
    
    duration = max(20, int(1000 / fps))
    
    gif_frames = []
    for frame in frames:
        rgba = frame.convert("RGBA")
        
        # Find unused color for transparency key
        used = set()
        for px in rgba.getdata():
            if px[3] > 128:
                used.add(px[:3])
        
        bg = (255, 0, 255)
        if bg in used:
            for r in range(254, 0, -1):
                bg = (r, 0, r)
                if bg not in used:
                    break
        
        # Composite over bg color
        rgb_canvas = Image.new("RGB", rgba.size, bg)
        rgb_canvas.paste(rgba, mask=rgba.split()[3])
        
        # Quantize
        pal = rgb_canvas.quantize(colors=255, method=Image.MEDIANCUT)
        
        # Find transparency index
        palette = pal.getpalette()
        trans_idx = 0
        best_dist = float("inf")
        for i in range(len(palette)//3):
            r, g, b = palette[i*3], palette[i*3+1], palette[i*3+2]
            d = (r-bg[0])**2 + (g-bg[1])**2 + (b-bg[2])**2
            if d < best_dist:
                best_dist = d
                trans_idx = i
                if d == 0:
                    break
        
        # Apply transparency mask
        pal_data = list(pal.getdata())
        alpha_data = list(rgba.split()[3].getdata())
        for idx in range(len(alpha_data)):
            if alpha_data[idx] < 128:
                pal_data[idx] = trans_idx
        pal.putdata(pal_data)
        pal.info["transparency"] = trans_idx
        gif_frames.append(pal)
    
    gif_frames[0].save(
        output_path,
        save_all=True,
        append_images=gif_frames[1:],
        duration=duration,
        loop=0 if loop else 1,
        disposal=2,
        optimize=False
    )
    return True


def process(input_path, output_path, frames=0, fps=10, size=48, loop=True):
    """Process a single sprite sheet."""
    print(f"\n[LOAD] {os.path.basename(input_path)}")
    img = Image.open(input_path).convert("RGBA")
    print(f"  Image: {img.width}x{img.height}")
    
    # Smart extraction
    raw_frames = extract_frames_smart(img, num_frames=frames)
    print(f"  Extracted: {len(raw_frames)} frames")
    
    if not raw_frames:
        print("  FAIL: No frames extracted!")
        return False
    
    # Normalize
    final_frames = normalize_frames(raw_frames, target_size=size)
    print(f"  Normalized: {len(final_frames)} frames at {size}x{size}px")
    
    if not final_frames:
        print("  FAIL: All frames empty after normalization!")
        return False
    
    # Make GIF
    ok = make_gif(final_frames, output_path, fps=fps, loop=loop)
    if ok:
        sz = os.path.getsize(output_path)
        print(f"  [OK] {output_path} — {sz:,} bytes, {len(final_frames)} frames, {fps} FPS")
    return ok


def main():
    parser = argparse.ArgumentParser(description="Sprite Sheet → Animated GIF v3")
    parser.add_argument("input", help="Input sprite sheet PNG")
    parser.add_argument("output", help="Output GIF file")
    parser.add_argument("--frames", type=int, default=0, help="Number of frames")
    parser.add_argument("--fps", type=int, default=10, help="FPS (default: 10)")
    parser.add_argument("--size", type=int, default=48, help="Output frame size (default: 48)")
    parser.add_argument("--no-loop", action="store_true", help="One-shot animation")
    
    args = parser.parse_args()
    
    if not os.path.exists(args.input):
        print(f"ERROR: File not found: {args.input}")
        sys.exit(1)
    
    ok = process(args.input, args.output,
                 frames=args.frames, fps=args.fps,
                 size=args.size, loop=not args.no_loop)
    sys.exit(0 if ok else 1)


if __name__ == "__main__":
    main()
