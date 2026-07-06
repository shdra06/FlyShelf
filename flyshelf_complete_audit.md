# 🔬 FlyShelf Complete Deep Audit Report

> **Date**: July 7, 2026 | **Status**: ✅ Complete
> **Scope**: PC App (WPF/C# — 237 files, 39+ deeply analyzed) + Android App (React Native — 159 files, 30+ deeply analyzed)
> **Total Issues Found**: **65+ issues** (PC: 45, Android: 20+)

---

## 📊 Executive Summary

| Metric | PC App | Android App | Total |
|--------|--------|-------------|-------|
| Files Analyzed | 39+ | 30+ | 69+ |
| Lines of Code Read | ~15,000+ | ~12,000+ | ~27,000+ |
| Issues Found | 45 | 20+ | **65+** |
| P0 Critical | 3 | 4 | **7** |
| P1 High | 8 | 6 | **14** |
| P2 Medium | 15 | 5 | **20** |
| P3 Low | 19 | 5 | **24** |
| Test Files | 0 | 0 | **0** ⚠️ |

### Overall Assessment
The codebase is **well-engineered** with strong security (AES-256-GCM, HMAC auth, rate limiting, DPAPI), thoughtful networking (transport failover, adaptive heartbeats, connection pooling), and rich features. The main areas for improvement are:

1. **God objects** — `index.tsx` (3,682 lines), `ClipboardItem` (7 files), `DropHandler.cs` (77KB)
2. **Resource leaks** — temp files, download resumables, timer intervals
3. **Thread/closure safety** — stale closures in React, thread-unsafe singletons in C#
4. **Performance** — synchronous crypto on JS thread, ScrollView for large lists, system-wide timer resolution

---

## 🔴 P0 — CRITICAL (Fix Immediately)

These issues cause **data loss**, **memory leaks**, or **UI freezes** in production.

### Android

| ID | File | Issue | Impact |
|----|------|-------|--------|
| **I-10** | [index.tsx](file:///e:/exeapps/FlyShelf/FlyShelf_Android/app/(tabs)/index.tsx) | **Temp file cleanup path mismatch** — cleanup deletes `sync_${name}` but actual file is `sync_${timestamp}_${name}`. Temp files are **never deleted**. | Disk space leak on every upload |
| **SS-1** | [secureStorage.ts](file:///e:/exeapps/FlyShelf/FlyShelf_Android/utils/secureStorage.ts) | **Synchronous `SecureStore.getItem()`** blocks JS thread. On cold start with Android Keystore, causes **200-500ms UI freeze**. | App freeze on launch |
| **T-1** | [todo.tsx](file:///e:/exeapps/FlyShelf/FlyShelf_Android/app/(tabs)/todo.tsx) | **Todo merge is day-level, not item-level** — editing task A on phone and task B on PC, the whole day from the later device wins. **Other edits lost.** | Silent data loss |
| **T-3** | [todo.tsx](file:///e:/exeapps/FlyShelf/FlyShelf_Android/app/(tabs)/todo.tsx) | **Timer intervals stored in React state** — not cleaned up on unmount. Active timers leak. | Memory leak |

### PC

| ID | File | Issue | Impact |
|----|------|-------|--------|
| **PC-1** | [NetworkSyncServer.Advanced.cs](file:///e:/exeapps/FlyShelf/FlyShelf_PC/Classes/NetworkSyncServer.Advanced.cs) | **Chunk session leak** — `_chunkSessions` ConcurrentDictionary has no TTL. If client starts upload but never finalizes, temp directories **accumulate forever**. | Disk space leak |
| **PC-26** | [SmoothScroll.cs](file:///e:/exeapps/FlyShelf/FlyShelf_PC/Classes/SmoothScroll.cs) | **`TimeBeginPeriod(1)`** sets 1ms system-wide timer resolution. Affects ENTIRE SYSTEM. `TimeEndPeriod` may not be called on crash. | System-wide battery drain |
| **PC-28** | [SmoothScroll.cs](file:///e:/exeapps/FlyShelf/FlyShelf_PC/Classes/SmoothScroll.cs) | **Static dictionaries** (`_states`, `_ancestorCache`) grow without bound. No eviction when ScrollViewers are removed. | Unbounded memory growth |

---

## 🟠 P1 — HIGH (Fix Soon)

These cause **performance degradation**, **potential crashes**, or **security concerns**.

### Android

| ID | File | Issue | Impact |
|----|------|-------|--------|
| **I-1** | [index.tsx](file:///e:/exeapps/FlyShelf/FlyShelf_Android/app/(tabs)/index.tsx) | **God file: 3,682 lines, ~60 useState, ~30 useRef, ~15 useEffect** in a single component. Massive stale closure and memory leak risk. | Maintenance nightmare, bugs |
| **I-4** | [index.tsx](file:///e:/exeapps/FlyShelf/FlyShelf_Android/app/(tabs)/index.tsx) | **Download resumables not cancelled on unmount** — `aborted` flag prevents starting new downloads but can't cancel in-progress ones. | Memory leak, zombie downloads |
| **I-9** | [index.tsx](file:///e:/exeapps/FlyShelf/FlyShelf_Android/app/(tabs)/index.tsx) | **25MB base64 strings in memory** during chunked upload (`readAsStringAsync` with Base64). Creates ~33MB strings. | OOM crash on low-RAM devices |
| **AR-3** | [archive.tsx](file:///e:/exeapps/FlyShelf/FlyShelf_Android/app/(tabs)/archive.tsx) | **`ScrollView` with inline `map()`** instead of `FlatList` for media grid. Rendering 1000+ thumbnails. | Massive frame drops, memory |
| **SS-3** | [secureStorage.ts](file:///e:/exeapps/FlyShelf/FlyShelf_Android/utils/secureStorage.ts) | **Synchronous PBKDF2** (`pbkdf2Sync`) with 100K iterations blocks JS thread **100-300ms**. | UI thread blocking |
| **I-14** | [index.tsx](file:///e:/exeapps/FlyShelf/FlyShelf_Android/app/(tabs)/index.tsx) | **QR processing deadlock** — if `executePairing` throws, `qrProcessingRef.current` stays `true` forever. QR scanner permanently broken. | Feature deadlock |

### PC

| ID | File | Issue | Impact |
|----|------|-------|--------|
| **PC-2** | [NetworkSyncServer.Advanced.cs](file:///e:/exeapps/FlyShelf/FlyShelf_PC/Classes/NetworkSyncServer.Advanced.cs) | **50MB size check happens AFTER full file assembly** from chunks. A 500MB file is written to disk then deleted. | Wasteful I/O |
| **PC-8** | [PeerManager.cs](file:///e:/exeapps/FlyShelf/FlyShelf_PC/Classes/PeerManager.cs) | **`_cts.Dispose()` before tasks observe cancellation** — background tasks holding old token may get `ObjectDisposedException`. | Crash on restart |
| **PC-10** | [LanTransferEngine.cs](file:///e:/exeapps/FlyShelf/FlyShelf_PC/Classes/LanTransferEngine.cs) | **No backpressure on AcceptLoop** — unlimited TCP connections accepted, each spawning `Task.Run`. LAN DoS possible. | LAN DoS vulnerability |
| **PC-22** | [SyncCrypto.cs](file:///e:/exeapps/FlyShelf/FlyShelf_PC/Classes/SyncCrypto.cs) | **Fallback decryption iterates all keys** — 10 devices × 100K PBKDF2 iterations = ~1M iterations per failed decrypt. | Performance bottleneck |
| **PC-32** | [FlyShelfViewModel.DropHandler.cs](file:///e:/exeapps/FlyShelf/FlyShelf_PC/ViewModels/FlyShelfViewModel.DropHandler.cs) | **77KB single file** — handles drag-and-drop from every source. God object. | Maintenance nightmare |
| **PC-34** | [ClipboardItem.cs](file:///e:/exeapps/FlyShelf/FlyShelf_PC/ViewModels/ClipboardItem.cs) | **God object spanning 7 files** — handles text, images, files, audio, PDF, OCR, QR, and table extraction. Each should be a service. | Tight coupling |
| **PC-38** | [UpdateManager.cs](file:///e:/exeapps/FlyShelf/FlyShelf_PC/Classes/UpdateManager.cs) | **Self-update from %TEMP%** — if attacker can write to temp dir, they can replace the updater EXE. Hash check mitigates but flow is interceptable. | Security concern |
| **PC-40** | Cross-cutting | **Singleton overuse** — 6+ nullable static singletons with no DI. Testing impossible, implicit coupling everywhere. | Testability, stability |

---

## 🟡 P2 — MEDIUM (Plan for Next Sprint)

### Android

| ID | File | Issue |
|----|------|-------|
| **SC-1** | [SettingsContext.tsx](file:///e:/exeapps/FlyShelf/FlyShelf_Android/context/SettingsContext.tsx) | `updateDeviceStatus` creates array copies on every poll → unnecessary re-renders |
| **AR-5** | [archive.tsx](file:///e:/exeapps/FlyShelf/FlyShelf_Android/app/(tabs)/archive.tsx) | Inline `DeviceCard` component re-created every render → breaks reconciliation |
| **N-3** | [notes.tsx](file:///e:/exeapps/FlyShelf/FlyShelf_Android/app/(tabs)/notes.tsx) | `modifiedDatesRef.current.delete()` during fetch → edits during fetch silently lost |
| **I-6** | [index.tsx](file:///e:/exeapps/FlyShelf/FlyShelf_Android/app/(tabs)/index.tsx) | `transmitTextSecurely` reads stale `clips` closure instead of ref |
| **DH-2** | [DeviceHub.tsx](file:///e:/exeapps/FlyShelf/FlyShelf_Android/components/DeviceHub.tsx) | `DeviceCard` memo broken by inline `onRemove` prop (no `useCallback`) |

### PC

| ID | File | Issue |
|----|------|-------|
| **PC-4** | [NetworkSyncServer.Lifecycle.cs](file:///e:/exeapps/FlyShelf/FlyShelf_PC/Classes/NetworkSyncServer.Lifecycle.cs) | Server restart on network change — no grace period for in-flight requests |
| **PC-5** | [NetworkSyncServer.cs](file:///e:/exeapps/FlyShelf/FlyShelf_PC/Classes/NetworkSyncServer.cs) | SSE broadcast iterates clients synchronously — slow client delays all others |
| **PC-6** | Multiple files | Nullable static `Instance` singletons — callers get NRE if `Stop()` was called |
| **PC-9** | [PeerManager.Heartbeat.cs](file:///e:/exeapps/FlyShelf/FlyShelf_PC/Classes/PeerManager.Heartbeat.cs) | `ForceResync` resets peers non-atomically — concurrent threads see inconsistent state |
| **PC-12** | [LanTransferManager.cs](file:///e:/exeapps/FlyShelf/FlyShelf_PC/Classes/LanTransferManager.cs) | `_checkpointLock` uses sync `File.WriteAllText` — should use async I/O |
| **PC-14** | [CloudflareDaemon.cs](file:///e:/exeapps/FlyShelf/FlyShelf_PC/Classes/CloudflareDaemon.cs) | Hardcoded `TRUSTED_CF_HASH` — becomes stale when cloudflared updates |
| **PC-18** | [DevicePairingManager.cs](file:///e:/exeapps/FlyShelf/FlyShelf_PC/Classes/DevicePairingManager.cs) | `GetPairedDevices()` returns mutable list reference — callers can mutate outside lock |
| **PC-24** | [ThemeManager.cs](file:///e:/exeapps/FlyShelf/FlyShelf_PC/Classes/ThemeManager.cs) | `_instance` lazy singleton not thread-safe (no `Lazy<T>` or lock) |
| **PC-25** | [ThemeManager.cs](file:///e:/exeapps/FlyShelf/FlyShelf_PC/Classes/ThemeManager.cs) | `AvailableThemes.Clear()` + re-add in refresh — brief empty state visible to UI |
| **PC-27** | [SmoothScroll.cs](file:///e:/exeapps/FlyShelf/FlyShelf_PC/Classes/SmoothScroll.cs) | `SetProcessInformation` disables EcoQoS — aggressive for a clipboard manager |
| **PC-30** | [App.xaml.cs](file:///e:/exeapps/FlyShelf/FlyShelf_PC/App.xaml.cs) | `_isCreatingMainWindow` volatile bool — not truly thread-safe mutex |
| **PC-36** | [ClipboardHistoryManager.cs](file:///e:/exeapps/FlyShelf/FlyShelf_PC/Classes/ClipboardHistoryManager.cs) | Timer callbacks on thread pool — may overlap with UI-thread list access |
| **PC-37** | [ClipboardHistoryManager.cs](file:///e:/exeapps/FlyShelf/FlyShelf_PC/Classes/ClipboardHistoryManager.cs) | `ABSOLUTE_MAX_ITEMS = 2500` applied after journal replay — memory spikes first |
| **PC-41** | Cross-cutting | Fire-and-forget `Task.Run` without error handling — unobserved exceptions |
| **PC-45** | Cross-cutting | Thread safety of `PeerConnection` properties — reads bypass `StateLock` |

---

## 🔵 P3 — LOW (Backlog)

### Android

| ID | File | Issue |
|----|------|-------|
| **AR-1** | archive.tsx | Hardcoded filesystem scan paths (Samsung, Xiaomi paths differ) |
| **CI-2** | CachedImage.tsx | Weak multiplicative hash for image cache — collision risk |
| **EB-1** | ErrorBoundary.tsx | Safe Mode UI uses hardcoded dark theme colors |
| **OK-3** | OverlayService.kt | Programmatic UI construction (20+ views, no XML) — fragile |
| **A-1** | _layout.tsx | LogBox warning suppression may mask real Firebase issues |

### PC

| ID | File | Issue |
|----|------|-------|
| **PC-3** | NetworkSyncServer.Advanced.cs | Chunk count mismatch path doesn't call `res.Close()` |
| **PC-7** | PeerManager.cs | Static `HttpClient` never disposed on shutdown |
| **PC-11** | LanTransferEngine.cs | `TcpClient` not explicitly disposed in `HandleIncomingConnection` |
| **PC-13** | LanTransferManager.cs | `ObservableCollection` modified from background threads |
| **PC-15** | CloudflareDaemon.cs | `_cfProcess` field could be stale during process restart window |
| **PC-16** | NearbyDiscovery.cs | UDP port 8999 conflict not gracefully handled |
| **PC-17** | NearbyDiscovery.cs | Broadcast every 5s on all interfaces — potential broadcast storm |
| **PC-19** | DevicePairingManager.cs | Max device limit (10) enforced client-side only |
| **PC-21** | SyncCrypto.cs | Static salt — same pairing key → same derived key across installs |
| **PC-23** | SecureStorage.cs | `LegacyMigrationNeeded` static field — thread safety |
| **PC-29** | App.xaml.cs | Mouse shake timer (40ms P/Invoke) — minor CPU overhead |
| **PC-33** | FlyShelfViewModel.cs | `SemaphoreSlim(2, 2)` for icon decode — too conservative |
| **PC-35** | ClipboardItem.Convert.cs | 50KB conversion logic in viewmodel layer — should be a service |
| **PC-39** | AntiTamperService.cs | Debugger detection bypassable via hooks |
| **PC-42** | Cross-cutting | `Application.Current.Dispatcher.InvokeAsync` — NRE during shutdown |
| **PC-43** | Cross-cutting | Empty catch blocks with `// Best-effort` mask bugs |
| **PC-44** | Cross-cutting | No structured logging — string-based, no levels, no correlation IDs |

---

## 🔄 CROSS-PLATFORM INTEGRATION AUDIT

### Stale Closure Risk Map (Android)

| File | Function | Stale Variable | Severity |
|------|----------|----------------|----------|
| index.tsx | `transmitTextSecurely` | `clips` | Medium |
| notes.tsx | `schedulePost` | `pairingKey` | Medium |
| todo.tsx | `pushChangedDays` | `pairingKey` | Medium |
| archive.tsx | `uploadChunked` retry | `pairedDevices` | Medium |

### Memory Leak Risk Map

| Platform | File | Source | Severity |
|----------|------|--------|----------|
| Android | index.tsx | Download resumables not cancelled | **High** |
| Android | todo.tsx | Timer intervals not cleaned | **High** |
| PC | SmoothScroll.cs | Static scroll state dictionaries | **High** |
| PC | NetworkSyncServer | Chunk sessions with no TTL | **High** |
| Android | OverlayService.kt | Panel views created/destroyed per toggle | Medium |
| PC | PeerManager.cs | Static HttpClient never disposed | Low |

### Re-render Hotspots (Android)

| Component | Cause | Impact |
|-----------|-------|--------|
| index.tsx | `setClips` on every poll | Entire clip list re-renders |
| index.tsx | `setConnectionInfo` on every poll | Re-renders connection badge |
| SettingsContext | `updateDeviceStatus` array copy | All consumers re-render |
| archive.tsx | Inline DeviceCard component | Remounts on parent render |

### Sync Protocol Issues

| Area | Issue | Severity |
|------|-------|----------|
| Todo sync | Day-level LWW instead of per-item merge | **Critical** |
| Notes sync | Per-bullet merge ✅ (fixed) | Resolved |
| Clipboard sync | Stale closure may send wrong data | Medium |
| File sync | Temp files never cleaned (path mismatch) | **Critical** |
| Clock sync | ±1 second accuracy (HTTP Date header) | Low |

---

## 📁 COMPLETE FILE AUDIT MAP

### PC App — Files by Category

````carousel
### 🌐 Networking (20 files)
```
NetworkSyncServer.cs              — HTTP listener core
NetworkSyncServer.Advanced.cs     — Chunked uploads, SSE
NetworkSyncServer.FileTransfer.cs — File upload/download
NetworkSyncServer.Handlers.cs     — Route handlers
NetworkSyncServer.Handlers.Routing.cs — URL routing
NetworkSyncServer.Lifecycle.cs    — Start/stop lifecycle
PeerManager.cs                    — P2P connection core
PeerManager.Connection.cs         — TCP connections
PeerManager.Heartbeat.cs          — Keep-alive system
PeerManager.Transfer.cs           — Data push
PeerModels.cs                     — Data models
LanTransferEngine.cs              — TCP LAN transfers
LanTransferManager.cs             — Transfer orchestration
CloudDiscoveryListener.cs         — Cloud long-poll
CloudDiscoveryListener.Download.cs— Cloud downloads
CloudDiscoveryManager.cs          — Cloud sessions
CloudDiscoveryManager.Devices.cs  — Cloud presence
CloudflareDaemon.cs               — CF tunnel management
NearbyDiscovery.cs                — UDP broadcast/mDNS
NetworkClock.cs                   — NTP time sync
```
<!-- slide -->
### 🎨 UI & Theming (20+ files)
```
MainWindow.xaml                   — Main UI layout
MainWindow.xaml.cs + 12 partials  — Window logic
ThemeManager.cs                   — Theme loading/switching
ColorHelper.cs                    — Color utilities
SmoothScroll.cs                   — Physics scrolling
SmoothScrollPCApp.cs              — App integration
AnimationHelper.cs                — WPF animations
AnimationTriggerService.cs        — Animation events
ElementPositionTracker.cs         — Position tracking
GlobalScrollBar.xaml              — Scrollbar styling
Theme.Midnight.xaml               — Dark theme
Theme.Ocean.xaml                  — Blue theme
Theme.Sunset.xaml                 — Warm theme
Theme.Emerald.xaml                — Green theme
Theme.Lavender.xaml               — Purple theme
Theme.ArcticSnow.xaml             — Light theme
```
<!-- slide -->
### 🪟 Windows (20+ windows)
```
HubWindow.xaml + 7 partials       — Feature hub
TransferManagerWindow             — Transfer progress
QuickLookWindow                   — File preview
NoteExpandWindow                  — Note editor
NotesAIWindow                     — AI assistant
PdfMergeWindow                    — PDF merge
PageReorderWindow                 — Page reorder
ReminderCreateWindow              — Reminder creation
TimerWindow                       — Timer
ToastWindow                       — Notifications
EmojiPickerWindow                 — Emoji picker
NetworkLogsWindow                 — Network logs
DragPreviewWindow                 — Drag preview
AiSetupPopup                      — AI setup
+ 6 more windows
```
<!-- slide -->
### 📊 ViewModels & Features (25+ files)
```
ClipboardItem.cs + 7 partials     — Clipboard items
FlyShelfViewModel.cs + 2 partials — Main viewmodel
TransferManagerViewModel.cs       — Transfer VM
ClipboardHistoryManager.cs        — History persistence
NoteManager.cs                    — Notes CRUD
TodoManager.cs                    — Todos CRUD
ReminderManager.cs + Scheduler    — Reminders
AiProviderService.cs              — AI routing
SmartContentDetector.cs           — Content detection
DocumentSniffer.cs                — Format detection
LicenseManager.cs                 — Licensing
AntiTamperService.cs              — Anti-debug
UpdateManager.cs                  — Auto-update
SettingsManager.cs                — Settings
Logger.cs                         — Async logging
```
````

### Android App — Files by Category

````carousel
### 📱 Screens (8 files)
```
app/(tabs)/index.tsx    — Clipboard (3,682 lines!)
app/(tabs)/notes.tsx    — Notes (1,376 lines)
app/(tabs)/todo.tsx     — Todos (1,994 lines)
app/(tabs)/archive.tsx  — Files (1,194 lines)
app/(tabs)/settings.tsx — Settings (702 lines)
app/(tabs)/_layout.tsx  — Tab navigation
app/pdf-tools.tsx       — PDF tools
app/_layout.tsx         — Root layout
```
<!-- slide -->
### 🧩 Components (16 files)
```
AnimatedCard.tsx        — Animated list items
AnimatedPressable.tsx   — Press animations
AppButton.tsx           — Button component
AppCard.tsx             — Card container
BottomSheet.tsx         — Bottom sheet modal
CachedImage.tsx         — Image caching
DeviceHub.tsx           — Device panel
EmptyState.tsx          — Empty state
ErrorBoundary.tsx       — Error recovery
OnboardingWizard.tsx    — First-run wizard
ScreenHeader.tsx        — Screen header
StepSlider.tsx          — Step slider
PdfPageEditor.tsx       — PDF page editor
+ 12 PDF tool components
```
<!-- slide -->
### 🔧 Hooks & Utils (17 files)
```
usePairing.ts           — Device pairing
usePcUrlResolver.ts     — PC address resolution
useModals.ts            — Modal management
useMultiSelect.ts       — Multi-select
usePdfEditor.ts         — PDF editor
useAppTheme.ts          — Theme hook
use-color-scheme.ts     — System theme
networkHelpers.ts       — HTTP client
networkClock.ts         — Time sync
syncCrypto.ts           — AES-256-GCM
secureStorage.ts        — Keystore storage
clipTypes.ts            — Type definitions
noteTypes.ts            — Note types
debugLog.ts             — Ring buffer logger
SettingsContext.tsx      — React context
theme.ts                — Design tokens
+ 6 style files
```
````

---

## 🎯 RECOMMENDED FABLE 5 AUDIT EXECUTION ORDER

### Phase 1: 🔴 Critical Fixes (Week 1)
1. Fix temp file cleanup path mismatch (`I-10`)
2. Make SecureStore async (`SS-1`, `SS-3`)
3. Implement per-item todo merge (`T-1`)
4. Clean up timer intervals on unmount (`T-3`)
5. Add chunk session TTL (`PC-1`)
6. Fix SmoothScroll memory growth (`PC-28`)

### Phase 2: 🟠 Stability & Performance (Week 2-3)
7. Decompose `index.tsx` (3,682 lines → 5-6 hooks + sub-components)
8. Cancel download resumables on unmount (`I-4`)
9. Replace ScrollView with FlatList in archive (`AR-3`)
10. Fix QR deadlock with try/finally (`I-14`)
11. Guard `_cts.Dispose()` in PeerManager (`PC-8`)
12. Add connection backpressure to LanTransferEngine (`PC-10`)

### Phase 3: 🟡 Optimization (Week 3-4)
13. Fix stale closures (4 instances across Android)
14. Extract DeviceCard from inline to memoized component (`AR-5`)
15. Replace singletons with DI (`PC-40`)
16. Break up ClipboardItem god object (`PC-34`)
17. Add structured logging (`PC-44`)

### Phase 4: 🔵 Polish (Ongoing)
18. Theme consistency audit across all windows
19. Accessibility compliance check (WCAG)
20. Add unit tests (currently 0!)
21. Add integration tests for sync protocol

---

## ✅ THINGS DONE WELL (Strengths)

> [!TIP]
> ### Security ✅
> - AES-256-GCM with PBKDF2-SHA256 (100K iterations)
> - HMAC-authenticated LAN connections
> - DPAPI-based local storage (PC)
> - Android Keystore-backed SecureStore
> - Constant-time PIN comparison
> - Rate limiting on pairing endpoints
> - SSRF protection on device URL validation
> - Assembly integrity verification

> [!TIP]
> ### Networking ✅
> - Transport failover (LAN → Cloudflare → Cloud)
> - Adaptive heartbeats (relaxed when healthy)
> - Connection pooling with `SocketsHttpHandler`
> - ArrayPool-based large file streaming
> - Protocol versioning in TCP headers
> - Self-healing key alignment
> - Network clock with monotonic anchoring

> [!TIP]
> ### Architecture ✅
> - Per-bullet merge for notes conflict resolution
> - Journal + snapshot persistence model
> - Log rotation with atomic writes
> - Safe mode with crash recovery
> - Hardware-bound device IDs
> - Background fetch with network checks
> - Comprehensive error boundary with diagnostics
