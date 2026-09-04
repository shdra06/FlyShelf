// Tools Hub — Unified Toolbox Dashboard
// Provides categorized access to all productivity tools
import React, { useState, useMemo, useCallback } from 'react';
import {
  View, Text, Pressable, ScrollView, TextInput,
  SafeAreaView, StyleSheet, Platform, StatusBar,
} from 'react-native';
import { Ionicons, MaterialCommunityIcons } from '@expo/vector-icons';
import * as Haptics from 'expo-haptics';
import Animated, { FadeInDown } from 'react-native-reanimated';
import { router } from 'expo-router';
import { useAppTheme } from '../hooks/useAppTheme';
import { font, space, radius } from '../styles/theme';

// Tool components
import QrScannerTool from '../components/tools/QrScannerTool';
import QrGeneratorTool from '../components/tools/QrGeneratorTool';
import TextToolsSuite from '../components/tools/TextToolsSuite';
import UnitConverterTool from '../components/tools/UnitConverterTool';
import ColorPickerTool from '../components/tools/ColorPickerTool';
import ImageCompressTool from '../components/tools/ImageCompressTool';
import ImageResizeTool from '../components/tools/ImageResizeTool';
import ImageInfoTool from '../components/tools/ImageInfoTool';
import ImageFormatTool from '../components/tools/ImageFormatTool';

// ═══════════════════════════════════════════
// TOOL DEFINITIONS
// ═══════════════════════════════════════════

type ToolId =
  | 'pdfSuite' | 'qrScanner' | 'qrGenerator'
  | 'textTools' | 'unitConverter' | 'colorPicker'
  | 'imageCompress' | 'imageResize' | 'imageInfo' | 'imageFormat'
  | null;

interface ToolDef {
  id: Exclude<ToolId, null>;
  label: string;
  desc: string;
  icon: string;
  iconLib?: 'ion' | 'mci';
  color: string;
  category: string;
}

const TOOLS: ToolDef[] = [
  // Documents
  { id: 'pdfSuite', label: 'PDF Suite', desc: '12+ PDF tools', icon: 'file-document-outline', iconLib: 'mci', color: '#F43F5E', category: 'documents' },
  // QR & Barcode
  { id: 'qrScanner', label: 'QR Scanner', desc: 'Scan codes', icon: 'qr-code-outline', color: '#10B981', category: 'qr' },
  { id: 'qrGenerator', label: 'QR Generator', desc: 'Create codes', icon: 'qr-code', color: '#06B6D4', category: 'qr' },
  // Text
  { id: 'textTools', label: 'Text Tools', desc: '9 text utilities', icon: 'text-outline', color: '#8B5CF6', category: 'text' },
  // Conversion
  { id: 'unitConverter', label: 'Unit Converter', desc: '10 categories', icon: 'swap-horizontal-outline', color: '#F59E0B', category: 'convert' },
  // Image
  { id: 'imageCompress', label: 'Compress Image', desc: 'Reduce size', icon: 'images-outline', color: '#EC4899', category: 'image' },
  { id: 'imageResize', label: 'Resize Image', desc: 'Change dimensions', icon: 'resize-outline', color: '#14B8A6', category: 'image' },
  { id: 'imageFormat', label: 'Convert Format', desc: 'JPG, PNG, WebP', icon: 'image-outline', color: '#6366F1', category: 'image' },
  { id: 'imageInfo', label: 'Image Info', desc: 'EXIF & details', icon: 'information-circle-outline', color: '#64748B', category: 'image' },
  // Color
  { id: 'colorPicker', label: 'Color Picker', desc: 'HEX, RGB, HSL', icon: 'color-palette-outline', color: '#E11D48', category: 'color' },
];

const CATEGORIES = [
  { id: 'all', label: 'All', icon: 'apps-outline' },
  { id: 'documents', label: 'Documents', icon: 'document-text-outline' },
  { id: 'qr', label: 'QR Code', icon: 'qr-code-outline' },
  { id: 'text', label: 'Text', icon: 'text-outline' },
  { id: 'image', label: 'Image', icon: 'images-outline' },
  { id: 'convert', label: 'Convert', icon: 'swap-horizontal-outline' },
  { id: 'color', label: 'Color', icon: 'color-palette-outline' },
];

const SBH = Platform.OS === 'android' ? (StatusBar.currentHeight || 24) : 44;

// ═══════════════════════════════════════════
// MAIN COMPONENT
// ═══════════════════════════════════════════

export default function ToolsScreen() {
  const { colors, shadows } = useAppTheme();
  const [activeTool, setActiveTool] = useState<ToolId>(null);
  const [selectedCategory, setSelectedCategory] = useState('all');
  const [searchQuery, setSearchQuery] = useState('');

  const filteredTools = useMemo(() => {
    let tools = TOOLS;
    if (selectedCategory !== 'all') {
      tools = tools.filter(t => t.category === selectedCategory);
    }
    if (searchQuery.trim()) {
      const q = searchQuery.toLowerCase().trim();
      tools = tools.filter(t =>
        t.label.toLowerCase().includes(q) ||
        t.desc.toLowerCase().includes(q) ||
        t.category.toLowerCase().includes(q)
      );
    }
    return tools;
  }, [selectedCategory, searchQuery]);

  const handleToolPress = useCallback((toolId: Exclude<ToolId, null>) => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
    if (toolId === 'pdfSuite') {
      router.push('/pdf-tools');
      return;
    }
    setActiveTool(toolId);
  }, []);

  const back = useCallback(() => {
    setActiveTool(null);
  }, []);

  // Render active tool
  if (activeTool) {
    switch (activeTool) {
      case 'qrScanner': return <QrScannerTool onBack={back} />;
      case 'qrGenerator': return <QrGeneratorTool onBack={back} />;
      case 'textTools': return <TextToolsSuite onBack={back} />;
      case 'unitConverter': return <UnitConverterTool onBack={back} />;
      case 'colorPicker': return <ColorPickerTool onBack={back} />;
      case 'imageCompress': return <ImageCompressTool onBack={back} />;
      case 'imageResize': return <ImageResizeTool onBack={back} />;
      case 'imageInfo': return <ImageInfoTool onBack={back} />;
      case 'imageFormat': return <ImageFormatTool onBack={back} />;
      default: return null;
    }
  }

  const s = useMemo(() => StyleSheet.create({
    safe: { flex: 1, backgroundColor: colors.bg.base },
    container: { flex: 1 },
    header: {
      flexDirection: 'row', alignItems: 'center',
      paddingHorizontal: space.xl, paddingTop: SBH + 8, paddingBottom: space.lg,
      backgroundColor: colors.bg.base,
    },
    backBtn: { padding: space.sm, marginRight: space.sm, borderRadius: radius.sm },
    headerTitle: { fontFamily: font.bold, fontSize: 24, color: colors.text.primary, flex: 1, letterSpacing: -0.5 },
    searchWrap: {
      paddingHorizontal: space.xl, paddingBottom: space.md,
    },
    searchBar: {
      flexDirection: 'row', alignItems: 'center',
      backgroundColor: colors.bg.card, borderRadius: radius.lg,
      paddingHorizontal: space.md, height: 44,
      borderWidth: 1, borderColor: colors.border.subtle,
    },
    searchInput: {
      flex: 1, fontFamily: font.medium, fontSize: 14,
      color: colors.text.primary, marginLeft: space.sm,
      paddingVertical: 0,
    },
    catRow: {
      paddingHorizontal: space.xl, paddingBottom: space.md,
    },
    catScroll: { gap: space.sm },
    catChip: {
      flexDirection: 'row', alignItems: 'center', gap: 4,
      paddingHorizontal: 14, paddingVertical: 8,
      borderRadius: radius.pill, borderWidth: 1,
      borderColor: colors.border.subtle, backgroundColor: colors.bg.card,
    },
    catChipActive: {
      backgroundColor: colors.accent.primary, borderColor: colors.accent.primary,
    },
    catLabel: { fontFamily: font.medium, fontSize: 12, color: colors.text.secondary },
    catLabelActive: { color: '#FFFFFF', fontFamily: font.bold },
    scrollContent: { padding: space.xl, paddingTop: 0, paddingBottom: 40 },
    toolGrid: { flexDirection: 'row', flexWrap: 'wrap', gap: 12 },
    toolCard: {
      width: '47%' as any,
      paddingVertical: 18, paddingHorizontal: 14,
      backgroundColor: colors.bg.card, borderRadius: 20,
      borderWidth: 1, borderColor: colors.border.subtle,
      alignItems: 'center', justifyContent: 'center', minHeight: 130,
      ...shadows.card,
    },
    toolIconWrap: {
      width: 48, height: 48, borderRadius: 16,
      alignItems: 'center', justifyContent: 'center', marginBottom: 10,
    },
    toolLabel: { fontFamily: font.bold, fontSize: 14, color: colors.text.primary, textAlign: 'center', letterSpacing: -0.2 },
    toolDesc: { fontFamily: font.medium, fontSize: 11, color: colors.text.tertiary, textAlign: 'center', marginTop: 3 },
    emptyWrap: { alignItems: 'center', paddingVertical: 48 },
    emptyText: { fontFamily: font.bold, fontSize: 16, color: colors.text.primary, marginTop: 12 },
    emptyHint: { fontFamily: font.regular, fontSize: 13, color: colors.text.tertiary, marginTop: 4 },
  }), [colors, shadows]);

  return (
    <SafeAreaView style={s.safe}>
      <View style={s.container}>
        {/* Header */}
        <View style={s.header}>
          <Pressable
            onPress={() => { Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light); router.back(); }}
            hitSlop={12} style={s.backBtn}
          >
            <Ionicons name="arrow-back" size={22} color={colors.text.primary} />
          </Pressable>
          <Text style={s.headerTitle}>Toolbox</Text>
        </View>

        {/* Search */}
        <View style={s.searchWrap}>
          <View style={s.searchBar}>
            <Ionicons name="search" size={18} color={colors.text.tertiary} />
            <TextInput
              style={s.searchInput}
              placeholder="Search tools..."
              placeholderTextColor={colors.text.disabled}
              value={searchQuery}
              onChangeText={setSearchQuery}
              returnKeyType="search"
            />
            {searchQuery.length > 0 && (
              <Pressable onPress={() => setSearchQuery('')} hitSlop={8}>
                <Ionicons name="close-circle" size={18} color={colors.text.tertiary} />
              </Pressable>
            )}
          </View>
        </View>

        {/* Category Tabs */}
        <View style={s.catRow}>
          <ScrollView horizontal showsHorizontalScrollIndicator={false} contentContainerStyle={s.catScroll}>
            {CATEGORIES.map(cat => {
              const active = selectedCategory === cat.id;
              return (
                <Pressable
                  key={cat.id}
                  style={[s.catChip, active && s.catChipActive]}
                  onPress={() => {
                    setSelectedCategory(cat.id);
                    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
                  }}
                >
                  <Ionicons name={cat.icon as any} size={14} color={active ? '#FFF' : colors.text.secondary} />
                  <Text style={[s.catLabel, active && s.catLabelActive]}>{cat.label}</Text>
                </Pressable>
              );
            })}
          </ScrollView>
        </View>

        {/* Tools Grid */}
        <ScrollView contentContainerStyle={s.scrollContent} showsVerticalScrollIndicator={false}>
          {filteredTools.length === 0 ? (
            <View style={s.emptyWrap}>
              <Ionicons name="search-outline" size={48} color={colors.text.disabled} />
              <Text style={s.emptyText}>No matching tools</Text>
              <Text style={s.emptyHint}>Try a different search or category</Text>
            </View>
          ) : (
            <View style={s.toolGrid}>
              {filteredTools.map((tool, i) => (
                <Animated.View key={tool.id} entering={FadeInDown.delay(i * 40)}>
                  <Pressable
                    style={s.toolCard}
                    onPress={() => handleToolPress(tool.id)}
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
        </ScrollView>
      </View>
    </SafeAreaView>
  );
}
