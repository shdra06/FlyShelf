from PIL import Image
import os

themes_dir = os.path.join(os.environ["APPDATA"], "FlyShelf", "Themes")
for theme in ["mochi-starpuff", "sparky-electric"]:
    sprites_dir = os.path.join(themes_dir, theme, "sprites")
    if not os.path.isdir(sprites_dir):
        continue
    print(f"\n=== {theme} ===")
    for f in sorted(os.listdir(sprites_dir)):
        if not f.endswith(".gif"):
            continue
        path = os.path.join(sprites_dir, f)
        img = Image.open(path)
        print(f"  {f}: {img.n_frames} frames, size={img.size}")
        for i in range(img.n_frames):
            img.seek(i)
            frame = img.convert("RGBA")
            bbox = frame.getbbox()
            alpha = frame.split()[3]
            non_transparent = sum(1 for a in alpha.getdata() if a > 128)
            total = img.size[0] * img.size[1]
            pct = non_transparent / total * 100
            print(f"    frame {i}: bbox={bbox}, visible_pixels={pct:.1f}% ({non_transparent}/{total})")
