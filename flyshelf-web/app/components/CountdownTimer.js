"use client";
import { useState, useRef, useEffect } from 'react';

const CIRCUMFERENCE = 2 * Math.PI * 54; // ~339.29

export default function CountdownTimer() {
  const [input, setInput] = useState('/10s');
  const [total, setTotal] = useState(0);
  const [remaining, setRemaining] = useState(0);
  const [running, setRunning] = useState(false);
  const [done, setDone] = useState(false);
  const intervalRef = useRef(null);

  useEffect(() => {
    return () => {
      if (intervalRef.current) clearInterval(intervalRef.current);
    };
  }, []);

  const parseInput = (val) => {
    const cleaned = val.replace(/^\//, '').trim().toLowerCase();
    const match = cleaned.match(/^(\d+(?:\.\d+)?)\s*(s|sec|m|min|mins|minutes|seconds)?$/);
    if (!match) return null;
    const num = parseFloat(match[1]);
    const unit = match[2] || 's';
    if (unit.startsWith('m')) return Math.round(num * 60);
    return Math.round(num);
  };

  const handlePlay = () => {
    const seconds = parseInput(input);
    if (!seconds || seconds <= 0) return;
    setTotal(seconds);
    setRemaining(seconds);
    setDone(false);
    setRunning(true);

    if (intervalRef.current) clearInterval(intervalRef.current);
    let rem = seconds;
    intervalRef.current = setInterval(() => {
      rem--;
      if (rem <= 0) {
        clearInterval(intervalRef.current);
        intervalRef.current = null;
        setRemaining(0);
        setRunning(false);
        setDone(true);
      } else {
        setRemaining(rem);
      }
    }, 1000);
  };

  const fraction = total > 0 ? remaining / total : 1;
  const offset = CIRCUMFERENCE * (1 - fraction);

  let ringColor = '#06b6d4';
  if (done) {
    ringColor = '#10b981';
  } else if (fraction <= 0.25) {
    ringColor = '#ef4444';
  } else if (fraction <= 0.5) {
    ringColor = '#f59e0b';
  }

  return (
    <>
      <style jsx>{`
        .countdown-timer {
          background: rgba(255,255,255,0.03);
          border: 1px solid rgba(255,255,255,0.06);
          border-radius: 16px;
          padding: 24px;
          backdrop-filter: blur(12px);
          display: flex;
          flex-direction: column;
          align-items: center;
        }
        .timer-title {
          font-size: 14px;
          font-weight: 600;
          color: #e2e8f0;
          margin-bottom: 16px;
          display: flex;
          align-items: center;
          gap: 8px;
          align-self: flex-start;
        }
        .timer-title span {
          font-size: 16px;
        }
        .input-group {
          display: flex;
          gap: 8px;
          width: 100%;
          margin-bottom: 4px;
        }
        .timer-input {
          flex: 1;
          background: rgba(0,0,0,0.3);
          border: 1px solid rgba(255,255,255,0.1);
          border-radius: 8px;
          padding: 10px 16px;
          color: white;
          font-family: monospace;
          font-size: 14px;
          outline: none;
          transition: border-color 0.2s;
        }
        .timer-input:focus {
          border-color: rgba(6,182,212,0.4);
        }
        .play-btn {
          background: #06b6d4;
          border: none;
          border-radius: 8px;
          padding: 10px 14px;
          color: white;
          cursor: pointer;
          font-size: 16px;
          transition: opacity 0.2s, transform 0.15s;
          display: flex;
          align-items: center;
          justify-content: center;
          line-height: 1;
        }
        .play-btn:hover {
          opacity: 0.85;
          transform: translateY(-1px);
        }
        .play-btn:active {
          transform: translateY(0);
        }
        .svg-container {
          width: 140px;
          height: 140px;
          margin: 20px auto;
          position: relative;
        }
        .circle-bg {
          stroke: rgba(255,255,255,0.05);
          fill: none;
          stroke-width: 6;
        }
        .circle-progress {
          fill: none;
          stroke-width: 6;
          stroke-linecap: round;
          transition: stroke-dashoffset 0.5s, stroke 0.5s;
          transform: rotate(-90deg);
          transform-origin: center;
        }
        .center-text {
          position: absolute;
          inset: 0;
          display: flex;
          align-items: center;
          justify-content: center;
          font-size: 28px;
          font-weight: 700;
          color: #e2e8f0;
          font-family: monospace;
        }
        .done-text {
          color: #10b981;
          animation: pulse 1.5s ease-in-out infinite;
        }
        @keyframes pulse {
          0%, 100% { opacity: 1; transform: scale(1); }
          50% { opacity: 0.7; transform: scale(1.05); }
        }
      `}</style>
      <div className="countdown-timer">
        <div className="timer-title">
          <span>⏱</span> Countdown
        </div>
        <div className="input-group">
          <input
            className="timer-input"
            type="text"
            value={input}
            onChange={(e) => setInput(e.target.value)}
            placeholder="/10s"
            disabled={running}
          />
          <button className="play-btn" onClick={handlePlay} disabled={running}>
            ▶
          </button>
        </div>
        <div className="svg-container">
          <svg viewBox="0 0 120 120" width="140" height="140">
            <circle className="circle-bg" cx="60" cy="60" r="54" />
            <circle
              className="circle-progress"
              cx="60"
              cy="60"
              r="54"
              style={{
                stroke: ringColor,
                strokeDasharray: CIRCUMFERENCE,
                strokeDashoffset: offset,
              }}
            />
          </svg>
          <div className={`center-text ${done ? 'done-text' : ''}`}>
            {done ? 'Done!' : (running || total > 0 ? remaining : '—')}
          </div>
        </div>
      </div>
    </>
  );
}
