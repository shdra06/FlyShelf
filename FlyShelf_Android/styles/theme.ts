/**
 * FlyShelf Design System — Premium Dark Theme
 * 
 * 3-tier elevation: base → card → elevated
 * Subtle cool-blue hue shifts throughout
 * Spring physics tuned for Apple-level motion
 */

// ═══════════════════════════════════════════
// COLOR TOKENS
// ═══════════════════════════════════════════

export const colors = {
  // Background layers (deep obsidian slate with subtle elevation)
  bg: {
    base:      '#0A0C10',   // deepest background (rich obsidian)
    baseEnd:   '#0E1117',   // gradient end (slight slate shift)
    card:      '#141721',   // card surfaces (elevated slate)
    cardHover: '#1A1E2B',   // card hover/active
    elevated:  '#1C202E',   // floating elements, modals, sheets
    input:     '#0F1219',   // input fields (recessed)
  },

  // Borders (semi-transparent for micro-depth)
  border: {
    subtle:    'rgba(255,255,255,0.07)',  // default card border
    medium:    'rgba(255,255,255,0.12)',  // hover/focus border
    strong:    'rgba(255,255,255,0.18)',  // active/selected
    accent:    'rgba(79,107,255,0.30)',   // accent glow border
  },

  // Text hierarchy (high contrast, warm off-white to balanced slate)
  text: {
    primary:   '#F1F5F9',   // titles, important headers
    secondary: '#94A3B8',   // body text, readable labels
    tertiary:  '#64748B',   // helper, metadata, placeholder
    disabled:  '#334155',   // disabled state
  },

  // Accent palette (vibrant, modern Electric Indigo & semantic accents)
  accent: {
    primary:   '#4F6BFF',   // main brand — modern Electric Indigo
    primaryDim:'rgba(79,107,255,0.14)',
    success:   '#10B981',   // emerald
    successDim:'rgba(16,185,129,0.14)',
    warning:   '#F59E0B',   // amber
    warningDim:'rgba(245,158,11,0.14)',
    error:     '#F43F5E',   // rose/red
    errorDim:  'rgba(244,63,94,0.14)',
    info:      '#06B6D4',   // cyan
    infoDim:   'rgba(6,182,212,0.14)',
  },

  // Semantic type colors
  type: {
    text:      '#94A3B8',
    url:       '#38BDF8',   // sky
    code:      '#10B981',   // emerald
    image:     '#8B5CF6',   // violet
    pdf:       '#F43F5E',   // rose red
    doc:       '#3B82F6',   // blue
    archive:   '#F59E0B',   // amber
    video:     '#8B5CF6',   // violet
    audio:     '#EC4899',   // pink
    ppt:       '#FB923C',   // orange
  },

  // Inner highlight (top edge micro-glow)
  innerHighlight: 'rgba(255,255,255,0.06)',
} as const;

// ═══════════════════════════════════════════
// SPACING SCALE (4px based)
// ═══════════════════════════════════════════

export const space = {
  xs:  4,
  sm:  8,
  md:  12,
  lg:  16,
  xl:  20,
  '2xl': 24,
  '3xl': 32,
} as const;

// ═══════════════════════════════════════════
// RADIUS
// ═══════════════════════════════════════════

export const radius = {
  sm:  8,
  md:  12,
  lg:  16,
  xl:  20,
  pill: 100,
} as const;

// ═══════════════════════════════════════════
// SHADOWS (soft, layered)
// ═══════════════════════════════════════════

export const shadows = {
  card: {
    shadowColor: '#000',
    shadowOffset: { width: 0, height: 4 },
    shadowOpacity: 0.25,
    shadowRadius: 12,
    elevation: 4,
  },
  elevated: {
    shadowColor: '#000',
    shadowOffset: { width: 0, height: 8 },
    shadowOpacity: 0.35,
    shadowRadius: 20,
    elevation: 8,
  },
  glow: (color: string) => ({
    shadowColor: color,
    shadowOffset: { width: 0, height: 4 },
    shadowOpacity: 0.3,
    shadowRadius: 12,
    elevation: 6,
  }),
} as const;

// ═══════════════════════════════════════════
// ANIMATION CONSTANTS
// ═══════════════════════════════════════════

export const spring = {
  /** Gentle card entrance — feels like settling into place */
  gentle: { damping: 20, stiffness: 120, mass: 0.8 },
  /** Button press — snappy but not jarring */
  press: { damping: 15, stiffness: 200, mass: 0.6 },
  /** Bounce back — confident return */
  bounce: { damping: 12, stiffness: 180, mass: 0.7 },
  /** Slow settle — modal or large element */
  slow: { damping: 25, stiffness: 90, mass: 1.0 },
} as const;

export const timing = {
  /** Card stagger base delay (ms) */
  staggerDelay: 40,
  /** Card entrance duration (ms) */
  entranceDuration: 400,
  /** Micro-interaction duration (ms) */
  microDuration: 200,
  /** Focus border transition (ms) */
  focusDuration: 250,
} as const;

// ═══════════════════════════════════════════
// TYPOGRAPHY
// ═══════════════════════════════════════════

export const font = {
  regular:   'Inter_400Regular',
  medium:    'Inter_500Medium',
  semibold:  'Inter_600SemiBold',
  bold:      'Inter_700Bold',
  extrabold: 'Inter_800ExtraBold',
} as const;

export const typography = {
  /** Screen title */
  pageTitle: {
    fontFamily: font.extrabold,
    fontSize: 30,
    letterSpacing: -0.8,
    color: colors.text.primary,
  },
  /** Section header */
  sectionTitle: {
    fontFamily: font.semibold,
    fontSize: 17,
    letterSpacing: -0.2,
    color: colors.text.primary,
  },
  /** Card title / item name */
  cardTitle: {
    fontFamily: font.semibold,
    fontSize: 15,
    letterSpacing: -0.1,
    color: colors.text.primary,
    lineHeight: 20,
  },
  /** Body text */
  body: {
    fontFamily: font.regular,
    fontSize: 14,
    color: colors.text.secondary,
    lineHeight: 20,
  },
  /** Small labels, badges */
  caption: {
    fontFamily: font.medium,
    fontSize: 11,
    letterSpacing: 0.3,
    color: colors.text.tertiary,
  },
  /** Status text, uppercase labels */
  overline: {
    fontFamily: font.semibold,
    fontSize: 10,
    letterSpacing: 1.2,
    textTransform: 'uppercase' as const,
    color: colors.text.tertiary,
  },
  /** Compact title for collapsed headers */
  compactTitle: {
    fontFamily: font.bold,
    fontSize: 20,
    letterSpacing: -0.4,
    color: colors.text.primary,
  },
  /** Subtitle / helper text under title */
  subtitle: {
    fontFamily: font.medium,
    fontSize: 13,
    color: colors.text.tertiary,
    letterSpacing: 0.2,
  },
} as const;

// ═══════════════════════════════════════════
// ICON SIZES
// ═══════════════════════════════════════════

export const iconSize = {
  xs:  16,
  sm:  18,
  md:  22,
  lg:  26,
  xl:  32,
} as const;

// ═══════════════════════════════════════════
// COMPONENT DIMENSIONS
// ═══════════════════════════════════════════

import { Platform } from 'react-native';

export const component = {
  /** Unified header height (content area, excl. safe area) */
  headerHeight: 56,
  /** Tab bar total height */
  tabBarHeight: Platform.OS === 'ios' ? 88 : 72,
  /** Tab bar content padding bottom */
  tabBarPaddingBottom: Platform.OS === 'ios' ? 24 : 10,
  /** Safe area top padding — FALLBACK value only.
   *  Components should prefer useSafeAreaInsets().top from react-native-safe-area-context
   *  for dynamic safe area support across all Android notch/punch-hole variants. */
  safeTop: Platform.OS === 'ios' ? 54 : 44,
  /** Card standard padding */
  cardPadding: space.lg,
  /** Button heights */
  buttonSm: 34,
  buttonMd: 44,
  buttonLg: 52,
  /** FAB size */
  fabSize: 56,
  /** Input field height */
  inputHeight: 48,
  /** Bottom sheet handle height */
  sheetHandle: 4,
  /** Tab bar pill indicator */
  pillWidth: 64,
  pillHeight: 32,
} as const;

// ═══════════════════════════════════════════
// SURFACE TOKENS (glassmorphism)
// ═══════════════════════════════════════════

export const surface = {
  /** Semi-transparent card background for glassmorphic overlays */
  glass: 'rgba(22, 25, 34, 0.85)',
  /** Backdrop for modals/sheets */
  backdrop: 'rgba(0, 0, 0, 0.6)',
  /** Frosted sheet background */
  sheet: 'rgba(30, 34, 45, 0.95)',
  /** Elevated overlay */
  overlay: 'rgba(11, 13, 18, 0.92)',
} as const;

// ═══════════════════════════════════════════
// MATERIAL MOTION DURATIONS
// ═══════════════════════════════════════════

export const motion = {
  /** Large transitions: modals, sheets, page changes */
  emphasized: 500,
  /** Standard transitions: cards, lists */
  standard: 300,
  /** Quick micro-interactions: press, toggle */
  quick: 150,
  /** Stagger delay between list items */
  stagger: 40,
} as const;

// ═══════════════════════════════════════════
// LIGHT MODE TOKENS
// ═══════════════════════════════════════════

export const lightColors = {
  // Background layers (warm creamy texture — Apple-like finish)
  bg: {
    base:      '#FAF9F6',   // warm creamy off-white
    baseEnd:   '#F4F2EC',   // slightly deeper warm gradient end
    card:      '#FFFFFF',   // clean white card
    cardHover: '#F7F6F2',   // subtle hover/press tint
    elevated:  '#FFFFFF',   // modals, sheets
    input:     '#F0EFE9',   // warm gray input bg
  },

  // Borders (very subtle warm gray)
  border: {
    subtle:    'rgba(0,0,0,0.05)',   // soft card borders
    medium:    'rgba(0,0,0,0.10)',   // hover/focus
    strong:    'rgba(0,0,0,0.16)',   // active/selected
    accent:    'rgba(77,104,223,0.20)', // accent glow
  },

  // Text hierarchy (dark, warm but not pure black)
  text: {
    primary:   '#1A1A1A',   // near-black, titles
    secondary: '#605E5C',   // medium warm gray, body text
    tertiary:  '#8A8A8E',   // light warm gray, helpers
    disabled:  '#C6C5C1',   // very light, disabled
  },

  // Accent palette (deepened for WCAG AA on white)
  accent: {
    primary:   '#4D68DF',   // deeper blue for light BG contrast
    primaryDim:'rgba(77,104,223,0.10)',
    success:   '#1E9D6D',   // darker green
    successDim:'rgba(30,157,109,0.10)',
    warning:   '#D89700',   // deeper amber
    warningDim:'rgba(216,151,0,0.10)',
    error:     '#D64C4C',   // deeper red
    errorDim:  'rgba(214,76,76,0.10)',
    info:      '#4283DB',   // deeper blue
    infoDim:   'rgba(66,131,219,0.10)',
  },

  // Semantic type colors (deepened for readability on light)
  type: {
    text:      '#605E5C',
    url:       '#1E88C2',   // deeper sky
    code:      '#168F60',   // deeper emerald
    image:     '#7C5BCE',   // deeper violet
    pdf:       '#CE4242',   // deeper red
    doc:       '#4283DB',   // deeper blue
    archive:   '#B88300',   // deeper amber
    video:     '#7C5BCE',   // deeper violet
    audio:     '#CD4D8C',   // deeper pink
    ppt:       '#C4681A',   // deeper orange
  },

  // Inner highlight (subtle white edge on cards)
  innerHighlight: 'rgba(255,255,255,0.6)',
} as const;

// ═══════════════════════════════════════════
// LIGHT SURFACE TOKENS (glassmorphism)
// ═══════════════════════════════════════════

export const lightSurface = {
  /** Semi-transparent card background for glassmorphic overlays */
  glass: 'rgba(255, 255, 255, 0.85)',
  /** Backdrop for modals/sheets */
  backdrop: 'rgba(20, 18, 16, 0.25)',
  /** Frosted sheet background */
  sheet: 'rgba(250, 249, 246, 0.95)',
  /** Elevated overlay — tab bar, floating elements */
  overlay: 'rgba(250, 249, 246, 0.95)',
} as const;

// ═══════════════════════════════════════════
// LIGHT SHADOWS (softer than dark)
// ═══════════════════════════════════════════

export const lightShadows = {
  card: {
    shadowColor: '#9B8B7B',   // warm shadow color for natural depth
    shadowOffset: { width: 0, height: 2 },
    shadowOpacity: 0.08,
    shadowRadius: 8,
    elevation: 2,
  },
  elevated: {
    shadowColor: '#9B8B7B',
    shadowOffset: { width: 0, height: 4 },
    shadowOpacity: 0.12,
    shadowRadius: 16,
    elevation: 4,
  },
  glow: (color: string) => ({
    shadowColor: color,
    shadowOffset: { width: 0, height: 2 },
    shadowOpacity: 0.18,
    shadowRadius: 8,
    elevation: 3,
  }),
} as const;

