using System.Text.RegularExpressions;

namespace FileTransfer.Tests;

public class PathTemplateRendererTests
{
    private readonly PathTemplateRenderer _renderer = new();

    [Theory]
    [InlineData("{guid}", "^[0-9a-f]{32}$")]
    [InlineData("{guid:N}", "^[0-9a-f]{32}$")]
    [InlineData("{guid:D}", "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")]
    [InlineData("{guid:B}", "^\\{[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\\}$")]
    [InlineData("{guid:P}", "^\\([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\\)$")]
    [InlineData("{guid:X}", "^\\{0x[0-9a-f]{8},0x[0-9a-f]{4},0x[0-9a-f]{4},\\{0x[0-9a-f]{2},0x[0-9a-f]{2},0x[0-9a-f]{2},0x[0-9a-f]{2},0x[0-9a-f]{2},0x[0-9a-f]{2},0x[0-9a-f]{2},0x[0-9a-f]{2}\\}\\}$")]
    public void TryRender_GuidBuiltIn_SupportsStandardFormats(string template, string expectedPattern)
    {
        var match = CreateMatch(@"^(?<date>\d{8})-(?<tail>.+)\.txt$", "20260122-report.txt");

        var success = _renderer.TryRender(
            template,
            @"C:\Inbox\A1\20260122-report.txt",
            "A1/20260122-report.txt",
            match,
            out var rendered,
            out var error);

        Assert.True(success);
        Assert.Null(error);
        Assert.Matches(expectedPattern, rendered);
    }

    [Fact]
    public void TryRender_DateTimeBuiltIn_SupportsCustomFormat()
    {
        var match = CreateMatch(@"^(?<date>\d{8})-(?<tail>.+)\.txt$", "20260122-report.txt");

        var success = _renderer.TryRender(
            "Rendered/{now:yyyy-MM-dd}/{utcNow:yyyyMMddHHmmss}",
            @"C:\Inbox\A1\20260122-report.txt",
            "A1/20260122-report.txt",
            match,
            out var rendered,
            out var error);

        Assert.True(success);
        Assert.Null(error);

        var segments = rendered.Split('/');
        Assert.Equal("Rendered", segments[0]);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", segments[1]);
        Assert.Matches(@"^\d{14}$", segments[2]);
    }

    [Fact]
    public void TryRender_StringBuiltIn_WithFormat_ReturnsError()
    {
        var match = CreateMatch(@"^(?<date>\d{8})-(?<tail>.+)\.txt$", "20260122-report.txt");

        var success = _renderer.TryRender(
            "{fileName:yyyyMMdd}",
            @"C:\Inbox\A1\20260122-report.txt",
            "A1/20260122-report.txt",
            match,
            out _,
            out var error);

        Assert.False(success);
        Assert.Equal("Template placeholder 'fileName' does not support format strings.", error);
    }

    [Fact]
    public void TryRender_NamedCaptureDateFormat_FormatsCaptureValue()
    {
        var match = CreateMatch(@"^(?<date>\d{8})-(?<tail>.+)\.txt$", "20260122-report.txt");

        var success = _renderer.TryRender(
            "Rendered/{date:yyyy-MM-dd}/{tail}",
            @"C:\Inbox\A1\20260122-report.txt",
            "A1/20260122-report.txt",
            match,
            out var rendered,
            out var error);

        Assert.True(success);
        Assert.Null(error);
        Assert.Equal("Rendered/2026-01-22/report", rendered);
    }

    [Fact]
    public void TryRender_NamedCaptureDateFormat_OnNonDateValue_ReturnsError()
    {
        var match = CreateMatch(@"^(?<date>[^-]+)-(?<tail>.+)\.txt$", "not-a-date-report.txt");

        var success = _renderer.TryRender(
            "Rendered/{date:yyyy-MM-dd}/{tail}",
            @"C:\Inbox\A1\not-a-date-report.txt",
            "A1/not-a-date-report.txt",
            match,
            out _,
            out var error);

        Assert.False(success);
        Assert.Equal("Template placeholder 'date' can only use a date/time format string when the captured value is parseable as date/time.", error);
    }

    [Fact]
    public void TryValidate_StringBuiltInWithFormat_IsRejected()
    {
        var mapping = CreatePathRule("Rendered/{fileName:yyyyMMdd}");

        var success = _renderer.TryValidate(mapping, out var error);

        Assert.False(success);
        Assert.Equal("Template placeholder 'fileName' does not support format strings.", error);
    }

    [Fact]
    public void TryValidate_GuidWithInvalidFormat_IsRejected()
    {
        var mapping = CreatePathRule("Rendered/{guid:Q}");

        var success = _renderer.TryValidate(mapping, out var error);

        Assert.False(success);
        Assert.Equal("Template placeholder 'guid' only supports GUID format strings: N, D, B, P, X.", error);
    }

    [Fact]
    public void TryValidate_DateTimeBuiltInWithInvalidFormat_IsRejected()
    {
        var mapping = CreatePathRule("Rendered/{now:Q}");

        var success = _renderer.TryValidate(mapping, out var error);

        Assert.False(success);
        Assert.Equal("Template placeholder 'now' has an invalid date/time format string 'Q'.", error);
    }

    [Fact]
    public void TryValidate_DateTimeBuiltInWithPathUnsafeFormat_IsRejected()
    {
        var mapping = CreatePathRule("Rendered/{now:O}");

        var success = _renderer.TryValidate(mapping, out var error);

        Assert.False(success);
        Assert.Equal("Template placeholder 'now' format 'O' produces characters that are invalid in Windows paths.", error);
    }

    [Fact]
    public void TryValidate_NamedCaptureDateFormat_AcceptsValidDateFormat()
    {
        var mapping = CreatePathRule("Rendered/{date:yyyy-MM-dd}/{tail}");

        var success = _renderer.TryValidate(mapping, out var error);

        Assert.True(success);
        Assert.Null(error);
    }

    [Fact]
    public void TryValidate_UnknownPlaceholder_IsRejected()
    {
        var mapping = CreatePathRule("Rendered/{missing}");

        var success = _renderer.TryValidate(mapping, out var error);

        Assert.False(success);
        Assert.Equal("Unknown template placeholder 'missing'.", error);
    }

    [Fact]
    public void TryValidate_UnmatchedBrace_IsRejected()
    {
        var mapping = CreatePathRule("Rendered/{date");

        var success = _renderer.TryValidate(mapping, out var error);

        Assert.False(success);
        Assert.Equal("Template contains unmatched braces.", error);
    }


    [Fact]
    public void TryValidate_StaticTemplateFragmentWithWindowsInvalidCharacter_IsRejected()
    {
        var rule = CreatePathRule("Rendered/invalid:segment/{fileName}");

        var success = _renderer.TryValidate(rule, out var error);

        Assert.False(success);
        Assert.Equal("Template contains characters that are invalid in Windows paths.", error);
    }
    private static PathRule CreatePathRule(string template) =>
        new()
        {
            MatchPattern = @"^(?<date>\d{8})-(?<tail>.+)\.txt$",
            TargetTemplate = template
        };

    private static Match CreateMatch(string pattern, string relativePath)
    {
        var match = new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase).Match(relativePath);
        Assert.True(match.Success);
        return match;
    }
}
