"use client";
import { useState, useRef } from 'react';

const PROGRESS_MESSAGES = [
  'Allocating virtual page streams...',
  'Merging catalog resources...',
  'Generating PDF document catalog...',
  'Optimizing compressed memory buffer...',
];

export default function LivePdfMerger() {
  const [files, setFiles] = useState([
    { name: 'CheXthought.pdf', checked: false },
    { name: 'mooc admit card natural hazards.pdf', checked: false },
    { name: 'Merged_20260520_171016.pdf', checked: true },
    { name: 'Merged_20260520_170913.pdf', checked: true },
  ]);

  const [phase, setPhase] = useState('ready');
  const [progressMsg, setProgressMsg] = useState('');
  const [toast, setToast] = useState(false);
  const intervalRef = useRef(null);

  const toggleCheck = (index) => {
    const updated = [...files];
    updated[index].checked = !updated[index].checked;
    setFiles(updated);
  };

  const checkedCount = files.filter(f => f.checked).length;

  const handleMerge = () => {
    if (checkedCount < 2) return;
    setPhase('merging');
    
    let msgIdx = 0;
    setProgressMsg(PROGRESS_MESSAGES[0]);
    intervalRef.current = setInterval(() => {
      msgIdx++;
      if (msgIdx < PROGRESS_MESSAGES.length) {
        setProgressMsg(PROGRESS_MESSAGES[msgIdx]);
      }
    }, 700);

    setTimeout(() => {
      clearInterval(intervalRef.current);
      setPhase('done');
      
      // Copy success status to actual system clipboard
      try {
        navigator.clipboard.writeText(`Success: Merged ${checkedCount} files into a single FlyShelf document!`);
        setToast(true);
        setTimeout(() => setToast(false), 2500);
      } catch (e) {
        // ignore
      }
    }, 3000);
  };

  const triggerDownload = () => {
    // Generate a real client-side PDF file filled with pre-rendered dummy text about FlyShelf!
    const pdfContent = `%PDF-1.4
1 0 obj
<< /Type /Catalog /Pages 2 0 R >>
endobj
2 0 obj
<< /Type /Pages /Kids [ 3 0 R ] /Count 1 >>
endobj
3 0 obj
<< /Type /Page /Parent 2 0 R /MediaBox [ 0 0 612 792 ] /Resources 4 0 R /Contents 5 0 R >>
endobj
4 0 obj
<< /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >>
endobj
5 0 obj
<< /Length 260 >>
stream
BT
/F1 20 Tf
70 700 Td
(FlyShelf Premium PDF Merger Success) Tj
/F1 12 Tf
0 -40 Td
(This PDF file was successfully stitched completely offline inside) Tj
0 -20 Td
(your browser. No remote servers were contacted.) Tj
0 -30 Td
(FlyShelf features three-tier LAN and cloud sync routing, AI-powered) Tj
0 -20 Td
(Gemini OCR text-extraction, and zero telemetry collection.) Tj
0 -40 Td
(Enjoy absolute offline file sharing and cross-device speed!) Tj
ET
endstream
endobj
xref
0 6
0000000000 65535 f 
0000000009 00000 n 
0000000056 00000 n 
0000000111 00000 n 
0000000222 00000 n 
0000000305 00000 n 
trailer
<< /Size 6 /Root 1 0 R >>
startxref
615
%%EOF`;

    const blob = new Blob([pdfContent], { type: 'application/pdf' });
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = `FlyShelf_Merged_${Date.now().toString().slice(-6)}.pdf`;
    document.body.appendChild(link);
    link.click();
    document.body.removeChild(link);
  };

  const handleReset = () => {
    setPhase('ready');
    setFiles([
      { name: 'CheXthought.pdf', checked: false },
      { name: 'mooc admit card natural hazards.pdf', checked: false },
      { name: 'Merged_20260520_171016.pdf', checked: true },
      { name: 'Merged_20260520_170913.pdf', checked: true },
    ]);
  };

  return (
    <>
      <style jsx>{`
        .pdf-merger-window {
          background: rgba(10, 10, 20, 0.75);
          border: 1px solid rgba(255,255,255,0.08);
          border-radius: 20px;
          padding: 0;
          backdrop-filter: blur(16px);
          position: relative;
          overflow: hidden;
          box-shadow: 0 20px 50px rgba(0, 0, 0, 0.6);
          font-family: var(--font-heading);
          max-width: 420px;
          margin: 0 auto;
          text-align: left;
        }

        .titlebar {
          display: flex;
          align-items: center;
          justify-content: space-between;
          padding: 14px 20px;
          background: rgba(255, 255, 255, 0.02);
          border-bottom: 1px solid rgba(255, 255, 255, 0.05);
        }

        .dots {
          display: flex;
          gap: 6px;
        }

        .dot {
          width: 9px;
          height: 9px;
          border-radius: 50%;
        }

        .title-text {
          font-size: 11px;
          font-weight: 600;
          color: rgba(255, 255, 255, 0.4);
          font-family: var(--font-mono);
        }

        .header-controls {
          display: flex;
          gap: 12px;
          font-size: 13px;
          color: rgba(255,255,255,0.4);
        }

        /* Filter Pills Row styling matching Screenshot 2 */
        .filter-row {
          display: flex;
          gap: 8px;
          padding: 14px 20px;
          background: rgba(255, 255, 255, 0.01);
          border-bottom: 1px solid rgba(255, 255, 255, 0.04);
        }

        .filter-pill {
          padding: 6px 12px;
          font-size: 11px;
          font-weight: 700;
          border-radius: 6px;
          border: 1px solid rgba(255,255,255,0.06);
          background: rgba(255,255,255,0.02);
          color: rgba(255, 255, 255, 0.6);
          cursor: pointer;
          transition: all 0.25s ease;
        }

        .filter-pill-active {
          border-color: #ef4444;
          background: rgba(239, 68, 68, 0.15);
          color: #ef4444;
        }

        .file-list {
          padding: 16px 20px;
          display: flex;
          flex-direction: column;
          gap: 10px;
          min-height: 240px;
        }

        .file-item {
          display: flex;
          align-items: center;
          gap: 12px;
          padding: 12px 14px;
          border-radius: 12px;
          border: 1px solid rgba(255, 255, 255, 0.04);
          background: rgba(255, 255, 255, 0.02);
          cursor: pointer;
          transition: all 0.25s cubic-bezier(0.16, 1, 0.3, 1);
        }

        .file-item:hover {
          background: rgba(255, 255, 255, 0.04);
          border-color: rgba(255, 255, 255, 0.1);
        }

        .file-checkbox {
          width: 18px;
          height: 18px;
          border-radius: 6px;
          border: 1.5px solid rgba(255,255,255,0.25);
          display: flex;
          align-items: center;
          justify-content: center;
          transition: all 0.2s;
          flex-shrink: 0;
        }

        .file-checkbox-checked {
          border-color: #ef4444;
          background: #ef4444;
        }

        .check-tick {
          color: white;
          font-weight: bold;
          font-size: 11px;
        }

        .file-icon {
          font-size: 16px;
          color: #ef4444;
        }

        .file-name {
          font-size: 12.5px;
          font-weight: 500;
          color: #e2e8f0;
          white-space: nowrap;
          overflow: hidden;
          text-overflow: ellipsis;
        }

        /* Floating Merge Button at bottom center */
        .merge-btn-container {
          padding: 20px;
          display: flex;
          justify-content: center;
          background: linear-gradient(to top, rgba(0,0,0,0.5), transparent);
        }

        .merge-btn {
          background: #1e293b;
          border: 1.5px solid rgba(255,255,255,0.1);
          color: #cbd5e1;
          font-weight: 700;
          font-size: 13.5px;
          padding: 12px 28px;
          border-radius: 99px;
          cursor: pointer;
          display: flex;
          align-items: center;
          gap: 10px;
          transition: all 0.3s cubic-bezier(0.16, 1, 0.3, 1);
          box-shadow: 0 4px 15px rgba(0,0,0,0.4);
        }

        .merge-btn-active {
          background: #1e1b4b;
          border-color: #6366f1;
          color: #e2e8f0;
          box-shadow: 0 8px 25px rgba(99, 102, 241, 0.25);
        }

        .merge-btn-active:hover {
          transform: translateY(-2px);
          box-shadow: 0 12px 30px rgba(99, 102, 241, 0.35);
        }

        .merge-btn-disabled {
          opacity: 0.5;
          cursor: not-allowed;
        }

        /* Merging overlays */
        .merging-overlay {
          position: absolute;
          inset: 0;
          background: rgba(10, 10, 20, 0.9);
          backdrop-filter: blur(10px);
          display: flex;
          flex-direction: column;
          align-items: center;
          justify-content: center;
          gap: 20px;
          z-index: 100;
        }

        .spinner {
          width: 42px;
          height: 42px;
          border: 3.5px solid rgba(239, 68, 68, 0.15);
          border-top-color: #ef4444;
          border-radius: 50%;
          animation: spin 0.8s linear infinite;
        }

        @keyframes spin {
          to { transform: rotate(360deg); }
        }

        .spinner-msg {
          font-size: 12px;
          color: #ef4444;
          font-family: var(--font-mono);
          letter-spacing: 0.5px;
        }

        /* Success Output view */
        .success-view {
          padding: 30px 20px;
          display: flex;
          flex-direction: column;
          align-items: center;
          gap: 16px;
          min-height: 240px;
          justify-content: center;
        }

        .success-badge {
          width: 54px;
          height: 54px;
          background: rgba(16, 185, 129, 0.15);
          border: 1px solid rgba(16, 185, 129, 0.3);
          border-radius: 50%;
          display: flex;
          align-items: center;
          justify-content: center;
          font-size: 24px;
          box-shadow: 0 0 15px rgba(16, 185, 129, 0.15);
        }

        .success-info {
          text-align: center;
        }

        .success-title {
          font-size: 15px;
          font-weight: 700;
          color: #34d399;
          margin: 0;
        }

        .success-desc {
          font-size: 12px;
          color: rgba(255, 255, 255, 0.45);
          margin-top: 4px;
        }

        .action-row {
          display: flex;
          gap: 12px;
          margin-top: 10px;
          width: 100%;
        }

        .action-btn {
          flex: 1;
          padding: 10px 16px;
          border-radius: 10px;
          font-size: 12px;
          font-weight: 700;
          cursor: pointer;
          transition: all 0.2s;
          text-align: center;
        }

        .download-btn {
          background: #10b981;
          color: white;
          border: none;
          box-shadow: 0 4px 15px rgba(16, 185, 129, 0.2);
        }

        .download-btn:hover {
          opacity: 0.95;
          transform: translateY(-1px);
        }

        .again-btn {
          background: rgba(255, 255, 255, 0.05);
          border: 1px solid rgba(255,255,255,0.08);
          color: #cbd5e1;
        }

        .again-btn:hover {
          background: rgba(255, 255, 255, 0.1);
        }

        /* Success Toast */
        .toast {
          position: fixed;
          bottom: 32px;
          left: 50%;
          transform: translateX(-50%);
          background: rgba(16, 185, 129, 0.15);
          border: 1px solid rgba(16, 185, 129, 0.3);
          color: #34d399;
          padding: 12px 28px;
          border-radius: 12px;
          font-size: 13.5px;
          font-weight: 600;
          box-shadow: 0 10px 30px rgba(0, 0, 0, 0.4);
          z-index: 1000;
          animation: pqvToastIn 0.3s cubic-bezier(0.16, 1, 0.3, 1) forwards;
        }

        @keyframes pqvToastIn {
          from { opacity: 0; transform: translateX(-50%) translateY(15px); }
          to { opacity: 1; transform: translateX(-50%) translateY(0); }
        }
      `}</style>

      <div className="pdf-merger-window">
        {/* Title Bar */}
        <div className="titlebar">
          <div className="dots">
            <span className="dot" style={{ background: '#ef4444' }} />
            <span className="dot" style={{ background: '#eab308' }} />
            <span className="dot" style={{ background: '#22c55e' }} />
          </div>
          <span className="title-text">FlyShelf PDF Stitcher</span>
          <div className="header-controls">
            <span>⚙️</span>
            <span>✕</span>
          </div>
        </div>

        {/* Filter Tab matching your Screenshot 2 */}
        <div className="filter-row">
          <span className="filter-pill">Img</span>
          <span className="filter-pill">Pin</span>
          <span className="filter-pill filter-pill-active">PDF</span>
          <span className="filter-pill">Doc</span>
          <span className="filter-pill">✕</span>
        </div>

        {/* Dynamic Loading Stitcher Overlay */}
        {phase === 'merging' && (
          <div className="merging-overlay">
            <div className="spinner" />
            <div className="spinner-msg">{progressMsg}</div>
          </div>
        )}

        {/* Core State Render */}
        {phase !== 'done' ? (
          <>
            <div className="file-list">
              {files.map((file, index) => (
                <div key={index} className="file-item" onClick={() => toggleCheck(index)}>
                  <div className={`file-checkbox ${file.checked ? 'file-checkbox-checked' : ''}`}>
                    {file.checked && <span className="check-tick">✓</span>}
                  </div>
                  <span className="file-icon">📄</span>
                  <span className="file-name">{file.name}</span>
                </div>
              ))}
            </div>

            <div className="merge-btn-container">
              <button
                className={`merge-btn ${checkedCount >= 2 ? 'merge-btn-active' : 'merge-btn-disabled'}`}
                disabled={checkedCount < 2}
                onClick={handleMerge}
              >
                <span>📁</span>
                {checkedCount >= 2 ? `Merge ${checkedCount} Files` : 'Select 2+ files to merge'}
              </button>
            </div>
          </>
        ) : (
          <div className="success-view">
            <div className="success-badge">✅</div>
            <div className="success-info">
              <p className="success-title">Stitched PDF Ready!</p>
              <p className="success-desc">
                Combined {checkedCount} PDFs into a single locally compiled file (1.4 MB)
              </p>
            </div>

            <div className="action-row">
              <button className="action-btn download-btn" onClick={triggerDownload}>
                📥 Download Merged PDF
              </button>
              <button className="action-btn again-btn" onClick={handleReset}>
                Stitch Again
              </button>
            </div>
          </div>
        )}
      </div>

      {/* Copy success toast notification */}
      {toast && (
        <div className="toast">
          ✨ Merged token copied directly to your physical clipboard!
        </div>
      )}
    </>
  );
}
