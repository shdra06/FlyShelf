// PDF Editor Styles — Theme-aware factory
import { StyleSheet, Dimensions } from 'react-native';
import { font, radius, space, shadows as defaultShadows } from './theme';

type ThemeColors = Record<string, any>;
type ThemeShadows = Record<string, any>;

const { width: SCREEN_W, height: SCREEN_H } = Dimensions.get('window');
const GRID_COLS = 3;
const GRID_GAP = space.sm;
const THUMB_W = Math.floor((SCREEN_W - space.xl * 2 - GRID_GAP * (GRID_COLS - 1)) / GRID_COLS);
const THUMB_H = Math.floor(THUMB_W * 1.414); // A4 aspect ratio

export const THUMB_SIZE = { width: THUMB_W, height: THUMB_H };

export const createPdfEditorStyles = (colors: ThemeColors, shadows: ThemeShadows) => StyleSheet.create({
  // ── Layout ──
  container: { flex: 1, backgroundColor: colors.bg.base },

  // ── Top Bar ──
  topBar: {
    flexDirection: 'row',
    alignItems: 'center',
    paddingHorizontal: space.md,
    paddingTop: 48,
    paddingBottom: space.sm,
    backgroundColor: colors.bg.base,
    borderBottomWidth: 1,
    borderBottomColor: colors.border.subtle,
    gap: space.xs,
  },
  topBarBack: {
    padding: space.sm,
    borderRadius: radius.sm,
  },
  topBarTitle: {
    flex: 1,
    fontFamily: font.semibold,
    fontSize: 16,
    color: colors.text.primary,
    marginHorizontal: space.xs,
  },
  topBarSubtitle: {
    fontFamily: font.regular,
    fontSize: 12,
    color: colors.text.tertiary,
  },
  topBarTitleWrap: {
    flex: 1,
    marginHorizontal: space.xs,
  },
  topBarAction: {
    padding: space.sm,
    borderRadius: radius.sm,
  },
  topBarActionDisabled: {
    opacity: 0.3,
  },
  saveBtn: {
    flexDirection: 'row',
    alignItems: 'center',
    backgroundColor: colors.accent.primary,
    paddingHorizontal: space.md,
    paddingVertical: space.sm,
    borderRadius: radius.md,
    gap: 4,
  },
  saveBtnText: {
    fontFamily: font.semibold,
    fontSize: 13,
    color: '#FFFFFF',
  },
  saveBtnDisabled: {
    opacity: 0.5,
  },

  // ── Page Grid ──
  gridContainer: {
    flex: 1,
    paddingHorizontal: space.xl,
    paddingTop: space.md,
  },
  gridContent: {
    paddingBottom: 120,
    gap: GRID_GAP,
  },
  gridRow: {
    flexDirection: 'row',
    gap: GRID_GAP,
  },

  // ── Page Thumbnail Card ──
  pageCard: {
    width: THUMB_W,
    height: THUMB_H,
    backgroundColor: colors.bg.card,
    borderRadius: radius.md,
    borderWidth: 1,
    borderColor: colors.border.subtle,
    overflow: 'hidden',
    ...shadows.card,
  },
  pageCardSelected: {
    borderColor: colors.accent.primary,
    borderWidth: 2,
  },
  pageThumb: {
    width: '100%',
    height: '100%',
    resizeMode: 'contain',
    backgroundColor: colors.bg.card,
  },
  pagePlaceholder: {
    flex: 1,
    alignItems: 'center',
    justifyContent: 'center',
    backgroundColor: colors.bg.elevated,
  },
  pagePlaceholderText: {
    fontFamily: font.bold,
    fontSize: 24,
    color: colors.text.disabled,
  },
  pageNumberBadge: {
    position: 'absolute',
    top: 4,
    left: 4,
    backgroundColor: 'rgba(0,0,0,0.65)',
    borderRadius: 10,
    minWidth: 20,
    height: 20,
    alignItems: 'center',
    justifyContent: 'center',
    paddingHorizontal: 5,
  },
  pageNumberText: {
    fontFamily: font.semibold,
    fontSize: 10,
    color: '#FFFFFF',
  },
  pageSelectionBadge: {
    position: 'absolute',
    top: 4,
    right: 4,
    width: 22,
    height: 22,
    borderRadius: 11,
    backgroundColor: colors.accent.primary,
    alignItems: 'center',
    justifyContent: 'center',
    borderWidth: 2,
    borderColor: '#FFFFFF',
  },
  pageSelectionEmpty: {
    position: 'absolute',
    top: 4,
    right: 4,
    width: 22,
    height: 22,
    borderRadius: 11,
    borderWidth: 2,
    borderColor: 'rgba(255,255,255,0.5)',
    backgroundColor: 'rgba(0,0,0,0.2)',
  },
  pageRotationBadge: {
    position: 'absolute',
    bottom: 4,
    right: 4,
    backgroundColor: 'rgba(0,0,0,0.5)',
    borderRadius: 8,
    paddingHorizontal: 5,
    paddingVertical: 2,
  },
  pageRotationText: {
    fontFamily: font.regular,
    fontSize: 9,
    color: '#FFFFFF',
  },

  // ── Bottom Toolbar ──
  toolbar: {
    position: 'absolute',
    bottom: 0,
    left: 0,
    right: 0,
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-around',
    paddingVertical: space.md,
    paddingHorizontal: space.md,
    paddingBottom: 32,
    backgroundColor: colors.bg.card,
    borderTopWidth: 1,
    borderTopColor: colors.border.subtle,
    ...shadows.card,
  },
  toolbarBtn: {
    alignItems: 'center',
    justifyContent: 'center',
    paddingVertical: space.xs,
    paddingHorizontal: space.sm,
    borderRadius: radius.md,
    minWidth: 56,
  },
  toolbarBtnActive: {
    backgroundColor: colors.accent.primaryDim,
  },
  toolbarBtnDisabled: {
    opacity: 0.3,
  },
  toolbarLabel: {
    fontFamily: font.medium,
    fontSize: 10,
    color: colors.text.secondary,
    marginTop: 2,
  },
  toolbarLabelActive: {
    color: colors.accent.primary,
  },

  // ── Selection Contextual Bar ──
  contextBar: {
    position: 'absolute',
    bottom: 0,
    left: 0,
    right: 0,
    flexDirection: 'row',
    alignItems: 'center',
    paddingVertical: space.md,
    paddingHorizontal: space.xl,
    paddingBottom: 32,
    backgroundColor: colors.accent.primary,
    gap: space.lg,
  },
  contextBarText: {
    flex: 1,
    fontFamily: font.semibold,
    fontSize: 14,
    color: '#FFFFFF',
  },
  contextBarBtn: {
    padding: space.sm,
    borderRadius: radius.md,
  },

  // ── Save Menu ──
  saveMenu: {
    position: 'absolute',
    top: 90,
    right: space.md,
    backgroundColor: colors.bg.elevated,
    borderRadius: radius.md,
    borderWidth: 1,
    borderColor: colors.border.medium,
    ...shadows.card,
    paddingVertical: space.xs,
    minWidth: 180,
    zIndex: 100,
  },
  saveMenuItem: {
    flexDirection: 'row',
    alignItems: 'center',
    paddingHorizontal: space.md,
    paddingVertical: space.md,
    gap: space.sm,
  },
  saveMenuText: {
    fontFamily: font.medium,
    fontSize: 14,
    color: colors.text.primary,
  },
  saveMenuDivider: {
    height: 1,
    backgroundColor: colors.border.subtle,
    marginHorizontal: space.md,
  },

  // ── Empty State ──
  emptyState: {
    flex: 1,
    alignItems: 'center',
    justifyContent: 'center',
    paddingHorizontal: space['2xl'],
  },
  emptyIcon: {
    marginBottom: space.lg,
    opacity: 0.5,
  },
  emptyTitle: {
    fontFamily: font.bold,
    fontSize: 20,
    color: colors.text.primary,
    textAlign: 'center',
    marginBottom: space.sm,
  },
  emptySubtitle: {
    fontFamily: font.regular,
    fontSize: 14,
    color: colors.text.tertiary,
    textAlign: 'center',
    lineHeight: 20,
  },

  // ── Loading ──
  loadingOverlay: {
    ...StyleSheet.absoluteFillObject,
    backgroundColor: 'rgba(0,0,0,0.6)',
    alignItems: 'center',
    justifyContent: 'center',
    zIndex: 200,
  },
  loadingCard: {
    backgroundColor: colors.bg.elevated,
    borderRadius: radius.lg,
    padding: space.xl,
    alignItems: 'center',
    gap: space.md,
    minWidth: 200,
  },
  loadingText: {
    fontFamily: font.medium,
    fontSize: 14,
    color: colors.text.primary,
  },

  // ── Add Pages Sheet ──
  sheetContainer: {
    backgroundColor: colors.bg.card,
    borderTopLeftRadius: radius.xl,
    borderTopRightRadius: radius.xl,
    paddingTop: space.md,
    paddingBottom: 40,
    paddingHorizontal: space.xl,
  },
  sheetHandle: {
    width: 40,
    height: 4,
    backgroundColor: colors.border.medium,
    borderRadius: 2,
    alignSelf: 'center',
    marginBottom: space.lg,
  },
  sheetTitle: {
    fontFamily: font.bold,
    fontSize: 18,
    color: colors.text.primary,
    marginBottom: space.lg,
  },
  sheetOption: {
    flexDirection: 'row',
    alignItems: 'center',
    paddingVertical: space.md,
    paddingHorizontal: space.md,
    borderRadius: radius.md,
    gap: space.md,
    marginBottom: space.xs,
  },
  sheetOptionIcon: {
    width: 44,
    height: 44,
    borderRadius: radius.md,
    alignItems: 'center',
    justifyContent: 'center',
  },
  sheetOptionTextWrap: {
    flex: 1,
  },
  sheetOptionLabel: {
    fontFamily: font.semibold,
    fontSize: 15,
    color: colors.text.primary,
  },
  sheetOptionDesc: {
    fontFamily: font.regular,
    fontSize: 12,
    color: colors.text.tertiary,
    marginTop: 1,
  },

  // ── Page Preview Modal ──
  previewContainer: {
    flex: 1,
    backgroundColor: '#000000',
  },
  previewTopBar: {
    position: 'absolute',
    top: 0,
    left: 0,
    right: 0,
    flexDirection: 'row',
    alignItems: 'center',
    paddingTop: 48,
    paddingBottom: space.md,
    paddingHorizontal: space.md,
    backgroundColor: 'rgba(0,0,0,0.7)',
    zIndex: 10,
  },
  previewTitle: {
    flex: 1,
    fontFamily: font.semibold,
    fontSize: 16,
    color: '#FFFFFF',
    textAlign: 'center',
  },
  previewImage: {
    width: SCREEN_W,
    height: SCREEN_H - 150,
    resizeMode: 'contain',
  },
});

export default createPdfEditorStyles;
