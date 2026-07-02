export type ToolId = 'merge' | 'split' | 'editPages' | 'imagesToPdf' | 'extract' | 'watermark' | 'password' | 'metadata' | 'info';

export interface SelectedFile {
  uri: string;
  name: string;
  size?: number;
}

export interface PageEntry {
  index: number;  // 0-indexed
  width: number;
  height: number;
  rotation: number;
  selected?: boolean;
}

export interface RecentPdf {
  name: string;
  path: string;
  pages: number;
  date: number;
  tool: ToolId;
}
