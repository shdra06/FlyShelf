import React, { Component, ErrorInfo } from 'react';
import { StyleSheet, View, Text, TouchableOpacity, ScrollView, Platform, Alert, ActivityIndicator } from 'react-native';
import AsyncStorage from '@react-native-async-storage/async-storage';
import * as Clipboard from 'expo-clipboard';
import * as FileSystem from 'expo-file-system/legacy';
import { colors, font, radius, space, shadows } from '../styles/theme';
import { syncLog } from '../utils/debugLog';
import { DOWNLOAD_BASE, SYNC_CACHE_BASE, CONVERTED_BASE, IMAGE_CACHE_BASE } from '../utils/clipTypes';

interface Props {
  children: React.ReactNode;
}

interface State {
  hasError: boolean;
  error: Error | null;
  errorInfo: ErrorInfo | null;
  previousCrashReport: string | null;
  isClearingCache: boolean;
  isResettingSettings: boolean;
}

export default class ErrorBoundary extends Component<Props, State> {
  private originalHandler: any = null;

  constructor(props: Props) {
    super(props);
    this.state = {
      hasError: false,
      error: null,
      errorInfo: null,
      previousCrashReport: null,
      isClearingCache: false,
      isResettingSettings: false,
    };
  }

  static getDerivedStateFromError(error: Error): Partial<State> {
    return { hasError: true, error };
  }

  componentDidCatch(error: Error, errorInfo: ErrorInfo) {
    this.setState({ errorInfo });
    const crashDetails = `[Render Crash]\nMessage: ${error.message}\nStack: ${error.stack}\nComponent Stack: ${errorInfo.componentStack}`;
    syncLog('CRASH', crashDetails);
    
    // Save crash report to AsyncStorage for reference
    AsyncStorage.setItem('last_crash_error', crashDetails).catch(() => {});
  }

  componentDidMount() {
    // Intercept async / unhandled errors
    if (Platform.OS !== 'web') {
      try {
        const globalHandler = ErrorUtils.getGlobalHandler();
        this.originalHandler = globalHandler;
        
        ErrorUtils.setGlobalHandler(async (error: any, isFatal?: boolean) => {
          const crashDetails = `[Fatal Async Crash]\nFatal: ${isFatal}\nMessage: ${error?.message || error}\nStack: ${error?.stack}`;
          syncLog('CRASH', crashDetails);
          
          await AsyncStorage.setItem('last_crash_error', crashDetails).catch(() => {});
          
          // Force render of ErrorBoundary Safe Mode UI
          this.setState({
            hasError: true,
            error: error instanceof Error ? error : new Error(String(error)),
          });

          // Call original handler to let RN log it appropriately (won't crash since hasError is true)
          if (globalHandler) {
            globalHandler(error, isFatal);
          }
        });
      } catch (err) {
        syncLog('CRASH', `Failed to hook global ErrorUtils handler: ${err}`);
      }
    }

    // Check for previous session crash logs
    this.checkPreviousCrash();
  }

  componentWillUnmount() {
    // Restore original handler
    if (Platform.OS !== 'web' && this.originalHandler) {
      ErrorUtils.setGlobalHandler(this.originalHandler);
    }
  }

  async checkPreviousCrash() {
    try {
      const lastCrash = await AsyncStorage.getItem('last_crash_error');
      if (lastCrash) {
        this.setState({ previousCrashReport: lastCrash });
      }
    } catch {}
  }

  clearPreviousCrash = async () => {
    try {
      await AsyncStorage.removeItem('last_crash_error');
      this.setState({ previousCrashReport: null });
    } catch {}
  };

  handleCopyCrashReport = async () => {
    const errorMsg = this.state.error?.message || 'Unknown Error';
    const errorStack = this.state.error?.stack || 'No Stack Trace';
    const componentStack = this.state.errorInfo?.componentStack || 'No Component Stack';
    const prevCrash = this.state.previousCrashReport || '';

    const report = `================ FLYSHELF CRASH REPORT ================
OS: ${Platform.OS} (v${Platform.Version})
Date: ${new Date().toISOString()}

--- CURRENT ERROR ---
Message: ${errorMsg}
Stack: ${errorStack}
Component Stack: ${componentStack}

--- PREVIOUS SESSION CRASH (IF ANY) ---
${prevCrash}
======================================================`;

    await Clipboard.setStringAsync(report);
    Alert.alert('Copied ✅', 'Crash diagnostics copied to clipboard. Share this with developers.');
  };

  handleClearCache = async () => {
    this.setState({ isClearingCache: true });
    try {
      // Wipes temporary sync caches and saved media
      const targets = [SYNC_CACHE_BASE, IMAGE_CACHE_BASE, CONVERTED_BASE];
      let clearedCount = 0;
      for (const path of targets) {
        try {
          const info = await FileSystem.getInfoAsync(path);
          if (info.exists) {
            await FileSystem.deleteAsync(path, { idempotent: true });
            clearedCount++;
          }
        } catch {}
      }
      Alert.alert('Cache Cleared 🧹', `Successfully cleaned up ${clearedCount} local directories.`);
    } catch (e: any) {
      Alert.alert('Error', e?.message || 'Failed to clear cache.');
    } finally {
      this.setState({ isClearingCache: false });
    }
  };

  handleFactoryReset = () => {
    Alert.alert(
      'Factory Reset ⚠️',
      'This will erase ALL FlyShelf settings, device names, and pairing keys from this phone. This action CANNOT be undone.',
      [
        { text: 'Cancel', style: 'cancel' },
        {
          text: 'Reset Everything',
          style: 'destructive',
          onPress: async () => {
            this.setState({ isResettingSettings: true });
            try {
              await AsyncStorage.clear();
              // Re-create root crash details storage
              await this.clearPreviousCrash();
              Alert.alert('Wiped Successfully', 'All storage keys have been reset. Restart the app.', [
                { text: 'OK', onPress: () => this.handleRestart() }
              ]);
            } catch (e: any) {
              Alert.alert('Error', e?.message || 'Failed to factory reset.');
            } finally {
              this.setState({ isResettingSettings: false });
            }
          }
        }
      ]
    );
  };

  handleRestart = () => {
    this.setState({
      hasError: false,
      error: null,
      errorInfo: null,
    });
  };

  render() {
    const { hasError, error, errorInfo, previousCrashReport } = this.state;

    // Trigger Safe Mode render if there was an active error
    if (hasError) {
      const errorMsg = error?.message || 'Unknown JavaScript Error';
      const errorStack = error?.stack || 'No stack trace details are available.';
      const componentStack = errorInfo?.componentStack || '';

      return (
        <View style={s.container}>
          <View style={s.warningBanner}>
            <Text style={s.bannerEmoji}>⚠️</Text>
            <Text style={s.bannerTitle}>FlyShelf Safe Mode</Text>
            <Text style={s.bannerSubtitle}>An unexpected crash occurred. The system has booted into Safe Mode to protect your configuration.</Text>
          </View>

          <View style={s.card}>
            <Text style={s.sectionHeader}>Crash Diagnostics</Text>
            <ScrollView style={s.logContainer} showsVerticalScrollIndicator>
              <Text style={s.errorLabel}>ERROR MESSAGE:</Text>
              <Text style={s.errorMessage} selectable>{errorMsg}</Text>
              
              {componentStack ? (
                <>
                  <Text style={s.errorLabel}>COMPONENT STACK:</Text>
                  <Text style={s.errorStack} selectable>{componentStack.trim()}</Text>
                </>
              ) : null}

              <Text style={s.errorLabel}>STACK TRACE:</Text>
              <Text style={s.errorStack} selectable>{errorStack.trim()}</Text>
            </ScrollView>
          </View>

          <View style={s.card}>
            <Text style={s.sectionHeader}>Recovery Tools</Text>
            <View style={s.btnRow}>
              <TouchableOpacity style={s.btnPrimary} onPress={this.handleCopyCrashReport}>
                <Text style={s.btnText}>📋 Copy Crash Report</Text>
              </TouchableOpacity>
              
              <TouchableOpacity 
                style={s.btnSecondary} 
                onPress={this.handleClearCache}
                disabled={this.state.isClearingCache}
              >
                {this.state.isClearingCache ? (
                  <ActivityIndicator size="small" color="#FFF" />
                ) : (
                  <Text style={s.btnText}>🧹 Clear Cache</Text>
                )}
              </TouchableOpacity>
            </View>

            <TouchableOpacity 
              style={[s.btnSecondary, { marginTop: 10, borderColor: colors.accent.errorDim }]} 
              onPress={this.handleFactoryReset}
              disabled={this.state.isResettingSettings}
            >
              {this.state.isResettingSettings ? (
                <ActivityIndicator size="small" color={colors.accent.error} />
              ) : (
                <Text style={[s.btnText, { color: colors.accent.error }]}>⚠️ Factory Reset settings</Text>
              )}
            </TouchableOpacity>
          </View>

          <TouchableOpacity style={s.btnRestart} onPress={this.handleRestart}>
            <Text style={s.btnRestartText}>🔄 Attempt Normal Restart</Text>
          </TouchableOpacity>
        </View>
      );
    }

    // Normal rendering of the application
    return (
      <>
        {this.props.children}
        {previousCrashReport && (
          <View style={s.overlayToast}>
            <View style={{ flex: 1 }}>
              <Text style={s.toastTitle}>⚠️ Crash Detected in Last Session</Text>
              <Text style={s.toastBody} numberOfLines={2}>{previousCrashReport.split('\n')[1] || previousCrashReport}</Text>
            </View>
            <View style={{ gap: 6 }}>
              <TouchableOpacity 
                style={s.toastActionBtn} 
                onPress={() => {
                  this.setState({
                    hasError: true,
                    error: new Error("Reviewing last session's crash."),
                    errorInfo: { componentStack: 'Previous session dump.' }
                  });
                }}
              >
                <Text style={s.toastActionText}>Inspect</Text>
              </TouchableOpacity>
              <TouchableOpacity style={[s.toastActionBtn, { backgroundColor: 'rgba(255,255,255,0.1)' }]} onPress={this.clearPreviousCrash}>
                <Text style={[s.toastActionText, { color: colors.text.secondary }]}>Dismiss</Text>
              </TouchableOpacity>
            </View>
          </View>
        )}
      </>
    );
  }
}

const s = StyleSheet.create({
  container: {
    flex: 1,
    backgroundColor: colors.bg.base,
    paddingTop: 60,
    paddingHorizontal: space.xl,
    paddingBottom: 30,
  },
  warningBanner: {
    alignItems: 'center',
    marginBottom: space.xl,
    backgroundColor: 'rgba(248,113,113,0.06)',
    borderWidth: 1,
    borderColor: 'rgba(248,113,113,0.15)',
    borderRadius: radius.xl,
    padding: space.xl,
  },
  bannerEmoji: {
    fontSize: 40,
    marginBottom: 8,
  },
  bannerTitle: {
    fontSize: 22,
    fontFamily: font.extrabold,
    color: colors.accent.error,
    letterSpacing: -0.5,
    marginBottom: 6,
  },
  bannerSubtitle: {
    fontSize: 12,
    fontFamily: font.medium,
    color: colors.text.secondary,
    textAlign: 'center',
    lineHeight: 18,
  },
  card: {
    backgroundColor: colors.bg.card,
    borderRadius: radius.xl,
    padding: space.xl,
    marginBottom: space.lg,
    borderWidth: 1,
    borderColor: colors.border.subtle,
    borderTopColor: colors.innerHighlight,
    ...shadows.card,
  },
  sectionHeader: {
    color: colors.text.primary,
    fontSize: 15,
    fontFamily: font.bold,
    marginBottom: space.md,
    letterSpacing: -0.2,
  },
  logContainer: {
    maxHeight: 180,
    backgroundColor: colors.bg.input,
    borderRadius: radius.md,
    padding: space.md,
    borderWidth: 1,
    borderColor: colors.border.subtle,
  },
  errorLabel: {
    fontSize: 10,
    fontFamily: font.bold,
    color: colors.text.tertiary,
    marginTop: 10,
    marginBottom: 4,
    letterSpacing: 0.5,
  },
  errorMessage: {
    color: colors.accent.error,
    fontSize: 13,
    fontFamily: font.semibold,
    lineHeight: 18,
  },
  errorStack: {
    color: colors.text.secondary,
    fontSize: 11,
    fontFamily: Platform.OS === 'ios' ? 'Menlo' : 'monospace',
    lineHeight: 16,
  },
  btnRow: {
    flexDirection: 'row',
    gap: 10,
  },
  btnPrimary: {
    flex: 1,
    backgroundColor: colors.accent.primary,
    paddingVertical: 14,
    borderRadius: radius.md,
    alignItems: 'center',
    justifyContent: 'center',
    ...shadows.glow(colors.accent.primary),
  },
  btnSecondary: {
    flex: 1,
    backgroundColor: colors.bg.input,
    borderWidth: 1,
    borderColor: colors.border.medium,
    paddingVertical: 14,
    borderRadius: radius.md,
    alignItems: 'center',
    justifyContent: 'center',
  },
  btnText: {
    color: '#FFF',
    fontSize: 13,
    fontFamily: font.bold,
  },
  btnRestart: {
    backgroundColor: '#10B981',
    paddingVertical: 16,
    borderRadius: radius.lg,
    alignItems: 'center',
    marginTop: 'auto',
    ...shadows.glow('#10B981'),
  },
  btnRestartText: {
    color: '#FFF',
    fontSize: 15,
    fontFamily: font.extrabold,
    letterSpacing: 0.3,
  },
  overlayToast: {
    position: 'absolute',
    bottom: 90,
    left: space.xl,
    right: space.xl,
    backgroundColor: 'rgba(30,34,45,0.95)',
    borderWidth: 1,
    borderColor: colors.accent.warningDim,
    borderRadius: radius.lg,
    padding: 16,
    flexDirection: 'row',
    alignItems: 'center',
    gap: 12,
    ...shadows.elevated,
    zIndex: 9999,
  },
  toastTitle: {
    color: colors.accent.warning,
    fontSize: 13,
    fontFamily: font.bold,
    marginBottom: 2,
  },
  toastBody: {
    color: colors.text.secondary,
    fontSize: 11,
    fontFamily: font.medium,
    lineHeight: 16,
  },
  toastActionBtn: {
    backgroundColor: colors.accent.warning,
    paddingHorizontal: 12,
    paddingVertical: 8,
    borderRadius: 8,
    alignItems: 'center',
  },
  toastActionText: {
    color: '#000',
    fontSize: 11,
    fontFamily: font.bold,
  },
});
