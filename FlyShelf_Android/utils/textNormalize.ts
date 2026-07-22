/**
 * Canonical text normalizer for clipboard fingerprinting.
 * Normalizes line endings and trims whitespace so that
 * semantically-identical text produces the same fingerprint
 * regardless of OS-specific line-ending differences.
 *
 * Audit: Consolidates duplicate definitions from index.tsx and useDeviceSync.ts.
 */
export const normalizeTextForFingerprint = (text: string): string => {
  if (!text) return '';
  return text.replace(/\r\n/g, '\n').replace(/\r/g, '\n').trim();
};
