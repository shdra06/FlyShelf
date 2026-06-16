# FlyShelf — Privacy Policy

**Effective Date**: May 22, 2026  
**Developer**: Shivendra  
**App**: FlyShelf — Seamless Cross-Device Clipboard

---

## Overview

FlyShelf is a productivity tool that syncs your clipboard, files, and screenshots across your devices in real-time. This privacy policy explains what data the app accesses, how it is used, and your control over it.

## Data We Access

### Clipboard Data
- FlyShelf reads your system clipboard (text, images, files) **locally on your device** to provide clipboard management features.
- Clipboard data is stored **only on your device** in `%AppData%/FlyShelf/`.
- Clipboard data is **never sent to external servers** unless you explicitly choose to sync it to your own paired devices.

### Device-to-Device Sync
- When you enable cross-device sync, clipboard content is transmitted **directly between your paired devices** using:
  - **Local Area Network (LAN)**: Direct peer-to-peer transfer on your local network.
  - **Cloudflare Quick Tunnel**: End-to-end encrypted tunnel for remote device sync. No data is stored on Cloudflare servers.
- **Firebase Realtime Database**: Used **only** for device discovery (finding your other devices). The database stores:
  - Device name
  - Device type (PC/Mobile)
  - Local IP address
  - Connection status (online/offline)
  - **No clipboard content is ever stored in Firebase.**

### Firebase Authentication
- FlyShelf uses **anonymous authentication** via Firebase. No personal information (email, name, phone number) is collected or required.

### Gemini API (Optional)
- If you provide your own Google Gemini API key in Settings, it is used **only** for AI-powered table extraction from images (OCR).
- Your API key is encrypted using Windows DPAPI and stored locally. It is never transmitted to any server other than Google's Gemini API endpoint.

### Local Storage
- All app data (settings, pinned items, clipboard history) is stored locally in `%AppData%/FlyShelf/`.
- Log files are stored in `%AppData%/FlyShelf/Logs/` for troubleshooting purposes and are never transmitted automatically.

## Data We Do NOT Collect

- ❌ No personal information (name, email, phone, address)
- ❌ No telemetry or analytics
- ❌ No usage tracking
- ❌ No advertising identifiers
- ❌ No crash reports sent to external services
- ❌ No third-party analytics SDKs
- ❌ No data sold to third parties

## Network Activity

FlyShelf communicates over the network **only** for the following purposes:
1. **Device Discovery**: Firebase Realtime Database (anonymous, device metadata only)
2. **Clipboard Sync**: Direct peer-to-peer transfers (LAN or Cloudflare tunnel)
3. **Update Checks**: GitHub API to check for new versions (Microsoft Store version uses Store updates instead)
4. **Gemini API**: Only when user explicitly triggers AI table extraction with their own API key

## Your Control

- You can disable all network features by turning off "Cloud Sync" in Settings.
- You can clear all clipboard history at any time from the Hub window.
- Pinned items can be unpinned and deleted individually.
- Log files can be viewed and deleted from `%AppData%/FlyShelf/Logs/`.
- Auto-cleanup removes old items based on your configured retention period.

## Children's Privacy

FlyShelf does not knowingly collect any personal information from children under 13.

## Changes to This Policy

We may update this privacy policy from time to time. Changes will be posted at this URL and the effective date will be updated.

## Contact

For questions about this privacy policy, contact:  
**GitHub**: [github.com/shdra06/FlyShelf](https://github.com/shdra06/FlyShelf)
