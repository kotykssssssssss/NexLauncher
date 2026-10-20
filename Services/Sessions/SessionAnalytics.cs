using System;
using System.Collections.Generic;
using System.Linq;
using NexLauncher.Models;

namespace NexLauncher.Services.Sessions;

public sealed record ChartPoint(string Label, double Value);
public sealed record StatsQuery(string? InstanceId = null, DateTimeOffset? From = null, DateTimeOffset? Until = null, string Bucket = "Daily");
public sealed record StatsSummary(double PlaytimeSeconds, int TotalLaunches, int SuccessfulLaunches, int FailedLaunches,
    int Crashes, double CrashRate, double AverageSeconds, double LongestSeconds, DateTimeOffset? LastPlayed,
    string MostPlayedInstance, string FavoriteVersion, string BusiestDay, int CrashFreeStreak,
    ChartPoint[] Playtime, ChartPoint[] Instances, ChartPoint[] Versions, ChartPoint[] Loaders, ChartPoint[] Durations, ChartPoint[] CrashHistory, int KnownOutcomes);
public static class SessionAnalytics
{
    public static GameSession[] Filter(IEnumerable<GameSession> sessions, StatsQuery query) => sessions.Where(s =>
        (query.InstanceId is null || s.InstanceId == query.InstanceId) &&
        (query.From is null || (s.EndedAt ?? s.LastObservedAt) >= query.From) &&
        (query.Until is null || (s.StartedAt ?? s.AttemptedAt) < query.Until)).OrderBy(s => s.AttemptedAt).ToArray();
    private static double Playtime(GameSession s, StatsQuery query)
    {
        if (s.StartedAt is null) return 0;
        var end = s.StartedAt.Value.AddSeconds(s.DurationSeconds);
        var start = s.StartedAt.Value;
        if (query.From is { } from && start < from) start = from;
        if (query.Until is { } until && end > until) end = until;
        return Math.Max(0, (end - start).TotalSeconds);
    }
    private static DateTime Bucket(DateTimeOffset time, string bucket)
    {
        // Calendar buckets use local dates, not UTC midnight; DST is handled by DateTimeOffset intervals.
        var date = time.ToLocalTime().Date;
        return bucket switch { "Monthly" => new DateTime(date.Year, date.Month, 1), "Weekly" => date.AddDays(-((int)date.DayOfWeek + 6) % 7), _ => date };
    }
    public static StatsSummary Calculate(IEnumerable<GameSession> source, StatsQuery query, IReadOnlyDictionary<string, string>? names = null)
    {
        var sessions = Filter(source, query);
        string Name(GameSession s) => names is not null && names.TryGetValue(s.InstanceId, out var current) ? current : s.InstanceName;
        ChartPoint[] Group(Func<GameSession, string> key) => sessions.GroupBy(key).Select(g => new ChartPoint(g.Key, g.Sum(s => Playtime(s, query)) / 3600))
            .Where(x => x.Value > 0).OrderByDescending(x => x.Value).ToArray();
        var byInstance = sessions.GroupBy(x => x.InstanceId).Select(g => new ChartPoint(Name(g.Last()), g.Sum(s => Playtime(s, query)) / 3600))
            .Where(x => x.Value > 0).OrderByDescending(x => x.Value).ToArray();
        var playtime = new SortedDictionary<DateTime, double>();
        var daily = new SortedDictionary<DateTime, double>();
        foreach (var session in sessions.Where(x => x.StartedAt is not null))
        {
            var start = session.StartedAt!.Value;
            var end = start.AddSeconds(session.DurationSeconds);
            if (query.From is { } from && start < from) start = from;
            if (query.Until is { } until && end > until) end = until;
            while (start < end)
            {
                var local = start.ToLocalTime(); var next = new DateTimeOffset(local.Date.AddDays(1));
                if (next > end) next = end;
                var hours = (next - start).TotalHours;
                var bucket = Bucket(start, query.Bucket);
                playtime[bucket] = playtime.GetValueOrDefault(bucket) + hours;
                daily[local.Date] = daily.GetValueOrDefault(local.Date) + hours;
                start = next;
            }
        }
        var completed = sessions.Count(x => x.Outcome == SessionOutcome.Completed);
        var crashes = sessions.Count(x => x.Outcome == SessionOutcome.Crashed);
        var durations = sessions.Where(x => x.StartedAt is not null && x.Outcome != SessionOutcome.Running).Select(x => Playtime(x, query)).ToArray();
        var streak = 0; var longest = 0;
        foreach (var session in sessions)
        { if (session.Outcome == SessionOutcome.Completed) { streak++; longest = Math.Max(streak, longest); } else streak = 0; }
        var versions = Group(s => s.MinecraftVersion);
        return new(sessions.Sum(s => Playtime(s, query)), sessions.Length, sessions.Count(x => x.StartedAt is not null), sessions.Count(x => x.Outcome == SessionOutcome.FailedToStart),
            crashes, completed + crashes == 0 ? 0 : (double)crashes / (completed + crashes) * 100,
            durations.Length == 0 ? 0 : durations.Average(), durations.Length == 0 ? 0 : durations.Max(),
            sessions.Where(x => x.StartedAt is not null).Select(x => x.StartedAt).Max(), byInstance.FirstOrDefault()?.Label ?? "—", versions.FirstOrDefault()?.Label ?? "—",
            daily.Count == 0 ? "—" : daily.MaxBy(x => x.Value).Key.ToString("dd.MM.yyyy"), longest,
            playtime.Select(x => new ChartPoint(x.Key.ToString("dd.MM.yy"), x.Value)).ToArray(), byInstance, versions, Group(s => s.Loader.ToString()),
            sessions.Where(x => x.StartedAt is not null).Select(x => new ChartPoint(x.StartedAt!.Value.ToLocalTime().ToString("dd.MM HH:mm"), Playtime(x, query) / 60)).ToArray(),
            sessions.Where(x => x.Outcome == SessionOutcome.Crashed).GroupBy(x => Bucket(x.EndedAt ?? x.LastObservedAt, query.Bucket)).OrderBy(g => g.Key)
                .Select(g => new ChartPoint(g.Key.ToString("dd.MM.yy"), g.Count())).ToArray(), completed + crashes);
    }
}
