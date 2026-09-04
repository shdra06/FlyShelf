// ---------------------------------------------------------------
// TodoPanelControl.Search.cs — Fuzzy search across all todo days
// Handles: applying search queries across all days and subtasks,
// binding filtered results to the ItemsControl, and restoring
// the normal view when the search is cleared.
// ---------------------------------------------------------------
using FlyShelf.Classes;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Controls;

namespace FlyShelf.Controls
{
    public partial class TodoPanelControl : UserControl
    {
        // ═══════════════════════════════════════════════════════════
        // TODO SEARCH — Fuzzy search across all days
        // ═══════════════════════════════════════════════════════════

        private ObservableCollection<TodoItem>? _todoSearchResults;

        /// <summary>
        /// Clears todo search results and restores normal selected day items.
        /// </summary>
        public void ClearSearch()
        {
            _todoSearchResults = null;
            if (_selectedTodoDay != null && TodoListItemsControl != null)
            {
                TodoListItemsControl.ItemsSource = _selectedTodoDay.Items;
            }
        }

        private void ApplyTodoSearch(string query)
        {
            string queryClean = (query ?? "").Trim();
            if (string.IsNullOrWhiteSpace(queryClean))
            {
                ClearSearch();
                return;
            }

            // Search across ALL days for matching items
            const int MAX_SEARCH_RESULTS = 200;
            var results = new ObservableCollection<TodoItem>();
            foreach (var day in TodoManager.Days)
            {
                if (results.Count >= MAX_SEARCH_RESULTS) break;
                foreach (var item in day.Items)
                {
                    if (results.Count >= MAX_SEARCH_RESULTS) break;
                    if (IsTodoItemMatch(queryClean, item))
                    {
                        results.Add(item);
                    }
                    // Also search subtasks
                    foreach (var sub in item.SubTasks)
                    {
                        if (results.Count >= MAX_SEARCH_RESULTS) break;
                        if (IsTodoItemMatch(queryClean, sub) && !results.Contains(sub))
                        {
                            results.Add(sub);
                        }
                    }
                }
            }

            _todoSearchResults = results;
            TodoListItemsControl.ItemsSource = results;
        }

        private static bool IsTodoItemMatch(string query, TodoItem item)
        {
            return FuzzyMatcher.IsMatchAny(query, item.Text, item.Description)
                || item.Tags.Any(t => FuzzyMatcher.IsMatch(query, t));
        }
    }
}
