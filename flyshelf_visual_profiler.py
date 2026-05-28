import os
import sys
import time
import re
import uuid
import threading
import win32gui
import win32ui
import win32con
import win32api
import numpy as np
import cv2

# High-DPI Awareness
try:
    from ctypes import windll
    windll.shcore.SetProcessDpiAwareness(1)
except Exception:
    pass

class TerminalColor:
    CYAN = '\033[96m'
    GREEN = '\033[92m'
    YELLOW = '\033[93m'
    RED = '\033[91m'
    BOLD = '\033[1m'
    PURPLE = '\033[95m'
    END = '\033[0m'

LOG_PATH = os.path.expandvars(r'%APPDATA%\FlyShelf\Logs\activity_log.txt')
REPORT_PATH = r"E:\exeapps\FlyShelf\spawning_visual_profile_report.md"

# Force stdout to use utf-8 to handle box-drawing characters in Windows command prompt
try:
    if hasattr(sys.stdout, 'reconfigure'):
        sys.stdout.reconfigure(encoding='utf-8')
except Exception:
    pass

def print_header(title):
    try:
        print(f"\n{TerminalColor.BOLD}{TerminalColor.PURPLE}╔═{'═' * len(title)}═╗")
        print(f"║ {title} ║")
        print(f"╚═{'═' * len(title)}═╝{TerminalColor.END}")
    except Exception:
        # Fallback to standard ASCII characters if console doesn't support Unicode
        print(f"\n{TerminalColor.BOLD}{TerminalColor.PURPLE}+-{'-' * len(title)}-+")
        print(f"| {title} |")
        print(f"+-{'-' * len(title)}-+{TerminalColor.END}")

def print_status(msg):
    print(f"{TerminalColor.CYAN}[*] {msg}{TerminalColor.END}")

def print_success(msg):
    print(f"{TerminalColor.GREEN}[+] {msg}{TerminalColor.END}")

def print_warning(msg):
    print(f"{TerminalColor.YELLOW}[!] {msg}{TerminalColor.END}")

def print_error(msg):
    print(f"{TerminalColor.RED}[-]{TerminalColor.BOLD} {msg}{TerminalColor.END}")

def get_bottom_left_rect(width=600, height=800):
    """Calculates the screen region for the bottom-left area of the primary monitor."""
    # Get primary monitor work area
    w_w = win32api.GetSystemMetrics(win32con.SM_CXSCREEN)
    w_h = win32api.GetSystemMetrics(win32con.SM_CYSCREEN)
    # Bottom-left crop coordinates
    x = 0
    y = w_h - height
    return (x, y, width, height)

def capture_screen_gdi(rect):
    """High-speed pure Win32 GDI screenshot capture of screen region."""
    x, y, w, h = rect
    hdesktop = win32gui.GetDesktopWindow()
    hwindc = win32gui.GetWindowDC(hdesktop)
    srcdc = win32ui.CreateDCFromHandle(hwindc)
    memdc = srcdc.CreateCompatibleDC()
    bmp = win32ui.CreateBitmap()
    bmp.CreateCompatibleBitmap(srcdc, w, h)
    memdc.SelectObject(bmp)
    memdc.BitBlt((0, 0), (w, h), srcdc, (x, y), win32con.SRCCOPY)
    
    signedIntsArray = bmp.GetBitmapBits(True)
    img = np.frombuffer(signedIntsArray, dtype='uint8')
    img.shape = (h, w, 4)
    
    # Clean up handles instantly to prevent leaks
    srcdc.DeleteDC()
    memdc.DeleteDC()
    win32gui.ReleaseDC(hdesktop, hwindc)
    win32gui.DeleteObject(bmp.GetHandle())
    
    # Return BGR image (discard alpha channel)
    return cv2.cvtColor(img, cv2.COLOR_BGRA2BGR)

def parse_flyshelf_logs(session_start_time):
    """Reads activity_log.txt and extracts exact C# events since session started."""
    if not os.path.exists(LOG_PATH):
        return []
    
    csharp_events = []
    try:
        with open(LOG_PATH, 'r', encoding='utf-8', errors='ignore') as f:
            lines = f.readlines()
            
        for line in lines:
            match = re.match(r'^\[([\d\-:\.\s]+)\]\s+\[([^\]]+)\]\s+(.*)$', line)
            if match:
                ts_str, tag, msg = match.groups()
                # Parse timestamp
                try:
                    t_part, ms_part = ts_str.split('.')
                    struct_t = time.strptime(t_part, "%Y-%m-%d %H:%M:%S")
                    ts = time.mktime(struct_t) + int(ms_part)/1000.0
                except Exception:
                    continue
                
                if ts >= session_start_time:
                    csharp_events.append({
                        'timestamp': ts,
                        'tag': tag,
                        'message': msg
                    })
    except Exception as ex:
        print_warning(f"Failed to read FlyShelf logs: {ex}")
    
    return csharp_events

def trigger_summon_shortcut_raw():
    """Simulates Alt+C keystroke programmatically using native win32 keyboard events.
    Uses redundant safety releases to prevent stuck key repeats in Windows."""
    try:
        # Press Alt (VK_MENU = 18)
        win32api.keybd_event(win32con.VK_MENU, 0, 0, 0)
        # Press C (0x43 = 67)
        win32api.keybd_event(0x43, 0, 0, 0)
        time.sleep(0.10) # 100ms delay so OS registers keydown reliably
        # Release C
        win32api.keybd_event(0x43, 0, win32con.KEYEVENTF_KEYUP, 0)
        # Release Alt
        win32api.keybd_event(win32con.VK_MENU, 0, win32con.KEYEVENTF_KEYUP, 0)
        
        # Redundant safety releases to guarantee keyup is registered by Windows
        time.sleep(0.05)
        win32api.keybd_event(0x43, 0, win32con.KEYEVENTF_KEYUP, 0)
        win32api.keybd_event(win32con.VK_MENU, 0, win32con.KEYEVENTF_KEYUP, 0)
    except Exception as ex:
        print(f"Keystroke simulation error: {ex}")

def is_flyshelf_onscreen():
    """Checks if the FlyShelf window is visible onscreen (not translated offscreen)."""
    try:
        hwnd = win32gui.FindWindow(None, "FlyShelf")
        if hwnd:
            if win32gui.IsWindowVisible(hwnd):
                rect = win32gui.GetWindowRect(hwnd)
                # Left coordinate > -10000 indicates it's onscreen
                if rect[0] > -10000 and rect[1] > -10000:
                    return hwnd
    except Exception:
        pass
    return None

def trigger_summon_shortcut():
    """Background helper to trigger exactly 20 rapid summons and dismissals programmatically to thoroughly stress-test spawning layers."""
    # Baseline sleep to establish a clean empty screen visual baseline
    time.sleep(0.5)
    
    total_cycles = 20
    for i in range(1, total_cycles + 1):
        # 1. Summon
        print_status(f"🤖 AUTO-TRIGGER: Simulating Alt+C for SUMMON {i}/{total_cycles}...")
        trigger_summon_shortcut_raw()
        print_success(f"Sent Alt+C keypress for SUMMON {i}/{total_cycles}!")
        time.sleep(0.32)  # Wait 320ms for the 350ms unified fade to settle almost fully
        
        # 2. Dismiss (except on the very last cycle so the final window stays settled onscreen)
        if i < total_cycles:
            print_status(f"🤖 AUTO-TRIGGER: Simulating Alt+C for DISMISS {i}/{total_cycles}...")
            trigger_summon_shortcut_raw()
            print_success(f"Sent Alt+C keypress for DISMISS {i}/{total_cycles}!")
            time.sleep(0.28)  # Wait 280ms for the dismiss offscreen render pass to complete
        else:
            print_success(f"🤖 AUTO-TRIGGER: Rapid stress sequence of {total_cycles} cycles complete!")

def run_profiler():
    print_header("FlyShelf Highly Advanced Spawning Visual Profiler")
    
def get_latest_csharp_log_timestamp():
    """Reads the very last line of activity_log.txt and returns its Unix timestamp.
    This protects against any clock skew or NTP offsets between Python and the C# app."""
    if not os.path.exists(LOG_PATH):
        return time.time()
    try:
        with open(LOG_PATH, 'r', encoding='utf-8', errors='ignore') as f:
            lines = f.readlines()
        for line in reversed(lines):
            match = re.match(r'^\[([\d\-:\.\s]+)\]\s+\[([^\]]+)\]\s+(.*)$', line)
            if match:
                ts_str, _, _ = match.groups()
                t_part, ms_part = ts_str.split('.')
                struct_t = time.strptime(t_part, "%Y-%m-%d %H:%M:%S")
                return time.mktime(struct_t) + int(ms_part)/1000.0
    except Exception:
        pass
    return time.time()

def run_profiler():
    print_header("FlyShelf Highly Advanced Spawning Visual Profiler")
    
    # 0. Pre-check window state: if already onscreen, automatically dismiss it
    hwnd = is_flyshelf_onscreen()
    if hwnd:
        print_warning("FlyShelf window is currently onscreen. Automatically dismissing it first...")
        trigger_summon_shortcut_raw()
        time.sleep(0.7)  # Wait for it to fully hide and settle
        print_success("FlyShelf dismissed. Proceeding with clean summon profiling.")
    
    # Secure log timestamp baseline from C# to eliminate C#-Python clock skew
    session_start_time = get_latest_csharp_log_timestamp()
    
    # Clean up output folders
    out_dir = r"E:\exeapps\FlyShelf\profiler_frames"
    if not os.path.exists(out_dir):
        os.makedirs(out_dir)
    else:
        for f in os.listdir(out_dir):
            if f.endswith(".png"):
                os.remove(os.path.join(out_dir, f))
                
    rect = get_bottom_left_rect()
    print_status(f"Profiler region configured at Bottom-Left: {rect}")
    print_warning("PREPARING GDI SCREEN RECORDER...")
    print(f"\n{TerminalColor.BOLD}{TerminalColor.YELLOW}👉 RECORDER STARTING! ALT+C WILL BE AUTOMATICALLY TRIGGERED IN 0.5 SECONDS.{TerminalColor.END}")
    print(f"{TerminalColor.BOLD}{TerminalColor.YELLOW}   (You do not need to press anything! just sit back and watch.){TerminalColor.END}\n")
    
    # Start thread to automatically trigger summoning 500ms after recording begins
    trigger_thread = threading.Thread(target=trigger_summon_shortcut)
    trigger_thread.daemon = True
    trigger_thread.start()
    
    # Record screen at maximum possible speed for 5 seconds
    frames = []
    timestamps = []
    
    start_rec = time.perf_counter()
    duration = 13.0  # Record for 13.0 seconds
    
    while time.perf_counter() - start_rec < duration:
        t_frame = time.perf_counter()
        img = capture_screen_gdi(rect)
        frames.append(img)
        timestamps.append(t_frame)
        # Small sleep to yield CPU and maintain consistency (~100 FPS cap)
        time.sleep(0.005)
        
    end_rec = time.perf_counter()
    fps = len(frames) / (end_rec - start_rec)
    print_success(f"Successfully recorded {len(frames)} frames over {end_rec - start_rec:.2f}s ({fps:.1f} FPS)")
    
    # 2. Image analysis
    print_status("Analyzing frame buffers for visual transitions and double-refreshes...")
    
    # Convert frames to grayscale for absolute diff calculations
    grays = [cv2.cvtColor(f, cv2.COLOR_BGR2GRAY) for f in frames]
    
    # Crop to where FlyShelf actually appears (e.g. bottom-left 400x500 of the captured 600x800 rect)
    crop_h, crop_w = 600, 450
    crops = [g[-crop_h:, :crop_w] for g in grays]
    
    intensities = [np.mean(c) for c in crops]
    
    diffs = []
    for i in range(len(crops) - 1):
        diff = cv2.absdiff(crops[i+1], crops[i])
        mae = np.mean(diff)
        diffs.append(mae)
        
    # Analyze state changes
    # Baseline intensity is when screen is empty (usually higher or steady background wallpaper)
    # The clipboard window is dark, so average intensity drops significantly when it shows
    
    summon_idx = -1
    dismiss_idx = -1
    double_refresh_indices = []
    
    # Thresholds
    drop_threshold = 5.0  # Drop in intensity indicates window appeared
    rise_threshold = 4.0  # Rise in intensity indicates window disappeared or flashed
    
    print_status("Scanning frame timeline for intensity changes...")
    
    # Timeline trace list
    visual_timeline = []
    
    last_summon_time_ms = -99999.0
    is_in_summon_animation_phase = False
    
    for i in range(1, len(intensities)):
        val = intensities[i]
        prev_val = intensities[i-1]
        diff = val - prev_val
        t_ms = (timestamps[i] - timestamps[0]) * 1000.0
        
        # 1. Window Appears / Pop (intensity drops significantly)
        if diff < -drop_threshold:
            if not is_in_summon_animation_phase:
                is_in_summon_animation_phase = True
                event_name = 'Window Appears (Pop)'
                # If the drop is extremely large, it's a sudden opaque pop!
                if diff < -25.0:
                    event_name = '⚠️ Window Opaque Composition Pop (Sudden Appearance)'
                
                # If we had a summon within 360ms, this is a double-refresh re-summon!
                if t_ms - last_summon_time_ms < 360:
                    event_name = '⚠️ Window Re-Summon Double-Refresh (Pop)'
                    
                last_summon_time_ms = t_ms
                if summon_idx == -1:
                    summon_idx = i
                
                visual_timeline.append({
                    'frame': i,
                    'time_ms': t_ms,
                    'event': event_name,
                    'intensity': val,
                    'diff': diff
                })
                cv2.imwrite(os.path.join(out_dir, f"frame_{i:04d}_summon_pop.png"), frames[i])
            
        # 2. Window Vanishes / Flicker (intensity rises significantly)
        elif diff > rise_threshold:
            is_in_summon_animation_phase = False
            event_name = 'Window Vanishes (Dismiss/Hide)'
            # If this rise occurs within 300ms of the last summon, it is a composition flicker/double-refresh!
            if t_ms - last_summon_time_ms < 300:
                event_name = '⚠️ Window Vanishes Composition Reset (Flicker)'
                double_refresh_indices.append(i)
                dismiss_idx = i
                
            visual_timeline.append({
                'frame': i,
                'time_ms': t_ms,
                'event': event_name,
                'intensity': val,
                'diff': diff
            })
            cv2.imwrite(os.path.join(out_dir, f"frame_{i:04d}_dismiss_flicker.png"), frames[i])

    # Detect final settlement
    settled_idx = -1
    # Settled when motion (diffs) goes below 0.15 for 8 consecutive frames after last summon
    if summon_idx != -1:
        for idx in range(summon_idx + 1, len(diffs)):
            if all(d < 0.15 for d in diffs[idx:idx+8]):
                settled_idx = idx
                t_ms = (timestamps[idx] - timestamps[0]) * 1000.0
                visual_timeline.append({
                    'frame': idx,
                    'time_ms': t_ms,
                    'event': 'Animation Settles (Stable Frame)',
                    'intensity': intensities[idx],
                    'diff': diffs[idx]
                })
                cv2.imwrite(os.path.join(out_dir, f"frame_{idx:04d}_settled.png"), frames[idx])
                break

    # 3. Parse and correlate C# application logs
    # Defer log reading by 2.5s to guarantee the C#'s 2-second background flush timer has fully committed all summon events to disk!
    print_status("Waiting 2.5s for FlyShelf C# process to flush background log buffers to disk...")
    time.sleep(2.5)
    print_status("Retrieving FlyShelf application process log traces...")
    csharp_traces = parse_flyshelf_logs(session_start_time)
    
    # Sensor Fusion: Cross-reference visual MAE changes with C# process log telemetry.
    csharp_summon_calls = [t for t in csharp_traces if "ShowNearPosition entered" in t['message']]
    
    # A C#-level double-summon is when two WndProc hotkeys are processed within 700ms of each other.
    csharp_confirmed_double = False
    for idx in range(len(csharp_summon_calls) - 1):
        time_diff = (csharp_summon_calls[idx+1]['timestamp'] - csharp_summon_calls[idx]['timestamp']) * 1000.0
        if time_diff < 700:
            csharp_confirmed_double = True
            break
            
    # Check if a visual double-refresh or opaque pop was detected
    real_flicker_detected = any('⚠️' in vt['event'] for vt in visual_timeline)
    has_double_refresh = real_flicker_detected or csharp_confirmed_double
    
    # Re-verify settled index if not settled yet
    if settled_idx == -1 and summon_idx != -1:
        settled_idx = len(diffs) - 1
    
    # 4. Generate Visual Performance Report
    print_header("Visual Performance Diagnostic Findings")
    
    # Build markdown report content
    report_content = []
    report_content.append("# FlyShelf Highly Advanced Spawning Visual Profile Report")
    report_content.append(f"\n*Generated dynamically on: {time.strftime('%Y-%m-%d %H:%M:%S')}*")
    report_content.append(f"\n## Capture Statistics\n* **Recording Frame Rate:** {fps:.1f} FPS")
    report_content.append(f"* **Total Frames Captured:** {len(frames)}")
    report_content.append(f"* **Analysis Crop Region:** Bottom-Left ({crop_w}x{crop_h} px)")
    
    report_content.append("\n## Visual Spawning Timeline (Screen Capture DWM Frames)")
    report_content.append("| Frame Index | Elasped Time (ms) | Visual Event Description | Avg Intensity | Delta |")
    report_content.append("|---|---|---|---|---|")
    
    for vt in visual_timeline:
        report_content.append(f"| **{vt['frame']:03d}** | {vt['time_ms']:.1f} ms | **{vt['event']}** | {vt['intensity']:.2f} | {vt['diff']:+.2f} |")
        # Print to console too
        print_success(f"Frame {vt['frame']:03d} (+{vt['time_ms']:.1f}ms): {vt['event']} (Intensity: {vt['intensity']:.2f}, Diff: {vt['diff']:+.2f})")
        
    report_content.append("\n## C# Telemetry Process Logs Correlation")
    report_content.append("| Timestamp | Elapsed (ms) | Subsystem Tag | Telemetry Event Message |")
    report_content.append("|---|---|---|---|")
    
    session_start_unix = session_start_time
    for trace in csharp_traces:
        elapsed_ms = (trace['timestamp'] - session_start_unix) * 1000.0
        report_content.append(f"| {time.strftime('%H:%M:%S', time.localtime(trace['timestamp']))}.{int((trace['timestamp']%1)*1000):03d} | {elapsed_ms:.1f} ms | `[{trace['tag']}]` | {trace['message']} |")
        print(f"  {TerminalColor.CYAN}[C# Log]{TerminalColor.END} +{elapsed_ms:.1f}ms `[{trace['tag']}]` {trace['message']}")

    # 5. Core Verdict
    report_content.append("\n## Visual Diagnostics & Verdict")
    
    if has_double_refresh:
        verdict = "⚠️ **DOUBLE REFRESH DETECTED!** The clipboard window flashed opaque, disappeared, and re-summoned within a few frames."
        print_warning(verdict)
    else:
        verdict = "✅ **GLITCH-FREE SUMMONING ACHIEVED!** The window faded in unified and settled in-place without any double-activation flashes or offscreen resets."
        print_success(verdict)
        
    report_content.append(f"\n### Verdict:\n{verdict}")
    
    # Timing analysis
    if summon_idx != -1 and settled_idx != -1:
        sum_time = (timestamps[summon_idx] - timestamps[0]) * 1000.0
        settle_time = (timestamps[settled_idx] - timestamps[0]) * 1000.0
        anim_duration = settle_time - sum_time
        
        report_content.append(f"\n### Spawning Transitions Timing Profile:")
        report_content.append(f"* **Total Spawning Latency:** {settle_time:.1f} ms (from start of recording to settled state)")
        report_content.append(f"* **First Frame Rendered Onscreen:** +{sum_time:.1f} ms (Window appeared)")
        report_content.append(f"* **Transition Settle Duration:** {anim_duration:.1f} ms (Fluid ease settle duration)")
        
        print_success(f"First frame rendered: +{sum_time:.1f}ms")
        print_success(f"Visual transition settled: {anim_duration:.1f}ms")
    else:
        report_content.append(f"\n*Note: Could not calculate complete timing profile (Window did not settle within capture timeframe).*")
        
    # Write to report file
    with open(REPORT_PATH, 'w', encoding='utf-8') as f:
        f.write("\n".join(report_content))
        
    print_success(f"\nHighly advanced visual profile report successfully written to: {REPORT_PATH}")
    print_status(f"Individual diagnostic frames saved inside folder: {out_dir}")

if __name__ == '__main__':
    run_profiler()
