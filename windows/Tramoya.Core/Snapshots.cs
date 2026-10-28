using System.Text;

namespace Tramoya.Core;

/// <summary>
/// Accessibility snapshots: one text file per scene, written next to the take, so a
/// demo that silently broke (a button moved, a label changed) fails a diff in CI
/// instead of shipping (port of <c>tramoya/snapshots.py</c>).
/// </summary>
/// <remarks>
/// The snapshot is Playwright's ARIA snapshot of the page body: roles and names, no
/// pixels, so it is stable across themes and fonts and reads as a diff a person can
/// act on. Files are written with <c>\n</c> endings so both implementations produce
/// the same bytes; on read, <c>\r\n</c> is folded to <c>\n</c> like Python's text mode.
/// </remarks>
public static class Snapshots
{
    private static readonly UTF8Encoding Utf8 = new(false);

    public static string WriteSnapshot(string directory, string scene, string text)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, scene + ".txt");
        File.WriteAllText(path, text.EndsWith('\n') ? text : text + "\n", Utf8);
        return path;
    }

    /// <summary>Scene name to snapshot text, sorted by file name; empty when the directory is missing.</summary>
    public static SortedDictionary<string, string> ReadSnapshots(string directory)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(directory)) return result;
        foreach (var path in Directory.GetFiles(directory, "*.txt"))
        {
            // The Win32 pattern also matches longer extensions; the Python glob does not.
            if (!Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase)) continue;
            result[Path.GetFileNameWithoutExtension(path)] = File.ReadAllText(path, Utf8).Replace("\r\n", "\n");
        }
        return result;
    }

    /// <summary>Human-readable problems, one per scene that differs; empty when equal.</summary>
    public static List<string> DiffSnapshots(string expectedDir, string actualDir, int context = 2)
    {
        var expected = ReadSnapshots(expectedDir);
        var actual = ReadSnapshots(actualDir);
        var problems = new List<string>();
        var scenes = new SortedSet<string>(expected.Keys, StringComparer.Ordinal);
        scenes.UnionWith(actual.Keys);
        foreach (var scene in scenes)
        {
            if (!actual.ContainsKey(scene))
                problems.Add($"{scene}: missing from {actualDir} (the scene did not run)");
            else if (!expected.ContainsKey(scene))
                problems.Add($"{scene}: new scene, not in {expectedDir}");
            else if (expected[scene] != actual[scene])
            {
                var diff = UnifiedDiff(SplitLines(expected[scene]), SplitLines(actual[scene]),
                    $"expected/{scene}", $"actual/{scene}", context);
                problems.Add($"{scene}: changed\n" + string.Join("\n", diff));
            }
        }
        return problems;
    }

    /// <summary>Python's <c>str.splitlines()</c>: every line boundary it knows, trailing empty line dropped.</summary>
    internal static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var isBreak = c is '\n' or '\r' or '\v' or '\f' or '\x1c' or '\x1d' or '\x1e' or '\x85' or (char)0x2028 or (char)0x2029;
            if (!isBreak) continue;
            lines.Add(text[start..i]);
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            start = i + 1;
        }
        if (start < text.Length) lines.Add(text[start..]);
        return lines;
    }

    /// <summary>
    /// <c>difflib.unified_diff(a, b, fromfile, tofile, lineterm="", n=context)</c>, line
    /// for line: same matching blocks, same hunk grouping, same <c>@@</c> headers. Python's
    /// CI output and this one must be interchangeable, so the whole algorithm is ported
    /// rather than approximated, including the popular-element heuristic for large inputs.
    /// </summary>
    public static List<string> UnifiedDiff(IReadOnlyList<string> a, IReadOnlyList<string> b,
        string fromFile, string toFile, int context = 3)
    {
        var output = new List<string>();
        var started = false;
        foreach (var group in GroupedOpcodes(a, b, context))
        {
            if (!started)
            {
                started = true;
                output.Add($"--- {fromFile}");
                output.Add($"+++ {toFile}");
            }
            var first = group[0];
            var last = group[^1];
            output.Add($"@@ -{FormatRange(first.I1, last.I2)} +{FormatRange(first.J1, last.J2)} @@");
            foreach (var op in group)
            {
                if (op.Tag == Tag.Equal)
                {
                    for (var i = op.I1; i < op.I2; i++) output.Add(" " + a[i]);
                    continue;
                }
                if (op.Tag is Tag.Replace or Tag.Delete)
                    for (var i = op.I1; i < op.I2; i++) output.Add("-" + a[i]);
                if (op.Tag is Tag.Replace or Tag.Insert)
                    for (var j = op.J1; j < op.J2; j++) output.Add("+" + b[j]);
            }
        }
        return output;
    }

    /// <summary>difflib's <c>_format_range_unified</c>: 1-based start, count omitted when it is 1.</summary>
    private static string FormatRange(int start, int stop)
    {
        var beginning = start + 1;
        var length = stop - start;
        if (length == 1) return beginning.ToString();
        if (length == 0) beginning--;
        return $"{beginning},{length}";
    }

    private enum Tag { Equal, Replace, Delete, Insert }

    private readonly record struct Opcode(Tag Tag, int I1, int I2, int J1, int J2);

    private readonly record struct Block(int A, int B, int Size);

    /// <summary><c>SequenceMatcher.find_longest_match</c> without junk: longest block, earliest in a, then in b.</summary>
    private static Block FindLongestMatch(IReadOnlyList<string> a, IReadOnlyList<string> b,
        Dictionary<string, List<int>> b2j, int alo, int ahi, int blo, int bhi)
    {
        int besti = alo, bestj = blo, bestsize = 0;
        var j2len = new Dictionary<int, int>();
        for (var i = alo; i < ahi; i++)
        {
            var newj2len = new Dictionary<int, int>();
            if (b2j.TryGetValue(a[i], out var js))
            {
                foreach (var j in js)
                {
                    if (j < blo) continue;
                    if (j >= bhi) break;
                    var k = (j2len.TryGetValue(j - 1, out var prev) ? prev : 0) + 1;
                    newj2len[j] = k;
                    if (k > bestsize)
                    {
                        besti = i - k + 1;
                        bestj = j - k + 1;
                        bestsize = k;
                    }
                }
            }
            j2len = newj2len;
        }
        while (besti > alo && bestj > blo && a[besti - 1] == b[bestj - 1])
        {
            besti--; bestj--; bestsize++;
        }
        while (besti + bestsize < ahi && bestj + bestsize < bhi && a[besti + bestsize] == b[bestj + bestsize])
            bestsize++;
        return new Block(besti, bestj, bestsize);
    }

    /// <summary><c>SequenceMatcher.get_matching_blocks</c>: recursive split around the longest match, adjacent blocks merged.</summary>
    private static List<Block> MatchingBlocks(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var b2j = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var j = 0; j < b.Count; j++)
        {
            if (!b2j.TryGetValue(b[j], out var list)) b2j[b[j]] = list = new List<int>();
            list.Add(j);
        }
        // __chain_b has no explicit junk when isjunk is None. Popular lines
        // cannot seed matches, but remain eligible for extension on either side.
        if (b.Count >= 200)
        {
            var threshold = b.Count / 100 + 1;
            var popular = b2j.Where(pair => pair.Value.Count > threshold).Select(pair => pair.Key).ToList();
            foreach (var line in popular) b2j.Remove(line);
        }
        var queue = new Stack<(int, int, int, int)>();
        queue.Push((0, a.Count, 0, b.Count));
        var blocks = new List<Block>();
        while (queue.Count > 0)
        {
            var (alo, ahi, blo, bhi) = queue.Pop();
            var m = FindLongestMatch(a, b, b2j, alo, ahi, blo, bhi);
            if (m.Size == 0) continue;
            blocks.Add(m);
            if (alo < m.A && blo < m.B) queue.Push((alo, m.A, blo, m.B));
            if (m.A + m.Size < ahi && m.B + m.Size < bhi) queue.Push((m.A + m.Size, ahi, m.B + m.Size, bhi));
        }
        blocks.Sort((x, y) => x.A != y.A ? x.A.CompareTo(y.A) : x.B != y.B ? x.B.CompareTo(y.B) : x.Size.CompareTo(y.Size));
        var merged = new List<Block>();
        int i1 = 0, j1 = 0, k1 = 0;
        foreach (var (i2, j2, k2) in blocks)
        {
            if (i1 + k1 == i2 && j1 + k1 == j2)
            {
                k1 += k2;
            }
            else
            {
                if (k1 > 0) merged.Add(new Block(i1, j1, k1));
                (i1, j1, k1) = (i2, j2, k2);
            }
        }
        if (k1 > 0) merged.Add(new Block(i1, j1, k1));
        merged.Add(new Block(a.Count, b.Count, 0));
        return merged;
    }

    /// <summary><c>SequenceMatcher.get_opcodes</c>.</summary>
    private static List<Opcode> Opcodes(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var ops = new List<Opcode>();
        int i = 0, j = 0;
        foreach (var (ai, bj, size) in MatchingBlocks(a, b))
        {
            Tag? tag = null;
            if (i < ai && j < bj) tag = Tag.Replace;
            else if (i < ai) tag = Tag.Delete;
            else if (j < bj) tag = Tag.Insert;
            if (tag is not null) ops.Add(new Opcode(tag.Value, i, ai, j, bj));
            i = ai + size;
            j = bj + size;
            if (size > 0) ops.Add(new Opcode(Tag.Equal, ai, i, bj, j));
        }
        return ops;
    }

    /// <summary><c>SequenceMatcher.get_grouped_opcodes(n)</c>: hunks of changes with up to n lines of context.</summary>
    private static List<List<Opcode>> GroupedOpcodes(IReadOnlyList<string> a, IReadOnlyList<string> b, int n)
    {
        var codes = Opcodes(a, b);
        if (codes.Count == 0) codes.Add(new Opcode(Tag.Equal, 0, 1, 0, 1));
        if (codes[0].Tag == Tag.Equal)
        {
            var c = codes[0];
            codes[0] = c with { I1 = Math.Max(c.I1, c.I2 - n), J1 = Math.Max(c.J1, c.J2 - n) };
        }
        if (codes[^1].Tag == Tag.Equal)
        {
            var c = codes[^1];
            codes[^1] = c with { I2 = Math.Min(c.I2, c.I1 + n), J2 = Math.Min(c.J2, c.J1 + n) };
        }
        var nn = n + n;
        var groups = new List<List<Opcode>>();
        var group = new List<Opcode>();
        foreach (var code in codes)
        {
            var (tag, i1, i2, j1, j2) = code;
            if (tag == Tag.Equal && i2 - i1 > nn)
            {
                group.Add(new Opcode(tag, i1, Math.Min(i2, i1 + n), j1, Math.Min(j2, j1 + n)));
                groups.Add(group);
                group = new List<Opcode>();
                i1 = Math.Max(i1, i2 - n);
                j1 = Math.Max(j1, j2 - n);
            }
            group.Add(new Opcode(tag, i1, i2, j1, j2));
        }
        if (group.Count > 0 && !(group.Count == 1 && group[0].Tag == Tag.Equal))
            groups.Add(group);
        return groups;
    }
}
