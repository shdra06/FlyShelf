"use client";

import { useState } from 'react';
import Link from 'next/link';
import ScrollReveal from './components/ScrollReveal';
import SyncSimulator from './components/SyncSimulator';
import ShakeSummon from './components/ShakeSummon';
import ThemeSwitcher from './components/ThemeSwitcher';
import LivePdfMerger from './components/LivePdfMerger';
import CountdownTimer from './components/CountdownTimer';
import ColorPicker from './components/ColorPicker';
import UtmSanitizer from './components/UtmSanitizer';
import PhotoQuickView from './components/PhotoQuickView';

/* ============================================
   THEME DEFINITIONS (DARK MODE)
   ============================================ */
const THEMES = {
  midnight: {
    cardBg: 'rgba(10, 10, 20, 0.65)',
    cardBorder: 'rgba(59, 130, 246, 0.15)',
    activeBg: 'rgba(59, 130, 246, 0.02)',
    activeBorder: 'rgba(59, 130, 246, 0.25)',
    codeColor: '#93c5fd',
    linkColor: '#c084fc',
    glow: '0 30px 60px rgba(0, 0, 0, 0.8), 0 0 30px rgba(59, 130, 246, 0.15)',
    tagCodeBg: 'rgba(59, 130, 246, 0.15)',
    tagCodeColor: '#93c5fd',
    tagImgBg: 'rgba(16, 185, 129, 0.15)',
    tagImgColor: '#a7f3d0',
    tagLinkBg: 'rgba(139, 92, 246, 0.15)',
    tagLinkColor: '#c084fc',
    headerBg: 'rgba(255, 255, 255, 0.02)',
  },
  ocean: {
    cardBg: 'rgba(5, 15, 30, 0.75)',
    cardBorder: 'rgba(6, 182, 212, 0.15)',
    activeBg: 'rgba(6, 182, 212, 0.03)',
    activeBorder: 'rgba(6, 182, 212, 0.3)',
    codeColor: '#67e8f9',
    linkColor: '#38bdf8',
    glow: '0 30px 60px rgba(0, 0, 0, 0.8), 0 0 30px rgba(6, 182, 212, 0.2)',
    tagCodeBg: 'rgba(6, 182, 212, 0.15)',
    tagCodeColor: '#67e8f9',
    tagImgBg: 'rgba(14, 165, 233, 0.15)',
    tagImgColor: '#7dd3fc',
    tagLinkBg: 'rgba(56, 189, 248, 0.15)',
    tagLinkColor: '#38bdf8',
    headerBg: 'rgba(6, 182, 212, 0.03)',
  },
  sunset: {
    cardBg: 'rgba(20, 8, 8, 0.75)',
    cardBorder: 'rgba(249, 115, 22, 0.15)',
    activeBg: 'rgba(249, 115, 22, 0.03)',
    activeBorder: 'rgba(249, 115, 22, 0.3)',
    codeColor: '#fdba74',
    linkColor: '#f472b6',
    glow: '0 30px 60px rgba(0, 0, 0, 0.8), 0 0 30px rgba(249, 115, 22, 0.2)',
    tagCodeBg: 'rgba(249, 115, 22, 0.15)',
    tagCodeColor: '#fdba74',
    tagImgBg: 'rgba(236, 72, 153, 0.15)',
    tagImgColor: '#f9a8d4',
    tagLinkBg: 'rgba(244, 114, 182, 0.15)',
    tagLinkColor: '#f472b6',
    headerBg: 'rgba(249, 115, 22, 0.03)',
  },
  emerald: {
    cardBg: 'rgba(5, 20, 10, 0.75)',
    cardBorder: 'rgba(16, 185, 129, 0.15)',
    activeBg: 'rgba(16, 185, 129, 0.03)',
    activeBorder: 'rgba(16, 185, 129, 0.3)',
    codeColor: '#6ee7b7',
    linkColor: '#34d399',
    glow: '0 30px 60px rgba(0, 0, 0, 0.8), 0 0 30px rgba(16, 185, 129, 0.2)',
    tagCodeBg: 'rgba(16, 185, 129, 0.15)',
    tagCodeColor: '#6ee7b7',
    tagImgBg: 'rgba(52, 211, 153, 0.15)',
    tagImgColor: '#a7f3d0',
    tagLinkBg: 'rgba(16, 185, 129, 0.15)',
    tagLinkColor: '#34d399',
    headerBg: 'rgba(16, 185, 129, 0.03)',
  },
  lavender: {
    cardBg: 'rgba(15, 5, 25, 0.75)',
    cardBorder: 'rgba(167, 139, 250, 0.15)',
    activeBg: 'rgba(167, 139, 250, 0.03)',
    activeBorder: 'rgba(167, 139, 250, 0.3)',
    codeColor: '#c4b5fd',
    linkColor: '#e879f9',
    glow: '0 30px 60px rgba(0, 0, 0, 0.8), 0 0 30px rgba(167, 139, 250, 0.2)',
    tagCodeBg: 'rgba(167, 139, 250, 0.15)',
    tagCodeColor: '#c4b5fd',
    tagImgBg: 'rgba(192, 132, 252, 0.15)',
    tagImgColor: '#d8b4fe',
    tagLinkBg: 'rgba(232, 121, 249, 0.15)',
    tagLinkColor: '#e879f9',
    headerBg: 'rgba(167, 139, 250, 0.03)',
  },
};

export default function Home() {
  const [activeTheme, setActiveTheme] = useState('midnight');
  const theme = THEMES[activeTheme];

  const stats = [
    { value: '3', label: 'Sync Modes (LAN / Cloud / signaling)', desc: 'Realtime dynamic connection routing' },
    { value: '0', label: 'Ads, Tracking or Spyware', desc: '100% private & passive operation' },
    { value: '100%', label: 'Free & Open Source', desc: 'No premium paywalls or subscriptions' },
    { value: '2', label: 'Supported Platforms', desc: 'Windows PC & Android Phone' },
  ];

  const competitors = [
    { name: 'FlyShelf', ui: '✨ Mica Glassmorphism', history: 'Unlimited', sync: '✅ PC ↔ Android', transfer: '✅ P2P LAN + Cloud', tools: '✅ AI OCR + PDF Merge' },
    { name: 'Win + V', ui: 'Standard Gray', history: '25 Items', sync: '❌ PC Only', transfer: '❌ None', tools: '❌ None' },
    { name: 'Ditto', ui: '📅 2005 Windows Forms', history: 'Unlimited', sync: '❌ PC Only', transfer: '❌ Local LAN only', tools: '❌ None' },
    { name: 'PastePaw', ui: '✨ Mica (Rust)', history: 'Unlimited', sync: '❌ PC Only', transfer: '❌ None', tools: '❌ AI ready only' },
  ];

  return (
    <>
      {/* BACKGROUND MESH BLOBS */}
      <div className="bg-mesh">
        <div className="blob-fluid blob-cyan" />
        <div className="blob-fluid blob-purple" />
        <div className="blob-fluid blob-emerald" />
      </div>
      <div className="grid-bg" />

      {/* ===== HERO SECTION ===== */}
      <section className="hero-section">
        <div className="container">
          <div className="hero-content">
            <ScrollReveal className="hero-badge-container">
              <span className="hero-badge">⚡ Now Available: v7.0.0 Stable Release</span>
            </ScrollReveal>

            <ScrollReveal delay={100}>
              <h1 className="hero-title">
                Copy Once. <br />
                <span className="gradient-text-rainbow">Paste Everywhere.</span>
              </h1>
            </ScrollReveal>

            <ScrollReveal delay={200}>
              <p className="hero-subtitle">
                Meet FlyShelf, the ultra-premium cross-device clipboard sync ecosystem. Capture your history, transfer huge files over local WiFi, extract text from screenshots using AI, and sync it all between Windows &amp; Android instantly.
              </p>
            </ScrollReveal>

            <ScrollReveal delay={300} className="hero-ctas">
              <Link href="/download" className="btn btn-primary">
                Get FlyShelf Free
              </Link>
              <Link href="/features" className="btn btn-secondary">
                Explore Features
              </Link>
            </ScrollReveal>

            {/* THEMED MOCKUP */}
            <ScrollReveal delay={400} className="hero-mockup-wrapper">
              <div
                className="mockup-frame glass-panel animate-float"
                style={{
                  background: theme.cardBg,
                  boxShadow: theme.glow,
                  transition: 'all 0.5s cubic-bezier(0.16, 1, 0.3, 1)',
                }}
              >
                <div className="mockup-header" style={{ background: theme.headerBg, transition: 'background 0.5s ease' }}>
                  <div className="mockup-dots">
                    <span className="dot red" />
                    <span className="dot yellow" />
                    <span className="dot green" />
                  </div>
                  <div className="mockup-title">FlyShelf Desktop Dashboard (Alt + C)</div>
                  <div className="mockup-search-container">
                    <span className="mockup-search-icon">🔍</span>
                    <span className="mockup-search-text">Search clips or type /5 for timer...</span>
                  </div>
                </div>
                <div className="mockup-body">
                  <div className="mockup-card text-card active" style={{
                    borderColor: theme.activeBorder,
                    background: theme.activeBg,
                    transition: 'all 0.5s ease',
                  }}>
                    <div className="card-top">
                      <span className="card-tag" style={{ background: theme.tagCodeBg, color: theme.tagCodeColor, transition: 'all 0.5s ease' }}>Snippet</span>
                      <span className="card-time">Just now</span>
                    </div>
                    <pre className="code-block">
                      <code style={{ color: theme.codeColor, transition: 'color 0.5s ease' }}>{`const syncEngine = new FlyShelf.LAN.Sync();\nawait syncEngine.pushClipboard(data);`}</code>
                    </pre>
                  </div>

                  <div className="mockup-card image-card" style={{ borderColor: theme.cardBorder, transition: 'all 0.5s ease' }}>
                    <div className="card-top">
                      <span className="card-tag" style={{ background: theme.tagImgBg, color: theme.tagImgColor, transition: 'all 0.5s ease' }}>Screenshot</span>
                      <span className="card-time">2 mins ago</span>
                    </div>
                    <div className="image-placeholder">
                      <span className="img-icon">🖼️</span>
                      <span className="img-desc">Screenshot_2026-05-25.png (142 KB)</span>
                    </div>
                    <div className="card-actions">
                      <span className="action-pill font-mono">Gemini OCR</span>
                      <span className="action-pill font-mono">Convert to WebP</span>
                    </div>
                  </div>

                  <div className="mockup-card text-card" style={{ borderColor: theme.cardBorder, transition: 'all 0.5s ease' }}>
                    <div className="card-top">
                      <span className="card-tag" style={{ background: theme.tagLinkBg, color: theme.tagLinkColor, transition: 'all 0.5s ease' }}>Hyperlink</span>
                      <span className="card-time">10 mins ago</span>
                    </div>
                    <p className="card-text" style={{ color: theme.linkColor, transition: 'color 0.5s ease' }}>https://github.com/shdra06/FlyShelf</p>
                  </div>
                </div>
              </div>
              {/* Theme Switcher Row */}
              <ThemeSwitcher activeTheme={activeTheme} onThemeChange={setActiveTheme} />
            </ScrollReveal>
          </div>
        </div>
      </section>

      {/* ===== SYNC SPACE — PC ↔ ANDROID LIVE DEMO ===== */}
      <section className="interactive-section" style={{ borderTop: '1px solid var(--border-glass)' }}>
        <div className="container">
          <SyncSimulator />
        </div>
      </section>

      {/* ===== CLIPBOARD IMAGE OCR — PHOTO QUICK VIEW ===== */}
      <section className="interactive-section" style={{ background: 'var(--bg-secondary)', borderTop: '1px solid var(--border-glass)' }}>
        <div className="container">
          <PhotoQuickView />
        </div>
      </section>

      {/* ===== SHAKE SUMMON GESTURE DEMO ===== */}
      <section className="interactive-section" style={{ borderTop: '1px solid var(--border-glass)' }}>
        <div className="container">
          <ShakeSummon />
        </div>
      </section>

      {/* ===== PREMIUM PDF MERGER — HIGHLIGHTED STANDALONE ===== */}
      <section className="interactive-section" style={{ borderTop: '1px solid var(--border-glass)' }}>
        <div className="container">
          <div className="section-header" style={{ textAlign: 'center', marginBottom: '50px' }}>
            <ScrollReveal>
              <span className="section-badge badge-purple">Live Tool</span>
              <h2 className="section-title">Premium PDF Merger</h2>
              <p className="section-subtitle">
                Drop your actual PDF files below, reorder them, and merge into a single document — all processed locally in your browser. No upload to any server.
              </p>
            </ScrollReveal>
          </div>
          <ScrollReveal>
            <div className="pdf-highlight-card">
              <LivePdfMerger />
            </div>
          </ScrollReveal>
        </div>
      </section>

      {/* ===== SMART UTILITIES — INTERACTIVE WIDGETS ===== */}
      <section className="interactive-section" style={{ background: 'var(--bg-secondary)', borderTop: '1px solid var(--border-glass)' }}>
        <div className="container">
          <div className="section-header" style={{ textAlign: 'center', marginBottom: '50px' }}>
            <ScrollReveal>
              <span className="section-badge badge-emerald">Productivity Powerhouses</span>
              <h2 className="section-title">Smart Utilities</h2>
              <p className="section-subtitle">
                Try out FlyShelf&apos;s intelligent context-aware processors directly in your browser.
              </p>
            </ScrollReveal>
          </div>
          <div className="smart-widgets-grid" style={{ gridTemplateColumns: 'repeat(3, 1fr)' }}>
            <ScrollReveal className="widget-card" delay={100}>
              <div className="widget-card-header">
                <div>
                  <h3>⏱️ Speed Countdown</h3>
                  <p>Type &apos;/10s&apos; or &apos;/5s&apos; to trigger high-glow countdown.</p>
                </div>
                <span className="section-badge badge-cyan" style={{ marginBottom: 0 }}>Interactive</span>
              </div>
              <CountdownTimer />
            </ScrollReveal>

            <ScrollReveal className="widget-card" delay={150}>
              <div className="widget-card-header">
                <div>
                  <h3>🎨 Context Color Picker</h3>
                  <p>Type any HEX, RGB, or HSL code to preview and convert.</p>
                </div>
                <span className="section-badge badge-emerald" style={{ marginBottom: 0 }}>Interactive</span>
              </div>
              <ColorPicker />
            </ScrollReveal>

            <ScrollReveal className="widget-card" delay={200}>
              <div className="widget-card-header">
                <div>
                  <h3>🔍 URL Tracking Sanitizer</h3>
                  <p>Strip UTM &amp; analytics query markers instantly.</p>
                </div>
                <span className="section-badge badge-amber" style={{ marginBottom: 0 }}>Interactive</span>
              </div>
              <UtmSanitizer />
            </ScrollReveal>
          </div>
        </div>
      </section>

      {/* ===== ARCHITECTURE DIAGRAM ===== */}
      <section className="interactive-section" style={{ background: 'var(--bg-secondary)', borderTop: '1px solid var(--border-glass)' }}>
        <div className="container">
          <ScrollReveal>
            <div className="arch-card">
              <div className="arch-header">
                <span className="section-badge badge-cyan">Under the Hood</span>
                <h2>Three-Tier Dynamic Routing</h2>
                <p>FlyShelf auto-detects active network environments to securely dispatch files and text down the path of lowest latency.</p>
              </div>
              <div className="arch-grid">
                <div className="arch-node cyan">
                  <div className="arch-node-icon">📡</div>
                  <h3 className="arch-node-title">Layer 1: Local LAN P2P</h3>
                  <p className="arch-node-desc">Transfers massive files, documents, and screenshots directly over local WiFi/Ethernet channels at speeds exceeding 100+ Mbps. Completely offline.</p>
                </div>
                <div className="arch-node purple">
                  <div className="arch-node-icon">🔀</div>
                  <h3 className="arch-node-title">Layer 2: Cloudflare Tunnel</h3>
                  <p className="arch-node-desc">Leverages integrated Cloudflare Daemons to bypass strict firewalls and corporate NATs. Safely share folders between home and office without manual port forwarding.</p>
                </div>
                <div className="arch-node amber">
                  <div className="arch-node-icon">☁️</div>
                  <h3 className="arch-node-title">Layer 3: Firebase RTDB</h3>
                  <p className="arch-node-desc">High-speed, sub-millisecond cloud relay for short clipboard strings, links, and signaling payloads. Auto-purges logs every 5 minutes for absolute data confidentiality.</p>
                </div>
              </div>
            </div>
          </ScrollReveal>
        </div>
      </section>

      {/* ===== PROBLEM SECTION ===== */}
      <section className="section-padding" style={{ borderTop: '1px solid var(--border-glass)' }}>
        <div className="container">
          <div className="section-header" style={{ textAlign: 'center', marginBottom: '60px' }}>
            <ScrollReveal>
              <h2 className="section-title">The Clipboard Dilemma</h2>
              <p className="section-subtitle">
                Why standard Windows solutions and legacy clipboard managers leave you frustrated:
              </p>
            </ScrollReveal>
          </div>
          <div className="problem-grid">
            <ScrollReveal className="problem-card glass-panel" delay={100}>
              <span className="problem-icon">🤢</span>
              <h3>Outdated UI/UX</h3>
              <p>Ditto &amp; CopyQ are packed with power but feature eyesore, early-2000s desktop layouts that interrupt your modern workspace focus.</p>
            </ScrollReveal>
            <ScrollReveal className="problem-card glass-panel" delay={200}>
              <span className="problem-icon">❌</span>
              <h3>No Mobile Sync</h3>
              <p>Windows built-in clipboard (Win + V) restricts history to 25 text items, handles files poorly, and does not sync with your Android phone.</p>
            </ScrollReveal>
            <ScrollReveal className="problem-card glass-panel" delay={300}>
              <span className="problem-icon">🕵️</span>
              <h3>Privacy Failures</h3>
              <p>Most commercial clipboard managers require personal cloud accounts, store your sensitive copied content on third-party servers, or bundle telemetry.</p>
            </ScrollReveal>
          </div>
        </div>
      </section>

      {/* ===== PRIVACY SHIELD ===== */}
      <section className="interactive-section" style={{ borderTop: '1px solid var(--border-glass)' }}>
        <div className="container">
          <ScrollReveal>
            <div className="privacy-banner">
              <div className="privacy-shield-icon">🛡️</div>
              <div>
                <h3>Privacy First. No Clouds Retained.</h3>
                <p>Unlike modern SaaS tools, FlyShelf keeps your data yours. Large files and credentials transfer entirely peer-to-peer over your local networks. Cloud storage channels are reserved solely for signaling relays and are set with 5-minute auto-purge timeouts. No telemetry tracking, no ads, no databases.</p>
              </div>
            </div>
          </ScrollReveal>
        </div>
      </section>

      {/* ===== COMPARISON TABLE ===== */}
      <section className="section-padding" style={{ background: 'var(--bg-secondary)', borderTop: '1px solid var(--border-glass)' }}>
        <div className="container">
          <div className="section-header" style={{ textAlign: 'center', marginBottom: '60px' }}>
            <ScrollReveal>
              <h2 className="section-title">How FlyShelf Compares</h2>
              <p className="section-subtitle">
                A features matrix comparing FlyShelf with standard and classic clipboard tools:
              </p>
            </ScrollReveal>
          </div>
          <ScrollReveal className="comparison-table-wrapper glass-panel">
            <table className="comparison-table">
              <thead>
                <tr>
                  <th>Feature</th>
                  {competitors.map((c, i) => (
                    <th key={i} className={i === 0 ? 'highlight-col' : ''}>{c.name}</th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {[
                  { label: 'Interface Design', key: 'ui' },
                  { label: 'History Capacity', key: 'history' },
                  { label: 'Cross-device Sync', key: 'sync' },
                  { label: 'Raw File Transfers', key: 'transfer' },
                  { label: 'Built-in Utilities', key: 'tools' },
                ].map((row) => (
                  <tr key={row.key}>
                    <td><strong>{row.label}</strong></td>
                    {competitors.map((c, i) => (
                      <td key={i} className={i === 0 ? 'highlight-col' : ''}>{c[row.key]}</td>
                    ))}
                  </tr>
                ))}
              </tbody>
            </table>
          </ScrollReveal>
        </div>
      </section>

      {/* ===== STATS ===== */}
      <section className="section-padding">
        <div className="container">
          <div className="stats-grid">
            {stats.map((s, i) => (
              <ScrollReveal className="stat-card glass-panel" key={i} delay={i * 100}>
                <span className="stat-value gradient-text">{s.value}</span>
                <span className="stat-label">{s.label}</span>
                <span className="stat-desc">{s.desc}</span>
              </ScrollReveal>
            ))}
          </div>
        </div>
      </section>

      {/* ===== FINAL CTA ===== */}
      <section className="section-padding final-cta-section">
        <div className="container">
          <ScrollReveal className="final-cta-card glass-panel-active">
            <h2 className="gradient-text-rainbow">Upgrade Your Productivity Today</h2>
            <p>
              FlyShelf is free, open source, lightweight, and requires no user accounts. Take back control of your workflow, save hours of manual text copying, and sync files across devices safely.
            </p>
            <div className="cta-buttons">
              <Link href="/download" className="btn btn-primary">
                Download For Windows (PC)
              </Link>
              <Link href="/download" className="btn btn-green">
                Download Android APK
              </Link>
            </div>
          </ScrollReveal>
        </div>
      </section>

      <style jsx>{`
        /* Hero Styles */
        .hero-section {
          padding: 120px 0 60px;
          text-align: center;
          position: relative;
        }

        .hero-badge-container {
          display: flex;
          justify-content: center;
          margin-bottom: 24px;
        }

        .hero-badge {
          background: rgba(59, 130, 246, 0.1);
          border: 1px solid rgba(59, 130, 246, 0.2);
          color: var(--accent-cyan);
          padding: 6px 16px;
          border-radius: 99px;
          font-size: 13px;
          font-weight: 600;
          font-family: var(--font-heading);
        }

        .hero-title {
          font-size: 64px;
          font-weight: 800;
          line-height: 1.1;
          margin-bottom: 24px;
        }

        .hero-subtitle {
          font-size: 19px;
          color: var(--text-secondary);
          max-width: 700px;
          margin: 0 auto 40px;
          line-height: 1.6;
        }

        .hero-ctas {
          display: flex;
          gap: 16px;
          justify-content: center;
          margin-bottom: 60px;
        }

        .hero-mockup-wrapper {
          max-width: 800px;
          margin: 0 auto;
        }

        /* Desktop Mockup Frame */
        .mockup-frame {
          width: 100%;
          border-radius: 16px;
          overflow: hidden;
          border: 1px solid var(--border-glass);
          text-align: left;
          transition: all 0.5s cubic-bezier(0.16, 1, 0.3, 1);
        }

        .mockup-header {
          border-bottom: 1px solid var(--border-glass);
          padding: 14px 20px;
          display: flex;
          align-items: center;
          gap: 20px;
          transition: background 0.5s ease;
        }

        .mockup-dots { display: flex; gap: 6px; }
        .dot { width: 10px; height: 10px; border-radius: 99px; }
        .dot.red { background-color: var(--accent-red); }
        .dot.yellow { background-color: var(--accent-amber); }
        .dot.green { background-color: var(--accent-green); }

        .mockup-title {
          font-size: 12px;
          font-family: var(--font-mono);
          color: var(--text-muted);
          flex-grow: 1;
        }

        .mockup-search-container {
          background: rgba(0, 0, 0, 0.3);
          border: 1px solid var(--border-glass);
          padding: 5px 12px;
          border-radius: 6px;
          display: flex;
          align-items: center;
          gap: 8px;
          font-size: 11px;
          color: var(--text-muted);
          width: 240px;
        }

        .mockup-body {
          padding: 24px;
          display: flex;
          flex-direction: column;
          gap: 16px;
        }

        .mockup-card {
          border: 1px solid var(--border-glass);
          background: rgba(255, 255, 255, 0.01);
          border-radius: 12px;
          padding: 16px;
          transition: all 0.5s ease;
        }

        .card-top { display: flex; justify-content: space-between; margin-bottom: 12px; }

        .card-tag {
          font-size: 10px;
          font-family: var(--font-mono);
          font-weight: bold;
          padding: 2px 8px;
          border-radius: 4px;
          transition: all 0.5s ease;
        }

        .card-time { font-size: 10px; color: var(--text-muted); }

        .code-block {
          font-family: var(--font-mono);
          font-size: 12px;
          line-height: 1.5;
        }

        .image-placeholder {
          background: rgba(0, 0, 0, 0.2);
          border: 1px dashed var(--border-glass);
          border-radius: 8px;
          padding: 20px;
          display: flex;
          flex-direction: column;
          align-items: center;
          gap: 8px;
        }

        .img-icon { font-size: 24px; }
        .img-desc { font-size: 12px; color: var(--text-secondary); }

        .card-actions { display: flex; gap: 8px; margin-top: 12px; }

        .action-pill {
          font-size: 10px;
          background: rgba(255, 255, 255, 0.05);
          border: 1px solid var(--border-glass);
          padding: 2px 8px;
          border-radius: 4px;
          color: var(--text-secondary);
        }

        .card-text { font-size: 13px; transition: color 0.5s ease; }

        /* Section Headers */
        .section-header { text-align: center; margin-bottom: 60px; }
        .section-title { font-size: 38px; font-weight: 800; margin-bottom: 16px; }
        .section-subtitle { font-size: 16px; color: var(--text-secondary); max-width: 600px; margin: 0 auto; }

        /* Problem Grid */
        .problem-grid { display: grid; grid-template-columns: repeat(3, 1fr); gap: 24px; }
        .problem-card { padding: 35px; text-align: center; display: flex; flex-direction: column; align-items: center; gap: 16px; }
        .problem-icon { font-size: 40px; }
        .problem-card h3 { font-size: 18px; color: var(--text-primary); }
        .problem-card p { font-size: 14px; color: var(--text-secondary); line-height: 1.6; }

        /* Comparison Table */
        .comparison-table-wrapper { width: 100%; overflow-x: auto; border-radius: 16px; }
        .comparison-table { width: 100%; border-collapse: collapse; text-align: left; font-size: 14px; }
        .comparison-table th, .comparison-table td { padding: 18px 24px; border-bottom: 1px solid var(--border-glass); }
        .comparison-table th { font-family: var(--font-heading); font-weight: 700; color: var(--text-primary); background: rgba(255, 255, 255, 0.01); }
        .comparison-table td { color: var(--text-secondary); }
        .highlight-col { background: rgba(59, 130, 246, 0.03) !important; color: var(--text-primary) !important; }

        /* Stats Grid */
        .stats-grid { display: grid; grid-template-columns: repeat(4, 1fr); gap: 20px; }
        .stat-card { padding: 30px; text-align: center; display: flex; flex-direction: column; gap: 8px; }
        .stat-value { font-family: var(--font-heading); font-size: 48px; font-weight: 800; line-height: 1; }
        .stat-label { font-size: 14px; font-weight: 600; color: var(--text-primary); }
        .stat-desc { font-size: 11px; color: var(--text-muted); }

        /* Final CTA */
        .final-cta-card { text-align: center; padding: 60px 40px; display: flex; flex-direction: column; align-items: center; gap: 24px; max-width: 800px; margin: 0 auto; }
        .final-cta-card h2 { font-size: 38px; font-weight: 800; }
        .final-cta-card p { font-size: 16px; color: var(--text-secondary); line-height: 1.6; max-width: 600px; }
        .cta-buttons { display: flex; flex-wrap: wrap; gap: 16px; justify-content: center; }

        /* PDF Highlight Card */
        .pdf-highlight-card {
          max-width: 640px;
          margin: 0 auto;
          background: rgba(255, 255, 255, 0.03);
          border: 1px solid rgba(139, 92, 246, 0.15);
          border-radius: 20px;
          padding: 36px;
          box-shadow: 0 8px 30px rgba(0, 0, 0, 0.3), 0 0 20px rgba(139, 92, 246, 0.06);
          backdrop-filter: blur(12px);
        }

        /* Responsiveness */
        @media (max-width: 1024px) {
          .problem-grid { grid-template-columns: 1fr; }
          .hero-title { font-size: 48px; }
          .stats-grid { grid-template-columns: repeat(2, 1fr); }
        }

        @media (max-width: 768px) {
          .stats-grid { grid-template-columns: 1fr; }
          .hero-ctas { flex-direction: column; align-items: stretch; max-width: 300px; margin-left: auto; margin-right: auto; }
          .mockup-search-container { display: none; }
        }
      `}</style>
    </>
  );
}
