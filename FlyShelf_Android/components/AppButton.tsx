/**
 * AppButton — Standard button system with variants, sizes, and press animation
 *
 * Variants: primary, secondary, ghost, danger
 * Sizes: sm (34px), md (44px), lg (52px)
 * Features: Spring press animation, built-in haptics, icon slots, loading state
 * Dynamic light/dark theming via useAppTheme
 */
import React, { useMemo } from 'react';
import { Text, ActivityIndicator, ViewStyle, TextStyle, StyleSheet } from 'react-native';
import Animated, {
  useSharedValue,
  useAnimatedStyle,
  withSpring,
  interpolate,
  runOnJS,
} from 'react-native-reanimated';
import { Gesture, GestureDetector } from 'react-native-gesture-handler';
import * as Haptics from 'expo-haptics';
import { useAppTheme } from '../hooks/useAppTheme';
import { font, radius, space, spring as springConfig } from '../styles/theme';

type ButtonVariant = 'primary' | 'secondary' | 'ghost' | 'danger';
type ButtonSize = 'sm' | 'md' | 'lg';

interface AppButtonProps {
  label: string;
  onPress: () => void;
  variant?: ButtonVariant;
  size?: ButtonSize;
  icon?: React.ReactNode;
  iconRight?: React.ReactNode;
  loading?: boolean;
  disabled?: boolean;
  fullWidth?: boolean;
  haptic?: boolean;
  style?: ViewStyle;
}

const sizeHeights: Record<ButtonSize, number> = { sm: 34, md: 44, lg: 52 };
const sizeFontSizes: Record<ButtonSize, number> = { sm: 12, md: 14, lg: 16 };
const sizePaddingH: Record<ButtonSize, number> = { sm: 12, md: 16, lg: 20 };

export default function AppButton({
  label,
  onPress,
  variant = 'primary',
  size = 'md',
  icon,
  iconRight,
  loading = false,
  disabled = false,
  fullWidth = false,
  haptic = true,
  style,
}: AppButtonProps) {
  const { colors } = useAppTheme();
  const pressed = useSharedValue(0);

  const handlePress = () => {
    if (haptic) Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
    onPress();
  };

  const gesture = Gesture.Tap()
    .enabled(!disabled && !loading)
    .onBegin(() => {
      pressed.value = withSpring(1, springConfig.press);
    })
    .onFinalize((_e, success) => {
      pressed.value = withSpring(0, springConfig.bounce);
      if (success) runOnJS(handlePress)();
    });

  const animatedStyle = useAnimatedStyle(() => {
    const scale = interpolate(pressed.value, [0, 1], [1, 0.96]);
    return { transform: [{ scale }] };
  });

  const variantContainerStyles: Record<ButtonVariant, ViewStyle> = useMemo(() => ({
    primary: {
      backgroundColor: colors.accent.primary,
    },
    secondary: {
      backgroundColor: colors.bg.card,
      borderWidth: 1,
      borderColor: colors.border.medium,
    },
    ghost: {
      backgroundColor: 'transparent',
    },
    danger: {
      backgroundColor: colors.accent.errorDim,
      borderWidth: 1,
      borderColor: 'rgba(248,113,113,0.2)',
    },
  }), [colors]);

  const variantTextStyles: Record<ButtonVariant, TextStyle> = useMemo(() => ({
    primary: {
      fontFamily: font.semibold,
      color: '#FFFFFF',
      letterSpacing: 0.2,
    },
    secondary: {
      fontFamily: font.semibold,
      color: colors.text.secondary,
      letterSpacing: 0.2,
    },
    ghost: {
      fontFamily: font.semibold,
      color: colors.accent.primary,
      letterSpacing: 0.2,
    },
    danger: {
      fontFamily: font.semibold,
      color: colors.accent.error,
      letterSpacing: 0.2,
    },
  }), [colors]);

  const containerStyle: ViewStyle = {
    ...variantContainerStyles[variant],
    height: sizeHeights[size],
    paddingHorizontal: sizePaddingH[size],
    borderRadius: sizeHeights[size] / 2, // pill shape
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'center',
    gap: space.sm,
    ...(fullWidth ? { width: '100%' } : {}),
    ...(disabled ? { opacity: 0.4 } : {}),
  };

  const textStyle: TextStyle = {
    ...variantTextStyles[variant],
    fontSize: sizeFontSizes[size],
  };

  return (
    <GestureDetector gesture={gesture}>
      <Animated.View style={[containerStyle, animatedStyle, style]}>
        {loading ? (
          <ActivityIndicator
            size="small"
            color={variant === 'primary' ? '#fff' : colors.accent.primary}
          />
        ) : (
          <>
            {icon}
            <Text style={textStyle}>{label}</Text>
            {iconRight}
          </>
        )}
      </Animated.View>
    </GestureDetector>
  );
}
