import { StyleSheet, Platform } from 'react-native';
import { colors, font, radius, space, shadows } from './theme';

// ═══════════════════════════════════════════════════════════
// TODO SCREEN — PREMIUM DARK STYLES
// ═══════════════════════════════════════════════════════════

export const todoStyles = StyleSheet.create({
  // ─── Layout ─────────────────────────────────────────────
  container: {
    flex: 1,
    backgroundColor: colors.bg.base,
  },
  header: {
    paddingTop: 15,
    paddingHorizontal: space['2xl'],
    marginBottom: space.md,
    flexDirection: 'row',
    justifyContent: 'space-between',
    alignItems: 'center',
  },
  headerTitle: {
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
    paddingHorizontal: space.md,
    paddingVertical: 6,
    borderRadius: radius.pill,
    borderWidth: 1,
    borderColor: colors.border.subtle,
  },
  syncDot: {
    width: 6,
    height: 6,
    borderRadius: 3,
    marginRight: 6,
  },
  syncText: {
    fontFamily: font.semibold,
    color: colors.text.tertiary,
    fontSize: 10,
    textTransform: 'uppercase',
    letterSpacing: 1,
  },

  // ─── Day Selector ───────────────────────────────────────
  daySelectorContainer: {
    paddingVertical: space.sm,
    paddingLeft: space.xl,
    marginBottom: space.sm,
  },
  dayChip: {
    paddingHorizontal: space.lg,
    paddingVertical: space.sm,
    borderRadius: radius.pill,
    marginRight: space.sm,
    backgroundColor: colors.bg.card,
    borderWidth: 1,
    borderColor: colors.border.subtle,
    alignItems: 'center',
    minWidth: 64,
  },
  dayChipActive: {
    backgroundColor: 'rgba(99,132,255,0.15)',
    borderColor: colors.accent.primary,
  },
  dayChipToday: {
    borderColor: 'rgba(99,132,255,0.3)',
  },
  dayChipDayName: {
    fontFamily: font.medium,
    fontSize: 10,
    letterSpacing: 0.5,
    color: colors.text.tertiary,
    textTransform: 'uppercase',
    marginBottom: 2,
  },
  dayChipDayNameActive: {
    color: colors.accent.primary,
  },
  dayChipDate: {
    fontFamily: font.bold,
    fontSize: 16,
    color: colors.text.secondary,
  },
  dayChipDateActive: {
    color: colors.text.primary,
  },

  // ─── Sidebar (unused but requested) ────────────────────
  sidebar: {
    width: 56,
    backgroundColor: colors.bg.card,
    borderRightWidth: 1,
    borderRightColor: colors.border.subtle,
    paddingTop: space.lg,
    alignItems: 'center',
  },
  sidebarItem: {
    width: 40,
    height: 40,
    borderRadius: radius.sm,
    justifyContent: 'center',
    alignItems: 'center',
    marginBottom: space.sm,
  },
  sidebarItemActive: {
    backgroundColor: 'rgba(99,132,255,0.15)',
    borderWidth: 1,
    borderColor: colors.accent.primary,
  },

  // ─── Todo Card ──────────────────────────────────────────
  todoCard: {
    backgroundColor: colors.bg.card,
    borderRadius: radius.lg,
    marginHorizontal: space.xl,
    marginBottom: space.md,
    borderWidth: 1,
    borderColor: colors.border.subtle,
    borderTopWidth: 1,
    borderTopColor: colors.innerHighlight,
    overflow: 'hidden',
    ...shadows.card,
  },
  todoCardOverdue: {
    borderLeftColor: '#FF4444',
    backgroundColor: 'rgba(255,68,68,0.04)',
  },
  todoCardDone: {
    opacity: 0.6,
  },
  todoCardInner: {
    flexDirection: 'row',
    alignItems: 'flex-start',
    padding: space.lg,
  },

  // ─── Color Strip ────────────────────────────────────────
  colorStrip: {
    position: 'absolute',
    left: 0,
    top: 0,
    bottom: 0,
    width: 3,
    borderTopLeftRadius: radius.lg,
    borderBottomLeftRadius: radius.lg,
  },

  // ─── Checkbox ───────────────────────────────────────────
  checkboxUnchecked: {
    width: 24,
    height: 24,
    borderRadius: 12,
    borderWidth: 2,
    borderColor: colors.accent.primary,
    justifyContent: 'center',
    alignItems: 'center',
    marginRight: space.md,
    marginTop: 1,
  },
  checkboxChecked: {
    width: 24,
    height: 24,
    borderRadius: 12,
    backgroundColor: colors.accent.primary,
    justifyContent: 'center',
    alignItems: 'center',
    marginRight: space.md,
    marginTop: 1,
  },
  checkboxCheckmark: {
    color: '#FFFFFF',
    fontSize: 13,
    fontWeight: '700',
    lineHeight: 14,
  },

  // ─── Todo Text ──────────────────────────────────────────
  todoTextContainer: {
    flex: 1,
  },
  todoText: {
    fontFamily: font.semibold,
    fontSize: 15,
    color: colors.text.primary,
    letterSpacing: -0.1,
    lineHeight: 22,
  },
  todoTextDone: {
    fontFamily: font.regular,
    fontSize: 15,
    color: colors.text.disabled,
    textDecorationLine: 'line-through',
    letterSpacing: -0.1,
    lineHeight: 22,
  },
  todoTextInput: {
    fontFamily: font.semibold,
    fontSize: 15,
    color: colors.text.primary,
    letterSpacing: -0.1,
    lineHeight: 22,
    padding: 0,
    margin: 0,
    minHeight: 22,
  },

  // ─── Metadata Row ───────────────────────────────────────
  metadataRow: {
    flexDirection: 'row',
    alignItems: 'center',
    flexWrap: 'wrap',
    marginTop: 6,
    gap: 6,
  },

  // ─── Priority Badge ─────────────────────────────────────
  priorityBadge: {
    flexDirection: 'row',
    alignItems: 'center',
    paddingHorizontal: 7,
    paddingVertical: 2,
    borderRadius: radius.sm,
    gap: 3,
  },
  priorityDot: {
    width: 6,
    height: 6,
    borderRadius: 3,
  },
  priorityText: {
    fontFamily: font.bold,
    fontSize: 10,
    letterSpacing: 0.4,
  },

  // ─── Due Date Chip ──────────────────────────────────────
  dueDateChip: {
    flexDirection: 'row',
    alignItems: 'center',
    paddingHorizontal: 8,
    paddingVertical: 3,
    borderRadius: radius.sm,
    backgroundColor: 'rgba(255,255,255,0.06)',
    gap: 4,
  },
  dueDateChipOverdue: {
    backgroundColor: 'rgba(255,68,68,0.12)',
  },
  dueDateChipToday: {
    backgroundColor: 'rgba(245,158,11,0.12)',
  },
  dueDateText: {
    fontFamily: font.medium,
    fontSize: 11,
    color: colors.text.secondary,
  },
  dueDateTextOverdue: {
    color: '#FF4444',
  },
  dueDateTextToday: {
    color: '#F59E0B',
  },

  // ─── Recurrence Badge ───────────────────────────────────
  recurrenceBadge: {
    flexDirection: 'row',
    alignItems: 'center',
    paddingHorizontal: 7,
    paddingVertical: 2,
    borderRadius: radius.sm,
    backgroundColor: 'rgba(99,132,255,0.10)',
    gap: 3,
  },
  recurrenceText: {
    fontFamily: font.medium,
    fontSize: 10,
    color: colors.accent.primary,
    letterSpacing: 0.2,
  },

  // ─── Tag Pill ───────────────────────────────────────────
  tagPill: {
    backgroundColor: 'rgba(255,255,255,0.06)',
    paddingHorizontal: 8,
    paddingVertical: 3,
    borderRadius: radius.pill,
    borderWidth: 1,
    borderColor: 'rgba(255,255,255,0.08)',
  },
  tagPillText: {
    fontFamily: font.medium,
    fontSize: 10,
    color: colors.text.secondary,
    letterSpacing: 0.3,
  },

  // ─── Expand Chevron ─────────────────────────────────────
  expandChevron: {
    paddingLeft: space.sm,
    paddingVertical: space.xs,
    justifyContent: 'center',
    alignItems: 'center',
  },
  expandChevronText: {
    fontSize: 16,
    color: colors.text.tertiary,
  },

  // ─── Expanded / Description Area ────────────────────────
  expandedArea: {
    paddingHorizontal: space.lg,
    paddingBottom: space.lg,
    borderTopWidth: 1,
    borderTopColor: colors.border.subtle,
  },
  descriptionArea: {
    backgroundColor: colors.bg.input,
    borderRadius: radius.md,
    padding: space.md,
    marginTop: space.md,
    minHeight: 60,
    borderWidth: 1,
    borderColor: colors.border.subtle,
  },
  descriptionInput: {
    fontFamily: font.regular,
    fontSize: 13,
    color: colors.text.primary,
    lineHeight: 20,
    padding: 0,
    textAlignVertical: 'top',
  },

  // ─── Subtask Section ────────────────────────────────────
  subtaskSection: {
    marginTop: space.md,
  },
  subtaskHeader: {
    flexDirection: 'row',
    justifyContent: 'space-between',
    alignItems: 'center',
    marginBottom: space.sm,
  },
  subtaskLabel: {
    fontFamily: font.semibold,
    fontSize: 12,
    color: colors.text.tertiary,
    letterSpacing: 0.5,
    textTransform: 'uppercase',
  },
  subtaskRow: {
    flexDirection: 'row',
    alignItems: 'center',
    paddingVertical: 6,
    paddingHorizontal: space.xs,
  },
  subtaskCheckbox: {
    width: 18,
    height: 18,
    borderRadius: 9,
    borderWidth: 1.5,
    borderColor: colors.text.tertiary,
    justifyContent: 'center',
    alignItems: 'center',
    marginRight: space.sm,
  },
  subtaskCheckboxDone: {
    backgroundColor: colors.accent.primary,
    borderColor: colors.accent.primary,
  },
  subtaskCheckmark: {
    color: '#FFFFFF',
    fontSize: 10,
    fontWeight: '700',
  },
  subtaskText: {
    fontFamily: font.regular,
    fontSize: 13,
    color: colors.text.secondary,
    flex: 1,
  },
  subtaskTextDone: {
    fontFamily: font.regular,
    fontSize: 13,
    color: colors.text.disabled,
    textDecorationLine: 'line-through',
    flex: 1,
  },
  subtaskTextInput: {
    fontFamily: font.regular,
    fontSize: 13,
    color: colors.text.secondary,
    flex: 1,
    padding: 0,
  },
  subtaskDeleteBtn: {
    padding: 4,
  },
  addSubtaskBtn: {
    flexDirection: 'row',
    alignItems: 'center',
    paddingVertical: 8,
    paddingHorizontal: space.xs,
    gap: 6,
  },
  addSubtaskText: {
    fontFamily: font.medium,
    fontSize: 12,
    color: colors.accent.primary,
  },

  // ─── Progress Bar ───────────────────────────────────────
  progressBar: {
    height: 3,
    backgroundColor: 'rgba(255,255,255,0.06)',
    borderRadius: 2,
    marginTop: space.sm,
    overflow: 'hidden',
  },
  progressFill: {
    height: '100%',
    backgroundColor: colors.accent.primary,
    borderRadius: 2,
  },

  // ─── Action Buttons Row ─────────────────────────────────
  actionRow: {
    flexDirection: 'row',
    alignItems: 'center',
    marginTop: space.md,
    gap: space.sm,
    flexWrap: 'wrap',
  },
  actionBtn: {
    flexDirection: 'row',
    alignItems: 'center',
    paddingHorizontal: space.md,
    paddingVertical: 7,
    borderRadius: radius.sm,
    backgroundColor: colors.bg.input,
    borderWidth: 1,
    borderColor: colors.border.subtle,
    gap: 5,
  },
  actionBtnText: {
    fontFamily: font.medium,
    fontSize: 11,
    color: colors.text.secondary,
  },
  actionBtnDanger: {
    borderColor: 'rgba(255,68,68,0.2)',
    backgroundColor: 'rgba(255,68,68,0.06)',
  },
  actionBtnDangerText: {
    color: '#FF4444',
  },

  // ─── Color Dots ─────────────────────────────────────────
  colorDotsRow: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 6,
    paddingVertical: 4,
  },
  colorDot: {
    width: 18,
    height: 18,
    borderRadius: 9,
    borderWidth: 2,
    borderColor: 'transparent',
  },
  colorDotSelected: {
    borderColor: colors.text.primary,
    borderWidth: 2,
  },

  // ─── Tag Input ──────────────────────────────────────────
  tagInputContainer: {
    flexDirection: 'row',
    alignItems: 'center',
    marginTop: space.sm,
    gap: space.sm,
  },
  tagInput: {
    flex: 1,
    backgroundColor: colors.bg.input,
    borderRadius: radius.sm,
    paddingHorizontal: space.md,
    paddingVertical: 6,
    fontFamily: font.regular,
    fontSize: 12,
    color: colors.text.primary,
    borderWidth: 1,
    borderColor: colors.border.subtle,
  },
  tagAddBtn: {
    paddingHorizontal: space.md,
    paddingVertical: 6,
    borderRadius: radius.sm,
    backgroundColor: 'rgba(99,132,255,0.15)',
  },
  tagAddBtnText: {
    fontFamily: font.semibold,
    fontSize: 12,
    color: colors.accent.primary,
  },

  // ─── Add Button / Bottom Input Bar ──────────────────────
  addButton: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'center',
    paddingHorizontal: space.xl,
    paddingVertical: space.md,
    marginHorizontal: space.xl,
    marginVertical: space.sm,
    borderRadius: radius.md,
    backgroundColor: 'rgba(99,132,255,0.10)',
    borderWidth: 1,
    borderColor: 'rgba(99,132,255,0.2)',
    borderStyle: 'dashed',
  },
  addButtonText: {
    fontFamily: font.medium,
    fontSize: 14,
    color: colors.accent.primary,
    marginLeft: space.sm,
  },

  inputBar: {
    flexDirection: 'row',
    paddingHorizontal: space.xl,
    paddingVertical: 8,
    paddingBottom: Platform.OS === 'ios' ? 30 : 78,
    backgroundColor: colors.bg.card,
    borderTopWidth: 1,
    borderTopColor: colors.border.subtle,
    alignItems: 'flex-end',
  },
  inputBarInput: {
    flex: 1,
    backgroundColor: colors.bg.input,
    color: colors.text.primary,
    fontFamily: font.regular,
    borderRadius: radius.xl,
    paddingHorizontal: space.xl,
    paddingTop: 10,
    paddingBottom: 10,
    fontSize: 15,
    maxHeight: 100,
    borderWidth: 1,
    borderColor: colors.border.subtle,
  },
  inputBarSend: {
    width: 40,
    height: 40,
    borderRadius: 20,
    backgroundColor: colors.accent.primary,
    justifyContent: 'center',
    alignItems: 'center',
    marginLeft: space.md,
    ...shadows.glow(colors.accent.primary),
  },
  inputBarSendDisabled: {
    backgroundColor: colors.bg.input,
    ...shadows.card,
  },

  // ─── FAB ────────────────────────────────────────────────
  fab: {
    position: 'absolute',
    right: space.xl,
    bottom: Platform.OS === 'ios' ? 100 : 85,
    width: 56,
    height: 56,
    borderRadius: 28,
    backgroundColor: colors.accent.primary,
    justifyContent: 'center',
    alignItems: 'center',
    ...shadows.glow(colors.accent.primary),
  },
  fabText: {
    fontSize: 28,
    color: '#FFFFFF',
    fontWeight: '300',
    lineHeight: 30,
  },

  // ─── Empty State ────────────────────────────────────────
  emptyState: {
    flex: 1,
    justifyContent: 'center',
    alignItems: 'center',
    paddingVertical: 60,
    paddingHorizontal: space['3xl'],
  },
  emptyStateIcon: {
    fontSize: 48,
    marginBottom: space.lg,
  },
  emptyStateTitle: {
    fontFamily: font.semibold,
    fontSize: 17,
    color: colors.text.secondary,
    marginBottom: space.sm,
    textAlign: 'center',
  },
  emptyStateSubtitle: {
    fontFamily: font.regular,
    fontSize: 13,
    color: colors.text.tertiary,
    textAlign: 'center',
    lineHeight: 20,
  },

  // ─── Swipe Actions ──────────────────────────────────────
  swipeAction: {
    justifyContent: 'center',
    alignItems: 'center',
    width: 80,
    borderRadius: radius.lg,
  },
  swipeActionDone: {
    backgroundColor: 'rgba(34,197,94,0.15)',
  },
  swipeActionDelete: {
    backgroundColor: 'rgba(255,68,68,0.15)',
  },
  swipeActionText: {
    fontFamily: font.semibold,
    fontSize: 11,
    marginTop: 4,
    letterSpacing: 0.3,
  },

  // ─── Date Picker Modal ──────────────────────────────────
  datePickerOverlay: {
    flex: 1,
    backgroundColor: 'rgba(0,0,0,0.6)',
    justifyContent: 'flex-end',
  },
  datePickerContainer: {
    backgroundColor: colors.bg.elevated,
    borderTopLeftRadius: radius.xl,
    borderTopRightRadius: radius.xl,
    padding: space['2xl'],
    paddingBottom: Platform.OS === 'ios' ? 40 : space['2xl'],
    borderWidth: 1,
    borderColor: colors.border.medium,
  },
  datePickerHeader: {
    flexDirection: 'row',
    justifyContent: 'space-between',
    alignItems: 'center',
    marginBottom: space.lg,
  },
  datePickerTitle: {
    fontFamily: font.semibold,
    fontSize: 17,
    color: colors.text.primary,
  },
  datePickerDoneBtn: {
    paddingHorizontal: space.lg,
    paddingVertical: space.sm,
    borderRadius: radius.sm,
    backgroundColor: colors.accent.primary,
  },
  datePickerDoneText: {
    fontFamily: font.semibold,
    fontSize: 13,
    color: '#FFFFFF',
  },
  datePickerClearBtn: {
    paddingHorizontal: space.lg,
    paddingVertical: space.sm,
    borderRadius: radius.sm,
    backgroundColor: 'rgba(255,68,68,0.1)',
  },
  datePickerClearText: {
    fontFamily: font.semibold,
    fontSize: 13,
    color: '#FF4444',
  },

  // ─── Summary Row ────────────────────────────────────────
  summaryRow: {
    flexDirection: 'row',
    justifyContent: 'space-between',
    alignItems: 'center',
    paddingHorizontal: space.xl,
    paddingVertical: space.sm,
    marginBottom: space.xs,
  },
  summaryText: {
    fontFamily: font.medium,
    fontSize: 12,
    color: colors.text.tertiary,
  },
  summaryCount: {
    fontFamily: font.bold,
    color: colors.accent.primary,
  },

  // ─── List container ─────────────────────────────────────
  listContainer: {
    flex: 1,
  },
  listContent: {
    paddingBottom: 140,
    paddingTop: space.sm,
  },
});
