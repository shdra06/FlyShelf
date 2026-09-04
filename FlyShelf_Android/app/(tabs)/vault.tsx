import React, { useState, useMemo } from 'react';
import { View, Text, StyleSheet, TouchableOpacity, ScrollView, TextInput, ActivityIndicator, Alert, SafeAreaView, FlatList, Platform } from 'react-native';
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

function VaultScreenInner() {
  const { colors, shadows } = useAppTheme();
  const s = useMemo(() => createStyles(colors, shadows), [colors, shadows]);
  
  const { manifest, isLoading, addFile, removeFile, openFile, shareFile, getDecryptedFilePath, getEntriesForCategory, searchEntries } = useVault();
  const { pairedDevices, pcLocalIp, pairingKey, deviceName } = useSettings();
  
  const [searchQuery, setSearchQuery] = useState('');
  const [selectedCategory, setSelectedCategory] = useState<VaultCategory | null>(null);
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
    toast.info('Sending to PC...', `Transferring ${entry.originalName}`);
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
        toast.success('Sent to PC', `${entry.originalName} is now on your PC!`);
        Haptics.notificationAsync(Haptics.NotificationFeedbackType.Success);
      } else {
        throw new Error(`Upload failed with status ${response.status}`);
      }
    } catch (err: any) {
      Alert.alert('Send Failed', err?.message || 'Could not send file to PC.');
    }
  };

  const handleFileOptions = (entry: VaultEntry) => {
    Alert.alert(entry.originalName, 'File actions', [
      { text: '📖 Open', onPress: () => openFile(entry) },
      { text: '💻 Send to PC', onPress: () => sendEntryToPc(entry) },
      { text: '📤 Share', onPress: () => shareFile(entry) },
      { text: '🗑️ Delete', style: 'destructive', onPress: () => {
        Alert.alert('Delete File', `Remove "${entry.originalName}"?`, [
          { text: 'Cancel', style: 'cancel' },
          { text: 'Delete', style: 'destructive', onPress: () => removeFile(entry.id) }
        ]);
      }},
      { text: 'Cancel', style: 'cancel' }
    ]);
  };

  if (isLoading) {
    return (
      <LinearGradient colors={[colors.bg.base, colors.bg.baseEnd]} style={{ flex: 1, justifyContent: 'center', alignItems: 'center' }}>
        <ActivityIndicator size="large" color={colors.accent.primary} />
        <Text style={{ color: colors.text.secondary, marginTop: 12 }}>Loading storage...</Text>
      </LinearGradient>
    );
  }

  const renderFileItem = ({ item }: { item: VaultEntry }) => {
    const isPdf = item.mimeType?.includes('pdf');
    const isImage = item.mimeType?.includes('image');
    const isVideo = item.mimeType?.includes('video');
    const iconColor = isPdf ? colors.type.pdf : isImage ? colors.type.image : isVideo ? colors.type.video : colors.type.doc;
    const iconName = isPdf ? 'document' : isImage ? 'image' : isVideo ? 'videocam' : 'document-text';

    return (
      <TouchableOpacity 
        style={s.fileRow} 
        onPress={() => openFile(item)} 
        onLongPress={() => handleFileOptions(item)}
        activeOpacity={0.7}
      >
        <View style={[s.fileIconBg, { backgroundColor: `${iconColor}15` }]}>
          <Ionicons name={iconName as any} size={24} color={iconColor} />
        </View>
        <View style={s.fileInfo}>
          <Text style={s.fileName} numberOfLines={1}>{item.originalName}</Text>
          <View style={s.fileMeta}>
            <Text style={s.fileSize}>{item.fileSize > 1048576 ? `${(item.fileSize / 1048576).toFixed(1)} MB` : `${Math.round(item.fileSize / 1024)} KB`}</Text>
            <Text style={s.fileDate}> • {new Date(item.dateAdded).toLocaleDateString()}</Text>
          </View>
        </View>
        <TouchableOpacity onPress={() => handleFileOptions(item)} style={{ padding: 8 }}>
          <Ionicons name="ellipsis-vertical" size={20} color={colors.text.tertiary} />
        </TouchableOpacity>
      </TouchableOpacity>
    );
  };

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

        {searchQuery ? (
          <FlatList
            data={searchEntries(searchQuery)}
            keyExtractor={item => item.id}
            renderItem={renderFileItem}
            contentContainerStyle={s.listContent}
            keyboardShouldPersistTaps="handled"
            keyboardDismissMode="on-drag"
            onScroll={scrollHandler}
            scrollEventThrottle={16}
            ListEmptyComponent={<View style={s.emptyState}><Ionicons name="search-outline" size={48} color={colors.text.tertiary} /><Text style={s.emptyText}>No matching files found</Text></View>}
          />
        ) : selectedCategory ? (
          <FlatList
            data={getEntriesForCategory(selectedCategory.id)}
            keyExtractor={item => item.id}
            renderItem={renderFileItem}
            contentContainerStyle={s.listContent}
            keyboardShouldPersistTaps="handled"
            keyboardDismissMode="on-drag"
            onScroll={scrollHandler}
            scrollEventThrottle={16}
            ListEmptyComponent={<View style={s.emptyState}><Ionicons name="folder-open-outline" size={48} color={colors.text.tertiary} /><Text style={s.emptyText}>No files in this category</Text></View>}
          />
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
  }
});
