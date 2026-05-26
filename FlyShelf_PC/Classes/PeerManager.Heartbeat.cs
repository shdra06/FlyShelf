// ---------------------------------------------------------------
// PeerManager — Heartbeat, Discovery Loop & Failure Handling
// HeartbeatLoop, DiscoveryLoop, HandlePeerDeath, HandlePeerFailure,
// ForceResync, GetPeerStatuses
// Split from PeerManager.cs for modularity
// ---------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace FlyShelf.Classes
{
    public partial class PeerManager
    {
        /// <summary>
        /// Pings all alive peers every HEARTBEAT_MS (5s).
        /// If a peer fails MAX_FAILURES (3) consecutive health checks, it's marked dead.
        /// This is the primary mechanism for detecting LAN disconnections.
        /// </summary>
        private async Task HeartbeatLoop(CancellationToken ct)
        {
            Logger.LogAction("PEER", "💓 Heartbeat loop started (5s interval)");
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(HEARTBEAT_MS, ct);
                }
                catch (OperationCanceledException) { return; }

                var alivePeers = _peers.Values.Where(p => p.IsAlive).ToList();
                if (alivePeers.Count == 0) continue;

                var pingTasks = alivePeers.Select(async peer =>
                {
                    if (ct.IsCancellationRequested) return;

                    // Skip peers with active file transfers — don't kill mid-transfer
                    if (peer.ActiveTransfers > 0) return;

                    bool ok = await PingPeer(peer);
                    if (ok)
                    {
                        peer.ConsecutiveFailures = 0;
                        peer.LastSeen = DateTime.UtcNow;
                    }
                    else
                    {
                        peer.ConsecutiveFailures++;
                        if (peer.ConsecutiveFailures >= MAX_FAILURES)
                        {
                            Logger.LogAction("PEER", $"💀 {peer.DeviceName} failed {MAX_FAILURES} heartbeats — marking dead");
                            await HandlePeerDeath(peer);
                        }
                        else
                        {
                            Logger.LogAction("PEER", $"⚠️ {peer.DeviceName} heartbeat miss ({peer.ConsecutiveFailures}/{MAX_FAILURES})");
                        }
                    }
                });

                await Task.WhenAll(pingTasks);
            }
        }

        /// <summary>
        /// HTTP health ping with short timeout. Returns true if peer responds.
        /// </summary>
        private async Task<bool> PingPeer(PeerConnection peer)
        {
            if (string.IsNullOrEmpty(peer.ActiveUrl)) return false;
            try
            {
                using var cts = new CancellationTokenSource(HEARTBEAT_TIMEOUT_MS);
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{peer.ActiveUrl.TrimEnd('/')}/api/health");
                string pk = DevicePairingManager.GetPairingKeyForDevice(peer.DeviceId);
                if (string.IsNullOrEmpty(pk)) pk = DevicePairingManager.EnsurePairingKey();
                if (!string.IsNullOrEmpty(pk)) req.Headers.TryAddWithoutValidation("X-Pairing-Key", pk);

                var resp = await _sharedClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                return resp.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Re-scans Firebase every DISCOVERY_MS (30s) looking for new peers or 
        /// reconnecting dead ones. Only does full discovery if there are dead peers.
        /// </summary>
        private async Task DiscoveryLoop(CancellationToken ct)
        {
            Logger.LogAction("PEER", "🔍 Discovery loop started (30s interval)");
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(DISCOVERY_MS, ct);
                }
                catch (OperationCanceledException) { return; }

                // Only re-discover if we have dead peers or zero peers
                bool hasDeadPeers = _peers.Values.Any(p => !p.IsAlive);
                if (hasDeadPeers || _peers.Count == 0)
                {
                    try
                    {
                        await DiscoverAndHandshake();
                    }
                    catch (Exception ex)
                    {
                        Logger.LogAction("PEER", $"Discovery loop error: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// Marks a peer as dead, closes its WebSocket, fires PeerDisconnected event,
        /// and attempts immediate failover to alternate transport.
        /// </summary>
        private async Task HandlePeerDeath(PeerConnection peer)
        {
            string oldTransport = peer.Transport;
            peer.IsAlive = false;
            peer.Transport = "offline";
            peer.ConsecutiveFailures = 0;

            // Close WebSocket
            try { peer.WsCts?.Cancel(); } catch { }
            try { peer.LiveSocket?.Dispose(); } catch { }
            peer.LiveSocket = null;

            // Fire event so UI updates immediately
            PeerDisconnected?.Invoke(peer.DeviceId);
            Logger.LogAction("PEER", $"❌ {peer.DeviceName} disconnected (was {oldTransport})");

            // Attempt immediate failover to alternate transport
            bool recovered = false;
            bool lanEnabled = SettingsManager.Current.EnableLocalLAN;

            if (oldTransport == "LAN" && !string.IsNullOrEmpty(peer.CloudflareUrl))
            {
                // Was LAN, try Cloudflare
                Logger.LogAction("PEER", $"🔄 Attempting Cloudflare failover for {peer.DeviceName}...");
                recovered = await TryConnect(peer, peer.CloudflareUrl, "Cloudflare");
            }
            else if (oldTransport == "Cloudflare" && lanEnabled && !string.IsNullOrEmpty(peer.LanUrl))
            {
                // Was Cloudflare, try LAN
                Logger.LogAction("PEER", $"🔄 Attempting LAN failover for {peer.DeviceName}...");
                recovered = await TryConnect(peer, peer.LanUrl, "LAN");
            }

            if (recovered)
            {
                Logger.LogAction("PEER", $"✅ {peer.DeviceName} recovered via {peer.Transport}");
                TransportSwitched?.Invoke(peer.DeviceId, peer.Transport);
                PeerConnected?.Invoke(peer.DeviceId, peer.Transport);
            }
        }

        /// <summary>
        /// Called when a data transfer to a peer fails. Increments failure count
        /// and triggers death if threshold is reached.
        /// </summary>
        private void HandlePeerFailure(PeerConnection peer, string reason)
        {
            peer.ConsecutiveFailures++;
            if (peer.ConsecutiveFailures >= MAX_FAILURES)
            {
                Logger.LogAction("PEER", $"💀 {peer.DeviceName} transfer failures hit {MAX_FAILURES} — marking dead ({reason})");
                _ = HandlePeerDeath(peer);
            }
            else
            {
                Logger.LogAction("PEER", $"⚠️ {peer.DeviceName} transfer failure ({peer.ConsecutiveFailures}/{MAX_FAILURES}): {reason}");
            }
        }

        /// <summary>
        /// Force re-discover all peers. Called from UI "Force Sync" button.
        /// Resets liveness and re-runs full discovery + handshake cycle.
        /// </summary>
        public async Task ForceResync()
        {
            Logger.LogAction("PEER", "🔄 Force resync requested");

            // Reset all peers
            foreach (var peer in _peers.Values)
            {
                peer.IsAlive = false;
                peer.ConsecutiveFailures = 0;
                try { peer.WsCts?.Cancel(); } catch { }
                try { peer.LiveSocket?.Dispose(); } catch { }
                peer.LiveSocket = null;
            }

            // Re-publish our URLs so peers can find us
            _urlCleanedFromFirebase = false;
            _urlRequestSent = false;

            string globalUrl = CloudDiscoveryManager.CachedGlobalUrl;
            string localUrl = CloudDiscoveryManager.CachedLocalUrl;
            if (!string.IsNullOrEmpty(globalUrl) || !string.IsNullOrEmpty(localUrl))
            {
                try
                {
                    await CloudDiscoveryManager.PushTunnelUrl(globalUrl ?? "", true, localUrl, forceWrite: true);
                }
                catch { }
            }

            // Try cached URLs first, then Firebase
            await TryCachedUrlsFirst();
            await DiscoverAndHandshake();

            Logger.LogAction("PEER", $"🔄 Force resync complete — {AliveCount}/{_peers.Count} peer(s) alive");
        }

        /// <summary>
        /// Returns a snapshot of all peer statuses for UI display in HubWindow.
        /// </summary>
        public List<PeerStatusItem> GetPeerStatuses()
        {
            return _peers.Values.Select(p => new PeerStatusItem
            {
                DeviceId = p.DeviceId,
                DeviceName = p.DeviceName,
                IsAlive = p.IsAlive,
                Transport = p.Transport,
                IsLanActive = !string.IsNullOrEmpty(p.LanUrl),
                IsCloudActive = !string.IsNullOrEmpty(p.CloudflareUrl),
                StatusText = p.IsAlive
                    ? $"Connected via {p.Transport}"
                    : "Offline",
                LanUrl = p.LanUrl,
                CloudflareUrl = p.CloudflareUrl,
                ActiveUrl = p.ActiveUrl,
                LastSeen = p.LastSeen
            }).ToList();
        }
    }
}
