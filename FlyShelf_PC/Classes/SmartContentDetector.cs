using System;
using System.Data;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FlyShelf.Classes
{
    /// <summary>
    /// Detects special content types in clipboard text and provides
    /// transformation utilities (JSON formatting, math eval, base64 decode, etc.).
    /// </summary>
    public static class SmartContentDetector
    {
        private static readonly Regex _rxEmail = new(@"[\w.+-]+@[\w-]+\.[\w.]+", RegexOptions.Compiled);
        private static readonly Regex _rxPhone = new(@"(?:\+?\d{1,3}[-.\s]?)?\(?\d{2,4}\)?[-.\s]?\d{3,4}[-.\s]?\d{3,4}", RegexOptions.Compiled);
        private static readonly Regex _rxMath = new(@"^[\d\s+\-*/().,%^]+$", RegexOptions.Compiled);
        private static readonly Regex _rxBase64 = new(@"^[A-Za-z0-9+/=]{20,}$", RegexOptions.Compiled | RegexOptions.Multiline);
        private static readonly Regex _rxEpoch = new(@"^\d{10,13}$", RegexOptions.Compiled);

        public static bool IsValidJson(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            text = text.Trim();
            if ((!text.StartsWith("{") || !text.EndsWith("}")) &&
                (!text.StartsWith("[") || !text.EndsWith("]")))
                return false;
            try
            {
                using var doc = JsonDocument.Parse(text);
                return true;
            }
            catch { return false; }
        }

        public static string PrettyPrintJson(string text)
        {
            try
            {
                using var doc = JsonDocument.Parse(text.Trim());
                return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
            }
            catch { return text; }
        }

        public static bool ContainsEmail(string text)
            => !string.IsNullOrEmpty(text) && _rxEmail.IsMatch(text);

        public static string ExtractFirstEmail(string text)
        {
            var m = _rxEmail.Match(text ?? "");
            return m.Success ? m.Value : null;
        }

        public static bool ContainsPhoneNumber(string text)
            => !string.IsNullOrEmpty(text) && text.Length < 30 && _rxPhone.IsMatch(text);

        public static bool IsMathExpression(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > 100 || text.Length < 3) return false;
            text = text.Trim();
            // Must contain at least one operator
            if (!text.Contains('+') && !text.Contains('-') && !text.Contains('*') && 
                !text.Contains('/') && !text.Contains('%') && !text.Contains('^'))
                return false;
            return _rxMath.IsMatch(text);
        }

        public static string EvaluateMath(string expr)
        {
            try
            {
                expr = expr.Trim().Replace("^", "**");
                using var dt = new DataTable();
                var result = dt.Compute(expr, null);
                return result?.ToString() ?? "Error";
            }
            catch { return "Error"; }
        }

        public static bool IsBase64(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            text = text.Trim();
            if (text.Length < 20 || text.Length > 100000) return false;
            if (text.Contains(' ') || text.Contains('\n')) return false;
            if (text.Length % 4 != 0) return false;
            try
            {
                Convert.FromBase64String(text);
                return _rxBase64.IsMatch(text);
            }
            catch { return false; }
        }

        public static string DecodeBase64(string text)
        {
            try
            {
                byte[] bytes = Convert.FromBase64String(text.Trim());
                return System.Text.Encoding.UTF8.GetString(bytes);
            }
            catch { return "[Failed to decode Base64]"; }
        }

        public static bool IsEpochTimestamp(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            text = text.Trim();
            return _rxEpoch.IsMatch(text) && long.TryParse(text, out long val) && val > 946684800; // after year 2000
        }

        public static string EpochToDateTime(string text)
        {
            try
            {
                long val = long.Parse(text.Trim());
                DateTimeOffset dto = text.Trim().Length >= 13
                    ? DateTimeOffset.FromUnixTimeMilliseconds(val)
                    : DateTimeOffset.FromUnixTimeSeconds(val);
                return dto.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss (ddd)");
            }
            catch { return "[Invalid timestamp]"; }
        }
    }
}
