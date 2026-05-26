"use client";

import ScrollReveal from '../components/ScrollReveal';
import Link from 'next/link';

export default function FeaturesPage() {
  const categories = [
    {
      title: 'Real-Time Sync Engine',
      icon: '🔄',
      desc: 'FlyShelf implements a smart 3-tier sync architecture to ensure zero latency and maximum privacy.',
      items: [
        'Direct LAN (P2P): Send heavy files and large images between devices on the same WiFi at 100+ Mbps.',
        'Cloudflare Tunnel: Securely bypass firewalls and home NATs without storing content in the cloud.',
        'Firebase signaling: Relays instant clipboard changes in milliseconds securely using anonymous keys.'
      ]
    },
    {
      title: 'Android Mobile Overlay',
      icon: '📱',
      desc: 'Access your shelf workspace from any mobile application using our custom Android companion features.',
      items: [
        'Foreground Sync Service: Highly optimized background service designed to survive aggressive battery cleaners and RAM managers.',
        'Physics Overlay Ball: Access clipboard history list with a simple double-shake gesture or custom floating bubble.',
        'Single-Tap Paste: Select any clip from the overlay bubble and paste it immediately into the active text field.'
      ]
    },
    {
      title: 'WPF Mica Dashboard',
      icon: '🖥️',
      desc: 'A premium Windows client built in WPF (.NET 10) featuring modern design guidelines.',
      items: [
        'Summon Dashboard: Alt + C or Win + Shift + V triggers a glassmorphic window right under your cursor.',
        'Chevron Action Pills: Action pills reveal themselves on card hover with beautiful animation curves.',
        'Arrow-Key Navigation: Seamlessly search, select, scroll, and copy using only keyboard arrows.'
      ]
    },
    {
      title: 'Power Productivity Tools',
      icon: '🛠️',
      desc: 'Stop switching between online converter websites. Do everything directly from your desktop shelf.',
      items: [
        'AI Table OCR: Capture a screenshot of a table, right-click, and let Google Gemini convert it into a clean markdown table.',
        'Bulk PDF Merger: Drag multiple PDF documents, text clips, or pictures onto your shelf, and merge them with a single click.',
        'Format Converter: Instantly compress clipboard images into lightweight WebPs, PNGs, or JPEGs in a blink.'
      ]
    }
  ];

  return (
    <div className="container" style={{ padding: '80px 24px' }}>
      {/* HEADER */}
      <ScrollReveal className="features-header">
        <span className="features-badge">⚡ Advanced Capabilities</span>
        <h1 className="gradient-text-rainbow">Engineered For Creators & Power Users</h1>
        <p className="features-subtitle">
          Discover why FlyShelf is more than just a clipboard history manager. Explore our custom syncing architecture and productivity suite.
        </p>
      </ScrollReveal>

      {/* CORE SYNC VISUAL */}
      <ScrollReveal className="sync-architecture-section glass-panel" delay={100}>
        <div className="architecture-grid">
          <div className="architecture-text">
            <h2>Three-Tier Transport Architecture</h2>
            <p>
              FlyShelf is engineered for maximum speed and absolute privacy. Our custom transport layer dynamically analyzes your network topologies to route files and clipboard text over the fastest secure lane available:
            </p>
            <ul className="feature-list">
              <li><strong>Local LAN:</strong> Heavy assets like media and files are sent directly between devices at local router speeds.</li>
              <li><strong>Cloudflare Relay:</strong> Ephemeral AES-256 encrypted lanes securely route data when devices are on separate networks.</li>
              <li><strong>Firebase RTDB:</strong> Low-overhead signaling channels sync text clips in milliseconds.</li>
            </ul>
          </div>
          <div className="architecture-diagram-container">
            <div className="diagram-card glass-panel">
              <span className="node-icon">💻</span>
              <h4>Windows PC</h4>
              <span className="node-desc">FlyShelf.exe</span>
            </div>
            <div className="diagram-connector">
              <span className="connector-line"></span>
              <span className="connector-text">LAN / P2P (WiFi)</span>
              <span className="connector-line"></span>
            </div>
            <div className="diagram-card glass-panel">
              <span className="node-icon">📱</span>
              <h4>Android Phone</h4>
              <span className="node-desc">FlyShelf APK</span>
            </div>
          </div>
        </div>
      </ScrollReveal>

      {/* DETAILED CATEGORIES */}
      <div className="features-categories-grid">
        {categories.map((cat, index) => (
          <ScrollReveal className="category-card glass-panel" key={index} delay={index * 100}>
            <div className="category-top">
              <span className="category-icon">{cat.icon}</span>
              <h3>{cat.title}</h3>
            </div>
            <p className="category-desc">{cat.desc}</p>
            <ul className="category-bullets">
              {cat.items.map((item, idx) => (
                <li key={idx}>{item}</li>
              ))}
            </ul>
          </ScrollReveal>
        ))}
      </div>

      {/* FOOTER CTA */}
      <ScrollReveal className="features-footer-cta glass-panel" delay={200}>
        <h2>Experience the Speed of FlyShelf</h2>
        <p>No subscriptions, no trackers, completely free. Install FlyShelf on Windows and Android in seconds.</p>
        <div className="features-cta-buttons">
          <Link href="/download" className="btn btn-primary">Download Free</Link>
          <Link href="/download" className="btn btn-secondary">Get Android APK</Link>
        </div>
      </ScrollReveal>

      <style jsx>{`
        .features-header {
          text-align: center;
          margin-bottom: 60px;
        }

        .features-badge {
          background: rgba(59, 130, 246, 0.1);
          border: 1px solid rgba(59, 130, 246, 0.2);
          color: var(--accent-blue);
          padding: 6px 16px;
          border-radius: 99px;
          font-size: 13px;
          font-weight: 600;
          display: inline-block;
          margin-bottom: 16px;
        }

        .features-header h1 {
          font-size: 42px;
          margin-bottom: 16px;
        }

        .features-subtitle {
          font-size: 17px;
          color: var(--text-secondary);
          max-width: 700px;
          margin: 0 auto;
          line-height: 1.6;
        }

        /* Architecture Section */
        .sync-architecture-section {
          padding: 40px;
          margin-bottom: 60px;
        }

        .architecture-grid {
          display: grid;
          grid-template-columns: 1.2fr 1fr;
          gap: 40px;
          align-items: center;
        }

        .architecture-text h2 {
          font-size: 28px;
          margin-bottom: 16px;
          color: var(--text-primary);
        }

        .architecture-text p {
          color: var(--text-secondary);
          font-size: 15px;
          line-height: 1.6;
          margin-bottom: 24px;
        }

        .architecture-diagram-container {
          display: flex;
          flex-direction: column;
          align-items: center;
          gap: 20px;
          position: relative;
        }

        .diagram-card {
          width: 180px;
          padding: 20px;
          text-align: center;
          display: flex;
          flex-direction: column;
          align-items: center;
          gap: 8px;
        }

        .node-icon { font-size: 32px; }
        .diagram-card h4 { font-size: 15px; color: var(--text-primary); }
        .node-desc { font-size: 11px; color: var(--text-muted); font-family: var(--font-mono); }

        .diagram-connector {
          display: flex;
          align-items: center;
          gap: 12px;
          color: var(--accent-blue);
          font-family: var(--font-mono);
          font-size: 11px;
          width: 100%;
          justify-content: center;
        }

        .connector-line {
          height: 1px;
          background: linear-gradient(90deg, transparent, var(--accent-blue), transparent);
          flex-grow: 1;
        }

        /* Categories Grid */
        .features-categories-grid {
          display: grid;
          grid-template-columns: repeat(2, 1fr);
          gap: 24px;
          margin-bottom: 60px;
        }

        .category-card {
          padding: 35px;
          display: flex;
          flex-direction: column;
          gap: 16px;
        }

        .category-top {
          display: flex;
          align-items: center;
          gap: 12px;
        }

        .category-icon { font-size: 28px; }
        .category-card h3 { font-size: 20px; color: var(--text-primary); }

        .category-desc {
          font-size: 14px;
          color: var(--text-secondary);
          line-height: 1.6;
        }

        .category-bullets {
          list-style: none;
          display: flex;
          flex-direction: column;
          gap: 10px;
        }

        .category-bullets li {
          font-size: 13.5px;
          color: var(--text-secondary);
          line-height: 1.5;
          position: relative;
          padding-left: 20px;
        }

        .category-bullets li::before {
          content: '→';
          color: var(--accent-cyan);
          position: absolute;
          left: 0;
          font-weight: bold;
        }

        /* Footer CTA */
        .features-footer-cta {
          text-align: center;
          padding: 50px 30px;
          display: flex;
          flex-direction: column;
          align-items: center;
          gap: 20px;
        }

        .features-footer-cta h2 { font-size: 32px; }
        .features-footer-cta p { color: var(--text-secondary); font-size: 15px; }
        .features-cta-buttons { display: flex; gap: 16px; }

        @media (max-width: 900px) {
          .architecture-grid {
            grid-template-columns: 1fr;
          }
          .features-categories-grid {
            grid-template-columns: 1fr;
          }
        }
      `}</style>
    </div>
  );
}
