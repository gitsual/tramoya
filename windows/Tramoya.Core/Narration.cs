using System.Collections;
using System.Diagnostics;
using System.Text;

namespace Tramoya.Core;

/// <summary>A narration script keyed by moment: <c>key -> {lang: text}</c> (port of <c>narration.Script</c>).</summary>
public sealed class Script : IReadOnlyDictionary<string, Dictionary<string, string>>
{
    private readonly Dictionary<string, Dictionary<string, string>> _table;

    public Script(IDictionary<string, Dictionary<string, string>> table)
    {
        _table = new Dictionary<string, Dictionary<string, string>>(table);
    }

    public string Text(string key, string lang) => _table[key][lang];

    public Dictionary<string, string> this[string key] => _table[key];
    public int Count => _table.Count;
    public IEnumerable<string> Keys => _table.Keys;
    public IEnumerable<Dictionary<string, string>> Values => _table.Values;
    public bool ContainsKey(string key) => _table.ContainsKey(key);
    public bool TryGetValue(string key, out Dictionary<string, string> value) => _table.TryGetValue(key, out value!);
    public IEnumerator<KeyValuePair<string, Dictionary<string, string>>> GetEnumerator() => _table.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>A per-page narration script: <c>lang -> [(page, text), ...]</c>.</summary>
public sealed class DeckScript
{
    private readonly Dictionary<string, List<(string Page, string Text)>> _table;

    public DeckScript(IDictionary<string, List<(string Page, string Text)>> table)
    {
        _table = table.ToDictionary(kv => kv.Key, kv => new List<(string, string)>(kv.Value));
    }

    public List<string> Pages(string lang) =>
        _table.TryGetValue(lang, out var pages) ? pages.Select(p => p.Page).ToList() : new List<string>();

    public string Text(string page, string lang)
    {
        if (_table.TryGetValue(lang, out var pages))
            foreach (var (candidate, text) in pages)
                if (candidate == page) return text;
        throw new KeyNotFoundException(page);
    }
}

/// <summary>Something that is playing a clip and can be waited on (what Python's <c>Popen.wait()</c> gave the narrator).</summary>
public interface IClipPlayback
{
    Task WaitAsync();
}

/// <summary>Voice clip measurement (port of <c>narration.wav_seconds</c>).</summary>
public static class Narration
{
    /// <summary>
    /// Duration of a WAV file in seconds, or 0.0 if it is missing or unreadable. Reads the
    /// RIFF header by hand with the same tolerance as Python's <c>wave</c> module: PCM
    /// only (plain or WAVE_FORMAT_EXTENSIBLE with a PCM sub-format), the <c>fmt </c> chunk
    /// must come before <c>data</c>, and the frame count comes from the data chunk's
    /// declared size, not from the bytes actually on disk.
    /// </summary>
    public static double WavSeconds(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var r = new BinaryReader(fs);
            if (Tag(r) != "RIFF") return 0.0;
            r.ReadUInt32(); // RIFF size: ignored, as Python's Chunk ignores it
            if (Tag(r) != "WAVE") return 0.0;

            var fmtRead = false;
            var channels = 0;
            var sampleWidth = 0;
            var rate = 0u;
            while (fs.Position + 8 <= fs.Length)
            {
                var id = Tag(r);
                var size = r.ReadUInt32();
                var body = fs.Position;
                if (id == "fmt ")
                {
                    if (size < 16) return 0.0;
                    var format = r.ReadUInt16();
                    channels = r.ReadUInt16();
                    rate = r.ReadUInt32();
                    r.ReadUInt32(); // byte rate
                    r.ReadUInt16(); // block align
                    var bits = r.ReadUInt16();
                    if (format == 0xFFFE)
                    {
                        // WAVE_FORMAT_EXTENSIBLE: the real format is the first word of the sub-format GUID.
                        if (size < 40) return 0.0;
                        r.ReadUInt16(); // cbSize
                        r.ReadUInt16(); // valid bits
                        r.ReadUInt32(); // channel mask
                        if (r.ReadUInt16() != 1) return 0.0;
                    }
                    else if (format != 1)
                    {
                        return 0.0;
                    }
                    sampleWidth = (bits + 7) / 8;
                    if (sampleWidth == 0 || channels == 0) return 0.0;
                    fmtRead = true;
                }
                else if (id == "data")
                {
                    if (!fmtRead) return 0.0;
                    var frames = size / (uint)(channels * sampleWidth);
                    return frames / (double)(rate == 0 ? 1 : rate);
                }
                // Chunks are padded to an even length.
                var next = body + size + (size & 1);
                if (next > fs.Length) return 0.0;
                fs.Position = next;
            }
            return 0.0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return 0.0;
        }
    }

    private static string Tag(BinaryReader r)
    {
        var bytes = r.ReadBytes(4);
        if (bytes.Length < 4) throw new EndOfStreamException();
        return Encoding.ASCII.GetString(bytes);
    }
}

/// <summary>
/// Plays a voice clip for a narration key, or falls back to a paced sleep (port of
/// <c>narration.Narrator</c>). The player is injectable so tests never make a sound.
/// </summary>
public sealed class Narrator
{
    private readonly Func<string, IClipPlayback> _player;
    private IClipPlayback? _pending;

    public string VoiceDir { get; }
    public Pacing Pacing { get; }

    public Narrator(string voiceDir, Pacing pacing, Func<string, IClipPlayback>? player = null)
    {
        VoiceDir = voiceDir;
        Pacing = pacing;
        _player = player ?? (path => new PowerShellPlayback(path));
    }

    /// <summary>The clip for <paramref name="key"/> in the current language, or null when there is none.</summary>
    public string? Clip(string key)
    {
        var path = Path.Combine(VoiceDir, Pacing.Lang, key + ".wav");
        return File.Exists(path) ? path : null;
    }

    /// <summary>Block on a clip started with <c>wait: false</c>, if any.</summary>
    public async Task WaitPendingAsync()
    {
        if (_pending is not null)
        {
            await _pending.WaitAsync().ConfigureAwait(false);
            _pending = null;
        }
    }

    /// <summary>
    /// Play the clip for <paramref name="key"/> and return its length in seconds; without a
    /// clip, sleep <paramref name="fallback"/> through the pacing instead. A rehearsal never
    /// waits for the clip, whatever <paramref name="wait"/> says.
    /// </summary>
    public async Task<double> SayAsync(string key, double fallback = 0.0, bool wait = true)
    {
        await WaitPendingAsync().ConfigureAwait(false);
        var clip = Clip(key);
        if (clip is null)
        {
            await Pacing.SleepAsync(fallback).ConfigureAwait(false);
            return fallback;
        }
        var seconds = Narration.WavSeconds(clip);
        var playback = _player(clip);
        if (wait && !Pacing.Rehearse)
            await playback.WaitAsync().ConfigureAwait(false);
        else
            _pending = playback;
        return seconds;
    }

    /// <summary>
    /// The default player: a PowerShell child playing the wav synchronously, which is what
    /// <c>paplay</c> was on Linux. A separate process keeps audio out of this library and
    /// lets the narrator keep going while the clip plays.
    /// </summary>
    private sealed class PowerShellPlayback : IClipPlayback
    {
        private readonly Process _process;

        public PowerShellPlayback(string path)
        {
            var script = $"(New-Object Media.SoundPlayer '{path.Replace("'", "''")}').PlaySync()";
            var psi = new ProcessStartInfo("powershell")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-Command", script })
                psi.ArgumentList.Add(a);
            _process = Process.Start(psi) ?? throw new InvalidOperationException("could not start powershell");
        }

        public async Task WaitAsync()
        {
            await _process.WaitForExitAsync().ConfigureAwait(false);
            _process.Dispose();
        }
    }
}
