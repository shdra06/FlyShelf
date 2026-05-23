import tkinter as tk
import math
import time
import re
import sys

# Windows High-DPI Awareness
try:
    import ctypes
    ctypes.windll.shcore.SetProcessDpiAwareness(1)
except Exception:
    pass

# Global Physics Parameters (matching SmoothScroll.cs defaults)
mouse_ease = 0.18
mouse_scroll_step = 96.0
touchpad_ease = 0.45
touchpad_multiplier = 0.80

# State variables
target_offset = 0.0
current_offset = 0.0
is_animating = False
is_touchpad = False
last_frame_time = 0.0
mouse_in_canvas = False

# Plot History
history_target = []
history_current = []
MAX_HISTORY = 70

def get_clipboard_item():
    """Live fetches the system clipboard text and categorizes it like FlyShelf."""
    try:
        # Create a temporary tk root if main is not ready, but we use the main root.
        clip_text = root.clipboard_get()
    except Exception:
        clip_text = None
        
    if not clip_text:
        return {
            'type': 'empty',
            'badge_text': 'CLIPBOARD EMPTY',
            'badge_color': '#b0b0bc',
            'bg_badge_color': '#2a2a32',
            'title': 'No text found in clipboard',
            'content': 'Copy some text from another app and click "Refresh Clipboard" to test with your actual data!',
            'accent_color': '#b0b0bc',
            'height': 110
        }
    
    clip_text_clean = clip_text.strip()
    
    # 1. Color Swatch Detector
    color_hex_pattern = re.compile(r'^#([A-Fa-f0-9]{6}|[A-Fa-f0-9]{3})$')
    color_rgb_pattern = re.compile(r'^rgb\s*\(\s*\d+\s*,\s*\d+\s*,\s*\d+\s*\)$', re.IGNORECASE)
    if color_hex_pattern.match(clip_text_clean) or color_rgb_pattern.match(clip_text_clean):
        hex_color = clip_text_clean if color_hex_pattern.match(clip_text_clean) else '#8844ff'
        return {
            'type': 'color',
            'badge_text': 'CLIPBOARD: COLOR',
            'badge_color': '#fee440',
            'bg_badge_color': '#302c08',
            'title': clip_text_clean,
            'content': 'Detected CSS color swathes natively.',
            'accent_color': '#fee440',
            'smart_action': {'type': 'color', 'value': hex_color},
            'height': 160
        }
        
    # 2. Smart Math Solver
    math_pattern = re.compile(r'^[\d\s\+\-\*\/\(\)\.\^\%]+$')
    if math_pattern.match(clip_text_clean) and any(op in clip_text_clean for op in '+-*/^'):
        try:
            safe_expr = clip_text_clean.replace('^', '**')
            result = eval(safe_expr, {"__builtins__": None}, {})
            result_str = f"= {result:.4f}".rstrip('0').rstrip('.')
            return {
                'type': 'math',
                'badge_text': 'CLIPBOARD: MATH',
                'badge_color': '#f15bb5',
                'bg_badge_color': '#360b24',
                'title': clip_text_clean,
                'content': 'Solved equation with Shunting-yard parser.',
                'accent_color': '#f15bb5',
                'smart_action': {'type': 'math', 'value': result_str},
                'height': 150
            }
        except Exception:
            pass
            
    # 3. Smart URL Cleaner
    if clip_text_clean.startswith(('http://', 'https://')):
        clean_url = clip_text_clean
        stripped = False
        if '?' in clean_url:
            base, query = clean_url.split('?', 1)
            params = query.split('&')
            clean_params = [p for p in params if not p.startswith(('utm_', 'fbclid', 'gclid'))]
            clean_url = base + ('?' + '&'.join(clean_params) if clean_params else '')
            if len(clean_url) != len(clip_text_clean):
                stripped = True
            
        return {
            'type': 'url',
            'badge_text': 'CLIPBOARD: URL',
            'badge_color': '#00bbf9',
            'bg_badge_color': '#042436',
            'title': 'Link Detected',
            'content': clean_url,
            'accent_color': '#00bbf9',
            'smart_action': {'type': 'url', 'value': 'Cleaned URL (stripped tracking parameters)'} if stripped else None,
            'height': 140 if stripped else 120
        }
        
    # 4. Code Classifier
    code_keywords = ['def ', 'import ', 'class ', 'public class ', 'private ', 'void ', 'string ', 'int ', 'const ', 'let ', 'function ', 'console.log', '#include', 'using System;']
    if any(kw in clip_text for kw in code_keywords) or len(clip_text_clean.splitlines()) > 1:
        snippet = '\n'.join(clip_text.splitlines()[:3])
        if len(clip_text.splitlines()) > 3:
            snippet += '\n...'
        return {
            'type': 'code',
            'badge_text': 'CLIPBOARD: CODE',
            'badge_color': '#9b5de5',
            'bg_badge_color': '#22123b',
            'title': 'Code Snippet Detected',
            'content': snippet,
            'accent_color': '#9b5de5',
            'height': 150
        }
        
    # 5. General Text
    snippet = clip_text_clean
    if len(snippet) > 120:
        snippet = snippet[:117] + "..."
    return {
        'type': 'text',
        'badge_text': 'CLIPBOARD: TEXT',
        'badge_color': '#00f5d4',
        'bg_badge_color': '#0a2e2b',
        'title': 'Text Content',
        'content': snippet,
        'accent_color': '#00f5d4',
        'height': 130 if len(snippet) > 60 else 110
    }

def get_mock_items():
    """Generates standard, beautifully styled FlyShelf-like cards to fill list."""
    return [
        {
            'type': 'code',
            'badge_text': 'CODE: C#',
            'badge_color': '#9b5de5',
            'bg_badge_color': '#22123b',
            'title': 'SmoothScroll.cs (WPF Hook)',
            'content': 'public static void AttachToWindow(Window window)\n{\n    window.PreviewMouseWheel -= OnWindowPreviewMouseWheel;\n    window.PreviewMouseWheel += OnWindowPreviewMouseWheel;\n}',
            'accent_color': '#9b5de5',
            'height': 160
        },
        {
            'type': 'color',
            'badge_text': 'SMART: COLOR',
            'badge_color': '#fee440',
            'bg_badge_color': '#302c08',
            'title': '#9B5DE5',
            'content': 'Deep purple color swatch found.',
            'accent_color': '#fee440',
            'smart_action': {'type': 'color', 'value': '#9b5de5'},
            'height': 160
        },
        {
            'type': 'math',
            'badge_text': 'SMART: SOLVER',
            'badge_color': '#f15bb5',
            'bg_badge_color': '#360b24',
            'title': '(12 * 8) / 1.5',
            'content': 'Solved via FlyShelf shunting-yard logic.',
            'accent_color': '#f15bb5',
            'smart_action': {'type': 'math', 'value': '= 64.0'},
            'height': 140
        },
        {
            'type': 'url',
            'badge_text': 'LINK: REPO',
            'badge_color': '#00bbf9',
            'bg_badge_color': '#042436',
            'title': 'FlyShelf Local HTTP Server',
            'content': 'http://localhost:8999/api/health',
            'accent_color': '#00bbf9',
            'height': 120
        },
        {
            'type': 'file',
            'badge_text': 'FILE: DOCUMENT',
            'badge_color': '#ff006e',
            'bg_badge_color': '#3c041c',
            'title': 'FlyShelf_Architecture_Overview.pdf',
            'content': 'Location: E:\\Documents\\FlyShelf\\\nSize: 4.2 MB | Pages: 24',
            'accent_color': '#ff006e',
            'height': 130
        },
        {
            'type': 'text',
            'badge_text': 'NOTES',
            'badge_color': '#00f5d4',
            'bg_badge_color': '#0a2e2b',
            'title': 'To-Do List',
            'content': '1. Test double-scroll unregister on HubWindow.\n2. Add multi-monitor layout detection.\n3. Integrate Cloudflare tunnel QUIC fallback.',
            'accent_color': '#00f5d4',
            'height': 150
        },
        {
            'type': 'file',
            'badge_text': 'FILE: ARCHIVE',
            'badge_color': '#ff006e',
            'bg_badge_color': '#3c041c',
            'title': 'FlyShelf_Android_Build_2.apk',
            'content': 'Location: E:\\Downloads\\FlyShelf\\Mobile\\\nSize: 28.5 MB | Platform: Android 11+',
            'accent_color': '#ff006e',
            'height': 130
        },
        {
            'type': 'url',
            'badge_text': 'LINK: DOCUMENTATION',
            'badge_color': '#00bbf9',
            'bg_badge_color': '#042436',
            'title': 'WPF Mouse Event Bubbling Specs',
            'content': 'https://learn.microsoft.com/en-us/dotnet/desktop/wpf/events/routed-events-overview',
            'accent_color': '#00bbf9',
            'height': 120
        },
        {
            'type': 'text',
            'badge_text': 'TERMINAL SNIPPET',
            'badge_color': '#9b5de5',
            'bg_badge_color': '#22123b',
            'title': 'Production Build Command',
            'content': 'dotnet publish FlyShelf_PC/FlyShelf.csproj -c Release -r win-x64 --self-contained true',
            'accent_color': '#9b5de5',
            'height': 130
        }
    ]

# Generate a list of cards with 18 items to make the list tall enough for great scrolling
cards_list = []
total_content_height = 0.0

def draw_rounded_rectangle(canvas, x1, y1, x2, y2, radius=10, **kwargs):
    """Draws a mathematically clean rounded rectangle using canvas primitive shapes."""
    fill = kwargs.get('fill', '')
    outline = kwargs.get('outline', '')
    width = kwargs.get('width', 1.5)
    tags = kwargs.get('tags', ())
    
    # Solid background fill (drawn in parts to avoid gaps)
    canvas.create_arc(x1, y1, x1+2*radius, y1+2*radius, start=90, extent=90, style='pieslice', fill=fill, outline='', tags=tags)
    canvas.create_arc(x2-2*radius, y1, x2, y1+2*radius, start=0, extent=90, style='pieslice', fill=fill, outline='', tags=tags)
    canvas.create_arc(x2-2*radius, y2-2*radius, x2, y2, start=270, extent=90, style='pieslice', fill=fill, outline='', tags=tags)
    canvas.create_arc(x1, y2-2*radius, x1+2*radius, y2, start=180, extent=90, style='pieslice', fill=fill, outline='', tags=tags)
    canvas.create_rectangle(x1+radius, y1, x2-radius, y2, fill=fill, outline='', tags=tags)
    canvas.create_rectangle(x1, y1+radius, x2, y2-radius, fill=fill, outline='', tags=tags)
    
    # Outer stroke outline lines and arcs
    if outline:
        canvas.create_arc(x1, y1, x1+2*radius, y1+2*radius, start=90, extent=90, style='arc', outline=outline, width=width, tags=tags)
        canvas.create_arc(x2-2*radius, y1, x2, y1+2*radius, start=0, extent=90, style='arc', outline=outline, width=width, tags=tags)
        canvas.create_arc(x2-2*radius, y2-2*radius, x2, y2, start=270, extent=90, style='arc', outline=outline, width=width, tags=tags)
        canvas.create_arc(x1, y2-2*radius, x1+2*radius, y2, start=180, extent=90, style='arc', outline=outline, width=width, tags=tags)
        
        canvas.create_line(x1+radius, y1, x2-radius, y1, fill=outline, width=width, tags=tags)
        canvas.create_line(x2, y1+radius, x2, y2-radius, fill=outline, width=width, tags=tags)
        canvas.create_line(x1+radius, y2, x2-radius, y2, fill=outline, width=width, tags=tags)
        canvas.create_line(x1, y1+radius, x1, y2-radius, fill=outline, width=width, tags=tags)

def draw_card(canvas, idx, x, y, width, height, item):
    """Draws a complete premium FlyShelf card structure on the canvas."""
    fill_color = '#1c1c24'
    border_color = '#2d2d38'
    accent = item['accent_color']
    radius = 10
    
    x1, y1 = x, y
    x2, y2 = x + width, y + height
    
    card_tag = f"card_{idx}"
    border_tag = f"border_{idx}"
    
    # Draw rounded background and border
    draw_rounded_rectangle(canvas, x1, y1, x2, y2, radius, fill=fill_color, outline=border_color, tags=(card_tag, border_tag))
    
    # Left accent vertical bar
    canvas.create_rectangle(x1+3, y1+12, x1+6, y2-12, fill=accent, outline='', tags=(card_tag,))
    
    # Badge (Top-left aligned)
    badge_text = item['badge_text']
    bg_badge = item['bg_badge_color']
    fg_badge = item['badge_color']
    
    badge_w = len(badge_text) * 6.5 + 14
    bx1, by1 = x1 + 18, y1 + 14
    bx2, by2 = bx1 + badge_w, by1 + 18
    br = 4
    
    draw_rounded_rectangle(canvas, bx1, by1, bx2, by2, br, fill=bg_badge, outline='', tags=(card_tag,))
    canvas.create_text(bx1 + badge_w/2, by1 + 9, text=badge_text, fill=fg_badge, font=("Segoe UI Semibold", 8), tags=(card_tag,))
    
    # Title
    canvas.create_text(x1 + 18, y1 + 45, text=item['title'], fill='#ffffff', font=("Segoe UI", 11, "bold"), anchor='w', tags=(card_tag,))
    
    # Body Content
    content_y = y1 + 68
    canvas.create_text(x1 + 18, content_y, text=item['content'], fill='#afafbd', font=("Segoe UI", 9), anchor='nw', width=width-36, tags=(card_tag,))
    
    # Smart action divider and item
    if 'smart_action' in item and item['smart_action']:
        sa = item['smart_action']
        div_y = y2 - 32
        canvas.create_line(x1 + 18, div_y, x2 - 18, div_y, fill='#2a2a35', width=1, tags=(card_tag,))
        
        swatch_y = y2 - 16
        if sa['type'] == 'color':
            swatch_x = x1 + 25
            canvas.create_oval(swatch_x-6, swatch_y-6, swatch_x+6, swatch_y+6, fill=sa['value'], outline='#ffffff', width=1, tags=(card_tag,))
            canvas.create_text(swatch_x + 15, swatch_y, text=f"Hex Preview: {sa['value']}", fill='#ffffff', font=("Segoe UI Semibold", 9), anchor='w', tags=(card_tag,))
        elif sa['type'] == 'math':
            result_x = x1 + 18
            canvas.create_text(result_x, swatch_y, text=sa['value'], fill='#f15bb5', font=("Segoe UI Semibold", 10), anchor='w', tags=(card_tag,))
        elif sa['type'] == 'url':
            result_x = x1 + 18
            canvas.create_text(result_x, swatch_y, text=sa['value'], fill='#00bbf9', font=("Segoe UI Semibold", 9, "italic"), anchor='w', tags=(card_tag,))

    # Attach interactive hover effects to the entire card area
    def on_hover(event, b_tag=border_tag):
        # We rewrite border items on hover to light gray border tag
        for item_id in canvas.find_withtag(b_tag):
            item_type = canvas.type(item_id)
            if item_type == 'line':
                canvas.itemconfigure(item_id, fill='#4a4a58')
            else:
                canvas.itemconfigure(item_id, outline='#4a4a58')
                
    def on_leave(event, b_tag=border_tag):
        for item_id in canvas.find_withtag(b_tag):
            item_type = canvas.type(item_id)
            if item_type == 'line':
                canvas.itemconfigure(item_id, fill=border_color)
            else:
                canvas.itemconfigure(item_id, outline=border_color)
                
    canvas.tag_bind(card_tag, "<Enter>", on_hover)
    canvas.tag_bind(card_tag, "<Leave>", on_leave)

def rebuild_cards_canvas():
    """Constructs cards dynamically on the simulator scroll canvas."""
    global total_content_height, cards_list
    sim_canvas.delete('all')
    
    # 1. Fetch live clipboard item
    live_item = get_clipboard_item()
    
    # 2. Fetch mock items and duplicate to guarantee long scrollable list
    mocks = get_mock_items()
    
    cards_list = [live_item] + mocks + [dict(m, title=m['title'] + " (Copy)") for m in mocks]
    
    current_y = 15
    card_width = 370
    x_margin = 15
    
    for i, item in enumerate(cards_list):
        draw_card(sim_canvas, i, x_margin, current_y, card_width, item['height'], item)
        current_y += item['height'] + 15
        
    total_content_height = float(current_y)
    sim_canvas.configure(scrollregion=(0, 0, 400, total_content_height))
    
    # Snap scrollbar/canvas top
    sim_canvas.yview_moveto(0.0)
    global target_offset, current_offset
    target_offset = 0.0
    current_offset = 0.0

def update_copy_constants():
    """Generates and writes C# code to copy paste box."""
    code = f"""// Copy-paste this into classes/SmoothScroll.cs:
private const double ListEase           = {mouse_ease:.2f};
private const double TouchpadEase       = {touchpad_ease:.2f};
private const double MouseScrollStep    = {mouse_scroll_step:.1f};
private const double TouchpadMultiplier = {touchpad_multiplier:.2f};"""
    code_text.delete('1.0', tk.END)
    code_text.insert('1.0', code)

def copy_to_clipboard():
    """Copies current C# constants directly to system clipboard."""
    root.clipboard_clear()
    root.clipboard_append(code_text.get('1.0', tk.END).strip())
    
    # Flash visual feedback label
    copied_label.configure(text="Constants Copied!", fg='#00f5d4')
    root.after(1500, lambda: copied_label.configure(text="", fg='#00f5d4'))

# Physics event handlers
def on_mouse_wheel(event):
    """Event handler intercepting mouse wheel actions, simulating WPF logic."""
    global target_offset, current_offset, is_animating, is_touchpad, last_frame_time
    if not mouse_in_canvas:
        return
        
    delta = event.delta
    # 120-notch is discrete mouse. Non-120 ticks represent touchpad gestures.
    is_tp = (delta % 120 != 0) or (abs(delta) < 120)
    is_touchpad = is_tp
    
    # View and scroll limits
    view_h = sim_canvas.winfo_height()
    max_scroll = max(0.0, total_content_height - view_h)
    
    # Calculate scroll offset vector
    if is_touchpad:
        scroll_amount = -delta * touchpad_multiplier
    else:
        scroll_amount = -(delta / 120.0) * mouse_scroll_step
        # Snap target to current location on direction reverse to avoid rubber banding
        diff = target_offset - current_offset
        if not is_animating or (scroll_amount > 0) != (diff > 0):
            target_offset = current_offset
            
    target_offset += scroll_amount
    target_offset = max(0.0, min(target_offset, max_scroll))
    
    # Start animation ticker
    if not is_animating:
        is_animating = True
        last_frame_time = time.perf_counter()
        animate_loop()

def animate_loop():
    """High-frequency rendering timer implementing frame-rate independent LERP."""
    global current_offset, is_animating, last_frame_time
    if not is_animating:
        return
        
    now = time.perf_counter()
    elapsed_ms = (now - last_frame_time) * 1000.0
    if elapsed_ms <= 0:
        elapsed_ms = 1.0
    last_frame_time = now
    
    # 16.667ms baseline (60 FPS standard delta ticks)
    time_scale = elapsed_ms / 16.667
    time_scale = min(time_scale, 4.0) # protection cap
    
    diff = target_offset - current_offset
    
    if abs(diff) < 0.1:
        current_offset = target_offset
        is_animating = False
        history_target.clear()
        history_current.clear()
        update_diagnostics_panel()
        plot_diagnostics()
    else:
        ease = touchpad_ease if is_touchpad else mouse_ease
        factor = 1.0 - math.pow(1.0 - ease, time_scale)
        current_offset += diff * factor
        
        # Move canvas view area
        sim_canvas.yview_moveto(current_offset / total_content_height)
        
        # Track history details for plot graph
        history_target.append(target_offset)
        history_current.append(current_offset)
        if len(history_target) > MAX_HISTORY:
            history_target.pop(0)
            history_current.pop(0)
            
        update_diagnostics_panel()
        plot_diagnostics()
        
        # Re-trigger frame
        root.after(8, animate_loop)

def plot_diagnostics():
    """Plots active real-time scrolling metrics (target vs current LERP) dynamically."""
    plot_canvas.delete('all')
    
    # Grid lines
    for x in range(0, 360, 40):
        plot_canvas.create_line(x, 0, x, 130, fill='#22222d', width=1)
    for y in range(0, 130, 25):
        plot_canvas.create_line(0, y, 360, y, fill='#22222d', width=1)
        
    if not is_animating or not history_target or not history_current:
        plot_canvas.create_text(180, 65, text="Scroll simulated list to plot physics", fill='#55556a', font=("Segoe UI Semibold", 10, "italic"))
        return
        
    # Scale calculation
    all_vals = history_target + history_current
    min_val = min(all_vals)
    max_val = max(all_vals)
    span = max_val - min_val
    if span < 5.0:
        span = 5.0
        
    plot_h = 95
    margin = 15
    
    def to_y(val):
        norm = (val - min_val) / span
        return 130 - (norm * plot_h + margin)
        
    num_pts = len(history_target)
    x_step = 360.0 / max(1, num_pts - 1)
    
    # Draw target line (dashed Pink)
    t_points = []
    for i, val in enumerate(history_target):
        t_points.extend([i * x_step, to_y(val)])
    if len(t_points) >= 4:
        plot_canvas.create_line(t_points, fill='#ff006e', width=1.5, dash=(2, 2))
        
    # Draw LERP current line (smooth violet)
    c_points = []
    for i, val in enumerate(history_current):
        c_points.extend([i * x_step, to_y(val)])
    if len(c_points) >= 4:
        plot_canvas.create_line(c_points, fill='#9b5de5', width=3, smooth=True)
        
    # Endpoint indicators
    last_tx = (num_pts - 1) * x_step
    plot_canvas.create_oval(last_tx-3, to_y(history_target[-1])-3, last_tx+3, to_y(history_target[-1])+3, fill='#ff006e', outline='')
    plot_canvas.create_oval(last_tx-4, to_y(history_current[-1])-4, last_tx+4, to_y(history_current[-1])+4, fill='#9b5de5', outline='#ffffff', width=1)

def update_diagnostics_panel():
    """Updates metrics text in real time."""
    mode_text = "Touchpad" if is_touchpad else "Mouse Wheel"
    color_mode = "#00bbf9" if is_touchpad else "#9b5de5"
    
    lbl_mode.configure(text=f"INPUT: {mode_text.upper()}", fg=color_mode)
    lbl_target.configure(text=f"Target Y: {target_offset:.1f} px")
    lbl_current.configure(text=f"Current Y: {current_offset:.1f} px")
    
    diff = abs(target_offset - current_offset)
    lbl_error.configure(text=f"Damping Error: {diff:.1f} px")

# Slider callbacks
def on_mouse_ease_change(val):
    global mouse_ease
    mouse_ease = float(val)
    update_copy_constants()

def on_mouse_step_change(val):
    global mouse_scroll_step
    mouse_scroll_step = float(val)
    update_copy_constants()

def on_tp_ease_change(val):
    global touchpad_ease
    touchpad_ease = float(val)
    update_copy_constants()

def on_tp_multiplier_change(val):
    global touchpad_multiplier
    touchpad_multiplier = float(val)
    update_copy_constants()

def trigger_clipboard_refresh():
    """Forced refresh button action."""
    rebuild_cards_canvas()
    copied_label.configure(text="Clipboard Synced!", fg='#00f5d4')
    root.after(1200, lambda: copied_label.configure(text="", fg='#ffffff'))

# Create GUI Root Window
root = tk.Tk()
root.title("FlyShelf Smooth Scroll Physics Optimizer")
root.geometry("820x650")
root.resizable(False, False)
root.configure(bg='#121216')

# Left Panel Layout (Control Center)
left_frame = tk.Frame(root, bg='#121216', width=400, height=650)
left_frame.pack_propagate(False)
left_frame.pack(side=tk.LEFT, fill=tk.Y, padx=(15, 0), pady=15)

# Header Title
hdr_title = tk.Label(left_frame, text="SCROLL OPTIMIZER", bg='#121216', fg='#ffffff', font=("Segoe UI", 14, "bold"))
hdr_title.pack(anchor='w', pady=(0, 2))

hdr_desc = tk.Label(left_frame, text="Fine-tune target-LERP scrolling curves interactively.", bg='#121216', fg='#6b6b7a', font=("Segoe UI", 9))
hdr_desc.pack(anchor='w', pady=(0, 15))

# ═══ MOUSE SETTINGS PANEL ═══
lbl_mouse_hdr = tk.Label(left_frame, text="MOUSE WHEEL PROFILE", bg='#121216', fg='#9b5de5', font=("Segoe UI Semibold", 9))
lbl_mouse_hdr.pack(anchor='w', pady=(0, 4))

mouse_pane = tk.Frame(left_frame, bg='#1a1a22', bd=1, relief=tk.FLAT)
mouse_pane.pack(fill=tk.X, pady=(0, 15))

# Mouse LERP Ease Slider
tk.Label(mouse_pane, text="Mouse Interpolation Ease (snappiness)", bg='#1a1a22', fg='#cfcfdf', font=("Segoe UI", 9)).pack(anchor='w', padx=10, pady=(8, 0))
scale_m_ease = tk.Scale(mouse_pane, from_=0.02, to=1.00, resolution=0.01, orient=tk.HORIZONTAL, bg='#1a1a22', fg='#ffffff', troughcolor='#2b2b36', activebackground='#9b5de5', bd=0, highlightthickness=0, command=on_mouse_ease_change)
scale_m_ease.set(mouse_ease)
scale_m_ease.pack(fill=tk.X, padx=10, pady=(0, 8))

# Mouse Scroll Step
tk.Label(mouse_pane, text="Mouse Scroll Step Size (px per notch)", bg='#1a1a22', fg='#cfcfdf', font=("Segoe UI", 9)).pack(anchor='w', padx=10, pady=(2, 0))
scale_m_step = tk.Scale(mouse_pane, from_=24.0, to=200.0, resolution=4.0, orient=tk.HORIZONTAL, bg='#1a1a22', fg='#ffffff', troughcolor='#2b2b36', activebackground='#9b5de5', bd=0, highlightthickness=0, command=on_mouse_step_change)
scale_m_step.set(mouse_scroll_step)
scale_m_step.pack(fill=tk.X, padx=10, pady=(0, 8))

# ═══ TOUCHPAD SETTINGS PANEL ═══
lbl_tp_hdr = tk.Label(left_frame, text="PRECISION TOUCHPAD PROFILE", bg='#121216', fg='#00bbf9', font=("Segoe UI Semibold", 9))
lbl_tp_hdr.pack(anchor='w', pady=(0, 4))

tp_pane = tk.Frame(left_frame, bg='#1a1a22', bd=1, relief=tk.FLAT)
tp_pane.pack(fill=tk.X, pady=(0, 15))

# Touchpad LERP Ease Slider
tk.Label(tp_pane, text="Touchpad Interpolation Ease (direct follow)", bg='#1a1a22', fg='#cfcfdf', font=("Segoe UI", 9)).pack(anchor='w', padx=10, pady=(8, 0))
scale_tp_ease = tk.Scale(tp_pane, from_=0.05, to=1.00, resolution=0.01, orient=tk.HORIZONTAL, bg='#1a1a22', fg='#ffffff', troughcolor='#2b2b36', activebackground='#00bbf9', bd=0, highlightthickness=0, command=on_tp_ease_change)
scale_tp_ease.set(touchpad_ease)
scale_tp_ease.pack(fill=tk.X, padx=10, pady=(0, 8))

# Touchpad Multiplier
tk.Label(tp_pane, text="Touchpad Sensitivity Multiplier", bg='#1a1a22', fg='#cfcfdf', font=("Segoe UI", 9)).pack(anchor='w', padx=10, pady=(2, 0))
scale_tp_mult = tk.Scale(tp_pane, from_=0.10, to=2.00, resolution=0.05, orient=tk.HORIZONTAL, bg='#1a1a22', fg='#ffffff', troughcolor='#2b2b36', activebackground='#00bbf9', bd=0, highlightthickness=0, command=on_tp_multiplier_change)
scale_tp_mult.set(touchpad_multiplier)
scale_tp_mult.pack(fill=tk.X, padx=10, pady=(0, 8))

# ═══ VISUAL PHYSICS PLOTTER ═══
plot_hdr_frame = tk.Frame(left_frame, bg='#121216')
plot_hdr_frame.pack(fill=tk.X, pady=(0, 4))

tk.Label(plot_hdr_frame, text="REAL-TIME PHYSICS WAVEFORM", bg='#121216', fg='#ffffff', font=("Segoe UI Semibold", 9)).pack(side=tk.LEFT)
lbl_mode = tk.Label(plot_hdr_frame, text="INPUT: IDLE", bg='#121216', fg='#555566', font=("Segoe UI Semibold", 8, "bold"))
lbl_mode.pack(side=tk.RIGHT)

plot_canvas = tk.Canvas(left_frame, width=360, height=130, bg='#181820', bd=0, highlightthickness=1, highlightbackground='#2b2b36')
plot_canvas.pack(fill=tk.X, pady=(0, 8))

# Diagnostics labels grid
diag_frame = tk.Frame(left_frame, bg='#121216')
diag_frame.pack(fill=tk.X, pady=(0, 15))

lbl_target = tk.Label(diag_frame, text="Target Y: 0.0 px", bg='#121216', fg='#6b6b7a', font=("Segoe UI", 8))
lbl_target.grid(row=0, column=0, sticky='w', padx=(0, 15))

lbl_current = tk.Label(diag_frame, text="Current Y: 0.0 px", bg='#121216', fg='#6b6b7a', font=("Segoe UI", 8))
lbl_current.grid(row=0, column=1, sticky='w')

lbl_error = tk.Label(diag_frame, text="Damping Error: 0.0 px", bg='#121216', fg='#6b6b7a', font=("Segoe UI", 8))
lbl_error.grid(row=0, column=2, sticky='w', padx=(15, 0))

# ═══ C# CONSTANTS CODE EXPORTER ═══
lbl_code_hdr = tk.Label(left_frame, text="OPTIMIZED C# CONSTANTS", bg='#121216', fg='#ffffff', font=("Segoe UI Semibold", 9))
lbl_code_hdr.pack(anchor='w', pady=(0, 4))

code_frame = tk.Frame(left_frame, bg='#181820', bd=1, relief=tk.FLAT)
code_frame.pack(fill=tk.X, pady=(0, 10))

code_text = tk.Text(code_frame, bg='#181820', fg='#00f5d4', insertbackground='#ffffff', font=("Consolas", 9), height=5, bd=0, highlightthickness=0)
code_text.pack(fill=tk.X, padx=8, pady=8)

# Control Buttons pane
btn_pane = tk.Frame(left_frame, bg='#121216')
btn_pane.pack(fill=tk.X)

btn_sync = tk.Button(btn_pane, text="Sync Clipboard", bg='#22222d', fg='#ffffff', font=("Segoe UI Semibold", 9), bd=0, relief=tk.FLAT, activebackground='#2d2d3c', activeforeground='#ffffff', cursor='hand2', command=trigger_clipboard_refresh)
btn_sync.pack(side=tk.LEFT, fill=tk.Y, ipadx=10, ipady=4)

btn_copy = tk.Button(btn_pane, text="Copy C# Constants", bg='#9b5de5', fg='#ffffff', font=("Segoe UI Semibold", 9), bd=0, relief=tk.FLAT, activebackground='#aa6ff0', activeforeground='#ffffff', cursor='hand2', command=copy_to_clipboard)
btn_copy.pack(side=tk.RIGHT, fill=tk.Y, ipadx=10, ipady=4)

copied_label = tk.Label(btn_pane, text="", bg='#121216', fg='#00f5d4', font=("Segoe UI Semibold", 9))
copied_label.pack(side=tk.RIGHT, padx=10)

# Right Panel Layout (Simulated Scroll List)
right_frame = tk.Frame(root, bg='#111115', width=400, height=650)
right_frame.pack_propagate(False)
right_frame.pack(side=tk.RIGHT, fill=tk.Y, padx=0, pady=0)

sim_canvas = tk.Canvas(right_frame, width=400, height=650, bg='#111115', bd=0, highlightthickness=0)
sim_canvas.pack(fill=tk.BOTH, expand=True)

# Canvas enter/leave tracking for scrolling boundary containment
def on_canvas_enter(e):
    global mouse_in_canvas
    mouse_in_canvas = True

def on_canvas_leave(e):
    global mouse_in_canvas
    mouse_in_canvas = False

sim_canvas.bind("<Enter>", on_canvas_enter)
sim_canvas.bind("<Leave>", on_canvas_leave)

# Window-wide mouse wheel hooks (contained via mouse_in_canvas checks)
sim_canvas.bind_all("<MouseWheel>", on_mouse_wheel)

# Perform first compile load
rebuild_cards_canvas()
update_copy_constants()
plot_diagnostics()

# Run main thread loop
root.mainloop()
