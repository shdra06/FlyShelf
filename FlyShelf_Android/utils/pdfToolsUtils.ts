// PDF Tools Utility Functions — Extended operations using pdf-lib
// Separate from pdfUtils.ts to keep the original file intact

import { PDFDocument, degrees as pdfDegrees, rgb, StandardFonts } from 'pdf-lib';
import * as FileSystem from 'expo-file-system/legacy';

import { base64ToUint8Array, uint8ArrayToBase64 } from './networkHelpers';

// ═══════════════════════════════════════════
// HELPERS
// ═══════════════════════════════════════════

const OUTPUT_DIR = `${FileSystem.documentDirectory}FlyShelf/PDFTools/`;

/** Max age in ms for PDF output files before cleanup (7 days) */
const CLEANUP_MAX_AGE_MS = 7 * 24 * 60 * 60 * 1000;

async function ensureOutputDir() {
  await FileSystem.makeDirectoryAsync(OUTPUT_DIR, { intermediates: true }).catch(() => {});
}

function getOutputPath(prefix: string): string {
  return `${OUTPUT_DIR}${prefix}_${Date.now()}.pdf`;
}

/**
 * Removes PDF output files older than 7 days from the OUTPUT_DIR.
 * Call on app startup to prevent disk bloat from processed PDFs.
 */
export async function cleanupOldPdfFiles(): Promise<number> {
  try {
    await ensureOutputDir();
    const files = await FileSystem.readDirectoryAsync(OUTPUT_DIR);
    const now = Date.now();
    let removedCount = 0;
    for (const file of files) {
      try {
        const filePath = `${OUTPUT_DIR}${file}`;
        const info = await FileSystem.getInfoAsync(filePath);
        if (info.exists && !info.isDirectory && info.modificationTime) {
          const ageMs = now - info.modificationTime * 1000;
          if (ageMs > CLEANUP_MAX_AGE_MS) {
            await FileSystem.deleteAsync(filePath, { idempotent: true });
            removedCount++;
          }
        }
      } catch { /* skip individual file errors */ }
    }
    if (removedCount > 0) {
      console.log(`[PDFTools] Cleaned up ${removedCount} files older than 7 days.`);
    }
    return removedCount;
  } catch (e) {
    console.warn('[PDFTools] Cleanup failed:', e);
    return 0;
  }
}

async function readPdfBytes(uri: string): Promise<Uint8Array> {
  // content:// URIs are handled directly by expo-file-system
  if (uri.startsWith('content://')) {
    // Pre-read size guard for content:// URIs to prevent OOM
    try {
      const info = await FileSystem.getInfoAsync(uri);
      if (info.exists && 'size' in info && typeof info.size === 'number') {
        if (info.size > 30 * 1024 * 1024) {
          throw new Error(`This PDF is too large (${(info.size / 1024 / 1024).toFixed(1)}MB). The maximum supported file size is 30MB. Please use a smaller file or split it first.`);
        }
      }
    } catch (e: any) {
      if (e?.message?.includes('too large')) throw e;
    }
    let b64: string | null = await FileSystem.readAsStringAsync(uri, { encoding: FileSystem.EncodingType.Base64 });
    const bytes = base64ToUint8Array(b64);
    b64 = null; // Release for GC
    return bytes;
  }
  const fileUri = uri.startsWith('file://') ? uri : `file://${uri}`;
  // Guard: reject files > 30MB to prevent OOM in JS memory
  try {
    const info = await FileSystem.getInfoAsync(fileUri);
    if (info.exists && 'size' in info && typeof info.size === 'number') {
      if (info.size > 30 * 1024 * 1024) {
        throw new Error(`This PDF is too large (${(info.size / 1024 / 1024).toFixed(1)}MB). The maximum supported file size is 30MB. Please use a smaller file or split it first.`);
      }
    }
  } catch (e: any) {
    // Re-throw size limit errors, ignore other info check failures
    if (e?.message?.includes('too large')) throw e;
  }
  let b64: string | null = await FileSystem.readAsStringAsync(fileUri, { encoding: FileSystem.EncodingType.Base64 });
  const bytes = base64ToUint8Array(b64);
  b64 = null; // Release for GC
  return bytes;
}

async function readImageBytes(uri: string): Promise<Uint8Array> {
  // content:// URIs are handled directly by expo-file-system
  if (uri.startsWith('content://')) {
    // Pre-read size guard for content:// URIs to prevent OOM
    try {
      const info = await FileSystem.getInfoAsync(uri);
      if (info.exists && 'size' in info && typeof info.size === 'number') {
        if (info.size > 30 * 1024 * 1024) {
          throw new Error(`This image is too large (${(info.size / 1024 / 1024).toFixed(1)}MB). The maximum supported file size is 30MB.`);
        }
      }
    } catch (e: any) {
      if (e?.message?.includes('too large')) throw e;
    }
    let b64: string | null = await FileSystem.readAsStringAsync(uri, { encoding: FileSystem.EncodingType.Base64 });
    const bytes = base64ToUint8Array(b64);
    b64 = null; // Release for GC
    return bytes;
  }
  const fileUri = uri.startsWith('file://') ? uri : (uri.startsWith('/') ? `file://${uri}` : uri);
  let b64: string | null = await FileSystem.readAsStringAsync(fileUri, { encoding: FileSystem.EncodingType.Base64 });
  const bytes = base64ToUint8Array(b64);
  b64 = null; // Release for GC
  return bytes;
}

async function savePdf(doc: PDFDocument, prefix: string): Promise<string> {
  await ensureOutputDir();
  const outPath = getOutputPath(prefix);
  const bytes = await doc.save();
  let b64: string | null = uint8ArrayToBase64(bytes);
  await FileSystem.writeAsStringAsync(outPath, b64, { encoding: FileSystem.EncodingType.Base64 });
  b64 = null; // Release for GC
  return outPath;
}

// ═══════════════════════════════════════════
// PDF OPERATIONS
// ═══════════════════════════════════════════

/** Split a PDF into multiple files by page ranges (1-indexed) */
export async function splitPdf(
  pdfPath: string,
  ranges: { start: number; end: number }[]
): Promise<string[]> {
  const bytes = await readPdfBytes(pdfPath);
  const source = await PDFDocument.load(bytes, { ignoreEncryption: true });
  const totalPages = source.getPageCount();
  const outputPaths: string[] = [];

  for (let i = 0; i < ranges.length; i++) {
    const { start, end } = ranges[i];
    const s = Math.max(0, start - 1);
    const e = Math.min(totalPages - 1, end - 1);
    if (s > e) continue;

    const indices = Array.from({ length: e - s + 1 }, (_, k) => s + k);
    const newDoc = await PDFDocument.create();
    const copied = await newDoc.copyPages(source, indices);
    copied.forEach(p => newDoc.addPage(p));

    const path = await savePdf(newDoc, `split_${i + 1}`);
    outputPaths.push(path);
  }

  if (outputPaths.length === 0) {
    throw new Error('Split produced no output files — all page ranges were invalid or out of bounds.');
  }

  return outputPaths;
}

/** Reorder pages in a PDF (0-indexed new order array) */
export async function reorderPages(pdfPath: string, newOrder: number[]): Promise<string> {
  const bytes = await readPdfBytes(pdfPath);
  const source = await PDFDocument.load(bytes, { ignoreEncryption: true });
  const newDoc = await PDFDocument.create();
  const copied = await newDoc.copyPages(source, newOrder);
  copied.forEach(p => newDoc.addPage(p));
  return savePdf(newDoc, 'reordered');
}

/** Edit pages in a single pass: reorder, delete, and rotate each page simultaneously */
export async function editPdfPages(
  pdfPath: string,
  pages: { index: number; rotation: number }[]
): Promise<string> {
  const bytes = await readPdfBytes(pdfPath);
  const source = await PDFDocument.load(bytes, { ignoreEncryption: true });
  const newDoc = await PDFDocument.create();
  const indices = pages.map(p => p.index);
  const copied = await newDoc.copyPages(source, indices);
  copied.forEach((p, i) => {
    const rot = pages[i].rotation;
    if (rot) {
      const current = p.getRotation().angle;
      p.setRotation(pdfDegrees((current + rot) % 360));
    }
    newDoc.addPage(p);
  });
  return savePdf(newDoc, 'edited_pages');
}

/** Rotate specific pages (1-indexed pageNumbers, degrees: 0|90|180|270) */
export async function rotatePages(
  pdfPath: string,
  pageNumbers: number[],
  degreesVal: 0 | 90 | 180 | 270
): Promise<string> {
  const bytes = await readPdfBytes(pdfPath);
  const doc = await PDFDocument.load(bytes, { ignoreEncryption: true });
  for (const pn of pageNumbers) {
    const idx = pn - 1;
    if (idx >= 0 && idx < doc.getPageCount()) {
      const page = doc.getPage(idx);
      const current = page.getRotation().angle;
      page.setRotation(pdfDegrees((current + degreesVal) % 360));
    }
  }
  return savePdf(doc, 'rotated');
}

/** Convert multiple images to a single PDF */
export async function imagesToPdf(imagePaths: string[]): Promise<string> {
  const doc = await PDFDocument.create();
  for (const imgPath of imagePaths) {
    try {
      const imgBytes = await readImageBytes(imgPath);
      const lower = imgPath.toLowerCase();
      let img;
      if (lower.endsWith('.png')) {
        img = await doc.embedPng(imgBytes);
      } else {
        try { img = await doc.embedJpg(imgBytes); }
        catch { img = await doc.embedPng(imgBytes); }
      }
      const { width, height } = img.scale(1.0);
      const page = doc.addPage([width, height]);
      page.drawImage(img, { x: 0, y: 0, width, height });
    } catch (e: any) {
      const filename = imgPath.split('/').pop() || imgPath;
      throw new Error(`Failed to embed image "${filename}": unsupported format or corrupted file. Only PNG and JPEG are supported.`);
    }
  }
  return savePdf(doc, 'images_to_pdf');
}

/** Add a diagonal text watermark to every page */
export async function addWatermark(
  pdfPath: string,
  text: string,
  options?: { opacity?: number; fontSize?: number; color?: { r: number; g: number; b: number }; rotation?: number }
): Promise<string> {
  const bytes = await readPdfBytes(pdfPath);
  const doc = await PDFDocument.load(bytes, { ignoreEncryption: true });
  const font = await doc.embedFont(StandardFonts.Helvetica);
  const opacity = options?.opacity ?? 0.15;
  const fontSize = options?.fontSize ?? 48;
  const c = options?.color ?? { r: 0.5, g: 0.5, b: 0.5 };
  const rotation = options?.rotation ?? -45;

  const pages = doc.getPages();
  for (const page of pages) {
    const { width, height } = page.getSize();
    const textWidth = font.widthOfTextAtSize(text, fontSize);
    page.drawText(text, {
      x: (width - textWidth * Math.cos(Math.abs(rotation) * Math.PI / 180)) / 2,
      y: height / 2,
      size: fontSize,
      font,
      color: rgb(c.r, c.g, c.b),
      opacity,
      rotate: pdfDegrees(rotation),
    });
  }
  return savePdf(doc, 'watermarked');
}

/**
 * Copy PDF — pdf-lib does NOT support native encryption.
 * WARNING: The password parameter is accepted for API compatibility but is NOT applied.
 */
export async function protectPdf(pdfPath: string, _password: string): Promise<string> {
  if (_password) {
    throw new Error(
      'PDF password protection requires a native module not yet installed. ' +
      'Please use FlyShelf on a paired PC to apply password protection.'
    );
  }
  const bytes = await readPdfBytes(pdfPath);
  const doc = await PDFDocument.load(bytes, { ignoreEncryption: true });
  return savePdf(doc, 'protected_copy');
}

/** Get detailed PDF information */
export async function getPdfInfo(pdfPath: string): Promise<{
  pageCount: number;
  title?: string;
  author?: string;
  subject?: string;
  creator?: string;
  producer?: string;
  creationDate?: string;
  modificationDate?: string;
  isEncrypted: boolean;
  pages: { width: number; height: number; rotation: number }[];
}> {
  const bytes = await readPdfBytes(pdfPath);
  const doc = await PDFDocument.load(bytes, { ignoreEncryption: true });
  return {
    pageCount: doc.getPageCount(),
    title: doc.getTitle() ?? undefined,
    author: doc.getAuthor() ?? undefined,
    subject: doc.getSubject() ?? undefined,
    creator: doc.getCreator() ?? undefined,
    producer: doc.getProducer() || '',
    creationDate: doc.getCreationDate()?.toISOString() || '',
    modificationDate: doc.getModificationDate()?.toISOString() || '',
    isEncrypted: false,
    pages: doc.getPages().map(p => {
      const { width, height } = p.getSize();
      return { width, height, rotation: p.getRotation().angle };
    }),
  };
}

/** Set PDF metadata fields */
export async function setPdfMetadata(
  pdfPath: string,
  metadata: { title?: string; author?: string; subject?: string; keywords?: string[] }
): Promise<string> {
  const bytes = await readPdfBytes(pdfPath);
  const doc = await PDFDocument.load(bytes, { ignoreEncryption: true });
  if (metadata.title !== undefined) doc.setTitle(metadata.title);
  if (metadata.author !== undefined) doc.setAuthor(metadata.author);
  if (metadata.subject !== undefined) doc.setSubject(metadata.subject);
  if (metadata.keywords !== undefined) doc.setKeywords(metadata.keywords);
  doc.setProducer('FlyShelf PDF Tools');
  return savePdf(doc, 'metadata_updated');
}

/** Insert image pages into an existing PDF at a specific position (0-indexed insertAt) */
export async function addImagePages(
  pdfPath: string,
  insertAt: number,
  imagePaths: string[]
): Promise<string> {
  const bytes = await readPdfBytes(pdfPath);
  const doc = await PDFDocument.load(bytes, { ignoreEncryption: true });
  const totalPages = doc.getPageCount();
  const pos = Math.min(Math.max(0, insertAt), totalPages);

  // Build image pages into a temp doc, then copy them
  const tempDoc = await PDFDocument.create();
  for (const imgPath of imagePaths) {
    const imgBytes = await readImageBytes(imgPath);
    const lower = imgPath.toLowerCase();
    let img;
    if (lower.endsWith('.png')) {
      img = await tempDoc.embedPng(imgBytes);
    } else {
      try { img = await tempDoc.embedJpg(imgBytes); }
      catch { img = await tempDoc.embedPng(imgBytes); }
    }
    const { width, height } = img.scale(1.0);
    const page = tempDoc.addPage([width, height]);
    page.drawImage(img, { x: 0, y: 0, width, height });
  }

  // Copy image pages into a new document with proper ordering
  const result = await PDFDocument.create();
  // Pages before insert point
  if (pos > 0) {
    const before = await result.copyPages(doc, Array.from({ length: pos }, (_, i) => i));
    before.forEach(p => result.addPage(p));
  }
  // Image pages
  const imgPages = await result.copyPages(tempDoc, tempDoc.getPageIndices());
  imgPages.forEach(p => result.addPage(p));
  // Pages after insert point
  if (pos < totalPages) {
    const after = await result.copyPages(doc, Array.from({ length: totalPages - pos }, (_, i) => pos + i));
    after.forEach(p => result.addPage(p));
  }

  return savePdf(result, 'pages_added');
}

/** Optimize & compress PDF by rendering pages to images */
export async function compressPdf(
  pdfPath: string,
  quality: 'low' | 'medium' | 'high' = 'medium'
): Promise<string> {
  const bytes = await readPdfBytes(pdfPath);
  const source = await PDFDocument.load(bytes, { ignoreEncryption: true });
  const totalPages = source.getPageCount();

  let thumbnailMod;
  try {
    thumbnailMod = require('react-native-pdf-thumbnail');
  } catch (e) {
    console.warn('[PDFTools] react-native-pdf-thumbnail not found, falling back to simple compression');
    thumbnailMod = null;
  }

  // If thumbnail module is available, render each page to an image and embed it
  if (thumbnailMod?.generate) {
    const newDoc = await PDFDocument.create();
    const cleanUri = pdfPath.startsWith('file://') ? pdfPath : `file://${pdfPath}`;
    
    // Map quality string to react-native-pdf-thumbnail quality parameter (1-100)
    const qualityMap: Record<string, number> = { low: 40, medium: 60, high: 80 };
    const thumbQuality = qualityMap[quality] || 60;

    for (let i = 0; i < totalPages; i++) {
      try {
        const thumb = await thumbnailMod.generate(cleanUri, i, thumbQuality);
        if (thumb?.uri) {
          const imgBytes = await readImageBytes(thumb.uri);
          let img;
          try {
            img = await newDoc.embedJpg(imgBytes);
          } catch {
            img = await newDoc.embedPng(imgBytes);
          }
          const { width, height } = img.scale(1.0);
          const page = newDoc.addPage([width, height]);
          page.drawImage(img, { x: 0, y: 0, width, height });
        }
      } catch (e) {
        // Fallback for this page to just copy it
        const [copied] = await newDoc.copyPages(source, [i]);
        newDoc.addPage(copied);
      }
    }
    const title = source.getTitle();
    if (title) newDoc.setTitle(title);
    newDoc.setProducer(`FlyShelf PDF Compressor (Quality: ${quality})`);
    return savePdf(newDoc, `compressed_${quality}`);
  } else {
    // Fallback: simple copy
    const newDoc = await PDFDocument.create();
    const copied = await newDoc.copyPages(source, source.getPageIndices());
    copied.forEach(p => newDoc.addPage(p));
    const title = source.getTitle();
    if (title) newDoc.setTitle(title);
    newDoc.setProducer('FlyShelf PDF Compressor');
    return savePdf(newDoc, 'compressed');
  }
}
