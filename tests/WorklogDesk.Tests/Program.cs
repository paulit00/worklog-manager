using WorklogDesk.Core;
using WorklogDesk.Windows;
using System.Windows.Forms;

var checks = new (string Title, Action Check)[]
{
    ("quarter-hour rounding", QuarterHour),
    ("same-issue rounding modes", Grouping),
    ("incidental switches", Incidental),
    ("untracked handoff slivers do not split one billing session", UntrackedSwitch),
    ("queued focus transitions keep the intervening activity", QueuedFocus),
    ("idle trimming across a focus switch", IdleTrim),
    ("idle trimming before switching to an excluded app", IdleBeforeExcluded),
    ("manual timer does not accrue lock time", ManualLock),
    ("stopping manual time during idle closes at the stop", ManualIdle),
    ("missing foreground handoff is unresolved", MissingHandoff),
    ("wall-clock rollback preserves review evidence", ClockRollback),
    ("midnight and DST allocation totals", Calendar),
    ("spreadsheet formula escaping", Csv),
    ("SQLite checkpoint recovery is exactly once", StoreRecovery),
    ("foreground checkpoints keep the input-confirmed prefix", CheckpointPrefix),
    ("overlapping approvals roll back", StoreOverlap),
    ("captured overlap stays unassigned", CaptureOverlap),
    ("manual Tempo coverage keeps a late delta separate", LedgerDifference),
    ("manual coverage persists and guards another rounding mode", LedgerPersistence),
    ("approval is scoped to the chosen rounding mode after restart", ApprovalModePersistence),
    ("changing a session boundary invalidates stored approvals", ApprovalPayloadPersistence),
    ("export excludes drafts and previously entered work", ExportEligibility),
    ("removed work retains a visible prior-entry obligation", LedgerOrphan),
    ("splitting and reassigning already entered time blocks duplicate export", SplitRecordedTime),
    ("recording the source correction unblocks only the moved time", CorrectThenMove),
    ("changed descriptions require a correction without exporting again", DescriptionCorrection),
    ("same-duration start edits remain visible as corrections", StartCorrection),
    ("a reduced recorded session can be corrected before moving time", ReducedSession),
    ("consolidated Tempo sessions produce a reconciliation rather than an export", ConsolidatedSessions),
    ("reciprocal issue corrections can be verified as one batch", ReciprocalCorrections),
    ("idle gaps stay reviewable while brief unpinned switches stay incidental", ReviewableIdle),
    ("export receipts and bulk Tempo marks persist", ManualBatchPersistence),
    ("native review approves active rows and hides approval on other tabs", NativeApproval),
    ("a legacy mark kept as separate holds only its own issue", LegacyMarkKeptSeparate),
    ("a short excluded switch does not split a session but internal work does", ExcludedBridge),
    ("a brief switch into a client-only mapped window is incidental", MappedGlance),
    ("oldest unassigned ignores days with only idle gaps", UnreviewedSkipsIdle),
    ("shortening or moving a row keeps the trimmed time unassigned", EditKeepsRemainders),
    ("scoped Jira authentication is confined to the gateway", JiraScoped),
    ("project search includes colleague issues and pins continuation", JiraSearch),
    ("Jira rejects redirects and untrusted sites", JiraRejectsRedirect)
};
var failures = 0;
foreach (var (title, check) in checks)
{
    try
    {
        check();
        Console.WriteLine($"PASS {title}");
    }
    catch (Exception error)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {title}: {error.Message}");
    }
}
Console.WriteLine($"{checks.Length - failures}/{checks.Length} passed");
return failures == 0 ? 0 : 1;

static void QuarterHour()
{
    Equal(900, Timeline.RoundSeconds(5 * 60_000));
    Equal(900, Timeline.RoundSeconds(15 * 60_000));
    Equal(1800, Timeline.RoundSeconds(16 * 60_000));
    Equal(0, Timeline.RoundSeconds(0));
    for (var milliseconds = 1; milliseconds < 3_600_000; milliseconds += 1973)
    {
        var result = Timeline.RoundSeconds(milliseconds);
        True(result % 900 == 0 && result * 1000 >= milliseconds && result * 1000 - milliseconds < 900_000);
    }
}

static void Grouping()
{
    var rows = new[] { Row(0, 300, "BC-5"), Row(600, 1020, "BC-5") };
    Equal(900, Timeline.Preview(rows, RoundingMode.IssueDay).Single().ProposedSeconds);
    Equal(1800, Timeline.Preview(rows, RoundingMode.Session).Sum(entry => entry.ProposedSeconds));
    Equal(720_000L, Timeline.Preview(rows, RoundingMode.IssueDay).Single().ActualMilliseconds);
}

static void Incidental()
{
    var rows = new[] { Row(0, 300, "BC-5"),
        Row(300, 307, ""), Row(307, 607, "BC-5") };
    var result = Timeline.Preview(rows, RoundingMode.Session).Single();
    Equal(600_000L, result.ActualMilliseconds);
    Equal(900, result.ProposedSeconds);
}

static void UntrackedSwitch()
{
    var engine = new CaptureEngine();
    var pinned = new CaptureSource("VS Code", new TaskLabel("Client", "BC-5", "Fix"));
    Observe(engine, 0, 0, pinned);
    Observe(engine, 60_000, 59_984, null);
    Observe(engine, 61_000, 61_000, pinned);
    Observe(engine, 91_000, 90_984, null);
    var rows = engine.Ready.SelectMany(span => Timeline.Allocate(span, TimeZoneInfo.Utc)).ToArray();
    Equal(2, rows.Count(row => row.Kind == SpanKind.Incidental && row.Status == Disposition.Excluded));
    Equal(0, rows.Count(row => row.Status == Disposition.Unassigned));
    var log = Timeline.Preview(rows, RoundingMode.Session).Single();
    Equal(900, log.ProposedSeconds);
}

static void QueuedFocus()
{
    var engine = new CaptureEngine();
    var browser = new CaptureSource("Browser");
    var desktop = new CaptureSource("Remote Desktop");
    Observe(engine, 0, 0, browser);
    Observe(engine, 10_000, 10_000, desktop);
    Observe(engine, 25_000, 25_000, browser);
    Observe(engine, 40_000, 40_000, null);
    Equal(15d, engine.Ready.Where(span => span.Source.App == "Remote Desktop")
        .Sum(span => (span.End - span.Start).TotalSeconds));
}

static void CheckpointPrefix()
{
    var engine = new CaptureEngine();
    var browser = new CaptureSource("Browser", new TaskLabel("Client", "BC-5", "Work"));
    Observe(engine, 0, 0, browser);
    Observe(engine, 3_600_000, 3_590_000, browser);
    var spans = engine.Checkpoint();
    Equal(2, spans.Count);
    Equal(SpanKind.Foreground, spans[0].Source.Kind);
    Equal(3590d, (spans[0].End - spans[0].Start).TotalSeconds);
    Equal(SpanKind.Uncertain, spans[1].Source.Kind);
    Equal(10d, (spans[1].End - spans[1].Start).TotalSeconds);
    Equal(spans[0].End, spans[1].Start);
}

static void IdleTrim()
{
    var core = new CaptureEngine();
    var browser = new CaptureSource("Browser", new TaskLabel("North", "BC-5"));
    var desktop = new CaptureSource("Remote Desktop");
    Observe(core, 0, 0, browser);
    Observe(core, 60_000, 30_000, desktop);
    Observe(core, 155_000, 30_000, desktop);
    Observe(core, 170_000, 170_000, desktop);
    Observe(core, 180_000, 180_000, null);
    var spans = core.Ready;
    Equal(30d, spans.Where(span => span.Source.App == "Browser").Sum(span => (span.End - span.Start).TotalSeconds));
    Equal(140d, spans.Where(span => span.Source.Kind == SpanKind.Idle).Sum(span => (span.End - span.Start).TotalSeconds));
    Equal(10d, spans.Where(span => span.Source.App == "Remote Desktop").Sum(span => (span.End - span.Start).TotalSeconds));
}

static void IdleBeforeExcluded()
{
    var core = new CaptureEngine();
    Observe(core, 0, 0, new CaptureSource("VS Code"));
    Observe(core, 140_000, 10_000, null);
    Equal(10d, (core.Ready[0].End - core.Ready[0].Start).TotalSeconds);
    Equal(SpanKind.Idle, core.Ready[1].Source.Kind);
}

static void ManualLock()
{
    var core = new CaptureEngine();
    Observe(core, 0, 0, new CaptureSource("Manual", Kind: SpanKind.Manual));
    Observe(core, 300_000, 0, null);
    Observe(core, 700_000, 700_000, new CaptureSource("VS Code"));
    Equal(300d, (core.Ready.Single().End - core.Ready.Single().Start).TotalSeconds);
    Equal(SpanKind.Manual, core.Ready.Single().Source.Kind);
}

static void ManualIdle()
{
    var core = new CaptureEngine();
    Observe(core, 0, 0, new CaptureSource("Manual", Kind: SpanKind.Manual));
    Observe(core, 300_000, 0, new CaptureSource("VS Code"));
    Observe(core, 320_000, 0, new CaptureSource("VS Code"));
    Observe(core, 330_000, 330_000, null);
    Equal(300d, core.Ready.Where(span => span.Source.Kind == SpanKind.Manual).Sum(span => (span.End - span.Start).TotalSeconds));
    Equal(30d, core.Ready.Where(span => span.Source.Kind == SpanKind.Idle).Sum(span => (span.End - span.Start).TotalSeconds));
}

static void MissingHandoff()
{
    var core = new CaptureEngine();
    Observe(core, 0, 0, new CaptureSource("VS Code"));
    Observe(core, 30_000, 30_000, new CaptureSource("VS Code"));
    core.Observe(new CaptureObservation(35_000, Origin().AddSeconds(35), 35_000,
        new CaptureSource("Remote Desktop"), SampleDisagrees: true));
    True(core.Ready.All(span => span.Source.Kind == SpanKind.Uncertain));
    Equal(35d, core.Ready.Sum(span => (span.End - span.Start).TotalSeconds));
}

static void ClockRollback()
{
    var core = new CaptureEngine();
    var source = new CaptureSource("VS Code");
    Observe(core, 0, 0, source);
    Observe(core, 30_000, 30_000, source);
    core.Observe(new CaptureObservation(31_000, Origin().AddSeconds(-10), 31_000, source));
    core.Observe(new CaptureObservation(41_000, Origin(), 41_000, null));
    Equal(2, core.Ready.Count);
    True(core.Ready.All(span => span.Source.Kind == SpanKind.Uncertain));
    Equal(40d, core.Ready.Sum(span => (span.End - span.Start).TotalSeconds));
}

static void Calendar()
{
    var zone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
    var spring = new DateOnly(2026, 3, 8);
    var autumn = new DateOnly(2026, 11, 1);
    Equal(23d, (Timeline.DayStart(spring.AddDays(1), zone) - Timeline.DayStart(spring, zone)).TotalHours);
    Equal(25d, (Timeline.DayStart(autumn.AddDays(1), zone) - Timeline.DayStart(autumn, zone)).TotalHours);
    var boundary = Timeline.DayStart(spring, zone);
    var span = new WorkSpan(Guid.NewGuid(), boundary.AddMinutes(-4), boundary.AddMinutes(5), new CaptureSource("Outlook"));
    var rows = Timeline.Allocate(span, zone).ToArray();
    Equal(2, rows.Length);
    Equal(540_000L, rows.Sum(row => row.Milliseconds));
}

static void Csv()
{
    var row = Row(0, 300, "BC-5") with { Task = new TaskLabel("=FORMULA", "BC-5", " =FORMULA", "https://example.com/") };
    var csv = WorklogCsv.Export(row.Day, Timeline.Preview([row], RoundingMode.IssueDay), TimeZoneInfo.Utc, true);
    True(csv.Contains("'=FORMULA") && csv.Contains("' =FORMULA") && !csv.Contains("https://"));
    True(csv.Contains(",To log,") && csv.Contains("\"900\",\"15m\""));
    Equal("15m", WorklogCsv.TempoDuration(900));
    Equal("1h", WorklogCsv.TempoDuration(3600));
    Equal("1h 45m", WorklogCsv.TempoDuration(6300));
}

static void StoreRecovery() => WithStore(folder =>
{
    var span = new WorkSpan(Guid.NewGuid(), Origin(), Origin().AddMinutes(5),
        new CaptureSource("Manual", new TaskLabel("Client", "BC-5", "Private work"), SpanKind.Manual));
    using (var db = new WorkStore(folder))
        db.SaveCapture([], [span], TimeZoneInfo.Utc);
    using (var db = new WorkStore(folder))
    {
        db.Recover(TimeZoneInfo.Utc);
        db.Recover(TimeZoneInfo.Utc);
        Equal("Private work", db.Rows(Day()).Single().Task.Description);
    }
});

static void StoreOverlap() => WithStore(folder =>
{
    using var db = new WorkStore(folder);
    db.ReplaceRows([], [Row(0, 300, "BC-5")]);
    try { db.ReplaceRows([], [Row(200, 500, "BC-8")]); }
    catch (InvalidOperationException)
    {
        Equal(1, db.Rows(Day()).Count);
        return;
    }
    throw new Exception("An overlap was accepted.");
});

static void CaptureOverlap() => WithStore(folder =>
{
    using var db = new WorkStore(folder);
    db.ReplaceRows([], [Row(0, 300, "BC-5")]);
    db.SaveCapture([new WorkSpan(Guid.NewGuid(), Origin().AddSeconds(200), Origin().AddSeconds(500),
        new CaptureSource("VS Code", new TaskLabel("", "BC-5")))], [], TimeZoneInfo.Utc);
    var rows = db.Rows(Day());
    Equal(Disposition.Unassigned, rows[1].Status);
    Equal(300_000L, Timeline.Preview(rows, RoundingMode.IssueDay).Single().ActualMilliseconds);
});

static void LedgerDifference()
{
    var first = Row(0, 300, "BC-5");
    var later = Row(360, 1020, "BC-5");
    var logs = Timeline.Preview([first, later], RoundingMode.IssueDay);
    var line = logs.Single();
    var entry = new ManualEntry("one", "BC-5", RoundingMode.IssueDay, 900, [first.Id]);
    Equal(900, ManualLedger.Check(line, RoundingMode.IssueDay, logs, [entry]).Difference(line));
}

static void LedgerPersistence() => WithStore(folder =>
{
    var row = Row(0, 300, "BC-5");
    var logs = Timeline.Preview([row], RoundingMode.Session);
    using (var db = new WorkStore(folder))
        db.SaveManualEntry(Day(), new ManualEntry("one", "BC-5", RoundingMode.IssueDay, 900, [row.Id]));
    using var reopened = new WorkStore(folder);
    True(ManualLedger.Check(logs.Single(), RoundingMode.Session, logs, reopened.ManualEntries(Day())).NeedsReview);
});

static void ApprovalModePersistence() => WithStore(folder =>
{
    using (var db = new WorkStore(folder))
        db.ReplaceRows([], [Row(0, 60, "BC-5") with { Approved = true, ApprovedMode = RoundingMode.IssueDay },
            Row(450, 750, "BC-5") with { Approved = true, ApprovedMode = RoundingMode.IssueDay }]);
    using var reopened = new WorkStore(folder);
    var rows = reopened.Rows(Day());
    Equal(2, rows.Count(row => row.IsApprovedIn(RoundingMode.IssueDay)));
    var exportable = rows.Where(row => row.IsApprovedIn(RoundingMode.Session)).Select(row => row.Id).ToHashSet();
    True(Timeline.Preview(rows, RoundingMode.Session).All(line =>
        !ManualLedger.CanExport(line, RoundingMode.Session, [], [], exportable)));
});

static void ApprovalPayloadPersistence() => WithStore(folder =>
{
    var first = Row(0, 300, "BC-5");
    var gap = Row(300, 360, "") with { Kind = SpanKind.Idle, Status = Disposition.Excluded };
    var second = Row(360, 660, "BC-5");
    var old = Timeline.Preview([first, gap, second], RoundingMode.Session);
    Equal(2, old.Count);
    using (var db = new WorkStore(folder))
        db.ReplaceRows([], [first with { Approved = true, ApprovedMode = RoundingMode.Session,
                ApprovedKey = old[0].ApprovalKey(RoundingMode.Session) },
            second with { Approved = true, ApprovedMode = RoundingMode.Session,
                ApprovedKey = old[1].ApprovalKey(RoundingMode.Session) }]);
    using var reopened = new WorkStore(folder);
    var rows = reopened.Rows(Day());
    True(rows[0].IsApprovedFor(RoundingMode.Session, old[0].ApprovalKey(RoundingMode.Session)));
    var merged = Timeline.Preview(rows, RoundingMode.Session).Single();
    True(rows.All(row => !row.IsApprovedFor(RoundingMode.Session, merged.ApprovalKey(RoundingMode.Session))));
});

static void ExportEligibility()
{
    var rows = new[] { Row(0, 300, "BC-5"), Row(360, 1020, "BC-5") };
    var logs = Timeline.Preview(rows, RoundingMode.IssueDay);
    var line = logs.Single();
    var approved = new HashSet<string> { rows[0].Id };
    True(!ManualLedger.CanExport(line, RoundingMode.IssueDay, logs, [], approved));
    approved.Add(rows[1].Id);
    True(ManualLedger.CanExport(line, RoundingMode.IssueDay, logs, [], approved));
    var entered = new ManualEntry("one", line.Issue, RoundingMode.IssueDay, 900, [rows[0].Id]);
    True(!ManualLedger.CanExport(line, RoundingMode.IssueDay, logs, [entered], approved));
}

static void LedgerOrphan()
{
    var row = Row(0, 300, "BC-5");
    var entry = new ManualEntry("one", "BC-5", RoundingMode.IssueDay, 900, [row.Id]);
    var logs = Timeline.Preview([row with { Status = Disposition.Internal }], RoundingMode.IssueDay);
    Equal("one", ManualLedger.Orphaned(RoundingMode.IssueDay, logs, [entry]).Single().Id);
}

static void EditKeepsRemainders()
{
    var original = Row(0, 1800, "BC-5") with { Approved = true };
    var shrunk = original with { Start = original.Start.AddMinutes(5), End = original.End.AddMinutes(-10) };
    var parts = Timeline.Remainders(original, shrunk).ToArray();
    Equal(2, parts.Length);
    Equal(1_800_000L, parts.Sum(part => part.Milliseconds) + shrunk.Milliseconds);
    True(parts.All(part => part.Status == Disposition.Unassigned && part.Task.Issue.Length == 0 && !part.Approved));
    var moved = original with { Start = original.End.AddMinutes(5), End = original.End.AddMinutes(10) };
    Equal(1_800_000L, Timeline.Remainders(original, moved).Sum(part => part.Milliseconds));
    var extended = original with { Start = original.Start.AddMinutes(-5) };
    Equal(0, Timeline.Remainders(original, extended).Count());
}

static void UnreviewedSkipsIdle() => WithStore(folder =>
{
    using var db = new WorkStore(folder);
    var idle = Row(0, 600, "") with { Kind = SpanKind.Idle };
    var active = Row(0, 600, "") with { Day = Day().AddDays(-1) };
    db.ReplaceRows([], [idle, active]);
    var days = db.UnreviewedDays();
    True(!days.Contains(Day()));
    True(days.Contains(Day().AddDays(-1)));
});

static void MappedGlance()
{
    Allocation Kind(int seconds, TaskLabel task) => Timeline.Allocate(new WorkSpan(Guid.NewGuid(), Origin(),
        Origin().AddSeconds(seconds), new CaptureSource("VS Code", task)), TimeZoneInfo.Utc).Single();
    var glance = Kind(3, new TaskLabel("Contoso"));
    Equal(SpanKind.Incidental, glance.Kind);
    Equal(Disposition.Excluded, glance.Status);
    Equal(SpanKind.Foreground, Kind(30, new TaskLabel("Contoso")).Kind);
    var mappedIssue = Kind(3, new TaskLabel("Contoso", "BC-5"));
    Equal(SpanKind.Foreground, mappedIssue.Kind);
    Equal(Disposition.Billable, mappedIssue.Status);
}

static void ExcludedBridge()
{
    var first = Row(0, 1200, "BC-5");
    var glance = Row(1200, 1230, "") with { Status = Disposition.Excluded };
    var second = Row(1230, 2430, "BC-5");
    var joined = Timeline.Preview([first, glance, second], RoundingMode.Session).Single();
    Equal(2_400_000L, joined.ActualMilliseconds);
    Equal(2700, joined.ProposedSeconds);
    Equal(2, Timeline.Preview([first, glance with { Status = Disposition.Internal }, second], RoundingMode.Session).Count);
    var longBreak = Row(1200, 1500, "") with { Status = Disposition.Excluded };
    var later = Row(1500, 2700, "BC-5");
    Equal(2, Timeline.Preview([first, longBreak, later], RoundingMode.Session).Count);
}

static void LegacyMarkKeptSeparate()
{
    var bc = Row(0, 600, "BC-5");
    var ops = Row(900, 1500, "OPS-2");
    var logs = Timeline.Preview([bc, ops], RoundingMode.IssueDay);
    var approved = new HashSet<string> { bc.Id, ops.Id };
    var legacy = new ManualEntry("old", "BC-9", RoundingMode.IssueDay, 900, ["gone"]);
    True(ManualLedger.IsUnplacedLegacy(legacy, logs));
    True(logs.All(line => !ManualLedger.CanExport(line, RoundingMode.IssueDay, logs, [legacy], approved)));
    var kept = ManualLedger.KeepSeparate(legacy);
    True(!ManualLedger.IsUnplacedLegacy(kept, logs));
    True(logs.All(line => ManualLedger.CanExport(line, RoundingMode.IssueDay, logs, [kept], approved)));
    var sameIssue = ManualLedger.KeepSeparate(legacy with { Issue = "BC-5" });
    True(!ManualLedger.CanExport(logs.Single(line => line.Issue == "BC-5"), RoundingMode.IssueDay, logs, [sameIssue], approved));
    True(ManualLedger.CanExport(logs.Single(line => line.Issue == "OPS-2"), RoundingMode.IssueDay, logs, [sameIssue], approved));
}

static void SplitRecordedTime() => WithStore(folder =>
{
    using var db = new WorkStore(folder);
    var original = Row(0, 600, "BC-5") with { Approved = true };
    db.ReplaceRows([], [original]);
    var old = Timeline.Preview([original], RoundingMode.IssueDay).Single();
    db.SaveManualEntry(Day(), Mark(old));
    var first = Row(0, 300, "BC-8") with { Approved = true };
    var second = Row(300, 600, "BC-8") with { Approved = true };
    db.ReplaceRows([original.Id], [first, second]);
    var current = Timeline.Preview(db.Rows(Day()), RoundingMode.IssueDay);
    var line = current.Single();
    var entered = db.ManualEntries(Day());
    True(ManualLedger.Check(line, RoundingMode.IssueDay, current, entered).NeedsReview);
    True(!ManualLedger.CanExport(line, RoundingMode.IssueDay, current, entered,
        new HashSet<string>([first.Id, second.Id])));
    Equal("BC-5", ManualLedger.OverlappingMarks(line, entered).Single().Issue);
});

static void CorrectThenMove()
{
    var retained = Row(0, 300, "BC-5");
    var moved = Row(300, 1200, "BC-5");
    var former = Timeline.Preview([retained, moved], RoundingMode.IssueDay).Single();
    var marked = Mark(former);
    var current = Timeline.Preview([retained, moved with { Task = moved.Task with { Issue = "BC-8" } }],
        RoundingMode.IssueDay);
    var source = current.Single(line => line.Issue == "BC-5");
    var target = current.Single(line => line.Issue == "BC-8");
    Equal(-900, ManualLedger.Check(source, RoundingMode.IssueDay, current, [marked]).Difference(source));
    True(ManualLedger.Check(target, RoundingMode.IssueDay, current, [marked]).NeedsReview);
    var corrected = Mark(source);
    True(ManualLedger.CanExport(target, RoundingMode.IssueDay, current, [corrected], new HashSet<string> { moved.Id }));
}

static ManualEntry Mark(Worklog line) => new("one", line.Issue, RoundingMode.IssueDay, line.ProposedSeconds,
    line.AllocationIds.ToArray(), line.Start, line.Intervals.ToArray(), line.Description);

static void DescriptionCorrection()
{
    var original = Row(0, 300, "BC-5");
    var former = Timeline.Preview([original], RoundingMode.IssueDay).Single();
    var current = Timeline.Preview([original with { Task = original.Task with { Description = "Updated note" } }],
        RoundingMode.IssueDay);
    var marked = Mark(former);
    var coverage = ManualLedger.Check(current[0], RoundingMode.IssueDay, current, [marked]);
    Equal(0, coverage.Difference(current[0]));
    True(coverage.DescriptionChanged(current[0]));
    True(!ManualLedger.CanExport(current[0], RoundingMode.IssueDay, current, [marked],
        new HashSet<string> { original.Id }));
}

static void StartCorrection()
{
    var original = Row(0, 300, "BC-5");
    var former = Timeline.Preview([original], RoundingMode.IssueDay).Single();
    var current = Timeline.Preview([original with { Start = original.Start.AddMinutes(30), End = original.End.AddMinutes(30) }],
        RoundingMode.IssueDay);
    var coverage = ManualLedger.Check(current[0], RoundingMode.IssueDay, current, [Mark(former)]);
    Equal(0, coverage.Difference(current[0]));
    True(coverage.StartChanged(current[0]));
}

static void ReducedSession()
{
    var first = Row(0, 600, "BC-5");
    var second = Row(600, 1200, "BC-5");
    var former = Timeline.Preview([first, second], RoundingMode.Session).Single();
    var old = Mark(former) with { Mode = RoundingMode.Session };
    var current = Timeline.Preview([first, second with { Task = second.Task with { Issue = "BC-8" } }],
        RoundingMode.Session);
    var retained = current.Single(line => line.Issue == "BC-5");
    var moved = current.Single(line => line.Issue == "BC-8");
    Equal(-900, ManualLedger.Check(retained, RoundingMode.Session, current, [old]).Difference(retained));
    True(ManualLedger.Check(moved, RoundingMode.Session, current, [old]).NeedsReview);
    True(ManualLedger.CanExport(moved, RoundingMode.Session, current, [Mark(retained) with { Mode = RoundingMode.Session }],
        new HashSet<string> { second.Id }));
}

static void ConsolidatedSessions() => WithStore(folder =>
{
    var left = Row(0, 300, "BC-5");
    var right = Row(360, 660, "BC-5");
    var interruption = Row(300, 360, "") with { Status = Disposition.Internal };
    var old = Timeline.Preview([left, interruption, right], RoundingMode.Session);
    var entered = old.Select((line, i) => Mark(line) with { Id = $"old-{i}", Mode = RoundingMode.Session }).ToArray();
    var current = Timeline.Preview([left, right], RoundingMode.Session);
    var merged = current.Single();
    var coverage = ManualLedger.Check(merged, RoundingMode.Session, current, entered);
    Equal(2, coverage.Consolidated!.Count);
    Equal(-900, coverage.Difference(merged));
    True(!ManualLedger.CanExport(merged, RoundingMode.Session, current, entered, merged.AllocationIds.ToHashSet()));
    Equal(0, ManualLedger.Orphaned(RoundingMode.Session, current, entered).Count);
    using var db = new WorkStore(folder);
    db.SaveManualEntries(Day(), entered);
    db.SaveManualEntries(Day(), [Mark(merged) with { Mode = RoundingMode.Session }],
        coverage.Consolidated.Select(entry => entry.Id).ToArray());
    Equal(900, db.ManualEntries(Day()).Single().Seconds);
});

static void ReciprocalCorrections() => WithStore(folder =>
{
    var first = Row(0, 600, "BC-5");
    var second = Row(600, 1200, "BC-8");
    var old = Timeline.Preview([first, second], RoundingMode.IssueDay)
        .Select((line, index) => Mark(line) with { Id = $"old-{index}" }).ToArray();
    var current = Timeline.Preview([first with { Task = first.Task with { Issue = "BC-8" } },
        second with { Task = second.Task with { Issue = "BC-5" } }], RoundingMode.IssueDay);
    True(current.All(line => ManualLedger.Check(line, RoundingMode.IssueDay, current, old).NeedsReview));
    True(ManualLedger.ReconcileIssueDayBatch(RoundingMode.IssueDay, [current[0]], old) == null);
    var matched = ManualLedger.ReconcileIssueDayBatch(RoundingMode.IssueDay, current, old)!;
    using var db = new WorkStore(folder);
    db.SaveManualEntries(Day(), old);
    db.SaveManualEntries(Day(), current.Select(line => Mark(line) with { Id = matched[line.Issue].Id }).ToArray());
    var recorded = db.ManualEntries(Day());
    True(current.All(line => ManualLedger.Check(line, RoundingMode.IssueDay, current, recorded).Difference(line) == 0));
});

static void ReviewableIdle()
{
    var origin = Origin();
    var gap = Timeline.Allocate(new WorkSpan(Guid.NewGuid(), origin, origin.AddMinutes(5),
        new CaptureSource("Idle gap", Kind: SpanKind.Idle)), TimeZoneInfo.Utc).Single();
    var switchTime = Timeline.Allocate(new WorkSpan(Guid.NewGuid(), origin.AddMinutes(6), origin.AddMinutes(6).AddSeconds(3),
        new CaptureSource("Browser")), TimeZoneInfo.Utc).Single();
    Equal(Disposition.Unassigned, gap.Status);
    Equal(SpanKind.Incidental, switchTime.Kind);
    Equal(Disposition.Excluded, switchTime.Status);
    Equal(0, Timeline.Preview([gap, switchTime], RoundingMode.Session).Count);
}

static void ManualBatchPersistence() => WithStore(folder =>
{
    var logs = Timeline.Preview([Row(0, 300, "BC-5"), Row(470, 800, "BC-8")], RoundingMode.IssueDay);
    using (var db = new WorkStore(folder))
    {
        db.RecordExport(Day(), logs);
        db.RecordExport(Day(), logs);
        db.SaveManualEntries(Day(), logs.Select((line, index) => Mark(line) with { Id = $"mark-{index}" }).ToArray());
    }
    using var reopened = new WorkStore(folder);
    Equal(2, reopened.ManualEntries(Day()).Count);
    var receipts = reopened.Exports(Day());
    Equal(2, receipts.Length);
    True(receipts.All(receipt => receipt.Intervals.Length == 1));
    reopened.RemoveManualEntry("mark-0");
    var remaining = reopened.ManualEntries(Day());
    True(ManualLedger.CanExport(logs[0], RoundingMode.IssueDay, logs, remaining,
        logs[0].AllocationIds.ToHashSet()));
});

static void NativeApproval() => WithStore(folder =>
{
    Exception? error = null;
    var thread = new Thread(() =>
    {
        try
        {
            using var app = new TrayApplication(folder, false, "native-approval", showOnStart: false, diagnostics: true);
            using var store = new WorkStore(folder);
            DemoData.Populate(store, app.Today, app.Zone);
            try
            {
                using var window = new ReviewWindow(app, store, openWithoutActivation: true)
                {
                    ShowInTaskbar = false, StartPosition = FormStartPosition.Manual,
                    Location = new System.Drawing.Point(SystemInformation.VirtualScreen.Right + 32, 0)
                };
                window.Show();
                Application.DoEvents();
                window.Reload();
                var tabs = Descendants(window).OfType<TabControl>().Single();
                var activity = Descendants(window).OfType<DataGridView>().Single(grid => grid.AccessibleName == "Daily activity");
                var approve = Descendants(window).OfType<Button>().Single(action => action.Text == "Approve");
                activity.ClearSelection();
                activity.CurrentCell = activity.Rows[0].Cells[0];
                activity.Rows[0].Selected = true;
                tabs.SelectedIndex = 1;
                True(!approve.Visible);
                approve.PerformClick();
                Equal(0, store.Rows(app.Today).Count(row => row.Approved));
                tabs.SelectedIndex = 0;
                True(approve.Visible);
                approve.PerformClick();
                Equal(1, store.Rows(app.Today).Count(row => row.Approved));
                var issue = (string)activity.Rows[0].Cells[4].Value!;
                activity.ClearSelection();
                foreach (DataGridViewRow row in activity.Rows)
                    if (StringComparer.Ordinal.Equals(row.Cells[4].Value as string, issue))
                        row.Selected = true;
                approve.PerformClick();
                window.Reload();
                var line = window.ApprovedWorklogs().Single();
                Equal(issue, line.Issue);
                var recomputed = line.ApprovalKey(RoundingMode.IssueDay);
                True(store.Rows(app.Today).Where(row => line.AllocationIds.Contains(row.Id))
                    .All(row => row.IsApprovedFor(RoundingMode.IssueDay, recomputed)));
                tabs.SelectedIndex = 1;
                var worklogs = Descendants(window).OfType<DataGridView>().Single(grid => grid.AccessibleName == "Proposed Tempo worklogs");
                True(worklogs.Rows.Cast<DataGridViewRow>().Any(row => Equals(row.Cells[0].Value, issue) &&
                    Equals(row.Cells[6].Value, "Approved")));
                True(window.RecentTasks().Any(task => task.Issue == issue));
                var approveLine = Descendants(window).OfType<Button>().Single(action => action.Text == "Approve line");
                True(approveLine.Visible);
                var other = worklogs.Rows.Cast<DataGridViewRow>().First(row => !Equals(row.Cells[0].Value, issue) &&
                    window.WorklogAt(row.Index) is Worklog);
                var otherLine = (Worklog)window.WorklogAt(other.Index)!;
                worklogs.ClearSelection();
                worklogs.CurrentCell = other.Cells[0];
                other.Selected = true;
                approveLine.PerformClick();
                var describedOther = !string.IsNullOrWhiteSpace(otherLine.Description);
                Equal(describedOther, window.ApprovedWorklogs().Any(candidate => candidate.Issue == otherLine.Issue));
                if (describedOther)
                {
                    var revoke = otherLine.AllocationIds.ToHashSet();
                    store.ReplaceRows([], store.Rows(app.Today).Where(row => revoke.Contains(row.Id))
                        .Select(row => row with { Approved = false }).ToArray());
                    window.Reload();
                }
                var protectedFile = Path.Combine(folder, "locked.csv");
                File.WriteAllText(protectedFile, "old");
                using (var locked = File.Open(protectedFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    try
                    {
                        window.WriteCsv(protectedFile, "new", app.Today, [line]);
                        throw new Exception("A locked destination was replaced.");
                    }
                    catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
                }
                Equal("old", File.ReadAllText(protectedFile));
                Equal(0, store.Exports(app.Today).Length);
                var copy = Descendants(window).OfType<Button>().Single(action => action.Text == "Copy worklog");
                var mark = Descendants(window).OfType<Button>().Single(action => action.Text == "Mark entered in Tempo");
                void SelectLine()
                {
                    worklogs.ClearSelection();
                    var row = worklogs.Rows.Cast<DataGridViewRow>().First(item => Equals(item.Cells[0].Value, issue));
                    worklogs.CurrentCell = row.Cells[0];
                    row.Selected = true;
                }
                Equal(DataGridViewClipboardCopyMode.Disable, worklogs.ClipboardCopyMode);
                var multi = worklogs.Rows.Cast<DataGridViewRow>().Where(row => window.WorklogAt(row.Index) is Worklog)
                    .Take(2).ToArray();
                worklogs.ClearSelection();
                foreach (var row in multi)
                    row.Selected = true;
                window.Reload();
                Equal(multi.Length, worklogs.SelectedRows.Count);
                var prompts = new List<string>();
                var clipboard = "";
                window.ClipboardOverride = text => clipboard = text;
                window.ConfirmOverride = text => { prompts.Add(text); return true; };
                SelectLine();
                copy.PerformClick();
                Equal(1, store.Exports(app.Today).Length);
                Equal(1, worklogs.SelectedRows.Count);
                True(clipboard.StartsWith(issue + "\t", StringComparison.Ordinal));
                worklogs.Focus();
                SelectLine();
                window.CopyField("duration");
                Equal(WorklogCsv.TempoDuration(line.ProposedSeconds), clipboard);
                window.ClipboardOverride = null;
                var real = "";
                for (var attempt = 0; attempt < 10 && real != issue; attempt++)
                {
                    window.CopyField("issue");
                    try { real = Clipboard.GetText(); }
                    catch (System.Runtime.InteropServices.ExternalException) { Thread.Sleep(50); }
                }
                Equal(issue, real);
                window.ClipboardOverride = text => clipboard = text;
                True(worklogs.Rows.Cast<DataGridViewRow>().Any(row => Equals(row.Cells[0].Value, issue) &&
                    Equals(row.Cells[6].Value, "Copied or exported · mark entered")));
                var extraStart = Timeline.DayStart(app.Today, app.Zone).AddHours(5);
                var extra = new Allocation(Guid.NewGuid().ToString("N"), app.Today, extraStart, extraStart.AddMinutes(20),
                    "Manual", SpanKind.Manual, Disposition.Billable, new TaskLabel("Client", issue, "Late time"));
                store.ReplaceRows([], [extra]);
                window.Reload();
                SelectLine();
                approveLine.PerformClick();
                True(worklogs.Rows.Cast<DataGridViewRow>().Any(row => Equals(row.Cells[0].Value, issue) &&
                    Equals(row.Cells[6].Value, "Changed since copy · copy again")));
                SelectLine();
                mark.PerformClick();
                Equal(0, store.ManualEntries(app.Today).Count);
                store.ReplaceRows([extra.Id], []);
                window.Reload();
                SelectLine();
                approveLine.PerformClick();
                True(worklogs.Rows.Cast<DataGridViewRow>().Any(row => Equals(row.Cells[0].Value, issue) &&
                    Equals(row.Cells[6].Value, "Copied or exported · mark entered")));
                string? prompt = null;
                window.ConfirmOverride = text => { prompt = text; return false; };
                SelectLine();
                mark.PerformClick();
                True(prompt != null && prompt.Contains(issue, StringComparison.Ordinal));
                Equal(0, store.ManualEntries(app.Today).Count);
                window.ConfirmOverride = _ => true;
                SelectLine();
                mark.PerformClick();
                var recorded = store.ManualEntries(app.Today).Single();
                Equal(line.ProposedSeconds, recorded.Seconds);
                True(worklogs.Rows.Cast<DataGridViewRow>().Any(row => Equals(row.Cells[0].Value, issue) &&
                    Equals(row.Cells[6].Value, $"Entered {line.ProposedSeconds / 60}m")));
                True(!window.ApprovedWorklogs().Any(candidate => candidate.Issue == issue));
                var start = Timeline.DayStart(app.Today, app.Zone).AddHours(6);
                store.ReplaceRows([], [new Allocation(Guid.NewGuid().ToString("N"), app.Today, start, start.AddMinutes(5),
                    "Manual", SpanKind.Manual, Disposition.Billable, new TaskLabel("Client", issue, "New time"))]);
                True(!window.VerifyUnchanged(app.Today, RoundingMode.IssueDay, [line], newHandoff: true));
                True(worklogs.Rows.Cast<DataGridViewRow>().Any(row => Equals(row.Cells[0].Value, issue) &&
                    (row.Cells[6].Value as string)?.StartsWith("Approve first", StringComparison.Ordinal) == true));
                SelectLine();
                approveLine.PerformClick();
                SelectLine();
                prompts.Clear();
                copy.PerformClick();
                Equal(1, store.CorrectionCopies(app.Today).Length);
                True(worklogs.Rows.Cast<DataGridViewRow>().Any(row => Equals(row.Cells[0].Value, issue) &&
                    (row.Cells[6].Value as string)?.EndsWith("· copied, mark entered", StringComparison.Ordinal) == true));
                window.ConfirmOverride = text => { prompts.Add(text); return false; };
                clipboard = "unchanged";
                SelectLine();
                copy.PerformClick();
                True(prompts.Any(text => text.Contains("already copied", StringComparison.Ordinal)));
                Equal("unchanged", clipboard);
            }
            finally { app.ShutDownDiagnostics(); }
        }
        catch (Exception observed) { error = observed; }
    }) { IsBackground = true };
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    if (!thread.Join(TimeSpan.FromSeconds(15)))
        throw new TimeoutException("Native approval smoke did not finish.");
    if (error != null)
        throw new InvalidOperationException(error.Message, error);

    static IEnumerable<Control> Descendants(Control control)
    {
        foreach (Control child in control.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child))
                yield return descendant;
        }
    }
});

static void JiraScoped()
{
    var settings = JiraSettings.Create("https://company.atlassian.net/", "demo@example.com",
        "5735e993-9d99-4337-b5d5-d828c7bef808", "BC, OPS", "Waiting for Customer");
    using var handler = new JiraHandler(request =>
    {
        Equal("api.atlassian.com", request.RequestUri!.Host);
        Equal("/ex/jira/5735e993-9d99-4337-b5d5-d828c7bef808/rest/api/3/myself", request.RequestUri.AbsolutePath);
        Equal("Basic", request.Headers.Authorization!.Scheme);
        return JiraHandler.Json("""{"accountId":"test-account"}""");
    });
    using var catalog = new JiraCatalog(settings, "fake-token", handler);
    Equal("test-account", catalog.AccountAsync(CancellationToken.None).GetAwaiter().GetResult());
}

static void JiraSearch()
{
    var settings = JiraSettings.Create("https://company.atlassian.net", "demo@example.com", "", "BC", "Waiting for Customer");
    using var handler = new JiraHandler(request =>
    {
        var query = Uri.UnescapeDataString(request.RequestUri!.Query);
        True(query.Contains("project in (\"BC\")"));
        True(query.Contains("status = \"Waiting for Customer\""));
        True(query.Contains("nextPageToken=known/page"));
        True(!query.Contains("assignee = currentUser()"));
        return JiraHandler.Json("""
            {"issues":[{"key":"BC-55","fields":{"summary":"Restore","status":{"name":"Waiting for Customer"}}}],
             "nextPageToken":"second"}
            """);
    });
    using var catalog = new JiraCatalog(settings, "fake-token", handler);
    var result = catalog.SearchAsync(new JiraQuery("restore", false, true, "known/page"), CancellationToken.None)
        .GetAwaiter().GetResult();
    Equal("BC-55", result.Issues.Single().Key);
    Equal("second", result.Next);
}

static void JiraRejectsRedirect()
{
    var settings = JiraSettings.Create("https://company.atlassian.net", "demo@example.com", "", "", "Waiting for Customer");
    var requests = 0;
    using var handler = new JiraHandler(_ =>
    {
        requests++;
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.Redirect);
        response.Headers.Location = new Uri("https://example.com/");
        return response;
    });
    using var catalog = new JiraCatalog(settings, "fake-token", handler);
    try { _ = catalog.AccountAsync(CancellationToken.None).GetAwaiter().GetResult(); }
    catch (InvalidOperationException) { }
    Equal(1, requests);
    try { _ = JiraSettings.Create("https://company.atlassian.net.example.com", "demo@example.com", "", "", "Waiting"); }
    catch (ArgumentException) { return; }
    throw new Exception("An untrusted Jira origin was accepted.");
}

static void WithStore(Action<string> action)
{
    var folder = Directory.CreateTempSubdirectory("WorklogDesk-Test-");
    try { action(folder.FullName); }
    finally { folder.Delete(true); }
}

static DateOnly Day() => new(2026, 1, 1);
static DateTimeOffset Origin() => Timeline.DayStart(Day(), TimeZoneInfo.Utc);
static Allocation Row(int start, int end, string issue) =>
    new(Guid.NewGuid().ToString("N"), Day(), Origin().AddSeconds(start), Origin().AddSeconds(end), "VS Code",
        SpanKind.Foreground, issue.Length > 0 ? Disposition.Billable : Disposition.Unassigned, new TaskLabel("Client", issue));

static void Observe(CaptureEngine core, long tick, long input, CaptureSource? source) =>
    core.Observe(new CaptureObservation(tick, Origin().AddMilliseconds(tick), input, source));
static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"Expected {expected}; got {actual}.");
}
static void True(bool value, [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
{
    if (!value)
        throw new Exception($"Expected condition to hold (line {line})."); 
}

sealed class JiraHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(response(request));

    public static HttpResponseMessage Json(string json) => new(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };
}
