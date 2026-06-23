/**
 * DeviceHub Styles — Premium Dark Theme
 *
 * Styles for the device management modal component.
 * Uses all tokens from the FlyShelf design system.
 */

import { StyleSheet, Platform } from 'react-native';
import { colors, font, radius, space, shadows, typography } from './theme';



export const deviceStyles = StyleSheet.create({
  // ═══════════════════════════════════════════
  // LAYOUT
  // ═══════════════════════════════════════════

  modal: {
    flex: 1,
    backgroundColor: 'rgba(0,0,0,0.85)',
    justifyContent: 'flex-end',
  },

  container: {
    backgroundColor: colors.bg.elevated,
    height: '92%',
    borderTopLeftRadius: radius.xl,
    borderTopRightRadius: radius.xl,
    overflow: 'hidden',
  },

  handleBar: {
    width: 40,
    height: 4,
    borderRadius: radius.pill,
    backgroundColor: colors.text.disabled,
    alignSelf: 'center',
    marginTop: space.sm,
    marginBottom: space.xs,
  },

  header: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
    paddingHorizontal: space.xl,
    paddingVertical: space.lg,
    borderBottomWidth: 1,
    borderBottomColor: colors.border.subtle,
  },

  headerLeft: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: space.sm,
  },

  headerTitle: {
    ...typography.pageTitle,
    fontSize: 24,
  },

  headerCountBadge: {
    backgroundColor: colors.accent.primaryDim,
    borderRadius: radius.pill,
    paddingHorizontal: space.sm,
    paddingVertical: 2,
    borderWidth: 1,
    borderColor: colors.border.accent,
    minWidth: 28,
    alignItems: 'center',
  },

  headerCountText: {
    fontFamily: font.bold,
    fontSize: 12,
    color: colors.accent.primary,
  },

  closeBtn: {
    width: 36,
    height: 36,
    borderRadius: 18,
    backgroundColor: colors.bg.input,
    alignItems: 'center',
    justifyContent: 'center',
    borderWidth: 1,
    borderColor: colors.border.subtle,
  },

  scrollContent: {
    paddingHorizontal: space.lg,
    paddingTop: space.lg,
    paddingBottom: 100,
  },

  // ═══════════════════════════════════════════
  // PAIRING KEY SECTION
  // ═══════════════════════════════════════════

  keySection: {
    backgroundColor: colors.bg.card,
    borderRadius: radius.lg,
    borderWidth: 1,
    borderColor: colors.border.subtle,
    padding: space.lg,
    marginBottom: space.lg,
  },

  keySectionHeader: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
  },

  keySectionHeaderLeft: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: space.sm,
  },

  keySectionTitle: {
    ...typography.sectionTitle,
    fontSize: 14,
  },

  keySectionChevron: {
    marginLeft: space.xs,
  },

  keySectionBody: {
    marginTop: space.md,
  },

  keyLabel: {
    ...typography.overline,
    marginBottom: space.xs,
  },

  keyDisplay: {
    backgroundColor: colors.bg.input,
    borderRadius: radius.sm,
    borderWidth: 1,
    borderColor: colors.border.subtle,
    paddingHorizontal: space.md,
    paddingVertical: space.md,
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
  },

  keyText: {
    fontFamily: Platform.select({ ios: 'Menlo', android: 'monospace' }),
    fontSize: 13,
    color: colors.text.primary,
    letterSpacing: 1.5,
    flex: 1,
  },

  keyActions: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: space.sm,
    marginTop: space.md,
  },

  keyActionBtn: {
    width: 40,
    height: 40,
    borderRadius: radius.sm,
    backgroundColor: colors.bg.input,
    alignItems: 'center',
    justifyContent: 'center',
    borderWidth: 1,
    borderColor: colors.border.subtle,
  },

  keyActionBtnRegen: {
    width: 40,
    height: 40,
    borderRadius: radius.sm,
    backgroundColor: colors.accent.warningDim,
    alignItems: 'center',
    justifyContent: 'center',
    borderWidth: 1,
    borderColor: 'rgba(251,191,36,0.2)',
  },

  notPairedBanner: {
    backgroundColor: colors.accent.errorDim,
    borderRadius: radius.sm,
    borderWidth: 1,
    borderColor: 'rgba(248,113,113,0.2)',
    paddingVertical: space.md,
    paddingHorizontal: space.lg,
    alignItems: 'center',
    marginTop: space.md,
  },

  notPairedText: {
    fontFamily: font.medium,
    fontSize: 13,
    color: colors.accent.error,
  },

  // ═══════════════════════════════════════════
  // DEVICE CARDS (the hero)
  // ═══════════════════════════════════════════

  deviceCard: {
    backgroundColor: colors.bg.card,
    borderRadius: radius.lg,
    borderWidth: 1,
    borderColor: colors.border.subtle,
    overflow: 'hidden',
    marginBottom: space.md,
    ...shadows.card,
  },

  deviceCardOnline: {
    borderColor: colors.border.accent,
    ...shadows.glow(colors.accent.primary),
  },

  deviceCardOffline: {
    opacity: 0.7,
  },

  deviceCardInner: {
    paddingVertical: space.lg,
    paddingHorizontal: space.lg,
    flexDirection: 'row',
    alignItems: 'center',
    gap: space.md,
  },

  onlineStrip: {
    position: 'absolute',
    left: 0,
    top: 0,
    bottom: 0,
    width: 4,
    backgroundColor: colors.accent.primary,
    borderTopLeftRadius: radius.lg,
    borderBottomLeftRadius: radius.lg,
  },

  deviceIconContainer: {
    width: 52,
    height: 52,
    borderRadius: 26,
    backgroundColor: colors.bg.input,
    alignItems: 'center',
    justifyContent: 'center',
  },

  deviceIconContainerPC: {
    backgroundColor: colors.accent.infoDim,
  },

  deviceIconContainerMobile: {
    backgroundColor: colors.accent.successDim,
  },

  deviceIconContainerBrowser: {
    backgroundColor: colors.accent.warningDim,
  },

  deviceInfo: {
    flex: 1,
    flexDirection: 'column',
    justifyContent: 'center',
  },

  deviceNameRow: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: space.sm,
    marginBottom: 4,
  },

  deviceName: {
    ...typography.cardTitle,
  },

  proBadge: {
    backgroundColor: colors.accent.successDim,
    borderWidth: 1,
    borderColor: 'rgba(52,211,153,0.25)',
    borderRadius: radius.pill,
    paddingHorizontal: space.sm,
    paddingVertical: 1,
  },

  proBadgeText: {
    fontFamily: font.bold,
    fontSize: 9,
    letterSpacing: 0.8,
    color: colors.accent.success,
    textTransform: 'uppercase',
  },

  freeBadge: {
    backgroundColor: 'rgba(85,92,107,0.15)',
    borderWidth: 1,
    borderColor: colors.border.subtle,
    borderRadius: radius.pill,
    paddingHorizontal: space.sm,
    paddingVertical: 1,
  },

  freeBadgeText: {
    fontFamily: font.medium,
    fontSize: 9,
    letterSpacing: 0.8,
    color: colors.text.tertiary,
    textTransform: 'uppercase',
  },

  statusRow: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: space.sm,
    marginBottom: 4,
  },

  statusDot: {
    width: 8,
    height: 8,
    borderRadius: 4,
  },

  statusDotOnline: {
    backgroundColor: colors.accent.success,
  },

  statusDotCloud: {
    backgroundColor: colors.accent.warning,
  },

  statusDotOffline: {
    backgroundColor: colors.accent.error,
  },

  statusText: {
    ...typography.caption,
    color: colors.text.secondary,
  },

  connectionBadge: {
    borderRadius: radius.pill,
    paddingHorizontal: 6,
    paddingVertical: 1,
  },

  connectionBadgeLAN: {
    backgroundColor: colors.accent.successDim,
  },

  connectionBadgeCloud: {
    backgroundColor: colors.accent.warningDim,
  },

  connectionBadgeOffline: {
    backgroundColor: colors.accent.errorDim,
  },

  connectionBadgeText: {
    fontFamily: font.semibold,
    fontSize: 9,
    letterSpacing: 0.5,
  },

  connectionBadgeTextLAN: {
    color: colors.accent.success,
  },

  connectionBadgeTextCloud: {
    color: colors.accent.warning,
  },

  connectionBadgeTextOffline: {
    color: colors.accent.error,
  },

  latencyText: {
    fontFamily: font.medium,
    fontSize: 10,
    color: colors.accent.success,
    letterSpacing: 0.3,
  },

  deviceMeta: {
    ...typography.caption,
    color: colors.text.tertiary,
  },

  removeBtn: {
    width: 36,
    height: 36,
    borderRadius: radius.sm,
    backgroundColor: colors.accent.errorDim,
    alignItems: 'center',
    justifyContent: 'center',
  },

  // ═══════════════════════════════════════════
  // STATUS BAR
  // ═══════════════════════════════════════════

  statusBar: {
    backgroundColor: colors.bg.card,
    borderRadius: radius.lg,
    borderWidth: 1,
    borderColor: colors.border.subtle,
    flexDirection: 'row',
    alignItems: 'center',
    paddingVertical: space.lg,
    marginBottom: space.lg,
  },

  statusBarItem: {
    flex: 1,
    alignItems: 'center',
    justifyContent: 'center',
  },

  statusBarValue: {
    fontFamily: font.extrabold,
    fontSize: 24,
    color: colors.accent.primary,
    letterSpacing: -0.5,
  },

  statusBarValueOnline: {
    color: colors.accent.success,
  },

  statusBarLabel: {
    ...typography.overline,
    marginTop: 2,
  },

  statusBarDivider: {
    width: 1,
    height: 32,
    backgroundColor: colors.border.subtle,
  },

  // ═══════════════════════════════════════════
  // ADD DEVICE SECTION
  // ═══════════════════════════════════════════

  addDeviceBtn: {
    borderRadius: radius.lg,
    overflow: 'hidden',
    marginTop: space.lg,
    marginBottom: space.lg,
    ...shadows.glow(colors.accent.primary),
  },

  addDeviceBtnGradient: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'center',
    paddingVertical: space.lg,
    gap: space.sm,
  },

  addDeviceBtnText: {
    fontFamily: font.bold,
    fontSize: 16,
    color: '#FFFFFF',
    letterSpacing: 0.3,
  },

  addDeviceSection: {
    marginTop: space.md,
  },

  pairOption: {
    backgroundColor: colors.bg.card,
    borderRadius: radius.lg,
    borderWidth: 1,
    borderColor: colors.border.subtle,
    flexDirection: 'row',
    alignItems: 'center',
    paddingVertical: space.lg,
    paddingHorizontal: space.lg,
    gap: space.md,
    marginBottom: space.md,
  },

  pairOptionIcon: {
    width: 48,
    height: 48,
    borderRadius: radius.md,
    backgroundColor: colors.accent.primaryDim,
    alignItems: 'center',
    justifyContent: 'center',
  },

  pairOptionContent: {
    flex: 1,
  },

  pairOptionTitle: {
    fontFamily: font.semibold,
    fontSize: 15,
    color: colors.text.primary,
    marginBottom: 2,
  },

  pairOptionDesc: {
    ...typography.caption,
    color: colors.text.secondary,
  },

  // ═══════════════════════════════════════════
  // EMPTY STATE
  // ═══════════════════════════════════════════

  emptyState: {
    alignItems: 'center',
    justifyContent: 'center',
    paddingVertical: space['3xl'] * 2,
    paddingHorizontal: space.xl,
  },

  emptyIcon: {
    fontSize: 64,
    marginBottom: space.xl,
    opacity: 0.5,
  },

  emptyTitle: {
    fontFamily: font.semibold,
    fontSize: 18,
    color: colors.text.primary,
    marginBottom: space.sm,
    textAlign: 'center',
  },

  emptySubtitle: {
    fontFamily: font.regular,
    fontSize: 14,
    color: colors.text.secondary,
    textAlign: 'center',
    lineHeight: 20,
    maxWidth: 280,
    marginBottom: space['2xl'],
  },

  emptyAddBtn: {
    borderRadius: radius.lg,
    overflow: 'hidden',
    ...shadows.glow(colors.accent.primary),
  },

  emptyAddBtnGradient: {
    paddingHorizontal: space['3xl'],
    paddingVertical: space.lg,
    flexDirection: 'row',
    alignItems: 'center',
    gap: space.sm,
  },

  emptyAddBtnText: {
    fontFamily: font.bold,
    fontSize: 15,
    color: '#FFFFFF',
  },

  // ═══════════════════════════════════════════
  // DEVICE LIMIT
  // ═══════════════════════════════════════════

  limitBar: {
    height: 6,
    borderRadius: radius.pill,
    backgroundColor: colors.bg.input,
    marginTop: space.md,
    marginBottom: space.xs,
    overflow: 'hidden',
  },

  limitBarFill: {
    height: '100%',
    borderRadius: radius.pill,
    backgroundColor: colors.accent.primary,
  },

  limitBarFillWarning: {
    backgroundColor: colors.accent.warning,
  },

  limitBarFillFull: {
    backgroundColor: colors.accent.error,
  },

  limitText: {
    ...typography.caption,
    color: colors.text.tertiary,
    textAlign: 'right',
  },

  limitTextWarning: {
    color: colors.accent.warning,
  },

  limitTextFull: {
    color: colors.accent.error,
    fontFamily: font.semibold,
  },

  // ═══════════════════════════════════════════
  // SECTION LABEL
  // ═══════════════════════════════════════════

  sectionLabel: {
    ...typography.overline,
    marginBottom: space.md,
    marginTop: space.sm,
  },
});
