// ---------------------------------------------------------------
// AiProviderService — Unified multi-provider AI service
// Supports Gemini, OpenAI, Claude with automatic fallback chain:
//   User's Cloud API → Windows Copilot Runtime → Offline Processor
// ---------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace FlyShelf.Classes
{
    /// <summary>
    /// Unified AI service routing requests to the best available provider.
    /// </summary>
    public class AiProviderService
    {
        private static readonly Lazy<AiProviderService> _instance = new(() => new AiProviderService());
        public static AiProviderService Instance => _instance.Value;

        private static readonly HttpClient _httpClient = new()
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        // ─── LRU Response Cache (avoid duplicate API calls) ───
        private readonly Dictionary<string, (string response, DateTime expiry)> _cache = new();
        private const int MaxCacheSize = 100;
        private static readonly TimeSpan CacheTTL = TimeSpan.FromMinutes(5);
        private readonly object _cacheLock = new();

        // ═══════════════════════════════════════════════════════════
        // PROVIDER DETECTION
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Returns the name of the active AI provider based on settings and availability.
        /// </summary>
        public string ActiveProviderName
        {
            get
            {
                var settings = SettingsManager.Current;
                if (!settings.AiEnabled) return "Disabled";

                var provider = settings.AiProvider?.ToLowerInvariant() ?? "auto";
                if (provider != "auto") return provider switch
                {
                    "gemini" => "Gemini",
                    "openai" => "OpenAI",
                    "claude" => "Claude",
                    "windows" => "Windows AI",
                    "offline" => "Offline",
                    _ => "Auto"
                };

                // Auto mode: check what's available
                if (HasCloudApiKey) return DetectProviderFromKey();
                if (WindowsAIService.Instance.IsAvailable) return "Windows AI";
                return "Offline";
            }
        }

        /// <summary>
        /// Whether any AI provider is configured and likely usable.
        /// </summary>
        public bool IsAvailable => ActiveProviderName != "Disabled";

        /// <summary>
        /// Whether a cloud API key is configured.
        /// </summary>
        public bool HasCloudApiKey => !string.IsNullOrEmpty(SettingsManager.Current.AiApiKey);

        /// <summary>
        /// Checks if an API key is configured. If not, shows the AiSetupPopup dialog.
        /// Returns true if a key is available (either already configured or just entered).
        /// Must be called from UI thread.
        /// </summary>
        public bool EnsureApiKeyOrPrompt(System.Windows.Window owner = null)
        {
            if (HasCloudApiKey) return true;
            try
            {
                var popup = new FlyShelf.Windows.AiSetupPopup(owner);
                popup.ShowDialog();
                return HasCloudApiKey;
            }
            catch { return false; }
        }

        /// <summary>
        /// Whether the active provider is a cloud provider (not local).
        /// </summary>
        public bool IsCloudProvider
        {
            get
            {
                var name = ActiveProviderName;
                return name == "Gemini" || name == "OpenAI" || name == "Claude";
            }
        }

        // ═══════════════════════════════════════════════════════════
        // HIGH-LEVEL AI METHODS
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Summarize text — extracts key points into a concise summary.
        /// </summary>
        public Task<string> SummarizeAsync(string text, CancellationToken ct = default)
            => GenerateAsync(text,
                "You are a concise summarizer. Summarize the following text, highlighting the key points. " +
                "Keep it brief but comprehensive. Use bullet points for multiple key points.", ct: ct);

        /// <summary>
        /// Rewrite text — improve clarity, grammar, and flow.
        /// </summary>
        public Task<string> RewriteAsync(string text, CancellationToken ct = default)
            => GenerateAsync(text,
                "You are a professional editor. Rewrite and improve the clarity, grammar, and flow of the following text. " +
                "Preserve the original meaning and tone. Return only the rewritten text.", ct: ct);

        /// <summary>
        /// Organize text — structure into clear sections with headings and bullets.
        /// </summary>
        public Task<string> OrganizeAsync(string text, CancellationToken ct = default)
            => GenerateAsync(text,
                "You are a document organizer. Organize the following text into clear sections with headings and bullet points. " +
                "Group related ideas together. Use markdown formatting.", ct: ct);

        /// <summary>
        /// Translate text to the specified language.
        /// </summary>
        public Task<string> TranslateAsync(string text, string targetLanguage, CancellationToken ct = default)
            => GenerateAsync(text,
                $"You are a professional translator. Translate the following text into {targetLanguage}. " +
                "Preserve formatting, tone, and meaning. Return only the translated text.", ct: ct);

        /// <summary>
        /// Expand brief notes into detailed content.
        /// </summary>
        public Task<string> ExpandAsync(string text, CancellationToken ct = default)
            => GenerateAsync(text,
                "You are a content expander. Take the following brief notes and expand them into detailed, " +
                "well-structured content. Add explanations, examples, and context where helpful.", ct: ct);

        /// <summary>
        /// Explain technical or complex content in simple terms.
        /// </summary>
        public Task<string> ExplainAsync(string text, CancellationToken ct = default)
            => GenerateAsync(text,
                "You are an expert explainer. Explain the following content in simple, easy-to-understand terms. " +
                "Assume the reader has no technical background. Use analogies and examples.", ct: ct);

        /// <summary>
        /// Extract action items / todos from meeting notes or text.
        /// </summary>
        public Task<string> ExtractActionsAsync(string text, CancellationToken ct = default)
            => GenerateAsync(text,
                "You are a productivity assistant. Extract all action items, tasks, and to-dos from the following text. " +
                "Format as a numbered checklist. Include who is responsible if mentioned.", ct: ct);

        /// <summary>
        /// Suggest tags/categories for content.
        /// </summary>
        public Task<string> AutoTagAsync(string text, CancellationToken ct = default)
            => GenerateAsync(text,
                "You are a content classifier. Suggest 3-5 short tags or categories for the following content. " +
                "Return only the tags, comma-separated. Be specific and descriptive.", ct: ct);

        /// <summary>
        /// Custom analysis — user provides the instruction.
        /// </summary>
        public Task<string> AnalyzeAsync(string text, string instruction, CancellationToken ct = default)
            => GenerateAsync(text, instruction, ct: ct);

        // ═══════════════════════════════════════════════════════════
        // RESEARCH-SPECIFIC AI METHODS
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Summarize an entire research topic (all items concatenated).
        /// </summary>
        public Task<string> SummarizeResearchAsync(string allItems, string topicName, CancellationToken ct = default)
            => GenerateAsync(allItems,
                $"You are a research assistant. The user has collected the following research items under the topic \"{topicName}\". " +
                "Create a comprehensive research brief that:\n" +
                "1. Identifies the main themes and findings\n" +
                "2. Highlights key facts and data points\n" +
                "3. Notes any contradictions or gaps\n" +
                "4. Provides a brief conclusion\n" +
                "Use markdown formatting with headers and bullets.", ct: ct);

        /// <summary>
        /// Auto-categorize research items into thematic groups.
        /// </summary>
        public Task<string> CategorizeResearchAsync(string allItems, CancellationToken ct = default)
            => GenerateAsync(allItems,
                "You are a content organizer. Analyze the following research items and group them into 3-6 thematic categories. " +
                "For each category, provide:\n" +
                "- A clear category name\n" +
                "- Which items belong to it (reference by number)\n" +
                "- A one-line summary of the category\n" +
                "Format as markdown.", ct: ct);

        /// <summary>
        /// Find connections and patterns across research items.
        /// </summary>
        public Task<string> FindConnectionsAsync(string allItems, CancellationToken ct = default)
            => GenerateAsync(allItems,
                "You are a research analyst. Identify connections, patterns, and relationships between the following research items. " +
                "Look for:\n- Common themes\n- Contradictions\n- Cause-and-effect relationships\n- Supporting evidence chains\n" +
                "Present your findings clearly with markdown formatting.", ct: ct);

        /// <summary>
        /// Generate a structured report from research items.
        /// </summary>
        public Task<string> GenerateReportAsync(string allItems, string topicName, CancellationToken ct = default)
            => GenerateAsync(allItems,
                $"You are a report writer. Create a well-structured research report on \"{topicName}\" using the following collected items as source material. " +
                "Include:\n" +
                "- Executive Summary\n" +
                "- Key Findings (with details)\n" +
                "- Analysis\n" +
                "- Conclusion & Recommendations\n" +
                "Use markdown formatting. Cite specific items where relevant.", ct: ct);

        /// <summary>
        /// Extract key facts and data points from research.
        /// </summary>
        public Task<string> ExtractFactsAsync(string allItems, CancellationToken ct = default)
            => GenerateAsync(allItems,
                "You are a fact extractor. From the following research items, extract all key facts, statistics, " +
                "quotes, dates, and data points. Format as a clean bullet list grouped by topic.", ct: ct);

        // ═══════════════════════════════════════════════════════════
        // CORE GENERATION — PROVIDER ROUTING
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Core generation method. Routes to the best available provider.
        /// </summary>
        public async Task<string> GenerateAsync(string userPrompt, string? systemPrompt = null,
            int? maxTokens = null, CancellationToken ct = default)
        {
            if (!SettingsManager.Current.AiEnabled)
                throw new InvalidOperationException("AI is disabled in settings.");

            // Check cache
            var cacheKey = $"{systemPrompt}|{userPrompt}|{maxTokens}";
            lock (_cacheLock)
            {
                if (_cache.TryGetValue(cacheKey, out var cached) && cached.expiry > DateTime.UtcNow)
                    return cached.response;
            }

            var tokens = maxTokens ?? SettingsManager.Current.AiMaxTokens;
            var provider = SettingsManager.Current.AiProvider?.ToLowerInvariant() ?? "auto";
            string result;

            try
            {
                if (provider == "auto")
                {
                    result = await AutoRouteAsync(userPrompt, systemPrompt, tokens, ct);
                }
                else
                {
                    result = provider switch
                    {
                        "gemini" => await CallGeminiAsync(userPrompt, systemPrompt, tokens, ct),
                        "openai" => await CallOpenAIAsync(userPrompt, systemPrompt, tokens, ct),
                        "claude" => await CallClaudeAsync(userPrompt, systemPrompt, tokens, ct),
                        "windows" => await CallWindowsAIAsync(userPrompt, systemPrompt),
                        "offline" => CallOffline(userPrompt, systemPrompt),
                        _ => await AutoRouteAsync(userPrompt, systemPrompt, tokens, ct)
                    };
                }
            }
            catch (Exception ex)
            {
                // Fallback chain on error
                Logger.LogAction("AI", $"Provider '{provider}' failed: {ex.Message}, falling back...");
                result = await FallbackAsync(userPrompt, systemPrompt, tokens, ct);
            }

            // Cache the result
            lock (_cacheLock)
            {
                if (_cache.Count >= MaxCacheSize)
                {
                    // Evict oldest entries
                    var oldest = _cache.OrderBy(kv => kv.Value.expiry).Take(10).Select(kv => kv.Key).ToList();
                    foreach (var key in oldest) _cache.Remove(key);
                }
                _cache[cacheKey] = (result, DateTime.UtcNow + CacheTTL);
            }

            return result;
        }

        /// <summary>
        /// Vision/multimodal generation — sends image + text prompt to AI.
        /// Only works with cloud providers (Gemini, OpenAI, Claude).
        /// </summary>
        public async Task<string> GenerateWithImageAsync(string userPrompt, byte[] imageBytes, string mimeType = "image/png",
            string? systemPrompt = null, int? maxTokens = null, CancellationToken ct = default)
        {
            if (!HasCloudApiKey)
                throw new InvalidOperationException("Vision requires a cloud API key.");

            var tokens = maxTokens ?? Math.Max(SettingsManager.Current.AiMaxTokens, 4096);
            var provider = DetectProviderFromKey();
            var base64Image = Convert.ToBase64String(imageBytes);

            return provider switch
            {
                "Gemini" => await CallGeminiVisionAsync(userPrompt, base64Image, mimeType, systemPrompt, tokens, ct),
                "OpenAI" => await CallOpenAIVisionAsync(userPrompt, base64Image, mimeType, systemPrompt, tokens, ct),
                _ => throw new InvalidOperationException($"Vision not supported for provider: {provider}")
            };
        }

        private async Task<string> CallGeminiVisionAsync(string prompt, string base64Image, string mimeType,
            string? systemPrompt, int maxTokens, CancellationToken ct)
        {
            var apiKey = SettingsManager.Current.AiApiKey;
            var model = string.IsNullOrEmpty(SettingsManager.Current.AiModelOverride)
                ? "gemini-2.0-flash" : SettingsManager.Current.AiModelOverride;

            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent";

            var parts = new List<object>
            {
                new { text = prompt },
                new { inline_data = new { mime_type = mimeType, data = base64Image } }
            };

            var requestBody = new Dictionary<string, object>
            {
                ["contents"] = new[] { new { parts = parts.ToArray() } },
                ["generationConfig"] = new { maxOutputTokens = maxTokens }
            };

            if (!string.IsNullOrEmpty(systemPrompt))
                requestBody["systemInstruction"] = new { parts = new[] { new { text = systemPrompt } } };

            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            request.Headers.Add("x-goog-api-key", apiKey);

            var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(ct);
                throw new HttpRequestException($"Gemini Vision error {(int)response.StatusCode}: {TruncateError(error)}");
            }

            var responseJson = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(responseJson);
            return doc.RootElement
                .GetProperty("candidates")[0]
                .GetProperty("content")
                .GetProperty("parts")[0]
                .GetProperty("text")
                .GetString() ?? "";
        }

        private async Task<string> CallOpenAIVisionAsync(string prompt, string base64Image, string mimeType,
            string? systemPrompt, int maxTokens, CancellationToken ct)
        {
            var apiKey = SettingsManager.Current.AiApiKey;
            var model = string.IsNullOrEmpty(SettingsManager.Current.AiModelOverride)
                ? "gpt-4o-mini" : SettingsManager.Current.AiModelOverride;

            var url = "https://api.openai.com/v1/chat/completions";

            var messages = new List<object>();
            if (!string.IsNullOrEmpty(systemPrompt))
                messages.Add(new { role = "system", content = systemPrompt });

            messages.Add(new {
                role = "user",
                content = new object[]
                {
                    new { type = "text", text = prompt },
                    new { type = "image_url", image_url = new { url = $"data:{mimeType};base64,{base64Image}" } }
                }
            });

            var requestBody = new Dictionary<string, object>
            {
                ["model"] = model,
                ["messages"] = messages,
                ["max_tokens"] = maxTokens
            };

            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            request.Headers.Add("Authorization", $"Bearer {apiKey}");

            var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(ct);
                throw new HttpRequestException($"OpenAI Vision error {(int)response.StatusCode}: {TruncateError(error)}");
            }

            var responseJson = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(responseJson);
            return doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "";
        }

        /// <summary>
        /// Auto-route: try cloud API first, then Windows AI, then offline.
        /// </summary>
        private async Task<string> AutoRouteAsync(string prompt, string? systemPrompt, int maxTokens, CancellationToken ct)
        {
            if (HasCloudApiKey)
            {
                var cloudProvider = DetectProviderFromKey();
                return cloudProvider switch
                {
                    "Gemini" => await CallGeminiAsync(prompt, systemPrompt, maxTokens, ct),
                    "OpenAI" => await CallOpenAIAsync(prompt, systemPrompt, maxTokens, ct),
                    "Claude" => await CallClaudeAsync(prompt, systemPrompt, maxTokens, ct),
                    _ => await FallbackAsync(prompt, systemPrompt, maxTokens, ct)
                };
            }

            return await FallbackAsync(prompt, systemPrompt, maxTokens, ct);
        }

        /// <summary>
        /// Fallback: Windows AI → Offline processor.
        /// </summary>
        private async Task<string> FallbackAsync(string prompt, string? systemPrompt, int maxTokens, CancellationToken ct)
        {
            if (WindowsAIService.Instance.IsAvailable)
            {
                return await CallWindowsAIAsync(prompt, systemPrompt);
            }
            return CallOffline(prompt, systemPrompt);
        }

        // ═══════════════════════════════════════════════════════════
        // PROVIDER IMPLEMENTATIONS
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Call Google Gemini API.
        /// </summary>
        private async Task<string> CallGeminiAsync(string prompt, string? systemPrompt, int maxTokens, CancellationToken ct)
        {
            var apiKey = SettingsManager.Current.AiApiKey;
            if (string.IsNullOrEmpty(apiKey)) throw new InvalidOperationException("No Gemini API key configured.");

            var model = string.IsNullOrEmpty(SettingsManager.Current.AiModelOverride)
                ? "gemini-2.0-flash" : SettingsManager.Current.AiModelOverride;

            // SECURITY (C-02): API key passed via header instead of URL query parameter
            // to prevent key leakage in server logs, proxy logs, and browser history.
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent";

            var requestBody = new Dictionary<string, object>
            {
                ["contents"] = new[] { new { parts = new[] { new { text = prompt } } } },
                ["generationConfig"] = new { maxOutputTokens = maxTokens }
            };

            if (!string.IsNullOrEmpty(systemPrompt))
            {
                requestBody["systemInstruction"] = new { parts = new[] { new { text = systemPrompt } } };
            }

            var json = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
            request.Headers.Add("x-goog-api-key", apiKey);
            var response = await _httpClient.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(ct);
                if ((int)response.StatusCode == 429)
                    throw new HttpRequestException("Rate limited. Please wait a moment and try again.");
                throw new HttpRequestException($"Gemini API error {(int)response.StatusCode}: {TruncateError(error)}");
            }

            var responseJson = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(responseJson);
            var candidates = doc.RootElement.GetProperty("candidates");
            if (candidates.GetArrayLength() == 0) throw new InvalidOperationException("Gemini returned no response.");

            var text = candidates[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString();
            Logger.LogAction("AI", $"Gemini response received ({text?.Length ?? 0} chars)");
            return text ?? "";
        }

        /// <summary>
        /// Call OpenAI Chat Completions API.
        /// </summary>
        private async Task<string> CallOpenAIAsync(string prompt, string? systemPrompt, int maxTokens, CancellationToken ct)
        {
            var apiKey = SettingsManager.Current.AiApiKey;
            if (string.IsNullOrEmpty(apiKey)) throw new InvalidOperationException("No OpenAI API key configured.");

            var model = string.IsNullOrEmpty(SettingsManager.Current.AiModelOverride)
                ? "gpt-4o-mini" : SettingsManager.Current.AiModelOverride;

            var messages = new List<object>();
            if (!string.IsNullOrEmpty(systemPrompt))
                messages.Add(new { role = "system", content = systemPrompt });
            messages.Add(new { role = "user", content = prompt });

            var requestBody = new { model, messages, max_tokens = maxTokens };
            var json = JsonSerializer.Serialize(requestBody);

            var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
            request.Headers.Add("Authorization", $"Bearer {apiKey}");
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(ct);
                if ((int)response.StatusCode == 429)
                    throw new HttpRequestException("Rate limited. Please wait a moment and try again.");
                throw new HttpRequestException($"OpenAI API error {(int)response.StatusCode}: {TruncateError(error)}");
            }

            var responseJson = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(responseJson);
            var choices = doc.RootElement.GetProperty("choices");
            if (choices.GetArrayLength() == 0) throw new InvalidOperationException("OpenAI returned no response.");

            var text = choices[0].GetProperty("message").GetProperty("content").GetString();
            Logger.LogAction("AI", $"OpenAI response received ({text?.Length ?? 0} chars)");
            return text ?? "";
        }

        /// <summary>
        /// Call Anthropic Claude Messages API.
        /// </summary>
        private async Task<string> CallClaudeAsync(string prompt, string? systemPrompt, int maxTokens, CancellationToken ct)
        {
            var apiKey = SettingsManager.Current.AiApiKey;
            if (string.IsNullOrEmpty(apiKey)) throw new InvalidOperationException("No Claude API key configured.");

            var model = string.IsNullOrEmpty(SettingsManager.Current.AiModelOverride)
                ? "claude-3-5-haiku-latest" : SettingsManager.Current.AiModelOverride;

            var requestBody = new Dictionary<string, object>
            {
                ["model"] = model,
                ["max_tokens"] = maxTokens,
                ["messages"] = new[] { new { role = "user", content = prompt } }
            };

            if (!string.IsNullOrEmpty(systemPrompt))
                requestBody["system"] = systemPrompt;

            var json = JsonSerializer.Serialize(requestBody);

            var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
            request.Headers.Add("x-api-key", apiKey);
            request.Headers.Add("anthropic-version", "2023-06-01");
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(ct);
                if ((int)response.StatusCode == 429)
                    throw new HttpRequestException("Rate limited. Please wait a moment and try again.");
                throw new HttpRequestException($"Claude API error {(int)response.StatusCode}: {TruncateError(error)}");
            }

            var responseJson = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(responseJson);
            var contentArray = doc.RootElement.GetProperty("content");
            if (contentArray.GetArrayLength() == 0) throw new InvalidOperationException("Claude returned no response.");

            var text = contentArray[0].GetProperty("text").GetString();
            Logger.LogAction("AI", $"Claude response received ({text?.Length ?? 0} chars)");
            return text ?? "";
        }

        /// <summary>
        /// Call Windows Copilot Runtime (Phi Silica) via the existing WindowsAIService.
        /// </summary>
        private async Task<string> CallWindowsAIAsync(string prompt, string? systemPrompt)
        {
            // WindowsAIService exposes SummarizeAsync/RewriteAsync/OrganizeAsync (all public)
            // which internally call GenerateResponseAsync. For arbitrary prompts, we use
            // SummarizeAsync as a general-purpose proxy since the system prompt is embedded
            // in the prompt text itself.
            var sp = (systemPrompt ?? "").ToLowerInvariant();
            if (sp.Contains("rewrite") || sp.Contains("improve") || sp.Contains("editor"))
                return await WindowsAIService.Instance.RewriteAsync(prompt);
            if (sp.Contains("organiz") || sp.Contains("bullet") || sp.Contains("structure"))
                return await WindowsAIService.Instance.OrganizeAsync(prompt);
            // Default: use summarize for general AI requests
            return await WindowsAIService.Instance.SummarizeAsync(prompt);
        }

        /// <summary>
        /// Offline fallback using OfflineTextProcessor (extractive, no API needed).
        /// </summary>
        private string CallOffline(string prompt, string? systemPrompt)
        {
            // Infer which action from the system prompt keywords
            var sp = (systemPrompt ?? "").ToLowerInvariant();
            if (sp.Contains("summariz")) return OfflineTextProcessor.Summarize(prompt);
            if (sp.Contains("rewrite") || sp.Contains("improve")) return OfflineTextProcessor.Rewrite(prompt);
            if (sp.Contains("organiz")) return OfflineTextProcessor.Organize(prompt);
            // For all other actions, offline can only provide the original text with a notice
            return $"[Offline mode — cloud AI required for this action]\n\n{OfflineTextProcessor.Summarize(prompt)}";
        }

        // ═══════════════════════════════════════════════════════════
        // TEST CONNECTION
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Tests the API connection with a simple prompt. Returns provider name + response time.
        /// </summary>
        public async Task<(bool success, string provider, string message, int responseTimeMs)> TestConnectionAsync(
            CancellationToken ct = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var result = await GenerateAsync("Say 'Hello! Connection successful.' in exactly those words.",
                    "You are a test assistant. Respond exactly as instructed.", maxTokens: 50, ct: ct);
                sw.Stop();
                return (true, ActiveProviderName, result.Trim(), (int)sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                sw.Stop();
                return (false, ActiveProviderName, ex.Message, (int)sw.ElapsedMilliseconds);
            }
        }

        // ═══════════════════════════════════════════════════════════
        // HELPERS
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// Auto-detect provider from API key prefix/format.
        /// </summary>
        private string DetectProviderFromKey()
        {
            var key = SettingsManager.Current.AiApiKey ?? "";
            var provider = SettingsManager.Current.AiProvider?.ToLowerInvariant() ?? "auto";

            // If user explicitly set a provider, trust it
            if (provider is "gemini" or "openai" or "claude") return provider switch
            {
                "gemini" => "Gemini",
                "openai" => "OpenAI",
                "claude" => "Claude",
                _ => "Gemini"
            };

            // Auto-detect from key format (sk-ant- must be checked before sk- to avoid misdetection)
            if (key.StartsWith("sk-ant-", StringComparison.Ordinal)) return "Claude";
            if (key.StartsWith("sk-", StringComparison.Ordinal)) return "OpenAI";
            if (key.StartsWith("AIza", StringComparison.Ordinal)) return "Gemini";

            // Default to Gemini (most generous free tier)
            return "Gemini";
        }

        /// <summary>
        /// Truncate error messages for logging (avoid huge API error responses in logs).
        /// </summary>
        private static string TruncateError(string error)
        {
            if (string.IsNullOrEmpty(error)) return "(empty)";
            return error.Length > 200 ? error[..200] + "..." : error;
        }

        /// <summary>
        /// Clear the response cache (useful after changing API key or provider).
        /// </summary>
        public void ClearCache()
        {
            lock (_cacheLock) { _cache.Clear(); }
        }
    }
}
