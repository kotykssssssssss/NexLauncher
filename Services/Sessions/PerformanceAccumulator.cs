using System;
using System.Linq;
using NexLauncher.Models;

namespace NexLauncher.Services.Sessions;

public sealed class PerformanceAccumulator(GameSession session)
{
    public const int MaximumSamples = 360;
    private double lastCpuSeconds;
    private double lastSeconds;
    public void Add(double elapsed, long ram, double totalCpuSeconds, int processors)
    {
        if (!double.IsFinite(elapsed) || elapsed < lastSeconds || ram < 0 || !double.IsFinite(totalCpuSeconds) || processors < 1) return;
        double? cpu = elapsed > lastSeconds && lastSeconds > 0
            ? Math.Clamp((totalCpuSeconds - lastCpuSeconds) / (elapsed - lastSeconds) / processors * 100, 0, 100) : null;
        lastCpuSeconds = totalCpuSeconds; lastSeconds = elapsed;
        session.SampleCount++;
        session.AverageRamBytes += (ram - session.AverageRamBytes) / session.SampleCount;
        session.PeakRamBytes = Math.Max(session.PeakRamBytes, ram);
        if (cpu is { } value) { session.CpuSampleCount++; session.AverageCpuPercent += (value - session.AverageCpuPercent) / session.CpuSampleCount; }
        // Compact older samples while retaining coverage of the entire session; summaries use every sample.
        if (session.Samples.Count >= MaximumSamples)
            session.Samples = session.Samples.Where((_, i) => i % 2 == 0).ToList();
        session.Samples.Add(new PerformanceSample(elapsed, ram, cpu));
    }
}
