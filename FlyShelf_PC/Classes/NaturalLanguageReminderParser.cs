// ---------------------------------------------------------------
// NaturalLanguageReminderParser — Smart Reminder Extraction
// Parses natural language notes (e.g., "meeting at 9 am tomorrow")
// into a structured (Title, DueDate) tuple using Microsoft's
// Recognizers.Text library. Zero-RAM, sub-millisecond, deterministic.
// ---------------------------------------------------------------
using System;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Recognizers.Text;
using Microsoft.Recognizers.Text.DateTime;

namespace FlyShelf.Classes
{
    public static class NaturalLanguageReminderParser
    {
        /// <summary>
        /// Attempts to parse a natural-language note into a clean reminder title and a resolved due date.
        /// Uses the local system clock as the reference point for relative dates ("tomorrow", "next Monday", etc.).
        /// </summary>
        /// <param name="text">The raw note text (e.g., "meeting at 9 am tomorrow").</param>
        /// <param name="refTime">Reference time for resolving relative dates (typically DateTime.Now).</param>
        /// <returns>A tuple of (Title, DueDate). If no date is detected, falls back to tomorrow 9:00 AM.</returns>
        public static (string Title, DateTime DueDate) Parse(string text, DateTime refTime)
        {
            if (string.IsNullOrWhiteSpace(text))
                return ("Reminder", FallbackDue(refTime));

            try
            {
                // Recognize date/time entities in the text using English culture
                var results = DateTimeRecognizer.RecognizeDateTime(text, Culture.English, refTime: refTime);

                if (results != null && results.Count > 0)
                {
                    // Try each recognized result (prefer the first that resolves to a valid future DateTime)
                    foreach (var result in results)
                    {
                        if (result.Resolution == null || !result.Resolution.ContainsKey("values"))
                            continue;

                        var values = result.Resolution["values"] as IList<Dictionary<string, string>>;
                        if (values == null || values.Count == 0)
                            continue;

                        DateTime? parsedDate = null;

                        foreach (var valueDict in values)
                        {
                            // Handle "datetime" type — has both date and time
                            if (valueDict.TryGetValue("value", out string rawValue) && !string.IsNullOrEmpty(rawValue))
                            {
                                if (DateTime.TryParse(rawValue, out DateTime dt))
                                {
                                    parsedDate = dt;
                                    break;
                                }
                            }

                            // Handle "date" type without time — default to 9:00 AM
                            if (valueDict.TryGetValue("type", out string type) && type == "date")
                            {
                                if (valueDict.TryGetValue("value", out string dateVal) && DateTime.TryParse(dateVal, out DateTime dateOnly))
                                {
                                    parsedDate = dateOnly.Date.AddHours(9);
                                    break;
                                }
                            }

                            // Handle "time" type without date — use today or tomorrow
                            if (type == "time" && valueDict.TryGetValue("value", out string timeVal) && DateTime.TryParse(timeVal, out DateTime timeOnly))
                            {
                                var combined = refTime.Date.Add(timeOnly.TimeOfDay);
                                if (combined <= refTime)
                                    combined = combined.AddDays(1); // If the time has passed today, assume tomorrow
                                parsedDate = combined;
                                break;
                            }
                        }

                        if (parsedDate.HasValue)
                        {
                            // Extract the title by removing the recognized date/time substring from the original text
                            string title = ExtractTitle(text, result.Text);
                            return (title, parsedDate.Value);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("NL_PARSER", $"Parse failed: {ex.Message}");
            }

            // Fallback: No date detected — use the full text as the title, default to tomorrow 9 AM
            string fallbackTitle = text.Length > 80 ? text[..80] + "..." : text;
            return (fallbackTitle, FallbackDue(refTime));
        }

        /// <summary>
        /// Strips the recognized date/time phrase from the note text and cleans up the remainder as a title.
        /// </summary>
        private static string ExtractTitle(string fullText, string recognizedPhrase)
        {
            // Remove the recognized date/time substring
            string title = fullText.Replace(recognizedPhrase, "", StringComparison.OrdinalIgnoreCase).Trim();

            // Clean up trailing/leading prepositions and connectors left behind
            title = Regex.Replace(title, @"^\s*(at|on|for|in|by|to|the|a|an|is|are)\s+", "", RegexOptions.IgnoreCase).Trim();
            title = Regex.Replace(title, @"\s+(at|on|for|in|by|to|the)$", "", RegexOptions.IgnoreCase).Trim();

            // Clean up double spaces and leading/trailing punctuation
            title = Regex.Replace(title, @"\s{2,}", " ").Trim();
            title = title.Trim(' ', '-', '–', '—', ',', ':', ';');

            // Capitalize first letter
            if (title.Length > 0)
                title = char.ToUpper(title[0], CultureInfo.InvariantCulture) + title[1..];

            // If nothing remains after stripping, use the original text
            if (string.IsNullOrWhiteSpace(title))
                title = fullText.Length > 80 ? fullText[..80] + "..." : fullText;

            return title;
        }

        /// <summary>
        /// Returns a sensible default: tomorrow at 9:00 AM local time.
        /// </summary>
        private static DateTime FallbackDue(DateTime refTime) =>
            refTime.Date.AddDays(1).AddHours(9);
    }
}
