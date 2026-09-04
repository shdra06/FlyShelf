import React, { useState, useEffect, useRef, useCallback } from 'react';
import { View, Text, StyleSheet, TouchableOpacity, Modal, Platform, Vibration } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import * as Haptics from 'expo-haptics';
import * as Notifications from 'expo-notifications';
import { font, space, radius } from '../styles/theme';
import { useAppTheme } from '../hooks/useAppTheme';

// ═══════════════════════════════════════════════════════════
// POMODORO TIMER COMPONENT
// ═══════════════════════════════════════════════════════════

type PomodoroPhase = 'work' | 'shortBreak' | 'longBreak' | 'idle';

const WORK_MINUTES = 25;
const SHORT_BREAK_MINUTES = 5;
const LONG_BREAK_MINUTES = 15;
const SESSIONS_BEFORE_LONG_BREAK = 4;

interface PomodoroTimerProps {
  /** Task name to show in timer */
  taskName?: string;
  /** Callback when timer finishes a work session */
  onWorkComplete?: () => void;
  /** Callback when full Pomodoro set completes */
  onAllComplete?: () => void;
  /** Visible state */
  visible: boolean;
  /** Close handler */
  onClose: () => void;
}

export function PomodoroTimer({ taskName, onWorkComplete, onAllComplete, visible, onClose }: PomodoroTimerProps) {
  const { colors } = useAppTheme();

  const [phase, setPhase] = useState<PomodoroPhase>('idle');
  const [secondsLeft, setSecondsLeft] = useState(WORK_MINUTES * 60);
  const [isRunning, setIsRunning] = useState(false);
  const [sessionsCompleted, setSessionsCompleted] = useState(0);
  const intervalRef = useRef<ReturnType<typeof setInterval> | null>(null);

  const totalSeconds = phase === 'work' ? WORK_MINUTES * 60
    : phase === 'shortBreak' ? SHORT_BREAK_MINUTES * 60
    : phase === 'longBreak' ? LONG_BREAK_MINUTES * 60
    : WORK_MINUTES * 60;

  const progress = totalSeconds > 0 ? (totalSeconds - secondsLeft) / totalSeconds : 0;

  // ─── Countdown Logic ───
  useEffect(() => {
    if (isRunning && secondsLeft > 0) {
      intervalRef.current = setInterval(() => {
        setSecondsLeft(prev => {
          if (prev <= 1) {
            clearInterval(intervalRef.current!);
            handlePhaseEnd();
            return 0;
          }
          return prev - 1;
        });
      }, 1000);
    }
    return () => {
      if (intervalRef.current) clearInterval(intervalRef.current);
    };
  }, [isRunning, phase]);

  const handlePhaseEnd = useCallback(() => {
    setIsRunning(false);
    Haptics.notificationAsync(Haptics.NotificationFeedbackType.Success);
    if (Platform.OS === 'android') Vibration.vibrate([0, 500, 200, 500]);

    if (phase === 'work') {
      const newSessions = sessionsCompleted + 1;
      setSessionsCompleted(newSessions);
      onWorkComplete?.();

      // Schedule notification
      Notifications.scheduleNotificationAsync({
        content: {
          title: '🍅 Pomodoro Complete!',
          body: taskName ? `"${taskName}" — Time for a break!` : 'Great work! Take a break.',
        },
        trigger: null,
      }).catch(() => {});

      if (newSessions >= SESSIONS_BEFORE_LONG_BREAK) {
        setPhase('longBreak');
        setSecondsLeft(LONG_BREAK_MINUTES * 60);
      } else {
        setPhase('shortBreak');
        setSecondsLeft(SHORT_BREAK_MINUTES * 60);
      }
    } else {
      // Break ended → start new work session
      if (phase === 'longBreak') {
        setSessionsCompleted(0);
        onAllComplete?.();
      }
      setPhase('work');
      setSecondsLeft(WORK_MINUTES * 60);

      Notifications.scheduleNotificationAsync({
        content: {
          title: '🔔 Break Over!',
          body: 'Time to focus again.',
        },
        trigger: null,
      }).catch(() => {});
    }
  }, [phase, sessionsCompleted, taskName, onWorkComplete, onAllComplete]);

  const startTimer = () => {
    if (phase === 'idle') {
      setPhase('work');
      setSecondsLeft(WORK_MINUTES * 60);
    }
    setIsRunning(true);
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Medium);
  };

  const pauseTimer = () => {
    setIsRunning(false);
    if (intervalRef.current) clearInterval(intervalRef.current);
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Light);
  };

  const resetTimer = () => {
    setIsRunning(false);
    if (intervalRef.current) clearInterval(intervalRef.current);
    setPhase('idle');
    setSecondsLeft(WORK_MINUTES * 60);
    setSessionsCompleted(0);
    Haptics.impactAsync(Haptics.ImpactFeedbackStyle.Heavy);
  };

  const skipPhase = () => {
    setIsRunning(false);
    if (intervalRef.current) clearInterval(intervalRef.current);
    handlePhaseEnd();
  };

  // ─── Format Time ───
  const minutes = Math.floor(secondsLeft / 60);
  const seconds = secondsLeft % 60;
  const timeStr = `${minutes.toString().padStart(2, '0')}:${seconds.toString().padStart(2, '0')}`;

  const phaseColor = phase === 'work' ? '#EF4444' : phase === 'shortBreak' ? '#34D399' : phase === 'longBreak' ? '#60A5FA' : colors.accent.primary;
  const phaseLabel = phase === 'work' ? '🍅 FOCUS' : phase === 'shortBreak' ? '☕ SHORT BREAK' : phase === 'longBreak' ? '🌴 LONG BREAK' : '🍅 POMODORO';
  const phaseEmoji = phase === 'work' ? '🍅' : phase === 'shortBreak' ? '☕' : phase === 'longBreak' ? '🌴' : '🍅';

  return (
    <Modal visible={visible} transparent animationType="slide" onRequestClose={onClose} statusBarTranslucent>
      <View style={[styles.overlay, { backgroundColor: 'rgba(0,0,0,0.85)' }]}>
        <View style={[styles.container, { backgroundColor: colors.bg.card }]}>
          {/* Header */}
          <View style={styles.header}>
            <Text style={[styles.phaseLabel, { color: phaseColor }]}>{phaseLabel}</Text>
            <TouchableOpacity onPress={onClose} style={styles.closeBtn}>
              <Ionicons name="close" size={24} color={colors.text.secondary} />
            </TouchableOpacity>
          </View>

          {/* Task Name */}
          {taskName && (
            <Text style={[styles.taskName, { color: colors.text.primary }]} numberOfLines={2}>
              {taskName}
            </Text>
          )}

          {/* Timer Circle */}
          <View style={styles.timerCircleContainer}>
            <View style={[styles.timerCircle, { borderColor: phaseColor + '30' }]}>
              {/* Progress arc (simplified) */}
              <View style={[styles.timerProgressBg, { borderColor: phaseColor + '15' }]} />
              <Text style={[styles.timerText, { color: colors.text.primary }]}>{timeStr}</Text>
              <Text style={[styles.timerPhaseEmoji]}>{phaseEmoji}</Text>
            </View>
          </View>

          {/* Session Dots */}
          <View style={styles.sessionDots}>
            {Array.from({ length: SESSIONS_BEFORE_LONG_BREAK }).map((_, i) => (
              <View
                key={i}
                style={[
                  styles.dot,
                  { backgroundColor: i < sessionsCompleted ? '#EF4444' : colors.text.tertiary + '30' }
                ]}
              />
            ))}
          </View>
          <Text style={[styles.sessionLabel, { color: colors.text.tertiary }]}>
            {sessionsCompleted}/{SESSIONS_BEFORE_LONG_BREAK} sessions
          </Text>

          {/* Controls */}
          <View style={styles.controls}>
            <TouchableOpacity onPress={resetTimer} style={[styles.controlBtn, { backgroundColor: colors.bg.elevated }]}>
              <Ionicons name="refresh" size={24} color={colors.text.secondary} />
            </TouchableOpacity>

            <TouchableOpacity
              onPress={isRunning ? pauseTimer : startTimer}
              style={[styles.mainControlBtn, { backgroundColor: phaseColor }]}
            >
              <Ionicons name={isRunning ? 'pause' : 'play'} size={32} color="#FFF" />
            </TouchableOpacity>

            <TouchableOpacity onPress={skipPhase} style={[styles.controlBtn, { backgroundColor: colors.bg.elevated }]}>
              <Ionicons name="play-forward" size={24} color={colors.text.secondary} />
            </TouchableOpacity>
          </View>

          {/* Info */}
          <Text style={[styles.infoText, { color: colors.text.tertiary }]}>
            {phase === 'idle' ? 'Tap play to start a 25-min focus session' :
             phase === 'work' ? 'Stay focused! Avoid distractions.' :
             'Relax, stretch, get water 💧'}
          </Text>
        </View>
      </View>
    </Modal>
  );
}

/** Small inline timer button to show on todo items */
export function PomodoroInlineButton({
  onPress,
  isActive,
  timeStr,
}: {
  onPress: () => void;
  isActive?: boolean;
  timeStr?: string;
}) {
  const { colors } = useAppTheme();
  return (
    <TouchableOpacity
      onPress={onPress}
      style={[
        styles.inlineBtn,
        { backgroundColor: isActive ? '#EF444420' : colors.bg.elevated }
      ]}
    >
      <Text style={{ fontSize: 12 }}>🍅</Text>
      {timeStr && <Text style={[styles.inlineTime, { color: isActive ? '#EF4444' : colors.text.tertiary }]}>{timeStr}</Text>}
    </TouchableOpacity>
  );
}

const styles = StyleSheet.create({
  overlay: {
    flex: 1,
    justifyContent: 'center',
    alignItems: 'center',
    padding: 24,
  },
  container: {
    width: '100%',
    maxWidth: 380,
    borderRadius: 28,
    padding: 24,
    alignItems: 'center',
  },
  header: {
    flexDirection: 'row',
    justifyContent: 'space-between',
    alignItems: 'center',
    width: '100%',
    marginBottom: 8,
  },
  phaseLabel: {
    fontFamily: font.bold,
    fontSize: 14,
    letterSpacing: 1.5,
  },
  closeBtn: {
    width: 36,
    height: 36,
    borderRadius: 18,
    justifyContent: 'center',
    alignItems: 'center',
  },
  taskName: {
    fontFamily: font.semibold,
    fontSize: 18,
    textAlign: 'center',
    marginBottom: 20,
  },
  timerCircleContainer: {
    marginVertical: 16,
  },
  timerCircle: {
    width: 200,
    height: 200,
    borderRadius: 100,
    borderWidth: 6,
    justifyContent: 'center',
    alignItems: 'center',
  },
  timerProgressBg: {
    position: 'absolute',
    width: 200,
    height: 200,
    borderRadius: 100,
    borderWidth: 6,
  },
  timerText: {
    fontFamily: font.bold,
    fontSize: 48,
    letterSpacing: 2,
  },
  timerPhaseEmoji: {
    fontSize: 24,
    marginTop: 4,
  },
  sessionDots: {
    flexDirection: 'row',
    gap: 8,
    marginTop: 16,
  },
  dot: {
    width: 12,
    height: 12,
    borderRadius: 6,
  },
  sessionLabel: {
    fontFamily: font.medium,
    fontSize: 12,
    marginTop: 8,
  },
  controls: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 20,
    marginTop: 24,
  },
  controlBtn: {
    width: 48,
    height: 48,
    borderRadius: 24,
    justifyContent: 'center',
    alignItems: 'center',
  },
  mainControlBtn: {
    width: 64,
    height: 64,
    borderRadius: 32,
    justifyContent: 'center',
    alignItems: 'center',
    elevation: 4,
    shadowColor: '#000',
    shadowOffset: { width: 0, height: 2 },
    shadowOpacity: 0.3,
    shadowRadius: 4,
  },
  infoText: {
    fontFamily: font.medium,
    fontSize: 13,
    textAlign: 'center',
    marginTop: 16,
  },
  inlineBtn: {
    flexDirection: 'row',
    alignItems: 'center',
    paddingHorizontal: 8,
    paddingVertical: 4,
    borderRadius: 12,
    gap: 4,
  },
  inlineTime: {
    fontFamily: font.medium,
    fontSize: 11,
  },
});
