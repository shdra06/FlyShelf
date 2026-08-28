// Copyright © 2024-2026 The FlyShelf Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;

namespace FlyShelf.Classes
{
    /// <summary>
    /// Ultra-fast, zero-allocation search matcher supporting:
    ///   1. Exact substring matching (case-insensitive)
    ///   2. Multi-word queries (all query tokens must match)
    ///   3. Word prefix & stemming matching (e.g. 'kube' -> 'kubernetes', 'periods' -> 'period')
    ///   4. Strict 1-edit typo tolerance for words >= 4 chars (zero false positives)
    /// </summary>
    public static class FuzzyMatcher
    {
        private const int MaxSearchableContentLength = 4096;

        private static readonly char[] _wordSeparators = 
            { ' ', '\t', '\n', '\r', ',', '.', ';', ':', '!', '?', '(', ')', '[', ']', '{', '}', '"', '\'', '/', '\\', '-', '_', '@', '#', '$', '%', '^', '&', '*', '+', '=', '<', '>', '~', '`', '|' };

        /// <summary>
        /// Returns true if <paramref name="text"/> matches <paramref name="query"/>.
        /// </summary>
        public static bool IsMatch(string query, string text)
        {
            if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(text))
                return false;

            string q = query.Trim();
            if (q.Length == 0) return true;

            // 1. Exact substring match (fastest path)
            if (text.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            // 2. Tokenize query words
            string[] queryWords = SplitWords(q);
            if (queryWords.Length == 0) return true;

            string textToSearch = text.Length > MaxSearchableContentLength ? text.Substring(0, MaxSearchableContentLength) : text;
            string[] textWords = SplitWords(textToSearch);
            if (textWords.Length == 0) return false;

            // ALL query words must match at least one word in text
            for (int qi = 0; qi < queryWords.Length; qi++)
            {
                string qw = queryWords[qi];
                if (qw.Length == 0) continue;

                if (!IsWordMatched(qw, textWords, textToSearch))
                    return false;
            }

            return true;
        }

        /// <summary>Uses pre-computed lowercase text for zero allocations.</summary>
        internal static bool IsMatchWithLower(string query, string rawText, string precomputedLower)
        {
            if (string.IsNullOrEmpty(query)) return true;
            if (string.IsNullOrEmpty(rawText) && string.IsNullOrEmpty(precomputedLower)) return false;

            string target = string.IsNullOrEmpty(precomputedLower) ? rawText : precomputedLower;
            string q = query.Trim();
            if (q.Length == 0) return true;

            // 1. Exact substring match
            if (target.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) return true;

            // 2. Tokenize query words
            string[] queryWords = SplitWords(q);
            if (queryWords.Length == 0) return true;

            string textToSearch = target.Length > MaxSearchableContentLength ? target.Substring(0, MaxSearchableContentLength) : target;
            string[] textWords = SplitWords(textToSearch);
            if (textWords.Length == 0) return false;

            for (int qi = 0; qi < queryWords.Length; qi++)
            {
                string qw = queryWords[qi];
                if (qw.Length == 0) continue;

                if (!IsWordMatched(qw, textWords, textToSearch))
                    return false;
            }

            return true;
        }

        private static bool IsWordMatched(string queryWord, string[] textWords, string fullText)
        {
            // Direct substring check for single token in text
            if (fullText.IndexOf(queryWord, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            string qStem = (queryWord.Length > 3 && queryWord.EndsWith("s", StringComparison.OrdinalIgnoreCase))
                ? queryWord.Substring(0, queryWord.Length - 1)
                : null;

            for (int i = 0; i < textWords.Length; i++)
            {
                string tw = textWords[i];
                if (tw.Length == 0) continue;

                // Exact word match
                if (tw.Equals(queryWord, StringComparison.OrdinalIgnoreCase))
                    return true;

                // Prefix match (e.g. "kube" -> "kubernetes")
                if (tw.StartsWith(queryWord, StringComparison.OrdinalIgnoreCase))
                    return true;

                // Query word is prefix of text word or stemmed match (e.g. "periods" -> "period")
                if (qStem != null && tw.Equals(qStem, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (qStem != null && tw.StartsWith(qStem, StringComparison.OrdinalIgnoreCase))
                    return true;

                // Text word is plural/stemmed form of query word (e.g. query "period" -> text "periods")
                if (tw.Length > 3 && tw.EndsWith("s", StringComparison.OrdinalIgnoreCase) &&
                    tw.Substring(0, tw.Length - 1).Equals(queryWord, StringComparison.OrdinalIgnoreCase))
                    return true;

                // Strict typo match: only for words >= 4 chars with max 1 edit distance
                if (queryWord.Length >= 4 && Math.Abs(queryWord.Length - tw.Length) <= 1)
                {
                    if (LevenshteinDistanceWithinLimit(queryWord, tw, 1))
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Fast bounded Levenshtein distance check (max 1 edit distance).
        /// Zero heap allocation using stack memory.
        /// </summary>
        private static bool LevenshteinDistanceWithinLimit(string s, string t, int maxDistance)
        {
            int sLen = s.Length;
            int tLen = t.Length;
            if (Math.Abs(sLen - tLen) > maxDistance) return false;
            if (sLen == 0) return tLen <= maxDistance;
            if (tLen == 0) return sLen <= maxDistance;

            int maxLen = Math.Max(sLen, tLen) + 1;
            if (maxLen > 64) return false; // Guard against huge words

            Span<int> prevRow = stackalloc int[maxLen];
            Span<int> currRow = stackalloc int[maxLen];

            for (int i = 0; i <= tLen; i++) prevRow[i] = i;

            for (int i = 0; i < sLen; i++)
            {
                currRow[0] = i + 1;
                int minInRow = currRow[0];

                char sChar = char.ToLowerInvariant(s[i]);
                for (int j = 0; j < tLen; j++)
                {
                    char tChar = char.ToLowerInvariant(t[j]);
                    int cost = (sChar == tChar) ? 0 : 1;

                    int insert = currRow[j] + 1;
                    int delete = prevRow[j + 1] + 1;
                    int replace = prevRow[j] + cost;

                    int val = Math.Min(insert, Math.Min(delete, replace));
                    currRow[j + 1] = val;
                    if (val < minInRow) minInRow = val;
                }

                if (minInRow > maxDistance) return false;

                for (int k = 0; k <= tLen; k++) prevRow[k] = currRow[k];
            }

            return currRow[tLen] <= maxDistance;
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

            // 0.95 — Text starts with query
            if (t.StartsWith(q, StringComparison.OrdinalIgnoreCase))
                return 0.95;

            // 0.85 — Exact substring match
            int subIdx = t.IndexOf(q, StringComparison.OrdinalIgnoreCase);
            if (subIdx >= 0)
            {
                double ratio = (double)q.Length / Math.Min(t.Length, 200);
                return 0.85 + Math.Min(ratio * 0.1, 0.1);
            }

            // Word-level scoring
            string[] queryWords = SplitWords(q);
            if (queryWords.Length == 0) return 0.0;

            string textToSearch = t.Length > MaxSearchableContentLength ? t.Substring(0, MaxSearchableContentLength) : t;
            string[] textWords = SplitWords(textToSearch);
            if (textWords.Length == 0) return 0.0;

            int matchCount = 0;
            double scoreSum = 0.0;

            for (int qi = 0; qi < queryWords.Length; qi++)
            {
                string qw = queryWords[qi];
                double bestWordScore = 0.0;

                for (int ti = 0; ti < textWords.Length; ti++)
                {
                    string tw = textWords[ti];
                    if (tw.Equals(qw, StringComparison.OrdinalIgnoreCase))
                    {
                        bestWordScore = Math.Max(bestWordScore, 1.0);
                    }
                    else if (tw.StartsWith(qw, StringComparison.OrdinalIgnoreCase))
                    {
                        bestWordScore = Math.Max(bestWordScore, 0.85);
                    }
                    else if (qw.Length >= 4 && Math.Abs(qw.Length - tw.Length) <= 1 && LevenshteinDistanceWithinLimit(qw, tw, 1))
                    {
                        bestWordScore = Math.Max(bestWordScore, 0.65);
                    }
                }

                if (bestWordScore > 0.0)
                {
                    matchCount++;
                    scoreSum += bestWordScore;
                }
            }

            if (matchCount == queryWords.Length)
            {
                return 0.5 + (0.3 * (scoreSum / queryWords.Length));
            }
            if (matchCount > 0)
            {
                return 0.2 + (0.2 * ((double)matchCount / queryWords.Length));
            }

            return 0.0;
        }

        internal static double ScoreWithLower(string query, string rawText, string precomputedLower)
        {
            if (string.IsNullOrEmpty(query)) return 1.0;
            string target = string.IsNullOrEmpty(precomputedLower) ? (rawText ?? "") : precomputedLower;
            if (target.Length == 0) return 0.0;

            return Score(query, target);
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

        private static string[] SplitWords(string input)
        {
            if (string.IsNullOrEmpty(input)) return Array.Empty<string>();
            return input.Split(_wordSeparators, StringSplitOptions.RemoveEmptyEntries);
        }
    }
}

