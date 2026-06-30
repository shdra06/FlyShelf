import { useState, useCallback } from 'react';

export interface UsePdfEditorReturn {
  pageEditorVisible: boolean;
  pageEditorUri: string;
  pageEditorTitle: string;
  openPageEditor: (uri: string, title: string) => void;
  closePageEditor: () => void;
}

/**
 * Manages the PDF page editor modal state.
 * Extracted from SyncScreen for decomposition.
 */
export function usePdfEditor(): UsePdfEditorReturn {
  const [pageEditorVisible, setPageEditorVisible] = useState(false);
  const [pageEditorUri, setPageEditorUri] = useState('');
  const [pageEditorTitle, setPageEditorTitle] = useState('');

  const openPageEditor = useCallback((uri: string, title: string) => {
    setPageEditorUri(uri);
    setPageEditorTitle(title);
    setPageEditorVisible(true);
  }, []);

  const closePageEditor = useCallback(() => {
    setPageEditorVisible(false);
  }, []);

  return {
    pageEditorVisible,
    pageEditorUri,
    pageEditorTitle,
    openPageEditor,
    closePageEditor,
  };
}
