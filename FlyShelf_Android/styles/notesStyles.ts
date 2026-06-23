import { StyleSheet, Platform } from 'react-native';
import { colors, font, radius, space, shadows } from './theme';

export const styles = StyleSheet.create({
  // ═══ Layout ═══
  container: {
    flex: 1,
    backgroundColor: colors.bg.base,
  },
  gradient: {
    flex: 1,
  },

  // ═══ Header ═══
  header: {
    paddingTop: 15,
    paddingHorizontal: space['2xl'],
    paddingBottom: space.md,
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
  headerRight: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: space.sm,
  },
  syncIndicator: {
    flexDirection: 'row',
    alignItems: 'center',
    backgroundColor: colors.bg.card,
    paddingHorizontal: space.sm,
    paddingVertical: 4,
    borderRadius: radius.pill,
    borderWidth: 1,
    borderColor: colors.border.subtle,
    gap: 4,
  },
  syncDot: {
    width: 5,
    height: 5,
    borderRadius: 3,
  },
  syncText: {
    fontFamily: font.semibold,
    fontSize: 9,
    letterSpacing: 0.8,
    textTransform: 'uppercase',
    color: colors.text.tertiary,
  },

  // ═══ Mode Toggle ═══
  modeToggle: {
    flexDirection: 'row',
    backgroundColor: colors.bg.card,
    borderRadius: radius.pill,
    borderWidth: 1,
    borderColor: colors.border.subtle,
    padding: 3,
  },
  modeButton: {
    paddingHorizontal: space.md,
    paddingVertical: 6,
    borderRadius: radius.pill,
  },
  modeButtonActive: {
    backgroundColor: colors.accent.primary,
  },
  modeButtonText: {
    fontFamily: font.semibold,
    fontSize: 11,
    color: colors.text.tertiary,
    letterSpacing: 0.2,
  },
  modeButtonTextActive: {
    color: '#FFFFFF',
  },

  // ═══ Day Selector (Horizontal Scrollable) ═══
  daySelectorContainer: {
    paddingHorizontal: space.lg,
    marginBottom: space.md,
  },
  daySelectorScroll: {
    paddingVertical: space.xs,
  },
  dayChip: {
    alignItems: 'center',
    justifyContent: 'center',
    paddingHorizontal: space.md,
    paddingVertical: space.sm,
    marginRight: space.sm,
    borderRadius: radius.md,
    backgroundColor: colors.bg.card,
    borderWidth: 1,
    borderColor: colors.border.subtle,
    minWidth: 52,
  },
  dayChipActive: {
    backgroundColor: colors.accent.primaryDim,
    borderColor: colors.accent.primary,
  },
  dayChipToday: {
    borderColor: colors.border.accent,
  },
  dayChipNumber: {
    fontFamily: font.bold,
    fontSize: 16,
    color: colors.text.secondary,
    lineHeight: 20,
  },
  dayChipNumberActive: {
    color: colors.accent.primary,
  },
  dayChipMonth: {
    fontFamily: font.medium,
    fontSize: 9,
    color: colors.text.tertiary,
    textTransform: 'uppercase',
    letterSpacing: 0.8,
    marginTop: 1,
  },
  dayChipMonthActive: {
    color: colors.accent.primary,
  },
  dayChipDot: {
    width: 4,
    height: 4,
    borderRadius: 2,
    backgroundColor: colors.accent.primary,
    marginTop: 3,
  },

  // ═══ Content Area ═══
  contentArea: {
    flex: 1,
    paddingHorizontal: space.lg,
  },
  listContent: {
    paddingBottom: 120,
  },

  // ═══ Bullet Card ═══
  bulletCard: {
    backgroundColor: colors.bg.card,
    borderRadius: radius.lg,
    marginBottom: space.md,
    borderWidth: 1,
    borderColor: colors.border.subtle,
    borderTopWidth: 1,
    borderTopColor: colors.innerHighlight,
    overflow: 'hidden',
    ...shadows.card,
  },
  bulletCardInner: {
    flexDirection: 'row',
  },
  colorStrip: {
    width: 4,
    borderTopLeftRadius: radius.lg,
    borderBottomLeftRadius: radius.lg,
  },
  bulletBody: {
    flex: 1,
    padding: space.md,
  },

  // ═══ Bullet Header ═══
  bulletHeader: {
    flexDirection: 'row',
    alignItems: 'center',
    marginBottom: space.xs,
  },
  bulletHeaderInput: {
    flex: 1,
    fontFamily: font.semibold,
    fontSize: 15,
    color: colors.text.primary,
    letterSpacing: -0.1,
    padding: 0,
    lineHeight: 20,
  },
  pinIndicator: {
    paddingHorizontal: space.xs,
    paddingVertical: 2,
  },
  pinText: {
    fontSize: 14,
  },

  // ═══ Bullet Content ═══
  bulletContent: {
    fontFamily: font.regular,
    fontSize: 14,
    color: colors.text.secondary,
    lineHeight: 20,
    padding: 0,
    marginBottom: space.sm,
    minHeight: 20,
  },

  // ═══ Tags ═══
  bulletMeta: {
    flexDirection: 'row',
    alignItems: 'center',
    flexWrap: 'wrap',
    gap: space.xs,
    marginBottom: space.sm,
  },
  tagPill: {
    backgroundColor: colors.accent.primaryDim,
    paddingHorizontal: space.sm,
    paddingVertical: 3,
    borderRadius: radius.pill,
    borderWidth: 1,
    borderColor: 'rgba(99,132,255,0.15)',
  },
  tagPillText: {
    fontFamily: font.medium,
    fontSize: 10,
    color: colors.accent.primary,
    letterSpacing: 0.3,
  },
  tagRemove: {
    marginLeft: 3,
    fontSize: 10,
    color: colors.text.tertiary,
  },
  addTagButton: {
    paddingHorizontal: space.sm,
    paddingVertical: 3,
    borderRadius: radius.pill,
    borderWidth: 1,
    borderColor: colors.border.subtle,
    borderStyle: 'dashed',
  },
  addTagText: {
    fontFamily: font.medium,
    fontSize: 10,
    color: colors.text.tertiary,
  },
  tagInput: {
    fontFamily: font.medium,
    fontSize: 10,
    color: colors.text.primary,
    paddingHorizontal: space.sm,
    paddingVertical: 3,
    borderRadius: radius.pill,
    backgroundColor: colors.bg.input,
    borderWidth: 1,
    borderColor: colors.border.medium,
    minWidth: 60,
    padding: 0,
  },

  // ═══ Color Dot ═══
  colorDot: {
    width: 8,
    height: 8,
    borderRadius: 4,
    marginRight: space.xs,
  },

  // ═══ Sub-bullets ═══
  subBulletsContainer: {
    marginTop: space.xs,
    paddingLeft: space.xs,
    borderLeftWidth: 1,
    borderLeftColor: colors.border.subtle,
    marginLeft: space.xs,
  },
  subBulletRow: {
    flexDirection: 'row',
    alignItems: 'center',
    paddingVertical: 4,
    gap: space.sm,
  },
  subBulletCheckbox: {
    width: 18,
    height: 18,
    borderRadius: 5,
    borderWidth: 1.5,
    borderColor: colors.border.medium,
    justifyContent: 'center',
    alignItems: 'center',
    backgroundColor: colors.bg.input,
  },
  subBulletCheckboxDone: {
    backgroundColor: colors.accent.primaryDim,
    borderColor: colors.accent.primary,
  },
  subBulletCheckmark: {
    fontSize: 10,
    color: colors.accent.primary,
  },
  subBulletText: {
    flex: 1,
    fontFamily: font.regular,
    fontSize: 13,
    color: colors.text.secondary,
    padding: 0,
    lineHeight: 18,
  },
  subBulletTextDone: {
    textDecorationLine: 'line-through',
    color: colors.text.tertiary,
  },
  addSubBulletButton: {
    flexDirection: 'row',
    alignItems: 'center',
    paddingVertical: 4,
    gap: space.xs,
    marginTop: 2,
  },
  addSubBulletText: {
    fontFamily: font.medium,
    fontSize: 11,
    color: colors.text.tertiary,
  },

  // ═══ Bullet Footer (time) ═══
  bulletFooter: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
    marginTop: space.xs,
  },
  bulletTime: {
    fontFamily: font.medium,
    fontSize: 10,
    color: colors.text.tertiary,
    letterSpacing: 0.3,
  },
  bulletActions: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: space.sm,
  },
  bulletActionButton: {
    padding: space.xs,
  },
  bulletActionText: {
    fontSize: 14,
  },

  // ═══ Freeform Card ═══
  freeformCard: {
    backgroundColor: colors.bg.card,
    borderRadius: radius.lg,
    marginBottom: space.md,
    padding: space.lg,
    borderWidth: 1,
    borderColor: colors.border.subtle,
    borderTopWidth: 1,
    borderTopColor: colors.innerHighlight,
    ...shadows.card,
  },
  freeformInput: {
    fontFamily: font.regular,
    fontSize: 14,
    color: colors.text.primary,
    lineHeight: 22,
    padding: 0,
    minHeight: 80,
    textAlignVertical: 'top',
  },
  freeformMeta: {
    flexDirection: 'row',
    justifyContent: 'flex-end',
    marginTop: space.sm,
  },
  freeformTime: {
    fontFamily: font.medium,
    fontSize: 10,
    color: colors.text.tertiary,
    letterSpacing: 0.3,
  },
  addSectionButton: {
    alignSelf: 'center',
    flexDirection: 'row',
    alignItems: 'center',
    gap: space.xs,
    paddingHorizontal: space.lg,
    paddingVertical: space.sm,
    borderRadius: radius.pill,
    borderWidth: 1,
    borderColor: colors.border.subtle,
    borderStyle: 'dashed',
    marginBottom: space.md,
  },
  addSectionText: {
    fontFamily: font.medium,
    fontSize: 11,
    color: colors.text.tertiary,
  },

  // ═══ Floating Action Button ═══
  fab: {
    position: 'absolute',
    bottom: 90,
    right: space['2xl'],
    width: 52,
    height: 52,
    borderRadius: 26,
    backgroundColor: colors.accent.primary,
    justifyContent: 'center',
    alignItems: 'center',
    ...shadows.elevated,
  },
  fabText: {
    fontSize: 24,
    color: '#FFFFFF',
    fontFamily: font.bold,
    marginTop: -1,
  },

  // ═══ Empty State ═══
  emptyState: {
    flex: 1,
    justifyContent: 'center',
    alignItems: 'center',
    paddingTop: 80,
  },
  emptyIcon: {
    fontSize: 48,
    marginBottom: space.lg,
  },
  emptyTitle: {
    fontFamily: font.semibold,
    fontSize: 17,
    color: colors.text.secondary,
    marginBottom: space.sm,
  },
  emptySubtitle: {
    fontFamily: font.regular,
    fontSize: 13,
    color: colors.text.tertiary,
    textAlign: 'center',
    lineHeight: 20,
  },

  // ═══ Delete Overlay ═══
  deleteOverlay: {
    position: 'absolute',
    top: 0,
    left: 0,
    right: 0,
    bottom: 0,
    backgroundColor: 'rgba(248,113,113,0.06)',
    borderRadius: radius.lg,
    justifyContent: 'center',
    alignItems: 'center',
  },
  deleteOverlayRow: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: space.sm,
  },
  deleteButton: {
    backgroundColor: colors.accent.errorDim,
    paddingHorizontal: space.lg,
    paddingVertical: space.sm,
    borderRadius: radius.pill,
    borderWidth: 1,
    borderColor: 'rgba(248,113,113,0.25)',
  },
  deleteButtonText: {
    fontFamily: font.semibold,
    fontSize: 12,
    color: colors.accent.error,
  },
  cancelButton: {
    paddingHorizontal: space.lg,
    paddingVertical: space.sm,
    borderRadius: radius.pill,
    borderWidth: 1,
    borderColor: colors.border.subtle,
  },
  cancelButtonText: {
    fontFamily: font.semibold,
    fontSize: 12,
    color: colors.text.secondary,
  },

  // ═══ Color Picker ═══
  colorRow: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: space.xs,
    marginTop: space.xs,
  },
  colorOption: {
    width: 16,
    height: 16,
    borderRadius: 8,
    borderWidth: 1.5,
    borderColor: 'transparent',
  },
  colorOptionSelected: {
    borderColor: colors.text.primary,
  },
});
