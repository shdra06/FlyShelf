using FluentAssertions;
using FlyShelf.Classes;
using Xunit;

namespace FlyShelf.Tests;

public class FuzzyMatcherTests
{
    // ═══════════════════════════════════════════════════════════
    // IsMatch — Exact substring
    // ═══════════════════════════════════════════════════════════

    [Theory]
    [InlineData("hello", "Hello World", true)]
    [InlineData("WORLD", "Hello World", true)]
    [InlineData("xyz", "Hello World", false)]
    public void IsMatch_ExactSubstring_CaseInsensitive(string query, string text, bool expected)
    {
        FuzzyMatcher.IsMatch(query, text).Should().Be(expected);
    }

    // ═══════════════════════════════════════════════════════════
    // IsMatch — Null/empty edge cases
    // ═══════════════════════════════════════════════════════════

    [Theory]
    [InlineData(null, "text", false)]
    [InlineData("query", null, false)]
    [InlineData("", "text", false)]
    [InlineData("query", "", false)]
    [InlineData("   ", "text", false)]
    public void IsMatch_NullOrEmptyInputs_ReturnsFalse(string? query, string? text, bool expected)
    {
        FuzzyMatcher.IsMatch(query!, text!).Should().Be(expected);
    }

    // ═══════════════════════════════════════════════════════════
    // IsMatch — Multi-word queries (all words must appear)
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void IsMatch_MultiWordQuery_AllWordsPresent_ReturnsTrue()
    {
        FuzzyMatcher.IsMatch("hello world", "The World says Hello to you").Should().BeTrue();
    }

    [Fact]
    public void IsMatch_MultiWordQuery_SomeWordsMissing_ReturnsFalse()
    {
        FuzzyMatcher.IsMatch("hello planet", "The World says Hello to you").Should().BeFalse();
    }

    // ═══════════════════════════════════════════════════════════
    // IsMatch — Fuzzy trigram matching (typo tolerance)
    // ═══════════════════════════════════════════════════════════

    [Theory]
    [InlineData("helo", "hello world greetings", true)]   // 1-char typo
    [InlineData("wrld", "hello world greetings", true)]   // 1-char typo
    public void IsMatch_TypoInQuery_FuzzyMatchesCorrectly(string query, string text, bool expected)
    {
        FuzzyMatcher.IsMatch(query, text).Should().Be(expected);
    }

    // ═══════════════════════════════════════════════════════════
    // Score — Ranking tiers
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Score_ExactMatch_Returns1()
    {
        FuzzyMatcher.Score("hello", "hello").Should().Be(1.0);
    }

    [Fact]
    public void Score_StartsWithMatch_Returns0_9()
    {
        FuzzyMatcher.Score("hello", "hello world").Should().Be(0.9);
    }

    [Fact]
    public void Score_SubstringMatch_Returns0_8()
    {
        FuzzyMatcher.Score("world", "hello world").Should().Be(0.8);
    }

    [Fact]
    public void Score_NoMatch_Returns0()
    {
        FuzzyMatcher.Score("zzzzz", "hello world").Should().Be(0.0);
    }

    [Fact]
    public void Score_NullInputs_Returns0()
    {
        FuzzyMatcher.Score(null!, null!).Should().Be(0.0);
        FuzzyMatcher.Score("query", null!).Should().Be(0.0);
    }

    // ═══════════════════════════════════════════════════════════
    // ScoreBest — Best across multiple fields
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void ScoreBest_MultipleTexts_ReturnsBestScore()
    {
        var score = FuzzyMatcher.ScoreBest("hello", "no match", "hello world", "hello");
        score.Should().Be(1.0); // exact match on "hello"
    }

    [Fact]
    public void ScoreBest_AllNull_Returns0()
    {
        FuzzyMatcher.ScoreBest("query", null, null).Should().Be(0.0);
    }

    // ═══════════════════════════════════════════════════════════
    // IsMatchAny — Convenience overload
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void IsMatchAny_OneMatches_ReturnsTrue()
    {
        FuzzyMatcher.IsMatchAny("hello", "no match", "hello world").Should().BeTrue();
    }

    [Fact]
    public void IsMatchAny_NoneMatch_ReturnsFalse()
    {
        FuzzyMatcher.IsMatchAny("zzz", "aaa", "bbb", "ccc").Should().BeFalse();
    }
}
