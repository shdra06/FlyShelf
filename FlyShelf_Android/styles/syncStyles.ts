import { StyleSheet } from 'react-native';
import { colors as defaultColors, radius, space, shadows as defaultShadows, font } from './theme';

// Type aliases for the factory parameters
type ThemeColors = Record<string, any>;
type ThemeShadows = Record<string, any>;

/**
 * Creates theme-aware styles for the Sync tab.
 * Call inside a component with colors/shadows from useAppTheme().
 */
export const createSyncStyles = (colors: ThemeColors, shadows: ThemeShadows) => StyleSheet.create({
  container: {
    flex: 1,
    backgroundColor: colors.bg.base,
  },
  header: {
    paddingTop: 15,
    paddingHorizontal: space['2xl'],
    marginBottom: space.xl,
    flexDirection: 'row',
    justifyContent: 'space-between',
    alignItems: 'center',
  },
  title: {
    fontSize: 28,
    fontFamily: font.extrabold,
    color: colors.text.primary,
    letterSpacing: -0.5,
  },
  statusRow: {
    flexDirection: 'row',
    alignItems: 'center',
    backgroundColor: colors.bg.card,
    paddingHorizontal: space.md,
    paddingVertical: 6,
    borderRadius: radius.pill,
    borderWidth: 1,
    borderColor: colors.border.subtle,
  },
  indicator: {
    width: 6,
    height: 6,
    borderRadius: 3,
    marginRight: 6,
  },
  statusText: {
    fontFamily: font.semibold,
    color: colors.text.tertiary,
    fontSize: 10,
    textTransform: 'uppercase',
    letterSpacing: 1,
  },
  feedContainer: {
    flex: 1,
    paddingHorizontal: space.xl,
  },
  clipCard: {
    backgroundColor: colors.bg.card,
    borderRadius: radius.lg,
    padding: space.md,
    marginBottom: space.md,
    borderWidth: 1,
    borderColor: colors.border.subtle,
    borderTopWidth: 1,
    borderTopColor: colors.innerHighlight,
    flexDirection: 'row',
    alignItems: 'center',
    ...shadows.card,
  },
  clipIconContainer: {
    width: 44,
    height: 44,
    borderRadius: radius.md,
    backgroundColor: colors.bg.input,
    justifyContent: 'center',
    alignItems: 'center',
    marginRight: space.md,
    borderWidth: 1,
    borderColor: colors.border.subtle,
  },
  clipContentContainer: {
    flex: 1,
    justifyContent: 'center',
  },
  clipActionsRow: {
    flexDirection: 'row',
    alignItems: 'center',
    marginLeft: space.sm,
  },
  clipType: {
    fontFamily: font.bold,
    color: colors.accent.primary,
    fontSize: 11,
    letterSpacing: 0.3,
  },
  clipTime: {
    fontFamily: font.medium,
    color: colors.text.tertiary,
    fontSize: 11,
  },
  clipTitle: {
    fontFamily: font.semibold,
    color: colors.text.primary,
    fontSize: 14,
    marginBottom: 3,
    lineHeight: 19,
    letterSpacing: -0.1,
  },
  actionBtnIcon: {
    width: 38,
    height: 38,
    borderRadius: radius.sm,
    justifyContent: 'center',
    alignItems: 'center',
    marginLeft: 6,
    backgroundColor: colors.bg.input,
    borderWidth: 1,
    borderColor: colors.border.subtle,
  },
  emptyText: {
    fontFamily: font.medium,
    color: colors.text.disabled,
    textAlign: 'center',
    marginTop: 50,
    fontSize: 14,
  },
  inputArea: {
    flexDirection: 'row',
    paddingHorizontal: space.lg,
    paddingVertical: 10,
    paddingBottom: 72, // Account for absolutely-positioned tab bar (64px height + 8px padding)
    backgroundColor: colors.bg.base,
    borderTopWidth: 1,
    borderTopColor: colors.border.subtle,
    alignItems: 'flex-end',
    gap: 6,
  },
  textInput: {
    flex: 1,
    backgroundColor: colors.bg.card,
    color: colors.text.primary,
    fontFamily: font.regular,
    borderRadius: radius.xl,
    paddingHorizontal: space.xl,
    paddingTop: 10,
    paddingBottom: 10,
    fontSize: 15,
    maxHeight: 120,
    borderWidth: 1.5,
    borderColor: colors.border.default,
    minHeight: 44,
  },
  sendButton: {
    marginBottom: 1,
    justifyContent: 'center',
    alignItems: 'center',
  },
  attachButton: {
    width: 36,
    height: 44,
    justifyContent: 'center',
    alignItems: 'center',
  },
  modalOverlay: {
    flex: 1,
    backgroundColor: 'rgba(0,0,0,0.80)',
    justifyContent: 'center',
    alignItems: 'center',
    padding: space.xl,
  },
  modalContent: {
    backgroundColor: colors.bg.elevated,
    borderRadius: radius.xl,
    padding: space['2xl'],
    width: '100%',
    maxWidth: 400,
    borderWidth: 1,
    borderColor: colors.border.medium,
    ...shadows.elevated,
  },
  modalTitle: {
    fontSize: 22,
    fontFamily: font.extrabold,
    color: colors.text.primary,
    marginBottom: space.sm,
    letterSpacing: -0.3,
  },
  modalSubtitle: {
    fontSize: 14,
    fontFamily: font.regular,
    color: colors.text.secondary,
    marginBottom: space['2xl'],
    lineHeight: 20,
  },
  modalInput: {
    backgroundColor: colors.bg.input,
    color: colors.text.primary,
    fontFamily: font.medium,
    padding: space.lg,
    borderRadius: radius.md,
    fontSize: 16,
    borderWidth: 1,
    borderColor: colors.border.subtle,
    marginBottom: space.xl,
  },
  modalButton: {
    backgroundColor: colors.accent.primary,
    padding: space.lg,
    borderRadius: radius.md,
    alignItems: 'center',
    ...shadows.glow(colors.accent.primary),
  },
  modalButtonText: {
    fontFamily: font.bold,
    color: '#FFF',
    fontSize: 15,
    letterSpacing: 0.2,
  },
  targetOption: {
    flexDirection: 'row',
    alignItems: 'center',
    paddingVertical: space.md,
    paddingHorizontal: space.lg,
    backgroundColor: colors.bg.input,
    borderRadius: radius.md,
    borderWidth: 1,
    borderColor: colors.border.subtle,
    marginBottom: 10,
  },

  // ─── Badge styles ───
  badge: {
    paddingHorizontal: 6,
    paddingVertical: 2,
    borderRadius: 6,
    marginLeft: 6,
  },
  badgeText: {
    fontFamily: font.bold,
    fontSize: 10,
    letterSpacing: 0.3,
  },

  // ─── Undo Toast ───
  undoToast: {
    position: 'absolute',
    bottom: 90,
    left: space.xl,
    right: space.xl,
    backgroundColor: colors.bg.elevated,
    borderRadius: radius.lg,
    borderWidth: 1,
    borderColor: colors.border.medium,
    paddingHorizontal: space.xl,
    paddingVertical: space.md,
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
    ...shadows.elevated,
    zIndex: 999,
  },
  undoToastText: {
    fontFamily: font.semibold,
    color: colors.text.secondary,
    fontSize: 13,
  },
  undoToastAction: {
    fontFamily: font.bold,
    color: colors.accent.primary,
    fontSize: 14,
    letterSpacing: 0.5,
    paddingHorizontal: space.md,
    paddingVertical: space.sm,
  },

  // ─── Incognito Banner ───
  incognitoBanner: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'center',
    backgroundColor: 'rgba(251,191,36,0.10)',
    borderWidth: 1,
    borderColor: 'rgba(251,191,36,0.20)',
    borderRadius: radius.md,
    marginHorizontal: space.xl,
    marginBottom: space.sm,
    paddingVertical: 6,
    paddingHorizontal: space.md,
    gap: 6,
  },
  incognitoBannerText: {
    fontFamily: font.semibold,
    color: '#FBBF24',
    fontSize: 12,
    letterSpacing: 0.2,
  },

  // ─── Incognito Toggle Button ───
  incognitoButton: {
    padding: 10,
    borderRadius: 10,
  },
});

/**
 * @deprecated Use createSyncStyles(colors, shadows) instead for theme support.
 * Kept for backward compatibility — returns dark-theme styles.
 */
export const styles = createSyncStyles(defaultColors, defaultShadows);
