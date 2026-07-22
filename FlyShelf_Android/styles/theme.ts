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
  // Background layers (subtle blue undertone gradient)
  bg: {
    base:      '#0B0D12',   // deepest background
    baseEnd:   '#0F1219',   // gradient end (slight blue shift)
    card:      '#161922',   // card surfaces
    cardHover: '#1C2029',   // card hover/active
    elevated:  '#1E222D',   // floating elements, modals
    input:     '#0E1017',   // input fields (recessed)
  },

  // Borders (semi-transparent for depth)
  border: {
    subtle:    'rgba(255,255,255,0.06)',  // default card border
    medium:    'rgba(255,255,255,0.10)',  // hover/focus border
    strong:    'rgba(255,255,255,0.15)',  // active/selected
    accent:    'rgba(99,132,255,0.25)',   // accent glow border
  },

  // Text hierarchy
  text: {
    primary:   '#F0F2F5',   // titles, important
    secondary: '#8B92A0',   // body, labels
    tertiary:  '#555C6B',   // helper, placeholder
    disabled:  '#3A3F4A',   // disabled state
  },

  // Accent palette
  accent: {
    primary:   '#6384FF',   // main brand — refined blue-violet
    primaryDim:'rgba(99,132,255,0.12)',
    success:   '#34D399',   // online, complete
    successDim:'rgba(52,211,153,0.12)',
    warning:   '#FBBF24',   // amber
    warningDim:'rgba(251,191,36,0.12)',
    error:     '#F87171',   // delete, error
    errorDim:  'rgba(248,113,113,0.12)',
    info:      '#60A5FA',   // links, info
    infoDim:   'rgba(96,165,250,0.12)',
  },

  // Semantic type colors
  type: {
    text:      '#8B92A0',
    url:       '#38BDF8',   // sky
    code:      '#34D399',   // emerald
    image:     '#A78BFA',   // violet
    pdf:       '#F87171',   // red
    doc:       '#60A5FA',   // blue
    archive:   '#FBBF24',   // amber
    video:     '#A78BFA',   // violet
    audio:     '#F472B6',   // pink
    ppt:       '#FB923C',   // orange
  },

  // Inner highlight (top edge light)
  innerHighlight: 'rgba(255,255,255,0.04)',
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
  // Background layers (warm neutral gray — NOT pure white)
  bg: {
    base:      '#F5F6FA',   // warm gray base (reduces glare)
    baseEnd:   '#ECEEF5',   // subtle blue-tinted gradient end
    card:      '#FFFFFF',   // crisp white cards float above base
    cardHover: '#F0F1F6',   // subtle hover/press tint
    elevated:  '#FFFFFF',   // modals, sheets
    input:     '#EDEEF3',   // recessed input fields
  },

  // Borders (opacity-based black for natural blending)
  border: {
    subtle:    'rgba(0,0,0,0.07)',   // soft card borders
    medium:    'rgba(0,0,0,0.12)',   // hover/focus
    strong:    'rgba(0,0,0,0.18)',   // active/selected
    accent:    'rgba(85,112,232,0.20)', // accent glow
  },

  // Text hierarchy (high-contrast charcoals)
  text: {
    primary:   '#1A1D26',   // near-black, titles
    secondary: '#5B6178',   // medium gray, body text
    tertiary:  '#9CA3B4',   // light gray, helpers
    disabled:  '#C8CCD6',   // very light, disabled
  },

  // Accent palette (deepened 15-20% for WCAG AA on white)
  accent: {
    primary:   '#5570E8',   // deeper blue for light BG contrast
    primaryDim:'rgba(85,112,232,0.10)',
    success:   '#22B07A',   // darker green
    successDim:'rgba(34,176,122,0.10)',
    warning:   '#E5A100',   // deeper amber
    warningDim:'rgba(229,161,0,0.10)',
    error:     '#E25555',   // deeper red
    errorDim:  'rgba(226,85,85,0.10)',
    info:      '#4A8FE7',   // deeper blue
    infoDim:   'rgba(74,143,231,0.10)',
  },

  // Semantic type colors (deepened for readability on light)
  type: {
    text:      '#5B6178',
    url:       '#2598D5',   // deeper sky
    code:      '#1A9A6A',   // deeper emerald
    image:     '#8B6AD6',   // deeper violet
    pdf:       '#D84C4C',   // deeper red
    doc:       '#4A8FE7',   // deeper blue
    archive:   '#C99000',   // deeper amber
    video:     '#8B6AD6',   // deeper violet
    audio:     '#D95B98',   // deeper pink
    ppt:       '#D47520',   // deeper orange
  },

  // Inner highlight (subtle white edge on cards)
  innerHighlight: 'rgba(255,255,255,0.6)',
} as const;

// ═══════════════════════════════════════════
// LIGHT SURFACE TOKENS (glassmorphism)
// ═══════════════════════════════════════════

export const lightSurface = {
  /** Semi-transparent card background for glassmorphic overlays */
  glass: 'rgba(255, 255, 255, 0.82)',
  /** Backdrop for modals/sheets */
  backdrop: 'rgba(15, 20, 30, 0.25)',
  /** Frosted sheet background */
  sheet: 'rgba(245, 246, 250, 0.96)',
  /** Elevated overlay — tab bar, floating elements */
  overlay: 'rgba(245, 246, 250, 0.95)',
} as const;

// ═══════════════════════════════════════════
// LIGHT SHADOWS (softer than dark)
// ═══════════════════════════════════════════

export const lightShadows = {
  card: {
    shadowColor: '#4A5568',   // gray-tinted shadow for natural depth
    shadowOffset: { width: 0, height: 1 },
    shadowOpacity: 0.08,
    shadowRadius: 6,
    elevation: 2,
  },
  elevated: {
    shadowColor: '#4A5568',
    shadowOffset: { width: 0, height: 3 },
    shadowOpacity: 0.12,
    shadowRadius: 12,
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

