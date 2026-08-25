// Copyright © 2024-2026 The FlyShelf Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;

namespace FlyShelf.Classes
{
    /// <summary>
    /// Ultra-fast, zero-allocation fuzzy text matcher using word-level tokenization and packed-integer trigram similarity.
    /// Zero heap allocations on hot search paths.
    /// 
    /// Matching strategy (in order of priority):
    ///   1. Exact substring match (fastest)
    ///   2. All-words match (query words found anywhere in text, any order)
    ///   3. Trigram fuzzy match (typo tolerance for individual words)
    /// </summary>
    public static class FuzzyMatcher
    {
        // Trigram similarity threshold — lower = more permissive.
        private const double FuzzyThreshold = 0.25;

        // Minimum query length for fuzzy matching (skip for very short queries)
        private const int MinFuzzyLength = 3;

        private const int MaxSearchableContentLength = 4096;

        private static readonly char[] _wordSeparators = 
            { ' ', '\t', '\n', '\r', ',', '.', ';', ':', '!', '?', '(', ')', '[', ']', '{', '}', '"', '\'', '/', '\\', '-', '_' };

        /// <summary>
        /// Returns true if <paramref name="text"/> matches <paramref name="query"/>
        /// via exact substring, word-level, or fuzzy trigram matching.
        /// Both inputs are case-insensitive.
        /// </summary>
        public static bool IsMatch(string query, string text)
        {
            if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(text))
                return false;

            string q = query.Trim();

            // 1. Exact substring (case-insensitive) — fastest path
            if (text.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            // 2. Word-level: ALL query words must appear somewhere in text
            string[] queryWords = SplitWords(q);
            if (queryWords.Length > 1)
            {
                bool allFound = true;
                for (int i = 0; i < queryWords.Length; i++)
                {
                    if (text.IndexOf(queryWords[i], StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        allFound = false;
                        break;
                    }
                }
                if (allFound) return true;
            }

            // 3. Fuzzy trigram matching — check if any word in text is close to any query word
            if (q.Length >= MinFuzzyLength)
            {
                string textToSplit = text.Length > MaxSearchableContentLength ? text.Substring(0, MaxSearchableContentLength) : text;
                string[] textWords = SplitWords(textToSplit);
                for (int qi = 0; qi < queryWords.Length; qi++)
                {
                    string qw = queryWords[qi];
                    if (qw.Length < MinFuzzyLength) continue;

                    bool wordMatched = false;
                    for (int ti = 0; ti < textWords.Length; ti++)
                    {
                        string tw = textWords[ti];
                        if (tw.Length < MinFuzzyLength) continue;

                        int lenDiff = Math.Abs(qw.Length - tw.Length);
                        if (lenDiff > Math.Max(qw.Length, tw.Length) / 2) continue;

                        if (ComputeTrigramSimilarity(qw, tw) >= FuzzyThreshold)
                        {
                            wordMatched = true;
                            break;
                        }
                    }

                    // For multi-word queries, ALL words must match (exact or fuzzy)
                    if (!wordMatched && queryWords.Length > 1)
                    {
                        if (text.IndexOf(qw, StringComparison.OrdinalIgnoreCase) < 0)
                            return false;
                    }
                    else if (wordMatched && queryWords.Length == 1)
                    {
                        return true;
                    }
                }

                if (queryWords.Length > 1)
                    return true;
            }

            return false;
        }

        /// <summary>Uses pre-computed lowercase text to avoid allocating ToLowerInvariant per call.</summary>
        internal static bool IsMatchWithLower(string query, string rawText, string precomputedLower)
        {
            if (string.IsNullOrEmpty(query)) return true;
            if (string.IsNullOrEmpty(rawText) && string.IsNullOrEmpty(precomputedLower)) return false;

            string target = string.IsNullOrEmpty(precomputedLower) ? rawText : precomputedLower;
            string q = query.Trim();
            if (q.Length == 0) return true;

            // Fast path: exact substring match
            if (target.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) return true;

            // Fuzzy path: check query words
            string[] queryWords = SplitWords(q);
            if (queryWords.Length == 0) return true;

            // Check all words present
            bool allPresent = true;
            for (int i = 0; i < queryWords.Length; i++)
            {
                if (target.IndexOf(queryWords[i], StringComparison.OrdinalIgnoreCase) < 0)
                {
                    allPresent = false;
                    break;
                }
            }
            if (allPresent) return true;

            // Trigram fuzzy fallback (only if query is sufficient length)
            if (q.Length < MinFuzzyLength) return false;

            string textToSplit = target.Length > MaxSearchableContentLength ? target.Substring(0, MaxSearchableContentLength) : target;
            string[] textWords = SplitWords(textToSplit);

            for (int qi = 0; qi < queryWords.Length; qi++)
            {
                string qw = queryWords[qi];
                if (qw.Length < MinFuzzyLength)
                {
                    if (target.IndexOf(qw, StringComparison.OrdinalIgnoreCase) < 0) return false;
                    continue;
                }

                bool found = false;
                for (int ti = 0; ti < textWords.Length; ti++)
                {
                    string tw = textWords[ti];
                    if (tw.Length < MinFuzzyLength) continue;

                    int lenDiff = Math.Abs(qw.Length - tw.Length);
                    if (lenDiff > Math.Max(qw.Length, tw.Length) / 2) continue;

                    if (ComputeTrigramSimilarity(qw, tw) >= 0.3)
                    {
                        found = true;
                        break;
                    }
                }

                if (!found && target.IndexOf(qw, StringComparison.OrdinalIgnoreCase) < 0)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Returns a relevance score (0.0 to 1.0) indicating how well <paramref name="text"/>
        /// matches <paramref name="query"/>. Higher = better match.
        /// </summary>
        public static double Score(string query, string text)
        {
            if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(text))
                return 0.0;

            string q = query.Trim();
            string t = text;

            // 1.0 — Exact full match
            if (t.Equals(q, StringComparison.OrdinalIgnoreCase))
                return 1.0;

            // 0.9 — Text starts with query
            if (t.StartsWith(q, StringComparison.OrdinalIgnoreCase))
                return 0.9;

            // 0.8 — Exact substring match
            int subIdx = t.IndexOf(q, StringComparison.OrdinalIgnoreCase);
            if (subIdx >= 0)
            {
                double ratio = (double)q.Length / Math.Min(t.Length, 200);
                return 0.8 + Math.Min(ratio * 0.15, 0.15);
            }

            // Word-level scoring
            string[] queryWords = SplitWords(q);
            if (queryWords.Length > 1)
            {
                int wordsFound = 0;
                for (int i = 0; i < queryWords.Length; i++)
                {
                    if (t.IndexOf(queryWords[i], StringComparison.OrdinalIgnoreCase) >= 0)
                        wordsFound++;
                }
                if (wordsFound == queryWords.Length)
                    return 0.6 + (0.1 * Math.Min(wordsFound, 3) / 3.0);

                if (wordsFound > 0)
                    return 0.3 + (0.2 * wordsFound / queryWords.Length);
            }

            // Fuzzy trigram match
            if (q.Length >= MinFuzzyLength)
            {
                string textToSplit = t.Length > MaxSearchableContentLength ? t.Substring(0, MaxSearchableContentLength) : t;
                string[] textWords = SplitWords(textToSplit);
                double bestSim = 0;

                for (int qi = 0; qi < queryWords.Length; qi++)
                {
                    string qw = queryWords[qi];
                    if (qw.Length < MinFuzzyLength) continue;

                    for (int ti = 0; ti < textWords.Length; ti++)
                    {
                        string tw = textWords[ti];
                        if (tw.Length < MinFuzzyLength) continue;

                        int lenDiff = Math.Abs(qw.Length - tw.Length);
                        if (lenDiff > Math.Max(qw.Length, tw.Length) / 2) continue;

                        double sim = ComputeTrigramSimilarity(qw, tw);
                        if (sim > bestSim) bestSim = sim;
                    }
                }
                if (bestSim >= FuzzyThreshold)
                    return 0.2 + (bestSim * 0.3);
            }

            return 0.0;
        }

        internal static double ScoreWithLower(string query, string rawText, string precomputedLower)
        {
            if (string.IsNullOrEmpty(query)) return 1.0;
            string target = string.IsNullOrEmpty(precomputedLower) ? (rawText ?? "") : precomputedLower;
            if (target.Length == 0) return 0.0;

            string q = query.Trim();
            if (q.Length == 0) return 1.0;

            // Exact match bonus
            int idx = target.IndexOf(q, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                double score = 0.8;
                if (idx == 0) score = target.Length == q.Length ? 1.0 : 0.9;
                double ratio = (double)q.Length / Math.Min(target.Length, 200);
                return score + (ratio * 0.1);
            }

            // Word match scoring
            string[] queryWords = SplitWords(q);
            if (queryWords.Length == 0) return 1.0;

            string textToSplit = target.Length > MaxSearchableContentLength ? target.Substring(0, MaxSearchableContentLength) : target;
            string[] textWords = SplitWords(textToSplit);

            int matchCount = 0;
            double simSum = 0;

            for (int qi = 0; qi < queryWords.Length; qi++)
            {
                string qw = queryWords[qi];
                double bestSim = 0;

                for (int ti = 0; ti < textWords.Length; ti++)
                {
                    string tw = textWords[ti];
                    double sim = ComputeTrigramSimilarity(qw, tw);
                    if (sim > bestSim) bestSim = sim;
                }

                if (bestSim > 0.3)
                {
                    matchCount++;
                    simSum += bestSim;
                }
                else if (target.IndexOf(qw, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    matchCount++;
                    simSum += 0.5;
                }
            }

            if (matchCount > 0)
                return (simSum / queryWords.Length) * 0.8;

            return 0.0;
        }

        public static bool IsMatchAny(string query, params string?[] texts)
        {
            for (int i = 0; i < texts.Length; i++)
            {
                if (!string.IsNullOrEmpty(texts[i]) && IsMatch(query, texts[i]!))
                    return true;
            }
            return false;
        }

        public static bool IsMatchAny(string query, string lowerFileName, string lowerContent, string rawFileName, string rawContent)
        {
            return IsMatchWithLower(query, rawFileName, lowerFileName) || IsMatchWithLower(query, rawContent, lowerContent);
        }

        public static double ScoreBest(string query, params string?[] texts)
        {
            double best = 0;
            for (int i = 0; i < texts.Length; i++)
            {
                if (!string.IsNullOrEmpty(texts[i]))
                {
                    double s = Score(query, texts[i]!);
                    if (s > best) best = s;
                }
            }
            return best;
        }

        public static double ScoreBest(string query, string lowerFileName, string lowerContent, string rawFileName, string rawContent)
        {
            return Math.Max(
                ScoreWithLower(query, rawFileName, lowerFileName),
                ScoreWithLower(query, rawContent, lowerContent)
            );
        }

        // ═══════════════════════════════════════════════════════════
        // ZERO-ALLOCATION PACKED-INTEGER TRIGRAM ENGINE
        // ═══════════════════════════════════════════════════════════

        private static string[] SplitWords(string input)
        {
            if (string.IsNullOrEmpty(input)) return Array.Empty<string>();
            return input.Split(_wordSeparators, StringSplitOptions.RemoveEmptyEntries);
        }

        /// <summary>
        /// Computes Jaccard similarity between character trigrams of strings a and b.
        /// Zero heap allocations — packs trigrams into stack-allocated integer spans.
        /// </summary>
        public static double ComputeTrigramSimilarity(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0.0;
            if (a.Equals(b, StringComparison.OrdinalIgnoreCase)) return 1.0;

            int lenA = a.Length;
            int lenB = b.Length;
            if (lenA < 2 || lenB < 2) return 0.0;

            // Maximum trigrams for a word is (length + 4) - 2 = length + 2
            int maxTriA = lenA + 2;
            int maxTriB = lenB + 2;

            Span<int> triA = stackalloc int[Math.Min(maxTriA, 64)];
            Span<int> triB = stackalloc int[Math.Min(maxTriB, 64)];

            int countA = ExtractPackedTrigrams(a, triA);
            int countB = ExtractPackedTrigrams(b, triB);

            if (countA == 0 || countB == 0) return 0.0;

            // Sort trigrams for linear O(N+M) set intersection
            triA.Slice(0, countA).Sort();
            triB.Slice(0, countB).Sort();

            // Deduplicate in-place
            countA = DeduplicateSorted(triA.Slice(0, countA));
            countB = DeduplicateSorted(triB.Slice(0, countB));

            // Linear intersection
            int i = 0, j = 0;
            int intersection = 0;
            while (i < countA && j < countB)
            {
                int valA = triA[i];
                int valB = triB[j];
                if (valA == valB)
                {
                    intersection++;
                    i++;
                    j++;
                }
                else if (valA < valB)
                {
                    i++;
                }
                else
                {
                    j++;
                }
            }

            int union = countA + countB - intersection;
            return union == 0 ? 0.0 : (double)intersection / union;
        }

        /// <summary>
        /// Extracts boundary-padded character trigrams into a destination span as packed 24-bit integers.
        /// Boundary padding: "  " + input + "  "
        /// Trigram (c1, c2, c3) packed as: (c1 & 0xFF) << 16 | (c2 & 0xFF) << 8 | (c3 & 0xFF)
        /// </summary>
        private static int ExtractPackedTrigrams(string word, Span<int> dest)
        {
            int len = word.Length;
            if (len == 0) return 0;

            int paddedLen = len + 4;
            int totalTrigrams = paddedLen - 2; // = len + 2
            int limit = Math.Min(totalTrigrams, dest.Length);

            for (int i = 0; i < limit; i++)
            {
                char c1 = GetPaddedChar(word, i);
                char c2 = GetPaddedChar(word, i + 1);
                char c3 = GetPaddedChar(word, i + 2);

                int packed = ((int)char.ToLowerInvariant(c1) << 16) |
                             ((int)char.ToLowerInvariant(c2) << 8) |
                             ((int)char.ToLowerInvariant(c3));
                dest[i] = packed;
            }

            return limit;
        }

        private static char GetPaddedChar(string word, int index)
        {
            // Index 0, 1: space padding
            if (index < 2) return ' ';
            int wordIdx = index - 2;
            if (wordIdx < word.Length) return word[wordIdx];
            // Beyond end: space padding
            return ' ';
        }

        private static int DeduplicateSorted(Span<int> sorted)
        {
            if (sorted.Length <= 1) return sorted.Length;
            int write = 1;
            for (int read = 1; read < sorted.Length; read++)
            {
                if (sorted[read] != sorted[write - 1])
                {
                    sorted[write] = sorted[read];
                    write++;
                }
            }
            return write;
        }
    }
}

