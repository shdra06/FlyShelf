# FlyShelf â€” Complete Application Documentation

> **Version:** 3.0.0 | **Platform:** Windows 10/11 (PC) + Android (Companion) | **Author:** Shivendra

---

## 1. What Is FlyShelf?

**FlyShelf** is a **cross-device clipboard manager and file transfer tool** for Windows. It intercepts everything you copy â€” text, images, files, code, URLs â€” organizes it in a floating overlay, and **syncs it across all your devices** (PC â†” PC, PC â†” Android) over LAN and the internet.

**Think of it as:** Windows Clipboard History (Win+V) on steroids â€” with drag-and-drop, cross-device sync, smart content detection, an embedded web server, and a Cloudflare tunnel for internet access. *(Note: Global Transfer via Cloudflare is disabled in the Microsoft Store version to comply with Store policies).*

---

## 2. Tech Stack

| Layer | Technology |
|---|---|
| **Framework** | WPF (.NET 10, C#) |
| **UI Toolkit** | MicaWPF (Windows 11 Mica blur) + WPF-UI (Fluent controls) + MaterialDesignThemes |
| **MVVM** | CommunityToolkit.Mvvm (`ObservableObject`, `RelayCommand`) |
| **Networking** | Raw `HttpListener` (embedded HTTP server) + TCP reverse proxy |
| **Cloud Sync** | Firebase Realtime Database + Firebase Storage |
| **Internet Tunnel** | Cloudflare `cloudflared` (free Argo tunnels via `trycloudflare.com`) |
| **QR Codes** | ZXing.Net (generate + scan) |
| **PDF** | PdfSharp (merge, convert) |
| **Emoji** | Emoji.Wpf (color emoji rendering) |
| **GIF** | XamlAnimatedGif (animated GIF playback) |
| **Build** | Single-file self-contained publish (`dotnet publish -r win-x64`) |
| **Android** | React Native / Expo (companion app) |

---

## 3. Application Architecture

```
â”Œâ”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”
â”‚                    App.xaml.cs (Entry Point)              â”‚
â”‚  â€¢ Single-instance mutex guard                           â”‚
â”‚  â€¢ Global mouse hook (shake detection)                   â”‚
â”‚  â€¢ Global exception handlers                             â”‚
â”‚  â€¢ Auto-startup registry                                 â”‚
â””â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”¬â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”˜
                     â”‚ creates
                     â–¼
â”Œâ”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”
â”‚               MainWindow.xaml (FlyShelf)                  â”‚
â”‚  â€¢ Floating clipboard overlay (the core UI)              â”‚
â”‚  â€¢ Clipboard monitoring (Win32 hooks)                    â”‚
â”‚  â€¢ Drag-and-drop target                                  â”‚
â”‚  â€¢ Three display modes: Mini / Medium / Full             â”‚
â””â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”¬â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”˜
                     â”‚ DataContext
                     â–¼
â”Œâ”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”
â”‚            FlyShelfViewModel.cs (Brain)                   â”‚
â”‚  â€¢ ObservableCollection<ClipboardItem> DroppedItems      â”‚
â”‚  â€¢ Content classification (Text/URL/Code/File/Image)     â”‚
â”‚  â€¢ Smart actions (math solve, color detect, QR scan)     â”‚
â”‚  â€¢ Firebase sync orchestration                           â”‚
â”‚  â€¢ Undo/Redo stack, Pin, Sort                            â”‚
â”œâ”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”¤
â”‚  Owns 3 subsystems:                                      â”‚
â”‚  â”œâ”€ NetworkSyncServer  (LAN HTTP server)                 â”‚
â”‚  â”œâ”€ DocumentSniffer    (filesystem watcher)              â”‚
â”‚  â””â”€ FirebaseListener   (cloud polling)                   â”‚
â””â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”˜
```

---

## 4. Every Feature Explained

### 4.1 Clipboard Monitoring & Capture
- Hooks into the Windows clipboard via `AddClipboardFormatListener` (Win32).
- Captures: **text**, **images/screenshots**, **file drops**, **URLs**, **code snippets**.
- Uses a `_isWritingClipboard` flag to prevent re-capturing items the app itself writes.
- Deduplicates: if the same text/file is copied again, the existing card moves to the top instead of creating a duplicate.

### 4.2 The FlyShelf (Floating Overlay)
- **Activation:** Shake the mouse while holding left-click (4 rapid direction reversals detected by a low-level mouse hook in `App.xaml.cs`).
- **Positioning:** Appears at the cursor position. Stays on-screen via `Topmost=true`.
- **Three Modes:** Mini (260Ã—260), Medium (360Ã—380), Full (850Ã—workarea). Toggled via Expand button.
- **Auto-hide:** Closes when it loses focus (`Window_Deactivated`).
- **Drag-out:** Items can be dragged from the FlyShelf into any app (Explorer, email, etc.).
- **Drag-in:** Files dragged onto the FlyShelf are added to the clipboard stack.

### 4.3 Smart Content Classification
When text is copied, regex patterns auto-classify it:

| Pattern | Classification | Extension Badge |
|---|---|---|
| `http://` or `https://` | `Url` | LINK |
| `#include`, `public class`, `def `, `console.log` | `Code` | C++, C#, PYTHON, JS, HTML, etc. |
| `PS C:\`, `npm run`, `git clone`, `sudo` | `Code` (Terminal) | TERM |
| JSON object/array | `Code` | JSON |
| Everything else | `Text` | TEXT |

Files are classified by extension into: `File`, `Document`, `Pdf`, `Image`, `Video`, `Audio`, `Archive`, `Presentation`.

### 4.4 Smart Actions (per-item context actions)
Each `ClipboardItem` runs `EvaluateSmartActions()` which detects special content:

- **Math Solver:** If the clipboard contains an equation like `(5+3)*2`, it auto-solves it using a Shunting-yard algorithm (`MathSolver.cs`). Shows result as `= 16`. Supports `sqrt`, `sin`, `cos`, `tan`, `log`, `^`, `pi`, `e`.
- **Graph Plotter:** If the equation contains `x` (e.g., `x^2 + 3x`), a "Plot Graph" button appears. Opens `GraphWindow.xaml` with a zoomable/pannable WPF Canvas plot.
- **Color Picker:** Detects `#FF5733`, `rgb(255,87,51)`, `hsl(14,100%,60%)` in text. Shows a color swatch preview. Click to copy in hex/rgb/hsl formats. (`ColorHelper.cs`)
- **URL Cleaning:** Automatically strips UTM tracking parameters (`utm_source`, `fbclid`, etc.) from URLs.
- **Timer & Reminders:** Typing `/5` sets a 5-minute countdown timer. Typing natural language like "remind me in 30 minutes to check build" uses `NaturalLanguageReminderParser.cs` to set reminders.
- **OCR Text Extraction:** Uses NPU-accelerated Windows AI TextRecognizer (`ModernOcrEngine.cs`) to instantly extract text from images.
- **QR Scanner:** Images are scanned for QR codes using ZXing. If found, the decoded text is surfaced.

### 4.5 Image & Screenshot Handling
- Screenshots (Ctrl+PrtSc, Snipping Tool) are captured as `BitmapSource`.
- Saved permanently to `%AppData%\FlyShelf\Images\` as PNG.
- Thumbnails decoded at 250px width for performance.
- **GIF support:** Animated GIFs play inline using `XamlAnimatedGif`.
- **Image â†’ PDF:** Right-click â†’ Convert to PDF (uses PdfSharp, fits to A4).

### 4.6 Document Sniffer
`DocumentSniffer.cs` uses `FileSystemWatcher` to monitor:
- `~/Downloads/` folder
- `~/AppData/Microsoft/Windows/Recent/` (Windows recent files)
- User-configured custom paths

When a `.pdf`, `.docx`, or `.doc` file appears (download completes), it's auto-added to the clipboard stack. `.lnk` (shortcut) files are resolved to their targets.

### 4.7 Emoji Picker
- Floating `EmojiPickerWindow` with 800+ emojis across 11 categories.
- Color rendering via `Emoji.Wpf` package (WPF's native TextBlock only renders monochrome).
- Real-time search filtering.
- Click any emoji â†’ copies to clipboard + shows toast.

### 4.8 PDF Merge
- Select multiple PDF items â†’ Right-click â†’ Merge PDFs.
- `PdfMergeWindow.xaml` allows reordering via drag handles.
- Uses PdfSharp to combine into a single output file.

### 4.9 Table Editor
`TableEditorWindow.xaml` â€” paste tabular data (CSV, TSV) and edit it in a grid. Export back as CSV/TSV.

### 4.10 QuickLook Preview
`QuickLookWindow.xaml` â€” spacebar preview for files (like macOS QuickLook). Shows images inline, text content for documents.

### 4.11 Taskbar Widget
`TaskbarWindow.xaml` â€” a small always-visible widget docked to the taskbar showing the last clipboard item. Configurable alignment (left/center/right).

### 4.12 Pin & Persist
- Items can be **pinned** â€” pinned items survive "Clear All" and are saved to `%AppData%\FlyShelf\pinned_items.json`.
- Full clipboard history (text + images) persists across restarts via `clipboard_history.json`.
- History saves are **debounced** (500ms) with **atomic writes** (write to `.tmp` then rename).

### 4.13 Undo Delete
Deleted items are pushed to `_deletedItemsHistory` (a `Stack`). Ctrl+Z or Undo button restores them.

### 4.14 Context Sorting
`SortForContext()` reorders the clipboard stack based on the currently active window title â€” items copied from the same app float to the top.

---

## 5. Networking Architecture (The Big Picture)

```
â”Œâ”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”         â”Œâ”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”
â”‚  Android App â”‚â—„â”€â”€â”€â”€â”€â”€â”€â–ºâ”‚   Firebase Realtime DB       â”‚
â”‚  (Companion) â”‚         â”‚  /clipboard (shared items)   â”‚
â””â”€â”€â”€â”€â”€â”€â”¬â”€â”€â”€â”€â”€â”€â”€â”˜         â”‚  /active_devices (heartbeat) â”‚
       â”‚                 â”‚  /forced_sync (direct send)   â”‚
       â”‚                 â”‚  /device_groups               â”‚
       â”‚                 â””â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”¬â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”˜
       â”‚                            â”‚
       â”‚    â”Œâ”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”¼â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â” 
       â”‚    â”‚                       â”‚                   â”‚
       â”‚    â–¼                       â–¼                   â”‚
       â”‚  â”Œâ”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”   â”Œâ”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”        â”‚
       â”‚  â”‚  PC #1       â”‚  â”‚  PC #2           â”‚       â”‚
       â”‚  â”‚  FlyShelf    â”‚  â”‚  FlyShelf        â”‚       â”‚
       â”‚  â”‚              â”‚  â”‚                  â”‚       â”‚
       â”‚  â”‚ HTTP Server  â”‚  â”‚  HTTP Server     â”‚       â”‚
       â”‚  â”‚ :8999        â”‚  â”‚  :8999           â”‚       â”‚
       â”‚  â”‚      â”‚       â”‚  â”‚                  â”‚       â”‚
       â”‚  â”‚  Cloudflare  â”‚  â”‚                  â”‚       â”‚
       â”‚  â”‚  Tunnel â”€â”€â”€â”€â”€â”¼â”€â”€â”¼â”€â”€â–º xxx.trycloudflare.com â”‚
       â”‚  â””â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”˜  â””â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”˜       â”‚
       â”‚         â”‚                                      â”‚
       â””â”€â”€â”€â”€â”€â”€â”€â”€â”€â”˜  (direct LAN or via Cloudflare)      â”‚
                                                        â”‚
                  Firebase Storage (file upload fallback)â”‚
                  â—„â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”˜
```

### 5.1 Layer 1: Local HTTP Server (`NetworkSyncServer.cs`)

An embedded HTTP server runs on port **8999** using `HttpListener`. It serves:

| Endpoint | Auth | Purpose |
|---|---|---|
| `GET /` | None | Serves the embedded web client UI (HTML/JS) |
| `GET /ping` | None | LAN discovery ("pong" response) |
| `GET /api/health` | None | Health check (used by Cloudflare verification) |
| `GET /download?path=...` | None | Serves files for cross-device download |
| `POST /api/pair` | Pairing Key | QR code device pairing |
| `GET /api/discover` | Pairing Key | Returns connection URLs for paired devices |
| `GET /api/sync` | PIN | Returns last 15 clipboard items as JSON |
| `POST /api/sync_text` | PIN | Receive text from mobile/web |
| `POST /api/sync_file` | PIN | Receive file upload |
| `POST /api/archive_upload` | PIN | Batch file upload (preserves folder structure) |
| `POST /api/upload_chunk` | PIN | Chunked upload for large files |
| `POST /api/upload_finalize` | PIN | Finalize chunked upload |
| `POST /api/relay_upload` | PIN | PC-to-PC file relay |
| `POST /api/convert_to_pdf` | PIN | Server-side imageâ†’PDF conversion |

**Binding Strategy (cascading fallback):**
1. `http://+:8999/` (all interfaces â€” requires admin or URL ACL)
2. `http://*:8999/` (alternative wildcard)
3. `http://{LAN_IP}:8999/` + `http://localhost:8999/`
4. **TCP Proxy fallback:** `HttpListener` on `localhost:18999` + raw `TcpListener` on `0.0.0.0:8999` that proxies TCP streams with Host header rewriting. This works **without admin privileges**.

**Auth Model:**
- PIN-based: 5-digit token stored in settings (default `55555`).
- QR Pairing: Devices paired via QR code bypass the PIN using a 32-char pairing key.
- Native mobile app identified by `X-FlyShelf-Client: MobileCompanion` header.

### 5.2 Layer 2: Cloudflare Tunnel (`CloudflareDaemon.cs`)

Makes the local HTTP server accessible from **anywhere on the internet** â€” no port forwarding needed.

**How it works:**
1. On startup, downloads `cloudflared.exe` (~40MB) to `%AppData%\FlyShelf\agent\`.
2. Runs: `cloudflared tunnel --url http://localhost:8999 --no-autoupdate`
3. Cloudflare assigns a random URL: `https://xxx-yyy-zzz.trycloudflare.com`
4. This URL is published to Firebase so other devices can discover it.

**Resilience:**
- Auto-retries with exponential backoff (5s â†’ 10s â†’ 20s â†’ 30s cap).
- Protocol switching: alternates between QUIC (UDP 7844) and HTTP/2 (TCP 443) on failures.
- Health monitor: pings localhost every 60s. After 3 consecutive failures, kills and restarts the tunnel.
- Self-verification: after tunnel starts, pings `localhost:8999/api/health` to confirm the tunnel actually works before advertising it.

### 5.3 Layer 3: Firebase Cloud Sync (`FirebaseSyncManager.cs` + `FirebaseListener.cs`)

**Push (outgoing):** When you copy something, `PushToGlobalSync()` sends it to Firebase Realtime Database at `/clipboard.json`. Each entry includes: content, type, timestamp, sender device name, and download URL.

**Pull (incoming):** `FirebaseListener.cs` polls Firebase every few seconds. New items from other devices are added to the local clipboard stack. Self-echoes are filtered by `SourceDeviceName`.

**File Sync Priority:**
1. **Cloudflare tunnel** (preferred â€” free, unlimited size, instant). Constructs URL: `https://xxx.trycloudflare.com/download?path=C:\...\file.pdf`
2. **Firebase Storage** (fallback for files <25MB when tunnel is down). Uploads to `flyshelf-sync.appspot.com`.
3. **Skip** (file >25MB + no tunnel = shows warning toast).

**Deduplication:** 10-second cooldown window on content fingerprints prevents rapid-fire duplicates.

**Auto-cleanup:** Text entries auto-delete from Firebase after 5 minutes. File entries after 24 hours.

**Device Registry:** Each PC heartbeats to `/active_devices/{deviceId}` every 60 seconds with its name, type, LAN IP, and Cloudflare URL. Stale devices (no heartbeat in 2 minutes) are shown as offline.

### 5.4 Layer 4: Force Send & Device Groups

**Force Send:** Select items â†’ pick target devices â†’ sends directly to `/forced_sync/{targetDeviceId}`. The target device's `FirebaseListener` picks it up.

**Device Groups:** Named groups of devices stored at `/device_groups/`. One-click send to "All Mobile" or "Office PCs".

### 5.5 QR Code Pairing (`DevicePairingManager.cs`)

1. PC generates QR code containing: `{ app, key, local, global, pin, name, id }`.
2. Android app scans QR â†’ extracts pairing key + URLs.
3. Android sends `POST /api/pair` with the key â†’ PC validates and registers the device.
4. Paired devices bypass PIN auth using `X-Pairing-Key` header.

---

## 6. Embedded Web Client

A full HTML/JS web client is **embedded inside the .exe** as a zip resource. At startup, `RuntimeHost.cs` extracts it to `%LocalAppData%\FlyShelf\RuntimeCore\Resources\WebClient\`. The HTTP server serves it at `GET /`.

Users on any device can open `http://{PC_IP}:8999` in a browser to view and manage clipboard items.

---

## 7. File Structure (Every Class Explained)

### `/ViewModels/`
| File | Purpose |
|---|---|
| `FlyShelfViewModel.cs` | Central ViewModel. Owns `DroppedItems`, handles all drop/paste logic, content classification, sync orchestration. 1077 lines. |
| `ClipboardItem.cs` | Data model for one clipboard entry. Properties: `FilePath`, `RawContent`, `ItemType`, `Icon`, `IsPinned`, smart action methods (`Execute`, `RunInTerminal`, `CompileAndRunNative`, `ConvertDocumentTask`, `ExtractText`, `ExtractTable`, `ScanForQRCodeAsync`, `ConvertImageToPdf`). 1130 lines. |

### `/Classes/`
| File | Purpose |
|---|---|
| `NetworkSyncServer.cs` | Embedded HTTP server + TCP proxy. Handles all API endpoints. 1544 lines. |
| `FirebaseSyncManager.cs` | Push clipboard items + files to Firebase. Device registry + groups. |
| `FirebaseListener.cs` | Polls Firebase for incoming items from other devices. |
| `CloudflareDaemon.cs` | Downloads, spawns, monitors, and auto-restarts `cloudflared.exe`. |
| `ClipboardHistoryManager.cs` | Persists clipboard history to JSON on disk with debounced atomic writes. |
| `SettingsManager.cs` | Loads/saves app settings to `%AppData%\FlyShelf\config.json`. Auto-saves on any property change. |
| `DevicePairingManager.cs` | QR code generation (ZXing), pairing key management, paired device registry. |
| `DocumentSniffer.cs` | `FileSystemWatcher` on Downloads + Recent folders. Auto-captures new PDFs/DOCs. |
| `MathSolver.cs` | Shunting-yard algorithm math evaluator. Supports `+âˆ’Ã—Ã·^`, functions, constants. |
| `ColorHelper.cs` | Detects hex/rgb/hsl color codes. Converts between formats. |
| `Logger.cs` | Async buffered logger with `ConcurrentQueue` + 2s flush timer. Separate network diagnostics log. Auto-truncates to 500 lines. |
| `RuntimeHost.cs` | Extracts embedded zip resources (Scripts, WebClient) on first run or version change. |
| `NativeMethods.cs` | Win32 P/Invoke declarations for clipboard hooks, window management. |
| `UpdateManager.cs` | Checks GitHub `version.json` for updates. Downloads new .exe + self-replaces via batch script. |
| `GeminiEngine.cs` | Optional Gemini AI API integration (API key in settings). |
| `SmoothScrollBehavior.cs` | WPF attached behavior for smooth scrolling in ListBoxes. |
| `NetworkActivityLog.cs` | In-memory ring buffer of recent network events for the live monitor UI. |

### `/Windows/`
| File | Purpose |
|---|---|
| `HubWindow.xaml` | The "big" clipboard manager. Full-featured UI with tabs: Clipboard, Network, Settings. 199KB of XAML. |
| `EmojiPickerWindow.xaml` | Floating emoji picker with 800+ emojis, search, categories. |
| `GraphWindow.xaml` | Zoomable/pannable math graph plotter (renders equations with `x`). |
| `TimerWindow.xaml` | Countdown timer triggered by `/N` clipboard commands. |
| `QuickLookWindow.xaml` | Spacebar file preview (images, text). |
| `PdfMergeWindow.xaml` | Drag-reorder PDF merger. |
| `TableEditorWindow.xaml` | CSV/TSV grid editor. |
| `TaskbarWindow.xaml` | Small taskbar-docked widget showing last clipboard item. |
| `PageSelectorWindow.xaml` | PDF page range selector for extraction. |
| `ToastWindow.xaml` | Minimal notification toast (auto-dismiss). |
| `PreviewPopup.xaml` | Small hover preview popup. |

---

## 8. Data Persistence Model

| Data | Location | Format |
|---|---|---|
| App settings | `%AppData%\FlyShelf\config.json` | JSON |
| Clipboard history | `%AppData%\FlyShelf\clipboard_history.json` | JSON |
| Pinned items | `%AppData%\FlyShelf\pinned_items.json` | JSON |
| Paired devices | `%AppData%\FlyShelf\paired_devices.json` | JSON |
| Persistent images | `%AppData%\FlyShelf\Images\FlyShelf_*.png` | PNG files |
| Activity logs | `%AppData%\FlyShelf\Logs\activity_log.txt` | Text |
| Network logs | `%AppData%\FlyShelf\Logs\network_diagnostics.txt` | Text |
| Extracted scripts | `%LocalAppData%\FlyShelf\RuntimeCore\` | Extracted zips |
| Cloudflared binary | `%AppData%\FlyShelf\agent\cloudflared.exe` | Binary |
| Received files | `~/Downloads/FlyShelf/Clipboard/{device}/{date}/` | Original files |

---

## 9. Startup Sequence (Exact Order)

1. `App.OnStartup()` â†’ Acquire single-instance `Mutex`
2. `RuntimeHost.Initialize()` â†’ Extract embedded Scripts + WebClient zips (skipped if version unchanged)
3. `SettingsManager.Load()` â†’ Read `config.json`
4. Write to `HKCU\...\Run` for auto-startup
5. Install global exception handlers (UI + background + async)
6. If no `DeviceName` set â†’ show registration dialog
7. Show "Service online" toast
8. **Background (async):** Create `MainWindow` â†’ Load persisted clipboard history â†’ Show then immediately Hide (for tray icon registration) â†’ Create `TaskbarWindow`
9. **Background:** `FlyShelfViewModel` constructor â†’ Create `NetworkSyncServer`, `DocumentSniffer`, `FirebaseListener`
10. **Background thread:** Start HTTP server â†’ Start Cloudflare tunnel â†’ Start filesystem watchers â†’ Start Firebase polling â†’ Start heartbeat timer
11. **After 8s:** Dump network diagnostics snapshot to log

---

## 10. Build & Deployment

```powershell
# Build (debug)
dotnet build AdvanceClip_PC/AdvanceClip.csproj -c Release

# Publish (single-file self-contained .exe)
dotnet publish AdvanceClip_PC/AdvanceClip.csproj -c Release -r win-x64 \
  --self-contained true /p:PublishSingleFile=true \
  /p:IncludeNativeLibrariesForSelfExtract=true -o FINAL/
```

**Output:** `FINAL/FlyShelf.exe` (~188MB, self-contained â€” no .NET runtime needed on target machine).

**Build process:** A custom MSBuild target (`ZipPayloads`) zips `Scripts/` and `Resources/WebClient/` into embedded resources before compilation. These are extracted at runtime by `RuntimeHost`.

**Auto-update:** `UpdateManager.cs` checks `https://raw.githubusercontent.com/shdra06/AdvanceClip/main/version.json` for newer versions. Downloads the new .exe, creates a batch script that waits for the old process to exit, swaps the file, and relaunches.

---

## 11. Android Companion App

Located in `AdvanceClip_Android/`. Built with React Native + Expo.

**Features:**
- Scans QR code from PC to pair
- Views PC clipboard items
- Sends text/files to PC
- Receives files from PC (with progress bar)
- Shares clipboard across PC â†” Mobile via Firebase

**Communication:** Uses the same Firebase Realtime Database + direct HTTP calls to the PC's server (via LAN IP or Cloudflare tunnel URL).

---

## 12. Key Design Decisions

| Decision | Rationale |
|---|---|
| **Mouse shake activation** | No hotkey conflicts. Works in any app. Physical gesture is unambiguous. |
| **HttpListener + TCP proxy** | Works without admin privileges. No need for Kestrel/ASP.NET overhead. |
| **Cloudflare free tunnels** | Zero-config internet access. No port forwarding. No cost. |
| **Firebase RTDB** | Real-time sync with minimal latency. Free tier sufficient for personal use. |
| **Single-file publish** | One .exe, no installer. Portable. Drop-and-run. |
| **Embedded web client** | Any device with a browser can access clipboard. No app install needed. |
| **Debounced saves** | Prevents disk thrashing from rapid clipboard changes. |
| **Atomic file writes** | Write to `.tmp` then rename â€” prevents corruption on crash. |
| **Background startup** | Heavy WPF layout + network init happens off UI thread. Boot time <50ms visible. |

---

## 13. NuGet Dependencies

| Package | Version | Purpose |
|---|---|---|
| `MicaWPF` | 6.3.2 | Windows 11 Mica/Acrylic backdrop |
| `WPF-UI` | 4.2.0 | Fluent design controls (Button, TextBox, etc.) |
| `WPF-UI.Tray` | 4.2.0 | System tray NotifyIcon |
| `CommunityToolkit.Mvvm` | 8.4.0 | MVVM base classes |
| `MaterialDesignThemes` | 5.3.1 | Additional Material Design controls |
| `Emoji.Wpf` | 0.3.4 | Full-color emoji rendering in WPF |
| `XamlAnimatedGif` | 2.3.0 | Animated GIF playback |
| `ZXing.Net.Bindings.Windows` | 0.16.14 | QR code generation + scanning |
| `PdfSharp` | 6.1.1 | PDF merge, page extraction, imageâ†’PDF |
| `FirebaseStorage.net` | 1.0.3 | Firebase Storage file uploads |
| `Newtonsoft.Json` | 13.0.3 | JSON (legacy, alongside System.Text.Json) |
