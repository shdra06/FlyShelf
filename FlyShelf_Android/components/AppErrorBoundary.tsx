import React, { Component, ErrorInfo, ReactNode } from 'react';
import { View, Text, TouchableOpacity, StyleSheet, ScrollView } from 'react-native';
import { syncLog, logFatalCrash } from '../utils/debugLog';

interface Props {
  children: ReactNode;
  fallbackTitle?: string;
}

interface State {
  hasError: boolean;
  error: Error | null;
  errorInfo: ErrorInfo | null;
}

/**
 * Enhanced error boundary that catches component crashes and shows recovery UI.
 * Wrap each tab screen to prevent one screen's crash from killing the entire app.
 */
export default class AppErrorBoundary extends Component<Props, State> {
  constructor(props: Props) {
    super(props);
    this.state = { hasError: false, error: null, errorInfo: null };
  }

  static getDerivedStateFromError(error: Error): Partial<State> {
    return { hasError: true, error };
  }

  componentDidCatch(error: Error, errorInfo: ErrorInfo): void {
    syncLog('CRASH', `Screen crash: ${error?.message || 'Unknown error'}`);
    syncLog('ERROR_BOUNDARY', `Component crashed: ${error.message}\n${errorInfo.componentStack}`);
    logFatalCrash(error, errorInfo?.componentStack).catch(() => {});
    this.setState({ errorInfo });
  }

  handleReset = (): void => {
    this.setState({ hasError: false, error: null, errorInfo: null });
  };

  render(): ReactNode {
    if (this.state.hasError) {
      const err = this.state.error;
      const info = this.state.errorInfo;
      return (
        <View style={styles.container}>
          <Text style={styles.emoji}>💥</Text>
          <Text style={styles.title}>
            {this.props.fallbackTitle || 'Something went wrong'}
          </Text>
          <Text style={styles.message}>
            {err?.message || 'An unexpected error occurred'}
          </Text>
          <TouchableOpacity style={styles.button} onPress={this.handleReset}>
            <Text style={styles.buttonText}>Try Again</Text>
          </TouchableOpacity>
          {/* Always show full error details for debugging */}
          <ScrollView style={styles.debugScroll}>
            {err?.stack ? (
              <Text style={styles.debugText} selectable>
                {'── Error Stack ──\n' + err.stack}
              </Text>
            ) : null}
            {info?.componentStack ? (
              <Text style={[styles.debugText, { marginTop: 12 }]} selectable>
                {'── Component Stack ──\n' + info.componentStack}
              </Text>
            ) : null}
          </ScrollView>
        </View>
      );
    }
    return this.props.children;
  }
}

const styles = StyleSheet.create({
  container: { flex: 1, justifyContent: 'center', alignItems: 'center', padding: 32, backgroundColor: '#1a1a2e' },
  emoji: { fontSize: 64, marginBottom: 16 },
  title: { fontSize: 22, fontWeight: '700', color: '#fff', marginBottom: 8, textAlign: 'center' },
  message: { fontSize: 14, color: '#aaa', marginBottom: 24, textAlign: 'center', maxWidth: 300 },
  button: { backgroundColor: '#6c63ff', paddingHorizontal: 32, paddingVertical: 12, borderRadius: 12 },
  buttonText: { color: '#fff', fontSize: 16, fontWeight: '600' },
  debugScroll: { marginTop: 24, maxHeight: 400, width: '100%', backgroundColor: '#111', borderRadius: 8, padding: 12 },
  debugText: { fontSize: 11, color: '#e0e0e0', fontFamily: 'monospace', lineHeight: 16 },
});
