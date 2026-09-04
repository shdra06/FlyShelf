import React, { useState, useMemo, useRef } from 'react';
import { View, Text, StyleSheet, Pressable, TextInput, ScrollView, Dimensions, Share } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import * as Haptics from 'expo-haptics';
import * as Clipboard from 'expo-clipboard';
import QRCode from 'react-native-qrcode-svg';
import Animated, { FadeInDown } from 'react-native-reanimated';

import { useAppTheme } from '../../hooks/useAppTheme';
import { font, space, radius } from '../../styles/theme';

const { width } = Dimensions.get('window');

interface QrGeneratorToolProps {
  onBack: () => void;
}

type TabMode = 'Text' | 'URL' | 'WiFi' | 'Contact';

const PRESET_COLORS = ['#000000', '#1E3A8A', '#064E3B', '#7F1D1D', '#581C87', '#0F172A'];

export default function QrGeneratorTool({ onBack }: QrGeneratorToolProps) {
  const { colors, shadows } = useAppTheme();
  const s = useMemo(() => createStyles(colors, shadows), [colors, shadows]);

  const [mode, setMode] = useState<TabMode>('Text');
  const [color, setColor] = useState(PRESET_COLORS[0]);

  // Form states
  const [textVal, setTextVal] = useState('');
  const [urlVal, setUrlVal] = useState('');
  
  // WiFi states
  const [wifiSsid, setWifiSsid] = useState('');
  const [wifiPass, setWifiPass] = useState('');
  const [wifiSec, setWifiSec] = useState<'WPA' | 'WEP' | 'nopass'>('WPA');

  // Contact states
  const [contactName, setContactName] = useState('');
  const [contactPhone, setContactPhone] = useState('');
  const [contactEmail, setContactEmail] = useState('');

  const qrRef = useRef<any>(null);

  const getQrValue = () => {
    switch (mode) {
      case 'Text': return textVal || ' ';
      case 'URL': return urlVal ? (urlVal.startsWith('http') ? urlVal : `https://${urlVal}`) : ' ';
      case 'WiFi': return wifiSsid ? `WIFI:T:${wifiSec};S:${wifiSsid};P:${wifiPass};;` : ' ';
      case 'Contact': return contactName ? `BEGIN:VCARD\nVERSION:3.0\nN:${contactName}\nTEL:${contactPhone}\nEMAIL:${contactEmail}\nEND:VCARD` : ' ';
      default: return ' ';
    }
  };

  const handleShare = async () => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);
    if (qrRef.current) {
      qrRef.current.toDataURL((data: string) => {
        const shareOptions = {
          title: 'Share QR Code',
          url: `data:image/png;base64,${data}`,
          message: 'Here is my QR Code',
        };
        Share.share(shareOptions).catch(err => console.error(err));
      });
    }
  };

  const handleCopy = async () => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
    await Clipboard.setStringAsync(getQrValue());
    Haptics.notificationAsync(Haptics.NotificationFeedbackType.Success);
  };

  const handleModeChange = (m: TabMode) => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
    setMode(m);
  };

  const TABS: TabMode[] = ['Text', 'URL', 'WiFi', 'Contact'];

  const qrValue = getQrValue();
  const hasValue = qrValue.trim().length > 0;

  return (
    <View style={s.container}>
      <View style={s.header}>
        <Pressable style={s.iconButton} onPress={() => { Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light); onBack(); }}>
          <Ionicons name="arrow-back" size={24} color={colors.text.primary} />
        </Pressable>
        <Text style={s.title}>QR Generator</Text>
        <View style={{ width: 44 }} />
      </View>

      <ScrollView style={s.scrollView} contentContainerStyle={s.scrollContent} keyboardShouldPersistTaps="handled">
        {/* Tabs */}
        <ScrollView horizontal showsHorizontalScrollIndicator={false} style={s.tabsContainer}>
          {TABS.map((t) => (
            <Pressable 
              key={t} 
              style={[s.tabPill, mode === t && s.tabPillActive]} 
              onPress={() => handleModeChange(t)}
            >
              <Text style={[s.tabText, mode === t && s.tabTextActive]}>{t}</Text>
            </Pressable>
          ))}
        </ScrollView>

        {/* Input Area */}
        <Animated.View style={s.inputContainer} entering={FadeInDown.duration(300)}>
          {mode === 'Text' && (
            <TextInput
              style={[s.input, s.inputMulti]}
              placeholder="Enter text here..."
              placeholderTextColor={colors.text.tertiary}
              value={textVal}
              onChangeText={setTextVal}
              multiline
              textAlignVertical="top"
            />
          )}

          {mode === 'URL' && (
            <TextInput
              style={s.input}
              placeholder="example.com"
              placeholderTextColor={colors.text.tertiary}
              value={urlVal}
              onChangeText={setUrlVal}
              keyboardType="url"
              autoCapitalize="none"
              autoCorrect={false}
            />
          )}

          {mode === 'WiFi' && (
            <View style={s.wifiForm}>
              <TextInput
                style={s.input}
                placeholder="Network Name (SSID)"
                placeholderTextColor={colors.text.tertiary}
                value={wifiSsid}
                onChangeText={setWifiSsid}
              />
              <TextInput
                style={s.input}
                placeholder="Password"
                placeholderTextColor={colors.text.tertiary}
                value={wifiPass}
                onChangeText={setWifiPass}
                secureTextEntry
              />
              <View style={s.secContainer}>
                {['WPA', 'WEP', 'nopass'].map(sec => (
                  <Pressable 
                    key={sec} 
                    style={[s.secBtn, wifiSec === sec && s.secBtnActive]}
                    onPress={() => {
                      Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
                      setWifiSec(sec as any);
                    }}
                  >
                    <Text style={[s.secText, wifiSec === sec && s.secTextActive]}>
                      {sec === 'nopass' ? 'None' : sec}
                    </Text>
                  </Pressable>
                ))}
              </View>
            </View>
          )}

          {mode === 'Contact' && (
            <View style={s.contactForm}>
              <TextInput
                style={s.input}
                placeholder="Full Name"
                placeholderTextColor={colors.text.tertiary}
                value={contactName}
                onChangeText={setContactName}
              />
              <TextInput
                style={s.input}
                placeholder="Phone Number"
                placeholderTextColor={colors.text.tertiary}
                value={contactPhone}
                onChangeText={setContactPhone}
                keyboardType="phone-pad"
              />
              <TextInput
                style={s.input}
                placeholder="Email Address"
                placeholderTextColor={colors.text.tertiary}
                value={contactEmail}
                onChangeText={setContactEmail}
                keyboardType="email-address"
                autoCapitalize="none"
              />
            </View>
          )}
        </Animated.View>

        {/* QR Preview Area */}
        <View style={s.previewContainer}>
          <View style={s.qrBox}>
            {hasValue ? (
              <QRCode
                value={qrValue}
                size={220}
                color={color}
                backgroundColor="#FFFFFF"
                getRef={(c) => (qrRef.current = c)}
              />
            ) : (
              <View style={s.qrPlaceholder}>
                <Ionicons name="qr-code-outline" size={64} color={colors.text.tertiary} />
                <Text style={s.qrPlaceholderText}>Enter data to generate</Text>
              </View>
            )}
          </View>
          
          <View style={s.colorsRow}>
            {PRESET_COLORS.map(c => (
              <Pressable
                key={c}
                style={[s.colorCircle, { backgroundColor: c }, color === c && s.colorCircleActive]}
                onPress={() => {
                  Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
                  setColor(c);
                }}
              />
            ))}
          </View>

          <View style={s.actionRow}>
            <Pressable style={s.actionBtn} onPress={handleCopy} disabled={!hasValue}>
              <Ionicons name="copy-outline" size={20} color={hasValue ? colors.accent.primary : colors.text.disabled} />
              <Text style={[s.actionText, !hasValue && s.actionTextDisabled]}>Copy Data</Text>
            </Pressable>
            
            <Pressable style={[s.actionBtn, s.actionBtnPrimary]} onPress={handleShare} disabled={!hasValue}>
              <Ionicons name="share-outline" size={20} color={hasValue ? '#FFF' : colors.text.disabled} />
              <Text style={[s.actionText, s.actionTextPrimary, !hasValue && s.actionTextDisabled]}>Share QR</Text>
            </Pressable>
          </View>
        </View>
      </ScrollView>
    </View>
  );
}

function createStyles(colors: any, shadows: any) {
  return StyleSheet.create({
    container: {
      flex: 1,
      backgroundColor: colors.bg.base,
    },
    header: {
      flexDirection: 'row',
      alignItems: 'center',
      justifyContent: 'space-between',
      paddingTop: 60,
      paddingHorizontal: space.lg,
      paddingBottom: space.md,
      backgroundColor: colors.bg.base,
      zIndex: 10,
    },
    iconButton: {
      width: 44,
      height: 44,
      borderRadius: 22,
      justifyContent: 'center',
      alignItems: 'center',
    },
    title: {
      color: colors.text.primary,
      fontSize: 18,
      fontWeight: '600',
    },
    scrollView: {
      flex: 1,
    },
    scrollContent: {
      paddingBottom: 40,
    },
    tabsContainer: {
      paddingHorizontal: space.lg,
      marginBottom: space.lg,
      flexGrow: 0,
    },
    tabPill: {
      paddingVertical: 8,
      paddingHorizontal: 20,
      borderRadius: radius.pill,
      backgroundColor: colors.bg.card,
      marginRight: space.sm,
      borderWidth: 1,
      borderColor: colors.border.subtle,
    },
    tabPillActive: {
      backgroundColor: colors.accent.primaryDim,
      borderColor: colors.accent.primary,
    },
    tabText: {
      color: colors.text.secondary,
      fontWeight: '500',
    },
    tabTextActive: {
      color: colors.accent.primary,
      fontWeight: '600',
    },
    inputContainer: {
      paddingHorizontal: space.lg,
      marginBottom: space.xl,
    },
    input: {
      backgroundColor: colors.bg.input,
      borderRadius: radius.md,
      padding: space.lg,
      color: colors.text.primary,
      fontSize: 16,
      borderWidth: 1,
      borderColor: colors.border.subtle,
      marginBottom: space.md,
    },
    inputMulti: {
      height: 120,
    },
    wifiForm: {
      gap: space.xs,
    },
    contactForm: {
      gap: space.xs,
    },
    secContainer: {
      flexDirection: 'row',
      gap: space.sm,
      marginTop: space.xs,
    },
    secBtn: {
      flex: 1,
      paddingVertical: space.sm,
      alignItems: 'center',
      backgroundColor: colors.bg.card,
      borderRadius: radius.sm,
      borderWidth: 1,
      borderColor: colors.border.subtle,
    },
    secBtnActive: {
      backgroundColor: colors.accent.primaryDim,
      borderColor: colors.accent.primary,
    },
    secText: {
      color: colors.text.secondary,
      fontSize: 14,
      fontWeight: '500',
    },
    secTextActive: {
      color: colors.accent.primary,
    },
    previewContainer: {
      backgroundColor: colors.bg.card,
      marginHorizontal: space.lg,
      borderRadius: radius.xl,
      padding: space.xl,
      alignItems: 'center',
      borderWidth: 1,
      borderColor: colors.border.subtle,
      ...shadows?.md,
    },
    qrBox: {
      width: 250,
      height: 250,
      backgroundColor: '#FFF',
      borderRadius: radius.lg,
      justifyContent: 'center',
      alignItems: 'center',
      marginBottom: space.xl,
      overflow: 'hidden',
    },
    qrPlaceholder: {
      alignItems: 'center',
      justifyContent: 'center',
    },
    qrPlaceholderText: {
      color: colors.text.tertiary,
      marginTop: space.sm,
      fontSize: 14,
    },
    colorsRow: {
      flexDirection: 'row',
      gap: space.md,
      marginBottom: space.xl,
    },
    colorCircle: {
      width: 32,
      height: 32,
      borderRadius: 16,
      borderWidth: 2,
      borderColor: 'transparent',
    },
    colorCircleActive: {
      borderColor: colors.accent.primary,
    },
    actionRow: {
      flexDirection: 'row',
      width: '100%',
      gap: space.md,
    },
    actionBtn: {
      flex: 1,
      flexDirection: 'row',
      alignItems: 'center',
      justifyContent: 'center',
      gap: space.sm,
      paddingVertical: space.md,
      backgroundColor: colors.bg.elevated,
      borderRadius: radius.lg,
      borderWidth: 1,
      borderColor: colors.border.subtle,
    },
    actionBtnPrimary: {
      backgroundColor: colors.accent.primary,
      borderColor: colors.accent.primary,
    },
    actionText: {
      color: colors.accent.primary,
      fontWeight: '600',
      fontSize: 14,
    },
    actionTextPrimary: {
      color: '#FFF',
    },
    actionTextDisabled: {
      color: colors.text.disabled,
    },
  });
}
