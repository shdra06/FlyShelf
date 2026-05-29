import cv2
import numpy as np
import os

video_path = r"E:\exeapps\FlyShelf\Recording 2026-05-28 212234.mp4"
output_dir = r"E:\exeapps\FlyShelf\final_glitch_frames"

if not os.path.exists(output_dir):
    os.makedirs(output_dir)

cap = cv2.VideoCapture(video_path)
if not cap.isOpened():
    print("Error opening video file")
    exit(1)

frame_idx = 0
while True:
    ret, frame = cap.read()
    if not ret:
        break
    
    frame_name = f"frame_{frame_idx:04d}.png"
    output_path = os.path.join(output_dir, frame_name)
    cv2.imwrite(output_path, frame)
    frame_idx += 1

cap.release()
print(f"Successfully extracted {frame_idx} frames to {output_dir}")

# Now perform intensity and motion analysis on the newly extracted frames
frame_files = sorted([f for f in os.listdir(output_dir) if f.endswith(".png")])

diffs = []
prev_gray = None
intensities = []

for idx, fname in enumerate(frame_files):
    fpath = os.path.join(output_dir, fname)
    img = cv2.imread(fpath)
    gray = cv2.cvtColor(img, cv2.COLOR_BGR2GRAY)
    
    if prev_gray is not None:
        diff = cv2.absdiff(gray, prev_gray)
        mae = np.mean(diff)
        diffs.append((idx, mae))
    
    # Bottom-left crop: from height-600 to height, and 0 to 500
    height, width = gray.shape
    bl_h = min(600, height)
    bl_w = min(500, width)
    crop = gray[height-bl_h:height, 0:bl_w]
    mean_val = np.mean(crop)
    intensities.append((idx, mean_val))
    
    prev_gray = gray

print("\n--- Motion / State Change Timeline ---")
in_motion = False
motion_start = -1

for idx, mae in diffs:
    if mae > 1.5:
        if not in_motion:
            in_motion = True
            motion_start = idx
    else:
        if in_motion:
            in_motion = False
            print(f"Motion detected from Frame {motion_start:03d} to {idx:03d} (Duration: {idx - motion_start} frames)")

print("\n--- Bottom-Left Region Intensity Timeline ---")
# Print frames where intensity shifts significantly
prev_val = None
for idx, val in intensities:
    if prev_val is not None:
        diff = abs(val - prev_val)
        if diff > 0.5:  # Slightly lower threshold to catch subtle flickers
            print(f"Frame {idx:03d}: Avg Intensity shifted from {prev_val:.2f} to {val:.2f} (diff={diff:.2f})")
    prev_val = val
