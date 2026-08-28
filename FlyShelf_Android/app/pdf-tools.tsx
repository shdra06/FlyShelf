// PDF Tools — Modularized Suite for FlyShelf Android
import { useState, useEffect, useMemo } from 'react';
import {
  View, Text, ScrollView, Pressable, SafeAreaView, Alert, TextInput,
} from 'react-native';
import { Ionicons, MaterialCommunityIcons } from '@expo/vector-icons';
import * as Haptics from 'expo-haptics';
import * as DocumentPicker from 'expo-document-picker';
import * as ImagePicker from 'expo-image-picker';
import AsyncStorage from '@react-native-async-storage/async-storage';
import Animated, { FadeInDown } from 'react-native-reanimated';
import { colors, space, radius } from '../styles/theme';
import s from '../styles/pdfToolsStyles';

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

const RECENT_KEY = '@flyshelf_recent_pdfs';

type CategoryId = 'all' | 'conversions' | 'edit' | 'security';

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

const TOOLS: ToolDef[] = [
  { id: 'pdfToWord', icon: 'file-word-box', iconLib: 'mci', color: colors.type.doc, label: 'PDF → Word', desc: 'DOCX with tables', category: 'conversions', badge: 'NEW' },
  { id: 'compress', icon: 'flash-outline', iconLib: 'ion', color: '#10B981', label: 'Compress', desc: 'Reduce file size', category: 'conversions', badge: 'HOT' },
  { id: 'imagesToPdf', icon: 'images-outline', iconLib: 'ion', color: colors.type.image, label: 'Images→PDF', desc: 'Convert photos', category: 'conversions' },
  { id: 'merge', icon: 'git-merge-outline', iconLib: 'ion', color: colors.accent.primary, label: 'Merge', desc: 'Combine PDFs', category: 'edit' },
  { id: 'split', icon: 'call-split', iconLib: 'mci', color: colors.type.url, label: 'Split', desc: 'Split by range', category: 'edit' },
  { id: 'editPages', icon: 'document-text-outline', iconLib: 'ion', color: colors.accent.success, label: 'Edit Pages', desc: 'Reorder & rotate', category: 'edit' },
  { id: 'extract', icon: 'cut-outline', iconLib: 'ion', color: colors.accent.warning, label: 'Extract', desc: 'Pick single pages', category: 'edit' },
  { id: 'watermark', icon: 'water-outline', iconLib: 'ion', color: '#8B5CF6', label: 'Watermark', desc: 'Add text stamp', category: 'security' },
  { id: 'password', icon: 'lock-closed-outline', iconLib: 'ion', color: colors.accent.error, label: 'Password', desc: 'Protect document', category: 'security' },
  { id: 'metadata', icon: 'information-circle-outline', iconLib: 'ion', color: colors.type.ppt, label: 'Metadata', desc: 'Edit title & author', category: 'security' },
  { id: 'info', icon: 'analytics-outline', iconLib: 'ion', color: colors.text.secondary, label: 'PDF Info', desc: 'Inspect properties', category: 'security' },
];

const CATEGORIES: { id: CategoryId; label: string; icon: string }[] = [
  { id: 'all', label: 'All', icon: 'apps-outline' },
  { id: 'conversions', label: 'Convert', icon: 'swap-horizontal-outline' },
  { id: 'edit', label: 'Edit & Pages', icon: 'create-outline' },
  { id: 'security', label: 'Security & Info', icon: 'shield-checkmark-outline' },
];

export default function PdfToolsScreen() {
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

  const renderTool = () => {
    const back = () => setActiveTool(null);
    switch (activeTool) {
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
        <View style={{ paddingHorizontal: space.xl, paddingTop: 52, paddingBottom: space.sm }}>
          <Text style={s.headerTitle}>PDF Power Tools</Text>
          <Text style={{ color: colors.text.tertiary, fontSize: 13, marginTop: 2 }}>
            High-performance local &amp; cross-device tools
          </Text>

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
          <View style={s.toolGrid}>
            {filteredTools.map((tool, i) => (
              <Animated.View key={tool.id} entering={FadeInDown.delay(i * 35)}>
                <Pressable
                  style={[s.toolCard, { position: 'relative', overflow: 'hidden' }]}
                  onPress={() => { setActiveTool(tool.id); Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light); }}
                  accessibilityLabel={`${tool.label}: ${tool.desc}`}
                  accessibilityRole="button"
                >
                  {tool.badge && (
                    <View style={{
                      position: 'absolute',
                      top: 6,
                      right: 6,
                      backgroundColor: tool.badge === 'NEW' ? '#06B6D4' : '#EF4444',
                      paddingHorizontal: 5,
                      paddingVertical: 1.5,
                      borderRadius: 4,
                    }}>
                      <Text style={{ color: '#fff', fontSize: 8, fontWeight: '800' }}>{tool.badge}</Text>
                    </View>
                  )}
                  <View style={[s.toolIconWrap, { backgroundColor: tool.color + '20' }]}>
                    {tool.iconLib === 'mci' ? 
                      <MaterialCommunityIcons name={tool.icon as any} size={28} color={tool.color} /> :
                      <Ionicons name={tool.icon as any} size={28} color={tool.color} />
                    }
                  </View>
                  <Text style={s.toolLabel}>{tool.label}</Text>
                  <Text style={s.toolDesc}>{tool.desc}</Text>
                </Pressable>
              </Animated.View>
            ))}
          </View>

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
