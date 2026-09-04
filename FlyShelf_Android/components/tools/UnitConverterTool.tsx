import React, { useState, useMemo, useEffect, useCallback } from 'react';
import { View, Text, StyleSheet, TouchableOpacity, ScrollView, TextInput, Platform, KeyboardAvoidingView } from 'react-native';
import Animated, { FadeInDown, FadeIn, Layout } from 'react-native-reanimated';
import { Ionicons } from '@expo/vector-icons';
import * as Haptics from 'expo-haptics';
import AsyncStorage from '@react-native-async-storage/async-storage';
import { useSafeAreaInsets } from 'react-native-safe-area-context';

import { useAppTheme } from '../../hooks/useAppTheme';
import { font, space, radius } from '../../styles/theme';

interface UnitConverterToolProps {
  onBack: () => void;
}

const CATEGORIES = [
  {
    id: 'length', label: 'Length', icon: 'resize-outline',
    units: [
      { id: 'mm', label: 'Millimeter', symbol: 'mm', toBase: 0.001 },
      { id: 'cm', label: 'Centimeter', symbol: 'cm', toBase: 0.01 },
      { id: 'm', label: 'Meter', symbol: 'm', toBase: 1 },
      { id: 'km', label: 'Kilometer', symbol: 'km', toBase: 1000 },
      { id: 'in', label: 'Inch', symbol: 'in', toBase: 0.0254 },
      { id: 'ft', label: 'Foot', symbol: 'ft', toBase: 0.3048 },
      { id: 'yd', label: 'Yard', symbol: 'yd', toBase: 0.9144 },
      { id: 'mi', label: 'Mile', symbol: 'mi', toBase: 1609.344 },
    ],
  },
  {
    id: 'weight', label: 'Weight', icon: 'barbell-outline',
    units: [
      { id: 'mg', label: 'Milligram', symbol: 'mg', toBase: 0.000001 },
      { id: 'g', label: 'Gram', symbol: 'g', toBase: 0.001 },
      { id: 'kg', label: 'Kilogram', symbol: 'kg', toBase: 1 },
      { id: 'lb', label: 'Pound', symbol: 'lb', toBase: 0.453592 },
      { id: 'oz', label: 'Ounce', symbol: 'oz', toBase: 0.0283495 },
      { id: 'ton', label: 'Metric Ton', symbol: 't', toBase: 1000 },
    ],
  },
  {
    id: 'temperature', label: 'Temp', icon: 'thermometer-outline',
    units: [
      { id: 'c', label: 'Celsius', symbol: '°C' },
      { id: 'f', label: 'Fahrenheit', symbol: '°F' },
      { id: 'k', label: 'Kelvin', symbol: 'K' },
    ],
  },
  {
    id: 'area', label: 'Area', icon: 'scan-outline',
    units: [
      { id: 'sqm', label: 'Square Meter', symbol: 'm²', toBase: 1 },
      { id: 'sqkm', label: 'Square Kilometer', symbol: 'km²', toBase: 1000000 },
      { id: 'sqft', label: 'Square Foot', symbol: 'ft²', toBase: 0.092903 },
      { id: 'sqin', label: 'Square Inch', symbol: 'in²', toBase: 0.00064516 },
      { id: 'acre', label: 'Acre', symbol: 'ac', toBase: 4046.86 },
      { id: 'ha', label: 'Hectare', symbol: 'ha', toBase: 10000 },
    ],
  },
  {
    id: 'volume', label: 'Volume', icon: 'beaker-outline',
    units: [
      { id: 'ml', label: 'Milliliter', symbol: 'mL', toBase: 0.001 },
      { id: 'l', label: 'Liter', symbol: 'L', toBase: 1 },
      { id: 'gal', label: 'US Gallon', symbol: 'gal', toBase: 3.78541 },
      { id: 'qt', label: 'US Quart', symbol: 'qt', toBase: 0.946353 },
      { id: 'pt', label: 'US Pint', symbol: 'pt', toBase: 0.473176 },
      { id: 'cup', label: 'US Cup', symbol: 'cup', toBase: 0.236588 },
      { id: 'floz', label: 'Fluid Ounce', symbol: 'fl oz', toBase: 0.0295735 },
    ],
  },
  {
    id: 'speed', label: 'Speed', icon: 'speedometer-outline',
    units: [
      { id: 'kmh', label: 'km/h', symbol: 'km/h', toBase: 1 },
      { id: 'mph', label: 'mph', symbol: 'mph', toBase: 1.60934 },
      { id: 'ms', label: 'm/s', symbol: 'm/s', toBase: 3.6 },
      { id: 'knot', label: 'Knot', symbol: 'kn', toBase: 1.852 },
    ],
  },
  {
    id: 'time', label: 'Time', icon: 'time-outline',
    units: [
      { id: 'ms', label: 'Millisecond', symbol: 'ms', toBase: 0.001 },
      { id: 's', label: 'Second', symbol: 's', toBase: 1 },
      { id: 'min', label: 'Minute', symbol: 'min', toBase: 60 },
      { id: 'hr', label: 'Hour', symbol: 'hr', toBase: 3600 },
      { id: 'day', label: 'Day', symbol: 'day', toBase: 86400 },
      { id: 'week', label: 'Week', symbol: 'wk', toBase: 604800 },
      { id: 'year', label: 'Year', symbol: 'yr', toBase: 31557600 },
    ],
  },
  {
    id: 'data', label: 'Data', icon: 'hardware-chip-outline',
    units: [
      { id: 'b', label: 'Byte', symbol: 'B', toBase: 1 },
      { id: 'kb', label: 'Kilobyte', symbol: 'KB', toBase: 1024 },
      { id: 'mb', label: 'Megabyte', symbol: 'MB', toBase: 1048576 },
      { id: 'gb', label: 'Gigabyte', symbol: 'GB', toBase: 1073741824 },
      { id: 'tb', label: 'Terabyte', symbol: 'TB', toBase: 1099511627776 },
    ],
  },
];

const HISTORY_KEY = '@unit_converter_history';

function convertTemperature(value: number, from: string, to: string): number {
  let celsius: number;
  if (from === 'c') celsius = value;
  else if (from === 'f') celsius = (value - 32) * 5/9;
  else celsius = value - 273.15; // kelvin
  
  if (to === 'c') return celsius;
  if (to === 'f') return celsius * 9/5 + 32;
  return celsius + 273.15; // kelvin
}

export default function UnitConverterTool({ onBack }: UnitConverterToolProps) {
  const { colors, shadows } = useAppTheme();
  const insets = useSafeAreaInsets();
  
  const [activeCategory, setActiveCategory] = useState(CATEGORIES[0].id);
  const [fromUnit, setFromUnit] = useState(CATEGORIES[0].units[2].id); // Meter
  const [inputValue, setInputValue] = useState('1');
  
  const [history, setHistory] = useState<any[]>([]);

  const currentCategory = useMemo(() => CATEGORIES.find(c => c.id === activeCategory) || CATEGORIES[0], [activeCategory]);
  
  const currentFromUnit = useMemo(() => currentCategory.units.find(u => u.id === fromUnit) || currentCategory.units[0], [currentCategory, fromUnit]);

  // Load history
  useEffect(() => {
    const loadHistory = async () => {
      try {
        const stored = await AsyncStorage.getItem(HISTORY_KEY);
        if (stored) {
          setHistory(JSON.parse(stored));
        }
      } catch (e) {}
    };
    loadHistory();
  }, []);

  const saveHistory = async (record: any) => {
    try {
      const newHistory = [record, ...history.filter(h => h.id !== record.id)].slice(0, 10);
      setHistory(newHistory);
      await AsyncStorage.setItem(HISTORY_KEY, JSON.stringify(newHistory));
    } catch (e) {}
  };

  const handleCategoryChange = useCallback((categoryId: string) => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
    setActiveCategory(categoryId);
    const cat = CATEGORIES.find(c => c.id === categoryId);
    if (cat) {
      setFromUnit(cat.units[0].id);
    }
  }, []);

  const handleUnitSwap = useCallback((newFromId: string) => {
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);
    setFromUnit(newFromId);
  }, []);

  const getConvertedValue = useCallback((toUnit: any) => {
    const val = parseFloat(inputValue);
    if (isNaN(val)) return '0';
    
    if (activeCategory === 'temperature') {
      return convertTemperature(val, fromUnit, toUnit.id).toPrecision(6).replace(/\.?0+$/, '');
    }
    
    if (currentFromUnit.toBase !== undefined && toUnit.toBase !== undefined) {
      const baseValue = val * currentFromUnit.toBase;
      const converted = baseValue / toUnit.toBase;
      return parseFloat(converted.toPrecision(6)).toString();
    }
    
    return '0';
  }, [activeCategory, fromUnit, inputValue, currentFromUnit]);

  const handleCopy = useCallback((resultUnit: any, resultValue: string) => {
    Haptics.notificationAsync(Haptics.NotificationFeedbackType.Success);
    // In a real app, use Clipboard.setStringAsync from expo-clipboard here
    saveHistory({
      id: `${Date.now()}`,
      category: activeCategory,
      fromValue: inputValue,
      fromUnit: currentFromUnit.symbol,
      toValue: resultValue,
      toUnit: resultUnit.symbol,
    });
    // Optional toast notification would go here
  }, [activeCategory, inputValue, currentFromUnit]);

  const styles = useMemo(() => StyleSheet.create({
    container: {
      flex: 1,
      backgroundColor: colors.bg.base,
      paddingTop: insets.top,
    },
    header: {
      flexDirection: 'row',
      alignItems: 'center',
      paddingHorizontal: space.lg,
      paddingVertical: space.md,
      borderBottomWidth: 1,
      borderBottomColor: colors.border.subtle,
    },
    backButton: {
      padding: space.sm,
      marginRight: space.sm,
    },
    title: {
      fontFamily: font.semibold,
      fontSize: 18,
      color: colors.text.primary,
    },
    categoriesScroll: {
      paddingHorizontal: space.lg,
      paddingVertical: space.md,
    },
    categoryChip: {
      flexDirection: 'row',
      alignItems: 'center',
      paddingHorizontal: space.lg,
      paddingVertical: space.sm,
      borderRadius: radius.pill,
      backgroundColor: colors.bg.card,
      marginRight: space.sm,
      borderWidth: 1,
      borderColor: colors.border.subtle,
    },
    categoryChipActive: {
      backgroundColor: colors.accent.primaryDim,
      borderColor: colors.accent.primary,
    },
    categoryText: {
      fontFamily: font.medium,
      fontSize: 14,
      color: colors.text.secondary,
      marginLeft: space.xs,
    },
    categoryTextActive: {
      color: colors.accent.primary,
    },
    inputSection: {
      padding: space.lg,
      backgroundColor: colors.bg.card,
      marginHorizontal: space.lg,
      borderRadius: radius.lg,
      marginBottom: space.lg,
      ...shadows.card,
    },
    inputHeader: {
      flexDirection: 'row',
      justifyContent: 'space-between',
      alignItems: 'center',
      marginBottom: space.md,
    },
    inputLabel: {
      fontFamily: font.medium,
      fontSize: 14,
      color: colors.text.tertiary,
    },
    unitSelector: {
      flexDirection: 'row',
      alignItems: 'center',
      backgroundColor: colors.bg.input,
      paddingHorizontal: space.md,
      paddingVertical: space.xs,
      borderRadius: radius.md,
    },
    unitSelectorText: {
      fontFamily: font.semibold,
      fontSize: 14,
      color: colors.text.primary,
      marginRight: space.xs,
    },
    inputValue: {
      fontFamily: font.bold,
      fontSize: 36,
      color: colors.text.primary,
      padding: 0,
    },
    resultsContainer: {
      flex: 1,
      paddingHorizontal: space.lg,
    },
    resultsGrid: {
      flexDirection: 'row',
      flexWrap: 'wrap',
      justifyContent: 'space-between',
      paddingBottom: space.xl * 2,
    },
    resultCard: {
      width: '48%',
      backgroundColor: colors.bg.card,
      padding: space.lg,
      borderRadius: radius.md,
      marginBottom: space.md,
      borderWidth: 1,
      borderColor: colors.border.subtle,
      ...shadows.card,
    },
    resultValue: {
      fontFamily: font.semibold,
      fontSize: 20,
      color: colors.text.primary,
      marginBottom: space.xs,
    },
    resultUnitContainer: {
      flexDirection: 'row',
      alignItems: 'center',
    },
    resultSymbol: {
      fontFamily: font.bold,
      fontSize: 14,
      color: colors.accent.primary,
      marginRight: space.xs,
    },
    resultLabel: {
      fontFamily: font.regular,
      fontSize: 12,
      color: colors.text.secondary,
      flex: 1,
    },
    swapButton: {
      position: 'absolute',
      right: space.sm,
      bottom: space.sm,
      width: 32,
      height: 32,
      borderRadius: 16,
      backgroundColor: colors.bg.elevated,
      justifyContent: 'center',
      alignItems: 'center',
      borderWidth: 1,
      borderColor: colors.border.subtle,
    },
    historySection: {
      paddingHorizontal: space.lg,
      paddingBottom: space.xl,
    },
    historyTitle: {
      fontFamily: font.semibold,
      fontSize: 14,
      color: colors.text.tertiary,
      marginBottom: space.md,
    },
    historyItem: {
      flexDirection: 'row',
      alignItems: 'center',
      backgroundColor: colors.bg.card,
      padding: space.md,
      borderRadius: radius.md,
      marginBottom: space.sm,
    },
    historyText: {
      fontFamily: font.medium,
      fontSize: 14,
      color: colors.text.secondary,
    },
    historyEquals: {
      fontFamily: font.regular,
      color: colors.text.tertiary,
      marginHorizontal: space.sm,
    },
    historyResult: {
      fontFamily: font.semibold,
      color: colors.text.primary,
    }
  }), [colors, insets.top, shadows.card]);

  return (
    <KeyboardAvoidingView style={styles.container} behavior={Platform.OS === 'ios' ? 'padding' : undefined}>
      {/* Header */}
      <View style={styles.header}>
        <TouchableOpacity style={styles.backButton} onPress={onBack}>
          <Ionicons name="arrow-back" size={24} color={colors.text.primary} />
        </TouchableOpacity>
        <Text style={styles.title}>Unit Converter</Text>
      </View>

      {/* Categories */}
      <View>
        <ScrollView 
          horizontal 
          showsHorizontalScrollIndicator={false}
          contentContainerStyle={styles.categoriesScroll}
        >
          {CATEGORIES.map(cat => (
            <TouchableOpacity 
              key={cat.id}
              style={[styles.categoryChip, activeCategory === cat.id && styles.categoryChipActive]}
              onPress={() => handleCategoryChange(cat.id)}
            >
              <Ionicons 
                name={cat.icon as any} 
                size={18} 
                color={activeCategory === cat.id ? colors.accent.primary : colors.text.secondary} 
              />
              <Text style={[styles.categoryText, activeCategory === cat.id && styles.categoryTextActive]}>
                {cat.label}
              </Text>
            </TouchableOpacity>
          ))}
        </ScrollView>
      </View>

      <ScrollView style={styles.resultsContainer} showsVerticalScrollIndicator={false}>
        {/* Input Section */}
        <Animated.View style={styles.inputSection} entering={FadeInDown.duration(400)}>
          <View style={styles.inputHeader}>
            <Text style={styles.inputLabel}>Convert from</Text>
            <View style={styles.unitSelector}>
              <Text style={styles.unitSelectorText}>{currentFromUnit?.label}</Text>
              <Ionicons name="chevron-down" size={16} color={colors.text.secondary} />
            </View>
          </View>
          <TextInput
            style={styles.inputValue}
            value={inputValue}
            onChangeText={setInputValue}
            keyboardType="numeric"
            placeholder="0"
            placeholderTextColor={colors.text.tertiary}
            selectTextOnFocus
          />
        </Animated.View>

        {/* Results Grid */}
        <View style={styles.resultsGrid}>
          {currentCategory.units.filter(u => u.id !== fromUnit).map((unit, index) => {
            const convertedVal = getConvertedValue(unit);
            return (
              <Animated.View 
                key={unit.id} 
                entering={FadeInDown.delay(index * 50).duration(400)}
                layout={Layout.springify()}
                style={styles.resultCard}
              >
                <TouchableOpacity 
                  onPress={() => handleCopy(unit, convertedVal)}
                  activeOpacity={0.7}
                >
                  <Text style={styles.resultValue} numberOfLines={1} adjustsFontSizeToFit>
                    {convertedVal}
                  </Text>
                  <View style={styles.resultUnitContainer}>
                    <Text style={styles.resultSymbol}>{unit.symbol}</Text>
                    <Text style={styles.resultLabel} numberOfLines={1}>{unit.label}</Text>
                  </View>
                </TouchableOpacity>
                <TouchableOpacity 
                  style={styles.swapButton}
                  onPress={() => handleUnitSwap(unit.id)}
                >
                  <Ionicons name="swap-vertical" size={16} color={colors.text.secondary} />
                </TouchableOpacity>
              </Animated.View>
            );
          })}
        </View>

        {/* History */}
        {history.length > 0 && (
          <Animated.View style={styles.historySection} entering={FadeIn.delay(300)}>
            <Text style={styles.historyTitle}>Recent Conversions</Text>
            {history.slice(0, 5).map((item, idx) => (
              <View key={item.id} style={styles.historyItem}>
                <Ionicons name="time-outline" size={16} color={colors.text.tertiary} style={{marginRight: space.sm}} />
                <Text style={styles.historyText}>{item.fromValue} {item.fromUnit}</Text>
                <Text style={styles.historyEquals}>=</Text>
                <Text style={styles.historyResult}>{item.toValue} {item.toUnit}</Text>
              </View>
            ))}
          </Animated.View>
        )}
      </ScrollView>
    </KeyboardAvoidingView>
  );
}
