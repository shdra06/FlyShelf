// PDF Utility Functions — On-device PDF merge + page extraction using pdf-lib
// Optimized for memory efficiency (JSI-based Base64) to support 50MB+ PDFs
import { PDFDocument } from 'pdf-lib';
import * as FileSystem from 'expo-file-system/legacy';
import { base64ToUint8Array, uint8ArrayToBase64 } from './networkHelpers';

/**
 * Read a PDF file from a URI (local or http) and return its bytes as Uint8Array.
 * Uses JSI-based react-native-quick-base64 for 10x faster decoding and lower memory.
 */
async function readPdfBytes(uri: string): Promise<Uint8Array> {
  const isRemote = uri.startsWith('http://') || uri.startsWith('https://');
  let localUri = isRemote ? `${FileSystem.cacheDirectory}pdf_temp_${Date.now()}.pdf` : (uri.startsWith('file://') ? uri : `file://${uri}`);

  try {
    if (isRemote) {
      await FileSystem.downloadAsync(uri, localUri, {
        headers: { 'X-FlyShelf-Client': 'MobileCompanion' },
      });
    }

    // Read as Base64 — currently the only way to get binary data into JS from expo-file-system
    const b64 = await FileSystem.readAsStringAsync(localUri, { encoding: FileSystem.EncodingType.Base64 });
    
    // Memory optimization: Clean up temp file immediately
    if (isRemote) await FileSystem.deleteAsync(localUri, { idempotent: true });

    // Use JSI-based decoder (Fast & Memory Efficient)
    const bytes = base64ToUint8Array(b64);
    return bytes;
  } catch (e) {
    if (isRemote) await FileSystem.deleteAsync(localUri, { idempotent: true }).catch(() => {});
    throw e;
  }
}

/**
 * Merge multiple PDF and Image files into a single PDF.
 */
export async function mergePdfs(fileUris: string[], outputPath: string): Promise<string> {
  const mergedPdf = await PDFDocument.create();

  for (const uri of fileUris) {
    try {
      const lowerUri = uri.toLowerCase();
      const isPdf = lowerUri.endsWith('.pdf') || uri.includes('/PDFs/') || lowerUri.includes('merged_') || uri.includes('_pages.pdf');

      if (isPdf) {
        const pdfBytes = await readPdfBytes(uri);
        const sourcePdf = await PDFDocument.load(pdfBytes, { ignoreEncryption: true });
        const pageIndices = sourcePdf.getPageIndices();
        const copiedPages = await mergedPdf.copyPages(sourcePdf, pageIndices);
        copiedPages.forEach(page => mergedPdf.addPage(page));
      } else {
        // Image file (.png, .jpg, .jpeg)
        const imageBytes = await readPdfBytes(uri);
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

        const { width, height } = embeddedImage.scale(1.0);
        const page = mergedPdf.addPage([width, height]);
        page.drawImage(embeddedImage, { x: 0, y: 0, width, height });
      }
    } catch (err: any) {
      throw new Error(`Failed to process: ${uri.split('/').pop()}`);
    }
  }

  const mergedBytes = await mergedPdf.save();
  const base64 = uint8ArrayToBase64(mergedBytes);
  
  const dir = outputPath.substring(0, outputPath.lastIndexOf('/'));
  await FileSystem.makeDirectoryAsync(dir, { intermediates: true }).catch(() => {});
  
  await FileSystem.writeAsStringAsync(outputPath, base64, { encoding: FileSystem.EncodingType.Base64 });
  return outputPath;
}

/**
 * Convert an image file on-device to a single-page PDF.
 */
export async function convertImageToPdf(imageUri: string, outputPath: string): Promise<string> {
  const pdfDoc = await PDFDocument.create();
  const fileUri = imageUri.startsWith('file://') ? imageUri : `file://${imageUri}`;
  const base64 = await FileSystem.readAsStringAsync(fileUri, { encoding: FileSystem.EncodingType.Base64 });
  const imageBytes = base64ToUint8Array(base64);

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

  const { width, height } = embeddedImage.scale(1.0);
  const page = pdfDoc.addPage([width, height]);
  page.drawImage(embeddedImage, { x: 0, y: 0, width, height });

  const pdfBytes = await pdfDoc.save();
  const pdfBase64 = uint8ArrayToBase64(pdfBytes);

  const dir = outputPath.substring(0, outputPath.lastIndexOf('/'));
  await FileSystem.makeDirectoryAsync(dir, { intermediates: true }).catch(() => {});

  await FileSystem.writeAsStringAsync(outputPath, pdfBase64, { encoding: FileSystem.EncodingType.Base64 });
  return outputPath;
}

/**
 * Extract specific pages from a PDF and save as a new PDF.
 */
export async function extractPages(pdfUri: string, pageNumbers: number[], outputPath: string): Promise<string> {
  const pdfBytes = await readPdfBytes(pdfUri);
  const sourcePdf = await PDFDocument.load(pdfBytes, { ignoreEncryption: true });
  const newPdf = await PDFDocument.create();

  const pageIndices = pageNumbers.map(n => n - 1).filter(i => i >= 0 && i < sourcePdf.getPageCount());
  if (pageIndices.length === 0) throw new Error('No valid pages selected');

  const copiedPages = await newPdf.copyPages(sourcePdf, pageIndices);
  copiedPages.forEach(page => newPdf.addPage(page));

  const newBytes = await newPdf.save();
  const base64 = uint8ArrayToBase64(newBytes);

  const dir = outputPath.substring(0, outputPath.lastIndexOf('/'));
  await FileSystem.makeDirectoryAsync(dir, { intermediates: true }).catch(() => {});

  await FileSystem.writeAsStringAsync(outputPath, base64, { encoding: FileSystem.EncodingType.Base64 });
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
 */
export async function getPdfPageInfo(pdfUri: string): Promise<{ pageCount: number; pages: { width: number; height: number }[] }> {
  const pdfBytes = await readPdfBytes(pdfUri);
  const pdf = await PDFDocument.load(pdfBytes, { ignoreEncryption: true });
  const pages = pdf.getPages().map(page => {
    const { width, height } = page.getSize();
    return { width, height };
  });
  return { pageCount: pdf.getPageCount(), pages };
}
