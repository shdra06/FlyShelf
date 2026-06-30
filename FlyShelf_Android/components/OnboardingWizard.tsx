import React, { useState, useRef } from 'react';
import {
  View,
  Text,
  Modal,
  TouchableOpacity,
  ScrollView,
  useWindowDimensions,
  NativeScrollEvent,
  NativeSyntheticEvent,
  StyleSheet,
} from 'react-native';
import { LinearGradient } from 'expo-linear-gradient';
import AsyncStorage from '@react-native-async-storage/async-storage';

type Props = {
  visible: boolean;
  onComplete: () => void;
};

const SLIDES = [
  {
    emoji: '🚀',
    title: 'Welcome to FlyShelf',
    subtitle: 'Sync clipboard, files, notes & todos between your PC and phone',
  },
  {
    emoji: '🔗',
    title: 'Connect to Your PC',
    subtitle: 'Open FlyShelf on your PC, then scan the QR code or enter the pairing code',
  },
  {
    emoji: '🔄',
    title: 'Automatic Sync',
    subtitle: 'Clipboard syncs instantly. Notes and Todos sync every 10 seconds.',
  },
  {
    emoji: '✨',
    title: "You're All Set!",
    subtitle: 'Start by pairing with your PC from the Sync tab',
    isFinal: true,
  },
];

export default function OnboardingWizard({ visible, onComplete }: Props) {
  const [currentPage, setCurrentPage] = useState(0);
  const { width: SCREEN_WIDTH } = useWindowDimensions();
  const scrollRef = useRef<ScrollView>(null);

  const handleScroll = (e: NativeSyntheticEvent<NativeScrollEvent>) => {
    const page = Math.round(e.nativeEvent.contentOffset.x / SCREEN_WIDTH);
    if (page !== currentPage) setCurrentPage(page);
  };

  const goToPage = (page: number) => {
    scrollRef.current?.scrollTo({ x: page * SCREEN_WIDTH, animated: true });
    setCurrentPage(page);
  };

  const handleNext = () => {
    if (currentPage < SLIDES.length - 1) {
      goToPage(currentPage + 1);
    }
  };

  const handleComplete = async () => {
    await AsyncStorage.setItem('@flyshelf_onboarding_done', 'true');
    onComplete();
  };

  const slide = SLIDES[currentPage];

  return (
    <Modal visible={visible} transparent animationType="fade" statusBarTranslucent>
      <LinearGradient
        colors={['#0D0F1A', '#141729', '#1A1F38']}
        style={s.container}
      >
        {/* Skip button — hidden on last slide */}
        {!slide.isFinal && (
          <TouchableOpacity style={s.skipBtn} onPress={handleComplete} accessibilityLabel="Skip onboarding" accessibilityRole="button">
            <Text style={s.skipText}>Skip</Text>
          </TouchableOpacity>
        )}

        {/* Swipeable slides */}
        <ScrollView
          ref={scrollRef}
          horizontal
          pagingEnabled
          showsHorizontalScrollIndicator={false}
          onMomentumScrollEnd={handleScroll}
          scrollEventThrottle={16}
          style={s.scrollView}
        >
          {SLIDES.map((item, idx) => (
            <View key={idx} style={[s.slide, { width: SCREEN_WIDTH }]}>
              <View style={s.emojiContainer}>
                <Text style={s.emoji}>{item.emoji}</Text>
              </View>
              <Text style={s.title}>{item.title}</Text>
              <Text style={s.subtitle}>{item.subtitle}</Text>
            </View>
          ))}
        </ScrollView>

        {/* Bottom section: dots + action button */}
        <View style={s.bottomSection}>
          {/* Dot indicators */}
          <View style={s.dotsRow}>
            {SLIDES.map((_, idx) => (
              <View
                key={idx}
                style={[
                  s.dot,
                  idx === currentPage ? s.dotActive : s.dotInactive,
                ]}
              />
            ))}
          </View>

          {/* Action button */}
          {slide.isFinal ? (
            <TouchableOpacity style={s.getStartedBtn} onPress={handleComplete} activeOpacity={0.85} accessibilityLabel="Get started" accessibilityRole="button">
              <LinearGradient
                colors={['#4A62EB', '#6384FF']}
                start={{ x: 0, y: 0 }}
                end={{ x: 1, y: 0 }}
                style={s.getStartedGradient}
              >
                <Text style={s.getStartedText}>Get Started</Text>
              </LinearGradient>
            </TouchableOpacity>
          ) : (
            <TouchableOpacity style={s.nextBtn} onPress={handleNext} activeOpacity={0.85} accessibilityLabel={`Next, step ${currentPage + 2} of ${SLIDES.length}`} accessibilityRole="button">
              <LinearGradient
                colors={['#4A62EB', '#6384FF']}
                start={{ x: 0, y: 0 }}
                end={{ x: 1, y: 0 }}
                style={s.nextGradient}
              >
                <Text style={s.nextText}>Next</Text>
              </LinearGradient>
            </TouchableOpacity>
          )}
        </View>
      </LinearGradient>
    </Modal>
  );
}

const s = StyleSheet.create({
  container: {
    flex: 1,
    justifyContent: 'center',
    alignItems: 'center',
  },
  skipBtn: {
    position: 'absolute',
    top: 56,
    right: 24,
    zIndex: 10,
    paddingHorizontal: 16,
    paddingVertical: 8,
  },
  skipText: {
    color: 'rgba(255,255,255,0.5)',
    fontSize: 15,
    fontFamily: 'Inter_500Medium',
    letterSpacing: 0.3,
  },
  scrollView: {
    flex: 1,
  },
  slide: {
    flex: 1,
    justifyContent: 'center',
    alignItems: 'center',
    paddingHorizontal: 40,
  },
  emojiContainer: {
    width: 120,
    height: 120,
    borderRadius: 60,
    backgroundColor: 'rgba(74, 98, 235, 0.12)',
    justifyContent: 'center',
    alignItems: 'center',
    marginBottom: 36,
    borderWidth: 1,
    borderColor: 'rgba(74, 98, 235, 0.2)',
  },
  emoji: {
    fontSize: 52,
  },
  title: {
    fontSize: 28,
    fontFamily: 'Inter_700Bold',
    color: '#FFFFFF',
    textAlign: 'center',
    marginBottom: 14,
    letterSpacing: -0.3,
  },
  subtitle: {
    fontSize: 16,
    fontFamily: 'Inter_400Regular',
    color: 'rgba(255,255,255,0.55)',
    textAlign: 'center',
    lineHeight: 24,
    maxWidth: 300,
  },
  bottomSection: {
    paddingBottom: 60,
    alignItems: 'center',
    gap: 28,
  },
  dotsRow: {
    flexDirection: 'row',
    gap: 8,
  },
  dot: {
    width: 8,
    height: 8,
    borderRadius: 4,
  },
  dotActive: {
    backgroundColor: '#4A62EB',
    width: 24,
  },
  dotInactive: {
    backgroundColor: 'rgba(255,255,255,0.2)',
  },
  nextBtn: {
    borderRadius: 14,
    overflow: 'hidden',
  },
  nextGradient: {
    paddingHorizontal: 48,
    paddingVertical: 16,
    borderRadius: 14,
  },
  nextText: {
    color: '#FFFFFF',
    fontSize: 17,
    fontFamily: 'Inter_600SemiBold',
    letterSpacing: 0.3,
  },
  getStartedBtn: {
    borderRadius: 14,
    overflow: 'hidden',
  },
  getStartedGradient: {
    paddingHorizontal: 56,
    paddingVertical: 18,
    borderRadius: 14,
  },
  getStartedText: {
    color: '#FFFFFF',
    fontSize: 18,
    fontFamily: 'Inter_700Bold',
    letterSpacing: 0.3,
  },
});
