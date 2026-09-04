import React, { useState, useRef } from 'react';
import { View, TextInput, Pressable, StyleSheet } from 'react-native';
import Animated, { 
  useSharedValue, 
  useAnimatedStyle, 
  withTiming 
} from 'react-native-reanimated';
import { Ionicons } from '@expo/vector-icons';
import { useAppTheme } from '../hooks/useAppTheme';
import createHomeStyles from '../styles/homeStyles';

interface MaterialSearchBarProps {
  connectionStatus: 'online' | 'cloud' | 'offline' | 'reconnecting';
  onSearch: (query: string) => void;
  onSettingsPress: () => void;
  onConnectionPress: () => void;
}

export default function MaterialSearchBar({
  connectionStatus,
  onSearch,
  onSettingsPress,
  onConnectionPress,
}: MaterialSearchBarProps) {
  const { colors, shadows } = useAppTheme();
  const styles = createHomeStyles(colors, shadows);
  const [query, setQuery] = useState('');
  const [isFocused, setIsFocused] = useState(false);
  const inputRef = useRef<TextInput>(null);

  const expandWidth = useSharedValue(0);

  const getStatusColor = () => {
    switch (connectionStatus) {
      case 'online': return colors.accent.success;
      case 'cloud': return colors.accent.warning;
      case 'reconnecting': return colors.accent.warning;
      case 'offline': return colors.accent.error;
    }
  };

  const handleFocus = () => {
    setIsFocused(true);
    expandWidth.value = withTiming(10);
  };

  const handleBlur = () => {
    setIsFocused(false);
    expandWidth.value = withTiming(0);
  };

  const handleClear = () => {
    setQuery('');
    onSearch('');
    inputRef.current?.focus();
  };

  const handleChange = (text: string) => {
    setQuery(text);
    onSearch(text);
  };

  const animatedContainerStyle = useAnimatedStyle(() => {
    return {
      marginHorizontal: -expandWidth.value,
    };
  });

  return (
    <Animated.View style={[styles.searchBar, animatedContainerStyle, isFocused && { borderColor: colors.accent.primary }]}>
      <Pressable onPress={() => onConnectionPress?.()} hitSlop={10} accessibilityLabel="Network status" accessibilityRole="button">
        <View style={[styles.searchDot, { backgroundColor: getStatusColor() }]} />
      </Pressable>

      <TextInput
        ref={inputRef}
        style={styles.searchText}
        placeholder="Search FlyShelf..."
        placeholderTextColor={colors.text.tertiary}
        value={query}
        onChangeText={handleChange}
        onFocus={handleFocus}
        onBlur={handleBlur}
        returnKeyType="search"
      />

      <View style={styles.searchActions}>
        {query.length > 0 && (
          <Pressable onPress={handleClear} style={styles.searchActionBtn} accessibilityLabel="Clear search" accessibilityRole="button">
            <Ionicons name="close-circle" size={20} color={colors.text.tertiary} />
          </Pressable>
        )}
        
        {!isFocused && query.length === 0 && (
          <Pressable onPress={() => onSettingsPress?.()} style={styles.searchActionBtn} accessibilityLabel="Open settings" accessibilityRole="button">
            <Ionicons name="settings-outline" size={20} color={colors.text.secondary} />
          </Pressable>
        )}
      </View>
    </Animated.View>
  );
}
