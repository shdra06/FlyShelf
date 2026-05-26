"use client";
import { useState, useRef, useCallback, useEffect } from 'react';

export default function ShakeSummon() {
  const [gauge, setGauge] = useState(0);
  const [isTracking, setIsTracking] = useState(false);
  const [isUnlocked, setIsUnlocked] = useState(false);
  const lastXRef = useRef(null);
  const lastDirRef = useRef(null);
  const reversalsRef = useRef(0);
  const gaugeRef = useRef(0);
  const decayRef = useRef(null);
  const sandboxRef = useRef(null);

  const THRESHOLD = 8;
  const TRIGGER_COUNT = 8;

  const resetAll = useCallback(() => {
    setGauge(0);
    setIsTracking(false);
    setIsUnlocked(false);
    lastXRef.current = null;
    lastDirRef.current = null;
    reversalsRef.current = 0;
    gaugeRef.current = 0;
    if (decayRef.current) {
      clearInterval(decayRef.current);
      decayRef.current = null;
    }
  }, []);

  const handleMouseDown = useCallback((e) => {
    if (isUnlocked) return;
    setIsTracking(true);
    lastXRef.current = e.clientX;
    lastDirRef.current = null;
    reversalsRef.current = 0;
    gaugeRef.current = 0;
    setGauge(0);

    // Start decay timer
    if (decayRef.current) clearInterval(decayRef.current);
    decayRef.current = setInterval(() => {
      gaugeRef.current = Math.max(0, gaugeRef.current - 0.7);
      setGauge(gaugeRef.current);
    }, 100);
  }, [isUnlocked]);

  const handleMouseMove = useCallback((e) => {
    if (!isTracking || isUnlocked) return;
    if (lastXRef.current === null) {
      lastXRef.current = e.clientX;
      return;
    }

    const deltaX = e.clientX - lastXRef.current;
    if (Math.abs(deltaX) < THRESHOLD) return;

    const dir = deltaX > 0 ? 'right' : 'left';
    if (lastDirRef.current !== null && dir !== lastDirRef.current) {
      reversalsRef.current += 1;
      const pct = Math.min((reversalsRef.current / TRIGGER_COUNT) * 100, 100);
      gaugeRef.current = pct;
      setGauge(pct);

      if (reversalsRef.current >= TRIGGER_COUNT) {
        if (decayRef.current) {
          clearInterval(decayRef.current);
          decayRef.current = null;
        }
        setIsTracking(false);
        setIsUnlocked(true);
      }
    }
    lastDirRef.current = dir;
    lastXRef.current = e.clientX;
  }, [isTracking, isUnlocked]);

  const handleMouseUp = useCallback(() => {
    if (!isUnlocked) {
      setIsTracking(false);
      if (decayRef.current) {
        clearInterval(decayRef.current);
        decayRef.current = null;
      }
      // Fade gauge
      const fadeInterval = setInterval(() => {
        gaugeRef.current = Math.max(0, gaugeRef.current - 3);
        setGauge(gaugeRef.current);
        if (gaugeRef.current <= 0) clearInterval(fadeInterval);
      }, 30);
    }
  }, [isUnlocked]);

  useEffect(() => {
    return () => {
      if (decayRef.current) clearInterval(decayRef.current);
    };
  }, []);

  return (
    <section className="shake-section">
      <span className="section-badge badge-purple">Summon Gesture</span>
      <h2>Shake to Summon WinSumo</h2>
      <p className="section-subtitle">Hold click and shake your mouse left & right rapidly to summon the FlyShelf workspace overlay.</p>

      <div
        className="sandbox"
        ref={sandboxRef}
        onMouseDown={handleMouseDown}
        onMouseMove={handleMouseMove}
        onMouseUp={handleMouseUp}
        onMouseLeave={handleMouseUp}
      >
        {/* Instruction */}
        <div className={`sandbox-instruction ${isUnlocked ? 'hidden' : ''}`}>
          <span className="hand-icon">👋</span>
          <span className="instruction-text">Click &amp; Shake Here</span>
        </div>

        {/* Gauge */}
        <div className={`gauge-container ${isUnlocked ? 'hidden' : ''}`}>
          <div className="gauge-track">
            <div className="gauge-fill" style={{ width: `${gauge}%` }} />
          </div>
          <span className="gauge-label">{Math.round(gauge)}%</span>
        </div>

        {/* WinSumo Overlay */}
        <div className={`winsumo-overlay ${isUnlocked ? 'unlocked' : ''}`}>
          <div className="overlay-header">
            <span className="overlay-title">⚡ FlyShelf Workspace</span>
            <button className="overlay-close" onClick={resetAll}>✕</button>
          </div>
          <div className="overlay-cards">
            <div className="overlay-card">
              <div className="oc-badge" style={{ background: 'rgba(6,182,212,0.15)', color: '#06b6d4' }}>TEXT</div>
              <div className="oc-content">
                <span className="oc-title">Meeting Notes — Sprint #42</span>
                <span className="oc-meta">Copied 3 min ago · 128 words</span>
              </div>
            </div>
            <div className="overlay-card">
              <div className="oc-badge" style={{ background: 'rgba(139,92,246,0.15)', color: '#8b5cf6' }}>IMAGE</div>
              <div className="oc-content">
                <span className="oc-title">Screenshot_2026-05-25.png</span>
                <span className="oc-meta">1920×1080 · OCR extracted</span>
              </div>
              <span className="ocr-tag">OCR</span>
            </div>
          </div>
        </div>
      </div>

      <style jsx>{`
        .shake-section {
          text-align: center;
          padding: 60px 20px;
        }
        .section-badge {
          display: inline-block;
          padding: 5px 14px;
          border-radius: 99px;
          font-size: 12px;
          font-weight: 600;
          letter-spacing: 0.5px;
          text-transform: uppercase;
          margin-bottom: 14px;
        }
        .badge-purple {
          background: rgba(139,92,246,0.12);
          color: #8b5cf6;
          border: 1px solid rgba(139,92,246,0.25);
        }
        .shake-section h2 {
          font-size: 32px;
          font-weight: 700;
          color: #fff;
          margin: 0 0 8px;
        }
        .section-subtitle {
          color: rgba(255,255,255,0.5);
          font-size: 15px;
          margin: 0 0 32px;
          max-width: 520px;
          margin-left: auto;
          margin-right: auto;
        }
        .sandbox {
          background: rgba(8,8,16,0.5);
          border: 1px solid rgba(255,255,255,0.08);
          border-radius: 16px;
          min-height: 400px;
          position: relative;
          overflow: hidden;
          cursor: crosshair;
          display: flex;
          flex-direction: column;
          align-items: center;
          justify-content: center;
          user-select: none;
        }
        .sandbox-instruction {
          display: flex;
          flex-direction: column;
          align-items: center;
          gap: 14px;
          transition: opacity 0.4s ease, transform 0.4s ease;
        }
        .sandbox-instruction.hidden {
          opacity: 0;
          transform: scale(0.9);
          pointer-events: none;
        }
        .hand-icon {
          font-size: 48px;
          animation: wave 1.8s ease-in-out infinite;
        }
        @keyframes wave {
          0%, 100% { transform: rotate(0deg); }
          25% { transform: rotate(20deg); }
          50% { transform: rotate(-15deg); }
          75% { transform: rotate(10deg); }
        }
        .instruction-text {
          font-size: 18px;
          font-weight: 600;
          color: rgba(255,255,255,0.4);
          letter-spacing: 0.5px;
        }
        /* Gauge */
        .gauge-container {
          position: absolute;
          bottom: 24px;
          left: 50%;
          transform: translateX(-50%);
          display: flex;
          align-items: center;
          gap: 12px;
          transition: opacity 0.4s ease;
          width: 260px;
        }
        .gauge-container.hidden {
          opacity: 0;
          pointer-events: none;
        }
        .gauge-track {
          flex: 1;
          height: 6px;
          border-radius: 99px;
          background: rgba(255,255,255,0.06);
          overflow: hidden;
        }
        .gauge-fill {
          height: 100%;
          border-radius: 99px;
          background: linear-gradient(90deg, #06b6d4, #8b5cf6);
          transition: width 0.15s ease;
        }
        .gauge-label {
          font-size: 12px;
          color: rgba(255,255,255,0.35);
          font-weight: 600;
          min-width: 36px;
          text-align: right;
          font-variant-numeric: tabular-nums;
        }
        /* Overlay */
        .winsumo-overlay {
          position: absolute;
          inset: 0;
          background: rgba(8,8,16,0.85);
          backdrop-filter: blur(20px);
          border-radius: 16px;
          display: flex;
          flex-direction: column;
          padding: 24px;
          opacity: 0;
          transform: scale(0.92);
          pointer-events: none;
          transition: opacity 0.5s cubic-bezier(0.16,1,0.3,1), transform 0.5s cubic-bezier(0.16,1,0.3,1);
        }
        .winsumo-overlay.unlocked {
          opacity: 1;
          transform: scale(1);
          pointer-events: auto;
        }
        .overlay-header {
          display: flex;
          align-items: center;
          justify-content: space-between;
          margin-bottom: 24px;
        }
        .overlay-title {
          font-size: 18px;
          font-weight: 700;
          color: #fff;
        }
        .overlay-close {
          width: 32px;
          height: 32px;
          border-radius: 8px;
          border: 1px solid rgba(255,255,255,0.1);
          background: rgba(255,255,255,0.05);
          color: rgba(255,255,255,0.5);
          font-size: 14px;
          cursor: pointer;
          display: flex;
          align-items: center;
          justify-content: center;
          transition: all 0.2s ease;
        }
        .overlay-close:hover {
          background: rgba(255,255,255,0.1);
          color: #fff;
        }
        .overlay-cards {
          display: flex;
          flex-direction: column;
          gap: 12px;
        }
        .overlay-card {
          display: flex;
          align-items: center;
          gap: 14px;
          padding: 16px;
          border-radius: 12px;
          border: 1px solid rgba(255,255,255,0.06);
          background: rgba(255,255,255,0.04);
          animation: cardSlide 0.5s cubic-bezier(0.16,1,0.3,1) forwards;
        }
        .overlay-card:nth-child(2) {
          animation-delay: 0.12s;
          opacity: 0;
        }
        @keyframes cardSlide {
          from { opacity: 0; transform: translateY(16px); }
          to { opacity: 1; transform: translateY(0); }
        }
        .oc-badge {
          font-size: 9px;
          font-weight: 700;
          padding: 4px 8px;
          border-radius: 6px;
          letter-spacing: 0.5px;
          flex-shrink: 0;
        }
        .oc-content {
          display: flex;
          flex-direction: column;
          gap: 3px;
          text-align: left;
        }
        .oc-title {
          font-size: 13px;
          color: rgba(255,255,255,0.85);
          font-weight: 500;
        }
        .oc-meta {
          font-size: 11px;
          color: rgba(255,255,255,0.3);
        }
        .ocr-tag {
          margin-left: auto;
          font-size: 9px;
          font-weight: 700;
          padding: 3px 7px;
          border-radius: 5px;
          background: rgba(245,158,11,0.15);
          color: #f59e0b;
          letter-spacing: 0.5px;
        }
      `}</style>
    </section>
  );
}
