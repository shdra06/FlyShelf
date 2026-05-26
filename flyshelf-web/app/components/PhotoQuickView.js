"use client";
import { useState } from 'react';

const samplePhotos = [
  {
    id: 1,
    imageUrl: 'screenshot_sunset_history.png',
    title: 'Text History (Sunset Theme)',
    timestamp: '2 mins ago',
    ocrText: `its good but the bug is not totally gone , also see the new video about the spawning animation having a weird black dark box appear and disappear , and this is making t...\n\n20260525-0752-43.5098695.mp4\n\n— Updated XAML initial Width\nNote: Existing users who already have a config.json will keep their current saved dimen...`,
    icon: '📋',
  },
  {
    id: 2,
    imageUrl: 'screenshot_pdf_merge.png',
    title: 'PDF Merger List',
    timestamp: '15 mins ago',
    ocrText: `CheXthought.pdf\nmooc admit card natural hazards.pdf\nMerged_20260520_171016.pdf\nMerged_20260520_170913.pdf`,
    icon: '📄',
  },
  {
    id: 3,
    imageUrl: 'screenshot_night_stars.png',
    title: 'Video History (Cosmos Theme)',
    timestamp: '1 hour ago',
    ocrText: `20260524-1943-04.2515504.mp4\n20260524-1713-53.0657277.mp4\n20260524-1613-52.8722717.mp4\n20260524-1528-01.8448000.mp4`,
    icon: '🌌',
  },
];

export default function PhotoQuickView() {
  const [selectedPhoto, setSelectedPhoto] = useState(null);
  const [copied, setCopied] = useState(false);
  const [toast, setToast] = useState(false);

  const openModal = (photo) => {
    setSelectedPhoto(photo);
    setCopied(false);
  };

  const closeModal = () => {
    setSelectedPhoto(null);
    setCopied(false);
  };

  const handleCopy = async () => {
    if (!selectedPhoto) return;
    try {
      await navigator.clipboard.writeText(selectedPhoto.ocrText);
    } catch {
      // fallback
      const ta = document.createElement('textarea');
      ta.value = selectedPhoto.ocrText;
      document.body.appendChild(ta);
      ta.select();
      document.execCommand('copy');
      document.body.removeChild(ta);
    }
    setCopied(true);
    setToast(true);
    setTimeout(() => setCopied(false), 2000);
    setTimeout(() => setToast(false), 2500);
  };

  return (
    <section className="pqv-section">
      <style jsx>{`
        .pqv-section {
          padding: 60px 0;
        }

        .pqv-badge {
          display: inline-flex;
          align-items: center;
          gap: 6px;
          background: linear-gradient(135deg, rgba(59,130,246,0.1), rgba(6,182,212,0.15));
          color: #06b6d4;
          font-size: 13px;
          font-weight: 600;
          padding: 6px 14px;
          border-radius: 20px;
          margin-bottom: 16px;
          border: 1px solid rgba(6, 182, 212, 0.2);
        }

        .pqv-title {
          font-size: 32px;
          font-weight: 800;
          color: #f1f5f9;
          margin: 0 0 10px 0;
          font-family: var(--font-heading);
        }

        .pqv-subtitle {
          font-size: 16px;
          color: rgba(255, 255, 255, 0.55);
          margin: 0 0 40px 0;
          max-width: 600px;
          line-height: 1.6;
        }

        .pqv-grid {
          display: grid;
          grid-template-columns: repeat(3, 1fr);
          gap: 28px;
        }

        .pqv-card {
          background: rgba(255, 255, 255, 0.02);
          border: 1px solid rgba(255, 255, 255, 0.06);
          border-radius: 20px;
          overflow: hidden;
          cursor: pointer;
          box-shadow: 0 10px 30px rgba(0, 0, 0, 0.4);
          transition: all 0.4s cubic-bezier(0.16, 1, 0.3, 1);
          display: flex;
          flex-direction: column;
          position: relative;
        }

        .pqv-card::before {
          content: '';
          position: absolute;
          inset: 0;
          background: linear-gradient(to bottom, transparent 60%, rgba(0,0,0,0.8));
          z-index: 2;
          pointer-events: none;
          opacity: 0.8;
          transition: opacity 0.3s;
        }

        .pqv-card:hover::before {
          opacity: 0.5;
        }

        .pqv-card:hover {
          transform: translateY(-6px);
          box-shadow: 0 20px 40px rgba(0, 0, 0, 0.5);
          border-color: rgba(6, 182, 212, 0.3);
        }

        .pqv-card-image-wrapper {
          height: 220px;
          overflow: hidden;
          position: relative;
          background: rgba(0,0,0,0.4);
        }

        .pqv-card-img {
          width: 100%;
          height: 100%;
          object-fit: cover;
          object-position: center top;
          transition: transform 0.6s cubic-bezier(0.16, 1, 0.3, 1);
        }

        .pqv-card:hover .pqv-card-img {
          transform: scale(1.05);
        }

        .pqv-hover-overlay {
          position: absolute;
          inset: 0;
          background: rgba(6, 182, 212, 0.1);
          display: flex;
          align-items: center;
          justify-content: center;
          opacity: 0;
          transition: opacity 0.3s;
          z-index: 3;
        }

        .pqv-card:hover .pqv-hover-overlay {
          opacity: 1;
        }

        .pqv-zoom-badge {
          background: rgba(0, 0, 0, 0.75);
          backdrop-filter: blur(8px);
          border: 1px solid rgba(255,255,255,0.15);
          color: #f1f5f9;
          font-size: 12px;
          font-weight: 600;
          padding: 8px 16px;
          border-radius: 99px;
          display: flex;
          align-items: center;
          gap: 6px;
          transform: translateY(10px);
          transition: transform 0.3s cubic-bezier(0.16, 1, 0.3, 1);
        }

        .pqv-card:hover .pqv-zoom-badge {
          transform: translateY(0);
        }

        .pqv-card-info {
          padding: 20px;
          background: rgba(10, 10, 15, 0.95);
          border-top: 1px solid rgba(255, 255, 255, 0.05);
          z-index: 3;
        }

        .pqv-card-title-row {
          display: flex;
          align-items: center;
          justify-content: space-between;
          margin-bottom: 4px;
        }

        .pqv-card-title {
          font-size: 15px;
          font-weight: 700;
          color: #f1f5f9;
          margin: 0;
        }

        .pqv-card-icon {
          font-size: 16px;
        }

        .pqv-card-timestamp {
          font-size: 12px;
          color: rgba(255, 255, 255, 0.4);
        }

        /* Modal Overlay */
        .pqv-overlay {
          position: fixed;
          inset: 0;
          background: rgba(0, 0, 0, 0.85);
          backdrop-filter: blur(12px);
          z-index: 1000;
          display: flex;
          align-items: center;
          justify-content: center;
          padding: 20px;
          animation: pqvFadeIn 0.3s cubic-bezier(0.16, 1, 0.3, 1) forwards;
        }

        .pqv-modal {
          position: relative;
          background: rgba(12, 12, 22, 0.95);
          border: 1px solid rgba(255, 255, 255, 0.08);
          border-radius: 24px;
          max-width: 900px;
          width: 100%;
          max-height: 85vh;
          overflow: hidden;
          display: grid;
          grid-template-columns: 1.1fr 0.9fr;
          box-shadow: 0 30px 80px rgba(0, 0, 0, 0.8);
          animation: modalIn 0.4s cubic-bezier(0.16, 1, 0.3, 1) forwards;
        }

        .pqv-modal-left {
          background: rgba(0, 0, 0, 0.4);
          padding: 24px;
          display: flex;
          align-items: center;
          justify-content: center;
          border-right: 1px solid rgba(255, 255, 255, 0.06);
          overflow: hidden;
          min-height: 380px;
        }

        .pqv-modal-img {
          max-width: 100%;
          max-height: 60vh;
          object-fit: contain;
          border-radius: 12px;
          box-shadow: 0 10px 30px rgba(0,0,0,0.5);
          border: 1px solid rgba(255,255,255,0.08);
        }

        .pqv-modal-text {
          padding: 32px;
          display: flex;
          flex-direction: column;
          background: rgba(10, 10, 18, 0.98);
        }

        .pqv-ocr-header {
          font-size: 15px;
          font-weight: 700;
          color: #06b6d4;
          margin-bottom: 16px;
          display: flex;
          align-items: center;
          gap: 10px;
          font-family: var(--font-heading);
        }

        .pqv-ocr-body {
          font-family: 'JetBrains Mono', monospace;
          font-size: 12px;
          color: #cbd5e1;
          background: rgba(0, 0, 0, 0.45);
          border: 1px solid rgba(255, 255, 255, 0.05);
          border-radius: 12px;
          padding: 20px;
          flex: 1;
          overflow-y: auto;
          white-space: pre-wrap;
          line-height: 1.8;
          margin: 0;
          box-shadow: inset 0 2px 8px rgba(0,0,0,0.5);
        }

        .pqv-copy-btn {
          background: linear-gradient(135deg, #06b6d4, #0891b2);
          color: white;
          border: none;
          border-radius: 12px;
          padding: 12px 24px;
          font-weight: 600;
          font-size: 14px;
          cursor: pointer;
          margin-top: 20px;
          transition: all 0.2s cubic-bezier(0.16, 1, 0.3, 1);
          box-shadow: 0 4px 15px rgba(6, 182, 212, 0.2);
        }

        .pqv-copy-btn:hover {
          opacity: 0.95;
          transform: translateY(-2px);
          box-shadow: 0 6px 20px rgba(6, 182, 212, 0.3);
        }

        .pqv-close-btn {
          position: absolute;
          top: 16px;
          right: 16px;
          background: rgba(255, 255, 255, 0.08);
          color: white;
          border: 1px solid rgba(255,255,255,0.06);
          width: 36px;
          height: 36px;
          border-radius: 50%;
          cursor: pointer;
          font-size: 14px;
          display: flex;
          align-items: center;
          justify-content: center;
          z-index: 10;
          transition: all 0.2s;
        }

        .pqv-close-btn:hover {
          background: rgba(255, 255, 255, 0.15);
          transform: rotate(90deg);
        }

        /* Toast Alert */
        .pqv-toast {
          position: fixed;
          bottom: 32px;
          left: 50%;
          transform: translateX(-50%);
          background: rgba(10, 20, 30, 0.85);
          backdrop-filter: blur(8px);
          border: 1px solid rgba(6, 182, 212, 0.3);
          color: #06b6d4;
          padding: 14px 32px;
          border-radius: 14px;
          font-size: 14px;
          font-weight: 600;
          box-shadow: 0 10px 30px rgba(0, 0, 0, 0.5), 0 0 15px rgba(6, 182, 212, 0.1);
          z-index: 1100;
          animation: pqvToastIn 0.3s cubic-bezier(0.16, 1, 0.3, 1) forwards;
          display: flex;
          align-items: center;
          gap: 8px;
        }

        @keyframes modalIn {
          from { opacity: 0; transform: scale(0.96) translateY(15px); }
          to { opacity: 1; transform: scale(1) translateY(0); }
        }

        @keyframes pqvFadeIn {
          from { opacity: 0; }
          to { opacity: 1; }
        }

        @keyframes pqvToastIn {
          from { opacity: 0; transform: translateX(-50%) translateY(15px); }
          to { opacity: 1; transform: translateX(-50%) translateY(0); }
        }

        @media (max-width: 800px) {
          .pqv-grid {
            grid-template-columns: 1fr;
            gap: 20px;
          }

          .pqv-modal {
            grid-template-columns: 1fr;
          }

          .pqv-modal-left {
            min-height: 200px;
            border-right: none;
            border-bottom: 1px solid rgba(255, 255, 255, 0.06);
          }

          .pqv-modal-img {
            max-height: 35vh;
          }
        }
      `}</style>

      <div className="pqv-badge">📸 Interactive Showcase</div>
      <h2 className="pqv-title">Real App Screenshot Quick-View</h2>
      <p className="pqv-subtitle">
        Inspect the active interfaces of FlyShelf across different states. Click any screenshot below to trigger local OCR text extraction.
      </p>

      <div className="pqv-grid">
        {samplePhotos.map((photo) => (
          <div
            key={photo.id}
            className="pqv-card"
            onClick={() => openModal(photo)}
          >
            <div className="pqv-card-image-wrapper">
              <img src={photo.imageUrl} alt={photo.title} className="pqv-card-img" />
              <div className="pqv-hover-overlay">
                <div className="pqv-zoom-badge">🔍 Inspect OCR ({photo.icon})</div>
              </div>
            </div>
            <div className="pqv-card-info">
              <div className="pqv-card-title-row">
                <p className="pqv-card-title">{photo.title}</p>
                <span className="pqv-card-icon">{photo.icon}</span>
              </div>
              <div className="pqv-card-timestamp">{photo.timestamp}</div>
            </div>
          </div>
        ))}
      </div>

      {selectedPhoto && (
        <div className="pqv-overlay" onClick={closeModal}>
          <div className="pqv-modal" onClick={(e) => e.stopPropagation()}>
            <button className="pqv-close-btn" onClick={closeModal}>
              ✕
            </button>
            
            <div className="pqv-modal-left">
              <img src={selectedPhoto.imageUrl} alt={selectedPhoto.title} className="pqv-modal-img" />
            </div>

            <div className="pqv-modal-text">
              <div className="pqv-ocr-header">
                ✨ Extracted App Text Content (FlyShelf OCR)
              </div>
              <pre className="pqv-ocr-body">{selectedPhoto.ocrText}</pre>
              <button className="pqv-copy-btn" onClick={handleCopy}>
                {copied ? '✓ Copied to Real Clipboard!' : '📋 Copy Clipboard Content'}
              </button>
            </div>
          </div>
        </div>
      )}

      {toast && (
        <div className="pqv-toast">
          <span>⚡</span> Synced directly to your actual computer clipboard!
        </div>
      )}
    </section>
  );
}
