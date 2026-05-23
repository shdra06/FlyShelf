import os
import sys
if sys.platform == 'win32':
    try:
        sys.stdout.reconfigure(encoding='utf-8')
    except Exception:
        pass
import time
import re
import uuid
import statistics
import win32gui
import win32con
import win32api
import win32clipboard
from ctypes import windll, create_unicode_buffer, WINFUNCTYPE, c_int
from ctypes.wintypes import LPARAM, HWND, BOOL

# Windows High-DPI Awareness
try:
    windll.shcore.SetProcessDpiAwareness(1)
except Exception:
    pass

# Colors for terminal output
class Color:
    PURPLE = '\033[95m'
    CYAN = '\033[96m'
    DARKCYAN = '\033[36m'
    BLUE = '\033[94m'
    GREEN = '\033[92m'
    YELLOW = '\033[93m'
    RED = '\033[91m'
    BOLD = '\033[1m'
    UNDERLINE = '\033[4m'
    END = '\033[0m'

LOG_PATH = os.path.expandvars(r'%APPDATA%\FlyShelf\Logs\activity_log.txt')

def print_header(title):
    print(f"\n{Color.BOLD}{Color.PURPLE}╔═{'═' * len(title)}═╗")
    print(f"║ {title} ║")
    print(f"╚═{'═' * len(title)}═╝{Color.END}")

def print_status(msg):
    print(f"{Color.CYAN}[*] {msg}{Color.END}")

def print_success(msg):
    print(f"{Color.GREEN}[+] {msg}{Color.END}")

def print_warning(msg):
    print(f"{Color.YELLOW}[!] {msg}{Color.END}")

def print_error(msg):
    print(f"{Color.RED}[-]{Color.BOLD} {msg}{Color.END}")

# --- Win32 Window Finding Helpers ---
def find_main_window():
    """Finds the main FlyShelf window handle."""
    hwnd = win32gui.FindWindow(None, "FlyShelf")
    if hwnd:
        return hwnd
    # Fallback to class name iteration
    hwnds = []
    def enum_cb(h, l):
        class_name = win32gui.GetClassName(h)
        title = win32gui.GetWindowText(h)
        if "HwndWrapper" in class_name and title == "FlyShelf":
            hwnds.append(h)
        return True
    win32gui.EnumWindows(enum_cb, 0)
    return hwnds[0] if hwnds else None

def find_widget_window():
    """Finds the FlyShelf TaskbarWindow (widget) handle, which is a child of the taskbar."""
    # Find Taskbars
    taskbars = []
    def find_taskbars(h, l):
        cls = win32gui.GetClassName(h)
        if cls in ["Shell_TrayWnd", "Shell_SecondaryTrayWnd"]:
            taskbars.append(h)
        return True
    win32gui.EnumWindows(find_taskbars, 0)
    
    widget_hwnd = [None]
    WNDENUMPROC = WINFUNCTYPE(BOOL, HWND, LPARAM)
    
    def enum_children(hwnd, lParam):
        title = win32gui.GetWindowText(hwnd)
        cls = win32gui.GetClassName(hwnd)
        if title == "TaskbarWindow" or "TaskbarWindow" in cls:
            widget_hwnd[0] = hwnd
            return False  # Stop enumeration
        return True
        
    for tb in taskbars:
        windll.user32.EnumChildWindows(tb, WNDENUMPROC(enum_children), 0)
        if widget_hwnd[0]:
            break
            
    # Absolute fallback (check all top levels just in case not parented yet)
    if not widget_hwnd[0]:
        def enum_toplevels(h, l):
            title = win32gui.GetWindowText(h)
            cls = win32gui.GetClassName(h)
            if title == "TaskbarWindow" or ("HwndWrapper" in cls and "TaskbarWindow" in title):
                widget_hwnd[0] = h
                return False
            return True
        win32gui.EnumWindows(enum_toplevels, 0)
        
    return widget_hwnd[0]

# --- Event Simulation Helpers ---
main_hwnd = None
widget_hwnd = None

def send_alt_c():
    """Simulates Alt + C hotkey directly by posting WM_HOTKEY to MainWindow."""
    if main_hwnd:
        win32gui.PostMessage(main_hwnd, win32con.WM_HOTKEY, 9000, 0)

def send_escape():
    """Sends VK_ESCAPE directly to the FlyShelf main window."""
    if main_hwnd:
        win32gui.PostMessage(main_hwnd, win32con.WM_KEYDOWN, win32con.VK_ESCAPE, 0)
        time.sleep(0.010)
        win32gui.PostMessage(main_hwnd, win32con.WM_KEYUP, win32con.VK_ESCAPE, 0)

def click_window_center(hwnd):
    """Sends mouse down/up directly to the HWND without changing mouse cursor."""
    rect = win32gui.GetWindowRect(hwnd)
    w = rect[2] - rect[0]
    h = rect[3] - rect[1]
    x = w // 2
    y = h // 2
    lParam = (y << 16) | x
    win32gui.PostMessage(hwnd, win32con.WM_LBUTTONDOWN, win32con.MK_LBUTTON, lParam)
    time.sleep(0.02)
    win32gui.PostMessage(hwnd, win32con.WM_LBUTTONUP, 0, lParam)

def click_offscreen():
    """Simulates focus loss directly by setting foreground window to the taskbar."""
    shell_tray = win32gui.FindWindow("Shell_TrayWnd", None)
    if shell_tray:
        try:
            windll.user32.SetForegroundWindow(shell_tray)
        except Exception:
            pass

def copy_text_to_clipboard(text):
    """Copies text to the system clipboard."""
    win32clipboard.OpenClipboard()
    try:
        win32clipboard.EmptyClipboard()
        win32clipboard.SetClipboardText(text, win32clipboard.CF_UNICODETEXT)
    finally:
        win32clipboard.CloseClipboard()

# --- Latency Measurement Runner ---
def wait_for_visibility(hwnd, target_visible, timeout=1.5):
    """Loops tightly to measure when window visibility changes."""
    start = time.perf_counter()
    while time.perf_counter() - start < timeout:
        visible = win32gui.IsWindowVisible(hwnd)
        if (visible != 0) == target_visible:
            return (time.perf_counter() - start) * 1000.0  # Return in ms
        time.sleep(0.0005)  # 0.5ms poll speed for ultimate precision
    return -1.0

# --- Log Parser & Analyzer ---
def parse_logs(start_time_str, unique_len):
    """Parses activity_log.txt and aggregates app-internal millisecond latencies."""
    if not os.path.exists(LOG_PATH):
        print_warning("activity_log.txt not found. Internal telemetry analysis skipped.")
        return None, []
        
    print_status("Reading activity_log.txt for deep diagnostics...")
    
    with open(LOG_PATH, 'r', encoding='utf-8', errors='ignore') as f:
        lines = f.readlines()
        
    # We only care about logs from this run. Find start line.
    run_lines = []
    start_found = False
    for line in lines:
        match = re.match(r'^\[([\d\-:\.\s]+)\]', line)
        if match:
            timestamp = match.group(1)
            if timestamp >= start_time_str:
                start_found = True
            if start_found:
                run_lines.append((timestamp, line))
                
    clipboard_logs = []
    
    internal_metrics = {
        'hotkey_WndProc_to_ShowNearPosition': [],
        'widget_click_to_ShowNearPosition': [],
        'mascot_resume_delay': []
    }
    
    # We can trace specific entries like:
    # [HOTKEY] Alt+C fired
    # [WIDGET] SetupWindow or mouse down
    # [CLIPBOARD] Routing as TEXT (N chars)
    
    # We'll analyze whatever traces are present.
    for ts_str, line in run_lines:
        # Convert timestamp to time float
        try:
            t_part, ms_part = ts_str.split('.')
            struct_t = time.strptime(t_part, "%Y-%m-%d %H:%M:%S")
            ts = time.mktime(struct_t) + int(ms_part)/1000.0
        except Exception:
            continue
            
        if "[CLIPBOARD]" in line and f"Routing as TEXT ({unique_len} chars)" in line:
            clipboard_logs.append(ts)
            
    return internal_metrics, clipboard_logs

# --- Main Program Run ---
def main():
    print_header("FlyShelf PC Spawning Performance Diagnostics")
    print_status(f"Python Environment: {sys.version}")
    
    # 1. Discover Window Handles
    global main_hwnd, widget_hwnd
    main_hwnd = find_main_window()
    widget_hwnd = find_widget_window()
    
    if not main_hwnd:
        print_error("MainWindow 'FlyShelf' NOT found! Is the application running?")
        sys.exit(1)
        
    print_success(f"MainWindow Found! HWND: {main_hwnd} (Title: {win32gui.GetWindowText(main_hwnd)})")
    
    if not widget_hwnd:
        print_warning("TaskbarWidgetWindow 'TaskbarWindow' NOT found inside taskbars. We will proceed with Hotkey-only tests.")
    else:
        print_success(f"TaskbarWidget Found! HWND: {widget_hwnd}")
        
    # Get current log size to crop scanning window
    start_time_struct = time.localtime()
    start_time_str = time.strftime("%Y-%m-%d %H:%M:%S.000", start_time_struct)
    print_status(f"Profiling Session started at: {time.strftime('%Y-%m-%d %H:%M:%S', start_time_struct)}")
    
    # 2. Performance Test Suite
    results = {
        'hotkey_summon': [],
        'hotkey_dismiss': [],
        'widget_summon': [],
        'widget_dismiss': [],
        'escape_dismiss': [],
        'focus_loss_dismiss': []
    }
    
    # Ensure window starts HIDDEN
    if win32gui.IsWindowVisible(main_hwnd):
        print_status("Preparing test state: Hiding FlyShelf overlay...")
        send_alt_c()
        time.sleep(1.0)
        if win32gui.IsWindowVisible(main_hwnd):
            print_status("Alt+C didn't dismiss it. Trying Escape key...")
            send_escape()
            time.sleep(1.0)
            if win32gui.IsWindowVisible(main_hwnd):
                print_error("Failed to dismiss FlyShelf. Please dismiss it manually and re-run.")
                sys.exit(1)
            
    # CYCLE 1: Alt+C Hotkey summon/dismiss (5 cycles)
    print_header("Test Action 1: Hotkey Alt+C Summon & Dismiss (5 Cycles)")
    for i in range(5):
        print(f"Cycle {i+1}/5...", end="", flush=True)
        
        # Summon
        t_start = time.perf_counter()
        send_alt_c()
        lat = wait_for_visibility(main_hwnd, True)
        if lat > 0:
            results['hotkey_summon'].append(lat)
            print(f" Summon: {lat:.1f}ms", end="", flush=True)
        else:
            print(f" Summon: TIMEOUT (>1500ms)", end="", flush=True)
            
        time.sleep(0.5) # Idle time
        
        # Dismiss
        t_start = time.perf_counter()
        send_alt_c()
        lat = wait_for_visibility(main_hwnd, False)
        if lat > 0:
            results['hotkey_dismiss'].append(lat)
            print(f" | Dismiss: {lat:.1f}ms")
        else:
            print(f" | Dismiss: TIMEOUT")
            
        time.sleep(0.5)
        
    # CYCLE 2: Taskbar Widget click summon/dismiss (5 cycles)
    if widget_hwnd:
        print_header("Test Action 2: Taskbar Widget Click Summon & Dismiss (5 Cycles)")
        for i in range(5):
            print(f"Cycle {i+1}/5...", end="", flush=True)
            
            # Summon
            t_start = time.perf_counter()
            click_window_center(widget_hwnd)
            lat = wait_for_visibility(main_hwnd, True)
            if lat > 0:
                results['widget_summon'].append(lat)
                print(f" Summon: {lat:.1f}ms", end="", flush=True)
            else:
                print(f" Summon: TIMEOUT", end="", flush=True)
                
            time.sleep(0.5)
            
            # Dismiss
            t_start = time.perf_counter()
            click_window_center(widget_hwnd)
            lat = wait_for_visibility(main_hwnd, False)
            if lat > 0:
                results['widget_dismiss'].append(lat)
                print(f" | Dismiss: {lat:.1f}ms")
            else:
                print(f" | Dismiss: TIMEOUT")
                
            time.sleep(0.5)
            
    # CYCLE 3: Escape Key Dismissal (3 cycles)
    print_header("Test Action 3: Escape Key Dismissal (3 Cycles)")
    for i in range(3):
        print(f"Cycle {i+1}/3...", end="", flush=True)
        
        # Summon
        send_alt_c()
        wait_for_visibility(main_hwnd, True)
        time.sleep(0.4)
        
        # Escape
        t_start = time.perf_counter()
        send_escape()
        lat = wait_for_visibility(main_hwnd, False)
        if lat > 0:
            results['escape_dismiss'].append(lat)
            print(f" Dismiss: {lat:.1f}ms")
        else:
            print(f" Dismiss: TIMEOUT")
            
        time.sleep(0.5)
        
    # CYCLE 4: Focus Loss Dismissal (3 cycles)
    print_header("Test Action 4: Focus Loss Dismissal (3 Cycles)")
    for i in range(3):
        print(f"Cycle {i+1}/3...", end="", flush=True)
        
        # Summon
        send_alt_c()
        wait_for_visibility(main_hwnd, True)
        time.sleep(0.4)
        
        # Focus loss click
        t_start = time.perf_counter()
        click_offscreen()
        lat = wait_for_visibility(main_hwnd, False)
        if lat > 0:
            results['focus_loss_dismiss'].append(lat)
            print(f" Dismiss: {lat:.1f}ms")
        else:
            print(f" Dismiss: TIMEOUT")
            
        time.sleep(0.5)

    # CYCLE 5: Clipboard Sniffer Latency
    print_header("Test Action 5: Background Clipboard Sniffer Sync (3 Cycles)")
    cb_latencies = []
    for i in range(3):
        print(f"Cycle {i+1}/3...", end="", flush=True)
        unique_id = uuid.uuid4().hex
        filler = "A" * (243 - len(unique_id) - 20)
        test_string = f"FLYSHELF-DIAG-TEST-{unique_id}-{filler}"
        unique_len = len(test_string)
        
        t_copy_start = time.time()
        copy_text_to_clipboard(test_string)
        print(" Copied unique string. Waiting for sniffer detection...", end="", flush=True)
        
        # Give a small buffer of time
        time.sleep(2.5)
        
        # Scan log for this cycle
        _, cb_timestamps = parse_logs(start_time_str, unique_len)
        if cb_timestamps:
            lat = (cb_timestamps[-1] - t_copy_start) * 1000.0
            cb_latencies.append(lat)
            print(f" Sync Latency: {lat:.1f}ms")
        else:
            print(f" Sync Latency: NOT DETECTED (log flush delayed)")
            
        time.sleep(0.5)

    # Clean clipboard
    copy_text_to_clipboard("")

    # 3. Process & Report Stats
    print_header("Diagnostic Statistical Performance Report")
    
    print(f"{Color.BOLD}{'Action Pathway':<25} | {'Avg Latency':<12} | {'Min Latency':<12} | {'Max Latency':<12} | {'Jitter (StdDev)':<15}{Color.END}")
    print("-" * 75)
    
    for name, lat_list in results.items():
        if not lat_list:
            print(f"{name:<25} | {'N/A':<12} | {'N/A':<12} | {'N/A':<12} | {'N/A':<15}")
            continue
            
        avg = statistics.mean(lat_list)
        mn = min(lat_list)
        mx = max(lat_list)
        jitter = statistics.stdev(lat_list) if len(lat_list) > 1 else 0.0
        
        color = Color.GREEN if avg < 80 else (Color.YELLOW if avg < 150 else Color.RED)
        print(f"{name:<25} | {color}{avg:>9.1f}ms{Color.END} | {mn:>9.1f}ms | {mx:>9.1f}ms | {jitter:>12.2f}ms")
        
    if cb_latencies:
        avg = statistics.mean(cb_latencies)
        mn = min(cb_latencies)
        mx = max(cb_latencies)
        jitter = statistics.stdev(cb_latencies) if len(cb_latencies) > 1 else 0.0
        print(f"{'clipboard_sync':<25} | {Color.BLUE}{avg:>9.1f}ms{Color.END} | {mn:>9.1f}ms | {mx:>9.1f}ms | {jitter:>12.2f}ms")
        
    # 4. Deep Lag Analysis Verdict
    print_header("Performance Verdict & Lag Analysis")
    
    h_summon_avg = statistics.mean(results['hotkey_summon']) if results['hotkey_summon'] else 999.0
    w_summon_avg = statistics.mean(results['widget_summon']) if results['widget_summon'] else 999.0
    
    if h_summon_avg < 50.0 and w_summon_avg < 50.0:
        print_success("EXCELLENT PERFORMANCE: Spawning pathways are both running under the 50ms human perception ceiling. Spawning is instant!")
    else:
        print_warning("LAG DETECTED:")
        if h_summon_avg >= 50.0:
            print_error(f" Alt+C Hotkey summon latency ({h_summon_avg:.1f}ms) is above target of 50ms.")
        if w_summon_avg >= 50.0:
            print_error(f" Widget Click summon latency ({w_summon_avg:.1f}ms) is above target of 50ms.")
            
        print("\nPossible Bottleneck Diagnosis:")
        if h_summon_avg - w_summon_avg > 25.0:
            print(" -> WndProc/Dispatcher queue bottleneck: The hotkey takes significantly longer than the widget click, indicating the Alt+C key message is stalling in the Win32 queue or inside `Dispatcher.InvokeAsync` wrapper.")
        elif w_summon_avg - h_summon_avg > 25.0:
            print(" -> Widget Click / Coordinate Resolution bottleneck: Click events on the taskbar widget are sluggish, possibly due to DPI conversion or mouse hook interception.")
        else:
            print(" -> General WPF Layout / HWND Activation bottleneck: Both summon paths take similar amounts of time. This indicates that WPF's internal window activation (`this.Show()`), visual layout pass, or animation triggers are the central culprits.")
            
        print("\nRecommendation:")
        print(" Run the optimizations in MainWindow.Lifecycle.cs by reducing layout parameters before Show(), or keeping the window always activated but hidden offscreen.")

if __name__ == '__main__':
    main()
