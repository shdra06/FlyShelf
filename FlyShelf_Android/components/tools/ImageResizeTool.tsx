import React, { useState, useMemo } from 'react';
import { View, Text, Pressable, ScrollView, Image, Alert, StyleSheet, TextInput } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import * as Haptics from 'expo-haptics';
import * as ImagePicker from 'expo-image-picker';
import * as Sharing from 'expo-sharing';
import * as FileSystem from 'expo-file-system/legacy';
import * as MediaLibrary from 'expo-media-library';
import * as ImageManipulator from 'expo-image-manipulator';
import { useAppTheme } from '../../hooks/useAppTheme';
import { font, space, radius } from '../../styles/theme';
import Animated, { FadeInDown } from 'react-native-reanimated';
import OcrButton from './OcrButton';

interface ImageResizeToolProps {
  onBack: () => void;
}

const PRESETS = [
  { label: 'HD', width: 1280, height: 720 },
  { label: 'Full HD', width: 1920, height: 1080 },
  { label: '4K', width: 3840, height: 2160 },
  { label: 'IG Square', width: 1080, height: 1080 },
  { label: 'IG Story', width: 1080, height: 1920 },
  { label: 'Twitter', width: 1200, height: 675 },
  { label: 'Thumbnail', width: 300, height: 300 },
  { label: 'Icon', width: 512, height: 512 },
];

export default function ImageResizeTool({ onBack }: ImageResizeToolProps) {
  const { colors, shadows } = useAppTheme();
  const styles = useMemo(() => createStyles(colors, shadows), [colors, shadows]);

  const [imageUri, setImageUri] = useState<string | null>(null);
  const [originalWidth, setOriginalWidth] = useState<number>(0);
  const [originalHeight, setOriginalHeight] = useState<number>(0);
  const [originalSize, setOriginalSize] = useState<number>(0);

  const [mode, setMode] = useState<'custom' | 'preset'>('custom');
  
  const [widthStr, setWidthStr] = useState<string>('');
  const [heightStr, setHeightStr] = useState<string>('');
  const [lockedRatio, setLockedRatio] = useState<boolean>(true);

  const [resizedUri, setResizedUri] = useState<string | null>(null);
  const [resizedSize, setResizedSize] = useState<number>(0);
  const [resizedWidth, setResizedWidth] = useState<number>(0);
  const [resizedHeight, setResizedHeight] = useState<number>(0);
  const [isProcessing, setIsProcessing] = useState(false);

  const pickImage = async () => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);
    const result = await ImagePicker.launchImageLibraryAsync({
      mediaTypes: ['images'],
      allowsEditing: false,
      quality: 1,
    });

    if (!result.canceled && result.assets.length > 0) {
      const asset = result.assets[0];
      setImageUri(asset.uri);
      setOriginalWidth(asset.width);
      setOriginalHeight(asset.height);
      setWidthStr(asset.width.toString());
      setHeightStr(asset.height.toString());
      setResizedUri(null);
      
      try {
        const fileInfo = await FileSystem.getInfoAsync(asset.uri);
        if (fileInfo.exists && fileInfo.size) {
          setOriginalSize(fileInfo.size);
        }
      } catch (err) {
        console.warn('Could not get size', err);
      }
    }
  };

  const handleWidthChange = (val: string) => {
    setWidthStr(val);
    const w = parseInt(val, 10);
    if (lockedRatio && !isNaN(w) && originalWidth > 0) {
      const ratio = originalHeight / originalWidth;
      setHeightStr(Math.round(w * ratio).toString());
    }
  };

  const handleHeightChange = (val: string) => {
    setHeightStr(val);
    const h = parseInt(val, 10);
    if (lockedRatio && !isNaN(h) && originalHeight > 0) {
      const ratio = originalWidth / originalHeight;
      setWidthStr(Math.round(h * ratio).toString());
    }
  };

  const handlePresetSelect = (w: number, h: number) => {
    Haptics.selectionAsync();
    setWidthStr(w.toString());
    setHeightStr(h.toString());
  };

  const handleResize = async () => {
    if (!imageUri) return;
    const w = parseInt(widthStr, 10);
    const h = parseInt(heightStr, 10);

    if (isNaN(w) || isNaN(h) || w <= 0 || h <= 0) {
      Alert.alert('Invalid dimensions', 'Please enter valid width and height.');
      return;
    }

    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);
    setIsProcessing(true);

    try {
      const result = await ImageManipulator.manipulateAsync(
        imageUri,
        [{ resize: { width: w, height: h } }],
        { compress: 1 }
      );

      const fileInfo = await FileSystem.getInfoAsync(result.uri);
      if (fileInfo.exists && fileInfo.size) {
        setResizedUri(result.uri);
        setResizedSize(fileInfo.size);
        setResizedWidth(result.width);
        setResizedHeight(result.height);
        Haptics.notificationAsync(Haptics.NotificationFeedbackType.Success);
      }
    } catch (error) {
      console.error(error);
      Alert.alert('Error', 'Failed to resize image.');
      Haptics.notificationAsync(Haptics.NotificationFeedbackType.Error);
    } finally {
      setIsProcessing(false);
    }
  };

  const formatSize = (bytes: number) => {
    if (bytes === 0) return '0 B';
    const k = 1024;
    const sizes = ['B', 'KB', 'MB', 'GB'];
    const i = Math.floor(Math.log(bytes) / Math.log(k));
    return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
  };

  const saveToGallery = async () => {
    if (!resizedUri) return;
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);
    try {
      const { status } = await MediaLibrary.requestPermissionsAsync();
      if (status !== 'granted') {
        Alert.alert('Permission needed', 'Please grant permission to save images.');
        return;
      }
      await MediaLibrary.saveToLibraryAsync(resizedUri);
      Alert.alert('Success', 'Image saved to gallery!');
      Haptics.notificationAsync(Haptics.NotificationFeedbackType.Success);
    } catch (err) {
      console.error(err);
      Alert.alert('Error', 'Failed to save image.');
    }
  };

  const shareImage = async () => {
    if (!resizedUri) return;
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);
    try {
      await Sharing.shareAsync(resizedUri);
    } catch (err) {
      console.error(err);
    }
  };

  return (
    <View style={styles.container}>
      <View style={styles.topBar}>
        <Pressable onPress={() => { Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light); onBack(); }} hitSlop={12} style={styles.backBtn}>
          <Ionicons name="arrow-back" size={22} color={colors.text.primary} />
        </Pressable>
        <Text style={styles.title}>Resize Image</Text>
      </View>

      <ScrollView contentContainerStyle={styles.content}>
        {!imageUri ? (
          <Pressable style={styles.pickArea} onPress={pickImage}>
            <Ionicons name="image-outline" size={48} color={colors.accent.primary} />
            <Text style={styles.pickText}>Tap to select an image</Text>
          </Pressable>
        ) : (
          <Animated.View entering={FadeInDown.duration(400)}>
            <View style={styles.imagePreviewContainer}>
              <Image source={{ uri: imageUri }} style={styles.imagePreview} resizeMode="contain" />
              <Pressable style={styles.repickBtn} onPress={pickImage}>
                <Ionicons name="refresh" size={20} color={colors.text.primary} />
              </Pressable>
            </View>
            <View style={{ flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', marginBottom: space.sm }}>
              <Text style={styles.infoText}>
                Original: {originalWidth} × {originalHeight} ({formatSize(originalSize)})
              </Text>
              <OcrButton imageUri={imageUri} variant="chip" />
            </View>

            <View style={styles.modeTabs}>
              <Pressable 
                style={[styles.modeTab, mode === 'custom' && styles.modeTabActive]}
                onPress={() => { Haptics.selectionAsync(); setMode('custom'); }}
              >
                <Text style={[styles.modeTabText, mode === 'custom' && styles.modeTabTextActive]}>Custom</Text>
              </Pressable>
              <Pressable 
                style={[styles.modeTab, mode === 'preset' && styles.modeTabActive]}
                onPress={() => { Haptics.selectionAsync(); setMode('preset'); }}
              >
                <Text style={[styles.modeTabText, mode === 'preset' && styles.modeTabTextActive]}>Presets</Text>
              </Pressable>
            </View>

            {mode === 'custom' ? (
              <View style={styles.card}>
                <View style={styles.inputRow}>
                  <View style={styles.inputCol}>
                    <Text style={styles.label}>Width</Text>
                    <TextInput
                      style={styles.input}
                      keyboardType="numeric"
                      value={widthStr}
                      onChangeText={handleWidthChange}
                      placeholderTextColor={colors.text.tertiary}
                    />
                  </View>
                  <Pressable 
                    style={styles.lockBtn} 
                    onPress={() => { Haptics.selectionAsync(); setLockedRatio(!lockedRatio); }}
                  >
                    <Ionicons name={lockedRatio ? "lock-closed" : "lock-open"} size={20} color={lockedRatio ? colors.accent.primary : colors.text.secondary} />
                  </Pressable>
                  <View style={styles.inputCol}>
                    <Text style={styles.label}>Height</Text>
                    <TextInput
                      style={styles.input}
                      keyboardType="numeric"
                      value={heightStr}
                      onChangeText={handleHeightChange}
                      placeholderTextColor={colors.text.tertiary}
                    />
                  </View>
                </View>
              </View>
            ) : (
              <View style={styles.presetsGrid}>
                {PRESETS.map((p, i) => (
                  <Pressable 
                    key={i} 
                    style={[
                      styles.presetCard, 
                      widthStr === p.width.toString() && heightStr === p.height.toString() && styles.presetCardActive
                    ]}
                    onPress={() => handlePresetSelect(p.width, p.height)}
                  >
                    <Text style={[
                      styles.presetLabel,
                      widthStr === p.width.toString() && heightStr === p.height.toString() && styles.presetTextActive
                    ]}>{p.label}</Text>
                    <Text style={styles.presetDims}>{p.width} × {p.height}</Text>
                  </Pressable>
                ))}
              </View>
            )}

            <Pressable 
              style={[styles.primaryBtn, isProcessing && styles.primaryBtnDisabled]} 
              onPress={handleResize}
              disabled={isProcessing}
            >
              <Text style={styles.primaryBtnText}>{isProcessing ? 'Resizing...' : 'Resize'}</Text>
            </Pressable>
          </Animated.View>
        )}

        {resizedUri && (
          <Animated.View entering={FadeInDown.duration(400)} style={styles.resultContainer}>
            <View style={styles.resultCard}>
              <Text style={styles.resultTitle}>Result</Text>
              <Text style={styles.resultDims}>{resizedWidth} × {resizedHeight}</Text>
              <Text style={styles.resultSize}>{formatSize(resizedSize)}</Text>
            </View>

            <View style={styles.actionRow}>
              <Pressable style={styles.actionBtn} onPress={saveToGallery}>
                <Ionicons name="download-outline" size={20} color={colors.text.primary} />
                <Text style={styles.actionBtnText}>Save</Text>
              </Pressable>
              <Pressable style={styles.actionBtn} onPress={shareImage}>
                <Ionicons name="share-outline" size={20} color={colors.text.primary} />
                <Text style={styles.actionBtnText}>Share</Text>
              </Pressable>
            </View>
          </Animated.View>
        )}
      </ScrollView>
    </View>
  );
}

const createStyles = (colors: any, shadows: any) => StyleSheet.create({
  container: {
    flex: 1,
    backgroundColor: colors.bg.base,
  },
  topBar: {
    flexDirection: 'row',
    alignItems: 'center',
    paddingTop: space.xl,
    paddingBottom: space.md,
    paddingHorizontal: space.lg,
    backgroundColor: colors.bg.base,
    borderBottomWidth: 1,
    borderBottomColor: colors.border.subtle,
  },
  backBtn: {
    marginRight: space.md,
  },
  title: {
    fontFamily: font.semibold,
    fontSize: 18,
    color: colors.text.primary,
  },
  content: {
    padding: space.lg,
    paddingBottom: space.xl * 4,
  },
  pickArea: {
    height: 200,
    borderRadius: radius.lg,
    backgroundColor: colors.bg.card,
    borderWidth: 1,
    borderColor: colors.border.subtle,
    borderStyle: 'dashed',
    alignItems: 'center',
    justifyContent: 'center',
    marginBottom: space.lg,
  },
  pickText: {
    marginTop: space.sm,
    fontFamily: font.medium,
    color: colors.text.secondary,
  },
  imagePreviewContainer: {
    height: 200,
    borderRadius: radius.lg,
    backgroundColor: colors.bg.card,
    overflow: 'hidden',
    marginBottom: space.sm,
    borderWidth: 1,
    borderColor: colors.border.subtle,
  },
  imagePreview: {
    width: '100%',
    height: '100%',
  },
  repickBtn: {
    position: 'absolute',
    top: space.sm,
    right: space.sm,
    backgroundColor: 'rgba(0,0,0,0.6)',
    padding: space.sm,
    borderRadius: radius.pill,
  },
  infoText: {
    fontFamily: font.medium,
    color: colors.text.secondary,
    textAlign: 'center',
    marginBottom: space.lg,
  },
  modeTabs: {
    flexDirection: 'row',
    backgroundColor: colors.bg.card,
    borderRadius: radius.pill,
    padding: space.xs,
    marginBottom: space.lg,
    borderWidth: 1,
    borderColor: colors.border.subtle,
  },
  modeTab: {
    flex: 1,
    paddingVertical: space.sm,
    alignItems: 'center',
    borderRadius: radius.pill,
  },
  modeTabActive: {
    backgroundColor: colors.bg.elevated,
  },
  modeTabText: {
    fontFamily: font.medium,
    color: colors.text.secondary,
  },
  modeTabTextActive: {
    color: colors.text.primary,
  },
  card: {
    backgroundColor: colors.bg.card,
    borderRadius: radius.lg,
    padding: space.lg,
    marginBottom: space.lg,
    borderWidth: 1,
    borderColor: colors.border.subtle,
    ...shadows?.card,
  },
  inputRow: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
  },
  inputCol: {
    flex: 1,
  },
  label: {
    fontFamily: font.medium,
    color: colors.text.secondary,
    marginBottom: space.xs,
    fontSize: 12,
  },
  input: {
    backgroundColor: colors.bg.input,
    borderRadius: radius.md,
    color: colors.text.primary,
    fontFamily: font.medium,
    paddingHorizontal: space.md,
    paddingVertical: space.md,
    borderWidth: 1,
    borderColor: colors.border.subtle,
  },
  lockBtn: {
    padding: space.md,
    marginTop: 16,
  },
  presetsGrid: {
    flexDirection: 'row',
    flexWrap: 'wrap',
    gap: space.md,
    marginBottom: space.lg,
  },
  presetCard: {
    width: '47%',
    backgroundColor: colors.bg.card,
    borderRadius: radius.md,
    padding: space.md,
    borderWidth: 1,
    borderColor: colors.border.subtle,
    ...shadows?.card,
  },
  presetCardActive: {
    borderColor: colors.accent.primary,
    backgroundColor: colors.accent.primaryDim,
  },
  presetLabel: {
    fontFamily: font.semibold,
    color: colors.text.primary,
    marginBottom: 4,
  },
  presetTextActive: {
    color: colors.accent.primary,
  },
  presetDims: {
    fontFamily: font.regular,
    color: colors.text.secondary,
    fontSize: 12,
  },
  primaryBtn: {
    backgroundColor: colors.accent.primary,
    paddingVertical: space.lg,
    borderRadius: radius.lg,
    alignItems: 'center',
  },
  primaryBtnDisabled: {
    opacity: 0.5,
  },
  primaryBtnText: {
    fontFamily: font.semibold,
    color: '#FFF',
    fontSize: 16,
  },
  resultContainer: {
    marginTop: space.xl,
  },
  resultCard: {
    backgroundColor: colors.bg.card,
    borderRadius: radius.lg,
    padding: space.lg,
    borderWidth: 1,
    borderColor: colors.accent.success,
    marginBottom: space.lg,
    alignItems: 'center',
  },
  resultTitle: {
    fontFamily: font.semibold,
    color: colors.accent.success,
    marginBottom: space.sm,
  },
  resultDims: {
    fontFamily: font.bold,
    color: colors.text.primary,
    fontSize: 20,
    marginBottom: 4,
  },
  resultSize: {
    fontFamily: font.medium,
    color: colors.text.secondary,
  },
  actionRow: {
    flexDirection: 'row',
    gap: space.md,
  },
  actionBtn: {
    flex: 1,
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'center',
    backgroundColor: colors.bg.card,
    paddingVertical: space.md,
    borderRadius: radius.md,
    borderWidth: 1,
    borderColor: colors.border.subtle,
    gap: space.sm,
  },
  actionBtnText: {
    fontFamily: font.medium,
    color: colors.text.primary,
  },
});
