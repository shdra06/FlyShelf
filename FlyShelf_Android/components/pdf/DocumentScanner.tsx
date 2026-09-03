// DocumentScanner — Camera-based document scanning wrapper
// Uses react-native-document-scanner-plugin for native edge detection
import React, { useState, useCallback, useMemo } from 'react';
import { View, Text, Modal, Pressable, Alert, ActivityIndicator, Image, ScrollView } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import * as Haptics from 'expo-haptics';
import * as ImagePicker from 'expo-image-picker';
import { useAppTheme } from '../../hooks/useAppTheme';
import { createPdfEditorStyles } from '../../styles/pdfEditorStyles';
import { font, space, radius } from '../../styles/theme';
import { ImageFilter } from './types';

interface DocumentScannerProps {
  visible: boolean;
  onClose: () => void;
  onScanned: (imageUris: string[], filter: ImageFilter) => void;
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

export default function DocumentScanner({ visible, onClose, onScanned }: DocumentScannerProps) {
  const { colors, shadows } = useAppTheme();
  const s = useMemo(() => createPdfEditorStyles(colors, shadows), [colors, shadows]);

  const [scannedImages, setScannedImages] = useState<string[]>([]);
  const [selectedFilter, setSelectedFilter] = useState<ImageFilter>('original');
  const [scanning, setScanning] = useState(false);

  const handleScan = useCallback(async () => {
    setScanning(true);
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);

    try {
      const mod = await getScannerModule();

      if (mod?.scanDocument) {
        // Use native document scanner (ML Kit / VisionKit)
        const result = await mod.scanDocument({
          letUserAdjustCrop: true,
          maxNumDocuments: 20,
        });

        if (result?.scannedImages?.length > 0) {
          setScannedImages(prev => [...prev, ...result.scannedImages]);
          Haptics.notificationAsync(Haptics.NotificationFeedbackType.Success);
        }
      } else {
        // Fallback: Use expo-image-picker camera
        const { status } = await ImagePicker.requestCameraPermissionsAsync();
        if (status !== 'granted') {
          Alert.alert('Permission Required', 'Camera access is needed to scan documents.');
          return;
        }

        const result = await ImagePicker.launchCameraAsync({
          quality: 1,
          allowsEditing: true,
          aspect: [3, 4],
        });

        if (!result.canceled && result.assets?.length > 0) {
          setScannedImages(prev => [...prev, ...(result.assets ? result.assets.map(a => a.uri) : [])]);
          Haptics.notificationAsync(Haptics.NotificationFeedbackType.Success);
        }
      }
    } catch (err: any) {
      Alert.alert('Scan Error', err.message || 'Failed to scan document');
    } finally {
      setScanning(false);
    }
  }, []);

  const handleRemovePage = useCallback((index: number) => {
    setScannedImages(prev => prev.filter((_, i) => i !== index));
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);
  }, []);

  const handleConfirm = useCallback(() => {
    if (scannedImages.length === 0) {
      Alert.alert('No Pages', 'Scan at least one page first.');
      return;
    }
    Haptics.notificationAsync(Haptics.NotificationFeedbackType.Success);
    onScanned(scannedImages, selectedFilter);
    setScannedImages([]);
    setSelectedFilter('original');
  }, [scannedImages, selectedFilter, onScanned]);

  const handleClose = useCallback(() => {
    setScannedImages([]);
    setSelectedFilter('original');
    onClose();
  }, [onClose]);

  return (
    <Modal visible={visible} animationType="slide" presentationStyle="fullScreen" onRequestClose={handleClose}>
      <View style={s.container}>
        {/* Top Bar */}
        <View style={s.topBar}>
          <Pressable style={s.topBarBack} onPress={handleClose}>
            <Ionicons name="close" size={24} color={colors.text.primary} />
          </Pressable>
          <View style={s.topBarTitleWrap}>
            <Text style={s.topBarTitle}>Document Scanner</Text>
            <Text style={s.topBarSubtitle}>
              {scannedImages.length === 0
                ? 'Tap scan to capture pages'
                : `${scannedImages.length} page${scannedImages.length !== 1 ? 's' : ''} scanned`}
            </Text>
          </View>
          {scannedImages.length > 0 && (
            <Pressable
              style={s.saveBtn}
              onPress={handleConfirm}
            >
              <Ionicons name="checkmark" size={18} color="#FFFFFF" />
              <Text style={s.saveBtnText}>Done</Text>
            </Pressable>
          )}
        </View>

        {/* Scanned Pages Preview */}
        {scannedImages.length > 0 ? (
          <ScrollView
            style={{ flex: 1 }}
            contentContainerStyle={{
              flexDirection: 'row',
              flexWrap: 'wrap',
              padding: space.xl,
              gap: space.sm,
              paddingBottom: 180,
            }}
          >
            {scannedImages.map((uri, i) => (
              <View
                key={`scan-${i}`}
                style={{
                  width: '31%',
                  aspectRatio: 0.707,
                  borderRadius: radius.md,
                  overflow: 'hidden',
                  backgroundColor: colors.bg.card,
                  borderWidth: 1,
                  borderColor: colors.border.subtle,
                }}
              >
                <Image source={{ uri }} style={{ width: '100%', height: '100%', resizeMode: 'cover' }} />
                <View style={{
                  position: 'absolute', top: 4, left: 4,
                  backgroundColor: 'rgba(0,0,0,0.65)', borderRadius: 10,
                  minWidth: 20, height: 20, alignItems: 'center', justifyContent: 'center',
                  paddingHorizontal: 5,
                }}>
                  <Text style={{ fontFamily: font.semibold, fontSize: 10, color: '#FFF' }}>{i + 1}</Text>
                </View>
                <Pressable
                  onPress={() => handleRemovePage(i)}
                  style={{
                    position: 'absolute', top: 4, right: 4,
                    backgroundColor: 'rgba(0,0,0,0.65)', borderRadius: 12,
                    width: 24, height: 24, alignItems: 'center', justifyContent: 'center',
                  }}
                >
                  <Ionicons name="close" size={14} color="#FFF" />
                </Pressable>
              </View>
            ))}
          </ScrollView>
        ) : (
          <View style={s.emptyState}>
            <Ionicons name="camera-outline" size={80} color={colors.text.disabled} style={s.emptyIcon} />
            <Text style={s.emptyTitle}>Scan Documents</Text>
            <Text style={s.emptySubtitle}>
              Point your camera at a document to capture it.{'\n'}
              You can scan multiple pages in a batch.
            </Text>
          </View>
        )}

        {/* Filter Strip (only when images scanned) */}
        {scannedImages.length > 0 && (
          <View style={{
            flexDirection: 'row',
            paddingHorizontal: space.xl,
            paddingVertical: space.md,
            gap: space.sm,
            backgroundColor: colors.bg.card,
            borderTopWidth: 1,
            borderTopColor: colors.border.subtle,
          }}>
            {FILTERS.map(f => (
              <Pressable
                key={f.id}
                onPress={() => {
                  setSelectedFilter(f.id);
                  Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
                }}
                style={{
                  flex: 1,
                  alignItems: 'center',
                  paddingVertical: space.sm,
                  borderRadius: radius.md,
                  backgroundColor: selectedFilter === f.id ? colors.accent.primaryDim : 'transparent',
                  borderWidth: selectedFilter === f.id ? 1 : 0,
                  borderColor: colors.accent.primary,
                }}
              >
                <Ionicons
                  name={f.icon as any}
                  size={20}
                  color={selectedFilter === f.id ? colors.accent.primary : colors.text.secondary}
                />
                <Text style={{
                  fontFamily: font.medium,
                  fontSize: 10,
                  color: selectedFilter === f.id ? colors.accent.primary : colors.text.tertiary,
                  marginTop: 2,
                }}>
                  {f.label}
                </Text>
              </Pressable>
            ))}
          </View>
        )}

        {/* Scan Button */}
        <View style={{
          position: 'absolute',
          bottom: 32,
          left: 0,
          right: 0,
          alignItems: 'center',
        }}>
          <Pressable
            onPress={handleScan}
            disabled={scanning}
            style={{
              width: 72,
              height: 72,
              borderRadius: 36,
              backgroundColor: colors.accent.primary,
              alignItems: 'center',
              justifyContent: 'center',
              elevation: 8,
              shadowColor: colors.accent.primary,
              shadowOffset: { width: 0, height: 4 },
              shadowOpacity: 0.4,
              shadowRadius: 8,
            }}
          >
            {scanning ? (
              <ActivityIndicator color="#FFFFFF" size="small" />
            ) : (
              <Ionicons name="camera" size={32} color="#FFFFFF" />
            )}
          </Pressable>
          <Text style={{
            fontFamily: font.medium,
            fontSize: 12,
            color: colors.text.secondary,
            marginTop: space.sm,
          }}>
            {scanning ? 'Scanning...' : 'Tap to Scan'}
          </Text>
        </View>

        {/* Loading Overlay */}
        {scanning && (
          <View style={s.loadingOverlay}>
            <View style={s.loadingCard}>
              <ActivityIndicator color={colors.accent.primary} size="large" />
              <Text style={s.loadingText}>Opening scanner...</Text>
            </View>
          </View>
        )}
      </View>
    </Modal>
  );
}
