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
    [InlineData("\"abc", "No closing quotation")]
    [InlineData("'abc", "No closing quotation")]
    [InlineData("a 'b", "No closing quotation")]
    [InlineData("a \"b\\\" c", "No closing quotation")]
    [InlineData("abc\\", "No escaped character")]
    [InlineData("\"abc\\", "No escaped character")]
    [InlineData("x\\", "No escaped character")]
    public void SplitRejectsWhatShlexRejects(string command, string message)
    {
        var exc = Assert.Throws<ArgumentException>(() => Shell.Split(command));
        Assert.Equal(message, exc.Message);
    }

    // Every expected string is repr(value) in Python 3.12; the exponent cases are where
    // .NET's own "R" spelling ("1E-05", "1.5E+20") differs from Python's.
    [Theory]
    [InlineData(1.0, "1.0")]
    [InlineData(0.05, "0.05")]
    [InlineData(2.5, "2.5")]
    [InlineData(12.333333333333334, "12.333333333333334")]
    [InlineData(0.0001, "0.0001")]
    [InlineData(100.0, "100.0")]
    [InlineData(123456789.123, "123456789.123")]
    [InlineData(-3.0, "-3.0")]
    [InlineData(1e-05, "1e-05")]
    [InlineData(5e-05, "5e-05")]
    [InlineData(9.999999999999999e-05, "9.999999999999999e-05")]
    [InlineData(0.00012345, "0.00012345")]
    [InlineData(1e16, "1e+16")]
    [InlineData(1.5e-07, "1.5e-07")]
    [InlineData(-2e+20, "-2e+20")]
    [InlineData(1e22, "1e+22")]
    [InlineData(0.0, "0.0")]
    [InlineData(double.NegativeZero, "-0.0")]
    [InlineData(1e15, "1000000000000000.0")]
    [InlineData(9999999999999998.0, "9999999999999998.0")]
    [InlineData(1234.5e10, "12345000000000.0")]
    [InlineData(5e-324, "5e-324")]
    [InlineData(double.MaxValue, "1.7976931348623157e+308")]
    [InlineData(0.30000000000000004, "0.30000000000000004")]
    [InlineData(2.0000000000000004, "2.0000000000000004")]
    [InlineData(double.PositiveInfinity, "inf")]
    [InlineData(double.NegativeInfinity, "-inf")]
    [InlineData(double.NaN, "nan")]
    public void PyMatchesPythonStrFloat(double value, string expected)
    {
        Assert.Equal(expected, Fmt.Py(value));
    }

    // Python rounds the exact binary value half to even: 2.675 is stored a hair below
    // 2.675, so it goes down; 0.125 is exact and sits on a tie, so it goes to the even
    // 0.12. Math.Round would answer 2.68 for the first.
    [Theory]
    [InlineData(2.675, 2, 2.67)]
    [InlineData(0.125, 2, 0.12)]
    [InlineData(0.375, 2, 0.38)]
    [InlineData(1.005, 2, 1.0)]
    [InlineData(2.5, 0, 2.0)]
    [InlineData(3.5, 0, 4.0)]
    [InlineData(-2.5, 0, -2.0)]
    [InlineData(-2.675, 2, -2.67)]
    [InlineData(1234.5678, 2, 1234.57)]
    [InlineData(1234.5678, -2, 1200.0)]
    [InlineData(0.0, 2, 0.0)]
    [InlineData(1e-9, 2, 0.0)]
    [InlineData(0.0025, 2, 0.0)]
    [InlineData(0.126, 2, 0.13)]
    [InlineData(10.126, 2, 10.13)]
    [InlineData(1.4, 2, 1.4)]
    public void PyRoundMatchesPythonRound(double value, int ndigits, double expected)
    {
        Assert.Equal(expected, Fmt.PyRound(value, ndigits));
    }

    [Fact]
    public void PyRoundKeepsTheSignOfAZeroAndPassesNonFiniteThrough()
    {
        Assert.True(double.IsNegative(Fmt.PyRound(-0.001, 2)));  // Python: -0.0
        Assert.True(double.IsNegative(Fmt.PyRound(double.NegativeZero, 2)));
        Assert.True(double.IsNaN(Fmt.PyRound(double.NaN, 2)));
        Assert.Equal(double.PositiveInfinity, Fmt.PyRound(double.PositiveInfinity, 2));
        Assert.Equal(double.NegativeInfinity, Fmt.PyRound(double.NegativeInfinity, 0));
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
