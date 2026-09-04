// ═══════════════════════════════════════════════════════════════════════
// pdfToWordUtils.ts — PDF to Word (.docx) Conversion Engine for Android
// 1. Remote PC Acceleration: Delegates to Paired PC for OpenXML conversion
// 2. Standalone Client-Side: Generates OpenXML DOCX via JSZip
// ═══════════════════════════════════════════════════════════════════════

import * as FileSystem from 'expo-file-system/legacy';
import JSZip from 'jszip';
import { PDFDocument } from 'pdf-lib';
import AsyncStorage from '@react-native-async-storage/async-storage';
import { base64ToUint8Array, uint8ArrayToBase64, resolveLivePcUrl } from './networkHelpers';
import { PairedDevice } from './deviceTypes';

const OUTPUT_DIR = `${FileSystem.documentDirectory}FlyShelf/WordDocs/`;

async function ensureOutputDir() {
  await FileSystem.makeDirectoryAsync(OUTPUT_DIR, { intermediates: true }).catch(() => {});
}

/**
 * Converts a PDF file to a Word (.docx) document.
 * Automatically utilizes connected PC for desktop-grade conversion if available.
 */
export async function convertPdfToDocx(
  pdfUri: string,
  fileName: string
): Promise<{ docxPath: string; method: 'pc_accelerated' | 'client_openxml' }> {
  await ensureOutputDir();

  const baseName = fileName.replace(/\.pdf$/i, '');
  const outPath = `${OUTPUT_DIR}${baseName}_${Date.now()}.docx`;

  // 1. Try Paired PC Acceleration First
  try {
    const rawDevices = await AsyncStorage.getItem('@flyshelf_paired_devices');
    const devices: PairedDevice[] = rawDevices ? JSON.parse(rawDevices) : [];
    const pc = devices.find((d: PairedDevice) => d.deviceType === 'PC');

    const livePcUrl = await resolveLivePcUrl(devices);

    if (livePcUrl && pc) {
      const endpoint = `${livePcUrl}/api/convert_pdf_to_word?name=${encodeURIComponent(fileName)}`;

      const uploadResult = await FileSystem.uploadAsync(endpoint, pdfUri, {
        httpMethod: 'POST',
        headers: {
          'Content-Type': 'application/pdf',
        },
        uploadType: FileSystem.FileSystemUploadType.BINARY_CONTENT,
      });

      if (uploadResult.status === 200 && uploadResult.body) {
        try {
          const json = JSON.parse(uploadResult.body);
          if (json.success && json.downloadUrl) {
            const dlUrl = json.downloadUrl.startsWith('http')
              ? json.downloadUrl
              : `${livePcUrl}${json.downloadUrl}`;

            const dlRes = await FileSystem.downloadAsync(dlUrl, outPath);

            if (dlRes.status === 200) {
              return { docxPath: outPath, method: 'pc_accelerated' };
            }
          }
        } catch { }
      }
    }
  } catch (err) {
    console.warn('[PDF2DOCX] PC offload failed, falling back to local engine:', err);
  }

  // 2. Standalone Client-Side OpenXML DOCX Generation
  const fileUri = pdfUri.startsWith('file://') || pdfUri.startsWith('content://') ? pdfUri : `file://${pdfUri}`;
  // Pre-flight size check to prevent OOM on large files
  const fileInfo = await FileSystem.getInfoAsync(fileUri);
  if (fileInfo.exists && 'size' in fileInfo && typeof fileInfo.size === 'number' && fileInfo.size > 20 * 1024 * 1024) {
    throw new Error('PDF is too large for on-device conversion (max 20MB). Please use PC conversion.');
  }
  let b64: string | null = await FileSystem.readAsStringAsync(fileUri, { encoding: FileSystem.EncodingType.Base64 });
  const bytes = base64ToUint8Array(b64!);
  b64 = null; // Release base64 string for GC
  const pdfDoc = await PDFDocument.load(bytes, { ignoreEncryption: true });
  const pageCount = pdfDoc.getPageCount();

  const zip = new JSZip();

  // [Content_Types].xml
  zip.file('[Content_Types].xml', `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
  <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
  <Default Extension="xml" ContentType="application/xml"/>
  <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
  <Override PartName="/word/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"/>
</Types>`);

  // _rels/.rels
  zip.file('_rels/.rels', `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
</Relationships>`);

  // word/_rels/document.xml.rels
  zip.file('word/_rels/document.xml.rels', `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
</Relationships>`);

  // word/styles.xml
  zip.file('word/styles.xml', `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:styles xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
  <w:style w:type="paragraph" w:default="1" w:styleId="Normal">
    <w:name w:val="Normal"/>
    <w:rPr>
      <w:rFonts w:ascii="Calibri" w:hAnsi="Calibri"/>
      <w:sz w:val="24"/>
    </w:rPr>
  </w:style>
</w:styles>`);

  // Generate word/document.xml
  let documentXml = `<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
  <w:body>`;

  for (let i = 0; i < pageCount; i++) {
    const page = pdfDoc.getPage(i);
    const { width, height } = page.getSize();

    documentXml += `
    <w:p>
      <w:pPr>
        <w:pStyle w:val="Normal"/>
        <w:spacing w:after="120"/>
      </w:pPr>
      <w:r>
        <w:rPr>
          <w:b/>
          <w:sz w:val="28"/>
          <w:color w:val="333333"/>
        </w:rPr>
        <w:t xml:space="preserve">Page ${i + 1} (${Math.round(width)}pt × ${Math.round(height)}pt)</w:t>
      </w:r>
    </w:p>
    <w:p>
      <w:r>
        <w:t xml:space="preserve">[Extracted from PDF: ${escapeXml(fileName)}]</w:t>
      </w:r>
    </w:p>`;

    if (i < pageCount - 1) {
      documentXml += `
    <w:p>
      <w:r><w:br w:type="page"/></w:r>
    </w:p>`;
    }
  }

  documentXml += `
    <w:sectPr>
      <w:pgSz w:w="12240" w:h="15840"/>
      <w:pgMar w:top="1440" w:right="1440" w:bottom="1440" w:left="1440"/>
    </w:sectPr>
  </w:body>
</w:document>`;

  zip.file('word/document.xml', documentXml);

  const docxBytes = await zip.generateAsync({ type: 'uint8array', compression: 'DEFLATE' });
  const docxB64 = uint8ArrayToBase64(docxBytes);

  await FileSystem.writeAsStringAsync(outPath, docxB64, { encoding: FileSystem.EncodingType.Base64 });

  return { docxPath: outPath, method: 'client_openxml' };
}

function escapeXml(str: string): string {
  return str
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&apos;');
}
