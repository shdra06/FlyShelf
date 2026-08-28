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

// ═══════════════════════════════════════════════════════════════════
// AUDIT FIX: FuzzyMatcher — TypeScript port of PC's FuzzyMatcher.cs
// Provides identical search ranking on mobile for cross-platform parity.
//
// Supports:
//   1. Exact substring matching (case-insensitive)
//   2. Multi-word queries (all query tokens must match)
//   3. Word prefix & stemming matching (e.g. 'kube' -> 'kubernetes', 'periods' -> 'period')
//   4. Strict 1-edit typo tolerance for words >= 4 chars (zero false positives)
// ═══════════════════════════════════════════════════════════════════

const MAX_SEARCHABLE_LENGTH = 4096;
const WORD_SEPARATOR_RE = /[\s,.\;:!?\(\)\[\]\{\}"'\/\\@#$%^&*+=<>~`|_-]+/;

function splitWords(input: string): string[] {
  if (!input) return [];
  return input.split(WORD_SEPARATOR_RE).filter(Boolean);
}

/**
 * Bounded Levenshtein distance check (max 1 edit distance).
 * Returns true if edit distance between s and t is <= maxDistance.
 */
function levenshteinWithinLimit(s: string, t: string, maxDistance: number): boolean {
  const sLen = s.length;
  const tLen = t.length;
  if (Math.abs(sLen - tLen) > maxDistance) return false;
  if (sLen === 0) return tLen <= maxDistance;
  if (tLen === 0) return sLen <= maxDistance;
  if (Math.max(sLen, tLen) > 64) return false; // Guard against huge words

  const sLow = s.toLowerCase();
  const tLow = t.toLowerCase();

  let prevRow = new Array(tLen + 1);
  let currRow = new Array(tLen + 1);

  for (let i = 0; i <= tLen; i++) prevRow[i] = i;

  for (let i = 0; i < sLen; i++) {
    currRow[0] = i + 1;
    let minInRow = currRow[0];

    for (let j = 0; j < tLen; j++) {
      const cost = sLow[i] === tLow[j] ? 0 : 1;
      const ins = currRow[j] + 1;
      const del = prevRow[j + 1] + 1;
      const rep = prevRow[j] + cost;
      const val = Math.min(ins, Math.min(del, rep));
      currRow[j + 1] = val;
      if (val < minInRow) minInRow = val;
    }

    if (minInRow > maxDistance) return false;

    // Swap rows
    [prevRow, currRow] = [currRow, prevRow];
  }

  return prevRow[tLen] <= maxDistance;
}

/**
 * Check if a single query word matches any word in the text.
 */
function isWordMatched(queryWord: string, textWords: string[], fullText: string): boolean {
  const qwLower = queryWord.toLowerCase();

  // Direct substring check
  if (fullText.toLowerCase().includes(qwLower)) return true;

  // Stem: remove trailing 's' for basic plural handling
  const qStem = qwLower.length > 3 && qwLower.endsWith('s')
    ? qwLower.slice(0, -1)
    : null;

  for (const tw of textWords) {
    const twLower = tw.toLowerCase();
    if (twLower.length === 0) continue;

    // Exact word match
    if (twLower === qwLower) return true;

    // Prefix match (e.g. "kube" -> "kubernetes")
    if (twLower.startsWith(qwLower)) return true;

    // Stemmed match (e.g. "periods" -> "period")
    if (qStem && (twLower === qStem || twLower.startsWith(qStem))) return true;

    // Text word is plural of query (e.g. query "period" -> text "periods")
    if (twLower.length > 3 && twLower.endsWith('s') && twLower.slice(0, -1) === qwLower) return true;

    // Strict typo match: only for words >= 4 chars with max 1 edit
    if (qwLower.length >= 4 && Math.abs(qwLower.length - twLower.length) <= 1) {
      if (levenshteinWithinLimit(qwLower, twLower, 1)) return true;
    }
  }

  return false;
}

/**
 * Returns true if `text` matches `query` using the multi-tier matching algorithm.
 */
export function fuzzyIsMatch(query: string, text: string): boolean {
  if (!query?.trim() || !text?.trim()) return false;

  const q = query.trim();

  // 1. Exact substring match (fastest path)
  if (text.toLowerCase().includes(q.toLowerCase())) return true;

  // 2. Tokenize query words
  const queryWords = splitWords(q);
  if (queryWords.length === 0) return true;

  const textToSearch = text.length > MAX_SEARCHABLE_LENGTH ? text.slice(0, MAX_SEARCHABLE_LENGTH) : text;
  const textWords = splitWords(textToSearch);
  if (textWords.length === 0) return false;

  // ALL query words must match at least one word in text
  for (const qw of queryWords) {
    if (qw.length === 0) continue;
    if (!isWordMatched(qw, textWords, textToSearch)) return false;
  }

  return true;
}

/**
 * Returns a relevance score (0.0 to 1.0) indicating how well `text`
 * matches `query`. Higher = better match. Identical logic to PC's FuzzyMatcher.Score().
 */
export function fuzzyScore(query: string, text: string): number {
  if (!query?.trim() || !text?.trim()) return 0.0;

  const q = query.trim();
  const t = text;
  const qLower = q.toLowerCase();
  const tLower = t.toLowerCase();

  // 1.0 — Exact full match
  if (tLower === qLower) return 1.0;

  // 0.95 — Text starts with query
  if (tLower.startsWith(qLower)) return 0.95;

  // 0.85+ — Exact substring match
  const subIdx = tLower.indexOf(qLower);
  if (subIdx >= 0) {
    const ratio = q.length / Math.min(t.length, 200);
    return 0.85 + Math.min(ratio * 0.1, 0.1);
  }

  // Word-level scoring
  const queryWords = splitWords(q);
  if (queryWords.length === 0) return 0.0;

  const textToSearch = t.length > MAX_SEARCHABLE_LENGTH ? t.slice(0, MAX_SEARCHABLE_LENGTH) : t;
  const textWords = splitWords(textToSearch);
  if (textWords.length === 0) return 0.0;

  let matchCount = 0;
  let scoreSum = 0.0;

  for (const qw of queryWords) {
    const qwLower = qw.toLowerCase();
    let bestWordScore = 0.0;

    for (const tw of textWords) {
      const twLower = tw.toLowerCase();
      if (twLower === qwLower) {
        bestWordScore = Math.max(bestWordScore, 1.0);
      } else if (twLower.startsWith(qwLower)) {
        bestWordScore = Math.max(bestWordScore, 0.85);
      } else if (qwLower.length >= 4 && Math.abs(qwLower.length - twLower.length) <= 1 && levenshteinWithinLimit(qwLower, twLower, 1)) {
        bestWordScore = Math.max(bestWordScore, 0.65);
      }
    }

    if (bestWordScore > 0.0) {
      matchCount++;
      scoreSum += bestWordScore;
    }
  }

  if (matchCount === queryWords.length) {
    return 0.5 + (0.3 * (scoreSum / queryWords.length));
  }
  if (matchCount > 0) {
    return 0.2 + (0.2 * (matchCount / queryWords.length));
  }

  return 0.0;
}

/**
 * Returns true if query matches any of the provided texts.
 */
export function fuzzyIsMatchAny(query: string, ...texts: (string | null | undefined)[]): boolean {
  for (const t of texts) {
    if (t && fuzzyIsMatch(query, t)) return true;
  }
  return false;
}

/**
 * Returns the best (highest) score across all provided texts.
 */
export function fuzzyScoreBest(query: string, ...texts: (string | null | undefined)[]): number {
  let best = 0;
  for (const t of texts) {
    if (t) {
      const s = fuzzyScore(query, t);
      if (s > best) best = s;
    }
  }
  return best;
}
