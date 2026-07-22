using System;
using System.Data;
using System.Globalization;
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
        // [FIX M-16]: Add timeout to prevent ReDoS — nested optional quantifiers can backtrack
        private static readonly Regex _rxPhone = new(@"(?:\+?\d{1,3}[-.\s]?)?\(?\d{2,4}\)?[-.\s]?\d{3,4}[-.\s]?\d{3,4}", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
        private static readonly Regex _rxMath = new(@"^[\d\s+\-*/().,%^]+$", RegexOptions.Compiled);
        // [FIX M-34]: Whitelist for safe math expressions — prevents DataTable.Compute expression injection
        private static readonly Regex _rxSafeMathExpr = new(@"^[\d\s+\-*/().,%^]+$", RegexOptions.Compiled);
        private static readonly Regex _rxBase64 = new(@"^[A-Za-z0-9+/=]{20,}$", RegexOptions.Compiled | RegexOptions.Multiline);
        private static readonly Regex _rxEpoch = new(@"^\d{10,13}$", RegexOptions.Compiled);

        public static bool IsValidJson(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            text = text.Trim();
            if ((!text.StartsWith('{') || !text.EndsWith('}')) &&
                (!text.StartsWith('[') || !text.EndsWith(']')))
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
                if (string.IsNullOrWhiteSpace(expr) || expr.Length > 200) return "Error";
                expr = expr.Trim();
                // [FIX M-34]: Sanitize before DataTable.Compute — only allow safe math characters
                if (!_rxSafeMathExpr.IsMatch(expr)) return "Error";
                expr = expr.Replace("^", "**");
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
            if (!_rxBase64.IsMatch(text)) return false;
            // [FIX M-17]: Use TryFromBase64String to validate without allocating the full decoded buffer
            Span<byte> buffer = stackalloc byte[256];
            string sample = text.Length > 256 ? text[..(256 - (256 % 4))] : text;
            return Convert.TryFromBase64String(sample, buffer, out _);
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
                long val = long.Parse(text.Trim(), CultureInfo.InvariantCulture);
                DateTimeOffset dto = text.Trim().Length >= 13
                    ? DateTimeOffset.FromUnixTimeMilliseconds(val)
                    : DateTimeOffset.FromUnixTimeSeconds(val);
                return dto.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss (ddd)", CultureInfo.InvariantCulture);
            }
            catch { return "[Invalid timestamp]"; }
        }
    }
}
