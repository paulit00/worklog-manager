# Worklog Desk

Worklog Desk is a Windows tray app for reviewing Business Central DevOps time before entering it in Tempo. It captures opted-in app activity, keeps ambiguous intervals visible, and previews 15-minute worklogs.

## Run

Use the Windows **.NET 10 Desktop Runtime**. From the project folder:

```powershell
dotnet build WorklogDesk.slnx -c Release
dotnet run --project src/WorklogDesk.Windows -c Release --no-build -- --demo
```

The demo uses synthetic activity and a local fake Jira catalog. Remove `--demo` for a personal workspace. On first launch, open **Settings** to choose sources and set your daily target, then select **Start foreground**. **Pause foreground** leaves an explicitly started manual timer running. Closing the review keeps the tray running. Use **Exit Worklog Desk** in the tray to stop the app.

## Automatic attribution

With **Read window titles** on (Settings → Automatic, on by default), you rarely need to pin:

- A Jira key in the active window title assigns that time to the issue. Examples are a Jira tab `[BC-123] Restore failed`, a VS Code branch `feature/BC-123-…`, a terminal title, or an Outlook subject `RE: BC-123 …` (with Outlook capture on). For a Jira tab, the issue summary becomes the description. If **Connect Jira** lists project keys, only those projects count. Otherwise, look-alikes such as `UTF-8` or `SHA-256` are ignored. Remote Desktop titles are host names and never count as keys.
- Each activity row shows what was open, for example `Remote Desktop · CONTOSO-BC01`. Window titles are stored encrypted with your Windows account and removed after 30 days.
- Time without a key (an RDP session, a terminal) gets a **Suggested** issue from the nearest detected issue within 30 minutes, if the client doesn't conflict. **Accept suggestions** assigns all of them, or only the selected rows. You still approve before anything is copied to Tempo.
- Mappings in Settings still help: an RDP host or VS Code workspace alias sets the client, and suggestions never cross clients.

Pinning remains available for long stretches where no window shows a key.

## The review window

The review is dark by default; change **Appearance** in Settings. Three views sit above the grid: **Activity**, **Worklogs** and **Requests**.

- **Activity:** select rows, then press **Enter** or double-click, or use **Assign…**. The assign window lists suggested and recent issues and your own open and Waiting for Customer Jira issues, loaded when it opens and cached for 10 minutes. Typing filters them and searches all of Jira, including colleagues' issues. You can also type any key directly. **Internal** and **Exclude** are in the same window. Every selected row is assigned, including rows marked *Check timing*.
- **Undo** (Ctrl+Z) reverses the last change in the review: assignment, accepted suggestions, approval, classification, edits, split or merge.
- **More ▾** holds the rarer actions: Edit times (F2), Split, Merge and Add time.

## Review a day

1. Start with **Accept suggestions**, then use **Assign…** for whatever is left. Pinning is optional: pin a task before working across the browser, VS Code and shared RDP. Use **Assign last RDP span** from the tray if you forgot the pin. Browser timing is opt-in and can use a configured Edge title alias where available. In **Settings**, an alias maps a workspace, RDP connection or Edge profile to a client. You can also add an optional standing Jira issue, so that time arrives pre-assigned but still needs approval.
2. When a colleague asks for something in Teams or Outlook and there's no ticket yet, use **Note a request** (tray, Ctrl+Shift+Alt+N, or **New request** in the Requests view). It saves a one-line note and an optional link, so you can later link it to a Jira issue or open Jira's create form. Start the manual timer for calls or work away from the keyboard.
3. Assign activity to the right Jira issue, or mark it Internal or Excluded, in the **Assign…** window. In the Requests view, **Link to Jira…** attaches a saved request to an issue after you create the ticket.
4. Review actual versus proposed time using **Once per issue/day** and **Each work session**. Choose the mode that matches your company's policy. Approve rows on **Activity**, or select lines on **Worklogs** and press **Enter** (**Approve line**). Then use **Ctrl+C** (**Copy for Tempo**, one line per worklog when several are selected) or **Export approved CSV** for Tempo entry. Copied or exported lines show *Copied or exported · mark entered*. After the first copy, **Ctrl+1** to **Ctrl+4** copy the issue key, duration (for example `1h 45m`), description or start time one at a time, for pasting into separate Tempo fields. After entering them in Tempo, keep them selected and press **Ctrl+M** (**Mark entered**). The selection stays in place between these steps. Changes to an entered line appear as adjustments and stay out of new exports.
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
