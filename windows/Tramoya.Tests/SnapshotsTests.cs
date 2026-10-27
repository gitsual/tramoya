using Tramoya.Core;

namespace Tramoya.Tests;

/// <summary>
/// Tests for <c>Snapshots</c>. The Python package has no <c>test_snapshots.py</c>; the
/// scene-level case is the one in <c>tests/test_director.py</c>, and the diff cases are
/// checked against what <c>difflib.unified_diff</c> prints for the same input.
/// </summary>
public sealed class SnapshotsTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tramoya-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void WriteSnapshotCreatesTheDirectoryAndEndsWithNewline()
    {
        var dir = Path.Combine(TempDir(), "snapshots");
        var path = Snapshots.WriteSnapshot(dir, "welcome", "- heading Notes");
        Assert.Equal(Path.Combine(dir, "welcome.txt"), path);
        Assert.Equal("- heading Notes\n", File.ReadAllText(path));

        Snapshots.WriteSnapshot(dir, "search", "- textbox\n");
        Assert.Equal("- textbox\n", File.ReadAllText(Path.Combine(dir, "search.txt")));
    }

    [Fact]
    public void ReadSnapshotsIsEmptyForAMissingDirectory()
    {
        Assert.Empty(Snapshots.ReadSnapshots(Path.Combine(TempDir(), "nope")));
    }

    [Fact]
    public void ReadSnapshotsSortsByFileNameAndIgnoresOtherFiles()
    {
        var dir = TempDir();
        Snapshots.WriteSnapshot(dir, "b", "B");
        Snapshots.WriteSnapshot(dir, "a", "A");
        Snapshots.WriteSnapshot(dir, "Z", "Z");
        File.WriteAllText(Path.Combine(dir, "notes.md"), "not a snapshot");
        File.WriteAllText(Path.Combine(dir, "other.txtx"), "not a snapshot either");
        var read = Snapshots.ReadSnapshots(dir);
        Assert.Equal(new[] { "Z", "a", "b" }, read.Keys);
        Assert.Equal(new[] { "Z\n", "A\n", "B\n" }, read.Values);
    }

    [Fact]
    public void ReadSnapshotsFoldsCrlfLikePythonTextMode()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "welcome.txt"), "- a\r\n- b\r\n");
        Assert.Equal("- a\n- b\n", Snapshots.ReadSnapshots(dir)["welcome"]);
    }

    [Fact]
    public void DiffSnapshotsReportsChangedMissingAndNewScenes()
    {
        var root = TempDir();
        var a = Path.Combine(root, "a");
        var b = Path.Combine(root, "b");
        Snapshots.WriteSnapshot(a, "welcome", "- heading \"Notes\"\n- button \"New note\"\n");
        Snapshots.WriteSnapshot(a, "search", "- textbox\n");
        Snapshots.WriteSnapshot(a, "gone", "- x\n");
        Snapshots.WriteSnapshot(b, "welcome", "- heading \"Notes\"\n- button \"New\"\n");
        Snapshots.WriteSnapshot(b, "search", "- textbox\n");
        Snapshots.WriteSnapshot(b, "extra", "- y\n");
        var problems = Snapshots.DiffSnapshots(a, b);
        Assert.Contains(problems, p => p.Contains("welcome") && p.Contains("New note"));
        Assert.Contains(problems, p => p.Contains("gone") && p.Contains("missing"));
        Assert.Contains(problems, p => p.Contains("extra") && p.Contains("new"));
        Assert.DoesNotContain(problems, p => p.Contains("search"));
        Assert.Empty(Snapshots.DiffSnapshots(a, a));

        // Same wording and order (scenes sorted) as the Python CLI prints.
        Assert.Equal(new[]
        {
            $"extra: new scene, not in {a}",
            $"gone: missing from {b} (the scene did not run)",
            "welcome: changed\n" +
            "--- expected/welcome\n" +
            "+++ actual/welcome\n" +
            "@@ -1,2 +1,2 @@\n" +
            " - heading \"Notes\"\n" +
            "-- button \"New note\"\n" +
            "+- button \"New\"",
        }, problems);
    }

    [Fact]
    public void DiffSnapshotsHonoursTheContextArgument()
    {
        var root = TempDir();
        var a = Path.Combine(root, "a");
        var b = Path.Combine(root, "b");
        var lines = Enumerable.Range(0, 10).Select(i => $"line {i}").ToList();
        Snapshots.WriteSnapshot(a, "s", string.Join("\n", lines));
        lines[5] = "changed";
        Snapshots.WriteSnapshot(b, "s", string.Join("\n", lines));
        var problem = Assert.Single(Snapshots.DiffSnapshots(a, b, context: 0));
        Assert.Equal("s: changed\n--- expected/s\n+++ actual/s\n@@ -6 +6 @@\n-line 5\n+changed", problem);
    }

    // Each expected string is the literal output of
    // difflib.unified_diff(a, b, fromfile="expected/s", tofile="actual/s", lineterm="", n=n).
    [Theory]
    [InlineData("x", "x|y|z", 2, "--- expected/s\n+++ actual/s\n@@ -1 +1,3 @@\n x\n+y\n+z")]
    [InlineData("", "y", 2, "--- expected/s\n+++ actual/s\n@@ -0,0 +1 @@\n+y")]
    [InlineData("a|b", "", 2, "--- expected/s\n+++ actual/s\n@@ -1,2 +0,0 @@\n-a\n-b")]
    [InlineData("a|b|c", "c|a|b", 2, "--- expected/s\n+++ actual/s\n@@ -1,3 +1,3 @@\n+c\n a\n b\n-c")]
    [InlineData("a", "a", 2, "")]
    [InlineData("", "", 2, "")]
    [InlineData("k|a|b|c|d|e|f|g|h|k", "k|a|B|c|d|e|f|G|h|k", 1,
        "--- expected/s\n+++ actual/s\n@@ -2,3 +2,3 @@\n a\n-b\n+B\n c\n@@ -7,3 +7,3 @@\n f\n-g\n+G\n h")]
    public void UnifiedDiffMatchesDifflib(string a, string b, int context, string expected)
    {
        var left = a.Length == 0 ? Array.Empty<string>() : a.Split('|');
        var right = b.Length == 0 ? Array.Empty<string>() : b.Split('|');
        Assert.Equal(expected, string.Join("\n", Snapshots.UnifiedDiff(left, right, "expected/s", "actual/s", context)));
    }

    [Fact]
    public void UnifiedDiffSplitsFarApartChangesIntoHunks()
    {
        var a = Enumerable.Range(0, 20).Select(i => $"line {i}").ToList();
        var b = new List<string>(a);
        b[3] = "changed 3";
        b.Insert(12, "inserted");
        b.RemoveAt(17);
        var expected =
            "--- expected/s\n+++ actual/s\n" +
            "@@ -2,5 +2,5 @@\n line 1\n line 2\n-line 3\n+changed 3\n line 4\n line 5\n" +
            "@@ -11,9 +11,9 @@\n line 10\n line 11\n+inserted\n line 12\n line 13\n line 14\n line 15\n-line 16\n line 17\n line 18";
        Assert.Equal(expected, string.Join("\n", Snapshots.UnifiedDiff(a, b, "expected/s", "actual/s", 2)));
    }

    [Fact]
    public void SplitLinesMatchesPython()
    {
        Assert.Equal(new[] { "a", "b", "c", "d" }, Snapshots.SplitLines("a\r\nb\rc\fd\n"));
        Assert.Equal(new[] { "a", "", "b" }, Snapshots.SplitLines("a\n\nb"));
        Assert.Empty(Snapshots.SplitLines(""));
    }
}
