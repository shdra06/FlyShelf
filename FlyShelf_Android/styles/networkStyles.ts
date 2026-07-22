/**
 * NetworkDashboard Styles — Theme-aware factory
 *
 * Styles for the network monitoring dashboard component.
 * Uses all tokens from the FlyShelf design system.
 * Pattern: matches createDeviceStyles in deviceStyles.ts
 */

import { StyleSheet, Platform } from 'react-native';
import { colors as defaultColors, font, radius, space, shadows as defaultShadows, typography } from './theme';

type ThemeColors = Record<string, any>;
type ThemeShadows = Record<string, any>;

export const createNetworkStyles = (colors: ThemeColors, shadows: ThemeShadows) => StyleSheet.create({
  // ═══════════════════════════════════════════
  // LAYOUT
  // ═══════════════════════════════════════════

  container: {
    flex: 1,
    backgroundColor: colors.bg.primary,
  },

  scrollContent: {
    paddingHorizontal: space.lg,
    paddingTop: space.md,
    paddingBottom: 100,
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
    ...typography.sectionTitle,
    fontSize: 17,
  },

  headerActions: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: space.sm,
  },

  refreshBtn: {
    width: 36,
    height: 36,
    borderRadius: 18,
    backgroundColor: colors.bg.input,
    alignItems: 'center',
    justifyContent: 'center',
    borderWidth: 1,
    borderColor: colors.border.subtle,
  },

  statusBadge: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: space.xs,
    backgroundColor: colors.accent.successDim,
    borderRadius: radius.pill,
    paddingHorizontal: space.sm,
    paddingVertical: 3,
    borderWidth: 1,
    borderColor: 'rgba(52,211,153,0.2)',
  },

  statusBadgeOffline: {
    backgroundColor: colors.accent.errorDim,
    borderColor: 'rgba(248,113,113,0.2)',
  },

  statusBadgeDot: {
    width: 6,
    height: 6,
    borderRadius: 3,
    backgroundColor: colors.accent.success,
  },

  statusBadgeDotOffline: {
    backgroundColor: colors.accent.error,
  },

  statusBadgeText: {
    fontFamily: font.semibold,
    fontSize: 10,
    color: colors.accent.success,
    letterSpacing: 0.3,
  },

  statusBadgeTextOffline: {
    color: colors.accent.error,
  },

  // ═══════════════════════════════════════════
  // RADAR SECTION
  // ═══════════════════════════════════════════

  radarContainer: {
    alignItems: 'center',
    justifyContent: 'center',
    paddingVertical: space.lg,
    height: 260,
    marginBottom: space.md,
  },

  radarRing: {
    position: 'absolute',
    borderWidth: 1,
    borderColor: colors.border.subtle,
    borderRadius: 999,
  },

  deviceDot: {
    position: 'absolute',
    width: 14,
    height: 14,
    borderRadius: 7,
  },

  deviceDotLAN: {
    backgroundColor: colors.accent.success,
    ...shadows.glow(colors.accent.success),
  },

  deviceDotCloud: {
    backgroundColor: colors.accent.warning,
    ...shadows.glow(colors.accent.warning),
  },

  deviceDotOffline: {
    backgroundColor: colors.text.disabled,
  },

  deviceLabel: {
    position: 'absolute',
    fontFamily: font.medium,
    fontSize: 9,
    color: colors.text.secondary,
    letterSpacing: 0.2,
  },

  centerDot: {
    width: 10,
    height: 10,
    borderRadius: 5,
    backgroundColor: colors.accent.primary,
    ...shadows.glow(colors.accent.primary),
  },

  // ═══════════════════════════════════════════
  // CONNECTION QUALITY CARDS
  // ═══════════════════════════════════════════

  qualitySection: {
    paddingHorizontal: space.md,
    marginTop: space.md,
  },

  qualitySectionTitle: {
    ...typography.overline,
    marginBottom: space.md,
  },

  qualityScrollContent: {
    paddingRight: space.lg,
  },

  qualityCard: {
    backgroundColor: colors.bg.card,
    borderRadius: radius.lg,
    padding: space.md,
    marginRight: space.sm,
    width: 160,
    borderWidth: 1,
    borderColor: colors.border.subtle,
    ...shadows.card,
  },

  qualityCardOnline: {
    borderColor: colors.border.accent,
  },

  qualityCardName: {
    ...typography.cardTitle,
    fontSize: 14,
    marginBottom: space.sm,
  },

  qualityCardTransport: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: space.xs,
    marginBottom: space.sm,
  },

  transportBadge: {
    borderRadius: radius.pill,
    paddingHorizontal: 6,
    paddingVertical: 2,
  },

  transportBadgeLAN: {
    backgroundColor: colors.accent.successDim,
  },

  transportBadgeCloud: {
    backgroundColor: colors.accent.warningDim,
  },

  transportBadgeOffline: {
    backgroundColor: colors.accent.errorDim,
  },

  transportBadgeText: {
    fontFamily: font.bold,
    fontSize: 9,
    letterSpacing: 0.5,
    textTransform: 'uppercase',
  },

  transportBadgeTextLAN: {
    color: colors.accent.success,
  },

  transportBadgeTextCloud: {
    color: colors.accent.warning,
  },

  transportBadgeTextOffline: {
    color: colors.accent.error,
  },

  qualityCardLatency: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: space.xs,
  },

  latencyDot: {
    width: 6,
    height: 6,
    borderRadius: 3,
  },

  latencyDotGood: {
    backgroundColor: colors.accent.success,
  },

  latencyDotMedium: {
    backgroundColor: colors.accent.warning,
  },

  latencyDotBad: {
    backgroundColor: colors.accent.error,
  },

  latencyText: {
    fontFamily: font.medium,
    fontSize: 11,
    color: colors.text.secondary,
    letterSpacing: 0.3,
  },

  qualityCardLastSeen: {
    ...typography.caption,
    color: colors.text.tertiary,
    marginTop: space.xs,
  },

  // ═══════════════════════════════════════════
  // NETWORK STATS ROW
  // ═══════════════════════════════════════════

  statsSection: {
    marginTop: space.lg,
    paddingHorizontal: space.md,
  },

  statsSectionTitle: {
    ...typography.overline,
    marginBottom: space.md,
  },

  statsRow: {
    flexDirection: 'row',
    justifyContent: 'space-around',
    backgroundColor: colors.bg.card,
    borderRadius: radius.lg,
    borderWidth: 1,
    borderColor: colors.border.subtle,
    paddingVertical: space.lg,
    ...shadows.card,
  },

  statBadge: {
    alignItems: 'center',
    padding: space.sm,
    borderRadius: radius.md,
    minWidth: 80,
  },

  statValue: {
    fontFamily: font.extrabold,
    fontSize: 20,
    color: colors.accent.primary,
    letterSpacing: -0.5,
  },

  statValueSuccess: {
    color: colors.accent.success,
  },

  statLabel: {
    ...typography.overline,
    marginTop: 2,
  },

  statDivider: {
    width: 1,
    height: 32,
    backgroundColor: colors.border.subtle,
    alignSelf: 'center',
  },

  // ═══════════════════════════════════════════
  // SPEED TEST
  // ═══════════════════════════════════════════

  speedTestSection: {
    marginTop: space.lg,
    paddingHorizontal: space.md,
  },

  speedTestBtn: {
    borderRadius: radius.lg,
    overflow: 'hidden',
    ...shadows.glow(colors.accent.primary),
  },

  speedTestBtnGradient: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'center',
    paddingVertical: space.lg,
    gap: space.sm,
  },

  speedTestBtnText: {
    fontFamily: font.bold,
    fontSize: 15,
    color: '#FFFFFF',
    letterSpacing: 0.3,
  },

  speedTestResult: {
    backgroundColor: colors.bg.card,
    borderRadius: radius.lg,
    borderWidth: 1,
    borderColor: colors.border.accent,
    padding: space.lg,
    marginTop: space.md,
    alignItems: 'center',
    ...shadows.card,
  },

  speedTestResultTitle: {
    ...typography.overline,
    marginBottom: space.sm,
  },

  speedTestResultValue: {
    fontFamily: font.extrabold,
    fontSize: 32,
    color: colors.accent.primary,
    letterSpacing: -1,
  },

  speedTestResultUnit: {
    fontFamily: font.medium,
    fontSize: 13,
    color: colors.text.secondary,
    marginTop: 2,
  },

  speedTestResultLatency: {
    fontFamily: font.medium,
    fontSize: 12,
    color: colors.accent.success,
    marginTop: space.sm,
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
    fontSize: 48,
    marginBottom: space.xl,
    opacity: 0.5,
  },

  emptyTitle: {
    fontFamily: font.semibold,
    fontSize: 16,
    color: colors.text.primary,
    marginBottom: space.sm,
    textAlign: 'center',
  },

  emptySubtitle: {
    fontFamily: font.regular,
    fontSize: 13,
    color: colors.text.secondary,
    textAlign: 'center',
    lineHeight: 18,
    maxWidth: 260,
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

/** @deprecated Use createNetworkStyles(colors, shadows) for theme support */
export const networkStyles = createNetworkStyles(defaultColors, defaultShadows);
