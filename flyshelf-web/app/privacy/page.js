"use client";

import ScrollReveal from '../components/ScrollReveal';

export default function PrivacyPage() {
  return (
    <div className="container" style={{ padding: '80px 24px' }}>
      <ScrollReveal>
        <div className="privacy-header">
          <span className="privacy-badge">🔒 Privacy First Architecture</span>
          <h1 className="gradient-text-rainbow">FlyShelf Privacy Policy</h1>
          <p className="privacy-meta">
            <strong>Last Updated:</strong> May 23, 2026 | <strong>Developer:</strong> Shivendra
          </p>
        </div>
      </ScrollReveal>

      <div className="privacy-grid">
        <ScrollReveal className="privacy-sidebar glass-panel" delay={100}>
          <h3>Table of Contents</h3>
          <ul>
            <li><a href="#intro">1. Introduction</a></li>
            <li><a href="#data-access">2. Data We Access</a></li>
            <li><a href="#device-sharing">3. How Data is Shared</a></li>
            <li><a href="#no-collect">4. What We Do NOT Collect</a></li>
            <li><a href="#security">5. Data Storage & Security</a></li>
            <li><a href="#third-party">6. Third-Party Services</a></li>
            <li><a href="#rights">7. Your Rights & Deletion</a></li>
          </ul>
        </ScrollReveal>

        <ScrollReveal className="privacy-content glass-panel" delay={200}>
          <section id="intro">
            <h2>1. Introduction</h2>
            <p>
              FlyShelf (&quot;the App&quot;) is a cross-device clipboard manager that syncs clipboard content between your personal devices. This privacy policy explains what data FlyShelf accesses, how it is stored, how it is shared, and how you can control or delete it.
            </p>
            <p>
              <strong>FlyShelf is designed with a privacy-first architecture.</strong> The App does not collect analytics, does not track users, and does not sell or share personal data with third parties.
            </p>
          </section>

          <section id="data-access">
            <h2>2. Data We Access</h2>
            <h3>2.1 Clipboard Content</h3>
            <p>
              FlyShelf monitors your Windows clipboard to capture text, images, files, and URLs that you copy. This is the core functionality of the App.
            </p>
            <ul className="dot-list">
              <li><strong>Storage:</strong> All clipboard data is stored <strong>locally</strong> on your device in <code>%AppData%\FlyShelf\</code>.</li>
              <li><strong>Retention:</strong> Configurable retention period (default: 7 days). You can set it to 1, 7, 14, or 30 days, or disable auto-cleanup entirely.</li>
              <li><strong>Capacity:</strong> Up to 500 clipboard items are stored. A warning appears at 150 items.</li>
            </ul>

            <h3>2.2 Device & Network Information</h3>
            <p>
              To establish connections, FlyShelf stores a randomly generated Device ID (e.g. <code>PC_a1b2c3</code>) and your local IP addresses. These are used only for device discovery and connection signaling.
            </p>
          </section>

          <section id="device-sharing">
            <h2>3. How Data Is Shared Between Your Devices</h2>
            <p>
              When you pair devices using QR codes or pairing codes:
            </p>
            <ul className="dot-list">
              <li><strong>Peer-to-Peer Sync:</strong> Clipboard content is transferred <strong>directly</strong> between your devices via encrypted peer-to-peer connections. All data in transit is encrypted using <strong>AES-256-GCM</strong> with keys derived from your pairing secret.</li>
              <li><strong>Firebase Signaling:</strong> Firebase is used as a signaling server for device discovery only. Clipboard content is <strong>never</strong> stored in Firebase.</li>
              <li><strong>Cloudflare Tunnels:</strong> Optional tunnels enable remote P2P sync. All data is encrypted end-to-end; Cloudflare cannot read your traffic.</li>
            </ul>
          </section>

          <section id="no-collect">
            <h2>4. What We Do NOT Collect</h2>
            <p>
              FlyShelf does <strong>NOT</strong>:
            </p>
            <ul className="cross-list">
              <li>❌ Collect analytics or usage telemetry</li>
              <li>❌ Track user behavior or browsing habits</li>
              <li>❌ Send clipboard content to any server</li>
              <li>❌ Store clipboard content in the cloud</li>
              <li>❌ Require account creation</li>
            </ul>
          </section>

          <section id="security">
            <h2>5. Data Storage & Security</h2>
            <p>
              All sensitive credentials (such as your Google Gemini API key or Firebase Auth Token) are stored on your local drive and encrypted using <strong>Windows DPAPI</strong>.
            </p>
          </section>

          <section id="third-party">
            <h2>6. Third-Party Services</h2>
            <p>
              FlyShelf relies on trusted third-party components for specific utility tasks:
            </p>
            <ul className="dot-list">
              <li><strong>Firebase (Google):</strong> Used for anonymous auth and device pairing signaling only.</li>
              <li><strong>Google Gemini API (Optional):</strong> Used only when you trigger AI table extraction on clipboard images.</li>
              <li><strong>Cloudflare:</strong> Used for optional remote P2P tunnel connections.</li>
            </ul>
          </section>

          <section id="rights">
            <h2>7. Your Rights & Data Deletion</h2>
            <p>
              Since all data is stored locally, you have total control over it:
            </p>
            <ul className="dot-list">
              <li><strong>View:</strong> Browse your clipboard cache at <code>%AppData%\FlyShelf\</code>.</li>
              <li><strong>Clear:</strong> Use the &quot;Clear All&quot; button in the Desktop dashboard to scrub your history instantly.</li>
              <li><strong>Uninstall:</strong> Deleting the app removes all local content.</li>
            </ul>
          </section>
        </ScrollReveal>
      </div>

      <style jsx>{`
        .privacy-header {
          text-align: center;
          margin-bottom: 50px;
        }

        .privacy-badge {
          background: rgba(16, 185, 129, 0.1);
          border: 1px solid rgba(16, 185, 129, 0.2);
          color: var(--accent-green);
          padding: 6px 16px;
          border-radius: 99px;
          font-size: 13px;
          font-weight: 600;
          margin-bottom: 16px;
          display: inline-block;
        }

        .privacy-header h1 {
          font-size: 42px;
          margin-bottom: 12px;
        }

        .privacy-meta {
          font-size: 14px;
          color: var(--text-muted);
        }

        .privacy-grid {
          display: grid;
          grid-template-columns: 280px 1fr;
          gap: 30px;
          align-items: start;
        }

        .privacy-sidebar {
          padding: 24px;
          position: sticky;
          top: 100px;
        }

        .privacy-sidebar h3 {
          font-size: 16px;
          margin-bottom: 16px;
        }

        .privacy-sidebar ul {
          list-style: none;
          display: flex;
          flex-direction: column;
          gap: 10px;
        }

        .privacy-sidebar a {
          color: var(--text-secondary);
          font-size: 14px;
          transition: color var(--transition-fast);
        }

        .privacy-sidebar a:hover {
          color: var(--accent-blue);
        }

        .privacy-content {
          padding: 40px;
          display: flex;
          flex-direction: column;
          gap: 40px;
        }

        .privacy-content h2 {
          font-size: 24px;
          margin-bottom: 16px;
          border-bottom: 1px solid var(--border-glass);
          padding-bottom: 8px;
          color: var(--text-primary);
        }

        .privacy-content h3 {
          font-size: 18px;
          margin: 16px 0 8px;
          color: var(--text-primary);
        }

        .privacy-content p {
          font-size: 15px;
          color: var(--text-secondary);
          line-height: 1.6;
          margin-bottom: 16px;
        }

        .dot-list, .cross-list {
          list-style: none;
          display: flex;
          flex-direction: column;
          gap: 8px;
          margin-bottom: 16px;
          padding-left: 10px;
        }

        .dot-list li {
          font-size: 14px;
          color: var(--text-secondary);
          position: relative;
          padding-left: 15px;
        }

        .dot-list li::before {
          content: '•';
          color: var(--accent-blue);
          position: absolute;
          left: 0;
          font-weight: bold;
        }

        .cross-list li {
          font-size: 14px;
          color: var(--text-secondary);
        }

        @media (max-width: 900px) {
          .privacy-grid {
            grid-template-columns: 1fr;
          }
          .privacy-sidebar {
            position: relative;
            top: 0;
          }
        }
      `}</style>
    </div>
  );
}
