/**
 * EmptyState — Standard empty view with icon, title, description, and optional CTA
 */
import React, { useMemo } from 'react';
import { View, Text, StyleSheet } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { font, space } from '../styles/theme';
import { useAppTheme } from '../hooks/useAppTheme';
import AppButton from './AppButton';

interface EmptyStateProps {
  icon?: string;
  iconColor?: string;
  title: string;
  description?: string;
  actionLabel?: string;
  onAction?: () => void;
}

export default function EmptyState({
  icon = 'file-tray-outline',
  iconColor,
  title,
  description,
  actionLabel,
  onAction,
}: EmptyStateProps) {
  const { colors } = useAppTheme();
  const styles = useMemo(() => createStyles(colors), [colors]);
  const resolvedIconColor = iconColor ?? colors.text.disabled;

  return (
    <View style={styles.container}>
      <View style={styles.iconWrap}>
        <Ionicons name={icon as any} size={48} color={resolvedIconColor} />
      </View>
      <Text style={styles.title}>{title}</Text>
      {description && <Text style={styles.description}>{description}</Text>}
      {actionLabel && onAction && (
        <View style={styles.actionWrap}>
          <AppButton label={actionLabel} onPress={onAction} variant="secondary" size="md" />
        </View>
      )}
    </View>
  );
}

const createStyles = (colors: Record<string, any>) => StyleSheet.create({
  container: {
    alignItems: 'center',
    paddingVertical: 60,
    paddingHorizontal: space.xl,
  },
  iconWrap: {
    width: 80,
    height: 80,
    borderRadius: 40,
    backgroundColor: colors.bg.card,
    alignItems: 'center',
    justifyContent: 'center',
    marginBottom: space.xl,
  },
  title: {
    fontFamily: font.semibold,
    fontSize: 16,
    color: colors.text.secondary,
    textAlign: 'center',
    marginBottom: space.sm,
  },
  description: {
    fontFamily: font.regular,
    fontSize: 13,
    color: colors.text.tertiary,
    textAlign: 'center',
    lineHeight: 19,
    maxWidth: 260,
  },
  actionWrap: {
    marginTop: space.xl,
  },
});

