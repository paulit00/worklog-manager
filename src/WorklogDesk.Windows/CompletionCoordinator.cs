using WorklogDesk.Core;

namespace WorklogDesk.Windows;

internal sealed class CompletionCoordinator(WorkStore store, TimeProvider clock, TimeZoneInfo zone,
    Func<RoundingMode> rounding, Func<bool> inProgress)
{
    private static readonly TimeSpan CutoffTolerance = TimeSpan.FromSeconds(15);
    private CompletionJournal journal = store.CompletionJournal();
    private DateTimeOffset? lastCutoffTick;
    private DateOnly measuredDay;
    private DateTimeOffset nextMeasure;
    private long measuredRevision = -1;
    private RoundingMode measuredRounding;
    private CompletionCounts? lastCounts;
    private CompletionCue previousDue;

    public DateTimeOffset Now => clock.GetUtcNow();
    public DateOnly Today => Timeline.DayOf(Now, zone);
    public CompletionPilot? Pilot => journal.Pilot;
    public long CheckInRevision { get; private set; }
    public IReadOnlyDictionary<DateOnly, CompletionDayRecord> Dates => journal.Dates ?? [];
    public CompletionDayRecord Day(DateOnly day) => Dates.GetValueOrDefault(day) ?? new(new());
    public CompletionCounts Counts(DateOnly day) => store.Completion(day, rounding(), day == Today && inProgress()).Counts;

    public void StartPilot(TimeOnly cutoff)
    {
        if (Pilot != null)
            return;
        var now = Now;
        var today = Timeline.DayOf(now, zone);
        var start = now < Timeline.AtLocalTime(today, cutoff, zone) && !Day(today).Cue.FirstReserved ? today : today.AddDays(1);
        Save(journal with { Pilot = new CompletionPilot(start, cutoff, zone.Id) });
        ObserveCutoff();
    }

    public void CheckIn(DateOnly date, DayCheckIn answer)
    {
        if (date > Today)
            return;
        ObserveCutoff();
        var day = Day(date);
        var counts = Counts(date);
        if ((answer is DayCheckIn.NoWork or DayCheckIn.Leave) && !AllowsNoWork(counts))
            return;
        var phase = Phase(date);
        Set(date, day with { CheckIn = answer, Pilot = day.Pilot ?? (phase.HasValue ? new PilotDay(phase.Value) : null) });
        CheckInRevision++;
        if (Pilot is { } pilot && answer == DayCheckIn.Worked)
        {
            CountPhase(PilotPhase.Baseline, 5, pilot.ReminderStarts == null, () => pilot with { ReminderStarts = Today.AddDays(1) });
            CountPhase(PilotPhase.Reminder, 10, pilot.Ends == null, () => pilot with { Ends = Today.AddDays(1) });
        }

        void CountPhase(PilotPhase target, int required, bool pending, Func<CompletionPilot> complete)
        {
            if (pending && Dates.Values.Count(item => item is { CheckIn: DayCheckIn.Worked } && item.Pilot?.Phase == target) >= required)
                Save(journal with { Pilot = complete() });
        }
    }

    public static bool AllowsNoWork(CompletionCounts counts) => !counts.HasActivity && !counts.InProgress &&
        !counts.Actionable && counts.Verified + counts.Attested == 0;

    public void SkipToday(bool skip)
    {
        var day = Day(Today);
        Set(Today, day with { Cue = day.Cue with { Skipped = skip } });
    }

    public bool Snooze()
    {
        var date = Today;
        var day = Day(date);
        var now = Now;
        if (!day.Cue.CanSnooze(now))
            return false;
        Set(date, day with { Cue = day.Cue with { SnoozeDue = now.AddMinutes(30) } });
        return true;
    }

    public CompletionCue Tick(CompletionReminder reminder, Func<bool> sessionReady)
    {
        ObserveCutoff();
        var now = Now;
        var date = Timeline.DayOf(now, zone);
        var day = Day(date);
        var cue = day.Cue;
        var baseline = Pilot is { } pilot && (date < pilot.Starts || Phase(date) == PilotPhase.Baseline);
        var eligible = reminder.Enabled && reminder.Days.Contains(date.DayOfWeek) &&
            day.CheckIn is not (DayCheckIn.NoWork or DayCheckIn.Leave) && !baseline;
        if (eligible && cue.Window == null)
        {
            var schedule = CueWindow.For(date, reminder.Time, zone);
            if (now >= schedule.Start)
                cue = cue with { Window = schedule };
        }
        cue = cue.Observe(now);
        var due = eligible ? cue.Due(now, sessionReady: true) : CompletionCue.None;
        if (due != CompletionCue.None && !sessionReady())
            due = CompletionCue.None;
        if (measuredDay != default && measuredDay < date && Phase(measuredDay).HasValue && lastCounts is { Closed: false } previous &&
            previous.Actionable && !(Day(measuredDay).Events ?? []).Any(item => item.Kind == CompletionEventKind.CarriedOver))
            AddEvent(measuredDay, CompletionEventKind.CarriedOver, now);
        var measure = now >= nextMeasure || measuredDay != date || nextMeasure - now > TimeSpan.FromSeconds(30) ||
            due != CompletionCue.None && due != previousDue;
        previousDue = due;
        if (measure && (due != CompletionCue.None || Phase(date).HasValue))
            Measure(date, now);
        if (cue != day.Cue)
        {
            if (cue.Expired && !day.Cue.Expired && cue.SnoozeDue.HasValue)
            {
                if (now - cue.Window!.Expiry <= CutoffTolerance)
                {
                    var counts = Counts(date);
                    if (counts.Actionable || counts.InProgress)
                        AddEvent(date, CompletionEventKind.SnoozedOpenAtExpiry, now);
                }
                else
                    AddEvent(date, CompletionEventKind.SnoozedExpiryUnknown, now);
            }
            Set(date, Day(date) with { Cue = cue });
        }
        ExpireOldDates(date, now);
        if (due == CompletionCue.None || measuredDay != date || lastCounts is not { Actionable: true })
            return CompletionCue.None;
        if (measuredRevision != store.CompletionRevision || measuredRounding != rounding())
        {
            Measure(date, now);
            if (lastCounts is not { Actionable: true })
                return CompletionCue.None;
        }
        // Persist the reservation before calling Windows so a restart cannot issue it twice.
        cue = due == CompletionCue.First ? cue with { FirstReserved = true } : cue with { SnoozedReserved = true };
        Set(date, Day(date) with { Cue = cue });
        return due;
    }

    public void CueRequested(DateOnly date, CompletionCue cue) => AddEvent(date,
        cue == CompletionCue.First ? CompletionEventKind.CueRequested : CompletionEventKind.SnoozedCueRequested, Now);

    public bool Activate(DateOnly date, CompletionCue cue, bool sessionReady)
    {
        var day = Day(date);
        if (!sessionReady || date != Today || day.CheckIn is DayCheckIn.NoWork or DayCheckIn.Leave ||
            !day.Cue.CanActivate(Now, cue))
            return false;
        AddEvent(date, CompletionEventKind.CueActivated, Now);
        return true;
    }

    public void ReviewOpened(DateOnly date)
    {
        if (Day(date).Cue.FirstReserved || Phase(date).HasValue)
            AddEvent(date, CompletionEventKind.ReviewOpened, Now);
    }

    public void Submission(DateOnly date, SubmissionProvenance provenance, int count = 1)
    {
        if (Phase(date).HasValue)
            AddEvent(date, provenance == SubmissionProvenance.VerifiedTempo ? CompletionEventKind.Verified : CompletionEventKind.Attested, Now, count);
    }

    public void Duplicate(DateOnly date, IEnumerable<long> remoteIds)
    {
        if (!Phase(date).HasValue)
            return;
        var ids = remoteIds.Order().Distinct().ToArray();
        if (ids.Length < 2)
            return;
        var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(ids)));
        if (!(Day(date).Events ?? []).Any(item => item.Kind == CompletionEventKind.DuplicateObserved && item.Evidence == fingerprint))
            AddEvent(date, CompletionEventKind.DuplicateObserved, Now, ids.Length - 1, fingerprint);
    }

    public void ObserveReview(DateOnly date, CompletionCounts counts)
    {
        if (Phase(date).HasValue)
            RecordProgress(date, counts, Now);
    }

    public void InvalidateCutoffContinuity() => lastCutoffTick = null;

    // Also called before store mutations, so an edit made just after cutoff cannot appear in its snapshot.
    public void ObserveCutoff()
    {
        if (Pilot is not { } pilot)
            return;
        var now = Now;
        var date = Timeline.DayOf(now, zone);
        var last = lastCutoffTick;
        lastCutoffTick = now;
        if (Phase(date) is not { } phase)
            return;
        var day = Day(date);
        var status = day.Pilot ?? new PilotDay(phase);
        if (status.Observation != null || status.MissedCutoff)
            return;
        var cutoff = Timeline.AtLocalTime(date, pilot.Cutoff, zone);
        if (now < cutoff)
            return;
        var onTime = last is { } previous && previous <= cutoff && now - previous <= CutoffTolerance;
        Set(date, day with { Pilot = onTime ? status with { Observation = new CutoffObservation(now, Counts(date)) }
            : status with { MissedCutoff = true } });
    }

    public PilotPhase? Phase(DateOnly day)
    {
        if (Pilot is not { } pilot || day < pilot.Starts || day >= pilot.Ends)
            return null;
        return Day(day).Pilot?.Phase ?? (day >= pilot.ReminderStarts ? PilotPhase.Reminder : PilotPhase.Baseline);
    }

    public string Report(CompletionReminder reminder)
    {
        if (Pilot is not { } pilot)
            return "Choose the fixed cutoff before starting. A check-in takes one click on each date.";
        var end = pilot.Ends.HasValue && pilot.Ends.Value <= Today ? pilot.Ends.Value.AddDays(-1) : Today;
        var dates = Enumerable.Range(0, Math.Max(0, end.DayNumber - pilot.Starts.DayNumber + 1))
            .Select(offset => pilot.Starts.AddDays(offset)).ToArray();
        var report = new List<string>();
        foreach (var phase in Enum.GetValues<PilotPhase>())
        {
            var days = dates.Where(date => Phase(date) == phase).Select(date => (Date: date, Record: Day(date))).ToArray();
            var work = days.Where(item => item.Record.CheckIn == DayCheckIn.Worked).ToArray();
            var due = work.Where(item => Now >= Timeline.AtLocalTime(item.Date, pilot.Cutoff, zone)).ToArray();
            var observed = due.Select(item => item.Record.Pilot?.Observation?.Counts).OfType<CompletionCounts>().ToArray();
            var closed = observed.Count(counts => counts.Closed);
            var missingCheckIns = days.Count(item => item.Record.CheckIn == DayCheckIn.Unknown &&
                (reminder.Days.Contains(item.Date.DayOfWeek) || item.Record.Pilot?.Observation?.Counts.HasActivity == true));
            var noWork = days.Count(item => item.Record.CheckIn == DayCheckIn.NoWork);
            var leave = days.Count(item => item.Record.CheckIn == DayCheckIn.Leave);
            var target = phase == PilotPhase.Baseline ? 5 : 10;
            report.Add($"{phase} · {work.Length}/{target} worked days checked in");
            report.Add($"Closed by cutoff {closed}/{due.Length} · missing cutoff {due.Length - observed.Length} · " +
                $"missing check-in {missingCheckIns} · nothing to log {noWork} · leave {leave}");
            report.Add($"Closed days · {observed.Count(item => item.Closed && item.Verified > 0 && item.Attested == 0)} verified only, " +
                $"{observed.Count(item => item.Closed && item.Attested > 0)} include manual attestation, " +
                $"{observed.Count(item => item.Closed && item.Verified + item.Attested == 0)} with no required entries");
            report.Add($"At cutoff · {observed.Sum(item => item.Verified)} verified, {observed.Sum(item => item.Attested)} attested, " +
                $"{observed.Count(item => !item.Closed && item.Verified + item.Attested > 0)} partially submitted days, " +
                $"{observed.Count(item => item.Actionable)} days with open items, {observed.Count(item => item.Unknown > 0)} with unknown Tempo status");
            var requested = 0;
            var opened = 0;
            var activations = 0;
            var snoozedOpen = 0;
            var snoozedUnknown = 0;
            var corrections = 0;
            var duplicates = 0;
            var carried = 0;
            var firstSubmissions = new List<TimeSpan>();
            var closures = new List<TimeSpan>();
            foreach (var (date, record) in days)
            {
                var events = record.Events ?? [];
                var workdayStart = Timeline.DayStart(date, zone);
                foreach (var cue in events.Where(item => item.Kind is CompletionEventKind.CueRequested or CompletionEventKind.SnoozedCueRequested))
                {
                    requested++;
                    if (events.Any(item => item.Kind == CompletionEventKind.ReviewOpened && item.AtUtc >= cue.AtUtc &&
                        item.AtUtc - cue.AtUtc <= TimeSpan.FromMinutes(15)))
                        opened++;
                }
                activations += events.Count(item => item.Kind == CompletionEventKind.CueActivated);
                snoozedOpen += events.Count(item => item.Kind == CompletionEventKind.SnoozedOpenAtExpiry);
                snoozedUnknown += events.Count(item => item.Kind == CompletionEventKind.SnoozedExpiryUnknown);
                corrections += events.Where(item => item.Kind == CompletionEventKind.CorrectionObserved).Sum(item => item.Count);
                duplicates += events.Where(item => item.Kind == CompletionEventKind.DuplicateObserved).Sum(item => item.Count);
                carried += events.Count(item => item.Kind == CompletionEventKind.CarriedOver);
                if (events.FirstOrDefault(item => item.Kind is CompletionEventKind.Verified or CompletionEventKind.Attested) is { } submission)
                    firstSubmissions.Add(submission.AtUtc - workdayStart);
                if (events.LastOrDefault(item => item.Kind is CompletionEventKind.Closed or CompletionEventKind.Reopened) is
                    { Kind: CompletionEventKind.Closed } closure)
                    closures.Add(closure.AtUtc - workdayStart);
            }
            report.Add($"Review within 15 min {opened}/{requested} Windows requests · notification activations {activations}");
            report.Add($"Median elapsed from midnight · first submission {Median(firstSubmissions)} · closure {Median(closures)}");
            report.Add($"Snoozed open at expiry {snoozedOpen} · snoozed unknown at expiry {snoozedUnknown} · carried-over dates {carried} · " +
                $"corrections observed {corrections} · duplicate matches observed {duplicates}");
            report.Add("");
        }
        report.Add("Closure includes verified and explicitly attested worklogs. Windows requests do not measure who saw a reminder.");
        report.Add("Verified counts reflect the confirmation recorded at the time. This is a descriptive comparison.");
        return string.Join(Environment.NewLine, report);

        static string Median(List<TimeSpan> values)
        {
            if (values.Count == 0)
                return "—";
            values.Sort();
            var middle = values.Count / 2;
            var minutes = (values[middle].TotalMinutes + values[(values.Count - 1) / 2].TotalMinutes) / 2;
            return $"{(int)minutes / 60}h {(int)minutes % 60:00}m";
        }
    }

    private void Measure(DateOnly date, DateTimeOffset now)
    {
        nextMeasure = now.AddSeconds(25);
        var mode = rounding();
        if (measuredDay == date && measuredRevision == store.CompletionRevision && measuredRounding == mode &&
            lastCounts?.InProgress == inProgress())
            return;
        lastCounts = Counts(date);
        measuredDay = date;
        measuredRevision = store.CompletionRevision;
        measuredRounding = mode;
        if (Day(date).CheckIn is DayCheckIn.NoWork or DayCheckIn.Leave && !AllowsNoWork(lastCounts) &&
            (lastCounts.HasActivity || lastCounts.Actionable || lastCounts.Verified + lastCounts.Attested > 0))
        {
            Set(date, Day(date) with { CheckIn = DayCheckIn.Unknown });
            CheckInRevision++;
        }
        if (Phase(date).HasValue)
            RecordProgress(date, lastCounts, now);
    }

    private void RecordProgress(DateOnly date, CompletionCounts counts, DateTimeOffset now)
    {
        var day = Day(date);
        var lastClosure = day.Events?.LastOrDefault(item => item.Kind is CompletionEventKind.Closed or CompletionEventKind.Reopened);
        if (counts.Closed && lastClosure?.Kind != CompletionEventKind.Closed)
            AddEvent(date, CompletionEventKind.Closed, now);
        if (!counts.Closed && lastClosure?.Kind == CompletionEventKind.Closed)
            AddEvent(date, CompletionEventKind.Reopened, now);
        if (counts.Corrections > day.ObservedCorrections)
            AddEvent(date, CompletionEventKind.CorrectionObserved, now, counts.Corrections - day.ObservedCorrections);
        if (counts.Corrections != day.ObservedCorrections)
            Set(date, Day(date) with { ObservedCorrections = counts.Corrections });
    }

    private void ExpireOldDates(DateOnly today, DateTimeOffset now)
    {
        foreach (var (date, day) in Dates.Where(item => item.Key < today && item.Value.Cue is
            { Window: not null, Expired: false }).ToArray())
        {
            if (day.Cue.SnoozeDue.HasValue)
            {
                if (now - day.Cue.Window!.Expiry <= CutoffTolerance)
                {
                    if (Counts(date).Actionable)
                        AddEvent(date, CompletionEventKind.SnoozedOpenAtExpiry, now);
                }
                else
                    AddEvent(date, CompletionEventKind.SnoozedExpiryUnknown, now);
            }
            Set(date, Day(date) with { Cue = day.Cue.Observe(now) });
        }
    }

    private void AddEvent(DateOnly date, CompletionEventKind kind, DateTimeOffset at, int count = 1, string? evidence = null)
    {
        var day = Day(date);
        Set(date, day with { Events = [.. day.Events ?? [], new CompletionEvent(kind, at, count, evidence)] });
    }

    private void Set(DateOnly date, CompletionDayRecord day)
    {
        var next = new Dictionary<DateOnly, CompletionDayRecord>(Dates) { [date] = day };
        Save(journal with { Dates = next });
    }

    private void Save(CompletionJournal next)
    {
        store.SaveCompletionJournal(next);
        journal = next;
    }
}
