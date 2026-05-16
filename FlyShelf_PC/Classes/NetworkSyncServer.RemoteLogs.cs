using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;

namespace AdvanceClip.Classes
{
    public partial class NetworkSyncServer
    {
        // ═══ Remote Log Streaming System ═══
        // Enables live cross-device log viewing via Cloudflare tunnel.
        // GET  /api/logs          → JSON array of recent log entries
        // GET  /api/logs/stream   → SSE (Server-Sent Events) live log stream
        // POST /api/logs          → Accept logs from paired devices
        // GET  /logs              → Live HTML dashboard with auto-refresh

        // In-memory ring buffer for remote device logs (device → logs)
        private static readonly ConcurrentDictionary<string, ConcurrentQueue<RemoteLogEntry>> _remoteDeviceLogs = new();
        private static readonly ConcurrentQueue<RemoteLogEntry> _localLogBuffer = new();
        private const int MAX_REMOTE_LOG_ENTRIES = 500;

        // SSE clients waiting for live log stream
        private static readonly ConcurrentBag<HttpListenerResponse> _sseLogClients = new();

        private struct RemoteLogEntry
        {
            public long Timestamp { get; set; }
            public string Device { get; set; }
            public string Category { get; set; }
            public string Message { get; set; }
            public string Raw { get; set; }
        }

        /// <summary>
        /// Hook into Logger to capture local logs in real-time for the remote viewer.
        /// Call this once during server startup.
        /// </summary>
        public void StartLocalLogCapture()
        {
            long lastLineCount = 0;
            // On first run, skip existing lines — only capture NEW entries
            try
            {
                string netLogPath = Logger.GetNetworkLogPath();
                if (File.Exists(netLogPath))
                    lastLineCount = File.ReadAllLines(netLogPath).Length;
            }
            catch { }

            var timer = new System.Threading.Timer(_ =>
            {
                try
                {
                    string netLogPath = Logger.GetNetworkLogPath();
                    if (!File.Exists(netLogPath)) return;

                    var lines = File.ReadAllLines(netLogPath);
                    if (lines.Length <= lastLineCount) return; // No new lines

                    // Process only NEW lines since last check
                    for (long i = lastLineCount; i < lines.Length; i++)
                    {
                        string line = lines[i];
                        if (string.IsNullOrWhiteSpace(line)) continue;
                        
                        var entry = new RemoteLogEntry
                        {
                            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            Device = Environment.MachineName,
                            Category = "LOCAL",
                            Message = line,
                            Raw = line
                        };

                        _localLogBuffer.Enqueue(entry);
                        while (_localLogBuffer.Count > MAX_REMOTE_LOG_ENTRIES)
                        { _localLogBuffer.TryDequeue(out RemoteLogEntry _discard); }

                        // Push to SSE clients
                        BroadcastLogToSSE(entry);
                    }
                    lastLineCount = lines.Length;
                }
                catch { }
            }, null, 2000, 1000); // Check every 1s for responsiveness
        }

        /// <summary>
        /// Serves JSON array of recent logs — local + all paired devices.
        /// Query params: ?device=X (filter by device), ?lines=N (limit), ?since=TS (after timestamp)
        /// </summary>
        private void ServeLogsJson(HttpListenerRequest req, HttpListenerResponse res)
        {
            try
            {
                string deviceFilter = req.QueryString["device"] ?? "";
                int lineCount = int.TryParse(req.QueryString["lines"], out int lc) ? Math.Min(lc, 500) : 200;
                long sinceTs = long.TryParse(req.QueryString["since"], out long ts) ? ts : 0;

                var allLogs = new System.Collections.Generic.List<object>();

                // Local logs from network_diagnostics.txt (real file, not buffer)
                string localDevice = Environment.MachineName;
                if (string.IsNullOrEmpty(deviceFilter) || deviceFilter.Equals(localDevice, StringComparison.OrdinalIgnoreCase) || deviceFilter == "local")
                {
                    string logContent = Logger.GetRecentNetworkLogs(lineCount);
                    foreach (string line in logContent.Split('\n'))
                    {
                        string trimmed = line.Trim();
                        if (string.IsNullOrWhiteSpace(trimmed)) continue;
                        allLogs.Add(new { device = localDevice, log = trimmed, ts = ExtractTimestamp(trimmed) });
                    }
                }

                // Remote device logs from in-memory buffer
                foreach (var kvp in _remoteDeviceLogs)
                {
                    if (!string.IsNullOrEmpty(deviceFilter) && !kvp.Key.Equals(deviceFilter, StringComparison.OrdinalIgnoreCase))
                        continue;

                    foreach (var entry in kvp.Value)
                    {
                        if (entry.Timestamp > sinceTs)
                        {
                            allLogs.Add(new { device = kvp.Key, log = entry.Raw, ts = entry.Timestamp });
                        }
                    }
                }

                // Sort by timestamp descending, limit
                var sorted = allLogs.OrderByDescending(l => ((dynamic)l).ts).Take(lineCount).ToList();

                byte[] json = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    localDevice = localDevice,
                    devices = _remoteDeviceLogs.Keys.Prepend(localDevice).Distinct().ToArray(),
                    count = sorted.Count,
                    logs = sorted
                });

                res.StatusCode = 200;
                res.ContentType = "application/json";
                res.AddHeader("Access-Control-Allow-Origin", "*");
                res.ContentLength64 = json.Length;
                res.OutputStream.Write(json, 0, json.Length);
            }
            catch (Exception ex)
            {
                byte[] err = Encoding.UTF8.GetBytes($"{{\"error\":\"{ex.Message}\"}}");
                res.StatusCode = 500;
                res.ContentType = "application/json";
                res.OutputStream.Write(err, 0, err.Length);
            }
            finally { try { res.Close(); } catch { } }
        }

        /// <summary>
        /// SSE endpoint — streams logs in real-time. Client connects and stays open.
        /// </summary>
        private async System.Threading.Tasks.Task ServeLogStream(HttpListenerRequest req, HttpListenerResponse res)
        {
            res.StatusCode = 200;
            res.ContentType = "text/event-stream";
            res.AddHeader("Cache-Control", "no-cache");
            res.AddHeader("Connection", "keep-alive");
            res.AddHeader("Access-Control-Allow-Origin", "*");

            // Send initial burst of recent logs
            string recent = Logger.GetRecentNetworkLogs(50);
            foreach (string line in recent.Split('\n'))
            {
                string trimmed = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmed)) continue;
                string sseData = $"data: {{\"device\":\"{JsonEscape(Environment.MachineName)}\",\"log\":\"{JsonEscape(trimmed)}\"}}\n\n";
                byte[] bytes = Encoding.UTF8.GetBytes(sseData);
                try { await res.OutputStream.WriteAsync(bytes, 0, bytes.Length); } catch { return; }
            }
            await res.OutputStream.FlushAsync();

            // Register this response as an SSE client
            _sseLogClients.Add(res);

            // Keep connection alive — send heartbeat every 15s
            try
            {
                while (true)
                {
                    await System.Threading.Tasks.Task.Delay(15000);
                    byte[] heartbeat = Encoding.UTF8.GetBytes(": heartbeat\n\n");
                    try { await res.OutputStream.WriteAsync(heartbeat, 0, heartbeat.Length); await res.OutputStream.FlushAsync(); }
                    catch { break; } // Client disconnected
                }
            }
            catch { }
            finally
            {
                // Remove from SSE clients (ConcurrentBag doesn't support Remove, but it's ok — we check on write)
                try { res.Close(); } catch { }
            }
        }

        /// <summary>
        /// Broadcast a log entry to all connected SSE clients.
        /// </summary>
        private static void BroadcastLogToSSE(RemoteLogEntry entry)
        {
            string sseData = $"data: {{\"device\":\"{JsonEscape(entry.Device)}\",\"log\":\"{JsonEscape(entry.Raw)}\",\"ts\":{entry.Timestamp}}}\n\n";
            byte[] bytes = Encoding.UTF8.GetBytes(sseData);

            var dead = new System.Collections.Generic.List<HttpListenerResponse>();
            foreach (var client in _sseLogClients)
            {
                try
                {
                    client.OutputStream.Write(bytes, 0, bytes.Length);
                    client.OutputStream.Flush();
                }
                catch { dead.Add(client); }
            }
            // Clean dead clients (ConcurrentBag doesn't support removal, but entries are GC'd when response closes)
        }

        /// <summary>
        /// Accept log entries from a paired remote device (POST /api/logs).
        /// Body: { "device": "LAPTOP-X", "logs": ["line1", "line2", ...] }
        /// </summary>
        private async System.Threading.Tasks.Task HandleRemoteLogPost(HttpListenerRequest req, HttpListenerResponse res)
        {
            try
            {
                using var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
                string body = await reader.ReadToEndAsync();
                var doc = JsonDocument.Parse(body);
                
                string device = doc.RootElement.TryGetProperty("device", out var dv) ? dv.GetString() ?? "Unknown" : "Unknown";
                
                if (!_remoteDeviceLogs.ContainsKey(device))
                    _remoteDeviceLogs[device] = new ConcurrentQueue<RemoteLogEntry>();

                var queue = _remoteDeviceLogs[device];

                if (doc.RootElement.TryGetProperty("logs", out var logsArr) && logsArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var logEl in logsArr.EnumerateArray())
                    {
                        string logLine = logEl.GetString() ?? "";
                        if (string.IsNullOrWhiteSpace(logLine)) continue;

                        var entry = new RemoteLogEntry
                        {
                            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            Device = device,
                            Category = "REMOTE",
                            Message = logLine,
                            Raw = logLine
                        };

                        queue.Enqueue(entry);
                        while (queue.Count > MAX_REMOTE_LOG_ENTRIES) { queue.TryDequeue(out RemoteLogEntry _d); }

                        BroadcastLogToSSE(entry);
                    }
                }

                byte[] ok = Encoding.UTF8.GetBytes($"{{\"status\":\"ok\",\"received\":{(doc.RootElement.TryGetProperty("logs", out var la) ? la.GetArrayLength() : 0)}}}");
                res.StatusCode = 200;
                res.ContentType = "application/json";
                res.OutputStream.Write(ok, 0, ok.Length);
            }
            catch (Exception ex)
            {
                byte[] err = Encoding.UTF8.GetBytes($"{{\"error\":\"{ex.Message}\"}}");
                res.StatusCode = 400;
                res.ContentType = "application/json";
                res.OutputStream.Write(err, 0, err.Length);
            }
            finally { try { res.Close(); } catch { } }
        }

        /// <summary>
        /// Serve a live HTML dashboard that shows logs from all devices with auto-refresh via SSE.
        /// </summary>
        private void ServeLogDashboard(HttpListenerResponse res)
        {
            string dn = Environment.MachineName;
            // Build HTML without C# interpolation to avoid conflicts with JS template literals
            string html = "<!DOCTYPE html><html><head><meta charset='utf-8'><title>FlyShelf Live Logs — " + dn + "</title>"
                + @"<style>
*{margin:0;padding:0;box-sizing:border-box}
body{background:#0d1117;color:#c9d1d9;font-family:'Consolas','Cascadia Code',monospace;font-size:12px}
.header{background:#161b22;padding:12px 20px;border-bottom:1px solid #30363d;display:flex;align-items:center;gap:16px;position:fixed;top:0;left:0;right:0;z-index:100}
.header h1{color:#58a6ff;font-size:16px;font-weight:600}
.header .device{background:#238636;color:#fff;padding:2px 8px;border-radius:4px;font-size:11px}
.header .status{color:#3fb950;font-size:11px}
.filters{display:flex;gap:8px;margin-left:auto}
.filters button{background:#21262d;border:1px solid #30363d;color:#8b949e;padding:4px 12px;border-radius:4px;cursor:pointer;font-size:11px}
.filters button.active{background:#1f6feb;color:#fff;border-color:#1f6feb}
.filters button:hover{border-color:#58a6ff}
#logs{padding:60px 12px 12px;overflow-y:auto;max-height:calc(100vh - 60px)}
.log-line{padding:2px 8px;border-radius:2px;white-space:pre-wrap;word-break:break-all;line-height:1.6}
.log-line:hover{background:#161b22}
.log-line .ts{color:#484f58}
.log-line .cat{font-weight:bold}
.cat-CLIPBOARD{color:#f0883e}.cat-PEER{color:#a371f7}.cat-HTTP{color:#58a6ff}.cat-PUSH{color:#3fb950}
.cat-CLOUDFLARE{color:#79c0ff}.cat-FIREBASE{color:#ffa657}.cat-DOWNLOAD{color:#d2a8ff}
.cat-SERVER{color:#ff7b72}.cat-ERROR{color:#f85149;font-weight:bold}.cat-REMOTE{color:#bc8cff;font-style:italic}
.device-tag{font-size:10px;padding:1px 6px;border-radius:3px;margin-right:6px}
.device-local{background:#1f3d2a;color:#3fb950}.device-remote{background:#2d1b4e;color:#bc8cff}
.stats{color:#484f58;font-size:11px;padding:4px 0}
#autoScroll{position:fixed;bottom:20px;right:20px;background:#1f6feb;color:#fff;border:none;padding:8px 16px;border-radius:6px;cursor:pointer;font-size:12px;z-index:100}
</style></head><body>
<div class='header'>
  <h1>📋 FlyShelf Live Logs</h1>
  <span class='device'>" + dn + @"</span>
  <span class='status' id='statusDot'>● Connected</span>
  <span class='stats' id='stats'></span>
  <div class='filters'>
    <button class='active' onclick=""setFilter('')"" id='btn-all'>All</button>
    <button onclick=""setFilter('PEER')"" id='btn-peer'>Peer</button>
    <button onclick=""setFilter('CLIPBOARD')"" id='btn-clip'>Clipboard</button>
    <button onclick=""setFilter('HTTP')"" id='btn-http'>HTTP</button>
    <button onclick=""setFilter('CLOUDFLARE')"" id='btn-cf'>Cloudflare</button>
    <button onclick=""setFilter('PUSH')"" id='btn-push'>Push</button>
    <button onclick=""setFilter('FIREBASE')"" id='btn-fb'>Firebase</button>
    <button onclick=""setFilter('ERROR')"" id='btn-err'>Errors</button>
  </div>
</div>
<div id='logs'></div>
<button id='autoScroll' onclick='toggleAutoScroll()'>⬇ Auto-Scroll: ON</button>
<script>
const DEVICE_NAME = '" + dn + @"';
let activeFilter = '';
let autoScroll = true;
let logCount = 0;
const logsDiv = document.getElementById('logs');
const MAX_LINES = 1000;

function setFilter(f) {
  activeFilter = f;
  document.querySelectorAll('.filters button').forEach(b => b.classList.remove('active'));
  if (f === '') document.getElementById('btn-all').classList.add('active');
  else { var el = document.querySelector('[onclick*=""'+f+'""]'); if(el) el.classList.add('active'); }
  document.querySelectorAll('.log-line').forEach(el => {
    el.style.display = (!f || (el.dataset.cat && el.dataset.cat.indexOf(f) >= 0)) ? '' : 'none';
  });
}

function toggleAutoScroll() {
  autoScroll = !autoScroll;
  document.getElementById('autoScroll').textContent = autoScroll ? '⬇ Auto-Scroll: ON' : '⬇ Auto-Scroll: OFF';
}

function extractCategory(line) {
  var m = line.match(/\[([A-Z_ ]+?)\]/g);
  if (m && m.length >= 2) return m[1].replace(/[\[\]]/g, '').trim();
  return 'LOG';
}

function colorLine(line, device, isRemote) {
  var cat = extractCategory(line);
  var catClass = 'cat-' + cat.split(' ')[0];
  var deviceTag = isRemote
    ? '<span class=""device-tag device-remote"">' + device + '</span>'
    : '<span class=""device-tag device-local"">' + device + '</span>';
  var colored = line.replace(/\[([\d-]+ [\d:.]+)\]/, '<span class=""ts"">[$1]</span>');
  colored = colored.replace(/\[([A-Z_ ]+?)\]/g, function(m, p) { return '<span class=""cat ' + catClass + '"">[' + p + ']</span>'; });
  var vis = (activeFilter && cat.indexOf(activeFilter) < 0) ? 'display:none' : '';
  return '<div class=""log-line"" data-cat=""' + cat + '"" style=""' + vis + '"">' + deviceTag + colored + '</div>';
}

var evtSource = new EventSource('/api/logs/stream');
evtSource.onmessage = function(e) {
  try {
    var data = JSON.parse(e.data);
    var isRemote = data.device !== DEVICE_NAME;
    logsDiv.insertAdjacentHTML('beforeend', colorLine(data.log, data.device, isRemote));
    logCount++;
    while (logsDiv.children.length > MAX_LINES) logsDiv.removeChild(logsDiv.firstChild);
    if (autoScroll) logsDiv.scrollTop = logsDiv.scrollHeight;
    document.getElementById('stats').textContent = logCount + ' entries';
  } catch(ex) {}
};
evtSource.onerror = function() {
  document.getElementById('statusDot').textContent = '○ Reconnecting...';
  document.getElementById('statusDot').style.color = '#f85149';
};
evtSource.onopen = function() {
  document.getElementById('statusDot').textContent = '● Connected';
  document.getElementById('statusDot').style.color = '#3fb950';
};

fetch('/api/logs?lines=200')
  .then(function(r) { return r.json(); })
  .then(function(data) {
    var logs = data.logs.reverse();
    logs.forEach(function(l) {
      var isRemote = l.device !== DEVICE_NAME;
      logsDiv.insertAdjacentHTML('beforeend', colorLine(l.log, l.device, isRemote));
      logCount++;
    });
    document.getElementById('stats').textContent = logCount + ' entries | Devices: ' + data.devices.join(', ');
    if (autoScroll) logsDiv.scrollTop = logsDiv.scrollHeight;
  });
</script></body></html>";

            byte[] htmlBytes = Encoding.UTF8.GetBytes(html);
            res.StatusCode = 200;
            res.ContentType = "text/html; charset=utf-8";
            res.ContentLength64 = htmlBytes.Length;
            res.OutputStream.Write(htmlBytes, 0, htmlBytes.Length);
            try { res.Close(); } catch { }
        }

        // ═══ Helper for log cross-device sync ═══

        /// <summary>
        /// Background task: periodically pushes local logs to all paired peer devices.
        /// This creates a bidirectional log mirror — each device can see all others' logs.
        /// </summary>
        public void StartRemoteLogPush()
        {
            long lastPushPosition = 0;
            var pushTimer = new System.Threading.Timer(async _ =>
            {
                try
                {
                    string netLogPath = Logger.GetNetworkLogPath();
                    if (!File.Exists(netLogPath)) return;

                    var lines = File.ReadAllLines(netLogPath);
                    if (lines.Length <= lastPushPosition) return; // No new lines

                    // Get only new lines since last push
                    var newLines = lines.Skip((int)lastPushPosition).Take(50).ToArray(); // Max 50 per push
                    lastPushPosition = lines.Length;

                    if (newLines.Length == 0) return;

                    // Push to all known peer devices
                    var peers = PeerManager.Instance?.ConnectedPeers;
                    if (peers != null)
                    {
                        foreach (var kvp in peers)
                        {
                            var peer = kvp.Value;
                            if (string.IsNullOrEmpty(peer.ActiveUrl) || !peer.IsAlive) continue;
                            try
                            {
                                var payload = JsonSerializer.Serialize(new
                                {
                                    device = Environment.MachineName,
                                    logs = newLines
                                });

                                using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                                var content = new System.Net.Http.StringContent(payload, Encoding.UTF8, "application/json");
                                await client.PostAsync($"{peer.ActiveUrl.TrimEnd('/')}/api/logs", content);
                            }
                            catch { } // Don't fail if a peer is unreachable
                        }
                    }
                }
                catch { }
            }, null, 10_000, 5_000); // Start after 10s, push every 5s
        }

        private static long ExtractTimestamp(string logLine)
        {
            // Try to parse [2026-05-16 14:25:06.602] format
            try
            {
                if (logLine.Length > 25 && logLine[0] == '[')
                {
                    string tsStr = logLine.Substring(1, 23);
                    if (DateTime.TryParseExact(tsStr, "yyyy-MM-dd HH:mm:ss.fff",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var dt))
                    {
                        return new DateTimeOffset(dt).ToUnixTimeMilliseconds();
                    }
                }
            }
            catch { }
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        private static string JsonEscape(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }
    }
}
