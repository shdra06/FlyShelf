using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace FlyShelf.Classes
{
    public static class GeminiEngine
    {
        private static readonly HttpClient _httpClient = new HttpClient() { Timeout = TimeSpan.FromSeconds(45) };

        public static async Task<string> ExtractFormattedTableFromImageAsync(string imagePath, string apiKey)
        {
            if (string.IsNullOrEmpty(apiKey)) throw new Exception("Gemini API Key is completely missing.");

            byte[] imageBytes = await File.ReadAllBytesAsync(imagePath);
            string base64Image = Convert.ToBase64String(imageBytes);

            string mimeType = "image/jpeg";
            string extension = Path.GetExtension(imagePath).ToLowerInvariant();
            if (extension == ".png") mimeType = "image/png";
            else if (extension == ".webp") mimeType = "image/webp";

            var payload = new
            {
                contents = new[]
                {
                    new
                    {
                        parts = new object[]
                        {
                            new { text = "Extract ALL tabular data from this image into a raw strict JSON coordinate array. " +
                                         "Format: { \"(row,col)\": { \"text\": \"Cell Value\", \"conf\": 1.0 } }. " +
                                         "Rules: Start row/col from 0. Row 0 should be the header row if one exists. " +
                                         "Include ALL cells even if empty (use empty string). " +
                                         "Never output HTML, markdown, or explanations — ONLY raw JSON. " +
                                         "Do not wrap in ```json blocks. " +
                                         "Example: {\"(0,0)\": {\"text\": \"ID\", \"conf\": 1.0}, \"(0,1)\": {\"text\": \"Name\", \"conf\": 1.0}, \"(1,0)\": {\"text\": \"1\", \"conf\": 1.0}}" },
                            new { inline_data = new { mime_type = mimeType, data = base64Image } }
                        }
                    }
                }
            };

            string jsonPayload = JsonSerializer.Serialize(payload);
            string endpoint = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-2.0-flash:generateContent?key={apiKey}";

            // Retry logic: up to 2 retries with 2-second delay on failure
            Exception lastException = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var requestContent = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
                    var response = await _httpClient.PostAsync(endpoint, requestContent);

                    if (!response.IsSuccessStatusCode)
                    {
                        string err = await response.Content.ReadAsStringAsync();
                        throw new Exception($"Gemini HTTP Engine Failure: {response.StatusCode} - {err}");
                    }

                    string responseJson = await response.Content.ReadAsStringAsync();

                    using JsonDocument doc = JsonDocument.Parse(responseJson);
                    var candidates = doc.RootElement.GetProperty("candidates");
                    if (candidates.GetArrayLength() == 0)
                        throw new Exception("Gemini returned no candidates (response may have been filtered).");

                    var parts = candidates[0].GetProperty("content").GetProperty("parts");
                    if (parts.GetArrayLength() == 0)
                        throw new Exception("Gemini candidate contained no parts.");

                    var textObj = parts[0].GetProperty("text");

                    string rawExtraction = textObj.GetString() ?? string.Empty;
                    // Pre-process any rogue markdown block wraps inserted by LLMs
                    if (rawExtraction.StartsWith("```json")) rawExtraction = rawExtraction.Substring(7);
                    if (rawExtraction.StartsWith("```")) rawExtraction = rawExtraction.Substring(3);
                    if (rawExtraction.EndsWith("```")) rawExtraction = rawExtraction.Substring(0, rawExtraction.Length - 3);

                    return rawExtraction.Trim();
                }
                catch (TaskCanceledException)
                {
                    lastException = new Exception("Gemini API request timed out after 45 seconds.");
                    if (attempt < 2) await Task.Delay(2000);
                }
                catch (HttpRequestException ex)
                {
                    lastException = ex;
                    if (attempt < 2) await Task.Delay(2000);
                }
                catch (Exception ex)
                {
                    // Non-retryable errors (parsing, auth failures) — throw immediately
                    throw new Exception($"Gemini parsing fault! {ex.Message}");
                }
            }

            throw lastException ?? new Exception("Gemini extraction failed after 3 attempts.");
        }
    }
}
