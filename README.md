<div align="center">
 
# ⚡ FlyShelf

### **The Ultimate Premium Cross-Device Clipboard & Productivity Ecosystem**

*Copy on one device. Paste anywhere. Instantly. Secure peer-to-peer pipelines for Windows & Android.*

[![Windows Desktop App](https://img.shields.io/badge/Windows_PC-v4.0.0-0078D6?style=for-the-badge&logo=windows&logoColor=white)](https://github.com/shdra06/FlyShelf/releases/download/v4.0.0/FlyShelf.exe)
[![Android Mobile Companion](https://img.shields.io/badge/Android_Mobile-v7.1.0-3DDC84?style=for-the-badge&logo=android&logoColor=white)](https://github.com/shdra06/FlyShelf/releases/download/v7.1.0/FlyShelf_Mobile.apk)
[![Ecosystem License](https://img.shields.io/badge/License-Proprietary-FF007F?style=for-the-badge&logo=shield&logoColor=white)](LICENSE)

🚀 **Live Interactive Showcase**: [https://shdra06.github.io/FlyShelf](https://shdra06.github.io/FlyShelf)

---

</div>

## 🎯 What is FlyShelf?

**FlyShelf** is an ultra-premium, zero-friction **cross-device clipboard manager** and real-time productivity ecosystem. Engineered specifically for developers, power users, and creators, it seamlessly unifies your clipboard history, local files, code snippets, and screenshots across all your Windows PCs and Android devices in real-time.

Unlike generic clipboard managers, FlyShelf works entirely **peer-to-peer (P2P)** where possible. It autodetects your network topology to route clipboard assets over the fastest path available—lightning-speed **WiFi LAN connections (100+ Mbps)**, firewalled secure **Cloudflare Tunnels**, or low-latency **Firebase Realtime Database** cloud relays. No advertising, no accounts to create, no spyware, and absolute privacy.

---

## ⚡ Feature Highlights at a Glance

- 📋 **Smart Clipboard Capture** — Auto-captures text, images, files, URLs, code, PDFs, documents, and audio
- 🔄 **Cross-Device Sync** — Real-time sync across Windows PCs and Android via LAN, Cloudflare Tunnel, or Firebase relay
- 🔍 **Instant Search & Filters** — Full-text search (Ctrl+F) with category filters (Images, Pinned, PDF, Docs)
- 📌 **Pin & Protect** — Pin important items to protect from auto-cleanup
- 🖱️ **Drag & Drop** — Drag files into FlyShelf or drag clips out into any app (Outlook, Slack, Explorer)
- 🎨 **7 Color Themes** — Default, Midnight, Ocean, Sunset, Emerald, Lavender, and Light
- 🖼️ **Custom Wallpapers** — Set any image as your clipboard backdrop with frosted glass header
- 📝 **Quick Notes** — Per-day bullet notes with image embeds and freeform mode
- ✅ **To-Do Lists** — Daily task lists with done/undone toggle
- 🔤 **Text Shortcuts** — Type `/trigger` abbreviations that auto-expand to full text snippets (50 max for Pro)
- 📦 **PDF Merger** — Merge multiple PDFs with drag-to-reorder and page selection
- 📊 **Table Extraction** — Extract tables from screenshots using local AI (Bradley-Roth + Projection-Profile) or Gemini API
- 🔍 **AI OCR Text Extraction** — NPU-accelerated text extraction from images using Windows AI TextRecognizer (Win11 24H2+)
- ⏰ **Natural Language Reminders** — Type naturally to create alerts ("remind me in 30 mins to check build")
- 🎓 **First-Run Onboarding** — Interactive tutorial experience to get started seamlessly
- 📷 **QR Code Scanner** — Decode QR codes from clipboard images
- ⏱️ **Timer & Stopwatch** — Type `/5`, `timer 30 min`, or `2:30` into search to launch a countdown
- 😊 **Emoji Picker** — Searchable floating panel with 800+ color emojis
- 🔒 **Password Manager** — Mark any text as password (masked display), view/edit/rename
- 🎵 **Audio Playback** — Play audio clipboard items with single-instance player
- 🖥️ **macOS-style QuickLook** — Spacebar instant preview for any item
- 🔗 **UTM Link Cleaner** — Strip marketing trackers (`utm_*`, `fbclid`) from URLs
- 🧮 **Math Solver** — Auto-evaluates expressions like `(12 * 8) - 15`
- 🔎 **Google Search** — Right-click any text to search on Google instantly
- 💻 **Code Actions** — Open in VS Code, Run in Terminal, Compile C++ & Run
- 🌐 **QR Quick Pair** — Scan QR code or enter 6-character code to pair devices instantly
- 🔐 **PIN Security** — PIN-based authentication for web/device access
- 🛡️ **Encrypted Sync** — End-to-end encrypted clipboard transfer
- 📡 **Cloudflare Tunnels** — Auto-retry with QUIC/HTTP2 protocol switching for global sync
- 🪟 **Taskbar Widget** — Embedded taskbar button with configurable positioning
- 🤏 **Shake to Open** — Rapidly shake mouse while holding left-click to summon FlyShelf
- ⌨️ **Global Hotkeys** — `Win + Shift + V` or `Alt + C` to summon dashboard
- 📱 **Android Overlay Ball** — Physics-enabled floating bubble for clipboard access anywhere on Android
- 🔔 **Update Manager** — Auto-update with SHA256 verification and in-app badge notification
- 💎 **Freemium Model** — Generous free tier with Pro unlock (₹299 lifetime)

---

## ✨ Features in Detail

### 📋 Universal Clipboard & File Sync
- **Instantly Synchronize**: Copy text, hyperlinks, rich code blocks, screenshots, or raw files on any device and watch them appear globally in milliseconds.
- **Dynamic Connection Routing**: Autodetects network environments to route heavy files locally over LAN, falling back to secure Cloudflare relays when remote.
- **Intelligent Deduplication**: Smart memory caching prevents double clipboard writes and maintains strict chronological order.
- **Multi-Select Operations**: Select multiple items for batch unpin or PDF merge workflows.
- **Auto Cleanup**: Automatically purge unpinned entries after 7, 14, or 30 days (configurable).

### 🖥️ Mica Summon Dashboard (Windows PC)
- **Summon Dashboard**: A global keyboard shortcut (`Win + Shift + V` or `Alt + C`) or a cursor shaking gesture summons a beautiful glassmorphic dashboard featuring deep Windows 11 Mica blur and Acrylic backdrops.
- **Interactive Action Pills**: Context-aware buttons reveal themselves on hover with micro-animations and drop-shadows.
- **Premium Chevron Control**: Sleek, high-contrast, glassmorphic expand/collapse buttons point down when collapsed (blue) and up when expanded (red) to maximize text space utilization.

### 📱 Android Foreground Service & Physics Overlay Ball
- **Persistent Overlay**: Access your entire workspace history anywhere on Android with a premium, physics-enabled floating overlay bubble.
- **Paste with a Single Tap**: Copy historical clips back into any active text input without shifting focus.
- **Kotlin Foreground Service**: Backed by an energy-efficient native background service that survives Android's aggressive memory management and screen lock.

### 🎨 7 Premium Color Themes
Match your focus with seven premium color schemes. Selecting a theme dynamically swaps accent colors, button chrome, text tones, and auto-applies matching wallpapers:
- ⚙️ **Default**: Clean native dark look matching Windows 11 Mica system theme
- 🌌 **Midnight**: Deep cybernetic dark blue with indigo and purple gradients
- 🌊 **Ocean**: Glowing neon teal with cyan and aqua highlights
- 🌅 **Sunset**: Warm orange/pink coral gradients with amber accents
- 🌲 **Emerald**: Sleek mint green and emerald highlights
- 🔮 **Lavender**: Pastel violet and magenta gradients
- ☀️ **Light**: Clean light mode with white surfaces and dark text

### 🖼️ Custom Wallpapers & Mascot Themes
- **Custom Wallpaper**: Choose any image as your clipboard backdrop with a frosted glass header overlay.
- **Mascot Theme Packs**: Install animated companion characters (GIF sprites) that react to clipboard actions — idle, delete, copy, search triggers.
- **Theme Pack Import**: Drag `.flyshelf-theme` zip files onto FlyShelf to install community themes.
- **Hot-Reload**: FileSystemWatcher monitors the themes folder for live changes.

### 📝 Quick Notes
- **Per-Day Bullet Notes**: Organized by date with sidebar navigation for browsing previous days.
- **Bullet Mode**: Create structured notes with collapsible headers and timestamps.
- **Freeform Mode**: Switch to a free-text editor with inline image embedding.
- **Image Embeds**: Paste or drag images directly into note bullets.
- **Search Notes**: Full-text search across all note days.
- **Auto-Save**: Debounced 2-second save with atomic file writes.

### ✅ To-Do Lists
- **Daily Task Lists**: Organized by date with done/undone toggle per item.
- **Quick Add**: Add tasks inline with auto-timestamp.
- **Persistent**: Survives app restarts via JSON serialization.

### 🔤 Text Shortcuts (Text Expander)
- **Custom Triggers**: Define `/trigger` abbreviations that auto-expand to full text snippets.
- **Auto-Paste**: Expansions are copied to clipboard and auto-pasted into active input fields.
- **Encrypted Storage**: Shortcuts are stored with DPAPI encryption.

### 📦 PDF Tools Suite
- **PDF Merger**: Select multiple PDFs on the shelf, reorder them visually with drag handles, and merge into a single PDF.
- **Page Selector**: Choose specific pages from each PDF to include in the merge.
- **Page Reorder**: Drag-and-drop reordering of individual pages across documents.
- **Image → PDF**: Convert any clipboard image to a PDF document.
- **PDF → Word**: Convert PDF files to editable DOCX format.
- **Document → PDF**: Convert Word, Excel, PowerPoint to PDF.

### 📊 Advanced Table Extraction
- **Local AI Engine**: Bradley-Roth adaptive thresholding + Projection-Profile column separation for accurate table detection from screenshots — entirely offline.
- **Gemini AI Fallback**: Google Gemini 2.0 Flash API for complex table extraction.
- **Table Editor**: Full-featured table editing window with column resize, cell editing, and export to CSV/JSON.

### 🔀 Drag-and-Drop Workspace
- **Drag-In Files**: Select folders or documents from your file manager and drag them directly onto the FlyShelf overlay to instantly add them to the clipboard stack.
- **Drag-Out Clips**: Grab card thumbnails from the FlyShelf overlay and drop them directly into active applications (Outlook, Slack, Word, Explorer) to paste them instantly.

### 📁 Universal File Extension Support
FlyShelf classified badges adapt to **all file extensions** to sort and optimize your shelf history:
- **Documents & PDFs**: `.pdf`, `.docx`, `.xlsx`, `.pptx`, `.txt`, `.csv`
- **Media Assets**: Images (`.png`, `.jpg`, `.webp`, `.gif`), Audio (`.mp3`, `.wav`), Videos (`.mp4`, `.mkv`)
- **Archives**: `.zip`, `.rar`, `.7z`, `.tar.gz`

### 🔍 Contextual Smart Utilities
Right-click any card to trigger contextual actions:
- **🔎 Search on Google**: Query highlighted clips instantly.
- **🔗 UTM URL Cleaner**: Strips marketing tracking parameters (`utm_*`, `fbclid`) from links.
- 🧮 **Math Solver**: Auto-evaluates math equations like `(12 * 8) - 15` using a Shunting-yard algorithm.
- 🔍 **AI OCR Text Extraction**: NPU-accelerated text extraction from images using Windows AI TextRecognizer.
- ⏰ **Natural Language Reminders**: Type naturally in the search bar to set quick reminders.
- **📊 Table Data Extraction**: Extract structured tables from screenshots.
- **📷 QR Code Scanner**: Decode QR codes from clipboard images.
- **🖥️ QuickLook Preview**: Tap Spacebar on any item to open a clean instant preview window.
- **😊 Emoji Picker**: Floating category-filtered panel containing 800+ color emojis.
- **💻 Open in VS Code / Run in Terminal**: Launch code snippets directly.
- **🔒 Password Mode**: Mark any text as password with masked display.

### ⏱️ Speed Timer Commands
- **Command Triggers**: Type `/5`, `timer 30 min`, or `2:30` directly into the search bar to launch a timer.
- **Visual Countdown**: A progress ring glows dynamically and transitions color (cyan ➔ amber ➔ warning red) as time ticks down.

### 🔗 Network Sync & Device Pairing
- **LAN Sync**: Direct IP clipboard sync over Wi-Fi for lightning-fast local transfers.
- **Cloudflare Tunnel**: Auto-retry with QUIC/HTTP2 protocol switching for global internet sync.
- **Firebase Cloud Relay**: Low-latency relay for text items when direct connections aren't available.
- **QR Quick Pair**: Generate/scan QR codes for instant device pairing.
- **6-Character Pairing Code**: Alphanumeric code alternative to QR.
- **PIN Security**: PIN-based authentication for web/device access.
- **Sync Direction Control**: Granular Incoming / Outgoing / Both toggle per connection.
- **Encrypted Transfer**: End-to-end encrypted clipboard sync via `SyncCrypto`.
- **Live Network Logs**: Real-time network event viewer for debugging.

### 🪟 System Integration
- **System Tray Icon**: Always-visible tray icon with quick-access menu.
- **Taskbar Widget**: Embedded taskbar button with configurable positioning (Auto, Far Left, After Start, Before Tray, Custom offset).
- **Run at Startup**: Auto-launch on Windows login.
- **Shake to Open**: Rapidly shake mouse while holding left-click to summon FlyShelf.
- **Toast Notifications**: Non-intrusive alerts for sync events, timer completion, and more.
- **Smooth Scrolling**: Custom smooth scroll implementation for buttery-smooth list navigation.

### 🔔 Auto-Update System
- **Update Detection**: Checks GitHub releases for new versions.
- **In-App Badge**: Update available indicator on the toolbar.
- **Download & Install**: Download with progress bar, SHA256 hash verification, automatic restart.

---

## 🚀 Installation & Getting Started

FlyShelf is distributed as fully self-contained, ready-to-run standalone binaries. No installer or runtime setup is required.

- **Desktop (Windows 10/11)**: [Download FlyShelf.exe](https://github.com/shdra06/FlyShelf/releases/download/v4.0.0/FlyShelf.exe) (Single-file, self-contained release, no .NET install required).
- **Microsoft Store**: Available soon! (Note: Global Transfer is disabled in the Microsoft Store version to comply with Store policies. Pro features are unlocked via an in-app add-on rather than a license key).
- **Mobile (Android 8.0+)**: [Download FlyShelf_Mobile.apk](https://github.com/shdra06/FlyShelf/releases/download/v7.1.0/FlyShelf_Mobile.apk) (Lightweight, single-architecture arm64 release).

---

## 💎 Free vs Pro

| Feature | Free | Pro (₹299 Lifetime) |
|---------|------|---------------------|
| Clipboard History | 500 items | 2,500 items |
| Note History | 30 days | Unlimited |
| PDF Merges | 20/day | Unlimited |
| OCR Extractions | 30/day | Unlimited |
| Table Extractions | 15/day | Unlimited |
| Pins | 20 max | Unlimited |
| Color Themes | Default only | All 7 themes |
| Custom Wallpaper | — | ✅ |
| Text Shortcuts | 20 max | 50 max |
| Clipboard Size | Fixed | Adjustable |
| Cloudflare Tunnel | — | ✅ (Standalone only) |
| Glass UI Theme | — | ✅ |

---

## 🛡️ Privacy & Security Strategy
- **No Cloud Data Retained**: Transferred files stream entirely peer-to-peer over your local networks.
- **Zero Third-Party Storage**: Clipboard historical content is only cached locally in RAM/disk on your own machines.
- **Auto-Purge Relays**: Signaling relays on Firebase are configured to auto-delete text items after 5 minutes and file indicators after 24 hours.
- **DPAPI Encrypted Storage**: Sensitive data (shortcuts, passwords) are encrypted with Windows Data Protection API.
- **HMAC License Validation**: License keys are cryptographically validated with checksum verification on every load.
- **Assembly Integrity Check**: SHA-256 binary tamper detection on startup.

---

## 🔒 Proprietary Software & Licensing Notice

**Copyright © 2026 FlyShelf Ecosystem. All Rights Reserved.**

FlyShelf is a **proprietary, copyrighted, closed-source** software application. 
- The core execution source codes—including the C# WPF (.NET 10) desktop engine, the React Native Android companion workspace, and the native Kotlin foreground synchronization services—are strictly private, proprietary, and copyrighted.
- Pre-compiled binaries are distributed officially under strict security compliance for end-user personal productivity. Modification, compilation from unauthorized sources, reverse-engineering, or unauthorized redistribution of the binaries is strictly prohibited.
