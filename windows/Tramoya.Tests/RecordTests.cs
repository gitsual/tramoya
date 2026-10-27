using System.Text.Json.Nodes;
using Tramoya.Core;

namespace Tramoya.Tests;

/// <summary>
/// Port of <c>tests/test_record.py</c>. Only the sidecar lives in <c>Sidecar.cs</c>;
/// wf-recorder, <c>/proc</c> and the PulseAudio null sink are Linux-only and stay in
/// the Python package, so their tests are listed here as skipped to keep the inventory.
/// </summary>
public sealed class RecordTests
{
    private const string LinuxOnly = "wf-recorder is Linux only";

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tramoya-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact(Skip = LinuxOnly)]
    public void WfRecorderArgvWithoutAudio() { }

    [Fact(Skip = LinuxOnly)]
    public void WfRecorderArgvWithAudioAddsSinkAndCodec() { }

    [Fact(Skip = "/proc is Linux only")]
    public void DirectorStartFromProcReadsBootEpochPlusStarttime() { }

    [Fact]
    public void OffsetSidecarShape()
    {
        var sidecar = Sidecar.OffsetSidecar(recStart: 10.0, directorStart: 12.5);
        Assert.Equal(new Dictionary<string, double> { ["rec_start"] = 10.0, ["director_start"] = 12.5, ["offset"] = 2.5 }, sidecar);
    }

    [Fact]
    public void WriteAndReadSidecarRoundTrip()
    {
        var path = Path.Combine(TempDir(), "out.mp4.offset.json");
        var sidecar = Sidecar.OffsetSidecar(recStart: 10.0, directorStart: 8.0);
        Sidecar.WriteSidecar(path, sidecar);

        var text = File.ReadAllText(path);
        var onDisk = JsonNode.Parse(text)!.AsObject()
            .ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<double>());
        Assert.Equal(sidecar, onDisk);
        Assert.Equal(-2.0, Sidecar.ReadSidecar(path));

        // json.dumps(indent=2) + "\n": two-space indent and a trailing newline.
        Assert.StartsWith("{\n  \"rec_start\":", text);
        Assert.EndsWith("}\n", text);
    }

    [Fact(Skip = "PulseAudio is Linux only")]
    public void NullSinkLoadsAndUnloadsViaInjectedRunner() { }

    [Fact(Skip = "PulseAudio is Linux only")]
    public void NullSinkSkipsUnloadWhenLoadNeverRan() { }

    [Fact(Skip = LinuxOnly)]
    public void RecorderStartRecordsRecStartAndLaunchesViaRunner() { }

    [Fact(Skip = LinuxOnly)]
    public void RecorderStopSendsSigintAndWaits() { }

    [Fact(Skip = LinuxOnly)]
    public void RecorderAsContextManagerStartsAndStops() { }

    [Fact(Skip = LinuxOnly)]
    public void RecorderStopWithoutStartIsANoOp() { }
}
