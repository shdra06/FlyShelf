import { syncLog } from './debugLog';

export interface BackoffState {
  retryCount: number;
  isInSlowMode: boolean;
}

/**
 * Calculate delay with exponential backoff.
 * 1s → 2s → 4s → 8s → 16s → 30s (cap)
 * After maxRetries, switches to slowModeInterval.
 *
 * Fixes: C4 (unbounded retry loops)
 */
export function getBackoffDelay(state: BackoffState): number {
  if (state.isInSlowMode) return 60_000; // 60s in slow mode
  return Math.min(1000 * Math.pow(2, state.retryCount), 30_000);
}

/**
 * Update backoff state after a failure.
 */
export function recordFailure(state: BackoffState, maxRetries: number = 10): BackoffState {
  const newCount = state.retryCount + 1;
  const isInSlowMode = newCount >= maxRetries;
  if (isInSlowMode && !state.isInSlowMode) {
    syncLog('BACKOFF', `Entering slow mode after ${maxRetries} failures (60s interval)`);
  }
  return { retryCount: newCount, isInSlowMode };
}

/**
 * Reset backoff state after a success.
 */
export function recordSuccess(state: BackoffState): BackoffState {
  if (state.retryCount > 0) {
    syncLog('BACKOFF', `Connection restored after ${state.retryCount} retries`);
  }
  return { retryCount: 0, isInSlowMode: false };
}
