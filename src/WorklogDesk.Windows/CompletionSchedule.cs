using WorklogDesk.Core;

namespace WorklogDesk.Windows;

internal sealed record CompletionReminder(bool Enabled, DayOfWeek[] Days, TimeOnly Time)
{
    public static CompletionReminder Default => new(false,
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday], new(16, 45));
}

internal enum CompletionCue { None, First, Snoozed }

internal sealed record CueWindow(DateTimeOffset Start, DateTimeOffset FirstExpiry, DateTimeOffset Expiry)
{
    public static CueWindow For(DateOnly day, TimeOnly time, TimeZoneInfo zone)
    {
        var start = Timeline.AtLocalTime(day, time, zone);
        var midnight = Timeline.DayStart(day.AddDays(1), zone);
        return new(start, Earlier(start.AddHours(1), midnight), Earlier(start.AddHours(2), midnight));
    }

    private static DateTimeOffset Earlier(DateTimeOffset first, DateTimeOffset second) => first < second ? first : second;
}

internal sealed record CueDay(CueWindow? Window = null, bool Skipped = false, bool FirstReserved = false,
    bool SnoozedReserved = false, DateTimeOffset? SnoozeDue = null, bool FirstExpired = false, bool Expired = false)
{
    public CueDay Observe(DateTimeOffset now) => Window == null ? this : this with
    {
        FirstExpired = FirstExpired || now >= Window.FirstExpiry,
        Expired = Expired || now >= Window.Expiry
    };

    public CompletionCue Due(DateTimeOffset now, bool sessionReady)
    {
        if (!sessionReady || Window == null || Skipped || Expired || now < Window.Start || now >= Window.Expiry)
            return CompletionCue.None;
        if (!FirstReserved && !FirstExpired && now < Window.FirstExpiry)
            return CompletionCue.First;
        return !SnoozedReserved && SnoozeDue is { } due && now >= due ? CompletionCue.Snoozed : CompletionCue.None;
    }

    public bool CanSnooze(DateTimeOffset now) => FirstReserved && !Skipped && !Expired && !SnoozeDue.HasValue &&
        Window is { } window && now >= window.Start && now.AddMinutes(30) < window.Expiry;

    public bool CanActivate(DateTimeOffset now, CompletionCue cue) => Window is { } window &&
        !Skipped && !Expired && now >= window.Start && (cue == CompletionCue.First
            ? FirstReserved && !FirstExpired && !SnoozeDue.HasValue && now < window.FirstExpiry
            : SnoozedReserved && SnoozeDue is { } due && now >= due && now < window.Expiry);
}

internal enum DayCheckIn { Unknown, Worked, NoWork, Leave }
internal enum PilotPhase { Baseline, Reminder }
internal enum CompletionEventKind
{
    CueRequested, SnoozedCueRequested, CueActivated, ReviewOpened, Verified, Attested, Closed,
    Reopened, SnoozedOpenAtExpiry, SnoozedExpiryUnknown, CorrectionObserved, DuplicateObserved, CarriedOver
}

internal sealed record CompletionEvent(CompletionEventKind Kind, DateTimeOffset AtUtc, int Count = 1, string? Evidence = null);
internal sealed record CutoffObservation(DateTimeOffset AtUtc, CompletionCounts Counts);
internal sealed record PilotDay(PilotPhase Phase, CutoffObservation? Observation = null, bool MissedCutoff = false);
internal sealed record CompletionDayRecord(CueDay Cue, DayCheckIn CheckIn = DayCheckIn.Unknown,
    PilotDay? Pilot = null, CompletionEvent[]? Events = null, int ObservedCorrections = 0);
internal sealed record CompletionPilot(DateOnly Starts, TimeOnly Cutoff, string ZoneId,
    DateOnly? ReminderStarts = null, DateOnly? Ends = null);
internal sealed record CompletionJournal(CompletionPilot? Pilot = null,
    Dictionary<DateOnly, CompletionDayRecord>? Dates = null);
