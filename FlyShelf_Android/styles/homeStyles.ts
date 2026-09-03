import { StyleSheet, ViewStyle, TextStyle, ImageStyle } from 'react-native';
import { ThemeColors, ThemeShadows } from '../hooks/useAppTheme';
import { font, space } from './theme';

export function createHomeStyles(colors: ThemeColors, shadows: ThemeShadows) {
  return StyleSheet.create({
    container: {
      flex: 1,
      backgroundColor: colors.bg.base,
    },
    scrollContent: {
      paddingBottom: 120,
    },
    // Search bar
    searchBar: {
      flexDirection: 'row',
      backgroundColor: colors.bg.card,
      borderRadius: 28,
      paddingHorizontal: 16,
      height: 52,
      borderWidth: 1,
      borderColor: colors.border.subtle,
      alignItems: 'center',
    },
    searchDot: {
      width: 10,
      height: 10,
      borderRadius: 5,
      marginRight: 12,
    },
    searchText: {
      flex: 1,
      fontFamily: font.medium,
      fontSize: 15,
      color: colors.text.primary,
    },
    searchActions: {
      flexDirection: 'row',
      gap: 8,
    },
    searchActionBtn: {
      padding: 8,
    },
    // Tiles grid
    tilesGrid: {
      flexDirection: 'row',
      flexWrap: 'wrap',
      gap: 12,
      paddingHorizontal: 20,
      marginTop: 14,
    },
    tile: {
      width: '48%',
      borderRadius: 20,
      padding: 16,
      backgroundColor: colors.bg.card,
      borderWidth: 1,
      borderColor: colors.border.subtle,
      justifyContent: 'space-between',
      minHeight: 116,
      ...shadows.card,
    },
    tileTopRow: {
      flexDirection: 'row',
      alignItems: 'center',
      justifyContent: 'space-between',
    },
    tileIconWrap: {
      width: 44,
      height: 44,
      borderRadius: 14,
      alignItems: 'center',
      justifyContent: 'center',
    },
    tileLabel: {
      fontFamily: font.bold,
      fontSize: 16,
      color: colors.text.primary,
      marginTop: 10,
    },
    tileSublabel: {
      fontFamily: font.medium,
      fontSize: 13,
      color: colors.text.tertiary,
      marginTop: 2,
    },
    // Quick actions
    chipsRow: {
      flexDirection: 'row',
      paddingHorizontal: 20,
      marginTop: 20,
      gap: 10,
    },
    chip: {
      flexDirection: 'row',
      paddingHorizontal: 14,
      paddingVertical: 10,
      borderRadius: 20,
      backgroundColor: colors.bg.card,
      borderWidth: 1,
      borderColor: colors.border.subtle,
      gap: 6,
      alignItems: 'center',
    },
    chipLabel: {
      fontFamily: font.medium,
      fontSize: 13,
      color: colors.text.secondary,
    },
    // Section header
    sectionHeader: {
      flexDirection: 'row',
      justifyContent: 'space-between',
      paddingHorizontal: 20,
      marginTop: 28,
      marginBottom: 12,
      alignItems: 'center',
    },
    sectionTitle: {
      fontFamily: font.bold,
      fontSize: 18,
      color: colors.text.primary,
    },
    sectionSeeAll: {
      fontFamily: font.medium,
      fontSize: 13,
      color: colors.accent.primary,
    },
    // Activity feed
    activityItem: {
      flexDirection: 'row',
      paddingHorizontal: 20,
      paddingVertical: 14,
      gap: 14,
      alignItems: 'center',
    },
    activityIconWrap: {
      width: 40,
      height: 40,
      borderRadius: 12,
      alignItems: 'center',
      justifyContent: 'center',
      backgroundColor: colors.bg.elevated,
    },
    activityContent: {
      flex: 1,
    },
    activityTitle: {
      fontFamily: font.medium,
      fontSize: 14,
      color: colors.text.primary,
    },
    activityTime: {
      fontFamily: font.regular,
      fontSize: 12,
      color: colors.text.tertiary,
      marginTop: 2,
    },
    activityDivider: {
      height: 1,
      backgroundColor: colors.border.subtle,
      marginHorizontal: 20,
    },
    // FAB
    fab: {
      position: 'absolute',
      bottom: 90,
      right: 20,
      width: 56,
      height: 56,
      borderRadius: 28,
      backgroundColor: colors.accent.primary,
      alignItems: 'center',
      justifyContent: 'center',
      ...shadows.elevated,
    },
    fabSheet: {
      backgroundColor: colors.bg.elevated,
      borderTopLeftRadius: 28,
      borderTopRightRadius: 28,
      padding: 20,
      paddingBottom: 44,
      borderWidth: 1,
      borderColor: colors.border.subtle,
      ...shadows.elevated,
    },
    fabSheetHeader: {
      marginBottom: 16,
      paddingHorizontal: 4,
    },
    fabSheetTitle: {
      fontFamily: font.bold,
      fontSize: 18,
      color: colors.text.primary,
      letterSpacing: -0.3,
    },
    fabSheetSubtitle: {
      fontFamily: font.regular,
      fontSize: 13,
      color: colors.text.tertiary,
      marginTop: 2,
    },
    fabHandle: {
      width: 40,
      height: 4,
      backgroundColor: colors.border.medium,
      borderRadius: 2,
      alignSelf: 'center',
      marginBottom: 16,
    },
    fabOption: {
      flexDirection: 'row',
      paddingVertical: 12,
      paddingHorizontal: 10,
      gap: 14,
      alignItems: 'center',
      borderRadius: 16,
      marginBottom: 4,
    },
    fabOptionIcon: {
      width: 46,
      height: 46,
      borderRadius: 14,
      alignItems: 'center',
      justifyContent: 'center',
    },
    fabOptionLabel: {
      fontFamily: font.bold,
      fontSize: 15,
      color: colors.text.primary,
    },
    fabOptionDesc: {
      fontFamily: font.medium,
      fontSize: 12,
      color: colors.text.tertiary,
      marginTop: 2,
    },
    // Empty state
    emptyState: {
      alignItems: 'center',
      justifyContent: 'center',
      padding: 40,
      marginTop: 20,
    },
    emptyIcon: {
      marginBottom: 16,
      opacity: 0.5,
    },
    emptyTitle: {
      fontFamily: font.semibold,
      fontSize: 16,
      color: colors.text.secondary,
      marginBottom: 8,
    },
    emptySubtitle: {
      fontFamily: font.regular,
      fontSize: 13,
      color: colors.text.tertiary,
      textAlign: 'center',
    },
  });
}

export default createHomeStyles;
