// ---------------------------------------------------------------
// NoteTemplateDefinitions — Single source of truth for note templates
// Used by both the toolbar Templates button (NotesTemplates_Click)
// and the bullet context menu (NoteBulletMore_Click).
// ---------------------------------------------------------------
namespace FlyShelf.Classes
{
    /// <summary>
    /// Holds the shared template definitions for Quick Notes.
    /// Each template has an emoji, a display label, an icon hex color
    /// (used in context-menu MI() calls), and an array of (header, content) sections.
    /// </summary>
    internal static class NoteTemplateDefinitions
    {
        internal sealed class Template
        {
            public string Emoji { get; }
            public string Label { get; }
            /// <summary>Hex color string for the MI() icon helper, e.g. "#22C55E".</summary>
            public string IconColor { get; }
            public (string Header, string Content)[] Sections { get; }
            /// <summary>When true, a menu separator should be inserted *before* this template.</summary>
            public bool SeparatorBefore { get; }

            public Template(string emoji, string label, string iconColor,
                            (string header, string content)[] sections,
                            bool separatorBefore = false)
            {
                Emoji = emoji;
                Label = label;
                IconColor = iconColor;
                Sections = sections;
                SeparatorBefore = separatorBefore;
            }
        }

        internal static readonly Template[] All = new[]
        {
            new Template("", "Grocery List", "#22C55E", new[]
            {
                ("Dairy", "Milk, Eggs, Cheese, Yogurt"),
                ("Produce", "Veggies, Fruits, Herbs"),
                ("Pantry", "Bread, Rice, Pasta, Cereal"),
                ("Frozen & Snacks", "")
            }),

            new Template("", "Daily Standup", "#3B82F6", new[]
            {
                ("Yesterday", ""),
                ("Today", ""),
                ("Blockers", ""),
                ("Notes", "")
            }),

            new Template("", "Meeting Notes", "#6366F1", new[]
            {
                ("Attendees", ""),
                ("Agenda", ""),
                ("Discussion", ""),
                ("Action Items", ""),
                ("Follow-up", "")
            }),

            new Template("", "Workout Planner", "#EF4444", new[]
            {
                ("Warmup", "5 min cardio"),
                ("Main Set", ""),
                ("Cooldown", "Stretching & foam roll")
            }),

            // ── Group 2: separator before this item ──
            new Template("", "Project Planning", "#00D2FF", new[]
            {
                ("Goal", ""),
                ("Tasks", ""),
                ("Timeline", ""),
                ("Risks & Mitigations", "")
            }, separatorBefore: true),

            new Template("", "Weekly Review", "#F59E0B", new[]
            {
                ("Wins", ""),
                ("Challenges", ""),
                ("Lessons Learned", ""),
                ("Next Week Priorities", "")
            }),

            new Template("", "Brain Dump", "#EC4899", new[]
            {
                ("Ideas", ""),
                ("To Research", ""),
                ("Questions", "")
            }),

            new Template("", "Reading Notes", "#A78BFA", new[]
            {
                ("Key Takeaways", ""),
                ("Quotes", ""),
                ("Reflections", "")
            }),
        };
    }
}
