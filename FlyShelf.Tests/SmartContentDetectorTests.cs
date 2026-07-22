using FluentAssertions;
using FlyShelf.Classes;
using Xunit;

namespace FlyShelf.Tests;

public class SmartContentDetectorTests
{
    // ═══════════════════════════════════════════════════════════
    // IsValidJson
    // ═══════════════════════════════════════════════════════════

    [Theory]
    [InlineData("{\"name\":\"Alice\"}", true)]
    [InlineData("[1,2,3]", true)]
    [InlineData("{}", true)]
    [InlineData("[]", true)]
    [InlineData("not json", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("  { \"key\": 42 }  ", true)]  // whitespace-padded
    [InlineData("{invalid}", false)]
    public void IsValidJson_VariousInputs_ReturnsExpected(string? input, bool expected)
    {
        SmartContentDetector.IsValidJson(input!).Should().Be(expected);
    }

    // ═══════════════════════════════════════════════════════════
    // PrettyPrintJson
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void PrettyPrintJson_WithCompactJson_ReturnsIndented()
    {
        var result = SmartContentDetector.PrettyPrintJson("{\"a\":1}");
        result.Should().Contain("\n");
        result.Should().Contain("\"a\"");
    }

    [Fact]
    public void PrettyPrintJson_WithInvalidJson_ReturnsOriginal()
    {
        SmartContentDetector.PrettyPrintJson("not json").Should().Be("not json");
    }

    // ═══════════════════════════════════════════════════════════
    // ContainsEmail / ExtractFirstEmail
    // ═══════════════════════════════════════════════════════════

    [Theory]
    [InlineData("Email me at user@example.com", true)]
    [InlineData("no email here", false)]
    [InlineData("user+tag@sub.domain.co.uk", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ContainsEmail_VariousInputs_ReturnsExpected(string? input, bool expected)
    {
        SmartContentDetector.ContainsEmail(input!).Should().Be(expected);
    }

    [Fact]
    public void ExtractFirstEmail_WithMultipleEmails_ReturnsFirst()
    {
        var result = SmartContentDetector.ExtractFirstEmail("Contact alice@test.com or bob@test.com");
        result.Should().Be("alice@test.com");
    }

    [Fact]
    public void ExtractFirstEmail_WithNoEmail_ReturnsNull()
    {
        SmartContentDetector.ExtractFirstEmail("no email here").Should().BeNull();
    }

    // ═══════════════════════════════════════════════════════════
    // IsMathExpression / EvaluateMath
    // ═══════════════════════════════════════════════════════════

    [Theory]
    [InlineData("2+3", true)]
    [InlineData("100 * 5 / 2", true)]
    [InlineData("(10+5)*3", true)]
    [InlineData("hello", false)]
    [InlineData("42", false)]         // no operator
    [InlineData("", false)]
    [InlineData("ab", false)]         // too short
    public void IsMathExpression_VariousInputs_ReturnsExpected(string? input, bool expected)
    {
        SmartContentDetector.IsMathExpression(input!).Should().Be(expected);
    }

    [Fact]
    public void EvaluateMath_SimpleAddition_ReturnsCorrectResult()
    {
        SmartContentDetector.EvaluateMath("2 + 3").Should().Be("5");
    }

    [Fact]
    public void EvaluateMath_InvalidExpression_ReturnsError()
    {
        SmartContentDetector.EvaluateMath("abc").Should().Be("Error");
    }

    // ═══════════════════════════════════════════════════════════
    // IsBase64 / DecodeBase64
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void IsBase64_WithValidBase64String_ReturnsTrue()
    {
        // "Hello, World! This is a test." encoded
        string encoded = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes("Hello, World! This is a test."));
        SmartContentDetector.IsBase64(encoded).Should().BeTrue();
    }

    [Theory]
    [InlineData("short", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("has spaces inside value", false)]
    public void IsBase64_InvalidInputs_ReturnsFalse(string? input, bool expected)
    {
        SmartContentDetector.IsBase64(input!).Should().Be(expected);
    }

    [Fact]
    public void DecodeBase64_ValidBase64_ReturnsDecodedString()
    {
        string original = "FlyShelf test data!";
        string encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(original));
        SmartContentDetector.DecodeBase64(encoded).Should().Be(original);
    }

    [Fact]
    public void DecodeBase64_InvalidBase64_ReturnsFailureMessage()
    {
        SmartContentDetector.DecodeBase64("!!!").Should().Be("[Failed to decode Base64]");
    }

    // ═══════════════════════════════════════════════════════════
    // IsEpochTimestamp / EpochToDateTime
    // ═══════════════════════════════════════════════════════════

    [Theory]
    [InlineData("1700000000", true)]       // Nov 2023, seconds
    [InlineData("1700000000000", true)]    // Nov 2023, milliseconds
    [InlineData("100000000", false)]       // Before year 2000
    [InlineData("hello", false)]
    [InlineData("", false)]
    public void IsEpochTimestamp_VariousInputs_ReturnsExpected(string? input, bool expected)
    {
        SmartContentDetector.IsEpochTimestamp(input!).Should().Be(expected);
    }

    [Fact]
    public void EpochToDateTime_ValidEpochSeconds_ReturnsFormattedDate()
    {
        var result = SmartContentDetector.EpochToDateTime("0");
        // Even "0" should not crash — but IsEpochTimestamp would reject it; this tests the method in isolation
        result.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void EpochToDateTime_InvalidInput_ReturnsError()
    {
        SmartContentDetector.EpochToDateTime("not_a_number").Should().Be("[Invalid timestamp]");
    }
}
