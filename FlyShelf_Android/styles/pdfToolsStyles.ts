// PDF Tools Styles — Theme-aware factory
import { StyleSheet, Dimensions } from 'react-native';
import { colors as defaultColors, font, radius, space, shadows as defaultShadows } from './theme';

type ThemeColors = Record<string, any>;
type ThemeShadows = Record<string, any>;

const { width: SCREEN_W } = Dimensions.get('window');
const CARD_GAP = 12;
const CARD_W = (SCREEN_W - space.xl * 2 - CARD_GAP) / 2;

export const createPdfToolsStyles = (colors: ThemeColors, shadows: ThemeShadows) => StyleSheet.create({
  // ── Layout ──
  safe: { flex: 1, backgroundColor: colors.bg.base },
  container: { flex: 1 },
  scroll: { flex: 1 },
  scrollContent: { padding: space.xl, paddingBottom: 40 },
  pb100: { paddingBottom: 100 },

  // ── Header ──
  header: {
    flexDirection: 'row', alignItems: 'center',
    paddingHorizontal: space.xl, paddingTop: 52, paddingBottom: space.lg,
    backgroundColor: colors.bg.base,
  },
  backBtn: { padding: space.sm, marginRight: space.sm, borderRadius: radius.sm },
  headerTitle: { fontFamily: font.bold, fontSize: 24, color: colors.text.primary, letterSpacing: -0.5 },
  headerRight: { marginLeft: 'auto' as any, flexDirection: 'row' as any, gap: space.sm },

  // ── Tool Grid ──
  toolGrid: {
    flexDirection: 'row', flexWrap: 'wrap',
    gap: CARD_GAP, marginBottom: space['2xl'],
  },
  toolCard: {
    width: CARD_W,
    paddingVertical: 18,
    paddingHorizontal: 14,
    backgroundColor: colors.bg.card,
    borderRadius: 20,
    borderWidth: 1,
    borderColor: colors.border.subtle,
    alignItems: 'center',
    justifyContent: 'center',
    minHeight: 130,
    ...shadows.card,
  },
  toolCardActive: { borderColor: colors.accent.primary, backgroundColor: colors.bg.cardHover },
  toolIconWrap: {
    width: 48, height: 48, borderRadius: 16,
    alignItems: 'center', justifyContent: 'center', marginBottom: 10,
  },
  toolLabel: { fontFamily: font.bold, fontSize: 14, color: colors.text.primary, textAlign: 'center', letterSpacing: -0.2 },
  toolDesc: { fontFamily: font.medium, fontSize: 11, color: colors.text.tertiary, textAlign: 'center', marginTop: 3 },

  // ── Modal ──
  modalOverlay: {
    flex: 1, backgroundColor: colors.bg.base,
  },
  modalHeader: {
    flexDirection: 'row', alignItems: 'center',
    paddingHorizontal: space.xl, paddingTop: 52, paddingBottom: space.lg,
    borderBottomWidth: 1, borderBottomColor: colors.border.subtle,
  },
  modalTitle: { fontFamily: font.bold, fontSize: 20, color: colors.text.primary, flex: 1 },
  modalScroll: { flex: 1, padding: space.xl },
  modalActions: {
    flexDirection: 'row', gap: space.md,
    padding: space.xl, paddingBottom: 36,
    borderTopWidth: 1, borderTopColor: colors.border.subtle,
  },

  // ── Buttons ──
  btnPrimary: {
    flex: 1, backgroundColor: colors.accent.primary,
    paddingVertical: 14, borderRadius: radius.md,
    alignItems: 'center', justifyContent: 'center',
  },
  btnPrimaryText: { fontFamily: font.semibold, fontSize: 15, color: '#fff' },
  btnSecondary: {
    flex: 1, backgroundColor: colors.bg.card,
    paddingVertical: 14, borderRadius: radius.md,
    borderWidth: 1, borderColor: colors.border.medium,
    alignItems: 'center', justifyContent: 'center',
  },
  btnSecondaryText: { fontFamily: font.semibold, fontSize: 15, color: colors.text.secondary },
  btnDanger: {
    backgroundColor: colors.accent.errorDim,
    paddingVertical: 8, paddingHorizontal: 12, borderRadius: radius.sm,
    alignItems: 'center',
  },
  btnDangerText: { fontFamily: font.medium, fontSize: 12, color: colors.accent.error },
  btnDisabled: { opacity: 0.4 },
  btnSmall: {
    padding: space.xs, borderRadius: radius.sm,
    backgroundColor: colors.bg.card, borderWidth: 1, borderColor: colors.border.subtle,
  },

  // ── File Item ──
  fileItem: {
    flexDirection: 'row', alignItems: 'center',
    backgroundColor: colors.bg.card, borderRadius: radius.md,
    padding: space.md, marginBottom: space.sm,
    borderWidth: 1, borderColor: colors.border.subtle,
  },
  fileItemSelected: { borderColor: colors.accent.primary },
  fileIcon: { marginRight: space.md },
  fileInfo: { flex: 1 },
  fileName: { fontFamily: font.medium, fontSize: 14, color: colors.text.primary },
  fileMeta: { fontFamily: font.regular, fontSize: 11, color: colors.text.tertiary, marginTop: 2 },
  fileActions: { flexDirection: 'row', gap: space.xs },

  // ── Page Card ──
  pageCard: {
    flexDirection: 'row', alignItems: 'center',
    backgroundColor: colors.bg.card, borderRadius: radius.md,
    padding: space.md, marginBottom: space.sm,
    borderWidth: 1, borderColor: colors.border.subtle,
  },
  pageNum: {
    width: 36, height: 36, borderRadius: radius.sm,
    backgroundColor: colors.accent.primaryDim,
    alignItems: 'center', justifyContent: 'center', marginRight: space.md,
  },
  pageNumText: { fontFamily: font.bold, fontSize: 14, color: colors.accent.primary },
  pageInfo: { flex: 1 },
  pageSize: { fontFamily: font.regular, fontSize: 12, color: colors.text.secondary },
  pageRotation: { fontFamily: font.regular, fontSize: 11, color: colors.text.tertiary },
  pageActions: { flexDirection: 'row', gap: 6 },

  // ── Checkbox ──
  checkbox: {
    width: 22, height: 22, borderRadius: 6,
    borderWidth: 2, borderColor: colors.border.medium,
    alignItems: 'center', justifyContent: 'center', marginRight: space.md,
  },
  checkboxChecked: { backgroundColor: colors.accent.primary, borderColor: colors.accent.primary },

  // ── Form ──
  label: { fontFamily: font.medium, fontSize: 13, color: colors.text.secondary, marginBottom: space.xs, marginTop: space.lg },
  input: {
    backgroundColor: colors.bg.input, borderRadius: radius.md,
    borderWidth: 1, borderColor: colors.border.subtle,
    padding: space.md, fontFamily: font.regular, fontSize: 14, color: colors.text.primary,
  },
  inputRow: { flexDirection: 'row', alignItems: 'center', gap: space.sm },
  sliderRow: {
    flexDirection: 'row', alignItems: 'center',
    marginTop: space.sm, gap: space.md,
  },
  sliderLabel: { fontFamily: font.medium, fontSize: 12, color: colors.text.tertiary, width: 40 },
  sliderTrack: {
    flex: 1, height: 6, backgroundColor: colors.bg.input,
    borderRadius: 3, overflow: 'hidden',
  },
  sliderFill: { height: 6, backgroundColor: colors.accent.primary, borderRadius: 3 },

  // ── Success / Result ──
  resultContainer: { alignItems: 'center', paddingVertical: space['3xl'] },
  resultIcon: { marginBottom: space.xl },
  resultTitle: { fontFamily: font.bold, fontSize: 20, color: colors.accent.success, marginBottom: space.sm },
  resultFile: { fontFamily: font.regular, fontSize: 13, color: colors.text.secondary, marginBottom: space.xl, textAlign: 'center' },
  resultActions: { flexDirection: 'row', gap: space.md, width: '100%' },

  // ── Loading ──
  loadingOverlay: {
    ...StyleSheet.absoluteFillObject,
    backgroundColor: 'rgba(0,0,0,0.7)',
    alignItems: 'center', justifyContent: 'center',
    zIndex: 100,
  },
  loadingText: { fontFamily: font.medium, fontSize: 14, color: colors.text.secondary, marginTop: space.lg },

  // ── Empty State ──
  emptyState: { alignItems: 'center', paddingVertical: space['3xl'] },
  emptyText: { fontFamily: font.medium, fontSize: 14, color: colors.text.tertiary, marginTop: space.md, textAlign: 'center' },

  // ── Recent PDFs ──
  recentSection: { marginTop: space['2xl'] },
  sectionTitle: { fontFamily: font.semibold, fontSize: 15, color: colors.text.primary, marginBottom: space.md },
  recentItem: {
    flexDirection: 'row', alignItems: 'center',
    backgroundColor: colors.bg.card, borderRadius: radius.md,
    padding: space.md, marginBottom: space.sm,
    borderWidth: 1, borderColor: colors.border.subtle,
  },
  recentIcon: {
    width: 36, height: 36, borderRadius: radius.sm,
    backgroundColor: 'rgba(248,113,113,0.12)',
    alignItems: 'center', justifyContent: 'center', marginRight: space.md,
  },
  recentInfo: { flex: 1 },
  recentName: { fontFamily: font.medium, fontSize: 13, color: colors.text.primary },
  recentMeta: { fontFamily: font.regular, fontSize: 11, color: colors.text.tertiary, marginTop: 2 },

  // ── Misc ──
  divider: { height: 1, backgroundColor: colors.border.subtle, marginVertical: space.xl },
  row: { flexDirection: 'row', alignItems: 'center' },
  flex1: { flex: 1 },
  gap8: { gap: 8 },
  mt8: { marginTop: 8 },
  mt16: { marginTop: 16 },
  mt20: { marginTop: 20 },
  mb16: { marginBottom: 16 },
  ph20: { paddingHorizontal: 20 },

  // ── Info Tool ──
  infoCard: {
    backgroundColor: colors.bg.card, borderRadius: radius.lg,
    padding: space.xl, borderWidth: 1, borderColor: colors.border.subtle,
  },
  infoRow: {
    flexDirection: 'row', justifyContent: 'space-between',
    alignItems: 'center', paddingVertical: space.sm,
    borderBottomWidth: 1, borderBottomColor: colors.border.subtle,
  },
  infoLabel: { fontFamily: font.medium, fontSize: 13, color: colors.text.secondary },
  infoValue: { fontFamily: font.semibold, fontSize: 13, color: colors.text.primary, textAlign: 'right' as any, flex: 1, marginLeft: space.md },

  // ── Password Tool ──
  inputWrapper: {
    flexDirection: 'row', alignItems: 'center',
    backgroundColor: colors.bg.input, borderRadius: radius.md,
    borderWidth: 1, borderColor: colors.border.subtle,
  },
  inputAction: {
    padding: space.md, justifyContent: 'center', alignItems: 'center',
  },
});

/** @deprecated Use createPdfToolsStyles(colors, shadows) for theme support */
export default createPdfToolsStyles(defaultColors, defaultShadows);
