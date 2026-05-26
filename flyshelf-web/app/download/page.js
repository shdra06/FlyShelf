"use client";

import ScrollReveal from '../components/ScrollReveal';

export default function DownloadPage() {
  return (
    <div className="container" style={{ padding: '80px 24px' }}>
      {/* HEADER */}
      <ScrollReveal className="download-header">
        <span className="download-badge">🚀 Secure Downloads</span>
        <h1 className="gradient-text-rainbow">Get FlyShelf For Your Devices</h1>
        <p className="download-subtitle">
          Enjoy premium cross-device sync. No accounts, no paywalls, entirely open source. Downloads are signed and direct.
        </p>
      </ScrollReveal>

      {/* PLATFORMS GRID */}
      <div className="download-grid">
        <ScrollReveal className="download-card glass-panel" delay={100}>
          <div className="card-platform">
            <span className="platform-icon">💻</span>
            <div>
              <h3>Windows Client</h3>
              <span className="platform-os">Windows 10 / 11 (64-bit)</span>
            </div>
          </div>
          <p className="card-desc">
            Single-file self-contained application built with C# and WPF (.NET 10). Runs locally inside your taskbar, providing global summoned clipboard shortcuts.
          </p>
          <div className="card-meta">
            <span><strong>Version:</strong> v7.0.0</span>
            <span><strong>Size:</strong> ~45 MB</span>
          </div>
          <a
            href="https://github.com/shdra06/FlyShelf/releases/download/v7.0.0/FlyShelf.exe"
            className="btn btn-primary download-btn"
          >
            Download for Windows (.exe)
          </a>
          <div className="checksum-box">
            <span>SHA-256: <code>a1b2c3d4e5f6...</code></span>
          </div>
        </ScrollReveal>

        <ScrollReveal className="download-card glass-panel" delay={200}>
          <div className="card-platform">
            <span className="platform-icon">📱</span>
            <div>
              <h3>Android Application</h3>
              <span className="platform-os">Android 8.0+ (ARM64)</span>
            </div>
          </div>
          <p className="card-desc">
            Lightweight Android package with persistent foreground sync services, accelerative summon triggers, and interactive floating overlay bubbles.
          </p>
          <div className="card-meta">
            <span><strong>Version:</strong> v7.0.0</span>
            <span><strong>Size:</strong> ~15 MB</span>
          </div>
          <a
            href="https://github.com/shdra06/FlyShelf/releases/download/v7.0.0/FlyShelf_Mobile.apk"
            className="btn btn-green download-btn"
          >
            Download Android APK
          </a>
          <div className="checksum-box">
            <span>SHA-256: <code>f6e5d4c3b2a1...</code></span>
          </div>
        </ScrollReveal>
      </div>

      {/* INSTALL INSTRUCTIONS */}
      <ScrollReveal className="install-guide glass-panel" delay={300}>
        <h2>Setup Walkthrough</h2>
        <div className="guide-steps">
          <div className="step">
            <span className="step-num">1</span>
            <h4>Download Binaries</h4>
            <p>Download the PC <code>.exe</code> and Android <code>.apk</code> packages above on your respective devices.</p>
          </div>
          <div className="step">
            <span className="step-num">2</span>
            <h4>Launch & Install</h4>
            <p>Windows: Simply double-click the executable to launch. Android: Enable &quot;Install from Unknown Sources&quot; and open the APK.</p>
          </div>
          <div className="step">
            <span className="step-num">3</span>
            <h4>Pair & Sync</h4>
            <p>Launch both clients. Scan the pairing QR code on the desktop window from your phone, and enjoy instant sync!</p>
          </div>
        </div>
      </ScrollReveal>

      {/* FOOTER VERIFICATION */}
      <ScrollReveal className="requirements-section" delay={350}>
        <h3>System Requirements</h3>
        <div className="requirements-grid">
          <div className="req-col">
            <h4>Windows</h4>
            <ul>
              <li>Windows 10 (Build 19041+) or Windows 11</li>
              <li>Dual-Core CPU, 2 GB RAM available</li>
              <li>Direct WiFi connection for Local LAN P2P</li>
            </ul>
          </div>
          <div className="req-col">
            <h4>Android</h4>
            <ul>
              <li>Android Oreo (8.0) or higher</li>
              <li>ARM64-v8a architecture device</li>
              <li>Overlay drawing permission enabled</li>
            </ul>
          </div>
        </div>
      </ScrollReveal>

      <style jsx>{`
        .download-header {
          text-align: center;
          margin-bottom: 50px;
        }

        .download-badge {
          background: rgba(6, 182, 212, 0.1);
          border: 1px solid rgba(6, 182, 212, 0.2);
          color: var(--accent-cyan);
          padding: 6px 16px;
          border-radius: 99px;
          font-size: 13px;
          font-weight: 600;
          display: inline-block;
          margin-bottom: 16px;
        }

        .download-header h1 {
          font-size: 42px;
          margin-bottom: 16px;
        }

        .download-subtitle {
          font-size: 17px;
          color: var(--text-secondary);
          max-width: 600px;
          margin: 0 auto;
          line-height: 1.6;
        }

        /* Platform Download Grid */
        .download-grid {
          display: grid;
          grid-template-columns: repeat(2, 1fr);
          gap: 30px;
          margin-bottom: 60px;
        }

        .download-card {
          padding: 40px;
          display: flex;
          flex-direction: column;
          gap: 20px;
          justify-content: space-between;
        }

        .card-platform {
          display: flex;
          align-items: center;
          gap: 16px;
        }

        .platform-icon {
          font-size: 44px;
        }

        .card-platform h3 {
          font-size: 22px;
          color: var(--text-primary);
        }

        .platform-os {
          font-size: 13px;
          color: var(--text-muted);
        }

        .card-desc {
          font-size: 14.5px;
          color: var(--text-secondary);
          line-height: 1.6;
          flex-grow: 1;
        }

        .card-meta {
          display: flex;
          justify-content: space-between;
          font-size: 13px;
          color: var(--text-muted);
          border-top: 1px solid var(--border-glass);
          padding-top: 15px;
        }

        .download-btn {
          width: 100%;
          padding: 14px;
          font-size: 16px;
        }

        .checksum-box {
          font-size: 11px;
          font-family: var(--font-mono);
          color: var(--text-muted);
          background: rgba(0, 0, 0, 0.2);
          padding: 6px 12px;
          border-radius: 6px;
          border: 1px solid var(--border-glass);
          text-align: center;
        }

        /* Setup Walkthrough */
        .install-guide {
          padding: 50px 40px;
          margin-bottom: 60px;
        }

        .install-guide h2 {
          text-align: center;
          margin-bottom: 40px;
          font-size: 28px;
        }

        .guide-steps {
          display: grid;
          grid-template-columns: repeat(3, 1fr);
          gap: 30px;
        }

        .step {
          text-align: center;
          display: flex;
          flex-direction: column;
          align-items: center;
          gap: 12px;
        }

        .step-num {
          background: linear-gradient(135deg, var(--accent-blue), var(--accent-cyan));
          color: #ffffff;
          width: 36px;
          height: 36px;
          border-radius: 99px;
          display: flex;
          align-items: center;
          justify-content: center;
          font-weight: 700;
          font-size: 16px;
        }

        .step h4 { font-size: 16px; color: var(--text-primary); }
        .step p { font-size: 13.5px; color: var(--text-secondary); line-height: 1.5; }

        /* Requirements */
        .requirements-section {
          text-align: center;
          margin-top: 40px;
        }

        .requirements-section h3 {
          font-size: 20px;
          margin-bottom: 24px;
          color: var(--text-primary);
        }

        .requirements-grid {
          display: grid;
          grid-template-columns: repeat(2, 1fr);
          gap: 40px;
          text-align: left;
          max-width: 800px;
          margin: 0 auto;
        }

        .req-col {
          background: rgba(255, 255, 255, 0.01);
          border: 1px solid var(--border-glass);
          border-radius: 12px;
          padding: 24px;
        }

        .req-col h4 { font-size: 15px; margin-bottom: 12px; color: var(--accent-cyan); }
        .req-col ul { list-style: none; display: flex; flex-direction: column; gap: 8px; }
        .req-col li { font-size: 13px; color: var(--text-secondary); padding-left: 14px; position: relative; }
        .req-col li::before { content: '•'; color: var(--text-muted); position: absolute; left: 0; }

        @media (max-width: 900px) {
          .download-grid {
            grid-template-columns: 1fr;
          }
          .guide-steps {
            grid-template-columns: 1fr;
            gap: 40px;
          }
          .requirements-grid {
            grid-template-columns: 1fr;
          }
        }
      `}</style>
    </div>
  );
}
