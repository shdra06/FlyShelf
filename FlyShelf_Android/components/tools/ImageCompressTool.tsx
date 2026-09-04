import React, { useState, useMemo, useCallback } from 'react';
import { View, Text, Pressable, ScrollView, Image, Alert, StyleSheet, Platform } from 'react-native';
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

interface ImageCompressToolProps {
  onBack: () => void;
}

export default function ImageCompressTool({ onBack }: ImageCompressToolProps) {
  const { colors, shadows } = useAppTheme();
  const styles = useMemo(() => createStyles(colors, shadows), [colors, shadows]);

  const [imageUri, setImageUri] = useState<string | null>(null);
  const [originalSize, setOriginalSize] = useState<number>(0);
  
  const [quality, setQuality] = useState<number>(70);
  const [format, setFormat] = useState<ImageManipulator.SaveFormat>(ImageManipulator.SaveFormat.JPEG);
  
  const [compressedUri, setCompressedUri] = useState<string | null>(null);
  const [compressedSize, setCompressedSize] = useState<number>(0);
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
      setCompressedUri(null);
      setCompressedSize(0);
      
      try {
        const fileInfo = await FileSystem.getInfoAsync(asset.uri);
        if (fileInfo.exists && fileInfo.size) {
          setOriginalSize(fileInfo.size);
        }
      } catch (err) {
        console.warn('Could not get original file size', err);
      }
    }
  };

  const handleCompress = async () => {
    if (!imageUri) return;
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);
    setIsProcessing(true);

    try {
      const result = await ImageManipulator.manipulateAsync(
        imageUri,
        [],
        { compress: quality / 100, format: format }
      );

      const fileInfo = await FileSystem.getInfoAsync(result.uri);
      if (fileInfo.exists && fileInfo.size) {
        setCompressedUri(result.uri);
        setCompressedSize(fileInfo.size);
        Haptics.notificationAsync(Haptics.NotificationFeedbackType.Success);
      }
    } catch (error) {
      console.error(error);
      Alert.alert('Error', 'Failed to compress image.');
      Haptics.notificationAsync(Haptics.NotificationFeedbackType.Error);
    } finally {
      setIsProcessing(false);
    }
  };

  const saveToGallery = async () => {
    if (!compressedUri) return;
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);
    try {
      const { status } = await MediaLibrary.requestPermissionsAsync();
      if (status !== 'granted') {
        Alert.alert('Permission needed', 'Please grant permission to save images.');
        return;
      }
      await MediaLibrary.saveToLibraryAsync(compressedUri);
      Alert.alert('Success', 'Image saved to gallery!');
      Haptics.notificationAsync(Haptics.NotificationFeedbackType.Success);
    } catch (err) {
      console.error(err);
      Alert.alert('Error', 'Failed to save image.');
    }
  };

  const shareImage = async () => {
    if (!compressedUri) return;
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);
    try {
      const isAvailable = await Sharing.isAvailableAsync();
      if (!isAvailable) {
        Alert.alert('Error', 'Sharing is not available on this device');
        return;
      }
      await Sharing.shareAsync(compressedUri);
    } catch (err) {
      console.error(err);
    }
  };

  const formatSize = (bytes: number) => {
    if (bytes === 0) return '0 B';
    const k = 1024;
    const sizes = ['B', 'KB', 'MB', 'GB'];
    const i = Math.floor(Math.log(bytes) / Math.log(k));
    return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
  };

  const renderQualityControl = () => (
    <View style={styles.card}>
      <Text style={styles.label}>Quality: {quality}%</Text>
      <View style={styles.sliderRow}>
        <Pressable 
          style={styles.sliderBtn} 
          onPress={() => { Haptics.selectionAsync(); setQuality(Math.max(10, quality - 10)); }}
        >
          <Ionicons name="remove" size={24} color={colors.text.primary} />
        </Pressable>
        <View style={styles.sliderTrack}>
          <View style={[styles.sliderFill, { width: `${quality}%` }]} />
        </View>
        <Pressable 
          style={styles.sliderBtn} 
          onPress={() => { Haptics.selectionAsync(); setQuality(Math.min(100, quality + 10)); }}
        >
          <Ionicons name="add" size={24} color={colors.text.primary} />
        </Pressable>
      </View>
    </View>
  );

  return (
    <View style={styles.container}>
      <View style={styles.topBar}>
        <Pressable onPress={() => { Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light); onBack(); }} hitSlop={12} style={styles.backBtn}>
          <Ionicons name="arrow-back" size={22} color={colors.text.primary} />
        </Pressable>
        <Text style={styles.title}>Compress Image</Text>
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
            <Text style={styles.infoText}>Original: {formatSize(originalSize)}</Text>

            {renderQualityControl()}

            <View style={styles.card}>
              <Text style={styles.label}>Format</Text>
              <View style={styles.formatRow}>
                {['JPEG', 'PNG'].map((fmt) => (
                  <Pressable
                    key={fmt}
                    style={[
                      styles.formatBtn,
                      format === (fmt === 'JPEG' ? ImageManipulator.SaveFormat.JPEG : ImageManipulator.SaveFormat.PNG) && styles.formatBtnActive
                    ]}
                    onPress={() => {
                      Haptics.selectionAsync();
                      setFormat(fmt === 'JPEG' ? ImageManipulator.SaveFormat.JPEG : ImageManipulator.SaveFormat.PNG);
                    }}
                  >
                    <Text style={[
                      styles.formatBtnText,
                      format === (fmt === 'JPEG' ? ImageManipulator.SaveFormat.JPEG : ImageManipulator.SaveFormat.PNG) && styles.formatBtnTextActive
                    ]}>{fmt}</Text>
                  </Pressable>
                ))}
              </View>
            </View>

            <Pressable 
              style={[styles.primaryBtn, isProcessing && styles.primaryBtnDisabled]} 
              onPress={handleCompress}
              disabled={isProcessing}
            >
              <Text style={styles.primaryBtnText}>{isProcessing ? 'Compressing...' : 'Compress'}</Text>
            </Pressable>
          </Animated.View>
        )}

        {compressedUri && (
          <Animated.View entering={FadeInDown.duration(400)} style={styles.resultContainer}>
            <View style={styles.resultCard}>
              <Text style={styles.resultTitle}>Result</Text>
              <View style={styles.comparisonRow}>
                <View style={styles.comparisonCol}>
                  <Text style={styles.comparisonLabel}>Original</Text>
                  <Text style={styles.comparisonValue}>{formatSize(originalSize)}</Text>
                </View>
                <Ionicons name="arrow-forward" size={24} color={colors.text.tertiary} />
                <View style={styles.comparisonCol}>
                  <Text style={styles.comparisonLabel}>Compressed</Text>
                  <Text style={[styles.comparisonValue, { color: colors.accent.success }]}>{formatSize(compressedSize)}</Text>
                </View>
              </View>
              {originalSize > 0 && compressedSize > 0 && (
                <Text style={styles.reductionText}>
                  {Math.round(((originalSize - compressedSize) / originalSize) * 100)}% reduction
                </Text>
              )}
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
  card: {
    backgroundColor: colors.bg.card,
    borderRadius: radius.lg,
    padding: space.lg,
    marginBottom: space.lg,
    borderWidth: 1,
    borderColor: colors.border.subtle,
    ...shadows?.card,
  },
  label: {
    fontFamily: font.medium,
    color: colors.text.primary,
    marginBottom: space.md,
  },
  sliderRow: {
    flexDirection: 'row',
    alignItems: 'center',
  },
  sliderBtn: {
    width: 40,
    height: 40,
    borderRadius: radius.pill,
    backgroundColor: colors.bg.elevated,
    alignItems: 'center',
    justifyContent: 'center',
  },
  sliderTrack: {
    flex: 1,
    height: 8,
    backgroundColor: colors.bg.elevated,
    borderRadius: radius.pill,
    marginHorizontal: space.md,
    overflow: 'hidden',
  },
  sliderFill: {
    height: '100%',
    backgroundColor: colors.accent.primary,
  },
  formatRow: {
    flexDirection: 'row',
    gap: space.md,
  },
  formatBtn: {
    flex: 1,
    paddingVertical: space.md,
    borderRadius: radius.md,
    backgroundColor: colors.bg.elevated,
    alignItems: 'center',
    borderWidth: 1,
    borderColor: 'transparent',
  },
  formatBtnActive: {
    backgroundColor: colors.accent.primaryDim,
    borderColor: colors.accent.primary,
  },
  formatBtnText: {
    fontFamily: font.medium,
    color: colors.text.secondary,
  },
  formatBtnTextActive: {
    color: colors.accent.primary,
  },
  primaryBtn: {
    backgroundColor: colors.accent.primary,
    paddingVertical: space.lg,
    borderRadius: radius.lg,
    alignItems: 'center',
    marginTop: space.sm,
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
  },
  resultTitle: {
    fontFamily: font.semibold,
    color: colors.text.primary,
    marginBottom: space.md,
    textAlign: 'center',
  },
  comparisonRow: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
    marginBottom: space.md,
  },
  comparisonCol: {
    alignItems: 'center',
    flex: 1,
  },
  comparisonLabel: {
    fontFamily: font.regular,
    color: colors.text.secondary,
    fontSize: 12,
    marginBottom: 4,
  },
  comparisonValue: {
    fontFamily: font.bold,
    color: colors.text.primary,
    fontSize: 16,
  },
  reductionText: {
    fontFamily: font.medium,
    color: colors.accent.success,
    textAlign: 'center',
    fontSize: 14,
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
