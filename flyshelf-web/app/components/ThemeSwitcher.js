"use client";

const themes = [
  { id: 'midnight', name: 'Midnight', colors: ['#1a1a2e', '#3b82f6', '#8b5cf6'] },
  { id: 'ocean', name: 'Ocean', colors: ['#0a192f', '#06b6d4', '#0ea5e9'] },
  { id: 'sunset', name: 'Sunset', colors: ['#1a0a0a', '#f97316', '#ec4899'] },
  { id: 'emerald', name: 'Emerald', colors: ['#0a1a0a', '#10b981', '#34d399'] },
  { id: 'lavender', name: 'Lavender', colors: ['#1a0a2e', '#a78bfa', '#c084fc'] },
];

export default function ThemeSwitcher({ activeTheme, onThemeChange }) {
  return (
    <div className="theme-switcher">
      {themes.map((theme) => {
        const isActive = activeTheme === theme.id;
        const gradient = `linear-gradient(135deg, ${theme.colors[0]}, ${theme.colors[1]}, ${theme.colors[2]})`;
        return (
          <button
            key={theme.id}
            className={`swatch ${isActive ? 'swatch-active' : ''}`}
            onClick={() => onThemeChange?.(theme.id)}
            aria-label={`Select ${theme.name} theme`}
            title={theme.name}
          >
            <span className="swatch-circle" style={{ background: gradient }} />
            <span className="swatch-name">{theme.name}</span>
          </button>
        );
      })}

      <style jsx>{`
        .theme-switcher {
          display: flex;
          gap: 16px;
          justify-content: center;
          margin-top: 24px;
        }
        .swatch {
          display: flex;
          flex-direction: column;
          align-items: center;
          gap: 6px;
          background: none;
          border: none;
          cursor: pointer;
          padding: 0;
          transition: transform 0.3s cubic-bezier(0.16,1,0.3,1);
        }
        .swatch:hover {
          transform: scale(1.08);
        }
        .swatch-circle {
          width: 40px;
          height: 40px;
          border-radius: 50%;
          border: 2px solid transparent;
          transition: all 0.3s ease;
          box-shadow: 0 2px 8px rgba(0,0,0,0.3);
        }
        .swatch-active .swatch-circle {
          border-color: #fff;
          box-shadow: 0 0 15px rgba(255,255,255,0.3);
          transform: scale(1.15);
        }
        .swatch-name {
          font-size: 10px;
          color: rgba(255,255,255,0.35);
          font-weight: 500;
          letter-spacing: 0.3px;
          transition: color 0.3s ease;
        }
        .swatch-active .swatch-name {
          color: rgba(255,255,255,0.8);
        }
      `}</style>
    </div>
  );
}
