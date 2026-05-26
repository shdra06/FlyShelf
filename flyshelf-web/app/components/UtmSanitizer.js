"use client";
import { useState } from 'react';

const TRACKING_PARAMS = [
  'utm_source', 'utm_medium', 'utm_campaign', 'utm_term', 'utm_content',
  'fbclid', 'gclid',
];

export default function UtmSanitizer() {
  const [url, setUrl] = useState(
    'https://flyshelf.app/docs?utm_source=newsletter&utm_medium=email&utm_campaign=launch_v6&fbclid=ab12cd34ef56'
  );
  const [cleaned, setCleaned] = useState(false);
  const [btnText, setBtnText] = useState('Sanitize Link');
  const [btnDone, setBtnDone] = useState(false);
  const [message, setMessage] = useState('');

  const handleSanitize = () => {
    try {
      const parsed = new URL(url.trim());
      let removed = 0;
      TRACKING_PARAMS.forEach((p) => {
        if (parsed.searchParams.has(p)) {
          parsed.searchParams.delete(p);
          removed++;
        }
      });

      if (removed > 0) {
        setUrl(parsed.toString());
        setCleaned(true);
        setBtnText('Sanitized! ✓');
        setBtnDone(true);
        setMessage(`${removed} tracking parameter${removed > 1 ? 's' : ''} removed.`);
        setTimeout(() => {
          setCleaned(false);
          setBtnText('Sanitize Link');
          setBtnDone(false);
          setMessage('');
        }, 2200);
      } else {
        setMessage('No tracking parameters detected.');
        setTimeout(() => setMessage(''), 2200);
      }
    } catch {
      setMessage('Invalid URL — please check the format.');
      setTimeout(() => setMessage(''), 2200);
    }
  };

  return (
    <>
      <style jsx>{`
        .utm-sanitizer {
          background: rgba(255,255,255,0.03);
          border: 1px solid rgba(255,255,255,0.06);
          border-radius: 16px;
          padding: 24px;
          backdrop-filter: blur(12px);
        }
        .sanitizer-title {
          font-size: 14px;
          font-weight: 600;
          color: #e2e8f0;
          margin-bottom: 16px;
          display: flex;
          align-items: center;
          gap: 8px;
        }
        .sanitizer-title span {
          font-size: 16px;
        }
        .url-textarea {
          width: 100%;
          min-height: 80px;
          resize: vertical;
          background: rgba(0,0,0,0.3);
          border: 1px solid rgba(255,255,255,0.08);
          border-radius: 10px;
          padding: 12px 16px;
          color: white;
          font-family: monospace;
          font-size: 13px;
          outline: none;
          transition: border-color 0.3s, box-shadow 0.3s;
          box-sizing: border-box;
          line-height: 1.5;
        }
        .url-textarea:focus {
          border-color: rgba(6,182,212,0.4);
        }
        .url-textarea.cleaned {
          border-color: #10b981;
          box-shadow: 0 0 15px rgba(16,185,129,0.15);
        }
        .sanitize-btn {
          background: #10b981;
          color: white;
          border: none;
          border-radius: 8px;
          padding: 10px 24px;
          font-weight: 600;
          font-size: 13px;
          margin-top: 12px;
          width: 100%;
          cursor: pointer;
          transition: opacity 0.2s, transform 0.15s, background 0.3s;
        }
        .sanitize-btn:hover {
          opacity: 0.9;
          transform: translateY(-1px);
        }
        .sanitize-btn:active {
          transform: translateY(0);
        }
        .sanitize-btn.done {
          background: #059669;
        }
        .sanitizer-msg {
          margin-top: 10px;
          font-size: 12px;
          color: #6ee7b7;
          font-family: monospace;
          min-height: 18px;
        }
      `}</style>
      <div className="utm-sanitizer">
        <div className="sanitizer-title">
          <span>🧹</span> UTM Sanitizer
        </div>
        <textarea
          className={`url-textarea${cleaned ? ' cleaned' : ''}`}
          value={url}
          onChange={(e) => setUrl(e.target.value)}
          placeholder="Paste a URL with tracking parameters..."
        />
        <button
          className={`sanitize-btn${btnDone ? ' done' : ''}`}
          onClick={handleSanitize}
        >
          {btnText}
        </button>
        <div className="sanitizer-msg">{message}</div>
      </div>
    </>
  );
}
