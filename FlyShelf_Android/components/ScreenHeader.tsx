/**
 * ScreenHeader — Unified scroll-aware header for all tabs
 *
 * Features:
 *  - Large title that compacts on scroll
 *  - Optional subtitle
 *  - Right-side action buttons slot
 *  - Built-in safe area padding
 *  - Subtle bottom border that fades in on scroll
 *  - Dynamic light/dark theming via useAppTheme
 */
import React from 'react';
import { View, Text, StyleSheet, Pressable } from 'react-native';
import Animated, {
  useAnimatedStyle,
  interpolate,
  Extrapolation,
  SharedValue,
} from 'react-native-reanimated';
import { useAppTheme } from '../hooks/useAppTheme';
import { font, space, component } from '../styles/theme';
import { useSafeAreaInsets } from 'react-native-safe-area-context';

interface ScreenHeaderProps {
  title: string;
  subtitle?: string;
  /** Pass scrollY from Animated.ScrollView's onScroll */
  scrollY?: SharedValue<number>;
  /** Right-side action buttons */
  rightActions?: React.ReactNode;
  /** Optional left action (e.g., back button) */
  leftAction?: React.ReactNode;
  /** Optional status badge (e.g., device online indicator) rendered below title */
  statusBadge?: React.ReactNode;
  /** Optional color overrides for custom-themed headers */
  colorOverrides?: {
    background?: string;
    title?: string;
    subtitle?: string;
    border?: string;
  };
}

export default function ScreenHeader({
  title,
  subtitle,
  scrollY,
  rightActions,
  leftAction,
  statusBadge,
  colorOverrides,
}: ScreenHeaderProps) {
  const { colors } = useAppTheme();
  const insets = useSafeAreaInsets();

  const bgColor = colorOverrides?.background ?? colors.bg.base;
  const titleColor = colorOverrides?.title ?? colors.text.primary;
  const subtitleColor = colorOverrides?.subtitle ?? colors.text.tertiary;
  const borderColor = colorOverrides?.border ?? colors.border.subtle;

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
    <View style={[styles.container, { backgroundColor: bgColor, paddingTop: Math.max(insets.top, component.safeTop) }]}>
      <View style={styles.content}>
        {leftAction && <View style={styles.leftAction}>{leftAction}</View>}
        <View style={styles.titleArea}>
          <Animated.Text
            style={[styles.title, { color: titleColor }, animatedTitle]}
            numberOfLines={1}
          >
            {title}
          </Animated.Text>
          {subtitle && (
            <Animated.Text
              style={[styles.subtitle, { color: subtitleColor }, animatedSubtitle]}
              numberOfLines={1}
            >
              {subtitle}
            </Animated.Text>
          )}
          {statusBadge}
        </View>
        {rightActions && <View style={styles.rightActions}>{rightActions}</View>}
      </View>
      <Animated.View style={[styles.borderLine, { backgroundColor: borderColor }, animatedBorder]} />
    </View>
  );
}

const styles = StyleSheet.create({
  container: {
    paddingTop: component.safeTop,
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
    letterSpacing: -0.6,
  },
  subtitle: {
    fontFamily: font.medium,
    fontSize: 13,
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
  },
});
