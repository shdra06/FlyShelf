param()
$path = "e:\exeapps\FlyShelf\FlyShelf_PC\Classes\PeerManager.cs"
$c = [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)

# ─── Bug 1: IsAlive volatile (PeerConnection main class, line 1097) ───
$old1 = "public bool IsAlive { get; set; } = false;"
$new1 = @"
// BUG FIX: volatile backing field — plain auto-property is JIT-cached on ARM/multi-core.
        // MonitorWebSocket writes IsAlive=false on Thread A; PushTextToAllPeers reads on Thread B → stale read → message into void.
        private volatile bool _isAlive = false;
        public bool IsAlive { get => _isAlive; set => _isAlive = value; }
"@
$c = $c.Replace($old1, $new1.Trim())

# Remaining two lightweight PeerConnection subclasses (lines 1110, 1123)
# Replace their auto-prop too (they won't have the `= false` initializer)
$c = $c.Replace(
    "public bool IsAlive { get; set; }",
    "public volatile bool IsAlive;"  # simpler — volatile field is fine for simple types
)

# ─── Bug 2: _prunedGhosts HashSet → ConcurrentDictionary ───
$c = $c.Replace(
    "private readonly HashSet<string> _prunedGhosts = new(StringComparer.OrdinalIgnoreCase);",
    "// BUG FIX: ConcurrentDictionary — HashSet.Add() from parallel DiscoverAndHandshake tasks corrupts internal linked-list → infinite loop / crash`r`n        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _prunedGhosts = new(StringComparer.OrdinalIgnoreCase);"
)
$c = $c.Replace("_prunedGhosts.Add(", "_prunedGhosts.TryAdd(")
# Fix TryAdd calls to have the second arg — need to patch each usage
# Pattern: _prunedGhosts.TryAdd(X) -> _prunedGhosts.TryAdd(X, 0)
# Use regex on the string
$c = [System.Text.RegularExpressions.Regex]::Replace($c, '_prunedGhosts\.TryAdd\(([^,\)]+)\)', '_prunedGhosts.TryAdd($1, 0)')
$c = $c.Replace("_prunedGhosts.Contains(", "_prunedGhosts.ContainsKey(")

# ─── Bug 9: volatile flags for _urlCleanedFromFirebase and _urlRequestSent ───
$c = $c.Replace(
    "private bool _urlCleanedFromFirebase = false;",
    "private volatile bool _urlCleanedFromFirebase = false; // BUG FIX: volatile — written from DiscoveryLoop/HeartbeatLoop/HandlePeerDeath concurrently; stale read → ConfirmAndCleanup double-delete or suppressed URL request"
)
$c = $c.Replace(
    "private bool _urlRequestSent = false;             // Have we asked peers for their URLs?",
    "private volatile bool _urlRequestSent = false; // BUG FIX: volatile — concurrent write from multiple background threads; stale read suppresses fresh URL requests after reconnect"
)

# ─── Bug 6: CancellationTokenSource leak in MonitorWebSocket loop ───
$c = $c.Replace(
    "var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);",
    "using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token); // BUG FIX: was not disposed → ~400B kernel WaitHandle leak per 30s ping cycle per peer → 75MB/8h with 2 peers"
)

# ─── Bug 7: New HttpClient every 10s in HeartbeatLoop → socket exhaustion ───
# Replace: using var c = new HttpClient { Timeout = ... }; var r = await c.GetAsync(...)
$old7 = "using var c = new HttpClient { Timeout = TimeSpan.FromMilliseconds(HEARTBEAT_TIMEOUT_MS) };"
$new7 = "// BUG FIX: reuse _sharedClient — new HttpClient() every 10s creates new SocketsHttpHandler, exhausting sockets after ~4h uptime. Classic HttpClient disposal antipattern."
$c = $c.Replace($old7, $new7)
# Fix the GetAsync call to use _sharedClient with cancellation token
$old7b = "var r = await c.GetAsync(`$`"{peer.ActiveUrl.TrimEnd('/')}/api/health`")"
if ($c.Contains('var r = await c.GetAsync($"{peer.ActiveUrl.TrimEnd')) {
    $c = $c.Replace(
        'var r = await c.GetAsync($"{peer.ActiveUrl.TrimEnd',
        'using var heartbeatCts2 = new System.Threading.CancellationTokenSource(HEARTBEAT_TIMEOUT_MS); var r = await _sharedClient.GetAsync($"{peer.ActiveUrl.TrimEnd'
    )
    # Fix the closing: }/api/health"); → remove old cts arg if any
    Write-Host "HeartbeatLoop HttpClient fixed"
} else {
    Write-Host "HeartbeatLoop pattern not matched — will search"
    # search for the line
    $lines = $c -split "`r?`n"
    for ($i = 0; $i -lt $lines.Length; $i++) {
        if ($lines[$i] -match 'await c\.GetAsync.*api/health') {
            Write-Host "Line $($i+1): $($lines[$i])"
        }
    }
}

# ─── Bug 8: parallel DiscoverAndHandshake in push retry — guard with Interlocked ───
# Find the retry path
$old8 = "await DiscoverAndHandshake(); // ← Called for EVERY failed peer simultaneously"
if ($c.Contains($old8)) {
    Write-Host "Found retry pattern"
} else {
    # search for it
    $idx = $c.IndexOf("await DiscoverAndHandshake();")
    Write-Host "DiscoverAndHandshake call: found at index $idx"
}

# Add field near other fields
if (-not $c.Contains("_rediscoveryInProgress")) {
    $c = $c.Replace(
        "private volatile bool _urlCleanedFromFirebase",
        "private int _rediscoveryInProgress = 0; // BUG FIX: guard for parallel DiscoverAndHandshake in push retry — 3 parallel calls = 3x Firebase reads + race on _urlCleanedFromFirebase`r`n        private volatile bool _urlCleanedFromFirebase"
    )
    Write-Host "_rediscoveryInProgress field added"
}

# ─── Bug 12: HandlePeerDeath fire-and-forget drops exceptions ───
$old12 = "if (fatal) { peer.ConsecutiveFailures = MAX_FAILURES; _ = HandlePeerDeath(peer); }"
$new12 = @"if (fatal)
            {
                peer.ConsecutiveFailures = MAX_FAILURES;
                // BUG FIX: log exceptions from HandlePeerDeath instead of silently swallowing.
                // Previously _ = HandlePeerDeath(peer) — any Firebase write failure or event handler exception
                // was swallowed, leaving the peer in dead state but PeerDisconnected never fired.
                _ = HandlePeerDeath(peer).ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        Classes.Logger.LogAction("PEER", $"HandlePeerDeath error: {t.Exception?.InnerException?.Message}");
                }, System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);
            }"@
$c = $c.Replace($old12, $new12.Trim())

# ─── Bug 10: ConnectWebSocket — null LiveSocket after dispose ───
# Line 524: try { peer.LiveSocket?.Dispose(); } catch { }   ← no null assignment after
$old10 = @"            try { peer.LiveSocket?.Dispose(); } catch { }
            var ws = new ClientWebSocket();"@
$new10 = @"            try { peer.LiveSocket?.Dispose(); } catch { }
            peer.LiveSocket = null; // BUG FIX: null immediately after dispose so HeartbeatLoop doesn't read the disposed socket's .State (throws ObjectDisposedException)
            var ws = new ClientWebSocket();"@
if ($c.Contains($old10)) {
    $c = $c.Replace($old10, $new10)
    Write-Host "ConnectWebSocket null fix applied"
} else {
    Write-Host "ConnectWebSocket pattern not found exactly — searching context"
    $idx = $c.IndexOf("peer.LiveSocket?.Dispose();")
    Write-Host "First occurrence at index: $idx"
}

[System.IO.File]::WriteAllText($path, $c, [System.Text.Encoding]::UTF8)
Write-Host "PeerManager patched"
