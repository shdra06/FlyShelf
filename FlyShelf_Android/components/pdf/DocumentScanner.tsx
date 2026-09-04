// DocumentScanner — Camera-based guided 3-step document scanner
// Uses react-native-document-scanner-plugin for native edge detection
import React, { useState, useCallback, useMemo, useEffect, useRef } from 'react';
import { 
  View, 
  Text, 
  Modal, 
  Pressable, 
  Alert, 
  ActivityIndicator, 
  Image, 
  ScrollView, 
  FlatList, 
  TextInput, 
  Dimensions, 
  KeyboardAvoidingView, 
  Platform 
} from 'react-native';
import { Ionicons, MaterialCommunityIcons } from '@expo/vector-icons';
import * as Haptics from 'expo-haptics';
import * as ImagePicker from 'expo-image-picker';
import * as Sharing from 'expo-sharing';
import Animated, { FadeInDown } from 'react-native-reanimated';
import { useAppTheme } from '../../hooks/useAppTheme';
import { createPdfEditorStyles } from '../../styles/pdfEditorStyles';
import { font, space, radius } from '../../styles/theme';
import { ImageFilter, ScannerStep, ScanPage, ScanCompleteResult, PageEntry } from './types';
import { buildEditedPdf } from '../../utils/pdfEditorUtils';
import OcrButton from '../tools/OcrButton';

const { width: SCREEN_WIDTH } = Dimensions.get('window');

interface DocumentScannerProps {
  visible: boolean;
  onClose: () => void;
  onScanned?: (imageUris: string[], filter: ImageFilter) => void;
  onScanComplete?: (result: ScanCompleteResult) => void;
  onOpenInEditor?: (pdfPath: string, name: string) => void;
  onSendToPc?: (filePath: string) => void;
}

const FILTERS: { id: ImageFilter; label: string; icon: string }[] = [
  { id: 'original', label: 'Original', icon: 'image-outline' },
  { id: 'enhanced', label: 'Enhanced', icon: 'sunny-outline' },
  { id: 'grayscale', label: 'Grayscale', icon: 'contrast-outline' },
  { id: 'bw', label: 'B & W', icon: 'moon-outline' },
];

let _scannerModule: any = null;
let _scannerChecked = false;

async function getScannerModule(): Promise<any> {
  if (_scannerChecked) return _scannerModule;
  _scannerChecked = true;
  try {
    _scannerModule = require('react-native-document-scanner-plugin');
  } catch {
    _scannerModule = null;
  }
  return _scannerModule;
}

export default function DocumentScanner({ 
  visible, 
  onClose, 
  onScanned, 
  onScanComplete, 
  onOpenInEditor, 
  onSendToPc 
}: DocumentScannerProps) {
  const { colors, shadows } = useAppTheme();
  const s = useMemo(() => createPdfEditorStyles(colors, shadows), [colors, shadows]);

  const [step, setStep] = useState<ScannerStep>('capture');
  const [pages, setPages] = useState<ScanPage[]>([]);
  const [currentPageIndex, setCurrentPageIndex] = useState(0);
  const [docName, setDocName] = useState(`Scan_${new Date().toISOString().slice(0,10)}`);
  
  const [scanning, setScanning] = useState(false);
  const [building, setBuilding] = useState(false);
  const [hasAutoLaunched, setHasAutoLaunched] = useState(false);

  const flatListRef = useRef<FlatList>(null);

  // Auto-launch scanner on first open
  useEffect(() => {
    if (visible && step === 'capture' && pages.length === 0 && !hasAutoLaunched) {
      setHasAutoLaunched(true);
      setTimeout(() => handleScan(), 400);
    }
  }, [visible, step, pages.length, hasAutoLaunched]);

  // Reset state on close
  const resetAndClose = useCallback(() => {
    setStep('capture');
    setPages([]);
    setCurrentPageIndex(0);
    setHasAutoLaunched(false);
    onClose();
  }, [onClose]);

  // Handle Android Back or close button based on step
  const handleBack = useCallback(() => {
    if (step === 'save') {
      setStep('review');
    } else if (step === 'review') {
      setStep('capture');
    } else {
      resetAndClose();
    }
  }, [step, resetAndClose]);

  const handleScan = useCallback(async () => {
    setScanning(true);
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);

    try {
      const mod = await getScannerModule();
      let newImages: string[] = [];

      if (mod?.scanDocument) {
        const result = await mod.scanDocument({
          letUserAdjustCrop: true,
          maxNumDocuments: 20,
        });
        if (result?.scannedImages?.length > 0) {
          newImages = result.scannedImages;
        }
      } else {
        const { status } = await ImagePicker.requestCameraPermissionsAsync();
        if (status !== 'granted') {
          Alert.alert('Permission Required', 'Camera access is needed to scan documents.');
          return;
        }
        const result = await ImagePicker.launchCameraAsync({
          quality: 1,
          allowsEditing: false, // Don't enforce standard crop
        });
        if (!result.canceled && result.assets?.length > 0) {
          newImages = result.assets.map(a => a.uri);
        }
      }

      if (newImages.length > 0) {
        setPages(prev => [
          ...prev, 
          ...newImages.map(uri => ({ uri, filter: 'original' as ImageFilter, rotation: 0 }))
        ]);
        Haptics.notificationAsync(Haptics.NotificationFeedbackType.Success);
      }
    } catch (err: any) {
      Alert.alert('Scan Error', err.message || 'Failed to scan document');
    } finally {
      setScanning(false);
    }
  }, []);

  const handleImportGallery = useCallback(async () => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
    try {
      const result = await ImagePicker.launchImageLibraryAsync({
        allowsMultipleSelection: true,
        quality: 1,
      });
      if (!result.canceled && result.assets?.length > 0) {
        setPages(prev => [
          ...prev, 
          ...result.assets.map(a => ({ uri: a.uri, filter: 'original' as ImageFilter, rotation: 0 }))
        ]);
        Haptics.notificationAsync(Haptics.NotificationFeedbackType.Success);
      }
    } catch (err) {}
  }, []);

  // -- Review Step Actions --

  const handleRotatePage = useCallback(() => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
    setPages(prev => {
      const next = [...prev];
      if (next[currentPageIndex]) {
        next[currentPageIndex] = { ...next[currentPageIndex], rotation: (next[currentPageIndex].rotation + 90) % 360 };
      }
      return next;
    });
  }, [currentPageIndex]);

  const handleDeletePage = useCallback(() => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);
    setPages(prev => {
      const next = prev.filter((_, i) => i !== currentPageIndex);
      if (next.length === 0) {
        setStep('capture');
      } else if (currentPageIndex >= next.length) {
        setCurrentPageIndex(next.length - 1);
      }
      return next;
    });
  }, [currentPageIndex]);

  const handleRetakePage = useCallback(async () => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
    try {
      const { status } = await ImagePicker.requestCameraPermissionsAsync();
      if (status !== 'granted') return;
      const result = await ImagePicker.launchCameraAsync({ quality: 1 });
      if (!result.canceled && result.assets?.length > 0) {
        setPages(prev => {
          const next = [...prev];
          next[currentPageIndex] = { uri: result.assets[0].uri, filter: 'original', rotation: 0 };
          return next;
        });
        Haptics.notificationAsync(Haptics.NotificationFeedbackType.Success);
      }
    } catch (err) {}
  }, [currentPageIndex]);

  const handleFilterChange = useCallback((filter: ImageFilter) => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
    setPages(prev => {
      const next = [...prev];
      if (next[currentPageIndex]) {
        next[currentPageIndex] = { ...next[currentPageIndex], filter };
      }
      return next;
    });
  }, [currentPageIndex]);

  // -- Save Step Actions --

  const buildPdf = async (): Promise<string> => {
    const pageEntries: PageEntry[] = await Promise.all(pages.map(async (p, i) => {
      let w = 595, h = 842;
      try {
        const dims = await new Promise<{width:number,height:number}>((resolve, reject) => {
          Image.getSize(p.uri, (width, height) => resolve({width, height}), reject);
        });
        const scale = Math.min(2000 / Math.max(dims.width, dims.height), 1);
        w = Math.round(dims.width * scale);
        h = Math.round(dims.height * scale);
      } catch {}
      return {
        index: i, originalIndex: i,
        width: w, height: h,
        rotation: p.rotation,
        source: 'scanned' as const,
        sourceUri: p.uri,
      };
    }));
    return await buildEditedPdf('', pageEntries);
  };

  const handleSaveAction = async (action: 'complete' | 'editor' | 'share' | 'pc') => {
    if (pages.length === 0) return;
    setBuilding(true);
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);
    try {
      const path = await buildPdf();
      const finalName = docName.trim() || `Scan_${new Date().toISOString().slice(0,10)}`;
      
      switch (action) {
        case 'complete':
          if (onScanComplete) {
            onScanComplete({ pdfPath: path, name: finalName, pageCount: pages.length });
          } else if (onScanned) { // fallback
            onScanned(pages.map(p => p.uri), pages[0].filter);
          }
          break;
        case 'editor':
          if (onOpenInEditor) onOpenInEditor(path, finalName);
          break;
        case 'share':
          if (await Sharing.isAvailableAsync()) {
            await Sharing.shareAsync(path, { dialogTitle: 'Share PDF' });
          }
          break;
        case 'pc':
          if (onSendToPc) onSendToPc(path);
          break;
      }
      
      if (action === 'complete' || action === 'editor') {
        Haptics.notificationAsync(Haptics.NotificationFeedbackType.Success);
        resetAndClose();
      }
    } catch (err: any) {
      Alert.alert('Error', err.message || 'Failed to process document');
    } finally {
      setBuilding(false);
    }
  };

  // -- Render Helpers --

  const renderCaptureStep = () => (
    <View style={{ flex: 1 }}>
      {/* Top Bar */}
      <View style={s.topBar}>
        <Pressable style={s.topBarBack} onPress={resetAndClose}>
          <Ionicons name="close" size={24} color={colors.text.primary} />
        </Pressable>
        <View style={s.topBarTitleWrap}>
          <Text style={s.topBarTitle}>Capture</Text>
        </View>
        {pages.length > 0 && (
          <View style={{ backgroundColor: colors.accent.primary, borderRadius: radius.pill, paddingHorizontal: 10, paddingVertical: 4 }}>
            <Text style={{ fontFamily: font.bold, fontSize: 12, color: '#FFF' }}>{pages.length}</Text>
          </View>
        )}
      </View>

      {/* Main Area */}
      <View style={{ flex: 1, alignItems: 'center', justifyContent: 'center' }}>
        {pages.length > 0 ? (
          <ScrollView 
            horizontal 
            showsHorizontalScrollIndicator={false}
            contentContainerStyle={{ padding: space.xl, alignItems: 'center' }}
          >
            {pages.map((p, i) => (
              <Animated.View key={`cap-page-${i}`} entering={FadeInDown.delay(i * 100)}>
                <Pressable
                  onPress={() => { setCurrentPageIndex(i); setStep('review'); }}
                  style={{
                    width: 120, height: 170,
                    marginRight: space.md,
                    borderRadius: radius.md,
                    overflow: 'hidden',
                    borderWidth: 1, borderColor: colors.border.subtle,
                  }}
                >
                  <Image source={{ uri: p.uri }} style={{ width: '100%', height: '100%', resizeMode: 'cover' }} />
                  <View style={{ position: 'absolute', bottom: 4, right: 4, backgroundColor: 'rgba(0,0,0,0.7)', borderRadius: 12, paddingHorizontal: 6, paddingVertical: 2 }}>
                    <Text style={{ color: '#FFF', fontSize: 10, fontFamily: font.bold }}>{i + 1}</Text>
                  </View>
                </Pressable>
              </Animated.View>
            ))}
          </ScrollView>
        ) : (
          <View style={s.emptyState}>
            <Ionicons name="camera-outline" size={80} color={colors.text.disabled} style={s.emptyIcon} />
            <Text style={s.emptyTitle}>Scan Documents</Text>
            <Text style={s.emptySubtitle}>Point camera at a document.</Text>
          </View>
        )}
      </View>

      {/* Bottom Controls */}
      <View style={{ paddingBottom: 50, paddingTop: 20, alignItems: 'center', backgroundColor: colors.bg.card, borderTopWidth: 1, borderTopColor: colors.border.subtle }}>
        {pages.length > 0 && (
          <Pressable
            onPress={() => setStep('review')}
            style={{
              position: 'absolute', top: -24,
              backgroundColor: colors.accent.success,
              paddingHorizontal: space.xl, paddingVertical: space.sm,
              borderRadius: radius.pill,
              flexDirection: 'row', alignItems: 'center', gap: space.xs,
              elevation: 4, shadowColor: '#000', shadowOffset: { width: 0, height: 2 }, shadowOpacity: 0.2, shadowRadius: 4,
            }}
          >
            <Text style={{ fontFamily: font.bold, fontSize: 14, color: '#FFF' }}>Review Pages</Text>
            <Ionicons name="arrow-forward" size={16} color="#FFF" />
          </Pressable>
        )}

        <Pressable
          onPress={handleScan}
          disabled={scanning}
          style={{
            width: 72, height: 72, borderRadius: 36,
            backgroundColor: colors.accent.primary,
            alignItems: 'center', justifyContent: 'center',
            elevation: 8, shadowColor: colors.accent.primary, shadowOffset: { width: 0, height: 4 }, shadowOpacity: 0.4, shadowRadius: 8,
            marginBottom: space.lg,
          }}
        >
          {scanning ? <ActivityIndicator color="#FFFFFF" /> : <Ionicons name="camera" size={32} color="#FFFFFF" />}
        </Pressable>

        <Pressable onPress={handleImportGallery} style={{ flexDirection: 'row', alignItems: 'center', gap: space.xs }}>
          <Ionicons name="image-outline" size={20} color={colors.text.secondary} />
          <Text style={{ fontFamily: font.medium, fontSize: 14, color: colors.text.secondary }}>Import from Gallery</Text>
        </Pressable>
      </View>
    </View>
  );

  const renderReviewStep = () => {
    const activeFilter = pages[currentPageIndex]?.filter || 'original';

    return (
      <View style={{ flex: 1, backgroundColor: '#000' }}>
        {/* Top Bar */}
        <View style={{ flexDirection: 'row', alignItems: 'center', paddingTop: 50, paddingBottom: 10, paddingHorizontal: space.md, backgroundColor: colors.bg.base }}>
          <Pressable style={s.topBarBack} onPress={handleBack}>
            <Ionicons name="arrow-back" size={24} color={colors.text.primary} />
          </Pressable>
          <Text style={{ flex: 1, fontFamily: font.semibold, fontSize: 16, color: colors.text.primary, textAlign: 'center' }}>
            Page {currentPageIndex + 1} of {pages.length}
          </Text>
          <Pressable onPress={() => setStep('save')} style={{ paddingHorizontal: 12, paddingVertical: 6, backgroundColor: colors.accent.success, borderRadius: radius.sm }}>
            <Text style={{ fontFamily: font.bold, fontSize: 14, color: '#FFF' }}>Done ✓</Text>
          </Pressable>
        </View>

        {/* Main Preview */}
        <FlatList
          ref={flatListRef}
          data={pages}
          keyExtractor={(_, i) => `review-${i}`}
          horizontal
          pagingEnabled
          showsHorizontalScrollIndicator={false}
          snapToInterval={SCREEN_WIDTH}
          getItemLayout={(_, index) => ({ length: SCREEN_WIDTH, offset: SCREEN_WIDTH * index, index })}
          onMomentumScrollEnd={(e) => {
            const idx = Math.round(e.nativeEvent.contentOffset.x / SCREEN_WIDTH);
            setCurrentPageIndex(idx);
          }}
          initialScrollIndex={currentPageIndex}
          renderItem={({ item }) => (
            <View style={{ width: SCREEN_WIDTH, flex: 1, alignItems: 'center', justifyContent: 'center' }}>
              <Image 
                source={{ uri: item.uri }} 
                style={{ width: '90%', height: '90%', resizeMode: 'contain', transform: [{ rotate: `${item.rotation}deg` }] }} 
              />
            </View>
          )}
        />

        {/* Bottom Panel */}
        <View style={{ backgroundColor: colors.bg.base, paddingBottom: Platform.OS === 'ios' ? 32 : 16 }}>
          {/* Action Row */}
          <View style={{ flexDirection: 'row', justifyContent: 'space-around', paddingVertical: space.md, borderBottomWidth: 1, borderBottomColor: colors.border.subtle }}>
            <Pressable onPress={handleRotatePage} style={{ alignItems: 'center' }}>
              <Ionicons name="sync" size={24} color={colors.text.primary} />
              <Text style={{ fontFamily: font.medium, fontSize: 10, color: colors.text.secondary, marginTop: 4 }}>Rotate</Text>
            </Pressable>
            <Pressable onPress={handleRetakePage} style={{ alignItems: 'center' }}>
              <Ionicons name="camera-reverse-outline" size={24} color={colors.text.primary} />
              <Text style={{ fontFamily: font.medium, fontSize: 10, color: colors.text.secondary, marginTop: 4 }}>Retake</Text>
            </Pressable>
            <Pressable onPress={handleDeletePage} style={{ alignItems: 'center' }}>
              <Ionicons name="trash-outline" size={24} color={colors.accent.error} />
              <Text style={{ fontFamily: font.medium, fontSize: 10, color: colors.accent.error, marginTop: 4 }}>Delete</Text>
            </Pressable>
            {pages[currentPageIndex] && (
              <View style={{ alignItems: 'center' }}>
                <OcrButton imageUri={pages[currentPageIndex].uri} variant="icon" />
                <Text style={{ fontFamily: font.medium, fontSize: 10, color: colors.accent.info, marginTop: 4 }}>OCR</Text>
              </View>
            )}
          </View>

          {/* Filters */}
          <View style={{ flexDirection: 'row', padding: space.sm, justifyContent: 'center', gap: space.sm }}>
            {FILTERS.map(f => (
              <Pressable
                key={f.id}
                onPress={() => handleFilterChange(f.id)}
                style={{
                  paddingHorizontal: space.md, paddingVertical: space.xs,
                  borderRadius: radius.pill,
                  backgroundColor: activeFilter === f.id ? colors.accent.primaryDim : 'transparent',
                  borderWidth: 1, borderColor: activeFilter === f.id ? colors.accent.primary : colors.border.subtle,
                }}
              >
                <Text style={{ fontFamily: font.medium, fontSize: 12, color: activeFilter === f.id ? colors.accent.primary : colors.text.secondary }}>
                  {f.label}
                </Text>
              </Pressable>
            ))}
          </View>

          {/* Thumbnails */}
          <ScrollView horizontal showsHorizontalScrollIndicator={false} contentContainerStyle={{ padding: space.sm, gap: space.xs }}>
            {pages.map((p, i) => (
              <Pressable
                key={`thumb-${i}`}
                onPress={() => {
                  setCurrentPageIndex(i);
                  flatListRef.current?.scrollToIndex({ index: i, animated: true });
                }}
                style={{
                  width: 48, height: 68, borderRadius: 4, overflow: 'hidden',
                  borderWidth: 2, borderColor: currentPageIndex === i ? colors.accent.primary : 'transparent',
                }}
              >
                <Image source={{ uri: p.uri }} style={{ width: '100%', height: '100%', resizeMode: 'cover', transform: [{ rotate: `${p.rotation}deg` }] }} />
              </Pressable>
            ))}
          </ScrollView>

          {/* Bottom Actions */}
          <View style={{ flexDirection: 'row', paddingHorizontal: space.md, paddingTop: space.sm, gap: space.md }}>
            <Pressable onPress={() => setStep('capture')} style={{ flex: 1, paddingVertical: 12, borderRadius: radius.md, backgroundColor: colors.bg.elevated, alignItems: 'center' }}>
              <Text style={{ fontFamily: font.semibold, fontSize: 14, color: colors.text.primary }}>+ Add More</Text>
            </Pressable>
            <Pressable onPress={() => setStep('save')} style={{ flex: 1, paddingVertical: 12, borderRadius: radius.md, backgroundColor: colors.accent.primary, alignItems: 'center' }}>
              <Text style={{ fontFamily: font.bold, fontSize: 14, color: '#FFF' }}>Done →</Text>
            </Pressable>
          </View>
        </View>
      </View>
    );
  };

  const renderSaveStep = () => (
    <KeyboardAvoidingView behavior={Platform.OS === 'ios' ? 'padding' : undefined} style={{ flex: 1 }}>
      <View style={{ flex: 1 }}>
        {/* Top Bar */}
        <View style={s.topBar}>
          <Pressable style={s.topBarBack} onPress={handleBack}>
            <Ionicons name="arrow-back" size={24} color={colors.text.primary} />
          </Pressable>
          <View style={s.topBarTitleWrap}>
            <Text style={s.topBarTitle}>Save Document</Text>
          </View>
        </View>

        <ScrollView contentContainerStyle={{ padding: space.xl, gap: space.xl }}>
          {/* Info Card */}
          <View style={{ backgroundColor: colors.bg.card, padding: space.xl, borderRadius: radius.lg, alignItems: 'center', borderWidth: 1, borderColor: colors.border.subtle }}>
            <MaterialCommunityIcons name="file-pdf-box" size={64} color={colors.accent.error} style={{ marginBottom: space.md }} />
            
            <TextInput
              value={docName}
              onChangeText={setDocName}
              style={{
                width: '100%', backgroundColor: colors.bg.input,
                color: colors.text.primary, fontFamily: font.semibold, fontSize: 16,
                padding: space.md, borderRadius: radius.md, textAlign: 'center',
                borderWidth: 1, borderColor: colors.border.medium,
              }}
              placeholder="Document Name"
              placeholderTextColor={colors.text.tertiary}
            />
            
            <Text style={{ fontFamily: font.medium, fontSize: 13, color: colors.text.secondary, marginTop: space.md }}>
              {pages.length} page{pages.length !== 1 ? 's' : ''} • Scanned just now
            </Text>
          </View>

          {/* Actions */}
          <View style={{ gap: space.md }}>
            <Pressable 
              onPress={() => handleSaveAction('complete')}
              style={{ backgroundColor: colors.accent.primary, padding: space.md, borderRadius: radius.md, alignItems: 'center', flexDirection: 'row', justifyContent: 'center', gap: space.sm }}
            >
              <Ionicons name="save-outline" size={20} color="#FFF" />
              <Text style={{ fontFamily: font.bold, fontSize: 16, color: '#FFF' }}>Save PDF</Text>
            </Pressable>

            <Pressable 
              onPress={() => handleSaveAction('editor')}
              style={{ backgroundColor: colors.bg.elevated, padding: space.md, borderRadius: radius.md, alignItems: 'center', flexDirection: 'row', justifyContent: 'center', gap: space.sm }}
            >
              <MaterialCommunityIcons name="file-document-edit-outline" size={20} color={colors.text.primary} />
              <Text style={{ fontFamily: font.semibold, fontSize: 15, color: colors.text.primary }}>Open in Editor</Text>
            </Pressable>

            <Pressable 
              onPress={() => handleSaveAction('share')}
              style={{ backgroundColor: colors.bg.elevated, padding: space.md, borderRadius: radius.md, alignItems: 'center', flexDirection: 'row', justifyContent: 'center', gap: space.sm }}
            >
              <Ionicons name="share-outline" size={20} color={colors.text.primary} />
              <Text style={{ fontFamily: font.semibold, fontSize: 15, color: colors.text.primary }}>Share</Text>
            </Pressable>

            {onSendToPc && (
              <Pressable 
                onPress={() => handleSaveAction('pc')}
                style={{ backgroundColor: colors.bg.elevated, padding: space.md, borderRadius: radius.md, alignItems: 'center', flexDirection: 'row', justifyContent: 'center', gap: space.sm }}
              >
                <Ionicons name="desktop-outline" size={20} color={colors.text.primary} />
                <Text style={{ fontFamily: font.semibold, fontSize: 15, color: colors.text.primary }}>Send to PC</Text>
              </Pressable>
            )}
          </View>
        </ScrollView>
      </View>
    </KeyboardAvoidingView>
  );

  return (
    <Modal visible={visible} animationType="slide" presentationStyle="fullScreen" onRequestClose={handleBack}>
      <View style={s.container}>
        {step === 'capture' && renderCaptureStep()}
        {step === 'review' && renderReviewStep()}
        {step === 'save' && renderSaveStep()}

        {/* Loading Overlay */}
        {(scanning || building) && (
          <View style={s.loadingOverlay}>
            <View style={s.loadingCard}>
              <ActivityIndicator color={colors.accent.primary} size="large" />
              <Text style={s.loadingText}>{building ? 'Creating PDF...' : 'Opening scanner...'}</Text>
            </View>
          </View>
        )}
      </View>
    </Modal>
  );
}
