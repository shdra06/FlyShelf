export type ToolId = 'merge' | 'split' | 'editPages' | 'imagesToPdf' | 'extract' | 'watermark' | 'password' | 'metadata' | 'info' | 'compress' | 'pdfToWord' | 'scanToPdf' | 'pdfEditor';

export interface SelectedFile {
  uri: string;
  name: string;
  size?: number;
}

export interface PageEntry {
  index: number;  // 0-indexed position in current order
  originalIndex: number; // 0-indexed position in source PDF
  width: number;
  height: number;
  rotation: number;
  selected?: boolean;
  thumbnailUri?: string; // local file:// URI to cached thumbnail
  source: 'original' | 'image' | 'scanned' | 'blank'; // where the page came from
  sourceUri?: string; // URI of added image/scanned page
}

export interface RecentPdf {
  name: string;
  path: string;
  pages: number;
  date: number;
  tool: ToolId;
}

// ── PDF Editor Types ──

export type EditorAction =
  | { type: 'reorder'; fromIndex: number; toIndex: number }
  | { type: 'rotate'; pageIndices: number[]; degrees: 90 | 180 | 270 }
  | { type: 'delete'; pageIndices: number[]; deletedPages: PageEntry[] }
  | { type: 'add'; atIndex: number; pages: PageEntry[] }
  | { type: 'duplicate'; pageIndex: number };

export interface EditorState {
  pages: PageEntry[];
  selectedIndices: Set<number>;
  undoStack: EditorAction[];
  redoStack: EditorAction[];
  isDirty: boolean;
}

export type ImageFilter = 'original' | 'enhanced' | 'grayscale' | 'bw' | 'whiteboard';

export type ScannerStep = 'capture' | 'review' | 'save';

export interface ScanPage {
  uri: string;
  filter: ImageFilter;
  rotation: number;
}

export interface ScanCompleteResult {
  pdfPath: string;
  name: string;
  pageCount: number;
}

export interface ScanResult {
  imageUris: string[];
  filter: ImageFilter;
}

export interface SaveOptions {
  mode: 'save' | 'saveAs';
  outputPath?: string;
  fileName?: string;
}
