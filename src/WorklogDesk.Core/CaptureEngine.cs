namespace WorklogDesk.Core;

public readonly record struct CaptureObservation(long Tick, DateTimeOffset Utc, long LastInputTick,
    CaptureSource? Source, bool SampleDisagrees = false);

public sealed class CaptureEngine
{
    private sealed record Segment(long StartTick, DateTimeOffset Start, CaptureSource Source, Guid Id);
    private sealed record ClosedSegment(WorkSpan Span, long StartTick, long EndTick);

    private CaptureObservation? previous;
    private Segment? open;
    private readonly List<ClosedSegment> provisional = [];
    private readonly List<WorkSpan> ready = [];
    private DateTimeOffset priorEpochEnd = DateTimeOffset.MinValue;
    private long cutoff = 120_000;

    public TimeSpan IdleThreshold
    {
        get => TimeSpan.FromMilliseconds(cutoff);
        set => cutoff = (long)Math.Clamp(value.TotalMilliseconds, 5_000, 3_600_000);
    }

    public SpanKind? CurrentKind => open?.Source.Kind;
    public CaptureSource? CurrentSource => open?.Source;

    public void Observe(CaptureObservation next)
    {
        if (previous is not { } last)
        {
            Initialize(next);
            return;
        }
        if (next.Tick < last.Tick)
            return;

        var predicted = last.Utc.AddMilliseconds(next.Tick - last.Tick);
        if (Math.Abs((next.Utc - predicted).TotalSeconds) > 5 || next.SampleDisagrees)
        {
            Close(last.Tick, last.Utc);
            FlushAsUncertain();
            if (next.Utc < last.Utc && last.Utc > priorEpochEnd)
                priorEpochEnd = last.Utc;
            if (next.Utc > last.Utc && last.Source != null)
                ready.Add(new WorkSpan(Guid.NewGuid(), last.Utc, next.Utc,
                    new CaptureSource("Unobserved interval", Kind: SpanKind.Uncertain)));
            Initialize(next);
            return;
        }

        // Small system clock adjustments never alter measured elapsed time.
        next = next with { Utc = predicted };
        previous = next;
        var lastInput = Math.Min(next.LastInputTick, next.Tick);
        var idle = next.Tick - lastInput >= cutoff;
        CaptureSource? source = next.Source;

        if (open?.Source.Kind == SpanKind.Idle && (!idle || source == null || source.Kind == SpanKind.Manual))
        {
            var end = !idle ? Math.Clamp(lastInput, open.StartTick, next.Tick) : next.Tick;
            Close(end, At(next, end));
            if (source != null && source.Kind != SpanKind.Manual && !idle)
                Open(end, At(next, end), source);
        }

        if (idle && IsForeground(source))
        {
            if (open != null && open.Source.Kind != SpanKind.Idle)
                Close(next.Tick, next.Utc);
            if (open == null)
            {
                var beginning = TrimIdleTail(next, lastInput);
                Open(beginning, At(next, beginning), new CaptureSource("Idle gap", Kind: SpanKind.Idle));
            }
            return;
        }

        if (idle && IsForeground(open?.Source))
        {
            Close(next.Tick, next.Utc);
            var beginning = TrimIdleTail(next, lastInput);
            if (beginning < next.Tick)
                ready.Add(new WorkSpan(Guid.NewGuid(), At(next, beginning), next.Utc,
                    new CaptureSource("Idle gap", Kind: SpanKind.Idle)));
        }

        if (open != null && source != null && open.Source.SameAttribution(source))
        {
            // Same app and task under a new title: keep one span and remember what else was open.
            var context = WindowContext.Append(open.Source.Context, source.Context);
            if (context != open.Source.Context)
                open = open with { Source = open.Source with { Context = context } };
        }
        else if (open?.Source != source)
        {
            Close(next.Tick, next.Utc);
            if (source != null)
                Open(next.Tick, next.Utc, source);
        }
        CommitInput(lastInput);
        if (source == null || source.Kind == SpanKind.Manual)
            FlushAsUncertain(knownBoundary: true);
    }

    public IReadOnlyList<WorkSpan> Ready => ready;

    public void ConfirmSaved() => ready.Clear();

    public IReadOnlyList<WorkSpan> Checkpoint()
    {
        var copy = provisional.Select(segment => Uncertain(segment.Span)).ToList();
        if (previous is { } sample && open is { } segment && sample.Tick > segment.StartTick)
        {
            var span = new WorkSpan(segment.Id, segment.Start, sample.Utc, segment.Source);
            if (segment.Source.Kind != SpanKind.Foreground)
            {
                copy.Add(DetectClockOverlap(span));
            }
            else
            {
                var knownTick = Math.Clamp(sample.LastInputTick, segment.StartTick, sample.Tick);
                var boundary = At(sample, knownTick);
                if (knownTick > segment.StartTick)
                    copy.Add(DetectClockOverlap(span with { End = boundary }));
                if (knownTick < sample.Tick)
                    copy.Add(Uncertain(span with { Id = Guid.NewGuid(), Start = boundary }));
            }
        }
        return copy;
    }

    private void Initialize(CaptureObservation sample)
    {
        previous = sample;
        if (sample.Source == null)
            return;
        var isIdle = IsForeground(sample.Source) && sample.Tick - sample.LastInputTick >= cutoff;
        Open(sample.Tick, sample.Utc, isIdle
            ? new CaptureSource("Idle gap", Kind: SpanKind.Idle)
            : sample.Source);
    }

    private void Open(long tick, DateTimeOffset utc, CaptureSource source) =>
        open = new Segment(tick, utc, source, Guid.NewGuid());

    private void Close(long tick, DateTimeOffset utc)
    {
        var current = open;
        open = null;
        if (current == null || tick <= current.StartTick || utc <= current.Start)
            return;
        var span = DetectClockOverlap(new WorkSpan(current.Id, current.Start, utc, current.Source));
        if (IsForeground(current.Source))
            provisional.Add(new ClosedSegment(span, current.StartTick, tick));
        else
            ready.Add(span);
    }

    private void CommitInput(long tick)
    {
        var count = provisional.TakeWhile(segment => segment.EndTick <= tick).Count();
        for (var index = 0; index < count; index++)
            ready.Add(provisional[index].Span);
        if (count != 0)
            provisional.RemoveRange(0, count);
        if (provisional.Count == 0)
            return;
        var next = provisional[0];
        if (tick <= next.StartTick)
            return;
        var end = next.Span.End.AddMilliseconds(tick - next.EndTick);
        ready.Add(next.Span with { End = end });
        provisional[0] = new ClosedSegment(next.Span with { Id = Guid.NewGuid(), Start = end }, tick, next.EndTick);
    }

    private long TrimIdleTail(CaptureObservation sample, long lastInput)
    {
        CommitInput(lastInput);
        if (provisional.Count == 0)
            return sample.Tick;
        var first = provisional[0];
        var cutoffTick = Math.Max(first.StartTick, lastInput);
        if (cutoffTick > first.StartTick)
            ready.Add(first.Span with { End = At(sample, cutoffTick) });
        provisional.Clear();
        return cutoffTick;
    }

    private void FlushAsUncertain(bool knownBoundary = false)
    {
        foreach (var segment in provisional)
        {
            var span = segment.Span;
            ready.Add(knownBoundary && span.Source.Kind == SpanKind.Foreground && segment.EndTick - segment.StartTick < 1000
                ? span with { Source = new CaptureSource("Switch transition", Kind: SpanKind.Incidental) }
                : Uncertain(span));
        }
        provisional.Clear();
    }

    private WorkSpan DetectClockOverlap(WorkSpan span) =>
        span.Start < priorEpochEnd ? Uncertain(span) : span;

    private static WorkSpan Uncertain(WorkSpan span) =>
        span with { Source = span.Source with { Kind = SpanKind.Uncertain } };

    private static bool IsForeground(CaptureSource? source) =>
        source?.Kind is SpanKind.Foreground or SpanKind.Uncertain;

    private static DateTimeOffset At(CaptureObservation sample, long tick) =>
        sample.Utc.AddMilliseconds(tick - sample.Tick);
}
