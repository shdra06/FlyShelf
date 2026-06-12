"""
FlyShelf Spawn Jitter Analyzer
Parses spawn_profile.txt and generates visual analysis of animation smoothness.
Shows per-frame opacity values, timing gaps, and detects jitter patterns.

Usage: python spawn_analyzer.py
Output: Opens HTML report with charts in browser
"""

import os
import re
import sys
from pathlib import Path

PROFILE_PATH = os.path.join(
    os.environ.get("APPDATA", ""), "FlyShelf", "Logs", "spawn_profile.txt"
)

def parse_profiles(path):
    """Parse all spawn profiles from the log file."""
    profiles = []
    current = None
    
    with open(path, "r", encoding="utf-8", errors="replace") as f:
        lines = f.readlines()
    
    for line in lines:
        # Detect new profile
        m = re.search(r"SPAWN PROFILE #(\d+)", line)
        if m:
            if current:
                profiles.append(current)
            current = {
                "id": int(m.group(1)),
                "steps": [],
                "frames": [],
                "verdict": ""
            }
            continue
        
        if not current:
            continue
        
        # Parse pipeline steps
        step_m = re.search(r"([\d.]+)ms\s+\(\+\s*([\d.]+)ms\)\s+(\S+)", line)
        if step_m and "PIPELINE" not in line:
            current["steps"].append({
                "elapsed": float(step_m.group(1)),
                "delta": float(step_m.group(2)),
                "name": step_m.group(3)
            })
        
        # Parse frame timings (with opacity and slideY)
        frame_m = re.search(
            r"(\d+)\s+([\d.]+)\s+([\d.]+)\s+([\d.]+)\s+([\d.-]+)\s+(.+?)$",
            line.strip()
        )
        if frame_m and "Frame" not in line and "─" not in line:
            current["frames"].append({
                "num": int(frame_m.group(1)),
                "delta": float(frame_m.group(2)),
                "total": float(frame_m.group(3)),
                "opacity": float(frame_m.group(4)),
                "slideY": float(frame_m.group(5)),
                "status": frame_m.group(6).strip()
            })
        
        # Fallback: frame without opacity/slideY (old format)
        if not frame_m:
            frame_old = re.search(
                r"(\d+)\s+([\d.]+)\s+([\d.]+)\s+(.+?)$",
                line.strip()
            )
            if frame_old and "Frame" not in line and "─" not in line and "Frames:" not in line:
                status = frame_old.group(4).strip()
                if any(k in status for k in ["OK", "DROPPED", "LATE", "POST"]):
                    current["frames"].append({
                        "num": int(frame_old.group(1)),
                        "delta": float(frame_old.group(2)),
                        "total": float(frame_old.group(3)),
                        "opacity": -1,
                        "slideY": 0,
                        "status": status
                    })
        
        # Parse verdict
        if "VERDICT" in line:
            current["verdict"] = line.strip()
    
    if current:
        profiles.append(current)
    
    return profiles


def generate_html(profiles):
    """Generate HTML report with inline SVG charts."""
    html = []
    html.append("""<!DOCTYPE html>
<html><head><meta charset="utf-8">
<title>FlyShelf Spawn Jitter Analysis</title>
<style>
body { font-family: 'Segoe UI', sans-serif; background: #0d1117; color: #c9d1d9; padding: 20px; }
h1 { color: #58a6ff; }
h2 { color: #8b949e; border-bottom: 1px solid #21262d; padding-bottom: 8px; }
.profile { background: #161b22; border: 1px solid #30363d; border-radius: 8px; padding: 16px; margin: 16px 0; }
.smooth { border-left: 4px solid #3fb950; }
.jitter { border-left: 4px solid #f85149; }
.minor { border-left: 4px solid #d29922; }
.chart { margin: 12px 0; }
.bar { display: inline-block; margin-right: 1px; vertical-align: bottom; min-width: 4px; border-radius: 2px 2px 0 0; }
.bar-ok { background: #238636; }
.bar-late { background: #d29922; }
.bar-drop { background: #f85149; }
.bar-post { background: #484f58; }
.opacity-chart { margin: 8px 0; }
table { border-collapse: collapse; font-size: 12px; }
td, th { padding: 4px 8px; border: 1px solid #30363d; }
th { background: #21262d; }
.drop-cell { background: #f8514922; color: #f85149; font-weight: bold; }
.summary { font-size: 18px; margin: 20px 0; padding: 16px; background: #161b22; border-radius: 8px; }
.opacity-dot { display: inline-block; width: 6px; height: 6px; border-radius: 50%; margin-right: 1px; vertical-align: bottom; }
</style></head><body>
<h1>🔍 FlyShelf Spawn Jitter Analysis</h1>
""")
    
    # Summary
    smooth = sum(1 for p in profiles if "SMOOTH" in p["verdict"])
    total = len(profiles)
    html.append(f"""
<div class="summary">
    <strong>Total Spawns:</strong> {total} &nbsp;|&nbsp;
    <strong>Smooth:</strong> <span style="color:#3fb950">{smooth}</span> &nbsp;|&nbsp;
    <strong>Jittery:</strong> <span style="color:#f85149">{total - smooth}</span> &nbsp;|&nbsp;
    <strong>Smooth Rate:</strong> <span style="color:{'#3fb950' if smooth/max(total,1) > 0.7 else '#f85149'}">{smooth/max(total,1)*100:.0f}%</span>
</div>
""")
    
    # Per-profile analysis
    for p in profiles:
        verdict_class = "smooth" if "SMOOTH" in p["verdict"] else ("minor" if "MINOR" in p["verdict"] else "jitter")
        
        html.append(f'<div class="profile {verdict_class}">')
        html.append(f'<h2>Spawn #{p["id"]}</h2>')
        
        # Frame timing bar chart
        html.append('<div class="chart"><strong>Frame Timings (height = delta ms, max 50ms):</strong><br>')
        for f in p["frames"]:
            h = min(f["delta"] / 50 * 100, 100)
            cls = "bar-ok"
            if "DROP" in f["status"] and "POST" not in f["status"]:
                cls = "bar-drop"
            elif "LATE" in f["status"] and "POST" not in f["status"]:
                cls = "bar-late"
            elif "POST" in f["status"]:
                cls = "bar-post"
            html.append(f'<div class="bar {cls}" style="height:{max(h,3):.0f}px;width:8px" title="F{f["num"]}: {f["delta"]:.1f}ms @ {f["total"]:.0f}ms (opacity={f["opacity"]:.2f})"></div>')
        html.append('</div>')
        
        # Opacity curve visualization
        if any(f["opacity"] >= 0 for f in p["frames"]):
            html.append('<div class="opacity-chart"><strong>Opacity Curve (dot height = opacity 0→1):</strong><br>')
            html.append('<div style="height:40px;position:relative;border-bottom:1px solid #30363d">')
            for f in p["frames"]:
                if f["opacity"] >= 0:
                    bottom = f["opacity"] * 38
                    color = "#f85149" if "DROP" in f["status"] and "POST" not in f["status"] else "#58a6ff"
                    html.append(f'<div style="position:absolute;left:{f["num"]*9}px;bottom:{bottom:.0f}px;width:6px;height:6px;border-radius:50%;background:{color}" title="F{f["num"]}: opacity={f["opacity"]:.3f}"></div>')
            html.append('</div></div>')
        
        # Frame details table (only show drops and their neighbors)
        drop_indices = {i for i, f in enumerate(p["frames"]) if "DROP" in f["status"] and "POST" not in f["status"]}
        show_indices = set()
        for di in drop_indices:
            for j in range(max(0, di-1), min(len(p["frames"]), di+3)):
                show_indices.add(j)
        
        if drop_indices:
            html.append('<table><tr><th>Frame</th><th>Delta(ms)</th><th>Total(ms)</th><th>Opacity</th><th>SlideY</th><th>Status</th></tr>')
            for i in sorted(show_indices):
                f = p["frames"][i]
                cls = ' class="drop-cell"' if i in drop_indices else ''
                html.append(f'<tr><td{cls}>{f["num"]}</td><td{cls}>{f["delta"]:.1f}</td><td{cls}>{f["total"]:.0f}</td><td>{f["opacity"]:.3f}</td><td>{f["slideY"]:.2f}</td><td{cls}>{f["status"]}</td></tr>')
            html.append('</table>')
        
        # Verdict
        html.append(f'<p style="margin-top:8px;opacity:0.7">{p["verdict"]}</p>')
        html.append('</div>')
    
    html.append("</body></html>")
    return "\n".join(html)


def main():
    if not os.path.exists(PROFILE_PATH):
        print(f"❌ Profile not found: {PROFILE_PATH}")
        print("Run FlyShelf and summon/dismiss the clipboard a few times first.")
        sys.exit(1)
    
    print(f"📊 Parsing: {PROFILE_PATH}")
    profiles = parse_profiles(PROFILE_PATH)
    print(f"   Found {len(profiles)} spawn profiles")
    
    if not profiles:
        print("❌ No profiles found in file")
        sys.exit(1)
    
    # Generate HTML report
    html = generate_html(profiles)
    
    output_path = os.path.join(os.path.dirname(PROFILE_PATH), "spawn_analysis.html")
    with open(output_path, "w", encoding="utf-8") as f:
        f.write(html)
    
    print(f"✅ Report saved: {output_path}")
    
    # Open in browser
    os.startfile(output_path)
    
    # Quick console summary
    smooth = sum(1 for p in profiles if "SMOOTH" in p["verdict"])
    print(f"\n📈 Summary: {smooth}/{len(profiles)} smooth ({smooth/len(profiles)*100:.0f}%)")
    
    for p in profiles:
        drops = [f for f in p["frames"] if "DROP" in f["status"] and "POST" not in f["status"]]
        if drops:
            for d in drops:
                opacity_jump = ""
                idx = d["num"]
                if idx > 0 and idx < len(p["frames"]) and p["frames"][idx-1]["opacity"] >= 0:
                    prev_op = p["frames"][idx-1]["opacity"]
                    curr_op = d["opacity"]
                    opacity_jump = f" (opacity {prev_op:.2f}→{curr_op:.2f}, jump={curr_op-prev_op:.3f})"
                print(f"   #{p['id']} Frame {d['num']}: {d['delta']:.1f}ms gap at {d['total']:.0f}ms{opacity_jump}")


if __name__ == "__main__":
    main()
