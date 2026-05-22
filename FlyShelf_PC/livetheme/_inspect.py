from PIL import Image
import os, sys

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
        trans = img.info.get("transparency", "NONE")
        disp = img.info.get("disposal", "NONE")
        
        # Check first frame for transparency
        frame0 = img.convert("RGBA")
        alpha_data = list(frame0.split()[3].getdata())
        transparent_pct = sum(1 for a in alpha_data if a < 128) / len(alpha_data) * 100
        
        print(f"  {f}: size={img.size}, frames={img.n_frames}, mode={img.mode}")
        print(f"    transparency_idx={trans}, disposal={disp}")
        print(f"    frame0 transparent_pixels={transparent_pct:.1f}%")
        print(f"    file_size={os.path.getsize(path):,} bytes")

        # Check if background pixels are solid colored (not transparent)
        # Sample corner pixels
        corners = [(0,0), (img.width-1,0), (0,img.height-1), (img.width-1,img.height-1)]
        for cx, cy in corners:
            px = frame0.getpixel((cx, cy))
            print(f"    corner({cx},{cy}) = RGBA{px}")
