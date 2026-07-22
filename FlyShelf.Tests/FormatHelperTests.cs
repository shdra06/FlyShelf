using FluentAssertions;
using FlyShelf.Classes;
using Xunit;

namespace FlyShelf.Tests;

public class FormatHelperTests
{
    // ═══════════════════════════════════════════════════════════
    // FormatSize
    // ═══════════════════════════════════════════════════════════

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "2 KB")]                // 1.5 KB → "2 KB" (F0 rounds)
    [InlineData(1048576, "1.0 MB")]           // exactly 1 MB
    [InlineData(1572864, "1.5 MB")]           // 1.5 MB
    [InlineData(10485760, "10.0 MB")]         // 10 MB
    public void FormatSize_VariousSizes_ReturnsExpectedFormat(long bytes, string expected)
    {
        FormatHelper.FormatSize(bytes).Should().Be(expected);
    }

    [Fact]
    public void FormatSize_LargeFile_ReturnsMB()
    {
        var result = FormatHelper.FormatSize(500 * 1024 * 1024L); // 500 MB
        result.Should().Contain("MB");
    }

    // ═══════════════════════════════════════════════════════════
    // GetFileTypeFriendly
    // ═══════════════════════════════════════════════════════════

    [Theory]
    [InlineData("photo.png", "Image")]
    [InlineData("photo.JPG", "Image")]
    [InlineData("photo.jpeg", "Image")]
    [InlineData("photo.webp", "Image")]
    [InlineData("photo.gif", "Image")]
    [InlineData("report.pdf", "PDF")]
    [InlineData("essay.docx", "Document")]
    [InlineData("essay.doc", "Document")]
    [InlineData("notes.txt", "Document")]
    [InlineData("data.xlsx", "Spreadsheet")]
    [InlineData("data.csv", "Spreadsheet")]
    [InlineData("slides.pptx", "Presentation")]
    [InlineData("backup.zip", "Archive")]
    [InlineData("backup.7z", "Archive")]
    [InlineData("backup.tar", "Archive")]
    [InlineData("song.mp3", "Audio")]
    [InlineData("song.wav", "Audio")]
    [InlineData("movie.mp4", "Video")]
    [InlineData("movie.mkv", "Video")]
    [InlineData("app.apk", "Android App")]
    [InlineData("mystery.xyz", "File")]
    public void GetFileTypeFriendly_KnownExtensions_ReturnsCategory(string fileName, string expected)
    {
        FormatHelper.GetFileTypeFriendly(fileName).Should().Be(expected);
    }

    [Theory]
    [InlineData(null, "File")]
    [InlineData("", "File")]
    public void GetFileTypeFriendly_NullOrEmpty_ReturnsFile(string? fileName, string expected)
    {
        FormatHelper.GetFileTypeFriendly(fileName!).Should().Be(expected);
    }

    [Fact]
    public void GetFileTypeFriendly_NoExtension_ReturnsFile()
    {
        FormatHelper.GetFileTypeFriendly("README").Should().Be("File");
    }

    [Fact]
    public void GetFileTypeFriendly_CaseInsensitive_MatchesCorrectly()
    {
        // Extension comparison should be case-insensitive
        FormatHelper.GetFileTypeFriendly("Photo.PNG").Should().Be("Image");
        FormatHelper.GetFileTypeFriendly("REPORT.PDF").Should().Be("PDF");
    }
}
