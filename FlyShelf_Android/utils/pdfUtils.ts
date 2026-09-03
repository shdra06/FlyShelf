// PDF Utility Functions — On-device PDF merge + page extraction using pdf-lib
// Optimized for memory efficiency (JSI-based Base64) with safety guards
import { PDFDocument } from 'pdf-lib';
import * as FileSystem from 'expo-file-system/legacy';
import { base64ToUint8Array, uint8ArrayToBase64 } from './networkHelpers';

/** Maximum single file size for JS-side processing (30MB) */
const MAX_SINGLE_FILE_BYTES = 30 * 1024 * 1024;
/** Maximum cumulative size across all files in a merge operation (80MB) */
const MAX_CUMULATIVE_MERGE_BYTES = 80 * 1024 * 1024;
/** Maximum image size for conversion to PDF (20MB) */
const MAX_IMAGE_BYTES = 20 * 1024 * 1024;
/** Maximum image dimension when embedding into PDFs (points). Prevents absurdly large pages. */
const MAX_PDF_PAGE_DIMENSION = 2000;

/**
 * Get file size safely, returns 0 if unknown.
 */
async function getFileSize(uri: string): Promise<number> {
  try {
    const info = await FileSystem.getInfoAsync(uri);
    if (info.exists && 'size' in info && typeof info.size === 'number') return info.size;
  } catch {}
  return 0;
}

/**
 * Read a file from a URI (local, content://, or http) and return its bytes as Uint8Array.
 * Uses JSI-based react-native-quick-base64 for 10x faster decoding and lower memory.
 * Handles file://, content://, and http:// URIs.
 */
async function readFileBytes(uri: string, maxBytes: number = MAX_SINGLE_FILE_BYTES): Promise<Uint8Array> {
  const isRemote = uri.startsWith('http://') || uri.startsWith('https://');
  const isContent = uri.startsWith('content://');

  // content:// URIs are handled directly by expo-file-system — no path transform needed
  if (isContent) {
    // C-2 fix: Check size BEFORE loading entire file into memory
    const fileSize = await getFileSize(uri);
    if (fileSize > 0 && fileSize > maxBytes) {
      throw new Error(`This file is too large (${(fileSize / 1024 / 1024).toFixed(1)}MB). Maximum supported size is ${Math.round(maxBytes / 1024 / 1024)}MB.`);
    }
    let b64: string | null = await FileSystem.readAsStringAsync(uri, { encoding: FileSystem.EncodingType.Base64 });
    // Post-read size guard (in case getInfoAsync couldn't report size for content:// URI)
    const actualSize = b64.length * 0.75; // approximate decoded size
    if (actualSize > maxBytes) {
      b64 = null;
      throw new Error(`This file is too large (~${(actualSize / 1024 / 1024).toFixed(1)}MB). Maximum supported size is ${Math.round(maxBytes / 1024 / 1024)}MB.`);
    }
    const bytes = base64ToUint8Array(b64);
    b64 = null; // Release Base64 for GC
    return bytes;
  }

  let localUri = isRemote
    ? `${FileSystem.cacheDirectory}pdf_temp_${Date.now()}.pdf`
    : (uri.startsWith('file://') ? uri : `file://${uri}`);

  // Guard: reject files > maxBytes to prevent OOM in JS memory
  if (!isRemote) {
    const fileSize = await getFileSize(localUri);
    if (fileSize > maxBytes) {
      throw new Error(`This file is too large (${(fileSize / 1024 / 1024).toFixed(1)}MB). Maximum supported size is ${Math.round(maxBytes / 1024 / 1024)}MB.`);
    }
  }

  try {
    if (isRemote) {
      await FileSystem.downloadAsync(uri, localUri, {
        headers: { 'X-FlyShelf-Client': 'MobileCompanion' },
      });
      // Validate downloaded file size
      const dlSize = await getFileSize(localUri);
      if (dlSize > maxBytes) {
        await FileSystem.deleteAsync(localUri, { idempotent: true }).catch(() => {});
        throw new Error(`Downloaded file is too large (${(dlSize / 1024 / 1024).toFixed(1)}MB). Maximum supported size is ${Math.round(maxBytes / 1024 / 1024)}MB.`);
      }
    }

    // Read as Base64 — currently the only way to get binary data into JS from expo-file-system
    let b64: string | null = await FileSystem.readAsStringAsync(localUri, { encoding: FileSystem.EncodingType.Base64 });
    
    // Memory optimization: Clean up temp file immediately
    if (isRemote) await FileSystem.deleteAsync(localUri, { idempotent: true });

    // Use JSI-based decoder (Fast & Memory Efficient)
    const bytes = base64ToUint8Array(b64);
    // Release the Base64 string for GC immediately — prevents 3x memory amplification
    b64 = null;
    return bytes;
  } catch (e) {
    if (isRemote) await FileSystem.deleteAsync(localUri, { idempotent: true }).catch(() => {});
    throw e;
  }
}

// Backward-compatible alias
const readPdfBytes = readFileBytes;

/**
 * Detect if a file is a PDF.
 * Uses magic bytes (%PDF-) for reliable detection on content:// URIs.
 * Falls back to extension check when magic bytes can't be read.
 */
async function isPdfFileAsync(uri: string): Promise<boolean> {
  const lower = uri.toLowerCase();
  // Fast path: clear extension match
  if (lower.endsWith('.pdf')) return true;
  const imageExts = ['.png', '.jpg', '.jpeg', '.gif', '.webp', '.bmp', '.heic', '.heif'];
  if (imageExts.some(ext => lower.endsWith(ext))) return false;

  // For content:// or ambiguous URIs: read first 5 bytes to check %PDF- magic header
  try {
    // Read a tiny slice (enough for the magic bytes) as base64
    const b64 = await FileSystem.readAsStringAsync(uri, {
      encoding: FileSystem.EncodingType.Base64,
      length: 8,
      position: 0,
    });
    // Decode first 5 chars: %PDF-
    const decoded = atob(b64).substring(0, 5);
    return decoded === '%PDF-';
  } catch {
    // If we can't read the file, fall back to false (treat as image)
    return false;
  }
}

// Synchronous fallback for contexts where async isn't possible
function isPdfFile(uri: string): boolean {
  const lower = uri.toLowerCase();
  if (lower.endsWith('.pdf')) return true;
  const imageExts = ['.png', '.jpg', '.jpeg', '.gif', '.webp', '.bmp', '.heic', '.heif'];
  if (imageExts.some(ext => lower.endsWith(ext))) return false;
  // For content:// URIs without extension, assume PDF (caller should use isPdfFileAsync)
  if (lower.startsWith('content://')) return true;
  return false;
}

/**
 * Scale image dimensions to fit within MAX_PDF_PAGE_DIMENSION while preserving aspect ratio.
 */
function clampDimensions(width: number, height: number): { width: number; height: number } {
  if (width <= MAX_PDF_PAGE_DIMENSION && height <= MAX_PDF_PAGE_DIMENSION) {
    return { width, height };
  }
  const scale = Math.min(MAX_PDF_PAGE_DIMENSION / width, MAX_PDF_PAGE_DIMENSION / height);
  return { width: Math.round(width * scale), height: Math.round(height * scale) };
}

/**
 * Merge multiple PDF and Image files into a single PDF.
 * Enforces a cumulative size limit to prevent OOM.
 */
export async function mergePdfs(fileUris: string[], outputPath: string): Promise<string> {
  // Pre-flight: check cumulative size (including content:// URIs)
  let cumulativeBytes = 0;
  for (const uri of fileUris) {
    if (!uri.startsWith('http://') && !uri.startsWith('https://')) {
      const fileUri = uri.startsWith('file://') || uri.startsWith('content://') ? uri : `file://${uri}`;
      const size = await getFileSize(fileUri);
      cumulativeBytes += size;
    }
  }
  if (cumulativeBytes > MAX_CUMULATIVE_MERGE_BYTES) {
    throw new Error(`Total file size (${(cumulativeBytes / 1024 / 1024).toFixed(1)}MB) exceeds the ${Math.round(MAX_CUMULATIVE_MERGE_BYTES / 1024 / 1024)}MB merge limit. Please merge fewer or smaller files.`);
  }

  const mergedPdf = await PDFDocument.create();
  const failedFiles: string[] = [];

  for (const uri of fileUris) {
    try {
      // Use async detection for reliable content:// URI handling
      const isPdf = await isPdfFileAsync(uri);
      if (isPdf) {
        const pdfBytes = await readPdfBytes(uri);
        const sourcePdf = await PDFDocument.load(pdfBytes, { ignoreEncryption: true });
        const pageIndices = sourcePdf.getPageIndices();
        const copiedPages = await mergedPdf.copyPages(sourcePdf, pageIndices);
        copiedPages.forEach(page => mergedPdf.addPage(page));
      } else {
        // Image file (.png, .jpg, .jpeg)
        const imageBytes = await readFileBytes(uri, MAX_IMAGE_BYTES);
        const lowerUri = uri.toLowerCase();
        let embeddedImage;
        if (lowerUri.endsWith('.png')) {
          embeddedImage = await mergedPdf.embedPng(imageBytes);
        } else {
          try {
            embeddedImage = await mergedPdf.embedJpg(imageBytes);
          } catch (e) {
            embeddedImage = await mergedPdf.embedPng(imageBytes);
          }
        }

        const natural = embeddedImage.scale(1.0);
        const { width, height } = clampDimensions(natural.width, natural.height);
        const page = mergedPdf.addPage([width, height]);
        page.drawImage(embeddedImage, { x: 0, y: 0, width, height });
      }
    } catch (err: any) {
      const fileName = uri.split('/').pop() || 'unknown';
      failedFiles.push(fileName);
      console.warn(`[pdfUtils] Failed to process ${fileName}: ${err?.message || err}`);
    }
  }

  if (mergedPdf.getPageCount() === 0) {
    const details = failedFiles.length > 0 ? ` Failed files: ${failedFiles.join(', ')}` : '';
    throw new Error(`No pages could be merged.${details}`);
  }

  const mergedBytes = await mergedPdf.save();
  let base64: string | null = uint8ArrayToBase64(mergedBytes);
  
  const dir = outputPath.substring(0, outputPath.lastIndexOf('/'));
  await FileSystem.makeDirectoryAsync(dir, { intermediates: true }).catch(() => {});
  
  await FileSystem.writeAsStringAsync(outputPath, base64, { encoding: FileSystem.EncodingType.Base64 });
  base64 = null; // Release for GC
  return outputPath;
}

/**
 * Convert an image file on-device to a single-page PDF.
 * Handles file://, content://, and bare paths.
 * Clamps large images to MAX_PDF_PAGE_DIMENSION to prevent oversized PDFs.
 */
export async function convertImageToPdf(imageUri: string, outputPath: string): Promise<string> {
  const pdfDoc = await PDFDocument.create();

  // Read image bytes — handles file://, content://, and bare paths
  const imageBytes = await readFileBytes(imageUri, MAX_IMAGE_BYTES);

  let embeddedImage;
  const lowerUri = imageUri.toLowerCase();
  
  if (lowerUri.endsWith('.png')) {
    embeddedImage = await pdfDoc.embedPng(imageBytes);
  } else {
    try {
      embeddedImage = await pdfDoc.embedJpg(imageBytes);
    } catch (e) {
      embeddedImage = await pdfDoc.embedPng(imageBytes);
    }
  }

  const natural = embeddedImage.scale(1.0);
  const { width, height } = clampDimensions(natural.width, natural.height);
  const page = pdfDoc.addPage([width, height]);
  page.drawImage(embeddedImage, { x: 0, y: 0, width, height });

  const pdfBytes = await pdfDoc.save();
  let pdfBase64: string | null = uint8ArrayToBase64(pdfBytes);

  const dir = outputPath.substring(0, outputPath.lastIndexOf('/'));
  await FileSystem.makeDirectoryAsync(dir, { intermediates: true }).catch(() => {});

  await FileSystem.writeAsStringAsync(outputPath, pdfBase64, { encoding: FileSystem.EncodingType.Base64 });
  pdfBase64 = null; // Release for GC
  return outputPath;
}

/**
 * Extract specific pages from a PDF and save as a new PDF.
 * Optionally accepts pre-loaded bytes to avoid double-loading.
 */
export async function extractPages(
  pdfUri: string,
  pageNumbers: number[],
  outputPath: string,
  preloadedBytes?: Uint8Array,
): Promise<string> {
  const pdfBytes = preloadedBytes || await readPdfBytes(pdfUri);
  const sourcePdf = await PDFDocument.load(pdfBytes, { ignoreEncryption: true });
  const newPdf = await PDFDocument.create();

  const pageIndices = pageNumbers.map(n => n - 1).filter(i => i >= 0 && i < sourcePdf.getPageCount());
  if (pageIndices.length === 0) throw new Error('No valid pages selected');

  const copiedPages = await newPdf.copyPages(sourcePdf, pageIndices);
  copiedPages.forEach(page => newPdf.addPage(page));

  const newBytes = await newPdf.save();
  let base64: string | null = uint8ArrayToBase64(newBytes);

  const dir = outputPath.substring(0, outputPath.lastIndexOf('/'));
  await FileSystem.makeDirectoryAsync(dir, { intermediates: true }).catch(() => {});

  await FileSystem.writeAsStringAsync(outputPath, base64, { encoding: FileSystem.EncodingType.Base64 });
  base64 = null; // Release for GC
  return outputPath;
}

/**
 * Get the page count of a PDF file.
 */
export async function getPdfPageCount(pdfUri: string): Promise<number> {
  const pdfBytes = await readPdfBytes(pdfUri);
  const pdf = await PDFDocument.load(pdfBytes, { ignoreEncryption: true });
  return pdf.getPageCount();
}

/**
 * Get page dimensions for all pages in a PDF.
 * Also returns the raw bytes for reuse (avoids double-loading in PdfPageEditor).
 */
export async function getPdfPageInfo(pdfUri: string): Promise<{
  pageCount: number;
  pages: { width: number; height: number }[];
  /** Raw PDF bytes — cache this to avoid re-loading when calling extractPages */
  cachedBytes: Uint8Array;
}> {
  const pdfBytes = await readPdfBytes(pdfUri);
  const pdf = await PDFDocument.load(pdfBytes, { ignoreEncryption: true });
  const pages = pdf.getPages().map(page => {
    const { width, height } = page.getSize();
    return { width, height };
  });
  return { pageCount: pdf.getPageCount(), pages, cachedBytes: pdfBytes };
}
