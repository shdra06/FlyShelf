using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace FlyShelf.Classes
{
    /// <summary>
    /// Lightweight offline text processor for Summarize, Rewrite, and Organize.
    /// Uses extractive TF-IDF sentence scoring — zero external dependencies, under 1 MB RAM.
    /// </summary>
    public static class OfflineTextProcessor
    {
        // Common English stop words to exclude from scoring
        private static readonly HashSet<string> StopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "a","an","the","and","or","but","is","are","was","were","be","been","being",
            "have","has","had","do","does","did","will","would","shall","should","may",
            "might","must","can","could","i","me","my","mine","myself","we","our","ours",
            "ourselves","you","your","yours","yourself","yourselves","he","him","his",
            "himself","she","her","hers","herself","it","its","itself","they","them",
            "their","theirs","themselves","what","which","who","whom","this","that",
            "these","those","am","at","by","for","with","about","against","between",
            "through","during","before","after","above","below","to","from","up","down",
            "in","out","on","off","over","under","again","further","then","once","here",
            "there","when","where","why","how","all","both","each","few","more","most",
            "other","some","such","no","nor","not","only","own","same","so","than","too",
            "very","just","because","as","until","while","of","if","into","also","just",
            "don","don't","didn","didn't","doesn","doesn't","hadn","hadn't","hasn","hasn't",
            "haven","haven't","isn","isn't","let","ll","re","ve","won","won't","wouldn",
            "wouldn't","s","t","d","m"
        };

        /// <summary>
        /// Summarize: extracts the most important sentences (up to ~40% of original or 5 sentences max).
        /// </summary>
        public static string Summarize(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return text;

            var sentences = SplitSentences(text);
            if (sentences.Count <= 2)
                return text; // Already short enough

            var wordFreqs = ComputeWordFrequencies(text);
            var scored = ScoreSentences(sentences, wordFreqs);

            // Keep top ~40% of sentences, min 1, max 5
            int keepCount = Math.Max(1, Math.Min(5, (int)Math.Ceiling(sentences.Count * 0.4)));

            // Select top-scoring sentences but preserve their original order
            var topIndices = scored
                .OrderByDescending(kv => kv.Value)
                .Take(keepCount)
                .Select(kv => kv.Key)
                .OrderBy(i => i) // preserve reading order
                .ToList();

            var sb = new StringBuilder();
            foreach (int idx in topIndices)
            {
                if (sb.Length > 0) sb.Append(" ");
                sb.Append(sentences[idx].Trim());
            }

            return sb.ToString();
        }

        /// <summary>
        /// Rewrite: cleans up the text — trims whitespace, normalizes line breaks,
        /// capitalizes sentence starts, and fixes basic punctuation.
        /// </summary>
        public static string Rewrite(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return text;

            // Normalize line endings
            text = text.Replace("\r\n", "\n").Replace("\r", "\n");

            // Collapse multiple blank lines into one
            text = Regex.Replace(text, @"\n{3,}", "\n\n");

            // Collapse multiple spaces into one
            text = Regex.Replace(text, @"[ \t]{2,}", " ");

            // Process line by line
            var lines = text.Split('\n');
            var result = new List<string>();

            foreach (var rawLine in lines)
            {
                string line = rawLine.Trim();
                if (string.IsNullOrEmpty(line))
                {
                    result.Add("");
                    continue;
                }

                // Capitalize first letter of each line
                if (char.IsLower(line[0]))
                {
                    line = char.ToUpper(line[0], CultureInfo.InvariantCulture) + line[1..];
                }

                // Ensure line ends with punctuation if it looks like a sentence
                if (!IsBulletOrHeading(line) && line.Length > 10)
                {
                    char last = line[line.Length - 1];
                    if (!char.IsPunctuation(last))
                    {
                        line += ".";
                    }
                }

                // Fix double punctuation like ".." or ",,"
                line = Regex.Replace(line, @"([.!?,;:])\1+", "$1");

                // Fix space before punctuation: "hello ." → "hello."
                line = Regex.Replace(line, @"\s+([.!?,;:])", "$1");

                result.Add(line);
            }

            return string.Join("\n", result).Trim();
        }

        /// <summary>
        /// Organize: groups text into logical bullet points with headers.
        /// </summary>
        public static string Organize(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return text;

            // Split into lines/sentences
            var lines = text
                .Replace("\r\n", "\n")
                .Replace("\r", "\n")
                .Split('\n')
                .Select(l => l.Trim())
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();

            if (lines.Count == 0)
                return text;

            // If there are very few lines, just bullet-point them
            if (lines.Count <= 3)
            {
                return string.Join("\n", lines.Select(l => FormatBullet(l)));
            }

            // For longer text, try to group related sentences
            var sentences = new List<string>();
            foreach (var line in lines)
            {
                // Split long lines into sentences
                var lineSentences = SplitSentences(line);
                sentences.AddRange(lineSentences.Select(s => s.Trim()).Where(s => !string.IsNullOrWhiteSpace(s)));
            }

            if (sentences.Count <= 5)
            {
                // Just bullet each sentence
                return string.Join("\n", sentences.Select(s => FormatBullet(s)));
            }

            // Group sentences by dominant keyword overlap
            var groups = GroupSentences(sentences);
            var sb = new StringBuilder();

            foreach (var group in groups)
            {
                if (sb.Length > 0) sb.Append("\n\n");

                // Generate a heading from the group's most common meaningful word
                string heading = GenerateGroupHeading(group);
                sb.AppendLine(CultureInfo.InvariantCulture, $"## {heading}");

                foreach (var sentence in group)
                {
                    sb.AppendLine(FormatBullet(sentence));
                }
            }

            return sb.ToString().Trim();
        }

        // ──────────────────────────── Private Helpers ────────────────────────────

        private static List<string> SplitSentences(string text)
        {
            // Split on sentence boundaries (period, !, ?) followed by space or end
            var parts = Regex.Split(text, @"(?<=[.!?])\s+");
            var sentences = new List<string>();
            foreach (var part in parts)
            {
                string trimmed = part.Trim();
                if (!string.IsNullOrWhiteSpace(trimmed))
                    sentences.Add(trimmed);
            }
            // If nothing split (no periods), split on newlines
            if (sentences.Count <= 1 && text.Contains('\n'))
            {
                sentences = text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList();
            }
            return sentences;
        }

        private static Dictionary<string, double> ComputeWordFrequencies(string text)
        {
            var words = Tokenize(text);
            var freq = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var word in words)
            {
                if (StopWords.Contains(word) || word.Length <= 2) continue;
                freq.TryGetValue(word, out double count);
                freq[word] = count + 1;
            }

            // Normalize by max frequency
            if (freq.Count > 0)
            {
                double maxFreq = freq.Values.Max();
                foreach (var key in freq.Keys.ToList())
                {
                    freq[key] = freq[key] / maxFreq;
                }
            }

            return freq;
        }

        private static Dictionary<int, double> ScoreSentences(List<string> sentences, Dictionary<string, double> wordFreqs)
        {
            var scores = new Dictionary<int, double>();
            for (int i = 0; i < sentences.Count; i++)
            {
                var words = Tokenize(sentences[i]);
                double score = 0;
                int meaningfulWords = 0;
                foreach (var word in words)
                {
                    if (wordFreqs.TryGetValue(word, out double freq))
                    {
                        score += freq;
                        meaningfulWords++;
                    }
                }
                // Normalize by sentence length to avoid bias towards long sentences
                scores[i] = meaningfulWords > 0 ? score / meaningfulWords : 0;

                // Boost first and second sentences slightly (lead bias — common in notes)
                if (i == 0) scores[i] *= 1.3;
                else if (i == 1) scores[i] *= 1.1;
            }
            return scores;
        }

        private static string[] Tokenize(string text)
        {
            return Regex.Split(text.ToLowerInvariant(), @"[^a-z0-9']+")
                .Where(w => !string.IsNullOrWhiteSpace(w))
                .ToArray();
        }

        private static bool IsBulletOrHeading(string line)
        {
            return line.StartsWith('-') || line.StartsWith('•') || line.StartsWith('*')
                || line.StartsWith('#') || line.StartsWith('→') || Regex.IsMatch(line, @"^\d+[.)]");
        }

        private static string FormatBullet(string sentence)
        {
            // Don't double-bullet
            if (sentence.StartsWith("- ", StringComparison.Ordinal) || sentence.StartsWith("• ", StringComparison.Ordinal) || sentence.StartsWith("* ", StringComparison.Ordinal))
                return sentence;

            // Strip existing bullet markers
            string cleaned = Regex.Replace(sentence, @"^[\-\•\*→]\s*", "").Trim();
            cleaned = Regex.Replace(cleaned, @"^\d+[.)]\s*", "").Trim();

            if (string.IsNullOrWhiteSpace(cleaned)) return "";

            // Capitalize first letter
            if (char.IsLower(cleaned[0]))
                cleaned = char.ToUpper(cleaned[0], CultureInfo.InvariantCulture) + cleaned[1..];

            return "• " + cleaned;
        }

        private static List<List<string>> GroupSentences(List<string> sentences)
        {
            // Simple keyword-overlap clustering
            var groups = new List<List<string>>();
            var assigned = new HashSet<int>();

            for (int i = 0; i < sentences.Count; i++)
            {
                if (assigned.Contains(i)) continue;

                var group = new List<string> { sentences[i] };
                assigned.Add(i);

                var wordsI = new HashSet<string>(Tokenize(sentences[i]).Where(w => !StopWords.Contains(w) && w.Length > 2));

                for (int j = i + 1; j < sentences.Count; j++)
                {
                    if (assigned.Contains(j)) continue;

                    var wordsJ = new HashSet<string>(Tokenize(sentences[j]).Where(w => !StopWords.Contains(w) && w.Length > 2));

                    // Jaccard similarity
                    int intersection = wordsI.Intersect(wordsJ).Count();
                    int union = wordsI.Union(wordsJ).Count();
                    double similarity = union > 0 ? (double)intersection / union : 0;

                    if (similarity >= 0.15) // Relatively low threshold for grouping
                    {
                        group.Add(sentences[j]);
                        assigned.Add(j);
                    }
                }

                groups.Add(group);
            }

            return groups;
        }

        private static string GenerateGroupHeading(List<string> group)
        {
            // Find the most frequent meaningful word across the group
            var allWords = group.SelectMany(s => Tokenize(s))
                .Where(w => !StopWords.Contains(w) && w.Length > 3)
                .GroupBy(w => w, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .ThenByDescending(g => g.Key.Length)
                .Select(g => g.Key)
                .ToList();

            if (allWords.Count == 0)
                return "Notes";

            // Take top 1-2 words as heading, capitalize
            var headingWords = allWords.Take(2).Select(w => char.ToUpper(w[0], CultureInfo.InvariantCulture) + w[1..]);
            return string.Join(" & ", headingWords);
        }
    }
}
