/**
 * AppCard — Unified card component with press feedback
 *
 * Features:
 *  - Glassmorphic surface (bg.card + border.subtle + inner highlight)
 *  - Press scale animation (0.98) with elevation reduction
 *  - Optional entrance animation (staggered fade-in-up)
 *  - Variants: default, elevated, outlined, ghost
 *  - Dynamic light/dark theming via useAppTheme
 */
import React, { useMemo } from 'react';
import { ViewStyle, StyleProp, StyleSheet } from 'react-native';
import Animated, {
  useSharedValue,
  useAnimatedStyle,
  withSpring,
  interpolate,
  runOnJS,
} from 'react-native-reanimated';
import { Gesture, GestureDetector } from 'react-native-gesture-handler';
import { useAppTheme } from '../hooks/useAppTheme';
import { radius, space, spring as springConfig } from '../styles/theme';

type CardVariant = 'default' | 'elevated' | 'outlined' | 'ghost';

interface AppCardProps {
  children: React.ReactNode;
  onPress?: () => void;
  onLongPress?: () => void;
  variant?: CardVariant;
  style?: StyleProp<ViewStyle>;
  /** Disable press feedback (for non-interactive cards) */
  static?: boolean;
}

export default function AppCard({
  children,
  onPress,
  onLongPress,
  variant = 'default',
  style,
  static: isStatic = false,
}: AppCardProps) {
  const { colors, shadows } = useAppTheme();
  const pressed = useSharedValue(0);

  const tapGesture = Gesture.Tap()
    .onBegin(() => {
      if (!isStatic) pressed.value = withSpring(1, springConfig.press);
    })
    .onFinalize((_e, success) => {
      pressed.value = withSpring(0, springConfig.bounce);
      if (success && onPress) runOnJS(onPress)();
    });

  const longPressGesture = Gesture.LongPress()
    .minDuration(400)
    .onStart(() => { if (onLongPress) runOnJS(onLongPress)(); });

  const composed = onLongPress
    ? Gesture.Race(tapGesture, longPressGesture)
    : tapGesture;

  const animatedStyle = useAnimatedStyle(() => {
    if (isStatic) return {};
    const scale = interpolate(pressed.value, [0, 1], [1, 0.98]);
    const elevation = interpolate(pressed.value, [0, 1], [4, 1]);
    return { transform: [{ scale }], elevation };
  });

  const baseCard: ViewStyle = useMemo(() => ({
    backgroundColor: colors.bg.card,
    borderRadius: radius.lg,
    padding: space.lg,
    marginBottom: space.md,
    borderWidth: 1,
    borderColor: colors.border.subtle,
    borderTopWidth: 1,
    borderTopColor: colors.innerHighlight,
    ...shadows.card,
  }), [colors, shadows]);

  const variantStyles: Record<CardVariant, ViewStyle> = useMemo(() => ({
    default: { ...baseCard },
    elevated: {
      ...baseCard,
      backgroundColor: colors.bg.elevated,
      ...shadows.elevated,
    },
    outlined: {
      ...baseCard,
      backgroundColor: 'transparent',
      borderColor: colors.border.medium,
      elevation: 0,
      shadowOpacity: 0,
    },
    ghost: {
      ...baseCard,
      backgroundColor: 'transparent',
      borderWidth: 0,
      borderTopWidth: 0,
      elevation: 0,
      shadowOpacity: 0,
    },
  }), [baseCard, colors, shadows]);

  const variantStyle = variantStyles[variant];

  if (isStatic) {
    return (
      <Animated.View style={[variantStyle, style]}>
        {children}
      </Animated.View>
    );
  }

  return (
    <GestureDetector gesture={composed}>
      <Animated.View style={[variantStyle, animatedStyle, style]}>
        {children}
      </Animated.View>
    </GestureDetector>
  );
}
