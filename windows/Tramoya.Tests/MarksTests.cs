using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Tramoya.Core;

namespace Tramoya.Tests;

/// <summary>Port of <c>tests/test_marks.py</c>.</summary>
public sealed class MarksTests
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "fixtures");

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tramoya-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>The Python tests feed the clock from an iterator; a queue does the same here.</summary>
    private static Func<double> Ticks(params double[] values)
    {
        var queue = new Queue<double>(values);
        return () => queue.Dequeue();
    }

    private static List<(double T, string Key, string Label)> OnDisk(string path)
    {
        var rows = Json.LoadNode(path)!.AsArray();
        var result = new List<(double, string, string)>();
        foreach (var row in rows)
        {
            var obj = row!.AsObject();
            Assert.Equal(new[] { "t", "key", "label" }, obj.Select(kv => kv.Key).ToArray());
            result.Add((obj["t"]!.GetValue<double>(), obj["key"]!.GetValue<string>(), obj["label"]!.GetValue<string>()));
        }
        return result;
    }

    [Fact]
    public void MarkIsFrozenDataclass()
    {
        var m = new Mark(1.0, "scene:intro:start", "hi");
        Assert.Equal(1.0, m.T);
        Assert.Equal("scene:intro:start", m.Key);
        Assert.Equal("hi", m.Label);
        // Frozen: every property is init-only, so `m.T = 2.0` does not even compile.
        foreach (var property in typeof(Mark).GetProperties())
            Assert.Contains(typeof(IsExternalInit), property.SetMethod!.ReturnParameter.GetRequiredCustomModifiers());
        Assert.Equal(new Mark(1.0, "scene:intro:start", "hi"), m);
    }

    [Fact]
    public void MarksAddRoundsAndAppends()
    {
        var marks = new Marks(t0: 10.0, clock: Ticks(10.126, 11.4));
        var m1 = marks.Add("scene:a:start", "A starts");
        var m2 = marks.Add("scene:a:end");
        Assert.Equal(0.13, m1.T);
        Assert.Equal(1.4, m2.T);
        Assert.Equal(new[] { m1, m2 }, marks.Rows);
    }

    [Fact]
    public void MarksIncrementalFlushWritesAfterEveryAdd()
    {
        var path = Path.Combine(TempDir(), "marks.json");
        var marks = new Marks(t0: 0.0, path: path, clock: Ticks(1.0, 2.0));

        marks.Add("scene:a:start", "A");
        Assert.Equal(new[] { (1.0, "scene:a:start", "A") }, OnDisk(path));

        marks.Add("scene:a:end", "A");
        Assert.Equal(new[] { (1.0, "scene:a:start", "A"), (2.0, "scene:a:end", "A") }, OnDisk(path));
    }

    [Fact]
    public void MarksWriteDumpsAllRows()
    {
        var path = Path.Combine(TempDir(), "out.json");
        var marks = new Marks(t0: 0.0, clock: Ticks(1.0));
        marks.Add("scene:a:start");
        marks.Write(path);
        Assert.Equal(new[] { (1.0, "scene:a:start", "") }, OnDisk(path));
    }

    [Fact]
    public void MarksSceneContextManagerEmitsStartAndEnd()
    {
        var marks = new Marks(t0: 0.0, clock: Ticks(0.0, 1.0, 2.0));
        using (marks.Scene("intro", "Intro label"))
        {
        }
        Assert.Equal(new[] { "scene:intro:start", "scene:intro:end" }, marks.Rows.Select(r => r.Key));
        Assert.All(marks.Rows, r => Assert.Equal("Intro label", r.Label));
    }

    [Fact]
    public void MarksSceneContextManagerEmitsEndOnException()
    {
        var marks = new Marks(t0: 0.0, clock: Ticks(0.0, 1.0, 2.0));
        Action scene = () =>
        {
            using (marks.Scene("intro"))
                throw new ArgumentException("boom");
        };
        var ex = Assert.Throws<ArgumentException>(scene);
        Assert.Contains("boom", ex.Message);
        Assert.Equal(new[] { "scene:intro:start", "scene:intro:end" }, marks.Rows.Select(r => r.Key));
    }

    [Fact]
    public void MarksWaitContextManager()
    {
        var marks = new Marks(t0: 0.0, clock: Ticks(0.0, 1.0, 2.0));
        using (marks.Wait("model call"))
        {
        }
        Assert.Equal(new[] { "wait:start", "wait:end" }, marks.Rows.Select(r => r.Key));
    }

    [Fact]
    public void SceneKeyBuildsCanonicalKey()
    {
        Assert.Equal("scene:intro:start", MarkKeys.SceneKey("intro", "start"));
        Assert.Equal("scene:intro:end", MarkKeys.SceneKey("intro", "end"));
    }

    [Theory]
    [InlineData("scene:intro:start", MarkKind.Scene, "intro", "start")]
    [InlineData("scene:intro:end", MarkKind.Scene, "intro", "end")]
    [InlineData("wait:start", MarkKind.Wait, null, "start")]
    [InlineData("wait:end", MarkKind.Wait, null, "end")]
    [InlineData("escena-apertura-inicio", MarkKind.Scene, "apertura", "start")]
    [InlineData("escena-apertura-fin", MarkKind.Scene, "apertura", "end")]
    [InlineData("espera-inicio", MarkKind.Wait, null, "start")]
    [InlineData("espera-fin", MarkKind.Wait, null, "end")]
    [InlineData("whatever-else", MarkKind.Other, "whatever-else", null)]
    public void ParseKey(string key, MarkKind kind, string? name, string? edge)
    {
        Assert.Equal((kind, name, edge), MarkKeys.ParseKey(key));
    }

    [Fact]
    public void LoadMarksNewShape()
    {
        var path = Path.Combine(TempDir(), "marks.json");
        File.WriteAllText(path, """[{"t": 1.0, "key": "scene:a:start", "label": "A"}]""");
        Assert.Equal(new[] { new Mark(1.0, "scene:a:start", "A") }, MarkKeys.LoadMarks(path));
    }

    [Fact]
    public void LoadMarksLegacyShape()
    {
        var path = Path.Combine(TempDir(), "marks.json");
        File.WriteAllText(path, """[{"t": 1.0, "clave": "escena-a-inicio", "etiqueta": "A"}]""");
        Assert.Equal(new[] { new Mark(1.0, "escena-a-inicio", "A") }, MarkKeys.LoadMarks(path));
    }

    [Fact]
    public void LoadMarksAppliesOffset()
    {
        var path = Path.Combine(TempDir(), "marks.json");
        File.WriteAllText(path, """[{"t": 1.0, "key": "scene:a:start", "label": ""}]""");
        Assert.Equal(6.0, MarkKeys.LoadMarks(path, offset: 5.0)[0].T);
    }

    [Fact]
    public void LoadMarksLegacyFixtureHasMatchedScenes()
    {
        var marks = MarkKeys.LoadMarks(Path.Combine(Fixtures, "legacy-marks-v11.json"));
        var bounds = MarkKeys.SceneBounds(marks);
        Assert.Equal(13, bounds.Count);
        Assert.Contains("apertura", bounds.Select(kv => kv.Key));
        foreach (var (_, (start, end)) in bounds)
            Assert.True(start < end);
    }

    [Fact]
    public void SceneBoundsPreservesFirstSeenOrder()
    {
        var marks = new[]
        {
            new Mark(0.0, "scene:b:start"),
            new Mark(1.0, "scene:a:start"),
            new Mark(2.0, "scene:b:end"),
            new Mark(3.0, "scene:a:end"),
        };
        Assert.Equal(new[] { "b", "a" }, MarkKeys.SceneBounds(marks).Select(kv => kv.Key));
    }

    [Fact]
    public void SceneBoundsNewAndLegacyKeysAgree()
    {
        var newMarks = new[] { new Mark(0.0, "scene:apertura:start"), new Mark(1.0, "scene:apertura:end") };
        var legacyMarks = new[] { new Mark(0.0, "escena-apertura-inicio"), new Mark(1.0, "escena-apertura-fin") };
        Assert.Equal(MarkKeys.SceneBounds(newMarks), MarkKeys.SceneBounds(legacyMarks));
        Assert.Equal(new[] { ("apertura", 0.0, 1.0) },
            MarkKeys.SceneBounds(newMarks).Select(kv => (kv.Key, kv.Value.Start, kv.Value.End)));
    }

    [Fact]
    public void WaitWindows()
    {
        var marks = new[]
        {
            new Mark(0.0, "wait:start"),
            new Mark(1.0, "wait:end"),
            new Mark(2.0, "scene:x:start"),
            new Mark(3.0, "wait:start"),
            new Mark(4.0, "wait:end"),
        };
        Assert.Equal(new[] { (0.0, 1.0), (3.0, 4.0) }, MarkKeys.WaitWindows(marks));
    }

    [Fact]
    public void WaitWindowsLegacyKeys()
    {
        var marks = new[] { new Mark(0.0, "espera-inicio"), new Mark(1.0, "espera-fin") };
        Assert.Equal(new[] { (0.0, 1.0) }, MarkKeys.WaitWindows(marks));
    }
}
