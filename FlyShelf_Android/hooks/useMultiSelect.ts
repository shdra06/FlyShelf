import { useState, useCallback } from 'react';
import { ClipItem } from '../utils/clipTypes';

export interface UseMultiSelectReturn {
  isMultiSelectMode: boolean;
  selectedItemIds: Set<string>;
  toggleSelectItem: (id: string) => void;
  exitMultiSelect: () => void;
  enterMultiSelect: (initialId: string) => void;
  getSelectedClips: (clips: ClipItem[], localWipeTimestamp: number, localDeletedIds: Set<string>) => ClipItem[];
}

/**
 * Manages multi-select state: enter/exit mode, toggle items, get selected clips.
 * Extracted from SyncScreen for decomposition.
 */
export function useMultiSelect(): UseMultiSelectReturn {
  const [isMultiSelectMode, setIsMultiSelectMode] = useState(false);
  const [selectedItemIds, setSelectedItemIds] = useState<Set<string>>(new Set());

  const toggleSelectItem = useCallback((id: string) => {
    setSelectedItemIds(prev => {
      const u = new Set(prev);
      if (u.has(id)) u.delete(id);
      else u.add(id);
      return u;
    });
  }, []);

  const exitMultiSelect = useCallback(() => {
    setIsMultiSelectMode(false);
    setSelectedItemIds(new Set());
  }, []);

  const enterMultiSelect = useCallback((initialId: string) => {
    setIsMultiSelectMode(true);
    setSelectedItemIds(new Set([initialId]));
  }, []);

  const getSelectedClips = useCallback(
    (clips: ClipItem[], localWipeTimestamp: number, localDeletedIds: Set<string>) => {
      return clips
        .filter(c => (c.IsPinned || (c.Timestamp || 0) >= localWipeTimestamp) && (!c.id || !localDeletedIds.has(c.id)) && (c.Raw || c.Title))
        .filter(c => selectedItemIds.has(c.id || ''));
    },
    [selectedItemIds]
  );

  return {
    isMultiSelectMode,
    selectedItemIds,
    toggleSelectItem,
    exitMultiSelect,
    enterMultiSelect,
    getSelectedClips,
  };
}
