using Tramoya.Core;

namespace Tramoya.Tests;

/// <summary>
/// <c>Shell</c> and <c>Fmt</c> against Python's <c>shlex</c> and <c>str(float)</c>; every
/// expected value here is what Python 3.12 prints for the same input. They matter because
/// dry-run output and ffmpeg argv are diffed against the Python CLI's.
/// </summary>
public sealed class ShellTests
{
    [Theory]
    [InlineData("a b", "'a b'")]
    [InlineData("", "''")]
    [InlineData("it's", "'it'\"'\"'s'")]
    [InlineData("/usr/bin/x", "/usr/bin/x")]
    [InlineData("a=b:c,d", "a=b:c,d")]
    [InlineData("x\"y", "'x\"y'")]
    [InlineData("tab\there", "'tab\there'")]
    [InlineData("año", "'año'")]
    public void QuoteMatchesShlexQuote(string input, string expected)
    {
        Assert.Equal(expected, Shell.Quote(input));
    }

    [Fact]
    public void JoinQuotesEachArgument()
    {
        Assert.Equal("ffmpeg -i 'in file.mp4' -vf 'drawtext=text='\"'\"'hi'\"'\"'' out.mp4",
            Shell.Join(new[] { "ffmpeg", "-i", "in file.mp4", "-vf", "drawtext=text='hi'", "out.mp4" }));
    }

    [Theory]
    [InlineData("python -m http.server 8000", "python|-m|http.server|8000")]
    [InlineData("\"a b\" c", "a b|c")]
    [InlineData("it\\'s", "it's")]
    [InlineData("'single quoted' x", "single quoted|x")]
    [InlineData("\"dq \\\"x\\\" \\$y\"", "dq \"x\" \\$y")]
    [InlineData("  a   b\tc  ", "a|b|c")]
    [InlineData("a\\ b 'c'd", "a b|cd")]
    [InlineData("'it''s'", "its")]
    [InlineData("x'y z'w", "xy zw")]
    [InlineData("", "")]
    public void SplitMatchesShlexSplit(string command, string expected)
    {
        var words = expected.Length == 0 ? Array.Empty<string>() : expected.Split('|');
        Assert.Equal(words, Shell.Split(command));
    }

    [Theory]
    [InlineData(1.0, "1.0")]
    [InlineData(0.05, "0.05")]
    [InlineData(2.5, "2.5")]
    [InlineData(12.333333333333334, "12.333333333333334")]
    [InlineData(0.0001, "0.0001")]
    [InlineData(100.0, "100.0")]
    [InlineData(123456789.123, "123456789.123")]
    [InlineData(-3.0, "-3.0")]
    public void PyMatchesPythonStrFloat(double value, string expected)
    {
        Assert.Equal(expected, Fmt.Py(value));
    }

    [Theory]
    [InlineData(1.5, 3, "1.500")]
    [InlineData(2.0, 1, "2.0")]
    [InlineData(0.0049, 2, "0.00")]
    [InlineData(12.3456, 2, "12.35")]
    public void FMatchesPythonFixedFormat(double value, int decimals, string expected)
    {
        Assert.Equal(expected, Fmt.F(value, decimals));
    }
}
