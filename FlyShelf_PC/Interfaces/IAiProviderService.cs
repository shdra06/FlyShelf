// ---------------------------------------------------------------
// IAiProviderService — Interface for AI provider routing.
// Part of FlyShelf modularization: enables DI + testability.
// ---------------------------------------------------------------
using System.Threading.Tasks;

namespace FlyShelf.Interfaces
{
    /// <summary>
    /// Unified AI service routing requests to the best available provider.
    /// Supports summarization, rewriting, organization, translation, expansion, and explanation.
    /// </summary>
    public interface IAiProviderService
    {
        bool IsAvailable { get; }
        string CurrentProvider { get; }
        Task<string> SummarizeAsync(string text);
        Task<string> RewriteAsync(string text, string style);
        Task<string> OrganizeAsync(string text);
        Task<string> TranslateAsync(string text, string targetLanguage);
        Task<string> ExpandAsync(string text);
        Task<string> ExplainAsync(string text);
    }
}
