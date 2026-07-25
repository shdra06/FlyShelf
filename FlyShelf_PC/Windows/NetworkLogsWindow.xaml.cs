using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FlyShelf.Classes;

namespace FlyShelf.Windows
{
    public partial class NetworkLogsWindow : MicaWPF.Controls.MicaWindow
    {
        // ═══ Data model for a single log line ═══
        public class LogEntry
        {
            public string DeviceName { get; set; } = "";
            public string LogText { get; set; } = "";
            public string Category { get; set; } = "";
            public long Timestamp { get; set; }
            public bool IsLocal { get; set; }

            // UI bindings
            public Brush LogColor => GetCategoryColor(Category);
            public Brush DeviceBadgeColor => IsLocal
                ? new SolidColorBrush(Color.FromArgb(40, 59, 130, 59))
                : new SolidColorBrush(Color.FromArgb(40, 130, 80, 180));
            public Brush DeviceTextColor => IsLocal
                ? new SolidColorBrush(Color.FromArgb(255, 63, 185, 80))
                : new SolidColorBrush(Color.FromArgb(255, 188, 140, 255));
            public Brush RowBackground => Category.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
                ? new SolidColorBrush(Color.FromArgb(15, 248, 81, 73))
                : Brushes.Transparent;

            private static Brush GetCategoryColor(string cat)
            {
                if (string.IsNullOrEmpty(cat)) return new SolidColorBrush(Color.FromRgb(180, 180, 180));
                if (cat.Contains("ERROR", StringComparison.OrdinalIgnoreCase) || cat.Contains("FAULT", StringComparison.OrdinalIgnoreCase)) return new SolidColorBrush(Color.FromRgb(248, 81, 73));
                if (cat.Contains("PEER", StringComparison.OrdinalIgnoreCase) || cat.Contains("WS", StringComparison.OrdinalIgnoreCase)) return new SolidColorBrush(Color.FromRgb(163, 113, 247));
                if (cat.Contains("CLIPBOARD", StringComparison.OrdinalIgnoreCase)) return new SolidColorBrush(Color.FromRgb(240, 136, 62));
                if (cat.Contains("HTTP", StringComparison.OrdinalIgnoreCase)) return new SolidColorBrush(Color.FromRgb(88, 166, 255));
                if (cat.Contains("CLOUDFLARE", StringComparison.OrdinalIgnoreCase) || cat.Contains("CF_", StringComparison.OrdinalIgnoreCase) || cat.Contains("TUNNEL", StringComparison.OrdinalIgnoreCase)) return new SolidColorBrush(Color.FromRgb(121, 192, 255));
                if (cat.Contains("FIREBASE", StringComparison.OrdinalIgnoreCase)) return new SolidColorBrush(Color.FromRgb(255, 166, 87));
                if (cat.Contains("PUSH", StringComparison.OrdinalIgnoreCase)) return new SolidColorBrush(Color.FromRgb(63, 185, 80));
                if (cat.Contains("DOWNLOAD", StringComparison.OrdinalIgnoreCase)) return new SolidColorBrush(Color.FromRgb(210, 168, 255));
                if (cat.Contains("SERVER", StringComparison.OrdinalIgnoreCase) || cat.Contains("BIND", StringComparison.OrdinalIgnoreCase)) return new SolidColorBrush(Color.FromRgb(255, 123, 114));
                if (cat.Contains("SECURITY", StringComparison.OrdinalIgnoreCase)) return new SolidColorBrush(Color.FromRgb(255, 200, 50));
                return new SolidColorBrush(Color.FromRgb(180, 190, 200));
            }
        }

        private readonly ObservableCollection<LogEntry> _allLogs = new();
        private readonly ObservableCollection<LogEntry> _filteredLogs = new();
        private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(8) };
        private Timer? _pollTimer;
        private int _pollIntervalMs = 2000;
        private string _activeDeviceFilter = "*";
        private string _activeCategoryFilter = "";
        private bool _autoScroll = true;
        private bool _userScrolledUp = false;
        private bool _isClosing = false;
        private string _localDevice = Environment.MachineName;
        private readonly ConcurrentDictionary<string, long> _lastSeenTimestamp = new(); // device → last ts
        private int _totalEntries = 0;

        public NetworkLogsWindow()
        {
            InitializeComponent();
            FlyShelf.Classes.SmoothScrollFeature.Attach(this);
            Classes.NativeMethods.ApplyWindowBackdropAndBackground(this);
            LogItems.ItemsSource = _filteredLogs;
            this.PreviewKeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Escape) this.Close(); };

            // Initial load
            _ = Task.Run(async () =>
            {
                await Task.Delay(500);
                await PollAllDevices();
                StartPolling();
            });
        }

        // ═══ Polling Engine ═══

        private void StartPolling()
        {
            _pollTimer?.Dispose();
            _pollTimer = new Timer(_ => SafeAsyncHandler.SafeTimerCallback(async () =>
            {
                if (_isClosing) return;
                await PollAllDevices();
            }), null, _pollIntervalMs, _pollIntervalMs);
        }

        private async Task PollAllDevices()
        {
            try
            {
                var tasks = new List<Task>();

                // 1. Poll local device
                tasks.Add(PollDevice(_localDevice, $"http://localhost:8999", true));

                // 2. Poll all paired remote devices
                var peers = PeerManager.Instance?.ConnectedPeers;
                if (peers != null)
                {
                    foreach (var kvp in peers)
                    {
                        var peer = kvp.Value;
                        string url = peer.ActiveUrl ?? peer.CloudflareUrl ?? peer.LanUrl;
                        if (string.IsNullOrEmpty(url)) continue;

                        tasks.Add(PollDevice(peer.DeviceName ?? peer.DeviceId, url.TrimEnd('/'), false));
                    }
                }

                await Task.WhenAll(tasks);

                // Update UI
                Dispatcher.Invoke(() =>
                {
                    ApplyFilters();
                    UpdateStatus();
                    EnsureDeviceTabs();
                });
            }
            catch { } // Best-effort: failure is acceptable
        }

        private async Task PollDevice(string deviceName, string baseUrl, bool isLocal)
        {
            try
            {
                // Get since timestamp to avoid duplicates
                long since = _lastSeenTimestamp.TryGetValue(deviceName, out var ts) ? ts : 0;

                string requestUrl = $"{baseUrl}/api/logs?lines=100&device={Uri.EscapeDataString(deviceName)}";
                if (!isLocal)
                {
                    // For remote, use their local device name in the query
                    requestUrl = $"{baseUrl}/api/logs?lines=100";
                }

                var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
                request.Headers.Add("X-FlyShelf-Client", "DesktopSync");

                // Try to add pairing key
                string pairingKey = DevicePairingManager.EnsurePairingKey();
                if (!string.IsNullOrEmpty(pairingKey))
                    request.Headers.Add("X-Pairing-Key", pairingKey);

                // Try PIN auth too
                string pin = SettingsManager.Current?.WebClientPinToken;
                if (!string.IsNullOrEmpty(pin))
                    request.Headers.Add("Authorization", $"Bearer {pin}");

                var response = await _httpClient.SendAsync(request);
                if (!response.IsSuccessStatusCode) return;

                string json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("logs", out var logsArr)) return;

                bool addedNew = false;
                foreach (var logEl in logsArr.EnumerateArray())
                {
                    string logLine = "";
                    string logDevice = deviceName;

                    if (logEl.ValueKind == JsonValueKind.Object)
                    {
                        logLine = logEl.TryGetProperty("log", out var lp) ? lp.GetString() ?? "" : "";
                        logDevice = logEl.TryGetProperty("device", out var dp) ? dp.GetString() ?? deviceName : deviceName;
                    }
                    else if (logEl.ValueKind == JsonValueKind.String)
                    {
                        logLine = logEl.GetString() ?? "";
                    }

                    if (string.IsNullOrWhiteSpace(logLine)) continue;

                    // Strip existing device prefix like "[💻 PC]" 
                    logLine = StripDevicePrefix(logLine);

                    string category = ExtractCategory(logLine);

                    // Simple dedup: check if we already have this exact log line from this device
                    var entry = new LogEntry
                    {
                        DeviceName = logDevice,
                        LogText = logLine,
                        Category = category,
                        Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        IsLocal = isLocal || logDevice.Equals(_localDevice, StringComparison.OrdinalIgnoreCase)
                    };

                    // M-6/M-7 FIX: Dedup check + add must be atomic, so both run inside
                    // Dispatcher.Invoke on the UI thread (naturally serialized).
                    bool isDuplicate = false;
                    Dispatcher.Invoke(() =>
                    {
                        // Check last 200 entries for duplicates
                        int checkStart = Math.Max(0, _allLogs.Count - 200);
                        for (int i = _allLogs.Count - 1; i >= checkStart; i--)
                        {
                            if (_allLogs[i].DeviceName == logDevice && _allLogs[i].LogText == logLine)
                            {
                                isDuplicate = true;
                                break;
                            }
                        }

                        if (!isDuplicate)
                        {
                            _allLogs.Add(entry);
                            _totalEntries++;

                            // Cap at 2000 entries
                            while (_allLogs.Count > 2000)
                                _allLogs.RemoveAt(0);
                        }
                    });

                    if (isDuplicate) continue;

                    addedNew = true;
                }

                if (addedNew)
                    _lastSeenTimestamp[deviceName] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); // ConcurrentDictionary — thread-safe
            }
            catch (TaskCanceledException) { } // Timeout — normal for unreachable devices
            catch (HttpRequestException) { } // Network error — device offline
            catch (Exception ex)
            {
                Logger.LogAction("LOGS WINDOW", $"Poll error for {deviceName}: {ex.Message}");
            }
        }

        // ═══ Filtering ═══

        private void ApplyFilters()
        {
            _filteredLogs.Clear();

            foreach (var entry in _allLogs)
            {
                if (_activeDeviceFilter != "*" && !entry.DeviceName.Equals(_activeDeviceFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!string.IsNullOrEmpty(_activeCategoryFilter) && !entry.Category.Contains(_activeCategoryFilter, StringComparison.OrdinalIgnoreCase))
                    continue;

                _filteredLogs.Add(entry);
            }

            // Auto-scroll
            if (_autoScroll && AutoScrollToggle.IsChecked == true && !_userScrolledUp)
            {
                LogScroller.ScrollToEnd();
            }
        }

        private void EnsureDeviceTabs()
        {
            var devices = _allLogs.Select(l => l.DeviceName).Distinct().ToList();

            foreach (var device in devices)
            {
                string tag = device;
                bool exists = false;
                foreach (var child in DeviceTabPanel.Children)
                {
                    if (child is RadioButton rb && rb.Tag?.ToString() == tag)
                    {
                        exists = true;
                        break;
                    }
                }

                if (!exists)
                {
                    bool isLocal = device.Equals(_localDevice, StringComparison.OrdinalIgnoreCase);
                    var tab = new RadioButton
                    {
                        Content = (isLocal ? "🖥 " : "🌐 ") + device,
                        Tag = tag,
                        GroupName = "DeviceFilter",
                        Style = (Style)FindResource("FilterTab"),
                        Margin = new Thickness(4, 0, 0, 0)
                    };
                    tab.Click += FilterChanged;
                    DeviceTabPanel.Children.Add(tab);
                }
            }
        }

        // ═══ UI Helpers ═══

        private void UpdateStatus()
        {
            var peers = PeerManager.Instance?.ConnectedPeers;
            int peerCount = peers?.Count ?? 0;
            int aliveCount = peers?.Values.Count(p => p.IsAlive) ?? 0;

            string peerInfo = peerCount > 0
                ? $"{aliveCount}/{peerCount} peers alive"
                : "No peers connected";

            StatusText.Text = $"📡 {_filteredLogs.Count} entries shown ({_allLogs.Count} total) | {peerInfo}";

            // Show peer URLs in status right
            if (peers != null)
            {
                var urls = peers.Values
                    .Where(p => !string.IsNullOrEmpty(p.ActiveUrl))
                    .Select(p => $"{p.DeviceName}: {(p.ActiveUrl.Contains("trycloudflare", StringComparison.OrdinalIgnoreCase) ? "☁ CF" : "🏠 LAN")}")
                    .ToList();
                StatusRight.Text = string.Join(" | ", urls);
            }

            TitleDeviceInfo.Text = $"Local: {_localDevice} | Polling every {_pollIntervalMs / 1000}s";
        }

        private static string ExtractCategory(string logLine)
        {
            // Parse [2026-05-16 15:00:00.000] [CATEGORY] ...
            try
            {
                int secondBracketStart = logLine.IndexOf('[', logLine.IndexOf(']') + 1);
                if (secondBracketStart >= 0)
                {
                    int secondBracketEnd = logLine.IndexOf(']', secondBracketStart);
                    if (secondBracketEnd > secondBracketStart)
                    {
                        return logLine.Substring(secondBracketStart + 1, secondBracketEnd - secondBracketStart - 1).Trim();
                    }
                }
            }
            catch { } // Best-effort: failure is acceptable
            return "LOG";
        }

        private static string StripDevicePrefix(string line)
        {
            // Remove "[💻 PC] " or "[📱 Mobile] " prefixes
            if (line.StartsWith('[') && line.Length > 5)
            {
                int closeIdx = line.IndexOf(']');
                if (closeIdx > 0 && closeIdx < 20)
                {
                    string inside = line.Substring(1, closeIdx - 1);
                    if (inside.Contains("PC", StringComparison.Ordinal) || inside.Contains("Mobile", StringComparison.Ordinal) || inside.Contains("💻", StringComparison.Ordinal) || inside.Contains("📱", StringComparison.Ordinal))
                    {
                        return line.Substring(closeIdx + 1).TrimStart();
                    }
                }
            }
            return line;
        }

        // ═══ Event Handlers ═══

        private void FilterChanged(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb)
            {
                _activeDeviceFilter = rb.Tag?.ToString() ?? "*";
                ApplyFilters();
            }
        }

        private void CategoryFilterChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CategoryFilter.SelectedItem is ComboBoxItem item)
            {
                _activeCategoryFilter = item.Tag?.ToString() ?? "";
                ApplyFilters();
            }
        }

        private void RefreshRateChanged(object sender, SelectionChangedEventArgs e)
        {
            if (RefreshRate.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out int ms))
            {
                _pollIntervalMs = ms;
                StartPolling();
            }
        }

        private void ClearLogs_Click(object sender, RoutedEventArgs e)
        {
            _allLogs.Clear();
            _filteredLogs.Clear();
            _totalEntries = 0;
            _lastSeenTimestamp.Clear();
            UpdateStatus();
        }

        private void CopyLogs_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var text = string.Join(Environment.NewLine, _filteredLogs.Select(l => $"[{l.DeviceName}] {l.LogText}"));
                if (ClipboardHelper.SafeSetText(text))
                {
                    ToastWindow.ShowToast($"📋 Copied {_filteredLogs.Count} log entries");
                }
            }
            catch { } // Best-effort: failure is acceptable
        }

        private void LogScroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            // Detect if user scrolled up manually (disable auto-scroll)
            if (e.ExtentHeightChange == 0)
            {
                // User initiated scroll
                _userScrolledUp = LogScroller.VerticalOffset < LogScroller.ScrollableHeight - 50;
            }
            else
            {
                // Content changed — if auto-scroll is on and user hasn't scrolled up, scroll to end
                if (_autoScroll && AutoScrollToggle.IsChecked == true && !_userScrolledUp)
                {
                    LogScroller.ScrollToEnd();
                }
            }
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            FlyShelf.Classes.SmoothScrollFeature.Detach(this);
            _isClosing = true;
            _pollTimer?.Dispose();
            // M-12: _httpClient is now static readonly — do not dispose per-instance
        }
    }
}
