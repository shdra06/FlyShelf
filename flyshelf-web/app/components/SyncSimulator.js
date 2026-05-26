"use client";
import { useState, useRef } from 'react';

const mockFiles = [
  { id: 'whitepaper', name: 'FlyShelf_Whitepaper.pdf', type: 'PDF', icon: '📄', color: '#ec4899', content: 'FlyShelf Whitepaper v7.0: Peer-to-peer clipboards, LAN streaming buffer technology.' },
  { id: 'theme', name: 'Sunset_Mica_Theme.png', type: 'IMG', icon: '🖼️', color: '#06b6d4', content: 'Sunset Mica Theme configuration JSON: { "acrylic": true, "tint": "#e11d48", "opacity": 0.6 }' },
  { id: 'wifi', name: 'wifi_office_credentials.txt', type: 'TXT', icon: '🔑', color: '#f59e0b', content: 'Network: FlyShelf_Office_HighSpeed\nPass: Productivity#2026' },
];

export default function SyncSimulator() {
  const [device1Clips, setDevice1Clips] = useState([
    { id: 'init1', name: 'spawning_animation_bug_report.txt', type: 'TXT', icon: '📋', color: '#cbd5e1' }
  ]);
  const [device2Clips, setDevice2Clips] = useState([
    { id: 'init2', name: 'meeting_notes_q2.md', type: 'TXT', icon: '📋', color: '#cbd5e1' }
  ]);
  const [device3Clips, setDevice3Clips] = useState([
    { id: 'init3', name: 'flyshelf_setup_installer.exe', type: 'EXE', icon: '⚙️', color: '#a78bfa' }
  ]);

  const [activeDragItem, setActiveDragItem] = useState(null);
  const [isSyncing, setIsSyncing] = useState(false);
  const [syncSource, setSyncSource] = useState(null);
  const [syncTargets, setSyncTargets] = useState([]);
  const [toast, setToast] = useState('');
  const [toastVisible, setToastVisible] = useState(false);

  const containerRef = useRef(null);

  const triggerToast = (message) => {
    setToast(message);
    setToastVisible(true);
    setTimeout(() => setToastVisible(false), 2500);
  };

  // Drag and Drop handlers
  const handleDragStart = (e, item) => {
    setActiveDragItem(item);
    e.dataTransfer.setData('text/plain', item.id);
  };

  const handleDragOver = (e) => {
    e.preventDefault();
  };

  const executeSync = (item, sourceDeviceId) => {
    if (isSyncing) return;
    setIsSyncing(true);
    setSyncSource(sourceDeviceId);

    // Setup animation target lines based on source
    let targets = [];
    if (sourceDeviceId === 'office') targets = ['home', 'android'];
    else if (sourceDeviceId === 'home') targets = ['office', 'android'];
    else if (sourceDeviceId === 'android') targets = ['office', 'home'];
    
    setSyncTargets(targets);

    // Add to all clipboard lists after flow animation completes (1.2 seconds)
    setTimeout(() => {
      const newClip = {
        id: `${item.id}_${Date.now()}`,
        name: item.name,
        type: item.type,
        icon: item.icon,
        color: item.color,
        content: item.content
      };

      setDevice1Clips(prev => [newClip, ...prev].slice(0, 4));
      setDevice2Clips(prev => [newClip, ...prev].slice(0, 4));
      setDevice3Clips(prev => [newClip, ...prev].slice(0, 4));

      setIsSyncing(false);
      setSyncSource(null);
      setSyncTargets([]);
      triggerToast(`📁 ${item.name} synced directly to all devices!`);
    }, 1400);
  };

  const handleDrop = (e, deviceId) => {
    e.preventDefault();
    if (!activeDragItem) return;
    executeSync(activeDragItem, deviceId);
    setActiveDragItem(null);
  };

  // Click trigger (for mobile/tablet touch fallback)
  const handleItemClickToSync = (item, sourceDeviceId) => {
    executeSync(item, sourceDeviceId);
  };

  // Copy clip contents to actual clipboard
  const handleCopyToSystemClipboard = async (content, label) => {
    try {
      await navigator.clipboard.writeText(content || label);
      triggerToast(`📋 Copied directly to your actual computer clipboard!`);
    } catch {
      const ta = document.createElement('textarea');
      ta.value = content || label;
      document.body.appendChild(ta);
      ta.select();
      document.execCommand('copy');
      document.body.removeChild(ta);
      triggerToast(`📋 Copied directly to your actual computer clipboard!`);
    }
  };

  return (
    <section className="sync-section" ref={containerRef}>
      <style jsx>{`
        .sync-section {
          position: relative;
          padding: 80px 20px;
          text-align: center;
          overflow: hidden;
        }

        .sync-badge {
          display: inline-flex;
          align-items: center;
          gap: 6px;
          background: rgba(139, 92, 246, 0.1);
          border: 1px solid rgba(139, 92, 246, 0.2);
          color: #a78bfa;
          font-size: 13px;
          font-weight: 600;
          padding: 6px 14px;
          border-radius: 20px;
          margin-bottom: 16px;
        }

        .sync-title {
          font-size: 32px;
          font-weight: 800;
          color: #f1f5f9;
          margin: 0 0 10px;
          font-family: var(--font-heading);
        }

        .sync-subtitle {
          font-size: 16px;
          color: rgba(255, 255, 255, 0.5);
          margin: 0 auto 40px;
          max-width: 600px;
          line-height: 1.6;
        }

        /* Drag and Drop Share Dock */
        .share-dock {
          background: rgba(255, 255, 255, 0.02);
          border: 1px solid rgba(255, 255, 255, 0.06);
          border-radius: 24px;
          padding: 24px;
          max-width: 720px;
          margin: 0 auto 60px;
          backdrop-filter: blur(16px);
          box-shadow: 0 8px 32px rgba(0, 0, 0, 0.4);
          position: relative;
          z-index: 10;
        }

        .dock-header {
          font-size: 13px;
          font-weight: 700;
          color: #06b6d4;
          letter-spacing: 1px;
          text-transform: uppercase;
          margin-bottom: 16px;
          display: flex;
          align-items: center;
          justify-content: center;
          gap: 8px;
        }

        .dock-items-row {
          display: flex;
          gap: 16px;
          justify-content: center;
          flex-wrap: wrap;
        }

        .dock-file-pill {
          padding: 12px 20px;
          border-radius: 14px;
          background: rgba(0, 0, 0, 0.4);
          border: 1px solid rgba(255, 255, 255, 0.08);
          color: #e2e8f0;
          font-size: 13px;
          font-family: var(--font-mono);
          cursor: grab;
          display: flex;
          align-items: center;
          gap: 10px;
          transition: all 0.3s cubic-bezier(0.16, 1, 0.3, 1);
          box-shadow: 0 4px 12px rgba(0,0,0,0.3);
          user-select: none;
        }

        .dock-file-pill:hover {
          border-color: var(--pill-color);
          transform: translateY(-2px);
          box-shadow: 0 8px 20px rgba(0, 0, 0, 0.4), 0 0 10px var(--pill-color);
          background: rgba(255,255,255,0.02);
        }

        .dock-file-pill:active {
          cursor: grabbing;
        }

        /* 3-Device Grid Layout */
        .devices-layout {
          position: relative;
          display: grid;
          grid-template-columns: repeat(3, 1fr);
          gap: 40px;
          max-width: 960px;
          margin: 0 auto;
          z-index: 5;
        }

        /* Desktop & Mobile Mockups styling */
        .device-window {
          background: rgba(10, 10, 18, 0.85);
          border-radius: 20px;
          border: 1px solid rgba(255, 255, 255, 0.08);
          box-shadow: 0 20px 50px rgba(0,0,0,0.6);
          overflow: hidden;
          min-height: 340px;
          display: flex;
          flex-direction: column;
          text-align: left;
          transition: all 0.4s cubic-bezier(0.16, 1, 0.3, 1);
          position: relative;
        }

        .device-window.active-source {
          border-color: #06b6d4;
          box-shadow: 0 0 30px rgba(6, 182, 212, 0.3), 0 20px 50px rgba(0,0,0,0.6);
        }

        .device-window.active-target {
          animation: pulseTarget 1.5s infinite alternate;
        }

        @keyframes pulseTarget {
          0% { border-color: rgba(255,255,255,0.08); box-shadow: 0 0 10px transparent; }
          100% { border-color: #a78bfa; box-shadow: 0 0 25px rgba(167, 139, 250, 0.35); }
        }

        .window-titlebar {
          display: flex;
          align-items: center;
          gap: 8px;
          padding: 12px 16px;
          background: rgba(255, 255, 255, 0.02);
          border-bottom: 1px solid rgba(255, 255, 255, 0.06);
        }

        .dot-controls {
          display: flex;
          gap: 6px;
        }

        .win-dot {
          width: 9px;
          height: 9px;
          border-radius: 50%;
          background: rgba(255, 255, 255, 0.15);
        }

        .titlebar-text {
          font-size: 11px;
          font-weight: 600;
          color: rgba(255, 255, 255, 0.4);
          font-family: var(--font-mono);
          margin-left: 4px;
        }

        .window-dropzone {
          flex: 1;
          padding: 16px;
          display: flex;
          flex-direction: column;
          gap: 10px;
          position: relative;
        }

        .dropzone-overlay-text {
          position: absolute;
          inset: 0;
          display: flex;
          align-items: center;
          justify-content: center;
          font-size: 11px;
          color: rgba(255,255,255,0.15);
          pointer-events: none;
          text-align: center;
          font-weight: 600;
          letter-spacing: 0.5px;
        }

        /* Clipboard Items inside mockups */
        .sim-clip-card {
          background: rgba(255, 255, 255, 0.02);
          border: 1px solid rgba(255, 255, 255, 0.05);
          border-radius: 12px;
          padding: 12px;
          display: flex;
          align-items: center;
          gap: 10px;
          cursor: pointer;
          transition: all 0.3s cubic-bezier(0.16, 1, 0.3, 1);
          animation: slideInCard 0.4s cubic-bezier(0.16, 1, 0.3, 1) forwards;
          position: relative;
          z-index: 10;
        }

        @keyframes slideInCard {
          from { opacity: 0; transform: translateY(12px) scale(0.95); }
          to { opacity: 1; transform: translateY(0) scale(1); }
        }

        .sim-clip-card:hover {
          background: rgba(255,255,255,0.05);
          border-color: var(--card-color, rgba(255,255,255,0.15));
          transform: translateX(2px);
          box-shadow: 0 4px 15px rgba(0,0,0,0.3);
        }

        .sim-clip-icon {
          font-size: 16px;
          flex-shrink: 0;
        }

        .sim-clip-details {
          display: flex;
          flex-direction: column;
          min-width: 0;
          flex: 1;
        }

        .sim-clip-name {
          font-size: 12px;
          font-weight: 600;
          color: #cbd5e1;
          white-space: nowrap;
          overflow: hidden;
          text-overflow: ellipsis;
        }

        .sim-clip-type {
          font-size: 9px;
          font-weight: 700;
          color: var(--card-color, rgba(255,255,255,0.3));
          margin-top: 2px;
          letter-spacing: 0.5px;
        }

        .sim-clip-copy-indicator {
          font-size: 11px;
          color: rgba(6,182,212,0.6);
          opacity: 0;
          transition: opacity 0.2s;
          margin-left: auto;
          flex-shrink: 0;
        }

        .sim-clip-card:hover .sim-clip-copy-indicator {
          opacity: 1;
        }

        /* Android Mockup Specifics */
        .android-body {
          border-radius: 36px !important;
          padding-top: 14px;
        }

        .android-notch {
          width: 60px;
          height: 14px;
          background: rgba(0, 0, 0, 0.9);
          border-radius: 99px;
          margin: 0 auto 10px;
          border: 1.5px solid rgba(255,255,255,0.06);
        }

        /* Vector lines background overlay */
        .flow-svg-overlay {
          position: absolute;
          inset: 0;
          width: 100%;
          height: 100%;
          pointer-events: none;
          z-index: 1;
        }

        .flow-line {
          fill: none;
          stroke: rgba(255, 255, 255, 0.03);
          stroke-width: 2.5;
          stroke-linecap: round;
        }

        .flow-line-active {
          stroke: url(#activeGrad);
          stroke-width: 3.5;
          stroke-dasharray: 10 300;
          animation: dashFlow 1.4s cubic-bezier(0.4, 0, 0.2, 1) forwards;
        }

        @keyframes dashFlow {
          from { stroke-dashoffset: 0; }
          to { stroke-dashoffset: -310; }
        }

        /* Mobile Click To Trigger Fallback Info Row */
        .fallback-info {
          font-size: 12px;
          color: rgba(255,255,255,0.3);
          margin-top: 16px;
          display: flex;
          align-items: center;
          justify-content: center;
          gap: 6px;
        }

        /* Toast notifications */
        .sync-toast {
          position: fixed;
          bottom: 32px;
          left: 50%;
          transform: translateX(-50%);
          background: rgba(12, 12, 22, 0.9);
          backdrop-filter: blur(12px);
          border: 1px solid rgba(167, 139, 250, 0.35);
          color: #a78bfa;
          padding: 14px 32px;
          border-radius: 16px;
          font-size: 13.5px;
          font-weight: 600;
          box-shadow: 0 15px 40px rgba(0, 0, 0, 0.6);
          z-index: 1000;
          display: flex;
          align-items: center;
          gap: 10px;
          animation: pqvToastIn 0.3s cubic-bezier(0.16, 1, 0.3, 1) forwards;
        }

        @media (max-width: 900px) {
          .devices-layout {
            grid-template-columns: 1fr;
            gap: 24px;
          }

          .device-window {
            min-height: 240px;
          }

          .flow-svg-overlay {
            display: none;
          }
        }
      `}</style>

      <div className="sync-badge">📡 3-Device Routing Mesh</div>
      <h2 className="sync-title">Universal Clipboard Space</h2>
      <p className="sync-subtitle">
        Drag files from the dock below onto <b>any device</b> to trigger a local sync loop. Files will securely propagate and populate the clipboards on <b>all devices</b>.
      </p>

      {/* DRAG AND SHARE DOCK */}
      <div className="share-dock">
        <div className="dock-header">
          <span>📁</span> Drag files to a PC or Phone below (or tap one to sync instantly)
        </div>
        <div className="dock-items-row">
          {mockFiles.map((file) => (
            <div
              key={file.id}
              className="dock-file-pill"
              draggable
              onDragStart={(e) => handleDragStart(e, file)}
              onClick={() => handleItemClickToSync(file, 'office')}
              style={{ '--pill-color': file.color }}
            >
              <span>{file.icon}</span>
              {file.name}
            </div>
          ))}
        </div>
      </div>

      {/* 3-DEVICE GRID LAYOUT */}
      <div className="devices-layout">
        
        {/* SVG Flow Arrows Overlay */}
        <svg className="flow-svg-overlay" viewBox="0 0 960 380" preserveAspectRatio="none">
          <defs>
            <linearGradient id="activeGrad" x1="0%" y1="0%" x2="100%" y2="0%">
              <stop offset="0%" stopColor="#06b6d4" />
              <stop offset="50%" stopColor="#a78bfa" />
              <stop offset="100%" stopColor="#ec4899" />
            </linearGradient>
          </defs>

          {/* Symmetrical pathways in triangle mesh */}
          {/* PC 1 (Office) ↔ PC 2 (Home) - x1=160, y1=170 to x2=480, y2=170 */}
          <path d="M 280,160 Q 480,80 680,160" className="flow-line" />
          {isSyncing && syncSource === 'office' && syncTargets.includes('home') && (
            <path d="M 280,160 Q 480,80 680,160" className="flow-line-active" />
          )}
          {isSyncing && syncSource === 'home' && syncTargets.includes('office') && (
            <path d="M 680,160 Q 480,80 280,160" className="flow-line-active" style={{ animationDirection: 'reverse' }} />
          )}

          {/* PC 1 (Office) ↔ Android Phone - x1=160, y1=170 to x2=800, y2=170 */}
          <path d="M 180,310 Q 480,360 780,310" className="flow-line" />
          {isSyncing && syncSource === 'office' && syncTargets.includes('android') && (
            <path d="M 180,310 Q 480,360 780,310" className="flow-line-active" />
          )}
          {isSyncing && syncSource === 'android' && syncTargets.includes('office') && (
            <path d="M 780,310 Q 480,360 180,310" className="flow-line-active" style={{ animationDirection: 'reverse' }} />
          )}

          {/* PC 2 (Home) ↔ Android Phone - x1=480, y1=170 to x2=800, y2=170 */}
          <path d="M 780,160 Q 820,240 780,310" className="flow-line" />
          {isSyncing && syncSource === 'home' && syncTargets.includes('android') && (
            <path d="M 780,160 Q 820,240 780,310" className="flow-line-active" />
          )}
          {isSyncing && syncSource === 'android' && syncTargets.includes('home') && (
            <path d="M 780,310 Q 820,240 780,160" className="flow-line-active" style={{ animationDirection: 'reverse' }} />
          )}
        </svg>

        {/* DEVICE 1: Windows Laptop (Office PC) */}
        <div
          className={`device-window ${syncSource === 'office' ? 'active-source' : ''} ${syncTargets.includes('office') ? 'active-target' : ''}`}
          onDragOver={handleDragOver}
          onDrop={(e) => handleDrop(e, 'office')}
        >
          <div className="window-titlebar">
            <div className="dot-controls">
              <span className="win-dot" style={{ background: '#ef4444' }} />
              <span className="win-dot" style={{ background: '#eab308' }} />
              <span className="win-dot" style={{ background: '#22c55e' }} />
            </div>
            <span className="titlebar-text">WinSumo Office (PC-1)</span>
          </div>
          <div className="window-dropzone">
            {device1Clips.length === 0 && (
              <div className="dropzone-overlay-text">Drag item here to sync</div>
            )}
            {device1Clips.map((clip) => (
              <div
                key={clip.id}
                className="sim-clip-card"
                style={{ '--card-color': clip.color }}
                onClick={() => handleCopyToSystemClipboard(clip.content, clip.name)}
              >
                <span className="sim-clip-icon">{clip.icon}</span>
                <div className="sim-clip-details">
                  <span className="sim-clip-name">{clip.name}</span>
                  <span className="sim-clip-type">{clip.type} Clip</span>
                </div>
                <span className="sim-clip-copy-indicator">📋 Copy</span>
              </div>
            ))}
          </div>
        </div>

        {/* DEVICE 2: Windows Laptop (Home PC) */}
        <div
          className={`device-window ${syncSource === 'home' ? 'active-source' : ''} ${syncTargets.includes('home') ? 'active-target' : ''}`}
          onDragOver={handleDragOver}
          onDrop={(e) => handleDrop(e, 'home')}
        >
          <div className="window-titlebar">
            <div className="dot-controls">
              <span className="win-dot" style={{ background: '#ef4444' }} />
              <span className="win-dot" style={{ background: '#eab308' }} />
              <span className="win-dot" style={{ background: '#22c55e' }} />
            </div>
            <span className="titlebar-text">WinSumo Home (PC-2)</span>
          </div>
          <div className="window-dropzone">
            {device2Clips.length === 0 && (
              <div className="dropzone-overlay-text">Drag item here to sync</div>
            )}
            {device2Clips.map((clip) => (
              <div
                key={clip.id}
                className="sim-clip-card"
                style={{ '--card-color': clip.color }}
                onClick={() => handleCopyToSystemClipboard(clip.content, clip.name)}
              >
                <span className="sim-clip-icon">{clip.icon}</span>
                <div className="sim-clip-details">
                  <span className="sim-clip-name">{clip.name}</span>
                  <span className="sim-clip-type">{clip.type} Clip</span>
                </div>
                <span className="sim-clip-copy-indicator">📋 Copy</span>
              </div>
            ))}
          </div>
        </div>

        {/* DEVICE 3: Android Phone */}
        <div
          className={`device-window android-body ${syncSource === 'android' ? 'active-source' : ''} ${syncTargets.includes('android') ? 'active-target' : ''}`}
          onDragOver={handleDragOver}
          onDrop={(e) => handleDrop(e, 'android')}
        >
          <div className="android-notch" />
          <div className="window-dropzone">
            {device3Clips.length === 0 && (
              <div className="dropzone-overlay-text">Drag item here to sync</div>
            )}
            {device3Clips.map((clip) => (
              <div
                key={clip.id}
                className="sim-clip-card"
                style={{ '--card-color': clip.color }}
                onClick={() => handleCopyToSystemClipboard(clip.content, clip.name)}
              >
                <span className="sim-clip-icon">{clip.icon}</span>
                <div className="sim-clip-details">
                  <span className="sim-clip-name">{clip.name}</span>
                  <span className="sim-clip-type">{clip.type} Clip</span>
                </div>
                <span className="sim-clip-copy-indicator">📋 Copy</span>
              </div>
            ))}
          </div>
        </div>

      </div>

      <div className="fallback-info">
        <span>💡</span> Clicking any list item copies it directly to your physical computer clipboard.
      </div>

      {/* TOAST SYSTEM */}
      {toastVisible && (
        <div className="sync-toast">
          <span>⚡</span> {toast}
        </div>
      )}
    </section>
  );
}
