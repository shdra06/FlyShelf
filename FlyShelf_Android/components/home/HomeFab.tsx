import React, { useState, useEffect } from 'react';
import { View, Text, Pressable, Modal, StyleSheet, TouchableOpacity } from 'react-native';
import Animated, { 
  useSharedValue, 
  useAnimatedStyle, 
  withSpring,
  withTiming,
} from 'react-native-reanimated';
import { Ionicons } from '@expo/vector-icons';
import * as Haptics from 'expo-haptics';
import { useAppTheme } from '../../hooks/useAppTheme';
import createHomeStyles from '../../styles/homeStyles';

interface HomeFabProps {
  onSendText: () => void;
  onCamera: () => void;
  onSendPhoto: () => void;
  onSendFile: () => void;
  onScanQr: () => void;
}

const AnimatedPressable = Animated.createAnimatedComponent(Pressable);

export default function HomeFab({
  onSendText,
  onCamera,
  onSendPhoto,
  onSendFile,
  onScanQr,
}: HomeFabProps) {
  const { colors, shadows, spring } = useAppTheme();
  const styles = createHomeStyles(colors, shadows);
  const [isOpen, setIsOpen] = useState(false);
  const rotation = useSharedValue(0);
  const sheetTranslateY = useSharedValue(400);

  useEffect(() => {
    if (isOpen) {
      rotation.value = withSpring(45, spring.bounce);
      sheetTranslateY.value = withSpring(0, spring.bounce);
    } else {
      rotation.value = withSpring(0, spring.bounce);
      sheetTranslateY.value = withTiming(400);
    }
  }, [isOpen]);

  const animatedFabStyle = useAnimatedStyle(() => {
    return {
      transform: [{ rotate: `${rotation.value}deg` }],
    };
  });

  const animatedSheetStyle = useAnimatedStyle(() => {
    return {
      transform: [{ translateY: sheetTranslateY.value }],
    };
  });

  const handleToggle = () => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
    setIsOpen(!isOpen);
  };

  const handleAction = (action?: () => void) => {
    setIsOpen(false);
    if (typeof action === 'function') {
      setTimeout(() => {
        try {
          action();
        } catch (err) {
          console.error('HomeFab action execution error:', err);
        }
      }, 250);
    }
  };

  const Option = ({ icon, label, desc, color, onPress }: any) => (
    <TouchableOpacity 
      style={styles.fabOption} 
      onPress={() => handleAction(onPress)}
      activeOpacity={0.7}
      accessibilityLabel={`${label}: ${desc}`}
      accessibilityRole="button"
    >
      <View style={[styles.fabOptionIcon, { backgroundColor: `${color}18` }]}>
        <Ionicons name={icon} size={22} color={color} />
      </View>
      <View style={{ flex: 1 }}>
        <Text style={styles.fabOptionLabel}>{label}</Text>
        <Text style={styles.fabOptionDesc}>{desc}</Text>
      </View>
      <Ionicons name="chevron-forward" size={16} color={colors.text.tertiary} style={{ opacity: 0.5 }} />
    </TouchableOpacity>
  );

  return (
    <>
      <AnimatedPressable 
        style={[styles.fab, animatedFabStyle]} 
        onPress={handleToggle}
        accessibilityLabel="Quick action menu"
        accessibilityRole="button"
      >
        <Ionicons name="add" size={32} color="#fff" />
      </AnimatedPressable>

      <Modal
        visible={isOpen}
        transparent
        animationType="fade"
        onRequestClose={() => setIsOpen(false)}
      >
        <Pressable 
          style={StyleSheet.absoluteFill} 
          onPress={() => setIsOpen(false)}
        >
          <View style={{ flex: 1, backgroundColor: 'rgba(0,0,0,0.6)' }} />
        </Pressable>
        
        <View style={{ position: 'absolute', bottom: 0, left: 0, right: 0 }}>
          <Animated.View style={[styles.fabSheet, animatedSheetStyle]}>
            <View style={styles.fabHandle} />

            <View style={styles.fabSheetHeader}>
              <Text style={styles.fabSheetTitle}>Quick Actions</Text>
              <Text style={styles.fabSheetSubtitle}>Share instantly with your connected PC</Text>
            </View>
            
            <Option 
              icon="create-outline" label="Send Text" 
              desc="Type and send to your PC" color={colors.accent.primary} 
              onPress={onSendText} 
            />
            <Option 
              icon="camera-outline" label="Camera" 
              desc="Take a photo to send" color={colors.accent.warning} 
              onPress={onCamera} 
            />
            <Option 
              icon="image-outline" label="Send Photo" 
              desc="Pick from gallery" color={colors.type.image} 
              onPress={onSendPhoto} 
            />
            <Option 
              icon="attach-outline" label="Send File" 
              desc="Pick any file" color={colors.accent.success} 
              onPress={onSendFile} 
            />
            <Option 
              icon="qr-code-outline" label="Scan QR" 
              desc="Pair with a new device" color={colors.accent.info} 
              onPress={onScanQr} 
            />
          </Animated.View>
        </View>
      </Modal>
    </>
  );
}
