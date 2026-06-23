Below is a technically complete, end-to-end walkthrough of the FlyShelf clipboard data lifecycle, with specific implementation details drawn directly from the source code.

---

## 1. Clipboard Capture & Activation

### How FlyShelf Hooks the Clipboard
FlyShelf uses the modern Windows API `AddClipboardFormatListener(IntPtr hwnd)` (`MainWindow.xaml.cs:296`) instead of the legacy `SetClipboardViewer` chain. This registers the WPF window to receive `WM_CLIPBOARDUPDATE` (0x031D) whenever the clipboard changes.

In `MainWindow.WndProc.cs:203`, the `HwndHook` catches `WM_CLIPBOARDUPDATE` and immediately defers processing to a background thread to avoid blocking the message pump. It uses a token-based coalescing mechanism (`_clipboardUpdateToken`) with a 100ms debounce to drop Windows' double-fire events.

### Preventing the Infinite Re-capture Loop
Before FlyShelf writes to the clipboard itself, it calls `FlyShelf.MainWindow.SetWritingClipboard(true)` (`MainWindow.Lifecycle.cs:772`). The WndProc handler checks `_isWritingClipboard` at line 206: if true, the event is ignored entirely. `ClipboardHelper.cs` wraps all clipboard writes (`SafeSetText`, `SafeSetImage`, `SafeSetDataObject`) with this guard, followed by `ReleaseEchoGuardWithDelay(500)` — a 500ms asynchronous delay before the flag is cleared, ensuring any subsequent Windows echo is swallowed.

### Shake-to-Open Mouse Gesture Detection
Implemented in `App.xaml.cs:683` as a store-compliant background timer (no low-level hooks):
- **Polling rate:** Adaptive — 40ms (25fps) when mouse is active, throttling to 150ms after 30 seconds of idle (`SHAKE_IDLE_THRESHOLD_MS = 30_000`).
- **Trigger:** Left mouse button must be held down.
- **Motion detected:** Directional reversals using the dot product of delta vectors. If the dot product of consecutive movement vectors is < 0, the angle between them is > 90° and counts as a reversal.
- **Displacement threshold:** Minimum squared distance of 16 pixels (4px Euclidean) per segment.
- **False-positive constraints:**
  - Strictly vertical movements (ΔY ≥ 5.67×ΔX) are ignored.
  - Strictly horizontal movements (ΔY ≤ 0.123×ΔX) are ignored.
  - Net Y-axis drift must not exceed 500px from the shake start point.
  - Fullscreen foreground apps suppress the gesture entirely (`IsForegroundFullScreen`).
  - A 900ms inactivity timer resets the count to zero.
  - 4 reversals are required to trigger the clipboard launch.

---

## 2. Content Classification & Smart Detection

### Classification Pipeline for Text (`FlyShelfViewModel.DropHandler.cs:554`)
1. **URL detection:** `Uri.TryCreate` with `UriKind.Absolute` — if the scheme is `http` or `https`, it's classified as `ClipboardItemType.Url`.
2. **File path fallback:** If the text looks like a file path with known extensions (`.md`, `.txt`, `.doc`, `.docx`), it becomes a `Document` type.
3. **Code detection:** `IsProperCode()` (`DropHandler.cs:987`) is called:
   - **Strong entry points:** `int main`, `void main`, `public static void main`, `using namespace std`, `#include <`, `Console.WriteLine`, `System.out.println`, `console.log(` — if any match, it's immediately code.
   - **Regex `_rxFunction`:** `\b(void|int|string|double|float|bool|var|let|const)?\s*\w+\s*\([^)]*\)\s*({|;|=>)`
   - **Structural check:** For 1–2 lines, requires high-confidence syntax (`;`, `{`, `}`, `=>`, `//`, `/*`, `*/`, or balanced parentheses).
   - **Multi-line density:** Requires ≥35% of non-empty lines to match the massive `_rxCode` compiled regex (`FlyShelfViewModel.cs:338`), and the text must contain absolute indicators (`;`, `{`, `}`, `=>`, `def `, `import `, `#include`, etc.).
4. **Language disambiguation** (if `isCode` is true):
   - **C++:** `std::`, `<iostream>`, `<vector>`, `using namespace`, or `_rxCpp` (`cout|cin|endl|cerr\s*<<`)
   - **C:** `<stdio.h>`, `<stdlib.h>`, `<string.h>`, or `_rxC`
   - **Python:** `def `, `import `, `self.`, `__init__`, or `_rxPython`
   - **Java:** `public static void main`, `System.out`, `import java.`, or `_rxJava`
   - **JavaScript:** `function`, `console.log`, `require(`, `module.exports`, `=>`, or `_rxJs`
   - **C#:** `using System`, `var ... = new`, `async Task`, or `_rxCs`
   - **SQL:** `SELECT ... FROM`, `INSERT INTO`, `CREATE TABLE`, or `_rxSql`
   - **HTML:** `<html`, `<div`, `<span`, `<script`, etc., or `_rxHtml`
   - **JSON:** `TrimStart().StartsWith("{\"")` or `[{"`
   - **Fallback:** `Extension = "CODE"`
5. **Fallback classification:** If none of the above, the item is `ClipboardItemType.Text`.

### Deduplication
When the same text is copied again, `DeduplicateAndInsert()` (`FlyShelfViewModel.Persistence.cs:802`) scans only the **first 10 items** in the `DroppedItems` collection. `IsDuplicate()` compares:
- `RawContent` for text/code/URL items (exact string match).
- `FilePath` (case-insensitive) for file-based items.
- `FileName` (case-insensitive) as a fallback, but screenshots starting with "Screenshot" are excluded to prevent false positives.

Additionally, `InsertWithDedup()` at position 0 skips back-to-back exact duplicates at the top of the list to prevent rapid-fire re-entries.

---

## 3. Smart Actions

After classification, `ClipboardItem.EvaluateSmartActions()` runs. The following smart action types exist (at least 6):
1. **OpenPDF** — `SmartActionName = "Open PDF"`, `SmartActionIcon = "Eye24"`, fires for PDF items.
2. **ConvertToPdf** — `SmartActionName = "Convert to PDF"`, `SmartActionIcon = "DocumentPdf24"`, for `.DOCX`, `.DOC`, `.TXT`, `.MD` documents.
3. **JoinMeeting** — `SmartActionName = "Join Meeting"`, `SmartActionIcon = "Video24"`, triggered when URL contains `zoom.us/j/` or `meet.google.com/`.
4. **OpenBrowser** — `SmartActionName = "Navigate QR Link"` or `"Open QR Link"`, for URLs and QR codes starting with `http`.
5. **CopyQRText** — For QR codes containing non-URL text.
6. **CompileAndRun** — `SmartActionName = "Run C/C++"`, `SmartActionIcon = "Play24"`, triggered by `_rxCppCheck` (`#include <...>` or `int main(`).
7. **SetTimer** — Triggered by `_rxTimeCheck` (HH:MM), `_rxDurationCheck` (`30 sec`, `5 min`), or `_rxSlashTimerCheck` (`/123`).
8. **OpenMap** — Triggered by `_rxAddressCheck` (`123 Main St`, `Avenue`, `Boulevard`, etc.).

### Color Detection System
`ColorHelper.TryDetectColor()` (`ColorHelper.cs:26`) recognizes:
- **Hex:** `#RGB`, `#RRGGBB`, `#RRGGBBAA` (3, 6, or 8 characters). The 8-char variant skips the alpha channel.
- **RGB / RGBA:** `rgb(255, 128, 0)` or `rgba(255, 128, 0, 0.5)`
- **HSL / HSLA:** `hsl(120, 50%, 50%)` or `hsla(120, 50%, 50%, 0.5)`

The detected color is stored as `DetectedColor` (hex string) and exposed as a `SolidColorBrush` via `DetectedColorBrush`.

### URL Tracking Parameter Detection
When a URL is classified, `DropHandler.cs:614` applies `_rxUtmClean`:
```regex
(?<=&|\?)(utm_source|utm_medium|utm_campaign|utm_term|utm_content|gclid|fbclid|_gl|msclkid|mc_eid|ig_shid)=[^&]*&?
```
All matched parameters are stripped, and trailing `?` or `&` are trimmed. The cleaned URL is stored as `RawContent`.

---

## 4. Persistence

### Exact Mechanism
`ClipboardHistoryManager` uses a **dual-layer persistence** strategy:
- **Snapshot file:** `%AppData%\FlyShelf\clipboard_history.json` — the compacted, full-database JSON.
- **Journal file:** `%AppData%\FlyShelf\clipboard_journal.jsonl` — append-only JSON Lines for fast, non-blocking writes.

### Debounce Delay
Full compaction (snapshot rewrite) is debounced at **1500ms** (`ClipboardHistoryManager.cs:295`). The `SaveHistoryDebounced` method uses a generation counter (`_saveGeneration`) so only the latest generation survives.

### Atomic Write Pattern (Step-by-Step)
`CompactNow()` (`ClipboardHistoryManager.cs:305`) performs:
1. **Safety check:** Refuses to overwrite if history hasn't fully loaded or if the new item count is <50% of the previously cached maximum (prevents stale snapshots from overwriting a larger database).
2. **Disk space check:** `DiskSpaceHelper.HasSufficientDiskSpace` verifies room before writing.
3. **Write to temp:** Serializes to `clipboard_history.json.tmp`.
4. **Backup:** Copies existing `clipboard_history.json` to `.bak`.
5. **Atomic rename:** `File.Move(tempPath, _historyPath, true)`.
6. **Journal clear:** Deletes `clipboard_journal.jsonl` and resets `_journalEntryCount` to 0.

### Journal-Based Approach
- `AppendToJournal()` writes a single `JournalEntry` line (`{"action":"add",...}`) asynchronously via `File.AppendAllText`.
- Actions: `"add"`, `"delete"`, `"clear"`.
- Auto-compaction triggers when the journal reaches **100 entries** (`COMPACTION_THRESHOLD`).
- A scheduled compaction also fires 5 seconds after the threshold is hit.

### Pinned Items vs. Clipboard History
- **Clipboard history** lives in `clipboard_history.json` + `clipboard_journal.jsonl`.
- **Pinned items** are stored separately in `%AppData%\FlyShelf\pinned_items.json` (`GetDbPath()` in `FlyShelfViewModel.Persistence.cs:252`).

### Retention Limits
- **Free users:** `MAX_HISTORY_ITEMS = 500` (`LicenseManager.cs:130`)
- **Pro users:** `MAX_HISTORY_ITEMS = 2500` (`LicenseManager.cs:131`)
- Free users get a warning at 150 items; Pro warnings start at 2000.
- Auto-cleanup runs every 6 hours and removes unpinned items older than `ClipboardRetentionDays` (default 7 days for free, configurable up to 30/0 for Pro).

---

## 5. Local Network Sync

### Embedded HTTP Server Port
The public-facing port is **8999**. If admin/URL ACL binding fails, an internal port of **18999** (`publicPort + 10000`) is used with a TCP proxy.

### Cascading Fallback Binding Strategy (`NetworkSyncServer.Lifecycle.cs:67`)
1. `http://+:8999/` — all interfaces, no proxy needed.
2. `http://*:8999/` — wildcard fallback.
3. `http://{localIp}:8999/` + `http://localhost:8999/` — dual bind.
4. `http://localhost:18999/` — localhost-only (no admin).
5. `http://127.0.0.1:18999/` — final localhost fallback.
If all fail, the server throws a fatal bind error.

### TCP Proxy Fallback & Host Header Rewriting
When the HttpListener is bound only to localhost, a `TcpListener` on `0.0.0.0:8999` accepts external LAN connections. In `ProxyConnection()` (`Lifecycle.cs:374`):
1. Reads up to 16KB of the HTTP headers from the client.
2. Uses a regex `(?i)Host:\s*[^\r\n]+` to find the original `Host` header.
3. Rewrites it to `Host: localhost:{targetPort}` (e.g., `localhost:18999`).
4. Sends the rewritten headers to the internal HttpListener.
5. Relays the remaining body bytes and then runs a bidirectional `CopyToAsync` stream relay with a 5-minute timeout.

### Authentication Mechanisms (`NetworkSyncServer.Handlers.Routing.cs`)
- **Primary:** `X-Pairing-Key` header or query parameter. The server checks `DevicePairingManager.IsDevicePaired(pairingKey)` — this is a cryptographic pairing key, not a simple PIN.
- **Secondary:** `Authorization: Bearer {pin}` or `?pin=` query parameter, matched against `SettingsManager.Current.WebClientPinToken` using `CryptographicOperations.FixedTimeEquals()` to prevent timing attacks.
- **Rate limiting:** Trusted paired peers get 2000 req/min; untrusted external clients get 30 writes/min and 60 reads/min.
- **Device blocking:** Recently unpaired devices are rejected via `DevicePairingManager.IsDeviceBlocked(deviceId)`.

### Sensitive Data Storage on PC
- The **sync PIN** (`WebClientPinToken`) and **pairing key** are stored in `SettingsManager` (which serializes to `%AppData%\FlyShelf\settings.json`).
- When pushed to Firebase, URLs are encrypted with `SyncCrypto.Encrypt()` before being written to `active_devices/{pairingKey}`. The pairing key itself is never transmitted to Firebase in plaintext.

---

## 6. Cross-Device Sync Architecture

### P2P Sync Architecture
Content does **not** travel through Firebase. Instead:
1. **Direct LAN:** `PeerManager` maintains WebSocket connections (`/ws/peer`) to paired peers on the same subnet. Text is pushed as `SyncText` JSON envelopes; files are pushed via `SyncFileStart` binary frames over the same WebSocket.
2. **Cloudflare tunnel:** If LAN is unavailable, the sender's `CloudflareDaemon` exposes `http://localhost:8999` via a `trycloudflare.com` subdomain. The receiver downloads files directly via the tunnel using the sender's public URL.
3. **LAN Transfer Engine:** A dedicated TCP zero-copy file transfer engine (`LanTransferEngine.cs`) runs on port **18998** for high-speed bulk file transfers outside the HTTP layer.

### Firebase's Role (and What It Does NOT Do)
- **Role:** Firebase Realtime Database is used **only for device discovery** and URL exchange.
  - `active_devices/{pairingKey}/{deviceId}` stores encrypted LAN/Cloudflare URLs and heartbeat timestamps.
  - `pairing_codes/{6-char-code}` stores temporary pairing handshakes (5-minute TTL).
  - `members/{pairingKey}/{uid}` tracks room membership.
- **NOT role:** Firebase is **never** used to transfer actual clipboard content or file payloads. Content is end-to-end encrypted and sent directly peer-to-peer (WebSocket over LAN or HTTP over Cloudflare tunnel).

### Sync Payload Encryption
`SyncCrypto.cs` implements **AES-256-GCM**:
- **Algorithm:** `AesGcm` (.NET 6+)
- **Key derivation:** `Rfc2898DeriveBytes.Pbkdf2` (PBKDF2-SHA256)
- **Iterations:** `100_000`
- **Salt:** `FlyShelf_v2.6.0_SyncSalt` (UTF-8 bytes)
- **Key size:** 32 bytes (256-bit)
- **Nonce:** 12 bytes, randomly generated per encryption (`RandomNumberGenerator.Fill`)
- **Tag:** 16 bytes (GCM authentication tag)
- **Format:** Base64-encoded concatenation of `nonce (12B) + ciphertext + tag (16B)`.
- **Key caching:** The derived key is cached in memory for the lifetime of the pairing session to avoid re-deriving on every sync.

### Time-Windowed Deduplication on the Sync Layer
`CloudDiscoveryManager.PushToCloudHub()` (`CloudDiscoveryManager.cs:97`) uses a 10-second deduplication window:
- A `Dictionary<string, long>` (`_recentPushTimes`) maps content fingerprints → last push timestamp.
- Fingerprint: `$"{item.ItemType}::{(RawContent ?? "").Substring(0, Math.Min(200, ...))}"`.
- If the same fingerprint was pushed **successfully** within `DEDUP_COOLDOWN_MS` (10,000ms), the push is skipped.
- Stale entries older than 60 seconds are pruned automatically.
- A separate `_recentPushSuccess` dictionary tracks whether the last push succeeded, so failed pushes can be retried without being swallowed by the dedup window.

---

## 7. Cloudflare Tunnel

### Failure Handling & Retry Mechanism (`CloudflareDaemon.cs`)
- The daemon parses `cloudflared.exe` stderr to extract the `trycloudflare.com` URL via regex.
- On process exit, `_consecutiveFailures` increments and `ScheduleRetry()` fires with exponential backoff:
  - Delays: 5s → 10s → 20s → 30s (capped at 30s).
- A `SemaphoreSlim _startLock` prevents concurrent `StartTunnelCore` calls.

### Protocol Switching (QUIC ↔ HTTP/2)
- Default: **QUIC** (UDP 7844).
- After every 2 consecutive failures, `_useHttp2` is toggled (`CloudflareDaemon.cs:140`).
- If QUIC-specific errors appear (`failed to run the datagram handler`, `control stream encountered a failure`, `no recent network activity`), the `_quicErrorCount` increments. After 5 QUIC errors, the tunnel is immediately killed and restarted with the opposite protocol.

### Health Monitoring System
- `StartHealthMonitor()` (`CloudflareDaemon.cs:404`) runs every **60 seconds**:
  1. Pings `http://localhost:{_localPort}/api/health`.
  2. If the ping fails, `_healthFailCount` increments.
  3. If `IsTunnelVerified` was true and 2 failures occur, verification is marked lost (fallback to Firebase Storage).
  4. On the 3rd failure, the tunnel is killed and restarted.
  5. Every 5th check, the **public** URL is also pinged to detect edge-side failures.
- `ForceCheckTunnelHealth()` is called immediately on wake-from-sleep to avoid waiting up to 60 seconds.

### Tunnel URL Sharing & Write Optimization
- **URL sharing:** Once the tunnel URL is received, the daemon waits up to **45 seconds** for DNS propagation (`Dns.GetHostAddressesAsync`). Only after DNS resolves is the URL published via the `GlobalUrlUpdated` event, which triggers `CloudDiscoveryManager.PushTunnelUrl()`.
- **Write optimization:** `PushTunnelUrl()` (`CloudDiscoveryManager.Devices.cs:31`) computes a fingerprint: `$"{url}|{localIp}|{isOnline}"`. If the fingerprint matches `_lastPushedTunnelUrl`, the Firebase write is **skipped entirely**. This reduces writes from ~1,440/day (once per minute) to ~2–5/day (only when the URL or IP actually changes). A 5-minute backoff is enforced if Firebase returns 429 or 402.

---

## 8. Android Reception

### How the Android Companion App Receives Synced Items
The Android app polls the PC's embedded HTTP server via several transports:
1. **LAN polling:** `GET /api/sync` on the PC's local IP (port 8999).
2. **Server-Sent Events (SSE):** `GET /api/events/stream` for instant push notifications.
3. **WebSocket:** `GET /ws/peer` for bidirectional P2P text and file streaming.
4. **Cloudflare fallback:** If the PC is on a different network, the Android app uses the `trycloudflare.com` URL from Firebase.

### Pairing Key's Role in Scoping Firebase Data
All Firebase data is scoped under the **pairing key**:
- `clipboard/{pairingKey}/...`
- `active_devices/{pairingKey}/...`
- `members/{pairingKey}/...`
- `pairing_codes/{code}`

This means Android devices can only see data from the room they have joined. Without the pairing key, Firebase security rules deny access.

### Echo Prevention (Avoiding Receiving Its Own Items Back)
- **Source ID filtering:** The Android app includes its `deviceId` in `X-Device-Id` headers. The PC's `HandlePeerWebSocket()` rejects `SyncText` and `SyncFileStart` envelopes where `sourceDeviceId == SettingsManager.Current.DeviceId`.
- **Cloud fingerprinting:** `NormalizeTextForFingerprint()` creates a deterministic fingerprint of received text. `MarkAsCloudSourced()` stores this in a 30-second sliding window (`_recentCloudContent`). If the PC tries to re-push the same content within 30 seconds, `IsCloudSourced()` returns true and the sync is skipped.
- **Image dedup:** On the PC, received image files from the cloud are checked for duplicates by comparing `FileSize + FormattedSize` within the first 10 items.

### QR Pairing Data Structure
`BuildQRPayload()` (`DevicePairingManager.cs:203`) serializes:
```json
{
  "app": "FlyShelf",
  "v": 1,
  "key": "{pairingKey}",      // 32-char hex room key
  "local": "{localUrl}",      // e.g., http://192.168.1.42:8999
  "global": "{globalUrl}",    // e.g., https://abc.trycloudflare.com
  "pin": "{pin}",             // WebClientPinToken
  "name": "{DeviceName}",     // PC node name
  "id": "{DeviceId}"          // PC device ID
}
```

### 6-Character Pairing Code Alphabet
`GenerateShortCode()` (`DevicePairingManager.CodePairing.cs:449`) uses:
```csharp
const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
```

**Excluded characters:** `I`, `O`, `0`, `1`

**Why:** These were excluded to prevent visual ambiguity when users read the code on screen or type it manually. The human eye easily confuses `I` (capital i) with `1` (one), and `O` (capital o) with `0` (zero). Removing them reduces transcription errors during manual pairing entry. Codes are generated using `RandomNumberGenerator.GetInt32()` (CSPRNG) for 6 characters.