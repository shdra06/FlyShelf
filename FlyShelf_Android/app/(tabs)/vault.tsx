import React, { useState, useMemo, useCallback, useRef } from 'react';
import { View, Text, StyleSheet, TouchableOpacity, ScrollView, TextInput, ActivityIndicator, Alert, SafeAreaView, FlatList, Platform, Image, Dimensions, Modal, StatusBar } from 'react-native';
import { LinearGradient } from 'expo-linear-gradient';
import { Ionicons } from '@expo/vector-icons';
import { useAppTheme } from '../../hooks/useAppTheme';
import { font, space, radius } from '../../styles/theme';
import AppErrorBoundary from '../../components/AppErrorBoundary';
import ScreenHeader from '../../components/ScreenHeader';
import { useVault } from '../../features/vault/useVault';
import { VaultCategory, VaultEntry } from '../../features/vault/vaultTypes';
import * as DocumentPicker from 'expo-document-picker';
import * as ImagePicker from 'expo-image-picker';
import * as FileSystem from 'expo-file-system/legacy';
import * as Haptics from 'expo-haptics';
import Animated, { useSharedValue } from 'react-native-reanimated';
import { toast } from '../../context/ToastContext';
import { useSettings } from '../../context/SettingsContext';
import { resolveBestPcUrl } from '../../utils/networkHelpers';

const SCREEN_WIDTH = Dimensions.get('window').width;
const THUMB_GAP = 4;
const THUMB_COLS = 3;
const THUMB_SIZE = Math.floor((SCREEN_WIDTH - space.lg * 2 - THUMB_GAP * (THUMB_COLS - 1)) / THUMB_COLS);
const VAULT_DIR = `${FileSystem.documentDirectory}vault/`;

/** Strip UUID/safe prefix from filename: "ve_1234567890_abc__passport.pdf" → "passport.pdf" */
const cleanDisplayName = (name: string): string => {
  if (!name) return 'Unnamed';
  // Strip "ve_<ts>_<rand>__" prefix from vault safe IDs
  const dblUnder = name.indexOf('__');
  if (dblUnder > 0 && name.startsWith('ve_')) {
    return name.substring(dblUnder + 2) || name;
  }
  // Strip UUID prefix: "2d2e924c-8d0f-4098-af94-c0fc..." → keep original if it matches UUID-like
  const uuidRegex = /^[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}/i;
  if (uuidRegex.test(name)) {
    // If the entire name is a UUID (no extension), just show "Image" or "Document"
    const ext = name.split('.').pop()?.toLowerCase();
    if (['jpg', 'jpeg', 'png', 'webp', 'gif'].includes(ext || '')) return `Photo.${ext}`;
    if (ext === 'pdf') return `Document.pdf`;
    return name;
  }
  return name;
};

function VaultScreenInner() {
  const { colors, shadows } = useAppTheme();
  const s = useMemo(() => createStyles(colors, shadows), [colors, shadows]);
  
  const { manifest, isLoading, addFile, removeFile, openFile, shareFile, getDecryptedFilePath, getEntriesForCategory, searchEntries, hasPermission, requestPermission, storagePath } = useVault();
  const { pairedDevices, pcLocalIp, pairingKey, deviceName } = useSettings();
  
  const [searchQuery, setSearchQuery] = useState('');
  const [selectedCategory, setSelectedCategory] = useState<VaultCategory | null>(null);
  const [viewerVisible, setViewerVisible] = useState(false);
  const [viewerImages, setViewerImages] = useState<VaultEntry[]>([]);
  const [viewerIndex, setViewerIndex] = useState(0);
  const scrollY = useSharedValue(0);
  const scrollHandler = (e: any) => {
    const offsetY = e?.nativeEvent?.contentOffset?.y;
    if (typeof offsetY === 'number') {
      scrollY.value = offsetY;
    }
  };

  const handlePickDocument = async () => {
    if (!selectedCategory) {
      toast.error('Select Category', 'Please select a category first');
      return;
    }
    try {
      const result = await DocumentPicker.getDocumentAsync({ copyToCacheDirectory: true });
      if (!result.canceled && result.assets && result.assets.length > 0) {
        const file = result.assets[0];
        toast.info('Saving...', 'Adding to storage');
        await addFile(file.uri, file.name, file.mimeType || 'application/octet-stream', selectedCategory.id, file.size || 0);
      }
    } catch (e) {
      toast.error('Error', 'Failed to pick document');
    }
  };

  const handleTakePhoto = async () => {
    if (!selectedCategory) {
      toast.error('Select Category', 'Please select a category first');
      return;
    }
    try {
      const permission = await ImagePicker.requestCameraPermissionsAsync();
      if (!permission.granted) {
        Alert.alert('Permission Denied', 'Camera access is required to take photos');
        return;
      }
      const result = await ImagePicker.launchCameraAsync({ quality: 0.8 });
      if (!result.canceled && result.assets && result.assets.length > 0) {
        const file = result.assets[0];
        const filename = file.uri.split('/').pop() || `photo_${Date.now()}.jpg`;
        toast.info('Saving...', 'Adding photo to storage');
        await addFile(file.uri, filename, 'image/jpeg', selectedCategory.id, file.fileSize || 0);
      }
    } catch (e) {
      toast.error('Error', 'Failed to capture photo');
    }
  };

  const showAddOptions = () => {
    Alert.alert('Add File', 'Choose source', [
      { text: '📄 Pick Document', onPress: handlePickDocument },
      { text: '📸 Take Photo', onPress: handleTakePhoto },
      { text: 'Cancel', style: 'cancel' }
    ]);
  };

  const sendEntryToPc = async (entry: VaultEntry) => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);
    const pcUrl = resolveBestPcUrl(pairedDevices, pcLocalIp);
    if (!pcUrl) {
      Alert.alert('No Paired PC Found', 'Connect or pair a PC in FlyShelf settings to send files directly.');
      return;
    }
    toast.info('Sending to PC...', `Transferring ${cleanDisplayName(entry.originalName)}`);
    try {
      const filePath = await getDecryptedFilePath(entry);
      const uploadUrl = `${pcUrl}/api/archive_upload`;
      const response = await FileSystem.uploadAsync(uploadUrl, filePath, {
        httpMethod: 'POST',
        uploadType: FileSystem.FileSystemUploadType.BINARY_CONTENT,
        headers: {
          'X-FlyShelf-Client': 'MobileCompanion',
          'X-Pairing-Key': pairingKey || '',
          'X-Original-Date': Date.now().toString(),
          'X-File-Name': encodeURIComponent(entry.originalName),
          'X-Batch-Name': encodeURIComponent('Quick_Storage'),
          'X-Source-Device': deviceName || 'Android',
        },
      });
      if (response.status === 200) {
        toast.success('Sent to PC', `${cleanDisplayName(entry.originalName)} is now on your PC!`);
        Haptics.notificationAsync(Haptics.NotificationFeedbackType.Success);
      } else {
        throw new Error(`Upload failed with status ${response.status}`);
      }
    } catch (err: any) {
      Alert.alert('Send Failed', err?.message || 'Could not send file to PC.');
    }
  };

  const handleFileOptions = (entry: VaultEntry) => {
    const name = cleanDisplayName(entry.originalName);
    const originLabel = entry.origin ? ` • From: ${entry.origin}${entry.originDevice ? ' (' + entry.originDevice + ')' : ''}` : '';
    Alert.alert(name, `${(entry.fileSize / 1024).toFixed(1)} KB${originLabel}`, [
      { text: '📖 Open', onPress: () => openFile(entry) },
      { text: '📤 Share', onPress: () => shareFile(entry) },
      { text: '💻 Send to PC', onPress: () => sendEntryToPc(entry) },
      { text: '🗑️ Permanently Delete', style: 'destructive', onPress: () => {
        Alert.alert(
          '⚠️ Permanently Delete?',
          `"${name}" will be permanently removed from ALL storage locations (internal + FlyShelf folder). This cannot be undone.`,
          [
            { text: 'Cancel', style: 'cancel' },
            { text: 'Delete Forever', style: 'destructive', onPress: () => removeFile(entry.id) }
          ]
        );
      }},
      { text: 'Cancel', style: 'cancel' }
    ]);
  };

  /** Open image viewer for an image entry */
  const openImageViewer = useCallback((entry: VaultEntry, allImages: VaultEntry[]) => {
    setViewerImages(allImages);
    setViewerIndex(allImages.findIndex(e => e.id === entry.id) || 0);
    setViewerVisible(true);
  }, []);

  /** Get file URI for vault entry */
  const getFileUri = useCallback((entry: VaultEntry): string => {
    return entry.iv
      ? '' // Legacy encrypted — can't show inline, needs decrypt
      : `${VAULT_DIR}${entry.encryptedFilename}`;
  }, []);

  // Separate entries into images and non-images for the dual-mode layout
  const getCategoryContent = useCallback((catId: string) => {
    const entries = getEntriesForCategory(catId);
    const images: VaultEntry[] = [];
    const documents: VaultEntry[] = [];
    for (const e of entries) {
      if (e.mimeType?.startsWith('image/')) {
        images.push(e);
      } else {
        documents.push(e);
      }
    }
    return { images, documents };
  }, [getEntriesForCategory]);

  // === Image Thumbnail ===
  const renderImageThumb = useCallback((item: VaultEntry, allImages: VaultEntry[]) => {
    const uri = getFileUri(item);
    return (
      <TouchableOpacity
        key={item.id}
        style={s.thumbContainer}
        onPress={() => {
          Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
          openImageViewer(item, allImages);
        }}
        onLongPress={() => handleFileOptions(item)}
        activeOpacity={0.8}
      >
        {uri ? (
          <Image
            source={{ uri }}
            style={s.thumbImage}
            resizeMode="cover"
          />
        ) : (
          <View style={[s.thumbImage, { backgroundColor: colors.bg.card, justifyContent: 'center', alignItems: 'center' }]}>
            <Ionicons name="lock-closed" size={24} color={colors.text.tertiary} />
          </View>
        )}
      </TouchableOpacity>
    );
  }, [colors, s, getFileUri, openImageViewer, handleFileOptions]);

  // === Document Row (non-image) ===
  const renderDocItem = useCallback((item: VaultEntry) => {
    const isPdf = item.mimeType?.includes('pdf');
    const isVideo = item.mimeType?.includes('video');
    const iconColor = isPdf ? colors.type.pdf : isVideo ? colors.type.video : colors.type.doc;
    const iconName = isPdf ? 'document' : isVideo ? 'videocam' : 'document-text';
    const displayName = cleanDisplayName(item.originalName);

    return (
      <TouchableOpacity
        key={item.id}
        style={s.fileRow}
        onPress={() => openFile(item)}
        onLongPress={() => handleFileOptions(item)}
        activeOpacity={0.7}
      >
        <View style={[s.fileIconBg, { backgroundColor: `${iconColor}15` }]}>
          <Ionicons name={iconName as any} size={24} color={iconColor} />
        </View>
        <View style={s.fileInfo}>
          <Text style={s.fileName} numberOfLines={1}>{displayName}</Text>
          <View style={s.fileMeta}>
            <Text style={s.fileSize}>{item.fileSize > 1048576 ? `${(item.fileSize / 1048576).toFixed(1)} MB` : `${Math.round(item.fileSize / 1024)} KB`}</Text>
            <Text style={s.fileDate}> • {new Date(item.dateAdded).toLocaleDateString()}</Text>
          </View>
        </View>
        <TouchableOpacity onPress={() => shareFile(item)} style={{ padding: 8, marginRight: 4 }}>
          <Ionicons name="share-outline" size={18} color={colors.accent.primary} />
        </TouchableOpacity>
        <TouchableOpacity onPress={() => handleFileOptions(item)} style={{ padding: 8 }}>
          <Ionicons name="ellipsis-vertical" size={20} color={colors.text.tertiary} />
        </TouchableOpacity>
      </TouchableOpacity>
    );
  }, [colors, s, openFile, shareFile, handleFileOptions]);

  // === Category View: Image grid + document list ===
  const renderCategoryContent = () => {
    if (!selectedCategory) return null;
    const { images, documents } = getCategoryContent(selectedCategory.id);
    const hasContent = images.length > 0 || documents.length > 0;

    return (
      <ScrollView
        contentContainerStyle={{ paddingHorizontal: space.lg, paddingBottom: 120 }}
        keyboardShouldPersistTaps="handled"
        onScroll={scrollHandler}
        scrollEventThrottle={16}
      >
        {/* Image Grid */}
        {images.length > 0 && (
          <View style={s.thumbGrid}>
            {images.map(img => renderImageThumb(img, images))}
          </View>
        )}

        {/* Section divider if both types exist */}
        {images.length > 0 && documents.length > 0 && (
          <View style={s.sectionDivider}>
            <Ionicons name="document-text-outline" size={16} color={colors.text.tertiary} />
            <Text style={s.sectionLabel}>Documents</Text>
          </View>
        )}

        {/* Document List */}
        {documents.map(doc => renderDocItem(doc))}

        {/* Empty state */}
        {!hasContent && (
          <View style={s.emptyState}>
            <Ionicons name="folder-open-outline" size={48} color={colors.text.tertiary} />
            <Text style={s.emptyText}>No files in this category</Text>
            <Text style={[s.emptyText, { fontSize: 12, marginTop: 4 }]}>Tap + to add files</Text>
          </View>
        )}
      </ScrollView>
    );
  };

  // === Search results ===
  const renderSearchItem = ({ item }: { item: VaultEntry }) => {
    const isImage = item.mimeType?.startsWith('image/');
    if (isImage) {
      const uri = getFileUri(item);
      return (
        <TouchableOpacity style={s.searchRow} onPress={() => openFile(item)} onLongPress={() => handleFileOptions(item)} activeOpacity={0.7}>
          {uri ? (
            <Image source={{ uri }} style={s.searchThumb} resizeMode="cover" />
          ) : (
            <View style={[s.searchThumb, { backgroundColor: colors.bg.card, justifyContent: 'center', alignItems: 'center' }]}>
              <Ionicons name="image" size={20} color={colors.type.image} />
            </View>
          )}
          <View style={s.fileInfo}>
            <Text style={s.fileName} numberOfLines={1}>{cleanDisplayName(item.originalName)}</Text>
            <Text style={s.fileSize}>{item.fileSize > 1048576 ? `${(item.fileSize / 1048576).toFixed(1)} MB` : `${Math.round(item.fileSize / 1024)} KB`}</Text>
          </View>
          <TouchableOpacity onPress={() => shareFile(item)} style={{ padding: 8 }}>
            <Ionicons name="share-outline" size={18} color={colors.accent.primary} />
          </TouchableOpacity>
        </TouchableOpacity>
      );
    }
    return renderDocItem(item) as any;
  };

  if (isLoading) {
    return (
      <LinearGradient colors={[colors.bg.base, colors.bg.baseEnd]} style={{ flex: 1, justifyContent: 'center', alignItems: 'center' }}>
        <ActivityIndicator size="large" color={colors.accent.primary} />
        <Text style={{ color: colors.text.secondary, marginTop: 12 }}>Loading storage...</Text>
      </LinearGradient>
    );
  }

  return (
    <LinearGradient colors={[colors.bg.base, colors.bg.baseEnd]} style={{ flex: 1 }}>
      <SafeAreaView style={s.container}>
        <ScreenHeader
          title={selectedCategory ? selectedCategory.name : 'Storage Shelf'}
          subtitle={selectedCategory ? `${selectedCategory.fileCount} files` : 'Quick offline files & documents'}
          scrollY={scrollY}
          leftAction={selectedCategory ? (
            <TouchableOpacity onPress={() => setSelectedCategory(null)} style={{ padding: 8 }}>
              <Ionicons name="chevron-back" size={24} color={colors.text.primary} />
            </TouchableOpacity>
          ) : undefined}
          rightActions={!selectedCategory ? (
            <View style={[s.lockIconWrapper, { backgroundColor: colors.accent.primary + '22' }]}>
              <Ionicons name="folder-outline" size={20} color={colors.accent.primary} />
            </View>
          ) : undefined}
        />

        <View style={s.searchContainer}>
          <View style={s.searchInputWrapper}>
            <Ionicons name="search" size={16} color={colors.text.tertiary} />
            <TextInput
              value={searchQuery}
              onChangeText={setSearchQuery}
              placeholder="Search files..."
              placeholderTextColor={colors.text.tertiary}
              style={s.searchInput}
            />
            {searchQuery ? <TouchableOpacity onPress={() => setSearchQuery('')}><Ionicons name="close-circle" size={18} color={colors.text.tertiary} /></TouchableOpacity> : null}
          </View>
        </View>

        {/* Storage permission banner */}
        {!hasPermission && (
          <TouchableOpacity
            onPress={requestPermission}
            style={{ marginHorizontal: 16, marginBottom: 8, backgroundColor: '#FBBF2420', borderRadius: 12, padding: 12, flexDirection: 'row', alignItems: 'center', gap: 10, borderWidth: 1, borderColor: '#FBBF2440' }}
            activeOpacity={0.7}
          >
            <Ionicons name="warning-outline" size={20} color="#FBBF24" />
            <View style={{ flex: 1 }}>
              <Text style={{ color: '#FBBF24', fontWeight: '700', fontSize: 12 }}>Storage Permission Required</Text>
              <Text style={{ color: colors.text.secondary, fontSize: 11, marginTop: 2 }}>Tap to grant "All Files Access" so vault data survives app reinstalls</Text>
            </View>
            <Ionicons name="chevron-forward" size={16} color="#FBBF24" />
          </TouchableOpacity>
        )}

        {/* Storage path indicator */}
        <View style={{ paddingHorizontal: 16, paddingBottom: 6, flexDirection: 'row', alignItems: 'center', gap: 6 }}>
          <Ionicons name={storagePath === 'Internal' ? 'phone-portrait-outline' : 'folder-outline'} size={12} color={colors.text.tertiary} />
          <Text style={{ color: colors.text.tertiary, fontSize: 10, fontWeight: '600' }}>{storagePath === 'Internal' ? 'Internal Storage' : storagePath}</Text>
        </View>

        {searchQuery ? (
          <FlatList
            data={searchEntries(searchQuery)}
            keyExtractor={item => item.id}
            renderItem={renderSearchItem}
            contentContainerStyle={s.listContent}
            keyboardShouldPersistTaps="handled"
            keyboardDismissMode="on-drag"
            onScroll={scrollHandler}
            scrollEventThrottle={16}
            ListEmptyComponent={<View style={s.emptyState}><Ionicons name="search-outline" size={48} color={colors.text.tertiary} /><Text style={s.emptyText}>No matching files found</Text></View>}
          />
        ) : selectedCategory ? (
          renderCategoryContent()
        ) : (
          <ScrollView contentContainerStyle={s.gridContainer} keyboardShouldPersistTaps="handled" onScroll={scrollHandler} scrollEventThrottle={16}>
            {manifest?.categories?.map(cat => (
              <TouchableOpacity key={cat.id} style={s.categoryCard} onPress={() => setSelectedCategory(cat)} activeOpacity={0.8}>
                <View style={[s.catIconWrapper, { backgroundColor: `${cat.color}15` }]}>
                  <Text style={s.catIcon}>{cat.icon}</Text>
                </View>
                <Text style={s.catName}>{cat.name}</Text>
                <Text style={s.catCount}>{cat.fileCount} files</Text>
              </TouchableOpacity>
            ))}
          </ScrollView>
        )}

        {selectedCategory && !searchQuery && (
          <TouchableOpacity style={s.fab} onPress={showAddOptions} activeOpacity={0.8}>
            <Ionicons name="add" size={32} color="#FFF" />
          </TouchableOpacity>
        )}
      </SafeAreaView>

      {/* ═══ Full-Screen Image Viewer Modal ═══ */}
      <Modal
        visible={viewerVisible}
        transparent
        animationType="fade"
        onRequestClose={() => setViewerVisible(false)}
        statusBarTranslucent
      >
        <View style={s.viewerContainer}>
          <StatusBar barStyle="light-content" backgroundColor="#000" />
          {/* Header */}
          <View style={s.viewerHeader}>
            <TouchableOpacity onPress={() => setViewerVisible(false)} style={s.viewerCloseBtn}>
              <Ionicons name="close" size={28} color="#FFF" />
            </TouchableOpacity>
            <Text style={s.viewerTitle} numberOfLines={1}>
              {viewerImages[viewerIndex] ? cleanDisplayName(viewerImages[viewerIndex].originalName) : ''}
            </Text>
            <Text style={s.viewerCounter}>{viewerIndex + 1} / {viewerImages.length}</Text>
          </View>

          {/* Image */}
          <FlatList
            data={viewerImages}
            horizontal
            pagingEnabled
            showsHorizontalScrollIndicator={false}
            initialScrollIndex={viewerIndex}
            getItemLayout={(_, idx) => ({ length: SCREEN_WIDTH, offset: SCREEN_WIDTH * idx, index: idx })}
            onMomentumScrollEnd={(e) => {
              const idx = Math.round(e.nativeEvent.contentOffset.x / SCREEN_WIDTH);
              setViewerIndex(idx);
            }}
            keyExtractor={item => item.id}
            renderItem={({ item }) => {
              const uri = getFileUri(item);
              return (
                <View style={{ width: SCREEN_WIDTH, flex: 1, justifyContent: 'center', alignItems: 'center' }}>
                  {uri ? (
                    <Image
                      source={{ uri }}
                      style={{ width: SCREEN_WIDTH, height: '100%' }}
                      resizeMode="contain"
                    />
                  ) : (
                    <View style={{ alignItems: 'center' }}>
                      <Ionicons name="lock-closed" size={48} color="#666" />
                      <Text style={{ color: '#999', marginTop: 12 }}>Encrypted file</Text>
                    </View>
                  )}
                </View>
              );
            }}
          />

          {/* Bottom Action Bar */}
          <View style={s.viewerActions}>
            <TouchableOpacity
              style={s.viewerActionBtn}
              onPress={() => {
                if (viewerImages[viewerIndex]) {
                  shareFile(viewerImages[viewerIndex]);
                }
              }}
            >
              <Ionicons name="share-outline" size={24} color="#FFF" />
              <Text style={s.viewerActionLabel}>Share</Text>
            </TouchableOpacity>
            <TouchableOpacity
              style={s.viewerActionBtn}
              onPress={() => {
                if (viewerImages[viewerIndex]) {
                  sendEntryToPc(viewerImages[viewerIndex]);
                }
              }}
            >
              <Ionicons name="laptop-outline" size={24} color="#FFF" />
              <Text style={s.viewerActionLabel}>Send to PC</Text>
            </TouchableOpacity>
            <TouchableOpacity
              style={s.viewerActionBtn}
              onPress={() => {
                if (viewerImages[viewerIndex]) {
                  const entry = viewerImages[viewerIndex];
                  Alert.alert('Delete', `Remove "${cleanDisplayName(entry.originalName)}"?`, [
                    { text: 'Cancel', style: 'cancel' },
                    { text: 'Delete', style: 'destructive', onPress: () => {
                      removeFile(entry.id);
                      const newImages = viewerImages.filter(e => e.id !== entry.id);
                      if (newImages.length === 0) {
                        setViewerVisible(false);
                      } else {
                        setViewerImages(newImages);
                        setViewerIndex(Math.min(viewerIndex, newImages.length - 1));
                      }
                    }}
                  ]);
                }
              }}
            >
              <Ionicons name="trash-outline" size={24} color="#EF4444" />
              <Text style={[s.viewerActionLabel, { color: '#EF4444' }]}>Delete</Text>
            </TouchableOpacity>
          </View>
        </View>
      </Modal>
    </LinearGradient>
  );
}

export default function VaultScreen() {
  return (
    <AppErrorBoundary>
      <VaultScreenInner />
    </AppErrorBoundary>
  );
}

const createStyles = (colors: any, shadows: any) => StyleSheet.create({
  container: {
    flex: 1,
  },
  searchContainer: {
    paddingHorizontal: space.xl,
    marginBottom: space.md,
  },
  searchInputWrapper: {
    flexDirection: 'row',
    alignItems: 'center',
    backgroundColor: colors.bg.input,
    borderRadius: radius.md,
    paddingHorizontal: space.md,
    borderWidth: 1,
    borderColor: colors.border.subtle,
  },
  searchInput: {
    flex: 1,
    color: colors.text.primary,
    fontSize: 14,
    paddingVertical: space.md,
    marginLeft: space.sm,
    fontFamily: font.medium,
  },
  gridContainer: {
    paddingHorizontal: space.lg,
    paddingBottom: 100,
    flexDirection: 'row',
    flexWrap: 'wrap',
    justifyContent: 'space-between',
  },
  categoryCard: {
    width: '48%',
    backgroundColor: colors.bg.card,
    borderRadius: radius.lg,
    padding: space.lg,
    marginBottom: space.lg,
    borderWidth: 1,
    borderColor: colors.border.subtle,
    ...shadows.card,
  },
  catIconWrapper: {
    width: 48,
    height: 48,
    borderRadius: radius.md,
    justifyContent: 'center',
    alignItems: 'center',
    marginBottom: space.md,
  },
  catIcon: {
    fontSize: 24,
  },
  catName: {
    color: colors.text.primary,
    fontFamily: font.bold,
    fontSize: 15,
    marginBottom: space.xs,
  },
  catCount: {
    color: colors.text.secondary,
    fontFamily: font.medium,
    fontSize: 12,
  },
  lockIconWrapper: {
    width: 36,
    height: 36,
    borderRadius: 18,
    alignItems: 'center',
    justifyContent: 'center',
  },
  listContent: {
    paddingHorizontal: space.lg,
    paddingBottom: 120,
  },
  // === Thumbnail Grid ===
  thumbGrid: {
    flexDirection: 'row',
    flexWrap: 'wrap',
    gap: THUMB_GAP,
    marginBottom: space.lg,
  },
  thumbContainer: {
    width: THUMB_SIZE,
    height: THUMB_SIZE,
    borderRadius: radius.sm,
    overflow: 'hidden',
  },
  thumbImage: {
    width: '100%',
    height: '100%',
    borderRadius: radius.sm,
  },
  // === Document Rows ===
  fileRow: {
    flexDirection: 'row',
    alignItems: 'center',
    backgroundColor: colors.bg.card,
    borderRadius: radius.md,
    padding: space.md,
    marginBottom: space.sm,
    borderWidth: 1,
    borderColor: colors.border.subtle,
  },
  fileIconBg: {
    width: 44,
    height: 44,
    borderRadius: radius.sm,
    alignItems: 'center',
    justifyContent: 'center',
  },
  fileInfo: {
    flex: 1,
    marginLeft: space.md,
  },
  fileName: {
    color: colors.text.primary,
    fontFamily: font.semibold,
    fontSize: 14,
    marginBottom: 4,
  },
  fileMeta: {
    flexDirection: 'row',
    alignItems: 'center',
  },
  fileSize: {
    color: colors.text.tertiary,
    fontFamily: font.medium,
    fontSize: 11,
  },
  fileDate: {
    color: colors.text.tertiary,
    fontFamily: font.regular,
    fontSize: 11,
  },
  // === Section Divider ===
  sectionDivider: {
    flexDirection: 'row',
    alignItems: 'center',
    marginBottom: space.md,
    gap: 6,
  },
  sectionLabel: {
    color: colors.text.tertiary,
    fontFamily: font.semibold,
    fontSize: 12,
    textTransform: 'uppercase',
    letterSpacing: 0.5,
  },
  // === Search ===
  searchRow: {
    flexDirection: 'row',
    alignItems: 'center',
    backgroundColor: colors.bg.card,
    borderRadius: radius.md,
    padding: space.sm,
    marginBottom: space.sm,
    borderWidth: 1,
    borderColor: colors.border.subtle,
  },
  searchThumb: {
    width: 48,
    height: 48,
    borderRadius: radius.sm,
  },
  // === Empty / FAB ===
  emptyState: {
    alignItems: 'center',
    justifyContent: 'center',
    paddingTop: 60,
  },
  emptyText: {
    color: colors.text.secondary,
    fontFamily: font.medium,
    fontSize: 14,
    marginTop: space.md,
  },
  fab: {
    position: 'absolute',
    bottom: space.xl + (Platform.OS === 'ios' ? 88 : 72),
    right: space.xl,
    width: 60,
    height: 60,
    borderRadius: 30,
    backgroundColor: colors.accent.primary,
    alignItems: 'center',
    justifyContent: 'center',
    ...shadows.glow(colors.accent.primary),
  },
  // === Image Viewer ===
  viewerContainer: {
    flex: 1,
    backgroundColor: '#000',
  },
  viewerHeader: {
    flexDirection: 'row',
    alignItems: 'center',
    paddingTop: Platform.OS === 'android' ? 40 : 56,
    paddingHorizontal: 16,
    paddingBottom: 12,
  },
  viewerCloseBtn: {
    width: 40,
    height: 40,
    borderRadius: 20,
    backgroundColor: 'rgba(255,255,255,0.15)',
    justifyContent: 'center',
    alignItems: 'center',
  },
  viewerTitle: {
    flex: 1,
    color: '#FFF',
    fontFamily: font.semibold,
    fontSize: 16,
    marginLeft: 12,
  },
  viewerCounter: {
    color: 'rgba(255,255,255,0.6)',
    fontFamily: font.medium,
    fontSize: 13,
    marginLeft: 8,
  },
  viewerActions: {
    flexDirection: 'row',
    justifyContent: 'space-around',
    paddingVertical: 16,
    paddingBottom: Platform.OS === 'ios' ? 40 : 24,
    backgroundColor: 'rgba(0,0,0,0.8)',
  },
  viewerActionBtn: {
    alignItems: 'center',
    paddingHorizontal: 20,
  },
  viewerActionLabel: {
    color: '#FFF',
    fontFamily: font.medium,
    fontSize: 11,
    marginTop: 4,
  },
});
