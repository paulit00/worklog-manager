# Worklog Desk

Worklog Desk is a Windows tray app for reviewing Business Central DevOps time before entering it in Tempo. It captures opted-in app activity, keeps ambiguous intervals visible, and previews 15-minute worklogs.

## Run

Use the Windows **.NET 10 Desktop Runtime**. From the project folder:

```powershell
dotnet build WorklogDesk.slnx -c Release
dotnet run --project src/WorklogDesk.Windows -c Release --no-build -- --demo
```

The demo uses synthetic activity and a local fake Jira catalog. Remove `--demo` for a personal workspace. On first launch, open **Settings** to choose sources and set your daily target, then select **Start foreground**. **Pause foreground** leaves an explicitly started manual timer running. Closing the review keeps the tray running. Use **Exit Worklog Desk** in the tray to stop the app.

## Review a day

1. **Assign selected** offers a *Recent task* list (pin, today's issues and mapped standing issues), so repeat work needs no retyping. Pin a task before working across the browser, VS Code and shared RDP. Use **Assign last RDP span** from the tray if you forgot the pin. Browser timing is opt-in and can use a configured Edge title alias where available. In **Settings**, an alias maps a workspace, RDP connection or Edge profile to a client. You can also add an optional standing Jira issue, so that time arrives pre-assigned but still needs approval.
2. Use **Capture request** for an informal Teams or Outlook request. Paste a link or enter a note. Start the manual timer for calls or work away from the keyboard.
3. Assign activity to the right Jira issue or use **Classify** to mark selected time Internal or Excluded. **Find Jira** can look up an exact key or search configured projects, including issues in Waiting for Customer. A saved request can be linked to a Jira key after creating the ticket in Jira.
4. Review actual versus proposed time using **Once per issue/day** and **Each work session**. Choose the mode that matches your company's policy. Approve rows on **Activity**, or select lines on **Worklogs** and press **Enter** (**Approve line**). Then use **Ctrl+C** (**Copy worklog**, one line per worklog when several are selected) or **Export approved CSV** for Tempo entry. Copied or exported lines show *Copied or exported · mark entered*. After the first copy, **Ctrl+1** to **Ctrl+4** copy the issue key, duration (for example `1h 45m`), description or start time one at a time, for pasting into separate Tempo fields. After entering them in Tempo, keep them selected and press **Ctrl+M** (**Mark entered in Tempo**). The selection stays in place between these steps. Changes to an entered line appear as adjustments and stay out of new exports.
   If an older local mark has no time range and blocks the whole day, open **Manage local Tempo entries** (right-click in Worklogs) and choose **Keep as separate Tempo entry** or **Record removal from Tempo**.

Default tray shortcuts are **Ctrl+Shift+Alt+L** for review, **Ctrl+Shift+Alt+P** for a pin, and **Ctrl+Shift+Alt+N** to capture a request.

Idle gaps appear in **Needs review only**. Assign reading or other passive work, or exclude breaks. After an idle or lock interruption, confirm the saved pin before using it across apps again.

## Connect Jira Cloud

Issue lookup is optional. In **Connect Jira**, enter your `.atlassian.net` site, Atlassian email, an API token allowed by your organization, and project keys. Use **Cloud ID** for a scoped token. Select **Test and save** to make a read-only account check. The token goes to Windows Credential Manager. [Atlassian API token settings and guidance](https://support.atlassian.com/atlassian-account/docs/manage-api-tokens-for-your-atlassian-account)

This increment uses manual Tempo entry and pasted Teams/Outlook links. Live Tempo posting, automatic ticket creation, host-aware browser integration and Graph imports are later increments in [SCAFFOLD.md](SCAFFOLD.md).

## Verify

```powershell
dotnet run --project tests/WorklogDesk.Tests -c Release
pwsh -File scripts/Measure-WorklogDesk.ps1
```

The test runner uses local SQLite and fake Jira responses. The measurement script uses a separate synthetic store, captures the review screen, and records a 60-second idle sample after a 60-second warm-up. `-SkipIdle` runs only the synthetic workload and UI snapshot.
