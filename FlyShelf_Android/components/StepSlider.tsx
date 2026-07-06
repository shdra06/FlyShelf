import React from 'react';
import { View, Text, TouchableOpacity } from 'react-native';
import { useAppTheme } from '../hooks/useAppTheme';
import * as Haptics from 'expo-haptics';

type StepSliderProps = {
  value: number;
  min: number;
  max: number;
  step: number;
  onValueChange: (v: number) => void;
  trackColor: string;
  label: string;
};

/** Custom pure-JS slider row — themed */
const StepSlider = ({ value, min, max, step, onValueChange, trackColor, label }: StepSliderProps) => {
  const { colors, font } = useAppTheme();
  const pct = Math.max(0, Math.min(100, ((value - min) / (max - min)) * 100));
  return (
    <View style={{marginTop: 8}} accessibilityValue={{ min, max, now: value }}>
      <View style={{flexDirection: 'row', alignItems: 'center', gap: 10}}>
        <TouchableOpacity onPress={() => { Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light); if (value - step >= min) onValueChange(value - step); }} style={{width: 36, height: 36, borderRadius: 12, backgroundColor: colors.bg.cardHover, alignItems: 'center', justifyContent: 'center'}} accessibilityLabel={`Decrease ${label}`} accessibilityRole="button">
          <Text style={{color: colors.text.primary, fontSize: 18, fontFamily: font.extrabold}}>−</Text>
        </TouchableOpacity>
        <View style={{flex: 1, height: 6, backgroundColor: colors.bg.cardHover, borderRadius: 3, overflow: 'hidden'}}>
          <View style={{width: `${pct}%`, height: '100%', backgroundColor: trackColor, borderRadius: 3}} />
        </View>
        <TouchableOpacity onPress={() => { Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light); if (value + step <= max) onValueChange(value + step); }} style={{width: 36, height: 36, borderRadius: 12, backgroundColor: colors.bg.cardHover, alignItems: 'center', justifyContent: 'center'}} accessibilityLabel={`Increase ${label}`} accessibilityRole="button">
          <Text style={{color: colors.text.primary, fontSize: 18, fontFamily: font.extrabold}}>+</Text>
        </TouchableOpacity>
      </View>
    </View>
  );
};

export default React.memo(StepSlider);
