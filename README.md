<div align="center">
 
# ⚡ FlyShelf

### **The Ultimate Premium Cross-Device Clipboard & Productivity Ecosystem**

*Copy on one device. Paste anywhere. Instantly. Secure peer-to-peer pipelines for Windows & Android.*

[![Windows Desktop App](https://img.shields.io/badge/Windows_PC-v6.0.1-0078D6?style=for-the-badge&logo=windows&logoColor=white)](https://github.com/shdra06/FlyShelf/releases/download/v6.0.1/FlyShelf.exe)
[![Android Mobile Companion](https://img.shields.io/badge/Android_Mobile-v6.0.1-3DDC84?style=for-the-badge&logo=android&logoColor=white)](https://github.com/shdra06/FlyShelf/releases/download/v6.0.1/FlyShelf_Mobile.apk)
[![Ecosystem License](https://img.shields.io/badge/License-Proprietary-FF007F?style=for-the-badge&logo=shield&logoColor=white)](LICENSE)

🚀 **Live Interactive Showcase**: [https://shdra06.github.io/FlyShelf](https://shdra06.github.io/FlyShelf)

---

</div>

## 🎯 What is FlyShelf?

**FlyShelf** is an ultra-premium, zero-friction **cross-device clipboard manager** and real-time productivity ecosystem. Engineered specifically for developers, power users, and creators, it seamlessly unifies your clipboard history, local files, code snippets, and screenshots across all your Windows PCs and Android devices in real-time.

Unlike generic clipboard managers, FlyShelf works entirely **peer-to-peer (P2P)** where possible. It autodetects your network topology to route clipboard assets over the fastest path available—lightning-speed **WiFi LAN connections (100+ Mbps)**, firewalled secure **Cloudflare Tunnels**, or low-latency **Firebase Realtime Database** cloud relays. No advertising, no accounts to create, no spyware, and absolute privacy.

---

## ✨ Features that Define FlyShelf

### 📋 Universal Clipboard & File Sync
- **Instantly Synchronize**: Copy text, hyperlinks, rich code blocks, screenshots, or raw files on any device and watch them appear globally in milliseconds.
- **Dynamic Connection Routing**: Autodetects network environments to route heavy files locally over LAN, falling back to secure Cloudflare relays when remote.
- **Intelligent Deduplication**: Smart memory caching prevents double clipboard writes and maintains strict chronological order.

### 🖥️ Mica summon Dashboard (Windows PC)
- **Summon Dashboard**: A global keyboard shortcut (`Win + Shift + V` or `Alt + C`) or a cursor shaking gesture summons a beautiful glassmorphic dashboard featuring deep Windows 11 Mica blur and Acrylic backdrops.
- **Interactive Action Pills**: Context-aware buttons reveal themselves on hover with micro-animations and drop-shadows.
- **Premium Chevron Control**: Sleek, high-contrast, glassmorphic expand/collapse buttons point down when collapsed (blue) and up when expanded (red) to maximize text space utilization.

### 📱 Android Foreground Service & Physics Overlay Ball
- **Persistent Overlay**: Access your entire workspace history anywhere on Android with a premium, physics-enabled floating overlay bubble.
- **Paste with a Single Tap**: Copy historical clips back into any active text input without shifting focus.
- **Kotlin Foreground Service**: Backed by an energy-efficient native background service that survives Android's aggressive memory management and screen lock.

### 🔀 Fluid Drag-and-Drop Workspace
- **Drag-In Files**: Select folders or documents from your file manager and drag them directly onto the FlyShelf overlay to instantly add them to the clipboard stack.
- **Drag-Out Clips**: Grab card thumbnails from the FlyShelf overlay and drop them directly into active applications (Outlook, Slack, Word, Explorer) to paste them instantly.

### 📁 Universal File Extension Support
FlyShelf classified badges adapt to **all file extensions** to sort and optimize your shelf history:
- **Documents & PDFs**: `.pdf`, `.docx`, `.xlsx`, `.pptx`, `.txt`, `.csv`
- **Media Assets**: Images (`.png`, `.jpg`, `.webp`, `.gif`), Audio (`.mp3`, `.wav`), Videos (`.mp4`, `.mkv`)
- **Archives**: `.zip`, `.rar`, `.7z`, `.tar.gz`

### 🎨 Visual Workspace Custom Themes
Match your focus with five premium glassmorphic color schemes. Selecting a theme changes the ambient background blurs, text gradients, and button shadows dynamically:
- 🌌 **Midnight**: Deep cybernetic dark blue with purple and cyan gradients (Default).
- 🌊 **Ocean**: Glowing neon teal with cyan highlights.
- 🌅 **Sunset**: Warm orange/pink coral gradients.
- 🌲 **Emerald**: Sleek mint green and emerald highlights.
- 🔮 **Lavender**: Pastel violet and magenta gradients.

### 📦 Local PDF Merger & Stitcher
- **Stitch Files**: Select multiple PDF items on your shelf, reorder them in a visual queue using drag handles, and merge them into a single high-quality PDF with a single click. Processes entirely locally inside the app.

### 📝 Clipboard Notes & Tasks
- **Sticky Notes**: Create, edit, and pin text notes directly on your clipboard shelf to store quick-access guidelines, snippets, or immediate reminders.

### ⏱️ Speed Timer Commands
- **Command Triggers**: Type `/5`, `timer 30 min`, or `2:30` directly into the search bar to launch a timer.
- **Visual Countdown**: A progress ring glows dynamically and transitions color (cyan ➔ amber ➔ warning red) as time ticks down.

### 🔍 Contextual Smart Utilities
Right-click any card to trigger contextual actions:
- **UTM URL Cleaner**: Strips marketing tracking parameters (`utm_*`, `fbclid`) from links.
- **Google Search**: Query highlighted clips instantly.
- **Math Solver**: Auto-evaluates math equations like `(12 * 8) - 15` using a Shunting-yard algorithm.
- **Gemini AI OCR**: Extract markdown tables from screenshots using Google Gemini API keys.
- **macOS QuickLook**: Tap Spacebar on any item to open a clean instant preview window.
- **Emoji Picker**: Floating category-filtered panel containing 800+ color emojis.

---

## 🚀 Installation & Getting Started

FlyShelf is distributed as fully self-contained, ready-to-run standalone binaries. No installer or runtime setup is required.

- **Desktop (Windows 10/11)**: [Download FlyShelf.exe](https://github.com/shdra06/FlyShelf/releases/download/v6.0.1/FlyShelf.exe) (Single-file, self-contained release, no .NET install required).
- **Mobile (Android 8.0+)**: [Download FlyShelf_Mobile.apk](https://github.com/shdra06/FlyShelf/releases/download/v6.0.1/FlyShelf_Mobile.apk) (Lightweight, single-architecture arm64 release).

---

## 🛡️ Privacy & Security Strategy
- **No Cloud Data Retained**: Transferred files stream entirely peer-to-peer over your local networks.
- **Zero Third-Party Storage**: Clipboard historical content is only cached locally in RAM/disk on your own machines.
- **Auto-Purge Relays**: Signaling relays on Firebase are configured to auto-delete text items after 5 minutes and file indicators after 24 hours.

---

## 🔒 Proprietary Software & Licensing Notice

**Copyright © 2026 FlyShelf Ecosystem. All Rights Reserved.**

FlyShelf is a **proprietary, copyrighted, closed-source** software application. 
- The core execution source codes—including the C# WPF (.NET 10) desktop engine, the React Native Android companion workspace, and the native Kotlin foreground synchronization services—are strictly private, proprietary, and copyrighted.
- Pre-compiled binaries are distributed officially under strict security compliance for end-user personal productivity. Modification, compilation from unauthorized sources, reverse-engineering, or unauthorized redistribution of the binaries is strictly prohibited.
