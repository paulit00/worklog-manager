using System.Diagnostics;
using System.Text.Json;
using WorklogDesk.Core;

namespace WorklogDesk.Windows;

internal static class DemoData
{
    public static void Populate(WorkStore store, DateOnly day, TimeZoneInfo zone)
    {
        var morning = Timeline.DayStart(day, zone).AddHours(9);
        var north = new TaskLabel("Northwind", "NW-243", "Review Business Central deployment");
        var reply = new TaskLabel("Cedar", "CED-118", "Reply to customer with restore details");
        var internalTask = new TaskLabel("Internal", "", "Review on-call handoff");
        var spans = new[]
        {
            Span(0, 12, "VS Code", north), Span(12, 17, "Browser", north),
            Span(17, 46, "Remote Desktop", north), Span(50, 57, "Remote Desktop"),
            Span(58, 63, "Outlook", reply), Span(65, 92, "Terminal / AI", north),
            Span(92, 104, "Idle gap", kind: SpanKind.Idle), Span(104, 128, "Manual timer", reply, SpanKind.Manual),
            Span(128, 128.125, "Browser"), Span(130, 142, "Teams", internalTask),
            Span(147, 162, "VS Code"), Span(165, 175, "Browser")
        };
        store.SaveCapture(spans, [], zone);
        var internalRow = store.Rows(day).Single(row => row.Task == internalTask);
        store.ReplaceRows([], [internalRow with { Status = Disposition.Internal }]);
        store.SaveRequest(new RequestDraft(Guid.NewGuid().ToString("N"), day,
            new TaskLabel("Cedar", "", "Check whether Cedar's sandbox can be refreshed before Thursday",
                "https://teams.microsoft.com/")));
        store.SaveRequest(new RequestDraft(Guid.NewGuid().ToString("N"), day, reply));

        WorkSpan Span(double start, double end, string source, TaskLabel? task = null, SpanKind kind = SpanKind.Foreground) =>
            new(Guid.NewGuid(), morning.AddMinutes(start), morning.AddMinutes(end), new CaptureSource(source, task, kind));
    }
}

internal static class Benchmark
{
    public static void CaptureView(string folder, string path)
    {
        using var app = new TrayApplication(folder, demo: true, "none", showOnStart: false, diagnostics: true);
        app.RenderDemo(path);
        app.ShutDownDiagnostics();
    }

    public static void Run(string[] args)
    {
        if (args.Length < 2)
            return;
        var report = Path.GetFullPath(args[^1]);
        var folder = Path.Combine(Path.GetDirectoryName(report)!, "benchmark-" + Guid.NewGuid().ToString("N"));
        using var store = new WorkStore(folder);
        var zone = TimeZoneInfo.Utc;
        var date = Timeline.DayOf(DateTimeOffset.UtcNow, zone);
        store.SavePreferences(new Preferences(TimeZoneId: zone.Id));
        var start = Timeline.DayStart(date, zone);
        var sampleTask = new TaskLabel("Demo", "BC-100", "Check the deployment");
        var tasks = Enumerable.Range(100, 16).Select(id =>
            new TaskLabel($"Demo client {id % 4 + 1}", $"BC-{id}", $"Check Business Central work {id}")).ToArray();
        var spans = Enumerable.Range(0, 10_000).Select(index =>
        {
            var time = start.AddSeconds(index * 8);
            var task = index % 5 == 0 ? null : tasks[index % tasks.Length];
            return new WorkSpan(Guid.NewGuid(), time, time.AddSeconds(8),
                new CaptureSource(index % 2 == 0 ? "VS Code" : "Browser", task));
        }).ToArray();
        var timer = Stopwatch.StartNew();
        store.SaveCapture(spans[..1000], [], zone);
        var seed = timer.Elapsed.TotalMilliseconds;
        var marked = Timeline.Preview(store.Rows(date), RoundingMode.IssueDay).Take(8).Select(line =>
            new ManualEntry(Guid.NewGuid().ToString("N"), line.Issue, RoundingMode.IssueDay, line.ProposedSeconds,
                line.AllocationIds.ToArray(), line.Start, line.Intervals.ToArray(), line.Description)).ToArray();
        store.SaveManualEntries(date, marked);
        double[] samples;
        double[] sessionSamples;
        using (var app = new TrayApplication(folder, false, "measure", showOnStart: false, diagnostics: true))
        {
            samples = app.MeasureReview();
            sessionSamples = app.MeasureReview(RoundingMode.Session);
            app.ShutDownDiagnostics();
        }
        var coldReview = samples[0];
        timer.Restart();
        foreach (var batch in spans[1000..].Chunk(100))
            store.SaveCapture(batch, [], zone);
        var remaining = timer.Elapsed.TotalMilliseconds;
        store.CheckpointWal();
        var bytes = Directory.EnumerateFiles(folder).Where(file => !file.EndsWith("-shm", StringComparison.Ordinal))
            .Sum(file => new FileInfo(file).Length);

        var engine = new CaptureEngine();
        var burst = new double[1200];
        var burstWrites = new List<double>();
        using var burstStore = new WorkStore(Path.Combine(folder, "capture-replay"));
        using var observer = new WindowsObserver(registerShortcuts: false);
        for (var i = 0; i < burst.Length; i++)
        {
            var tick = i * 100;
            timer.Restart();
            observer.MeasureNativeProbe();
            engine.Observe(new CaptureObservation(tick, start.AddMilliseconds(tick), tick,
                new CaptureSource(i % 2 == 0 ? "Browser" : "VS Code", sampleTask)));
            burst[i] = timer.Elapsed.TotalMilliseconds;
            if ((i + 1) % 50 == 0)
                CommitBurst();
        }
        engine.Observe(new CaptureObservation(120_000, start.AddSeconds(120), 120_000, null));
        CommitBurst();
        burstWrites.Sort();
        Array.Sort(samples);
        Array.Sort(sessionSamples);
        Array.Sort(burst);
        var results = new
        {
            SeedFirst1000Ms = seed, DailyReviewAllocationCount = 1000,
            DayOpenColdMs = coldReview, DayOpenMedianMs = (samples[14] + samples[15]) / 2,
            DayOpenP95Ms = samples[(int)Math.Ceiling(samples.Length * 0.95) - 1],
            SessionWorklogsP95Ms = sessionSamples[(int)Math.Ceiling(sessionSamples.Length * 0.95) - 1],
            Other9000WritesMs = remaining, DbAndWalBytes = bytes,
            NativeProbeAndReducerP95Ms = burst[(int)Math.Ceiling(burst.Length * 0.95) - 1],
            CaptureBatchWriteP95Ms = burstWrites[(int)Math.Ceiling(burstWrites.Count * 0.95) - 1],
            CaptureReplayAllocations = burstStore.Rows(date).Count,
            Spans = spans.Length, StopwatchResolution = Stopwatch.Frequency
        };
        File.WriteAllText(report, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));

        void CommitBurst()
        {
            timer.Restart();
            burstStore.SaveCapture(engine.Ready, engine.Checkpoint(), zone);
            engine.ConfirmSaved();
            burstWrites.Add(timer.Elapsed.TotalMilliseconds);
        }
    }
}
