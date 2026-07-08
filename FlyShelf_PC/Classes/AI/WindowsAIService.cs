using System;
using System.Reflection;
using System.Threading.Tasks;

namespace FlyShelf.Classes
{
    public class WindowsAIService
    {
        private static readonly Lazy<WindowsAIService> _instance = new Lazy<WindowsAIService>(() => new WindowsAIService());
        public static WindowsAIService Instance => _instance.Value;

        private readonly Type _modelType;
        private readonly bool _isSupported;

        private WindowsAIService()
        {
            try
            {
                if (!StartupHelper.IsPackaged())
                {
                    _isSupported = false;
                    Logger.LogAction("AI_INIT", "Unpackaged execution; local AI requires MSIX deployment.");
                    return;
                }

                bool hasText = global::Windows.Foundation.Metadata.ApiInformation.IsTypePresent("Microsoft.Windows.AI.Text.LanguageModel");
                bool hasGen = global::Windows.Foundation.Metadata.ApiInformation.IsTypePresent("Microsoft.Windows.AI.Generative.LanguageModel");
                _isSupported = hasText || hasGen;

                Logger.LogAction("AI_INIT", $"IsTypePresent results: Text.LanguageModel={hasText}, Generative.LanguageModel={hasGen}");

                if (_isSupported)
                {
                    _modelType = ResolveLanguageModelType();
                    Logger.LogAction("AI_INIT", $"Resolved _modelType: {(_modelType != null ? _modelType.FullName : "NULL")}");
                }
                else
                {
                    Logger.LogAction("AI_INIT", "Neither Microsoft.Windows.AI.Text.LanguageModel nor Microsoft.Windows.AI.Generative.LanguageModel is present via ApiInformation.");
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("AI_INIT_ERROR", $"Error during initialization: {ex.Message}\n{ex.StackTrace}");
                _isSupported = false;
            }
        }

        public bool IsAvailable
        {
            get
            {
                if (!_isSupported || _modelType == null)
                    return false;

                try
                {
                    // Call static LanguageModel.GetReadyState() via reflection if it exists
                    var getReadyStateMethod = _modelType.GetMethod("GetReadyState", BindingFlags.Public | BindingFlags.Static);
                    if (getReadyStateMethod != null)
                    {
                        var state = getReadyStateMethod.Invoke(null, null);
                        if (state != null)
                        {
                            string stateStr = state.ToString();
                            // If it's ready, we can use it.
                            // Standard enum values: Ready, NotReady, etc.
                            return stateStr.Equals("Ready", StringComparison.OrdinalIgnoreCase);
                        }
                    }
                    return true; // Fallback if GetReadyState method is not present
                }
                catch
                {
                    return false;
                }
            }
        }

        public async Task<string> SummarizeAsync(string text)
        {
            string prompt = "Summarize the following note concisely, highlighting key points. Output only the summarized note, nothing else:\n\n" + text;
            return await GenerateResponseAsync(prompt);
        }

        public async Task<string> RewriteAsync(string text)
        {
            string prompt = "Rewrite and improve the clarity of the following note, preserving its original meaning. Output only the rewritten note, nothing else:\n\n" + text;
            return await GenerateResponseAsync(prompt);
        }

        public async Task<string> OrganizeAsync(string text)
        {
            string prompt = "Organize the following note into clear bullet points or logical sections. Output only the organized note, nothing else:\n\n" + text;
            return await GenerateResponseAsync(prompt);
        }

        private async Task<string> GenerateResponseAsync(string prompt)
        {
            if (!_isSupported || _modelType == null)
                throw new NotSupportedException("Windows Copilot Runtime AI is not supported on this machine.");

            try
            {
                // Ensure model is ready (if EnsureReadyAsync is present)
                var getReadyStateMethod = _modelType.GetMethod("GetReadyState", BindingFlags.Public | BindingFlags.Static);
                if (getReadyStateMethod != null)
                {
                    var state = getReadyStateMethod.Invoke(null, null);
                    if (state != null && !state.ToString().Equals("Ready", StringComparison.OrdinalIgnoreCase))
                    {
                        var ensureReadyMethod = _modelType.GetMethod("EnsureReadyAsync", BindingFlags.Public | BindingFlags.Static);
                        if (ensureReadyMethod != null)
                        {
                            dynamic ensureReadyOp = ensureReadyMethod.Invoke(null, null);
                            await ensureReadyOp;
                        }
                    }
                }

                // Create the model: LanguageModel.CreateAsync()
                var createAsyncMethod = _modelType.GetMethod("CreateAsync", BindingFlags.Public | BindingFlags.Static);
                if (createAsyncMethod == null)
                    throw new InvalidOperationException("Could not find CreateAsync method on LanguageModel.");

                dynamic createAsyncOp = createAsyncMethod.Invoke(null, null);
                dynamic model = await createAsyncOp;

                if (model == null)
                    throw new InvalidOperationException("Failed to instantiate local LanguageModel.");

                try
                {
                    // Generate response: model.GenerateResponseAsync(prompt)
                    var generateMethod = model.GetType().GetMethod("GenerateResponseAsync", new Type[] { typeof(string) });
                    if (generateMethod == null)
                        throw new InvalidOperationException("Could not find GenerateResponseAsync method on LanguageModel.");

                    dynamic generateOp = generateMethod.Invoke(model, new object[] { prompt });
                    dynamic result = await generateOp;

                    if (result == null)
                        throw new InvalidOperationException("Null result received from local LanguageModel.");

                    // Retrieve response text from result.Response
                    var responseProp = result.GetType().GetProperty("Response");
                    string response = responseProp?.GetValue(result) as string;

                    return response ?? string.Empty;
                }
                finally
                {
                    // Clean up resources if model implements IDisposable
                    if (model is IDisposable disposable)
                    {
                        disposable.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[WindowsAIService] Error during AI generation: {ex}");
                throw new Exception($"AI Generation failed: {ex.Message}", ex);
            }
        }

        private static Type ResolveLanguageModelType()
        {
            try
            {
                // Try direct resolution
                try
                {
                    var t1 = Type.GetType("Microsoft.Windows.AI.Text.LanguageModel, Microsoft.Windows.AI.Text");
                    if (t1 != null) return t1;
                }
                catch (Exception ex)
                {
                    Logger.LogAction("AI_RESOLVE_ERR", $"Direct resolution t1 failed: {ex.Message}");
                }

                try
                {
                    var t2 = Type.GetType("Microsoft.Windows.AI.Text.LanguageModel");
                    if (t2 != null) return t2;
                }
                catch (Exception ex)
                {
                    Logger.LogAction("AI_RESOLVE_ERR", $"Direct resolution t2 failed: {ex.Message}");
                }

                try
                {
                    var t3 = Type.GetType("Microsoft.Windows.AI.Generative.LanguageModel, Microsoft.Windows.AI.Generative");
                    if (t3 != null) return t3;
                }
                catch (Exception ex)
                {
                    Logger.LogAction("AI_RESOLVE_ERR", $"Direct resolution t3 failed: {ex.Message}");
                }

                try
                {
                    var t4 = Type.GetType("Microsoft.Windows.AI.Generative.LanguageModel");
                    if (t4 != null) return t4;
                }
                catch (Exception ex)
                {
                    Logger.LogAction("AI_RESOLVE_ERR", $"Direct resolution t4 failed: {ex.Message}");
                }

                // Scan loaded assemblies
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        var name = assembly.FullName;
                        if (name.Contains("Microsoft.Windows.AI") || name.Contains("Microsoft.WindowsAppSDK") || name.Contains("Microsoft.Windows.AI.Text"))
                        {
                            var t = assembly.GetType("Microsoft.Windows.AI.Text.LanguageModel") 
                                 ?? assembly.GetType("Microsoft.Windows.AI.Generative.LanguageModel");
                            if (t != null) return t;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogAction("AI_RESOLVE_ERR", $"Assembly scan of {assembly.FullName} failed: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogAction("AI_RESOLVE_ERR", $"General resolve error: {ex.Message}");
            }
            return null;
        }
    }
}
