"use client";
import { useState } from 'react';

function hexToRgb(hex) {
  const h = hex.replace('#', '');
  const full = h.length === 3
    ? h.split('').map(c => c + c).join('')
    : h;
  const num = parseInt(full, 16);
  return {
    r: (num >> 16) & 255,
    g: (num >> 8) & 255,
    b: num & 255,
  };
}

function hexToHsl(hex) {
  const { r: rr, g: gg, b: bb } = hexToRgb(hex);
  const r = rr / 255, g = gg / 255, b = bb / 255;
  const max = Math.max(r, g, b), min = Math.min(r, g, b);
  let h = 0, s = 0;
  const l = (max + min) / 2;
  if (max !== min) {
    const d = max - min;
    s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
    switch (max) {
      case r: h = ((g - b) / d + (g < b ? 6 : 0)) / 6; break;
      case g: h = ((b - r) / d + 2) / 6; break;
      case b: h = ((r - g) / d + 4) / 6; break;
    }
  }
  return {
    h: Math.round(h * 360),
    s: Math.round(s * 100),
    l: Math.round(l * 100),
  };
}

export default function ColorPicker() {
  const [color, setColor] = useState('#06b6d4');
  const [toast, setToast] = useState('');

  const isValidColor = (val) => {
    return val.startsWith('#') || val.startsWith('rgb') || val.startsWith('hsl');
  };

  const handleChange = (e) => {
    const val = e.target.value;
    setColor(val);
  };

  const showToast = (msg) => {
    setToast(msg);
    setTimeout(() => setToast(''), 2000);
  };

  const copyHex = () => {
    navigator.clipboard.writeText(color).then(() => showToast('HEX copied!'));
  };

  const copyRgb = () => {
    try {
      const { r, g, b } = hexToRgb(color);
      const str = `rgb(${r}, ${g}, ${b})`;
      navigator.clipboard.writeText(str).then(() => showToast('RGB copied!'));
    } catch {
      showToast('Invalid color');
    }
  };

  const copyHsl = () => {
    try {
      const { h, s, l } = hexToHsl(color);
      const str = `hsl(${h}, ${s}%, ${l}%)`;
      navigator.clipboard.writeText(str).then(() => showToast('HSL copied!'));
    } catch {
      showToast('Invalid color');
    }
  };

  const displayColor = isValidColor(color) ? color : '#06b6d4';

  return (
    <>
      <style jsx>{`
        .color-picker {
          background: rgba(255,255,255,0.03);
          border: 1px solid rgba(255,255,255,0.06);
          border-radius: 16px;
          padding: 24px;
          backdrop-filter: blur(12px);
          display: flex;
          flex-direction: column;
          align-items: center;
          gap: 16px;
        }
        .picker-title {
          font-size: 14px;
          font-weight: 600;
          color: #e2e8f0;
          display: flex;
          align-items: center;
          gap: 8px;
          align-self: flex-start;
        }
        .picker-title span {
          font-size: 16px;
        }
        .swatch {
          width: 80px;
          height: 80px;
          border-radius: 16px;
          margin: 0 auto;
          transition: all 0.3s;
          border: 2px solid rgba(255,255,255,0.08);
        }
        .color-input {
          width: 100%;
          background: rgba(0,0,0,0.3);
          border: 1px solid rgba(255,255,255,0.1);
          border-radius: 8px;
          padding: 10px 16px;
          color: white;
          font-family: monospace;
          font-size: 14px;
          outline: none;
          text-align: center;
          transition: border-color 0.2s;
        }
        .color-input:focus {
          border-color: rgba(6,182,212,0.4);
        }
        .copy-row {
          display: flex;
          gap: 8px;
          justify-content: center;
        }
        .copy-btn {
          background: rgba(255,255,255,0.05);
          border: 1px solid rgba(255,255,255,0.1);
          border-radius: 6px;
          padding: 6px 16px;
          color: #94a3b8;
          cursor: pointer;
          font-size: 12px;
          font-weight: 600;
          transition: all 0.2s;
        }
        .copy-btn:hover {
          background: rgba(255,255,255,0.1);
          color: #e2e8f0;
          border-color: rgba(255,255,255,0.2);
        }
        .toast {
          position: fixed;
          bottom: 30px;
          left: 50%;
          transform: translateX(-50%);
          background: rgba(6,182,212,0.15);
          border: 1px solid rgba(6,182,212,0.3);
          color: #06b6d4;
          padding: 8px 20px;
          border-radius: 8px;
          font-size: 13px;
          z-index: 9999;
          animation: fadeInUp 0.3s ease-out;
          pointer-events: none;
        }
        @keyframes fadeInUp {
          from {
            opacity: 0;
            transform: translateX(-50%) translateY(10px);
          }
          to {
            opacity: 1;
            transform: translateX(-50%) translateY(0);
          }
        }
      `}</style>
      <div className="color-picker">
        <div className="picker-title">
          <span>🎨</span> Color Picker
        </div>
        <div
          className="swatch"
          style={{
            backgroundColor: displayColor,
            boxShadow: `0 0 24px ${displayColor}44, 0 0 48px ${displayColor}22`,
          }}
        />
        <input
          className="color-input"
          type="text"
          value={color}
          onChange={handleChange}
          placeholder="#06b6d4"
        />
        <div className="copy-row">
          <button className="copy-btn" onClick={copyHex}>HEX</button>
          <button className="copy-btn" onClick={copyRgb}>RGB</button>
          <button className="copy-btn" onClick={copyHsl}>HSL</button>
        </div>
      </div>
      {toast && <div className="toast">{toast}</div>}
    </>
  );
}
