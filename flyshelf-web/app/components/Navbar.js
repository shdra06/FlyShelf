"use client";

import { useState, useEffect } from 'react';
import Link from 'next/link';
import { usePathname } from 'next/navigation';

export default function Navbar() {
  const [scrolled, setScrolled] = useState(false);
  const [mobileMenuOpen, setMobileMenuOpen] = useState(false);
  const pathname = usePathname();

  useEffect(() => {
    const handleScroll = () => {
      if (window.scrollY > 20) {
        setScrolled(true);
      } else {
        setScrolled(false);
      }
    };
    window.addEventListener('scroll', handleScroll);
    return () => window.removeEventListener('scroll', handleScroll);
  }, []);

  const navLinks = [
    { name: 'Home', path: '/' },
    { name: 'Features', path: '/features' },
    { name: 'Download', path: '/download' },
  ];

  return (
    <>
      <nav className={`navbar ${scrolled ? 'navbar-scrolled' : ''}`}>
        <div className="navbar-container">
          <Link href="/" className="nav-logo">
            <span className="logo-icon">⚡</span>
            <span className="logo-text">Fly<span className="logo-accent">Shelf</span></span>
          </Link>

          <div className="nav-links-desktop">
            {navLinks.map((link) => {
              const isActive = pathname === link.path;
              return (
                <Link
                  key={link.path}
                  href={link.path}
                  className={`nav-link ${isActive ? 'active' : ''}`}
                >
                  {link.name}
                  {isActive && <span className="nav-indicator" />}
                </Link>
              );
            })}
            <Link href="/download" className="btn btn-primary" style={{ padding: '8px 20px', fontSize: '14px' }}>
              Download Free
            </Link>
          </div>

          <button 
            className={`mobile-menu-toggle ${mobileMenuOpen ? 'open' : ''}`} 
            onClick={() => setMobileMenuOpen(!mobileMenuOpen)}
            aria-label="Toggle menu"
          >
            <span className="hamburger-line" />
            <span className="hamburger-line" />
            <span className="hamburger-line" />
          </button>
        </div>
      </nav>

      {/* Mobile Drawer Overlay */}
      <div className={`mobile-menu-drawer ${mobileMenuOpen ? 'active' : ''}`}>
        <div className="mobile-drawer-links">
          {navLinks.map((link) => {
            const isActive = pathname === link.path;
            return (
              <Link
                key={link.path}
                href={link.path}
                className={`mobile-nav-link ${isActive ? 'active' : ''}`}
                onClick={() => setMobileMenuOpen(false)}
              >
                {link.name}
              </Link>
            );
          })}
          <Link 
            href="/download" 
            className="btn btn-primary" 
            style={{ width: '100%', marginTop: '20px' }}
            onClick={() => setMobileMenuOpen(false)}
          >
            Download Free
          </Link>
        </div>
      </div>

      <style jsx>{`
        .navbar {
          position: fixed;
          top: 0;
          left: 0;
          right: 0;
          height: 80px;
          z-index: 100;
          transition: all 0.3s cubic-bezier(0.16, 1, 0.3, 1);
          border-bottom: 1px solid transparent;
        }

        .navbar-scrolled {
          height: 70px;
          background: rgba(4, 4, 8, 0.7);
          backdrop-filter: blur(20px);
          -webkit-backdrop-filter: blur(20px);
          border-bottom: 1px solid rgba(255, 255, 255, 0.06);
          box-shadow: 0 10px 30px -10px rgba(0, 0, 0, 0.5);
        }

        .navbar-container {
          max-width: 1200px;
          margin: 0 auto;
          height: 100%;
          display: flex;
          align-items: center;
          justify-content: space-between;
          padding: 0 24px;
        }

        .nav-logo {
          display: flex;
          align-items: center;
          gap: 10px;
          font-family: var(--font-heading);
          font-weight: 800;
          font-size: 22px;
          letter-spacing: -0.03em;
        }

        .logo-icon {
          font-size: 24px;
          animation: float 4s ease-in-out infinite;
        }

        @keyframes float {
          0%, 100% { transform: translateY(0); }
          50% { transform: translateY(-4px); }
        }

        .logo-accent {
          background: linear-gradient(135deg, var(--accent-blue), var(--accent-cyan));
          background-clip: text;
          -webkit-background-clip: text;
          -webkit-text-fill-color: transparent;
        }

        .nav-links-desktop {
          display: flex;
          align-items: center;
          gap: 32px;
        }

        .nav-link {
          position: relative;
          color: var(--text-secondary);
          font-size: 15px;
          font-weight: 500;
          transition: color 0.2s ease;
          padding: 8px 0;
        }

        .nav-link:hover {
          color: var(--text-primary);
        }

        .nav-link.active {
          color: var(--text-primary);
          font-weight: 600;
        }

        .nav-indicator {
          position: absolute;
          bottom: 0;
          left: 0;
          right: 0;
          height: 2px;
          background: linear-gradient(90deg, var(--accent-blue), var(--accent-cyan));
          border-radius: 99px;
        }

        .mobile-menu-toggle {
          display: none;
          flex-direction: column;
          justify-content: space-between;
          width: 24px;
          height: 18px;
          background: transparent;
          border: none;
          cursor: pointer;
          z-index: 110;
        }

        .hamburger-line {
          width: 100%;
          height: 2px;
          background-color: var(--text-primary);
          transition: all 0.3s cubic-bezier(0.16, 1, 0.3, 1);
          border-radius: 99px;
        }

        .mobile-menu-toggle.open .hamburger-line:nth-child(1) {
          transform: translateY(8px) rotate(45deg);
        }

        .mobile-menu-toggle.open .hamburger-line:nth-child(2) {
          opacity: 0;
        }

        .mobile-menu-toggle.open .hamburger-line:nth-child(3) {
          transform: translateY(-8px) rotate(-45deg);
        }

        .mobile-menu-drawer {
          position: fixed;
          top: 0;
          right: 0;
          bottom: 0;
          left: 0;
          background: rgba(4, 4, 8, 0.95);
          backdrop-filter: blur(20px);
          -webkit-backdrop-filter: blur(20px);
          z-index: 99;
          display: flex;
          align-items: center;
          justify-content: center;
          transform: translateY(-100%);
          transition: transform 0.5s cubic-bezier(0.16, 1, 0.3, 1);
        }

        .mobile-menu-drawer.active {
          transform: translateY(0);
        }

        .mobile-drawer-links {
          display: flex;
          flex-direction: column;
          align-items: center;
          gap: 30px;
          width: 80%;
          max-width: 320px;
        }

        .mobile-nav-link {
          font-family: var(--font-heading);
          font-size: 24px;
          font-weight: 700;
          color: var(--text-secondary);
          transition: color 0.2s ease;
        }

        .mobile-nav-link:hover, .mobile-nav-link.active {
          color: var(--text-primary);
          background: linear-gradient(135deg, var(--accent-blue), var(--accent-cyan));
          background-clip: text;
          -webkit-background-clip: text;
          -webkit-text-fill-color: transparent;
        }

        @media (max-width: 768px) {
          .nav-links-desktop {
            display: none;
          }
          .mobile-menu-toggle {
            display: flex;
          }
        }
      `}</style>
    </>
  );
}
