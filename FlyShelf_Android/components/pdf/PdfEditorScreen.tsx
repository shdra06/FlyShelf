import React, { useState, useEffect, useMemo } from 'react';
import { View, Text, TouchableOpacity, ScrollView, Image, ActivityIndicator, Alert, TextInput, StyleSheet, KeyboardAvoidingView } from 'react-native';
import { SafeAreaView } from 'react-native-safe-area-context';
import { Ionicons } from '@expo/vector-icons';
import * as Haptics from 'expo-haptics';
import Animated, { FadeInDown } from 'react-native-reanimated';

import { useAppTheme } from '../../hooks/useAppTheme';
import { createPdfEditorStyles } from '../../styles/pdfEditorStyles';
import { PageEntry, EditorAction, ImageFilter } from './types';
import { getPdfPageInfo } from '../../utils/pdfUtils';
import { generateThumbnails, savePdf, undoAction, redoAction, buildEditedPdf } from '../../utils/pdfEditorUtils';
import AddPagesSheet from './AddPagesSheet';
import DocumentScanner from './DocumentScanner';
import * as ImagePicker from 'expo-image-picker';
import * as DocumentPicker from 'expo-document-picker';

interface PdfEditorScreenProps {
  sourceUri: string;
  sourceName: string;
  onClose: () => void;
  onSaved?: (outputUri: string, name: string) => void;
}

export default function PdfEditorScreen({
  sourceUri,
  sourceName,
  onClose,
  onSaved,
}: PdfEditorScreenProps) {
  const { colors, shadows, font, surface } = useAppTheme();
  const styles = useMemo(() => createPdfEditorStyles(colors, shadows), [colors, shadows]);

  const [pages, setPages] = useState<PageEntry[]>([]);
  const [selectedIndices, setSelectedIndices] = useState<Set<number>>(new Set());
  const [undoStack, setUndoStack] = useState<EditorAction[]>([]);
  const [redoStack, setRedoStack] = useState<EditorAction[]>([]);
  const [isDirty, setIsDirty] = useState(false);
  
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [loadingText, setLoadingText] = useState('Loading PDF...');
  
  const [showSaveMenu, setShowSaveMenu] = useState(false);
  const [showAddSheet, setShowAddSheet] = useState(false);
  const [showScanner, setShowScanner] = useState(false);
  const [reorderMode, setReorderMode] = useState(false);
  
  const [showNamePrompt, setShowNamePrompt] = useState(false);
  const [saveAsName, setSaveAsName] = useState(sourceName);

  // ── Add Page Handlers ──

  const addPagesFromImages = async () => {
    setShowAddSheet(false);
    try {
      const result = await ImagePicker.launchImageLibraryAsync({
        mediaTypes: ['images'],
        allowsMultipleSelection: true,
        quality: 1,
      });
      if (result.canceled || !result.assets?.length) return;

      const newPages: PageEntry[] = result.assets.map((asset, i) => ({
        index: pages.length + i,
        originalIndex: pages.length + i,
        width: asset.width || 595,
        height: asset.height || 842,
        rotation: 0,
        source: 'image' as const,
        sourceUri: asset.uri,
        thumbnailUri: asset.uri,
      }));

      const action: EditorAction = { type: 'add', atIndex: pages.length, pages: newPages };
      pushAction(action, [...pages, ...newPages]);
      Haptics.notificationAsync(Haptics.NotificationFeedbackType.Success);
    } catch (e: any) {
      Alert.alert('Error', e.message || 'Failed to add images');
    }
  };

  const addPagesFromPdf = async () => {
    setShowAddSheet(false);
    try {
      const result = await DocumentPicker.getDocumentAsync({
        type: 'application/pdf',
        copyToCacheDirectory: true,
      });
      if (result.canceled || !result.assets?.length) return;

      const importedUri = result.assets[0].uri;
      const info = await getPdfPageInfo(importedUri);

      const newPages: PageEntry[] = info.pages.map((p, i) => ({
        index: pages.length + i,
        originalIndex: i,
        width: p.width,
        height: p.height,
        rotation: 0,
        source: 'original' as const,
        sourceUri: importedUri,
      }));

      // Generate thumbnails for imported pages
      const thumbs = await generateThumbnails(importedUri, newPages.map(p => p.originalIndex));
      newPages.forEach(p => { p.thumbnailUri = thumbs.get(p.originalIndex); });

      const action: EditorAction = { type: 'add', atIndex: pages.length, pages: newPages };
      pushAction(action, [...pages, ...newPages]);
      Haptics.notificationAsync(Haptics.NotificationFeedbackType.Success);
    } catch (e: any) {
      Alert.alert('Error', e.message || 'Failed to import PDF pages');
    }
  };

  const addBlankPage = () => {
    setShowAddSheet(false);
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
    const newPage: PageEntry = {
      index: pages.length,
      originalIndex: pages.length,
      width: 595,
      height: 842,
      rotation: 0,
      source: 'blank',
    };
    const action: EditorAction = { type: 'add', atIndex: pages.length, pages: [newPage] };
    pushAction(action, [...pages, newPage]);
  };

  const handleScanComplete = (imageUris: string[], _filter: ImageFilter) => {
    setShowScanner(false);
    if (imageUris.length === 0) return;

    const newPages: PageEntry[] = imageUris.map((uri, i) => ({
      index: pages.length + i,
      originalIndex: pages.length + i,
      width: 595,
      height: 842,
      rotation: 0,
      source: 'scanned' as const,
      sourceUri: uri,
      thumbnailUri: uri,
    }));

    const action: EditorAction = { type: 'add', atIndex: pages.length, pages: newPages };
    pushAction(action, [...pages, ...newPages]);
    Haptics.notificationAsync(Haptics.NotificationFeedbackType.Success);
  };

  useEffect(() => {
    const loadPdf = async () => {
      try {
        setLoading(true);
        setLoadingText('Loading PDF info...');
        const info = await getPdfPageInfo(sourceUri);
        
        const initialPages: PageEntry[] = info.pages.map((p, i) => ({
          index: i,
          originalIndex: i,
          width: p.width,
          height: p.height,
          rotation: 0,
          source: 'original'
        }));
        setPages(initialPages);
        
        setLoadingText('Generating thumbnails...');
        // H-3: Batch thumbnail generation (5 at a time) to prevent OOM on large PDFs
        const BATCH_SIZE = 5;
        const allIndices = initialPages.map(p => p.index);
        for (let i = 0; i < allIndices.length; i += BATCH_SIZE) {
          const batch = allIndices.slice(i, i + BATCH_SIZE);
          setLoadingText(`Generating thumbnails (${Math.min(i + BATCH_SIZE, allIndices.length)}/${allIndices.length})...`);
          const thumbs = await generateThumbnails(sourceUri, batch);
          setPages(prev => prev.map(p => {
            const thumb = thumbs.get(p.originalIndex);
            return thumb ? { ...p, thumbnailUri: thumb } : p;
          }));
        }
        
      } catch (e: any) {
        // M-7: Show error alert and wait for user acknowledgment before closing
        Alert.alert('Error', e.message || 'Failed to load PDF', [
          { text: 'OK', onPress: onClose },
        ]);
        return; // Don't call onClose immediately — let the alert callback handle it
      } finally {
        setLoading(false);
      }
    };
    loadPdf();
  }, [sourceUri, onClose]);

  const applyNewPages = (newPages: PageEntry[]) => {
    return newPages.map((p, i) => ({ ...p, index: i }));
  };

  const pushAction = (action: EditorAction, newPages: PageEntry[]) => {
    setUndoStack(prev => [...prev, action]);
    setRedoStack([]);
    setPages(applyNewPages(newPages));
    setIsDirty(true);
  };

  const handleRotate = () => {
    if (selectedIndices.size === 0) return;
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);
    const indices = Array.from(selectedIndices);
    const action: EditorAction = { type: 'rotate', pageIndices: indices, degrees: 90 };
    const newPages = [...pages];
    indices.forEach(idx => {
      newPages[idx] = { ...newPages[idx], rotation: (newPages[idx].rotation + 90) % 360 };
    });
    pushAction(action, newPages);
  };

  const handleDelete = () => {
    if (selectedIndices.size === 0) return;
    Alert.alert('Delete Pages', `Are you sure you want to delete ${selectedIndices.size} page(s)?`, [
      { text: 'Cancel', style: 'cancel' },
      { 
        text: 'Delete', 
        style: 'destructive', 
        onPress: () => {
          Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);
          const indices = Array.from(selectedIndices).sort((a, b) => a - b);
          const deletedPages = indices.map(idx => pages[idx]);
          const action: EditorAction = { type: 'delete', pageIndices: indices, deletedPages };
          
          const newPages = pages.filter((_, i) => !selectedIndices.has(i));
          setSelectedIndices(new Set());
          pushAction(action, newPages);
        }
      }
    ]);
  };

  const handleDuplicate = () => {
    if (selectedIndices.size === 0) return;
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);
    const idx = Array.from(selectedIndices).sort((a, b) => a - b)[0];
    const action: EditorAction = { type: 'duplicate', pageIndex: idx };
    const newPages = [...pages];
    newPages.splice(idx + 1, 0, { ...newPages[idx] });
    setSelectedIndices(new Set([idx + 1]));
    pushAction(action, newPages);
  };

  const handleUndo = () => {
    if (undoStack.length === 0) return;
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);
    const action = undoStack[undoStack.length - 1];
    const reversedPages = undoAction(pages, action);
    
    setUndoStack(prev => prev.slice(0, -1));
    setRedoStack(prev => [...prev, action]);
    setPages(applyNewPages(reversedPages));
    setSelectedIndices(new Set());
    if (undoStack.length === 1) setIsDirty(false);
  };

  const handleRedo = () => {
    if (redoStack.length === 0) return;
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);
    const action = redoStack[redoStack.length - 1];
    const forwardedPages = redoAction(pages, action);
    
    setRedoStack(prev => prev.slice(0, -1));
    setUndoStack(prev => [...prev, action]);
    setPages(applyNewPages(forwardedPages));
    setSelectedIndices(new Set());
    setIsDirty(true);
  };

  const movePage = (index: number, direction: -1 | 1) => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
    const newIndex = index + direction;
    if (newIndex < 0 || newIndex >= pages.length) return;
    
    const action: EditorAction = { type: 'reorder', fromIndex: index, toIndex: newIndex };
    const newPages = [...pages];
    const [moved] = newPages.splice(index, 1);
    newPages.splice(newIndex, 0, moved);
    
    pushAction(action, newPages);
  };

  const doSave = async (mode: 'save' | 'saveAs', newName?: string) => {
    try {
      setSaving(true);
      const outUri = await savePdf(sourceUri, pages, mode, newName);
      setIsDirty(false);
      setUndoStack([]);
      setRedoStack([]);
      Alert.alert('Success', 'PDF saved successfully.');
      onSaved?.(outUri, newName || sourceName);
    } catch (e: any) {
      Alert.alert('Save Failed', e.message);
    } finally {
      setSaving(false);
    }
  };

  const handleSaveBtnClick = (mode: 'save' | 'saveAs') => {
    setShowSaveMenu(false);
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Heavy);
    
    if (mode === 'saveAs') {
      setSaveAsName(sourceName);
      setShowNamePrompt(true);
    } else {
      doSave('save');
    }
  };

  const renderGrid = () => {
    const rows = [];
    for (let i = 0; i < pages.length; i += 3) {
      const rowPages = pages.slice(i, i + 3);
      rows.push(
        <View key={`row-${i}`} style={styles.gridRow}>
          {rowPages.map((page, idx) => {
            const globalIdx = i + idx;
            const isSelected = selectedIndices.has(globalIdx);
            
            return (
              <Animated.View key={`page-${page.index}-${page.originalIndex}-${page.source}`} entering={FadeInDown.delay(idx * 40)}>
                <TouchableOpacity
                  activeOpacity={0.8}
                  onPress={() => {
                    if (reorderMode) return;
                    Haptics.selectionAsync();
                    const newSet = new Set(selectedIndices);
                    if (newSet.has(globalIdx)) newSet.delete(globalIdx);
                    else newSet.add(globalIdx);
                    setSelectedIndices(newSet);
                  }}
                  style={[styles.pageCard, isSelected && styles.pageCardSelected]}
                >
                  {page.thumbnailUri ? (
                    <Image 
                      source={{ uri: page.thumbnailUri }} 
                      style={[styles.pageThumb, { transform: [{ rotate: `${page.rotation}deg` }] }]} 
                    />
                  ) : (
                    <View style={styles.pagePlaceholder}>
                      <Text style={styles.pagePlaceholderText}>{globalIdx + 1}</Text>
                    </View>
                  )}
                  
                  <View style={styles.pageNumberBadge}>
                    <Text style={styles.pageNumberText}>{globalIdx + 1}</Text>
                  </View>
                  
                  {!reorderMode && (
                    isSelected ? (
                      <View style={styles.pageSelectionBadge}>
                        <Ionicons name="checkmark" size={14} color="#FFF" />
                      </View>
                    ) : (
                      <View style={styles.pageSelectionEmpty} />
                    )
                  )}
                  
                  {page.rotation !== 0 && (
                    <View style={styles.pageRotationBadge}>
                      <Text style={styles.pageRotationText}>{page.rotation}°</Text>
                    </View>
                  )}
                  
                  {reorderMode && (
                    <View style={{ position: 'absolute', right: 0, top: 0, bottom: 0, justifyContent: 'space-between', padding: 4, backgroundColor: surface.backdrop }}>
                      <TouchableOpacity 
                        onPress={() => movePage(globalIdx, -1)} 
                        disabled={globalIdx === 0} 
                        style={{ padding: 4, opacity: globalIdx === 0 ? 0.3 : 1 }}
                      >
                        <Ionicons name="arrow-up-circle" size={24} color="#FFF" />
                      </TouchableOpacity>
                      <TouchableOpacity 
                        onPress={() => movePage(globalIdx, 1)} 
                        disabled={globalIdx === pages.length - 1} 
                        style={{ padding: 4, opacity: globalIdx === pages.length - 1 ? 0.3 : 1 }}
                      >
                        <Ionicons name="arrow-down-circle" size={24} color="#FFF" />
                      </TouchableOpacity>
                    </View>
                  )}
                </TouchableOpacity>
              </Animated.View>
            );
          })}
          {rowPages.length < 3 && Array.from({ length: 3 - rowPages.length }).map((_, idx) => (
            <View key={`empty-${idx}`} style={[styles.pageCard, { opacity: 0, borderWidth: 0 }]} />
          ))}
        </View>
      );
    }
    return <View style={styles.gridContent}>{rows}</View>;
  };

  return (
    <SafeAreaView style={styles.container} edges={['top', 'bottom']}>
      {/* Top Bar */}
      <View style={styles.topBar}>
        <TouchableOpacity onPress={onClose} style={styles.topBarBack}>
          <Ionicons name="close" size={24} color={colors.text.primary} />
        </TouchableOpacity>
        
        <View style={styles.topBarTitleWrap}>
          <Text style={styles.topBarTitle} numberOfLines={1}>{sourceName}</Text>
          <Text style={styles.topBarSubtitle}>{pages.length} pages</Text>
        </View>
        
        <TouchableOpacity 
          onPress={handleUndo} 
          disabled={undoStack.length === 0} 
          style={[styles.topBarAction, undoStack.length === 0 && styles.topBarActionDisabled]}
        >
          <Ionicons name="arrow-undo" size={20} color={colors.text.primary} />
        </TouchableOpacity>
        
        <TouchableOpacity 
          onPress={handleRedo} 
          disabled={redoStack.length === 0} 
          style={[styles.topBarAction, redoStack.length === 0 && styles.topBarActionDisabled]}
        >
          <Ionicons name="arrow-redo" size={20} color={colors.text.primary} />
        </TouchableOpacity>
        
        <View style={{ zIndex: 100 }}>
          <TouchableOpacity 
            onPress={() => setShowSaveMenu(!showSaveMenu)} 
            disabled={!isDirty} 
            style={[styles.saveBtn, !isDirty && styles.saveBtnDisabled]}
          >
            <Text style={styles.saveBtnText}>Save</Text>
            <Ionicons name="chevron-down" size={14} color="#FFF" />
          </TouchableOpacity>
        </View>
      </View>

      {/* Save Menu Dropdown */}
      {showSaveMenu && (
        <View style={styles.saveMenu}>
          <TouchableOpacity onPress={() => handleSaveBtnClick('save')} style={styles.saveMenuItem}>
            <Ionicons name="save-outline" size={18} color={colors.text.primary} />
            <Text style={styles.saveMenuText}>Save</Text>
          </TouchableOpacity>
          <View style={styles.saveMenuDivider} />
          <TouchableOpacity onPress={() => handleSaveBtnClick('saveAs')} style={styles.saveMenuItem}>
            <Ionicons name="document-text-outline" size={18} color={colors.text.primary} />
            <Text style={styles.saveMenuText}>Save As...</Text>
          </TouchableOpacity>
        </View>
      )}

      {/* Grid */}
      <ScrollView style={styles.gridContainer} showsVerticalScrollIndicator={false}>
        {renderGrid()}
      </ScrollView>

      {/* Bottom Toolbars */}
      {selectedIndices.size > 0 && !reorderMode ? (
        <View style={styles.contextBar}>
          <TouchableOpacity onPress={() => setSelectedIndices(new Set())} style={styles.contextBarBtn}>
            <Ionicons name="close" size={24} color="#FFF" />
          </TouchableOpacity>
          <Text style={styles.contextBarText}>{selectedIndices.size} selected</Text>
          <TouchableOpacity onPress={handleRotate} style={styles.contextBarBtn}>
            <Ionicons name="refresh" size={24} color="#FFF" />
          </TouchableOpacity>
          <TouchableOpacity onPress={handleDuplicate} style={styles.contextBarBtn}>
            <Ionicons name="copy" size={24} color="#FFF" />
          </TouchableOpacity>
          <TouchableOpacity onPress={handleDelete} style={styles.contextBarBtn}>
            <Ionicons name="trash" size={24} color="#FFF" />
          </TouchableOpacity>
        </View>
      ) : (
        <View style={styles.toolbar}>
          <TouchableOpacity 
            onPress={() => {
              Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
              setReorderMode(!reorderMode);
            }} 
            style={[styles.toolbarBtn, reorderMode && styles.toolbarBtnActive]}
          >
            <Ionicons name="swap-vertical" size={24} color={reorderMode ? colors.accent.primary : colors.text.secondary} />
            <Text style={[styles.toolbarLabel, reorderMode && styles.toolbarLabelActive]}>Reorder</Text>
          </TouchableOpacity>
          <TouchableOpacity 
            onPress={() => {
              Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
              setShowAddSheet(true);
            }} 
            style={styles.toolbarBtn}
          >
            <Ionicons name="add-circle" size={24} color={colors.text.secondary} />
            <Text style={styles.toolbarLabel}>Add</Text>
          </TouchableOpacity>
        </View>
      )}

      {/* Load/Save Overlay */}
      {(loading || saving) && (
        <View style={styles.loadingOverlay}>
          <View style={styles.loadingCard}>
            <ActivityIndicator size="large" color={colors.accent.primary} />
            <Text style={styles.loadingText}>{saving ? 'Saving PDF...' : loadingText}</Text>
          </View>
        </View>
      )}

      {/* Save As Name Prompt Overlay */}
      {showNamePrompt && (
        <View style={[StyleSheet.absoluteFill, { backgroundColor: surface.backdrop, justifyContent: 'center', alignItems: 'center', zIndex: 300 }]}>
          <KeyboardAvoidingView behavior="padding" style={{ width: '80%' }}>
            <View style={{ backgroundColor: colors.bg.card, padding: 20, borderRadius: 12, ...shadows.elevated }}>
              <Text style={{ fontFamily: font.bold, fontSize: 18, color: colors.text.primary, marginBottom: 12 }}>Save As</Text>
              <TextInput
                value={saveAsName}
                onChangeText={setSaveAsName}
                style={{ backgroundColor: colors.bg.input, color: colors.text.primary, padding: 12, borderRadius: 8, marginBottom: 20 }}
                placeholder="Filename"
                placeholderTextColor={colors.text.tertiary}
                autoFocus
              />
              <View style={{ flexDirection: 'row', justifyContent: 'flex-end', gap: 12 }}>
                <TouchableOpacity onPress={() => setShowNamePrompt(false)}>
                  <Text style={{ color: colors.text.tertiary, padding: 8 }}>Cancel</Text>
                </TouchableOpacity>
                <TouchableOpacity onPress={() => { setShowNamePrompt(false); doSave('saveAs', saveAsName); }}>
                  <Text style={{ color: colors.accent.primary, padding: 8, fontFamily: font.bold }}>Save</Text>
                </TouchableOpacity>
              </View>
            </View>
          </KeyboardAvoidingView>
        </View>
      )}

      {/* Add Pages Bottom Sheet */}
      <AddPagesSheet
        visible={showAddSheet}
        onClose={() => setShowAddSheet(false)}
        onScanDocument={() => { setShowAddSheet(false); setShowScanner(true); }}
        onPickImages={addPagesFromImages}
        onPickPdf={addPagesFromPdf}
        onAddBlankPage={addBlankPage}
      />

      {/* Document Scanner */}
      <DocumentScanner
        visible={showScanner}
        onClose={() => setShowScanner(false)}
        onScanned={handleScanComplete}
      />

    </SafeAreaView>
  );
}
