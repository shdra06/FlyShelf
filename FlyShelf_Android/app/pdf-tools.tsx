// PDF Tools — Modularized Suite for FlyShelf Android
import { useState, useEffect, useMemo } from 'react';
import {
  View, Text, ScrollView, Pressable, SafeAreaView, Alert, TextInput, TouchableOpacity,
} from 'react-native';
import { router } from 'expo-router';
import { Ionicons, MaterialCommunityIcons } from '@expo/vector-icons';
import * as Haptics from 'expo-haptics';
import * as DocumentPicker from 'expo-document-picker';
import * as ImagePicker from 'expo-image-picker';
import AsyncStorage from '@react-native-async-storage/async-storage';
import Animated, { FadeInDown } from 'react-native-reanimated';
import { space, radius, font } from '../styles/theme';
import { createPdfToolsStyles } from '../styles/pdfToolsStyles';
import { useAppTheme } from '../hooks/useAppTheme';
import { useSafeAreaInsets } from 'react-native-safe-area-context';

import { cleanupOldPdfFiles } from '../utils/pdfToolsUtils';
import { ToolId, SelectedFile, RecentPdf } from '../components/pdf/types';
import MergeTool from '../components/pdf/MergeTool';
import SplitTool from '../components/pdf/SplitTool';
import EditPagesTool from '../components/pdf/EditPagesTool';
import ImagesToPdfTool from '../components/pdf/ImagesToPdfTool';
import ExtractTool from '../components/pdf/ExtractTool';
import WatermarkTool from '../components/pdf/WatermarkTool';
import PasswordTool from '../components/pdf/PasswordTool';
import MetadataTool from '../components/pdf/MetadataTool';
import InfoTool from '../components/pdf/InfoTool';
import CompressTool from '../components/pdf/CompressTool';
import PdfToWordTool from '../components/pdf/PdfToWordTool';
import PdfEditorScreen from '../components/pdf/PdfEditorScreen';
import DocumentScanner from '../components/pdf/DocumentScanner';
import { buildEditedPdf, cleanupEditorTempFiles } from '../utils/pdfEditorUtils';
import { PageEntry, ImageFilter } from '../components/pdf/types';

const RECENT_KEY = '@flyshelf_recent_pdfs';

type CategoryId = 'all' | 'create' | 'conversions' | 'edit' | 'security';

interface ToolDef {
  id: ToolId;
  icon: string;
  iconLib: 'ion' | 'mci';
  color: string;
  label: string;
  desc: string;
  category: CategoryId;
  badge?: string;
}

const CATEGORIES: { id: CategoryId; label: string; icon: string }[] = [
  { id: 'all', label: 'All', icon: 'apps-outline' },
  { id: 'create', label: 'Create', icon: 'add-circle-outline' },
  { id: 'conversions', label: 'Convert', icon: 'swap-horizontal-outline' },
  { id: 'edit', label: 'Edit & Pages', icon: 'create-outline' },
  { id: 'security', label: 'Security & Info', icon: 'shield-checkmark-outline' },
];

export default function PdfToolsScreen() {
  const { colors, shadows } = useAppTheme();
  const s = useMemo(() => createPdfToolsStyles(colors, shadows), [colors, shadows]);
  const insets = useSafeAreaInsets();

  const TOOLS: ToolDef[] = useMemo(() => [
    { id: 'scanToPdf', icon: 'scan-outline', iconLib: 'ion', color: colors.accent.success, label: 'Scan to PDF', desc: 'Camera scanner', category: 'create' },
    { id: 'pdfEditor', icon: 'create-outline', iconLib: 'ion', color: colors.accent.primary, label: 'PDF Editor', desc: 'Full page editor', category: 'create' },
    { id: 'pdfToWord', icon: 'file-word-box', iconLib: 'mci', color: colors.type.doc, label: 'PDF → Word', desc: 'DOCX with tables', category: 'conversions' },
    { id: 'compress', icon: 'flash-outline', iconLib: 'ion', color: colors.accent.info, label: 'Compress', desc: 'Reduce file size', category: 'conversions' },
    { id: 'imagesToPdf', icon: 'images-outline', iconLib: 'ion', color: colors.type.image, label: 'Images→PDF', desc: 'Convert photos', category: 'conversions' },
    { id: 'merge', icon: 'git-merge-outline', iconLib: 'ion', color: colors.accent.primary, label: 'Merge', desc: 'Combine PDFs', category: 'edit' },
    { id: 'split', icon: 'call-split', iconLib: 'mci', color: colors.accent.warning, label: 'Split', desc: 'Split by range', category: 'edit' },
    { id: 'editPages', icon: 'document-text-outline', iconLib: 'ion', color: colors.accent.success, label: 'Edit Pages', desc: 'Reorder & rotate', category: 'edit' },
    { id: 'extract', icon: 'cut-outline', iconLib: 'ion', color: colors.accent.warning, label: 'Extract', desc: 'Pick single pages', category: 'edit' },
    { id: 'watermark', icon: 'water-outline', iconLib: 'ion', color: colors.type.image, label: 'Watermark', desc: 'Add text stamp', category: 'security' },
    { id: 'password', icon: 'lock-closed-outline', iconLib: 'ion', color: colors.accent.error, label: 'Password', desc: 'Protect document', category: 'security' },
    { id: 'metadata', icon: 'information-circle-outline', iconLib: 'ion', color: colors.type.ppt, label: 'Metadata', desc: 'Edit title & author', category: 'security' },
    { id: 'info', icon: 'analytics-outline', iconLib: 'ion', color: colors.text.secondary, label: 'PDF Info', desc: 'Inspect properties', category: 'security' },
  ], [colors]);

  const [activeTool, setActiveTool] = useState<ToolId | null>(null);
  const [recentPdfs, setRecentPdfs] = useState<RecentPdf[]>([]);
  const [selectedCategory, setSelectedCategory] = useState<CategoryId>('all');
  const [searchQuery, setSearchQuery] = useState('');

  useEffect(() => {
    AsyncStorage.getItem(RECENT_KEY).then(data => {
      if (data) {
        try { setRecentPdfs(JSON.parse(data)); } catch { }
      }
    }).catch(() => {});
  }, []);

  useEffect(() => { cleanupOldPdfFiles(); }, []);

  const saveRecent = async (name: string, path: string, pages: number, tool: ToolId) => {
    const entry: RecentPdf = { name, path, pages, date: Date.now(), tool };
    setRecentPdfs(prev => {
      const updated = [entry, ...prev.filter(r => r.path !== path)].slice(0, 10);
      AsyncStorage.setItem(RECENT_KEY, JSON.stringify(updated)).catch(() => {});
      return updated;
    });
  };

  const pickPdf = async (multiple = false): Promise<SelectedFile[]> => {
    try {
      const result = await DocumentPicker.getDocumentAsync({
        type: 'application/pdf', multiple,
        copyToCacheDirectory: true,
      });
      if (result.canceled || !result.assets?.length) return [];
      return result.assets.map(a => ({ uri: a.uri, name: a.name || 'document.pdf', size: a.size ?? 0 }));
    } catch (e: any) {
      Alert.alert('File Picker Error', e.message || 'Failed to pick PDF file(s).');
      return [];
    }
  };

  const pickImages = async (): Promise<SelectedFile[]> => {
    try {
      const result = await ImagePicker.launchImageLibraryAsync({
        mediaTypes: ['images'],
        allowsMultipleSelection: true, quality: 1,
      });
      if (result.canceled || !result.assets?.length) return [];
      return result.assets.map((a, i) => ({
        uri: a.uri,
        name: a.fileName || `image_${i + 1}.${a.uri.split('.').pop() || 'jpg'}`,
        size: a.fileSize,
      }));
    } catch (e: any) {
      Alert.alert('Image Picker Error', e.message || 'Failed to pick image(s).');
      return [];
    }
  };

  const filteredTools = useMemo(() => {
    return TOOLS.filter(t => {
      const matchCat = selectedCategory === 'all' || t.category === selectedCategory;
      const matchSearch = !searchQuery.trim() ||
        t.label.toLowerCase().includes(searchQuery.toLowerCase()) ||
        t.desc.toLowerCase().includes(searchQuery.toLowerCase());
      return matchCat && matchSearch;
    });
  }, [selectedCategory, searchQuery]);

  // ── Scanner & Editor State ──
  const [editorPdf, setEditorPdf] = useState<{ uri: string; name: string } | null>(null);

  useEffect(() => { cleanupEditorTempFiles(); }, []);

  const handleScanComplete = async (imageUris: string[], _filter: ImageFilter) => {
    if (imageUris.length === 0) return;

    // Convert scanned images into a PDF using the editor
    try {
      // M-11: Get actual image dimensions instead of hardcoding A4
      const pages: PageEntry[] = await Promise.all(imageUris.map(async (uri, i) => {
        let w = 595, h = 842; // A4 fallback
        try {
          const dims = await new Promise<{ width: number; height: number }>((resolve, reject) => {
            require('react-native').Image.getSize(
              uri,
              (width: number, height: number) => resolve({ width, height }),
              (err: Error) => reject(err),
            );
          });
          // Scale to PDF points (max 2000pt dimension)
          const scale = Math.min(2000 / Math.max(dims.width, dims.height), 1);
          w = Math.round(dims.width * scale);
          h = Math.round(dims.height * scale);
        } catch {}
        return {
          index: i,
          originalIndex: i,
          width: w,
          height: h,
          rotation: 0,
          source: 'scanned' as const,
          sourceUri: uri,
        };
      }));

      const outputPath = await buildEditedPdf('', pages);
      const name = `Scan_${new Date().toISOString().slice(0, 10)}.pdf`;
      saveRecent(name, outputPath, pages.length, 'scanToPdf');
      // Open the result in the editor for further editing
      setEditorPdf({ uri: outputPath, name });
      setActiveTool('pdfEditor');
      Haptics.notificationAsync(Haptics.NotificationFeedbackType.Success);
    } catch (e: any) {
      Alert.alert('Scan Error', e.message || 'Failed to create PDF from scanned pages');
    }
  };

  const openPdfEditor = async () => {
    const files = await pickPdf(false);
    if (files.length > 0) {
      setEditorPdf({ uri: files[0].uri, name: files[0].name });
      setActiveTool('pdfEditor');
    } else {
      // H-1: User cancelled picker — return to tool grid, don't leave blank screen
      setActiveTool(null);
    }
  };

  const renderTool = () => {
    const back = () => { setActiveTool(null); setEditorPdf(null); };
    switch (activeTool) {
      case 'scanToPdf':
        return (
          <DocumentScanner
            visible={true}
            onClose={back}
            onScanned={handleScanComplete}
          />
        );
      case 'pdfEditor':
        if (editorPdf) {
          return (
            <PdfEditorScreen
              sourceUri={editorPdf.uri}
              sourceName={editorPdf.name}
              onClose={back}
              onSaved={(outputUri, name) => {
                saveRecent(name, outputUri, 0, 'pdfEditor');
                back();
              }}
            />
          );
        }
        // If no PDF selected yet, trigger picker
        openPdfEditor();
        return null;
      case 'pdfToWord': return <PdfToWordTool onBack={back} onPickFile={() => pickPdf(false)} saveRecent={saveRecent} />;
      case 'merge': return <MergeTool onBack={back} onPickFiles={() => pickPdf(true)} saveRecent={saveRecent} />;
      case 'split': return <SplitTool onBack={back} onPickFile={() => pickPdf(false)} saveRecent={saveRecent} />;
      case 'editPages': return <EditPagesTool onBack={back} onPickFile={() => pickPdf(false)} onPickImages={pickImages} saveRecent={saveRecent} />;
      case 'compress': return <CompressTool onBack={back} onPickFile={() => pickPdf(false)} saveRecent={saveRecent} />;
      case 'imagesToPdf': return <ImagesToPdfTool onBack={back} onPickImages={pickImages} saveRecent={saveRecent} />;
      case 'extract': return <ExtractTool onBack={back} onPickFile={() => pickPdf(false)} saveRecent={saveRecent} />;
      case 'watermark': return <WatermarkTool onBack={back} onPickFile={() => pickPdf(false)} saveRecent={saveRecent} />;
      case 'password': return <PasswordTool onBack={back} onPickFile={() => pickPdf(false)} />;
      case 'metadata': return <MetadataTool onBack={back} onPickFile={() => pickPdf(false)} saveRecent={saveRecent} />;
      case 'info': return <InfoTool onBack={back} onPickFile={() => pickPdf(false)} saveRecent={saveRecent} />;
      default: return null;
    }
  };

  if (activeTool) return <SafeAreaView style={s.safe}>{renderTool()}</SafeAreaView>;

  return (
    <SafeAreaView style={s.safe}>
      <View style={s.container}>
        <View style={{ paddingHorizontal: space.xl, paddingTop: insets.top + 8, paddingBottom: space.sm }}>
          <View style={{ flexDirection: 'row', alignItems: 'center', marginBottom: space.xs }}>
            <TouchableOpacity
              onPress={() => {
                Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
                router.back();
              }}
              style={{
                width: 38,
                height: 38,
                borderRadius: 12,
                backgroundColor: colors.bg.card,
                justifyContent: 'center',
                alignItems: 'center',
                borderWidth: 1,
                borderColor: colors.border.subtle,
                marginRight: 12,
              }}
              accessibilityLabel="Back to Home"
              accessibilityRole="button"
            >
              <Ionicons name="arrow-back" size={20} color={colors.text.primary} />
            </TouchableOpacity>
            <View style={{ flex: 1 }}>
              <Text style={s.headerTitle}>PDF Power Tools</Text>
              <Text style={{ color: colors.text.tertiary, fontSize: 13, marginTop: 1 }}>
                High-performance local &amp; cross-device tools
              </Text>
            </View>
          </View>

          {/* Search Bar */}
          <View style={{
            flexDirection: 'row',
            alignItems: 'center',
            backgroundColor: colors.bg.card,
            borderRadius: radius.md,
            paddingHorizontal: space.md,
            paddingVertical: 8,
            marginTop: space.md,
            borderWidth: 1,
            borderColor: colors.border.subtle,
          }}>
            <Ionicons name="search-outline" size={18} color={colors.text.tertiary} style={{ marginRight: 8 }} />
            <TextInput
              style={{ flex: 1, color: colors.text.primary, fontSize: 14, padding: 0 }}
              placeholder="Search tools (e.g. word, compress, merge)..."
              placeholderTextColor={colors.text.tertiary}
              value={searchQuery}
              onChangeText={setSearchQuery}
            />
            {searchQuery ? (
              <Pressable onPress={() => setSearchQuery('')} hitSlop={8}>
                <Ionicons name="close-circle" size={18} color={colors.text.tertiary} />
              </Pressable>
            ) : null}
          </View>

          {/* Category Tabs */}
          <ScrollView
            horizontal
            showsHorizontalScrollIndicator={false}
            contentContainerStyle={{ gap: 8, paddingVertical: space.sm }}
          >
            {CATEGORIES.map(cat => {
              const isSelected = selectedCategory === cat.id;
              return (
                <Pressable
                  key={cat.id}
                  onPress={() => {
                    setSelectedCategory(cat.id);
                    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
                  }}
                  style={{
                    flexDirection: 'row',
                    alignItems: 'center',
                    backgroundColor: isSelected ? colors.accent.primary : colors.bg.card,
                    paddingHorizontal: 12,
                    paddingVertical: 6,
                    borderRadius: radius.pill,
                    borderWidth: 1,
                    borderColor: isSelected ? colors.accent.primary : colors.border.subtle,
                  }}
                >
                  <Ionicons
                    name={cat.icon as any}
                    size={14}
                    color={isSelected ? '#fff' : colors.text.secondary}
                    style={{ marginRight: 5 }}
                  />
                  <Text style={{
                    fontSize: 12,
                    fontWeight: isSelected ? '700' : '500',
                    color: isSelected ? '#fff' : colors.text.secondary,
                  }}>
                    {cat.label}
                  </Text>
                </Pressable>
              );
            })}
          </ScrollView>
        </View>

        <ScrollView contentContainerStyle={s.scrollContent} showsVerticalScrollIndicator={false}>
          {filteredTools.length === 0 ? (
            <View style={{ alignItems: 'center', justifyContent: 'center', paddingVertical: 48, paddingHorizontal: 20 }}>
              <View style={{ width: 56, height: 56, borderRadius: 28, backgroundColor: colors.bg.card, alignItems: 'center', justifyContent: 'center', marginBottom: 16, borderWidth: 1, borderColor: colors.border.subtle }}>
                <Ionicons name="search-outline" size={24} color={colors.text.tertiary} />
              </View>
              <Text style={{ fontFamily: font.bold, fontSize: 16, color: colors.text.primary, textAlign: 'center' }}>
                No matching tools found
              </Text>
              <Text style={{ fontFamily: font.regular, fontSize: 13, color: colors.text.tertiary, textAlign: 'center', marginTop: 4 }}>
                Try searching for &quot;merge&quot;, &quot;word&quot;, &quot;compress&quot;, or &quot;scan&quot;
              </Text>
              <Pressable
                style={{
                  marginTop: 18,
                  backgroundColor: colors.bg.card,
                  paddingHorizontal: 18,
                  paddingVertical: 10,
                  borderRadius: radius.pill,
                  borderWidth: 1,
                  borderColor: colors.border.medium,
                }}
                onPress={() => {
                  setSearchQuery('');
                  setSelectedCategory('all');
                  Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
                }}
              >
                <Text style={{ fontFamily: font.semibold, fontSize: 13, color: colors.accent.primary }}>
                  Reset Filters
                </Text>
              </Pressable>
            </View>
          ) : (
            <View style={s.toolGrid}>
              {filteredTools.map((tool, i) => (
                <Animated.View key={tool.id} entering={FadeInDown.delay(i * 30)}>
                  <Pressable
                    style={s.toolCard}
                    onPress={() => {
                      setActiveTool(tool.id);
                      Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
                    }}
                    accessibilityLabel={`${tool.label}: ${tool.desc}`}
                    accessibilityRole="button"
                  >
                    <View style={[s.toolIconWrap, { backgroundColor: `${tool.color}18` }]}>
                      {tool.iconLib === 'mci' ? (
                        <MaterialCommunityIcons name={tool.icon as any} size={24} color={tool.color} />
                      ) : (
                        <Ionicons name={tool.icon as any} size={24} color={tool.color} />
                      )}
                    </View>
                    <Text style={s.toolLabel} numberOfLines={1}>{tool.label}</Text>
                    <Text style={s.toolDesc} numberOfLines={1}>{tool.desc}</Text>
                  </Pressable>
                </Animated.View>
              ))}
            </View>
          )}

          {recentPdfs.length > 0 && (
            <View style={s.recentSection}>
              <Text style={s.sectionTitle}>Recent Documents</Text>
              {recentPdfs.map((pdf) => (
                <Pressable
                  key={pdf.path}
                  style={s.recentItem}
                  onPress={() => { setActiveTool(pdf.tool); Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light); }}
                >
                  <Ionicons name="time-outline" size={20} color={colors.text.tertiary} />
                  <View style={s.recentInfo}>
                    <Text style={s.recentName} numberOfLines={1}>{pdf.name}</Text>
                    <Text style={s.recentMeta}>{new Date(pdf.date).toLocaleDateString()} • {pdf.pages} pages</Text>
                  </View>
                </Pressable>
              ))}
            </View>
          )}
        </ScrollView>
      </View>
    </SafeAreaView>
  );
}
