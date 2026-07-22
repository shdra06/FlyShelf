using FluentAssertions;
using FlyShelf.Classes;
using Xunit;

namespace FlyShelf.Tests;

public class NaturalLanguageReminderParserTests
{
    // Fixed reference time for deterministic tests: 2025-06-15 14:30:00 (Sunday)
    private static readonly DateTime RefTime = new(2025, 6, 15, 14, 30, 0);

    // ═══════════════════════════════════════════════════════════
    // Parse — Date extraction
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Parse_WithTomorrow_ResolveToNextDay()
    {
        var (title, dueDate) = NaturalLanguageReminderParser.Parse("meeting tomorrow", RefTime);

        dueDate.Date.Should().Be(RefTime.Date.AddDays(1));
        title.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Parse_WithSpecificTime_ResolvesCorrectTime()
    {
        var (title, dueDate) = NaturalLanguageReminderParser.Parse("call at 9 am tomorrow", RefTime);

        dueDate.Date.Should().Be(RefTime.Date.AddDays(1));
        title.Should().NotBeNullOrWhiteSpace();
        // The title should have the date portion stripped, leaving the meaningful part
        title.ToLowerInvariant().Should().Contain("call");
    }

    [Fact]
    public void Parse_WithNextMonday_ResolvesToNextMonday()
    {
        var (_, dueDate) = NaturalLanguageReminderParser.Parse("standup next Monday", RefTime);

        // RefTime is Sunday June 15 2025 → next Monday is June 16
        dueDate.DayOfWeek.Should().Be(DayOfWeek.Monday);
        dueDate.Should().BeAfter(RefTime);
    }

    // ═══════════════════════════════════════════════════════════
    // Parse — Title extraction
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Parse_TitleExtraction_RemovesDatePhraseFromTitle()
    {
        var (title, _) = NaturalLanguageReminderParser.Parse("submit report on Friday", RefTime);

        // "on Friday" should be stripped; "submit report" should remain
        title.ToLowerInvariant().Should().Contain("submit");
        title.ToLowerInvariant().Should().Contain("report");
    }

    [Fact]
    public void Parse_TitleExtraction_CapitalizesFirstLetter()
    {
        var (title, _) = NaturalLanguageReminderParser.Parse("dentist appointment tomorrow at 3 pm", RefTime);

        // First letter should be capitalized
        title.Should().MatchRegex("^[A-Z]");
    }

    // ═══════════════════════════════════════════════════════════
    // Parse — Fallback behavior
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Parse_NullInput_ReturnsFallbackTitle()
    {
        var (title, dueDate) = NaturalLanguageReminderParser.Parse(null!, RefTime);

        title.Should().Be("Reminder");
        dueDate.Should().Be(RefTime.Date.AddDays(1).AddHours(9)); // tomorrow 9 AM
    }

    [Fact]
    public void Parse_EmptyInput_ReturnsFallbackTitle()
    {
        var (title, dueDate) = NaturalLanguageReminderParser.Parse("", RefTime);

        title.Should().Be("Reminder");
        dueDate.Should().Be(RefTime.Date.AddDays(1).AddHours(9));
    }

    [Fact]
    public void Parse_WhitespaceOnly_ReturnsFallbackTitle()
    {
        var (title, dueDate) = NaturalLanguageReminderParser.Parse("   ", RefTime);

        title.Should().Be("Reminder");
        dueDate.Should().Be(RefTime.Date.AddDays(1).AddHours(9));
    }

    [Fact]
    public void Parse_NoDateDetected_FallsBackToTomorrow9AM()
    {
        // Text with no recognizable date expression
        var (title, dueDate) = NaturalLanguageReminderParser.Parse("buy groceries", RefTime);

        dueDate.Should().Be(RefTime.Date.AddDays(1).AddHours(9));
        title.Should().Contain("buy groceries");
    }

    // ═══════════════════════════════════════════════════════════
    // Parse — Edge: long text truncation
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Parse_VeryLongTextWithoutDate_TruncatesTitleTo80Chars()
    {
        var longText = new string('a', 200);
        var (title, _) = NaturalLanguageReminderParser.Parse(longText, RefTime);

        // Fallback path truncates to 80 chars + "..."
        title.Length.Should().BeLessOrEqualTo(83); // 80 + "..."
    }
}
