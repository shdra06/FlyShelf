import { useRef, useEffect } from 'react';

/**
 * Returns a ref that always holds the latest value.
 * Use in long-running async callbacks to avoid stale closures.
 *
 * Fixes: H16 (stale closures in sync hooks)
 *
 * @example
 * const countRef = useLatest(count);
 * useEffect(() => {
 *   const timer = setInterval(() => {
 *     console.log(countRef.current); // Always current value
 *   }, 1000);
 *   return () => clearInterval(timer);
 * }, []); // No need to add count to deps!
 */
export function useLatest<T>(value: T): React.MutableRefObject<T> {
  const ref = useRef(value);
  useEffect(() => {
    ref.current = value;
  });
  return ref;
}
