"use client";

import Link from 'next/link';

export default function Footer() {
  const currentYear = new Date().getFullYear();

  return (
    <footer className="footer">
      <div className="container">
        <div className="footer-grid">
          <div className="footer-brand">
            <Link href="/" className="footer-logo">
              <span className="logo-icon">⚡</span>
              <span className="logo-text">Fly<span className="logo-accent">Shelf</span></span>
            </Link>
            <p className="footer-desc">
              The ultimate premium cross-device clipboard manager & syncing engine. Copy on Windows, paste on Android instantly. Safe, secure, and lightning fast.
            </p>
            <div className="tech-badges">
              <span className="tech-badge wpf">.NET / WPF</span>
              <span className="tech-badge rn">React Native</span>
              <span className="tech-badge kotlin">Kotlin</span>
            </div>
          </div>

          <div className="footer-column">
            <h4>Product</h4>
            <ul>
              <li><Link href="/">Home</Link></li>
              <li><Link href="/features">Features</Link></li>
              <li><Link href="/download">Download</Link></li>
              <li><a href="https://github.com/shdra06/FlyShelf" target="_blank" rel="noopener noreferrer">Source Code</a></li>
            </ul>
          </div>


          <div className="footer-column">
            <h4>Security & Privacy</h4>
            <ul>
              <li><Link href="/privacy">Privacy Policy</Link></li>
              <li><a href="https://github.com/shdra06/FlyShelf" target="_blank" rel="noopener noreferrer">GitHub Issues</a></li>
              <li><a href="mailto:shdra06@gmail.com">Contact Developer</a></li>
            </ul>
          </div>
        </div>

        <div className="footer-bottom">
          <p>© {currentYear} FlyShelf. Open source under MIT License. Crafted with ❤️ by Shivendra.</p>
          <div className="footer-bottom-links">
            <Link href="/privacy">Privacy</Link>
            <a href="https://github.com/shdra06/FlyShelf" target="_blank" rel="noopener noreferrer">GitHub</a>
          </div>
        </div>
      </div>

      <style jsx>{`
        .footer-logo {
          display: inline-flex;
          align-items: center;
          gap: 10px;
          font-family: var(--font-heading);
          font-weight: 800;
          font-size: 20px;
          margin-bottom: 20px;
        }

        .logo-icon {
          font-size: 22px;
        }

        .logo-accent {
          background: linear-gradient(135deg, var(--accent-blue), var(--accent-cyan));
          background-clip: text;
          -webkit-background-clip: text;
          -webkit-text-fill-color: transparent;
        }

        .footer-desc {
          color: var(--text-secondary);
          font-size: 14px;
          line-height: 1.6;
          margin-bottom: 24px;
          max-width: 320px;
        }

        .tech-badges {
          display: flex;
          flex-wrap: wrap;
          gap: 8px;
        }

        .tech-badge {
          font-size: 11px;
          font-family: var(--font-mono);
          font-weight: 500;
          padding: 4px 10px;
          border-radius: 999px;
          background: rgba(255, 255, 255, 0.04);
          border: 1px solid var(--border-glass);
          color: var(--text-secondary);
        }

        .tech-badge.wpf {
          color: #512bd4;
          border-color: rgba(81, 43, 212, 0.2);
          background: rgba(81, 43, 212, 0.05);
        }

        .tech-badge.rn {
          color: #61dafb;
          border-color: rgba(97, 218, 251, 0.2);
          background: rgba(97, 218, 251, 0.05);
        }

        .tech-badge.kotlin {
          color: #f88909;
          border-color: rgba(248, 137, 9, 0.2);
          background: rgba(248, 137, 9, 0.05);
        }

        .footer-column h4 {
          font-size: 16px;
          font-weight: 600;
          margin-bottom: 20px;
          color: var(--text-primary);
        }

        .footer-column ul {
          list-style: none;
          display: flex;
          flex-direction: column;
          gap: 12px;
        }

        .footer-column a {
          color: var(--text-secondary);
          font-size: 14px;
          transition: color var(--transition-fast);
        }

        .footer-column a:hover {
          color: var(--text-primary);
        }

        .footer-bottom {
          margin-top: 60px;
          padding-top: 30px;
          border-top: 1px solid var(--border-glass);
          display: flex;
          justify-content: space-between;
          align-items: center;
          font-size: 13px;
          color: var(--text-muted);
        }

        .footer-bottom-links {
          display: flex;
          gap: 20px;
        }

        .footer-bottom-links a {
          transition: color var(--transition-fast);
        }

        .footer-bottom-links a:hover {
          color: var(--text-primary);
        }

        @media (max-width: 768px) {
          .footer-bottom {
            flex-direction: column;
            gap: 15px;
            text-align: center;
          }
        }
      `}</style>
    </footer>
  );
}
