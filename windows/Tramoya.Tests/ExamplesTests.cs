using Tramoya.Core;
using Tramoya.Stage;

namespace Tramoya.Tests;

/// <summary>
/// Every example profile loads, every step parses and the plan describes itself, so a
/// broken example never reaches the README (port of <c>tests/test_examples.py</c>).
/// The profiles are read from the repository itself, found by walking up from the test
/// assembly to the folder that holds <c>pyproject.toml</c>.
/// </summary>
public sealed class ExamplesTests
{
    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "pyproject.toml"))) return dir.FullName;
        throw new DirectoryNotFoundException($"no pyproject.toml above {AppContext.BaseDirectory}");
    }

    private static List<string> ExamplePaths() =>
        Directory.GetDirectories(Path.Combine(RepoRoot(), "examples"))
            .Select(d => Path.Combine(d, "tramoya.toml"))
            .Where(File.Exists)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

    public static IEnumerable<object[]> Examples() => ExamplePaths().Select(p => new object[] { p });

    [Theory]
    [MemberData(nameof(Examples))]
    public void ExampleProfileLoadsAndEveryStepParses(string path)
    {
        var profile = ProfileFile.Load(path);
        Assert.True(profile.Scenes.Count > 0, $"{path} has no scenes");
        foreach (var scene in profile.Scenes)
        {
            foreach (var step in scene.Steps)
                Steps.Parse(step);
            foreach (var lang in profile.Languages)
                Assert.True(scene.Narration.TryGetValue(lang, out var text) && text.Length > 0,
                    $"{scene.Name} has no {lang} narration");
        }
        var plan = Director.Describe(profile);
        Assert.Contains(profile.Name, plan);
        Assert.Contains(profile.Scenes[0].Name, plan);
    }

    [Fact]
    public void ExamplesUseDistinctPortsSoTheyCanRunSideBySide()
    {
        var paths = ExamplePaths();
        Assert.NotEmpty(paths);
        var urls = paths.Select(p => ProfileFile.Load(p).Url).ToList();
        Assert.Equal(urls.Count, urls.Distinct().Count());
    }
}
