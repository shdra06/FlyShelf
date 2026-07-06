// PDF Tools — Modularized Suite for FlyShelf Android
import { useState, useEffect } from 'react';
import {
  View, Text, ScrollView, Pressable, SafeAreaView, Alert,
} from 'react-native';
import { Ionicons, MaterialCommunityIcons } from '@expo/vector-icons';
import * as Haptics from 'expo-haptics';
import * as DocumentPicker from 'expo-document-picker';
import * as ImagePicker from 'expo-image-picker';
import AsyncStorage from '@react-native-async-storage/async-storage';
import Animated, { FadeInDown } from 'react-native-reanimated';
import { colors } from '../styles/theme';
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

const RECENT_KEY = '@flyshelf_recent_pdfs';

const TOOLS: { id: ToolId; icon: string; iconLib: 'ion' | 'mci'; color: string; label: string; desc: string }[] = [
  { id: 'merge', icon: 'git-merge-outline', iconLib: 'ion', color: colors.accent.primary, label: 'Merge', desc: 'Combine PDFs' },
  { id: 'split', icon: 'call-split', iconLib: 'mci', color: colors.type.url, label: 'Split', desc: 'Split by range' },
  { id: 'editPages', icon: 'document-text-outline', iconLib: 'ion', color: colors.accent.success, label: 'Edit Pages', desc: 'Reorder & edit' },
  { id: 'imagesToPdf', icon: 'images-outline', iconLib: 'ion', color: colors.type.image, label: 'Images→PDF', desc: 'Convert images' },
  { id: 'extract', icon: 'cut-outline', iconLib: 'ion', color: colors.accent.warning, label: 'Extract', desc: 'Pick pages' },
  { id: 'watermark', icon: 'water-outline', iconLib: 'ion', color: colors.type.doc, label: 'Watermark', desc: 'Add text overlay' },
  { id: 'password', icon: 'lock-closed-outline', iconLib: 'ion', color: colors.accent.error, label: 'Password', desc: 'Protect PDF' },
  { id: 'metadata', icon: 'information-circle-outline', iconLib: 'ion', color: colors.type.ppt, label: 'Metadata', desc: 'Edit info' },
  { id: 'info', icon: 'analytics-outline', iconLib: 'ion', color: colors.text.secondary, label: 'PDF Info', desc: 'View details' },
];

export default function PdfToolsScreen() {
  const [activeTool, setActiveTool] = useState<ToolId | null>(null);
  const [recentPdfs, setRecentPdfs] = useState<RecentPdf[]>([]);

  useEffect(() => {
    AsyncStorage.getItem(RECENT_KEY).then(data => {
      if (data) {
        try { setRecentPdfs(JSON.parse(data)); } catch { /* corrupted data, ignore */ }
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

  const renderTool = () => {
    const back = () => setActiveTool(null);
    switch (activeTool) {
      case 'merge': return <MergeTool onBack={back} onPickFiles={() => pickPdf(true)} saveRecent={saveRecent} />;
      case 'split': return <SplitTool onBack={back} onPickFile={() => pickPdf(false)} saveRecent={saveRecent} />;
      case 'editPages': return <EditPagesTool onBack={back} onPickFile={() => pickPdf(false)} onPickImages={pickImages} saveRecent={saveRecent} />;
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
        <Text style={s.headerTitle}>PDF Tools</Text>
        <ScrollView contentContainerStyle={s.scrollContent} showsVerticalScrollIndicator={false}>
          <View style={s.toolGrid}>
            {TOOLS.map((tool, i) => (
              <Animated.View key={tool.id} entering={FadeInDown.delay(i * 50)}>
                <Pressable
                  style={s.toolCard}
                  onPress={() => { setActiveTool(tool.id); Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light); }}
                  accessibilityLabel={`${tool.label}: ${tool.desc}`}
                  accessibilityRole="button"
                >
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
              <Text style={s.sectionTitle}>Recent</Text>
              {recentPdfs.map((pdf, i) => (
                <Pressable key={pdf.path} style={s.recentItem} onPress={() => { setActiveTool(pdf.tool); Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light); }}>
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
