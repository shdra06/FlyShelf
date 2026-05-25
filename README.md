<div align="center">

# ⚡ FlyShelf — Best Cross-Platform Clipboard Manager for PC & Android

### **The Premium Cross-Device Clipboard & Universal Copy-Paste Sync Ecosystem**

*Copy on one device. Paste anywhere instantly. Safe & secure peer-to-peer local WiFi and cloud pipeline clipboard sync.*

[![Windows](https://img.shields.io/badge/Windows_Desktop-v7.5.0-0078D6?style=for-the-badge&logo=windows&logoColor=white)](https://github.com/shdra06/FlyShelf/releases/download/v7.5.0/FlyShelf.exe)
[![Android](https://img.shields.io/badge/Android_Mobile-v7.1.0-3DDC84?style=for-the-badge&logo=android&logoColor=white)](https://github.com/shdra06/FlyShelf/releases/download/v7.1.0/FlyShelf_Mobile.apk)
[![Privacy](https://img.shields.io/badge/Privacy_First-Zero_Telemetry-10B981?style=for-the-badge&logo=shield&logoColor=white)](PRIVACY_POLICY.md)

---

</div>

## 🎯 What is FlyShelf?

**FlyShelf** is an ultra-premium, zero-friction clipboard synchronization and productivity platform designed for developers, power users, and creators. It unifies your clipboard history, local files, code snippets, and screenshots across **all your Windows PCs and Android devices** in real-time. 

Unlike generic clipboard managers, FlyShelf works entirely peer-to-peer where possible, offering a hybrid syncing model utilizing direct **LAN connections**, secure **Cloudflare Tunnels**, and low-latency **Firebase Realtime Database** synchronization. No accounts to create, no ads, no spyware, and absolutely no limits.

---

## ✨ Features that Define FlyShelf

### 📋 Seamless Universal Clipboard Sync
- **Instantly Synchronize**: Copy text, hyperlinks, rich code blocks, images, or raw files on any device and watch them appear globally in milliseconds.
- **Dynamic Connection Engine**: Autodetects network topologies to route data over the fastest path—LAN (instant, 100+ Mbps) or secure cloud relays.
- **Intelligent Deduplication**: Smart memory caching prevents double clipboard writes and maintains chronological order.

### 📱 Android Floating Ball & Background Service
- **Persistent Overlay**: Access your entire workspace history anywhere on Android with a premium, physics-enabled floating overlay.
- **Paste with a Single Tap**: Copy historical clips back into any active text input without shifting focus.
- **Persistent Operations**: Backed by a secure, power-efficient native Kotlin **Foreground Service** that survives Android's aggressive background app termination and screen lock.

### 🖥️ Premium Desktop Power Hub (WPF / .NET 10)
- **Summon Dashboard**: A global keyboard shortcut (`Win + Shift + V` or `Alt + C`) reveals a glassmorphic dashboard featuring deep Mica blur effects.
- **Interactive Action Pills**: Action buttons reveal themselves on hover with micro-animations and drop-shadows.
- **Premium Chevron Control**: Sleek, high-contrast, circular glassmorphic expand/collapse buttons point down when collapsed (blue) and up when expanded (red) for optimal text space utilization.
- **Search & Filter**: Find files, code structures, or links with high-speed keyword queries.

### 🛠️ Advanced Productivity Powerhouses
- **📦 Bulk PDF Merging**: Select multiple PDFs, documents, or images on your shelf and merge them into a single high-quality PDF with a single click.
- **🔍 Contextual Utilities**: Right-click any card to trigger contextual actions:
  - **Code Preview**: Launch terminal runners or open scripts directly in VS Code.
  - **Google Search**: Proactively query highlighted clipboard clips.
  - **Text-to-PDF**: Instantly render raw text files into professional documents.
  - **Gemini AI OCR & Tables**: Extract clean markdown tables from images using Gemini Pro Vision APIs.

### ⏱️ Glassmorphic Timer Dashboard
- **Speed Commands**: Type `/5`, `timer 30 min`, or `2:30` directly into the search bar to launch a timer.
- **Visual Feedback**: A beautiful progress ring glows dynamically and transitions color (green/blue ➔ amber ➔ red) as time runs out.

### 🔄 Integrated One-Click Updates
- Pre-configured check-for-updates system that safely pulls, decompresses, and reinstalls the latest binaries directly from GitHub Releases.

---

## 🏗️ Technical Architecture & Routing

```
┌──────────────────┐                        ┌──────────────────┐
│   Windows PC     │   ◄── Firebase ──►     │   Android Phone  │
│   FlyShelf.exe   │       (signaling)      │   FlyShelf.apk   │
│                  │                        │                  │
│  • Clipboard     │   ◄── Cloudflare ──►   │  • Floating Ball │
│  • File Server   │      (file transfer)   │  • Background    │
│  • Hub Dashboard │                        │    Sync Service  │
│  • Timer         │   ◄── LAN Direct ──►   │  • Settings      │
│  • PDF Tools     │      (same WiFi)       │                  │
└──────────────────┘                        └──────────────────┘
```

FlyShelf implements a robust three-tier transport system:
1. **Direct LAN (Peer-to-Peer)**: Transfers heavy files and large images locally over WiFi at maximum network speed.
2. **Cloudflare Tunnel**: Bypasses firewalls and NATs securely, allowing remote file sharing between home and office.
3. **Firebase RTDB**: Low-overhead signaling channel to relay short text clipboard items securely.

---

## 📦 Project Structure

```
FlyShelf/
├── FlyShelf_PC/                 # Windows desktop app (C# / WPF / .NET 10)
│   ├── Classes/                 # Networking, updates, Daemons, and file transport logic
│   ├── Windows/                 # Mica-enabled XAML UI views (Hub, Timer, Toast)
│   ├── ViewModels/              # Core MVVM binders and drag-drop structures
│   ├── Resources/               # Web client components, icons, and themes
│   ├── Scripts/                 # Native deployment scripts
│   └── FlyShelf.csproj          # Performance-tuned build configuration
│
├── FlyShelf_Android/            # Android companion (React Native + Kotlin)
│   ├── app/(tabs)/              # React Native view screens & feeds
│   ├── android/app/src/main/    # Native Android code, foreground Service, Overlay
│   ├── context/                 # System configuration persistence
│   └── package.json             # Mobile app dependencies
│
├── flyshelf-web/                # Next.js website (GitHub Pages)
│   ├── app/                     # Pages: home, features, download, privacy
│   └── deploy.ps1               # Deployment script for gh-pages
│
├── PRIVACY_POLICY.md            # Full privacy policy
├── version.json                 # Auto-update version metadata
└── README.md
```

---

## 🚀 Installation & Getting Started — Clipboard Manager for PC & Mobile Free Download

### Pre-built Standalone Binaries
Ensure a swift start by downloading the latest stable release binaries directly:
- **Desktop (Windows 10/11)**: [Download FlyShelf.exe](https://github.com/shdra06/FlyShelf/releases/download/v7.5.0/FlyShelf.exe) (Single-file, self-contained release, no .NET install required).
- **Mobile (Android 8.0+)**: [Download FlyShelf_Mobile.apk](https://github.com/shdra06/FlyShelf/releases/download/v7.1.0/FlyShelf_Mobile.apk) (Lightweight, single-architecture arm64 release).

---

### Building from Source

#### Windows Client (WPF)
Launch standard compilations using the pre-configured automation scripts:
```powershell
cd FlyShelf_PC
# Compiles an optimized, self-contained single executable inside the 'FINAL' directory
.\Build_PC.bat
```

#### Android Client (Expo / Native Kotlin)
Run a custom native device compilation:
```powershell
cd FlyShelf_Android
# Cleans workspace, configures SDK paths, and compiles arm64-only APK natively
.\Build_Android_Device.bat
```

---

## ⚙️ Configuration & Integration

Configure the companion apps within their respective Settings dashboards:
- **Universal Clipboard Sync**: Toggling "Global Sync" activates realtime cross-device sync.
- **P2P File Transfers**: Enter your desktop's local IP address on Android's Settings panel to route large file transfers instantly over LAN WiFi.
- **AI Acceleration**: Input a Google Gemini API Key in the Desktop Client to activate Gemini-powered OCR and structural markdown table extraction.

---

## 🛡️ Security & Privacy First

FlyShelf is designed with a **privacy-first architecture**:

- **No Cloud Data Retained**: Clipboard content is **never** stored in any cloud service. All sync is peer-to-peer.
- **Zero Telemetry**: No analytics, no tracking, no usage data collection whatsoever.
- **End-to-End Encryption**: All data in transit is encrypted using **AES-256-GCM** with PBKDF2-SHA256 derived keys.
- **Local-Only Storage**: All clipboard history is stored as JSON files in `%AppData%\FlyShelf\` on your machine.
- **No Account Required**: Anonymous Firebase auth for signaling only — no email, no password.

📄 **[Read the full Privacy Policy →](PRIVACY_POLICY.md)**  
🌐 **[View Privacy Policy on our website →](https://shdra06.github.io/FlyShelf/privacy)**

---

## 🗑️ Uninstall & Data Deletion

FlyShelf stores all data locally in `%AppData%\FlyShelf\`. You have complete control over your data.

### In-App Uninstall
Open the Hub dashboard → navigate to the **Logs** tab → scroll to the **Danger Zone** section → click **"Uninstall FlyShelf & Remove All Data"**. This will:
- Delete all clipboard history, images, and synced files
- Remove all settings, paired devices, and certificates
- Remove the auto-start registry entry
- Close the application

### Manual Removal
```powershell
# Delete all FlyShelf data
Remove-Item -Recurse -Force "$env:APPDATA\FlyShelf"

# Remove auto-start (if enabled)
Remove-ItemProperty -Path "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run" -Name "FlyShelf" -ErrorAction SilentlyContinue

# Delete the executable
Remove-Item "path\to\FlyShelf.exe"
```

---

<div align="center">

Built with ❤️ using **C# WPF (.NET 10)**, **React Native**, and **Kotlin**.

**Copy once. Paste everywhere.**

</div>
