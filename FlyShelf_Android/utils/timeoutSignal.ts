/**
 * Hermes-safe timeout signal — AbortSignal.timeout() is NOT available in
 * all Hermes versions, causing "undefined is not a function" crashes.
 * Returns the signal; a clear function is attached to prevent timer leaks (C-3 fix).
 */
export function createTimeoutSignal(ms: number): AbortSignal {
  const controller = new AbortController();
  const timerId = setTimeout(() => controller.abort(), ms);
  // Attach clear function to the signal for cleanup
  (controller.signal as any)._clearTimeout = () => clearTimeout(timerId);
  return controller.signal;
}

/** Clear the timeout associated with a createTimeoutSignal signal */
export function clearTimeoutSignal(signal: AbortSignal): void {
  if ((signal as any)?._clearTimeout) (signal as any)._clearTimeout();
}
