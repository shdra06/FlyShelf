/**
 * BottomSheet — Gesture-dismissible modal sheet
 *
 * Features:
 *  - Swipe down to dismiss
 *  - Animated backdrop
 *  - Handle bar at top
 *  - Spring open/close animation
 *  - Configurable max height
 */
import React, { useEffect, useState } from 'react';
import { View, Text, Pressable, StyleSheet, useWindowDimensions, Modal } from 'react-native';
import Animated, {
  useSharedValue,
  useAnimatedStyle,
  withSpring,
  withTiming,
  runOnJS,
} from 'react-native-reanimated';
import { Gesture, GestureDetector } from 'react-native-gesture-handler';
import { colors, radius, space, surface, spring as springConfig } from '../styles/theme';



interface BottomSheetProps {
  visible: boolean;
  onClose: () => void;
  children: React.ReactNode;
  /** Title shown in sheet header */
  title?: string;
  /** Max height as fraction of screen (0-1), default 0.85 */
  maxHeight?: number;
}

export default function BottomSheet({
  visible,
  onClose,
  children,
  title,
  maxHeight = 0.85,
}: BottomSheetProps) {
  const { height: screenH } = useWindowDimensions();
  const sheetHeight = screenH * maxHeight;
  const translateY = useSharedValue(sheetHeight);
  const backdropOpacity = useSharedValue(0);

  // Keep component mounted briefly after visible→false so close animation plays
  const [isRendered, setIsRendered] = useState(visible);
  useEffect(() => {
    if (visible) {
      setIsRendered(true);
    } else {
      const timer = setTimeout(() => setIsRendered(false), 350);
      return () => clearTimeout(timer);
    }
  }, [visible]);

  useEffect(() => {
    if (visible) {
      translateY.value = withSpring(0, springConfig.slow);
      backdropOpacity.value = withTiming(1, { duration: 300 });
    } else {
      translateY.value = withSpring(sheetHeight, springConfig.slow);
      backdropOpacity.value = withTiming(0, { duration: 200 });
    }
  }, [visible]);

  const handleClose = () => {
    translateY.value = withSpring(sheetHeight, springConfig.slow);
    backdropOpacity.value = withTiming(0, { duration: 200 });
    setTimeout(onClose, 300);
  };

  const panGesture = Gesture.Pan()
    .onUpdate((e) => {
      if (e.translationY > 0) {
        translateY.value = e.translationY;
      }
    })
    .onEnd((e) => {
      if (e.translationY > sheetHeight * 0.3 || e.velocityY > 500) {
        runOnJS(handleClose)();
      } else {
        translateY.value = withSpring(0, springConfig.slow);
      }
    });

  const sheetStyle = useAnimatedStyle(() => ({
    transform: [{ translateY: translateY.value }],
  }));

  const backdropStyle = useAnimatedStyle(() => ({
    opacity: backdropOpacity.value,
  }));

  if (!isRendered) return null;

  return (
    <Modal transparent visible={visible} onRequestClose={handleClose} statusBarTranslucent>
      <View style={styles.wrapper}>
        <Animated.View style={[styles.backdrop, backdropStyle]}>
          <Pressable style={StyleSheet.absoluteFill} onPress={handleClose} />
        </Animated.View>
        <GestureDetector gesture={panGesture}>
          <Animated.View style={[styles.sheet, { maxHeight: sheetHeight }, sheetStyle]}>
            <View style={styles.handleBar} />
            {title && (
              <View style={styles.header}>
                <Text style={styles.title}>{title}</Text>
                <Pressable onPress={handleClose} hitSlop={12}>
                  <Text style={styles.closeText}>Done</Text>
                </Pressable>
              </View>
            )}
            {children}
          </Animated.View>
        </GestureDetector>
      </View>
    </Modal>
  );
}

const styles = StyleSheet.create({
  wrapper: {
    flex: 1,
    justifyContent: 'flex-end',
  },
  backdrop: {
    ...StyleSheet.absoluteFillObject,
    backgroundColor: surface.backdrop,
  },
  sheet: {
    backgroundColor: surface.sheet,
    borderTopLeftRadius: radius.xl,
    borderTopRightRadius: radius.xl,
    paddingBottom: 34, // safe area
    overflow: 'hidden',
  },
  handleBar: {
    width: 36,
    height: 4,
    borderRadius: 2,
    backgroundColor: colors.border.medium,
    alignSelf: 'center',
    marginTop: space.sm,
    marginBottom: space.sm,
  },
  header: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
    paddingHorizontal: space.xl,
    paddingVertical: space.md,
    borderBottomWidth: 1,
    borderBottomColor: colors.border.subtle,
  },
  title: {
    fontFamily: 'Inter_600SemiBold',
    fontSize: 17,
    color: colors.text.primary,
    letterSpacing: -0.2,
  },
  closeText: {
    fontFamily: 'Inter_600SemiBold',
    fontSize: 15,
    color: colors.accent.primary,
  },
});
