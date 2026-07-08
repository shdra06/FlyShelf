// Copyright © 2024-2026 The FlyShelf Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;

namespace FlyShelf.Classes
{
    /// <summary>
    /// Lightweight fuzzy text matcher using word-level tokenization and trigram similarity.
    /// Zero dependencies, pure C#, sub-millisecond per comparison.
    /// 
    /// Matching strategy (in order of priority):
    ///   1. Exact substring match (fastest)
    ///   2. All-words match (query words found anywhere in text, any order)
    ///   3. Trigram fuzzy match (typo tolerance for individual words)
    /// </summary>
    public static class FuzzyMatcher
    {
        // Trigram similarity threshold — lower = more permissive.
        // 0.25 catches 1-2 char typos in short words; 0.3 is stricter.
        private const double FuzzyThreshold = 0.25;

        // Minimum query length for fuzzy matching (skip for very short queries)
        private const int MinFuzzyLength = 3;

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
            string t = text;

            // 1. Exact substring (case-insensitive) — fastest path
            if (t.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            // 2. Word-level: ALL query words must appear somewhere in text
            string[] queryWords = SplitWords(q);
            if (queryWords.Length > 1)
            {
                string tLower = t.ToLowerInvariant();
                bool allFound = true;
                for (int i = 0; i < queryWords.Length; i++)
                {
                    if (tLower.IndexOf(queryWords[i], StringComparison.Ordinal) < 0)
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
                string[] textWords = SplitWords(t);
                for (int qi = 0; qi < queryWords.Length; qi++)
                {
                    string qw = queryWords[qi];
                    if (qw.Length < MinFuzzyLength) continue; // too short for fuzzy

                    bool wordMatched = false;
                    for (int ti = 0; ti < textWords.Length; ti++)
                    {
                        string tw = textWords[ti];
                        if (tw.Length < MinFuzzyLength) continue;

                        // Quick length filter — skip wildly different lengths
                        int lenDiff = Math.Abs(qw.Length - tw.Length);
                        if (lenDiff > Math.Max(qw.Length, tw.Length) / 2) continue;

                        if (TrigramSimilarity(qw, tw) >= FuzzyThreshold)
                        {
                            wordMatched = true;
                            break;
                        }
                    }

                    // For multi-word queries, ALL words must match (exact or fuzzy)
                    if (!wordMatched && queryWords.Length > 1)
                    {
                        // Check if this word had an exact substring match
                        if (t.IndexOf(qw, StringComparison.OrdinalIgnoreCase) < 0)
                            return false;
                    }
                    else if (wordMatched && queryWords.Length == 1)
                    {
                        return true;
                    }
                }

                // If we reach here with multi-word query and didn't return false, all matched
                if (queryWords.Length > 1)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Returns a relevance score (0.0 to 1.0) indicating how well <paramref name="text"/>
        /// matches <paramref name="query"/>. Higher = better match.
        /// Used for sorting search results by relevance.
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
            if (t.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                return 0.8;

            // 0.6 — All query words found in text (any order)
            string[] queryWords = SplitWords(q);
            if (queryWords.Length > 1)
            {
                string tLower = t.ToLowerInvariant();
                int wordsFound = 0;
                for (int i = 0; i < queryWords.Length; i++)
                {
                    if (tLower.IndexOf(queryWords[i], StringComparison.Ordinal) >= 0)
                        wordsFound++;
                }
                if (wordsFound == queryWords.Length)
                    return 0.6 + (0.1 * Math.Min(wordsFound, 3) / 3.0); // up to 0.7

                // Partial word match — some words found
                if (wordsFound > 0)
                    return 0.3 + (0.2 * wordsFound / queryWords.Length);
            }

            // 0.2-0.5 — Fuzzy trigram match
            if (q.Length >= MinFuzzyLength)
            {
                string[] textWords = SplitWords(t);
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

                        double sim = TrigramSimilarity(qw, tw);
                        if (sim > bestSim) bestSim = sim;
                    }
                }
                if (bestSim >= FuzzyThreshold)
                    return 0.2 + (bestSim * 0.3); // 0.2 to 0.5
            }

            return 0.0;
        }

        /// <summary>
        /// Checks if any of the given texts match the query. Convenience overload.
        /// </summary>
        public static bool IsMatchAny(string query, params string?[] texts)
        {
            for (int i = 0; i < texts.Length; i++)
            {
                if (!string.IsNullOrEmpty(texts[i]) && IsMatch(query, texts[i]!))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Returns the best score across multiple text fields.
        /// </summary>
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

        // ═══════════════════════════════════════════════════════════
        // INTERNALS
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Splits text into lowercase words. Reuses no allocations beyond the array.
        /// </summary>
        private static string[] SplitWords(string input)
        {
            return input.ToLowerInvariant()
                .Split(_wordSeparators, StringSplitOptions.RemoveEmptyEntries);
        }

        private static readonly char[] _wordSeparators = 
            { ' ', '\t', '\n', '\r', ',', '.', ';', ':', '!', '?', '(', ')', '[', ']', '{', '}', '"', '\'', '/', '\\', '-', '_' };

        /// <summary>
        /// Computes Jaccard similarity between the trigram sets of two strings.
        /// Returns 0.0 (no overlap) to 1.0 (identical trigrams).
        /// </summary>
        private static double TrigramSimilarity(string a, string b)
        {
            var triA = GetTrigrams(a);
            var triB = GetTrigrams(b);

            if (triA.Count == 0 || triB.Count == 0)
                return 0.0;

            int intersection = 0;
            // Iterate the smaller set for efficiency
            var smaller = triA.Count <= triB.Count ? triA : triB;
            var larger = triA.Count <= triB.Count ? triB : triA;

            foreach (var tri in smaller)
            {
                if (larger.Contains(tri))
                    intersection++;
            }

            int union = triA.Count + triB.Count - intersection;
            return union == 0 ? 0.0 : (double)intersection / union;
        }

        /// <summary>
        /// Generates character trigrams for a string with boundary padding.
        /// "hello" → {"  h", " he", "hel", "ell", "llo", "lo ", "o  "}
        /// </summary>
        private static HashSet<string> GetTrigrams(string input)
        {
            string padded = "  " + input.ToLowerInvariant() + "  ";
            var result = new HashSet<string>(padded.Length - 2);
            for (int i = 0; i <= padded.Length - 3; i++)
            {
                result.Add(padded.Substring(i, 3));
            }
            return result;
        }
    }
}
