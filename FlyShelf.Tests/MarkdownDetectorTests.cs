using FluentAssertions;
using FlyShelf.Classes;
using Xunit;

namespace FlyShelf.Tests;

public class MarkdownDetectorTests
{
    // ═══════════════════════════════════════════════════════════
    // IsMarkdown — True positives
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void IsMarkdown_WithHeadingsAndBold_ReturnsTrue()
    {
        var text = """
            # Welcome to FlyShelf

            This is **bold text** in a markdown document.
            Here is some more content to fill the length requirement.

            ## Section Two

            More text with *italic* words and details.
            """;
        MarkdownDetector.IsMarkdown(text).Should().BeTrue();
    }

    [Fact]
    public void IsMarkdown_WithLinksAndLists_ReturnsTrue()
    {
        var text = """
            # Getting Started

            Visit [our website](https://example.com) for more info.
            
            - Item one in the list
            - Item two in the list
            - Item three in the list
            
            Some extra text to meet the minimum length requirement for detection.
            """;
        MarkdownDetector.IsMarkdown(text).Should().BeTrue();
    }

    [Fact]
    public void IsMarkdown_WithFencedCodeBlock_ReturnsTrue()
    {
        var text = """
            # Code Example

            Here is some code:

            ```
            var x = 42;
            Console.WriteLine(x);
            ```

            That was a simple code sample with enough text to pass detection.
            """;
        MarkdownDetector.IsMarkdown(text).Should().BeTrue();
    }

    [Fact]
    public void IsMarkdown_WithImageSyntax_ReturnsTrue()
    {
        var text = """
            # Screenshot Gallery

            Here is a screenshot of the application:

            ![App Screenshot](https://example.com/screenshot.png)

            And here is another paragraph with some additional text for length.
            """;
        MarkdownDetector.IsMarkdown(text).Should().BeTrue();
    }

    // ═══════════════════════════════════════════════════════════
    // IsMarkdown — True negatives
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void IsMarkdown_PlainText_ReturnsFalse()
    {
        var text = "This is just a normal plain text sentence without any markdown formatting whatsoever, just words.";
        MarkdownDetector.IsMarkdown(text).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("short")]
    public void IsMarkdown_EmptyOrShortInput_ReturnsFalse(string? input)
    {
        MarkdownDetector.IsMarkdown(input!).Should().BeFalse();
    }

    [Fact]
    public void IsMarkdown_OnlyLowConfidencePatterns_ReturnsFalse()
    {
        // Only has unordered list items (low confidence, weight 1 each) — no high-confidence pattern
        var text = """
            - Item one with some extra text here
            - Item two with some extra text here
            - Item three with some extra text here
            - Item four with some extra text here
            - Item five with some extra text here
            - Item six with some extra text here
            """;
        MarkdownDetector.IsMarkdown(text).Should().BeFalse();
    }

    // ═══════════════════════════════════════════════════════════
    // IsMarkdown — Edge cases
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void IsMarkdown_TextExactly30Chars_ReturnsFalse()
    {
        // Exactly 30 chars — boundary: must be > 30 to proceed
        var text = "# H\n**b**\nabcdefghijklmnopqrst";
        text.Length.Should().BeLessOrEqualTo(30);
        MarkdownDetector.IsMarkdown(text).Should().BeFalse();
    }
}
