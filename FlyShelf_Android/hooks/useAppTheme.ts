/**
 * useAppTheme — Provides theme-aware tokens based on system color scheme
 *
 * Returns the correct color/surface/shadow set for light or dark mode,
 * plus all theme-invariant tokens (space, radius, font, etc.).
 */
import { useMemo } from 'react';
import { useColorScheme } from 'react-native';
import {
  colors as darkColors, surface as darkSurface, shadows as darkShadows,
  lightColors, lightSurface, lightShadows,
  space, radius, font, typography, spring, timing, motion, iconSize, component,
} from '../styles/theme';

export function useAppTheme() {
  const scheme = useColorScheme();
  const isDark = scheme !== 'light';

  const colors = isDark ? darkColors : lightColors;

  const themedTypography = useMemo(() => ({
    ...typography,
    pageTitle: { ...typography.pageTitle, color: colors.text.primary },
    sectionTitle: { ...typography.sectionTitle, color: colors.text.primary },
    cardTitle: { ...typography.cardTitle, color: colors.text.primary },
    body: { ...typography.body, color: colors.text.primary },
    caption: { ...typography.caption, color: colors.text.tertiary },
    overline: { ...typography.overline, color: colors.text.tertiary },
  }), [colors]);

  return {
    isDark,
    colors,
    surface: isDark ? darkSurface : lightSurface,
    shadows: isDark ? darkShadows : lightShadows,
    // These don't change between themes
    space,
    radius,
    font,
    typography: themedTypography,
    spring,
    timing,
    motion,
    iconSize,
    component,
  };
}

/** Type for the return value of useAppTheme */
export type AppTheme = ReturnType<typeof useAppTheme>;

/** Color token type that works for both light and dark themes */
export type ThemeColors = typeof darkColors | typeof lightColors;

/** Shadow token type that works for both themes */
export type ThemeShadows = typeof darkShadows | typeof lightShadows;
