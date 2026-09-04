import React from 'react';
import { View, Text, Pressable } from 'react-native';
import Animated, { useSharedValue, useAnimatedStyle, withSpring } from 'react-native-reanimated';
import { Ionicons } from '@expo/vector-icons';
import * as Haptics from 'expo-haptics';
import { useAppTheme } from '../../hooks/useAppTheme';
import createHomeStyles from '../../styles/homeStyles';

interface CategoryTileProps {
  icon: string;
  label: string;
  subtitle: string;
  bgColor?: string;
  iconColor: string;
  onPress: () => void;
}

const AnimatedPressable = Animated.createAnimatedComponent(Pressable);

export default function CategoryTile({
  icon,
  label,
  subtitle,
  iconColor,
  onPress,
}: CategoryTileProps) {
  const { colors, shadows, spring } = useAppTheme();
  const styles = createHomeStyles(colors, shadows);
  const scale = useSharedValue(1);

  const animatedStyle = useAnimatedStyle(() => {
    return {
      transform: [{ scale: scale.value }],
    };
  });

  const handlePressIn = () => {
    scale.value = withSpring(0.96, spring.bounce);
  };

  const handlePressOut = () => {
    scale.value = withSpring(1, spring.bounce);
  };

  const handlePress = () => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
    if (typeof onPress === 'function') {
      try {
        onPress();
      } catch (err) {
        console.error('CategoryTile press error:', err);
      }
    }
  };

  return (
    <AnimatedPressable
      onPressIn={handlePressIn}
      onPressOut={handlePressOut}
      onPress={handlePress}
      style={[styles.tile, animatedStyle]}
      accessibilityLabel={`${label}: ${subtitle}`}
      accessibilityRole="button"
    >
      <View style={styles.tileTopRow}>
        <View style={[styles.tileIconWrap, { backgroundColor: `${iconColor}18` }]}>
          <Ionicons name={icon as any} size={20} color={iconColor} />
        </View>
        <Ionicons name="chevron-forward" size={16} color={colors.text.tertiary} style={{ opacity: 0.5 }} />
      </View>
      <View>
        <Text style={styles.tileLabel} numberOfLines={1}>
          {label}
        </Text>
        <Text style={styles.tileSublabel} numberOfLines={1}>
          {subtitle}
        </Text>
      </View>
    </AnimatedPressable>
  );
}
