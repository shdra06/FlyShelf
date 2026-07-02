/**
 * ScreenHeader — Unified scroll-aware header for all tabs
 *
 * Features:
 *  - Large title that compacts on scroll
 *  - Optional subtitle
 *  - Right-side action buttons slot
 *  - Built-in safe area padding
 *  - Subtle bottom border that fades in on scroll
 */
import React from 'react';
import { View, Text, StyleSheet, Pressable } from 'react-native';
import Animated, {
  useAnimatedStyle,
  interpolate,
  Extrapolation,
  SharedValue,
} from 'react-native-reanimated';
import { colors, font, space, component, typography } from '../styles/theme';

interface ScreenHeaderProps {
  title: string;
  subtitle?: string;
  /** Pass scrollY from Animated.ScrollView's onScroll */
  scrollY?: SharedValue<number>;
  /** Right-side action buttons */
  rightActions?: React.ReactNode;
  /** Optional left action (e.g., back button) */
  leftAction?: React.ReactNode;
}

export default function ScreenHeader({
  title,
  subtitle,
  scrollY,
  rightActions,
  leftAction,
}: ScreenHeaderProps) {
  const animatedTitle = useAnimatedStyle(() => {
    if (!scrollY) return {};
    const fontSize = interpolate(scrollY.value, [0, 80], [28, 20], Extrapolation.CLAMP);
    const translateY = interpolate(scrollY.value, [0, 80], [0, -4], Extrapolation.CLAMP);
    return { fontSize, transform: [{ translateY }] };
  });

  const animatedSubtitle = useAnimatedStyle(() => {
    if (!scrollY) return {};
    const opacity = interpolate(scrollY.value, [0, 50], [1, 0], Extrapolation.CLAMP);
    const height = interpolate(scrollY.value, [0, 50], [20, 0], Extrapolation.CLAMP);
    return { opacity, height, overflow: 'hidden' as const };
  });

  const animatedBorder = useAnimatedStyle(() => {
    if (!scrollY) return {};
    const opacity = interpolate(scrollY.value, [0, 60], [0, 1], Extrapolation.CLAMP);
    return { opacity };
  });

  return (
    <View style={styles.container}>
      <View style={styles.content}>
        {leftAction && <View style={styles.leftAction}>{leftAction}</View>}
        <View style={styles.titleArea}>
          <Animated.Text
            style={[styles.title, animatedTitle]}
            numberOfLines={1}
          >
            {title}
          </Animated.Text>
          {subtitle && (
            <Animated.Text
              style={[styles.subtitle, animatedSubtitle]}
              numberOfLines={1}
            >
              {subtitle}
            </Animated.Text>
          )}
        </View>
        {rightActions && <View style={styles.rightActions}>{rightActions}</View>}
      </View>
      <Animated.View style={[styles.borderLine, animatedBorder]} />
    </View>
  );
}

const styles = StyleSheet.create({
  container: {
    paddingTop: component.safeTop,
    backgroundColor: colors.bg.base,
    zIndex: 10,
  },
  content: {
    flexDirection: 'row',
    alignItems: 'center',
    paddingHorizontal: space.xl,
    paddingVertical: space.md,
    minHeight: component.headerHeight,
  },
  leftAction: {
    marginRight: space.md,
  },
  titleArea: {
    flex: 1,
  },
  title: {
    fontFamily: font.extrabold,
    fontSize: 28,
    color: colors.text.primary,
    letterSpacing: -0.6,
  },
  subtitle: {
    fontFamily: font.medium,
    fontSize: 13,
    color: colors.text.tertiary,
    letterSpacing: 0.2,
    marginTop: 2,
  },
  rightActions: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: space.sm,
    marginLeft: space.md,
  },
  borderLine: {
    height: 1,
    backgroundColor: colors.border.subtle,
  },
});
