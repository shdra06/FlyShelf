import React, { useState, useMemo } from 'react';
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

interface ImageFormatToolProps {
  onBack: () => void;
}

export default function ImageFormatTool({ onBack }: ImageFormatToolProps) {
  const { colors, shadows } = useAppTheme();
  const styles = useMemo(() => createStyles(colors, shadows), [colors, shadows]);

  const [imageUri, setImageUri] = useState<string | null>(null);
  const [currentFormat, setCurrentFormat] = useState<string>('');
  
  const [targetFormat, setTargetFormat] = useState<ImageManipulator.SaveFormat>(ImageManipulator.SaveFormat.JPEG);
  const [quality, setQuality] = useState<number>(100);
  
  const [convertedUri, setConvertedUri] = useState<string | null>(null);
  const [convertedSize, setConvertedSize] = useState<number>(0);
  const [isProcessing, setIsProcessing] = useState(false);

  const formats = [
    { label: 'JPEG', value: ImageManipulator.SaveFormat.JPEG },
    { label: 'PNG', value: ImageManipulator.SaveFormat.PNG },
  ];
  if (Platform.OS === 'android' || Platform.OS === 'web') {
    formats.push({ label: 'WebP', value: ImageManipulator.SaveFormat.WEBP });
  }

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
      setConvertedUri(null);
      
      let fmt = 'Unknown';
      if (asset.uri.toLowerCase().endsWith('.jpg') || asset.uri.toLowerCase().endsWith('.jpeg')) fmt = 'JPEG';
      else if (asset.uri.toLowerCase().endsWith('.png')) fmt = 'PNG';
      else if (asset.uri.toLowerCase().endsWith('.webp')) fmt = 'WebP';
      setCurrentFormat(fmt);
    }
  };

  const handleConvert = async () => {
    if (!imageUri) return;
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);
    setIsProcessing(true);

    try {
      const result = await ImageManipulator.manipulateAsync(
        imageUri,
        [],
        { compress: targetFormat === ImageManipulator.SaveFormat.PNG ? 1 : quality / 100, format: targetFormat }
      );

      const fileInfo = await FileSystem.getInfoAsync(result.uri);
      if (fileInfo.exists && fileInfo.size) {
        setConvertedUri(result.uri);
        setConvertedSize(fileInfo.size);
        Haptics.notificationAsync(Haptics.NotificationFeedbackType.Success);
      }
    } catch (error) {
      console.error(error);
      Alert.alert('Error', 'Failed to convert image.');
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
    if (!convertedUri) return;
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);
    try {
      const { status } = await MediaLibrary.requestPermissionsAsync();
      if (status !== 'granted') {
        Alert.alert('Permission needed', 'Please grant permission to save images.');
        return;
      }
      await MediaLibrary.saveToLibraryAsync(convertedUri);
      Alert.alert('Success', 'Image saved to gallery!');
      Haptics.notificationAsync(Haptics.NotificationFeedbackType.Success);
    } catch (err) {
      console.error(err);
      Alert.alert('Error', 'Failed to save image.');
    }
  };

  const shareImage = async () => {
    if (!convertedUri) return;
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);
    try {
      await Sharing.shareAsync(convertedUri);
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
        <Text style={styles.title}>Format Converter</Text>
      </View>

      <ScrollView contentContainerStyle={styles.content}>
        {!imageUri ? (
          <Pressable style={styles.pickArea} onPress={pickImage}>
            <Ionicons name="images-outline" size={48} color={colors.accent.warning} />
            <Text style={styles.pickText}>Tap to select an image</Text>
          </Pressable>
        ) : (
          <Animated.View entering={FadeInDown.duration(400)}>
            <View style={styles.imagePreviewContainer}>
              <Image source={{ uri: imageUri }} style={styles.imagePreview} resizeMode="contain" />
              <View style={styles.badge}>
                <Text style={styles.badgeText}>{currentFormat}</Text>
              </View>
              <Pressable style={styles.repickBtn} onPress={pickImage}>
                <Ionicons name="refresh" size={20} color={colors.text.primary} />
              </Pressable>
            </View>

            <View style={styles.card}>
              <Text style={styles.label}>Target Format</Text>
              <View style={styles.formatRow}>
                {formats.map((fmt) => (
                  <Pressable
                    key={fmt.label}
                    style={[
                      styles.formatBtn,
                      targetFormat === fmt.value && styles.formatBtnActive
                    ]}
                    onPress={() => {
                      Haptics.selectionAsync();
                      setTargetFormat(fmt.value);
                    }}
                  >
                    <Text style={[
                      styles.formatBtnText,
                      targetFormat === fmt.value && styles.formatBtnTextActive
                    ]}>{fmt.label}</Text>
                  </Pressable>
                ))}
              </View>
            </View>

            {targetFormat !== ImageManipulator.SaveFormat.PNG && (
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
            )}

            <Pressable 
              style={[styles.primaryBtn, isProcessing && styles.primaryBtnDisabled]} 
              onPress={handleConvert}
              disabled={isProcessing}
            >
              <Text style={styles.primaryBtnText}>{isProcessing ? 'Converting...' : 'Convert Format'}</Text>
            </Pressable>
          </Animated.View>
        )}

        {convertedUri && (
          <Animated.View entering={FadeInDown.duration(400)} style={styles.resultContainer}>
            <View style={styles.resultCard}>
              <Text style={styles.resultTitle}>Converted Successfully</Text>
              <Text style={styles.resultFormat}>{formats.find(f => f.value === targetFormat)?.label}</Text>
              <Text style={styles.resultSize}>{formatSize(convertedSize)}</Text>
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
    marginBottom: space.lg,
    borderWidth: 1,
    borderColor: colors.border.subtle,
  },
  imagePreview: {
    width: '100%',
    height: '100%',
  },
  badge: {
    position: 'absolute',
    top: space.sm,
    left: space.sm,
    backgroundColor: colors.accent.warning,
    paddingHorizontal: space.md,
    paddingVertical: 4,
    borderRadius: radius.pill,
  },
  badgeText: {
    fontFamily: font.bold,
    color: '#FFF',
    fontSize: 12,
  },
  repickBtn: {
    position: 'absolute',
    top: space.sm,
    right: space.sm,
    backgroundColor: 'rgba(0,0,0,0.6)',
    padding: space.sm,
    borderRadius: radius.pill,
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
    backgroundColor: colors.accent.warningDim,
    borderColor: colors.accent.warning,
  },
  formatBtnText: {
    fontFamily: font.medium,
    color: colors.text.secondary,
  },
  formatBtnTextActive: {
    color: colors.accent.warning,
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
    backgroundColor: colors.accent.warning,
  },
  primaryBtn: {
    backgroundColor: colors.accent.warning,
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
  resultFormat: {
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
