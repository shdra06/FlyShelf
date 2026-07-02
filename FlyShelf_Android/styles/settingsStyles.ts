// Settings Tab Styles — Extracted from inline styles
import { StyleSheet, Platform } from 'react-native';
import { colors, font, radius, space, shadows, component } from './theme';

export default StyleSheet.create({
  // ── Layout ──
  container: { flex: 1, backgroundColor: 'transparent' },
  scrollContent: { paddingBottom: component.tabBarHeight + 20 },

  // ── Cards ──
  card: {
    backgroundColor: colors.bg.card,
    marginHorizontal: space.xl,
    borderRadius: radius.xl,
    padding: space['2xl'],
    borderWidth: 1,
    borderColor: colors.border.subtle,
    borderTopColor: colors.innerHighlight,
    ...shadows.card,
  },
  cardSpacing: { marginTop: space.lg },

  // ── Section Headers ──
  sectionHeader: {
    color: colors.text.primary,
    fontSize: 17,
    fontFamily: font.semibold,
    marginBottom: space.xl,
    letterSpacing: -0.2,
  },
  sectionRow: {
    flexDirection: 'row',
    alignItems: 'center',
    marginBottom: space.xl,
    gap: space.sm,
  },
  sectionIcon: {
    width: 32, height: 32, borderRadius: radius.sm,
    alignItems: 'center', justifyContent: 'center',
  },

  // ── Inputs ──
  inputContainer: { marginBottom: 10 },
  inputHeaderRow: {
    flexDirection: 'row',
    alignItems: 'center',
    marginBottom: space.md,
  },
  inputLabel: {
    color: colors.text.primary,
    fontSize: 14,
    fontFamily: font.semibold,
    marginLeft: space.sm,
  },
  input: {
    backgroundColor: colors.bg.input,
    color: colors.text.primary,
    fontSize: 16,
    fontFamily: font.medium,
    borderRadius: radius.md,
    paddingHorizontal: space.lg,
    paddingVertical: 14,
    borderWidth: 1,
    borderColor: colors.border.subtle,
  },
  helperText: {
    color: colors.text.tertiary,
    fontSize: 12,
    fontFamily: font.regular,
    marginTop: 10,
    lineHeight: 18,
  },

  // ── Buttons ──
  saveButton: {
    backgroundColor: colors.accent.primary,
    paddingVertical: 16,
    borderRadius: radius.lg,
    alignItems: 'center',
    marginHorizontal: space.xl,
    marginBottom: space.xl,
    ...shadows.glow(colors.accent.primary),
  },
  saveButtonText: {
    color: '#FFFFFF',
    fontSize: 15,
    fontFamily: font.bold,
    letterSpacing: 0.3,
  },

  // ── Toggle Row ──
  toggleRow: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
    paddingVertical: space.md,
  },
  toggleLabel: {
    fontFamily: font.semibold,
    fontSize: 14,
    color: colors.text.primary,
    flex: 1,
    marginRight: space.md,
  },
  toggleSubLabel: {
    fontFamily: font.regular,
    fontSize: 12,
    color: colors.text.tertiary,
    marginTop: 2,
  },

  // ── Slider ──
  sliderContainer: { marginTop: space.sm },
  sliderRow: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 10,
  },
  sliderBtn: {
    width: 36, height: 36, borderRadius: 10,
    backgroundColor: colors.bg.cardHover,
    alignItems: 'center', justifyContent: 'center',
  },
  sliderBtnText: {
    color: colors.text.primary,
    fontSize: 18,
    fontFamily: font.extrabold,
  },
  sliderTrack: {
    flex: 1, height: 8,
    backgroundColor: colors.bg.cardHover,
    borderRadius: 4, overflow: 'hidden',
  },
  sliderFill: {
    height: '100%',
    backgroundColor: colors.accent.primary,
    borderRadius: 4,
  },

  // ── Device / Pairing ──
  deviceRow: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
    paddingVertical: space.md,
    borderBottomWidth: 1,
    borderBottomColor: colors.border.subtle,
  },
  deviceName: {
    fontFamily: font.medium,
    fontSize: 14,
    color: colors.text.primary,
  },
  deviceMeta: {
    fontFamily: font.regular,
    fontSize: 12,
    color: colors.text.tertiary,
  },

  // ── Update Section ──
  updateRow: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: space.md,
    marginTop: space.md,
  },
  updateBadge: {
    paddingHorizontal: space.md,
    paddingVertical: space.xs,
    borderRadius: radius.pill,
  },
  updateBadgeText: {
    fontFamily: font.bold,
    fontSize: 11,
  },

  // ── Network Log Viewer ──
  logViewer: {
    backgroundColor: colors.bg.base,
    borderRadius: radius.md,
    borderWidth: 1,
    borderColor: colors.border.subtle,
    marginTop: space.md,
    overflow: 'hidden',
  },
  logHeader: {
    flexDirection: 'row',
    justifyContent: 'space-between',
    alignItems: 'center',
    paddingHorizontal: space.md,
    paddingVertical: space.sm,
    backgroundColor: colors.bg.card,
    borderBottomWidth: 1,
    borderBottomColor: colors.border.subtle,
  },
  logHeaderText: {
    color: colors.text.tertiary,
    fontSize: 10,
    fontFamily: Platform.OS === 'ios' ? 'Menlo' : 'monospace',
    fontWeight: '600',
  },
  logRefresh: {
    color: colors.accent.primary,
    fontSize: 10,
    fontFamily: font.bold,
  },
  logScroll: {
    maxHeight: 300,
    paddingHorizontal: space.md,
    paddingVertical: space.sm,
  },
  logEmpty: {
    color: colors.text.disabled,
    fontSize: 12,
    fontStyle: 'italic',
    textAlign: 'center',
    paddingVertical: 20,
    fontFamily: Platform.OS === 'ios' ? 'Menlo' : 'monospace',
  },
  logEntry: {
    fontSize: 10,
    fontFamily: Platform.OS === 'ios' ? 'Menlo' : 'monospace',
    lineHeight: 16,
    marginBottom: 2,
  },

  // ── Divider ──
  divider: {
    height: 1,
    backgroundColor: colors.border.subtle,
    marginVertical: space.lg,
  },
});
