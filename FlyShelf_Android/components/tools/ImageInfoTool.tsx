import React, { useState, useMemo } from 'react';
import { View, Text, Pressable, ScrollView, Image, Alert, StyleSheet } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import * as Haptics from 'expo-haptics';
import * as ImagePicker from 'expo-image-picker';
import * as Sharing from 'expo-sharing';
import * as FileSystem from 'expo-file-system/legacy';
import * as Clipboard from 'expo-clipboard';
import { useAppTheme } from '../../hooks/useAppTheme';
import { font, space, radius } from '../../styles/theme';
import Animated, { FadeInDown } from 'react-native-reanimated';

interface ImageInfoToolProps {
  onBack: () => void;
}

function gcd(a: number, b: number): number {
  return b === 0 ? a : gcd(b, a % b);
}

export default function ImageInfoTool({ onBack }: ImageInfoToolProps) {
  const { colors, shadows } = useAppTheme();
  const styles = useMemo(() => createStyles(colors, shadows), [colors, shadows]);

  const [imageUri, setImageUri] = useState<string | null>(null);
  const [info, setInfo] = useState<any>(null);

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
      
      try {
        const fileInfo = await FileSystem.getInfoAsync(asset.uri);
        
        const w = asset.width;
        const h = asset.height;
        const g = gcd(w, h);
        const ratioStr = `${w / g}:${h / g}`;
        
        let format = 'Unknown';
        if (asset.uri.toLowerCase().endsWith('.jpg') || asset.uri.toLowerCase().endsWith('.jpeg')) format = 'JPEG';
        else if (asset.uri.toLowerCase().endsWith('.png')) format = 'PNG';
        else if (asset.uri.toLowerCase().endsWith('.webp')) format = 'WebP';
        else if (asset.uri.toLowerCase().endsWith('.gif')) format = 'GIF';
        
        const fileName = asset.fileName || asset.uri.split('/').pop() || 'image';

        setInfo({
          fileName,
          size: fileInfo.exists && fileInfo.size ? fileInfo.size : 0,
          width: w,
          height: h,
          aspectRatio: ratioStr,
          format,
          uri: asset.uri
        });

      } catch (err) {
        console.warn('Could not get image info', err);
      }
    }
  };

  const formatSize = (bytes: number) => {
    if (!bytes || bytes === 0) return 'Unknown size';
    const k = 1024;
    const sizes = ['B', 'KB', 'MB', 'GB'];
    const i = Math.floor(Math.log(bytes) / Math.log(k));
    return parseFloat((bytes / Math.pow(k, i)).toFixed(2)) + ' ' + sizes[i];
  };

  const copyInfo = async () => {
    if (!info) return;
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);
    const text = `File: ${info.fileName}
Size: ${formatSize(info.size)}
Dimensions: ${info.width} × ${info.height}
Aspect Ratio: ${info.aspectRatio}
Format: ${info.format}`;
    await Clipboard.setStringAsync(text);
    Alert.alert('Copied', 'Image information copied to clipboard.');
    Haptics.notificationAsync(Haptics.NotificationFeedbackType.Success);
  };

  const shareImage = async () => {
    if (!imageUri) return;
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);
    try {
      await Sharing.shareAsync(imageUri);
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
        <Text style={styles.title}>Image Info</Text>
      </View>

      <ScrollView contentContainerStyle={styles.content}>
        {!imageUri ? (
          <Pressable style={styles.pickArea} onPress={pickImage}>
            <Ionicons name="information-circle-outline" size={48} color={colors.accent.info} />
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

            {info && (
              <View style={styles.infoCard}>
                <View style={styles.infoRow}>
                  <Text style={styles.infoLabel}>File Name</Text>
                  <Text style={styles.infoValue} numberOfLines={1} ellipsizeMode="middle">{info.fileName}</Text>
                </View>
                <View style={styles.divider} />
                
                <View style={styles.infoRow}>
                  <Text style={styles.infoLabel}>Size</Text>
                  <Text style={styles.infoValue}>{formatSize(info.size)}</Text>
                </View>
                <View style={styles.divider} />

                <View style={styles.infoRow}>
                  <Text style={styles.infoLabel}>Dimensions</Text>
                  <Text style={styles.infoValue}>{info.width} × {info.height}</Text>
                </View>
                <View style={styles.divider} />

                <View style={styles.infoRow}>
                  <Text style={styles.infoLabel}>Aspect Ratio</Text>
                  <Text style={styles.infoValue}>{info.aspectRatio}</Text>
                </View>
                <View style={styles.divider} />

                <View style={styles.infoRow}>
                  <Text style={styles.infoLabel}>Format</Text>
                  <Text style={styles.infoValue}>{info.format}</Text>
                </View>
                <View style={styles.divider} />

                <View style={styles.infoRowCol}>
                  <Text style={styles.infoLabel}>Path / URI</Text>
                  <Text style={styles.infoValueSmall}>{info.uri}</Text>
                </View>
              </View>
            )}

            <View style={styles.actionRow}>
              <Pressable style={styles.actionBtn} onPress={copyInfo}>
                <Ionicons name="copy-outline" size={20} color={colors.text.primary} />
                <Text style={styles.actionBtnText}>Copy Info</Text>
              </Pressable>
              <Pressable style={[styles.actionBtn, { backgroundColor: colors.accent.infoDim, borderColor: colors.accent.info }]} onPress={shareImage}>
                <Ionicons name="share-outline" size={20} color={colors.accent.info} />
                <Text style={[styles.actionBtnText, { color: colors.accent.info }]}>Share Image</Text>
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
    height: 240,
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
  repickBtn: {
    position: 'absolute',
    top: space.sm,
    right: space.sm,
    backgroundColor: 'rgba(0,0,0,0.6)',
    padding: space.sm,
    borderRadius: radius.pill,
  },
  infoCard: {
    backgroundColor: colors.bg.card,
    borderRadius: radius.lg,
    padding: space.lg,
    borderWidth: 1,
    borderColor: colors.border.subtle,
    marginBottom: space.lg,
    ...shadows?.card,
  },
  infoRow: {
    flexDirection: 'row',
    justifyContent: 'space-between',
    alignItems: 'center',
    paddingVertical: space.sm,
  },
  infoRowCol: {
    flexDirection: 'column',
    paddingVertical: space.sm,
    gap: 4,
  },
  divider: {
    height: 1,
    backgroundColor: colors.border.subtle,
  },
  infoLabel: {
    fontFamily: font.medium,
    color: colors.text.secondary,
    fontSize: 14,
  },
  infoValue: {
    fontFamily: font.semibold,
    color: colors.text.primary,
    fontSize: 14,
    maxWidth: '60%',
    textAlign: 'right',
  },
  infoValueSmall: {
    fontFamily: font.regular,
    color: colors.text.tertiary,
    fontSize: 12,
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
