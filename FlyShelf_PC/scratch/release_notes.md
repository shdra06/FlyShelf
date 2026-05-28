# FlyShelf v8.0.0 — Official New Update

This major release introduces substantial performance, visual, and architectural improvements to the FlyShelf PC Dashboard and Clipboard Manager.

## Key Enhancements

### 1. ScrollBar Layout & Anti-Jitter Stability
* **Constant-Size Thumbs**: Locked vertical scrollbar thumbs to a constant, premium `80px` height under both the Standard and Glassmorphism themes (symmetric with the constant `60px` horizontal thumbs).
* **Zero Recalculation Lag**: Completely eliminated cascading listview layout invalidation cycles (`Measure`/`Arrange`) during scrolling and item deletions. Scrolling and card removals are now Snappy and lightweight.

### 2. Robust Summon Sizing Transitions (Zero Glitches)
* **OS-Level Layered Alpha Shield**: Implemented Win32 layered window attributes to force window opacity to `0` synchronously at the OS-level offscreen. This shields DWM redirection buffers, making it physically impossible to see any black-box composition frames during coordinate reallocation.
* **Unified Deferral Pipeline**: Restructured summoning to always pre-apply sizing changes offscreen and always defer onscreen positioning to `DispatcherPriority.Background`.
* **Token Summon Guards**: Engineered window-wide `_spawnToken` validity counters to safely discard stale, deferred hide callbacks and race-condition summons.

### 3. High-Sensitivity Mouse Shake Summoning
* **Displacement Vector Detection**: Refactored shake triggers to utilize a continuous velocity displacement vector check ($\ge 9\text{px}$ in a $40\text{ms}$ step).
* **Dot-Product Reversals**: Uses dot-product math (`dot < 0`) to capture vertical, horizontal, or diagonal direction reversals effortlessly (triggers on 4 reversals).
* **Vertical Drift Filtering**: Prevents false triggers during normal long downward drag-and-drop operations.

### 4. Intelligent Code Classification & .MD Smart Actions
* **High-Density Code Classifier**: Replaced basic regex heuristics with a multi-priority syntax analysis engine. Prioritizes language entry points (`int main`, `console.log`) and evaluates line-by-line syntax density (requiring $\ge 35\%$ density) to avoid text/webpage copy-paste misclassifications.
* **Markdown Smart Actions**: Added support for `.MD` documents enabling instant PDF conversion and smart document preview actions.

### 5. Toolbar Customization
* **Settings Gear Toolbar Button**: Swapped the legacy "Clear Shelf" button on the clipboard header with a Settings Gear button (`Settings24`), providing a quick, convenient entry point to the main PC App Hub.

### 6. Brighter & More Vibrant Wallpapers
* **Increased Opacity**: Elevated the baseline opacity of the background image panel from `38%` to `55%` for better image detail.
* **Vibrant Vignette**: Softened the radial vignette dark overlay for significantly richer dominant background colors and color vibrancy.
