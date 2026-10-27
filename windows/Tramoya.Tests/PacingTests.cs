using Tramoya.Core;

namespace Tramoya.Tests;

/// <summary>Port of <c>tests/test_pacing.py</c>.</summary>
public sealed class PacingTests
{
    private static Func<double, Task> RecordingSleep(List<double> calls) => seconds =>
    {
        calls.Add(seconds);
        return Task.CompletedTask;
    };

    [Fact]
    public async Task SleepScalesByPace()
    {
        var calls = new List<double>();
        var pacing = new Pacing(pace: 2.0, sleep: RecordingSleep(calls));
        var slept = await pacing.SleepAsync(3.0);
        Assert.Equal(6.0, slept);
        Assert.Equal(new[] { 6.0 }, calls);
    }

    [Fact]
    public async Task SleepAccumulatesUnscaledTotalBase()
    {
        var calls = new List<double>();
        var pacing = new Pacing(pace: 2.0, sleep: RecordingSleep(calls));
        await pacing.SleepAsync(3.0);
        await pacing.SleepAsync(1.5);
        Assert.Equal(4.5, pacing.TotalBase);
    }

    [Fact]
    public async Task SleepRehearsalCompressesWait()
    {
        var calls = new List<double>();
        var pacing = new Pacing(pace: 1.0, rehearse: true, sleep: RecordingSleep(calls));
        var slept = await pacing.SleepAsync(10.0);
        Assert.Equal(0.05, slept);
        Assert.Equal(new[] { 0.05 }, calls);
    }

    [Fact]
    public async Task SleepRehearsalCapsBaseAtOneSecond()
    {
        var calls = new List<double>();
        var pacing = new Pacing(pace: 1.0, rehearse: true, sleep: RecordingSleep(calls));
        var slept = await pacing.SleepAsync(0.4);
        Assert.Equal(0.4 * 0.05, slept);
        Assert.Equal(new[] { 0.4 * 0.05 }, calls);
    }

    [Fact]
    public async Task SleepRehearsalStillAccumulatesRealBase()
    {
        var pacing = new Pacing(pace: 1.0, rehearse: true, sleep: _ => Task.CompletedTask);
        await pacing.SleepAsync(10.0);
        Assert.Equal(10.0, pacing.TotalBase);
    }

    [Fact]
    public void OpTimeoutDefaultMinimum()
    {
        Assert.Equal(20.0, new Pacing(pace: 1.0).OpTimeout());
    }

    [Fact]
    public void OpTimeoutScalesWithPace()
    {
        Assert.Equal(48.0, new Pacing(pace: 4.0).OpTimeout());
    }

    [Fact]
    public void OpTimeoutInRehearsalIsShortButNeverBelowMinimum()
    {
        Assert.Equal(20.0, new Pacing(pace: 4.0, rehearse: true).OpTimeout());
        Assert.Equal(5.0, new Pacing(pace: 4.0, rehearse: true).OpTimeout(minimum: 2.0));
    }

    [Fact]
    public void OpTimeoutRespectsCustomMinimum()
    {
        Assert.Equal(50.0, new Pacing(pace: 1.0).OpTimeout(minimum: 50.0));
    }
}
