# Worklog Desk

Worklog Desk is a Windows tray app for reviewing Business Central DevOps time before entering it in Tempo. It captures opted-in app activity, keeps ambiguous intervals visible, and previews 15-minute worklogs.

## Run

Use the Windows **.NET 10 Desktop Runtime**. The app bundles the Windows App SDK components used for notifications. Keep the build or publish files together when moving the executable. From the project folder:

```powershell
dotnet build WorklogDesk.slnx -c Release
dotnet run --project src/WorklogDesk.Windows -c Release --no-build -- --demo
```

The demo uses synthetic activity and a local fake Jira catalog. Remove `--demo` for a personal workspace. On first launch, open **Settings** to choose sources and set your daily target, then select **Start recording**. The choice is remembered across restarts. **Pause recording** leaves an explicitly started manual timer running. Tick **Start Worklog Desk when I sign in to Windows** in Settings to have it start with Windows. It registers the exe's current location, so keep the app in one folder. Closing the review keeps the tray running. Use **Exit Worklog Desk** in the tray to stop the app.

## Automatic attribution

With **Read window titles** on (Settings → Automatic, on by default), you rarely need to pin:

- A Jira key in the active window title assigns that time to the issue. Examples are a Jira tab `[BC-123] Restore failed`, a VS Code branch `feature/BC-123-…`, a terminal title, or an Outlook subject `RE: BC-123 …` (with Outlook capture on). For a Jira tab, the issue summary becomes the description. If **Connect Jira** lists project keys, only those projects count. Otherwise, look-alikes such as `UTF-8` or `SHA-256` are ignored. Remote Desktop titles are host names and never count as keys.
- Each activity row shows what was open, for example `Remote Desktop · CONTOSO-BC01`. Window titles are stored encrypted with your Windows account and removed after 30 days.
- Time without a key (an RDP session, a terminal) gets a **Suggested** issue from the nearest detected issue within 30 minutes, if the client doesn't conflict. **Accept suggestions** assigns all of them, or only the selected rows. You still approve before anything is copied to Tempo.
- Mappings in Settings still help: an RDP host or VS Code workspace alias sets the client, and suggestions never cross clients.

Pinning remains available for long stretches where no window shows a key.

## The review window

The review is dark by default; change **Appearance** in Settings. Two views sit above the grid: **Activity** and **Worklogs**.

- **Activity:** select rows, then press **Enter** or double-click, or use **Assign…**. The assign window lists suggested and recent issues and your own open and Waiting for Customer Jira issues, loaded when it opens and cached for 10 minutes. Typing filters them and searches all of Jira, including colleagues' issues. You can also type any key directly. **Internal** and **Exclude** are in the same window. Every selected row is assigned, including rows marked *Check timing*.
- **Undo** (Ctrl+Z) reverses the last change in the review: assignment, accepted suggestions, approval, classification, edits, split or merge.
- **More ▾** holds the rarer actions: Edit times (F2), Split, Merge and Add time.

## Review a day

1. Start with **Accept suggestions**, then use **Assign…** for whatever is left. Pinning is optional: pin a task before working across the browser, VS Code and shared RDP. Use **Assign last RDP span** from the tray if you forgot the pin. Browser timing is opt-in and can use a configured Edge title alias where available. In **Settings**, an alias maps a workspace, RDP connection or Edge profile to a client. You can also add an optional standing Jira issue, so that time arrives pre-assigned but still needs approval.
2. When work has no ticket, create one with **New Jira task…**. It's in the Assign window, and in the tray or with Ctrl+Shift+Alt+N when a colleague asks for something. Search for a project, pick the type (Task by default), enter a summary, and the issue is created in Jira, assigned to you. From the Assign window, the selected time is assigned to the new issue straight away. From the tray, the new key goes on your clipboard. Start the manual timer for calls or work away from the keyboard.
3. Assign activity to the right Jira issue, or mark it Internal or Excluded, in the **Assign…** window.
4. Review actual versus proposed time using **Once per issue/day** and **Each work session**. Choose the mode that matches your company's policy. Approve rows on **Activity**, or select lines on **Worklogs** and press **Enter** (**Approve line**). Then use **Ctrl+C** (**Copy for Tempo**, one line per worklog when several are selected) or **Export approved CSV** for Tempo entry. Copied or exported lines show *Copied or exported · mark entered*. After the first copy, **Ctrl+1** to **Ctrl+4** copy the issue key, duration (for example `1h 45m`), description or start time one at a time, for pasting into separate Tempo fields. After entering them in Tempo, keep them selected and press **Ctrl+M** (**Mark entered**). The selection stays in place between these steps. Changes to an entered line appear as adjustments and stay out of new exports.
   If an older local mark has no time range and blocks the whole day, open **Manage local Tempo entries** (right-click in Worklogs) and choose **Keep as separate Tempo entry** or **Record removal from Tempo**.

Default tray shortcuts are **Ctrl+Shift+Alt+L** for review, **Ctrl+Shift+Alt+P** for an optional pin, and **Ctrl+Shift+Alt+N** for a new Jira task.

Idle gaps appear in **Needs review only**. Assign reading or other passive work, or exclude breaks. After an idle or lock interruption, confirm the saved pin before using it across apps again.

## Send to Tempo

1. Connect Jira first. Tempo uses it to find your account and the issue IDs.
2. Select **Connect Tempo** and paste a Tempo API token. In Jira, open **Apps → Tempo → Settings → API integration → New token**, with Worklogs view and manage access. **Test and save** makes a read-only check of today's worklogs. The token goes to Windows Credential Manager.
3. In **Worklogs**, approve the lines and select **Send to Tempo**. It sends the selected approved lines, or all new approved lines if none are selected. Each one goes out with the issue, date, start time, rounded duration and description, after a confirmation that lists them. The rounding preview selected at the bottom decides what is sent.

Before sending, the app reads that day's worklogs from Tempo:

- A worklog that already exists identically is recorded as entered, not sent again.
- If Tempo already has time on the same issue that the app didn't send or record, it asks before adding another worklog.
- If a send times out or the reply is lost, the line shows *Send unconfirmed*. **Check Tempo status** can reconcile a matching receipt. **Send to Tempo** also checks for a match before offering a retry.
- A refusal, such as a locked period or missing permission, is shown with Tempo's reason and leaves the line ready to retry.

Sent lines are marked entered automatically. The app only creates worklogs. Corrections to time already in Tempo are still copied with **Copy for Tempo** and applied by hand.

## Finish today’s worklog

Open **Finish today’s worklog** from the tray to see what is verified in Tempo, manually attested, ready or still open. The same window lists earlier unfinished dates. Review opens the selected workday and the relevant Activity or Worklogs view. Use **Check Tempo status** on Worklogs to look for an earlier receipt without sending anything. **Send to Tempo** still asks for confirmation.

Use **Reminder settings** to opt in and choose workdays and a time in the existing workday time zone. There is one Windows cue for actionable work and one optional 30-minute snooze. After the banner disappears, its action remains in Windows Notification Center until it expires or you exit Worklog Desk. Use **Skip today** for a day off. The tray summary is available when Windows suppresses the cue or after it expires.

In **Pilot and check-in**, choose the fixed cutoff before starting. It defaults to 18:00 in the workday time zone. The pilot starts with five worked days without cues and continues with ten worked days in the reminder period. Check in for each date, including days when you worked but capture was empty. Results stay on this PC and show cutoff closure, missing observations and verified versus manually attested entries separately. A Windows cue request records an attempt to notify. The app tracks review opens separately.

## Reconstruct my day

**Reconstruct my day…** in the review window sends the shown date's activity, in time order, to Claude or OpenAI in one request. It sees what came before and after each piece of work. It suggests tasks: a time range → a Jira issue, a draft for a new issue, or "needs your decision".

- **Setup:** choose a provider and model, then use **API key…** (the key goes to Windows Credential Manager). For meetings, use **Connect Outlook…** with a tenant-approved Entra public client. Meetings need `Calendars.ReadBasic`; Sent Items subjects are optional and need `Mail.ReadBasic`.
- **What is sent:** window titles by default, with Teams chat names counted first, because they are people's names. Mail stays local: Outlook titles, Outlook on the web, mail-like browser titles and Sent Items subjects are sent only when you include them for that request. Excluded, idle and internal time never sends titles. Browser titles recorded before this version are held back until Outlook is connected. Links, email addresses, phone numbers and secrets are removed first.
- **The preview:** it lists every title by family. You can turn a family off (remembered) or untick single titles (this request only). **Prepare request** fetches your recent Jira issues: only keys and JQL go to Jira, never window text. **Show exact request** shows the body that **Send** transmits.
- **Review:** untick episodes that belong elsewhere. Then **Accept selected…** shows the recorded time and the Tempo time per issue after rounding, and asks for a description so the worklogs can be approved. Small or heavily rounded worklogs need **Accept anyway**. One accept is one undo step.
- **Blocked proposals:** issues that already have approved, copied or sent time today can't be changed here. **Open in Activity** handles those through the normal Assign flow. So does idle meeting time. Date navigation is locked while the window is open.
- **Saved for later:** each reconstruction is kept for its date and part of the day (whole day, morning or afternoon), with your edits, unticks and accepts. Opening the window again brings back the most recent one. Switching the part of the day shows that part's saved reconstruction. Changed activity marks the affected proposal stale, which prevents accepting it or using it to start an assignment in Activity. Saved reconstructions are encrypted and expire 30 days after generation. The AI's reasons and drafts, meeting subjects and Jira summaries can remain in a saved reconstruction until then, including after the underlying activity is deleted.
- **Diagnostics:** `ai-diagnostics.log` in the data folder records each request's stop reason, token counts and duration, without content.

For other AI assistants, `WorklogDesk.Windows.exe --mcp YYYY-MM-DD [--data FOLDER] [--include-context]` serves two read-only tools over stdio. `worklog_activity` lists unresolved rows; `worklog_episodes` gives the whole day's timeline. Titles appear only with `--include-context`, under the same rules, and that now includes titles of assigned time.

## Connect Jira Cloud

Issue lookup is optional. In **Connect Jira**, enter your `.atlassian.net` site, Atlassian email, an API token allowed by your organization, and project keys. Use **Cloud ID** for a scoped token. Select **Test and save** to make a read-only account check. The token goes to Windows Credential Manager. [Atlassian API token settings and guidance](https://support.atlassian.com/atlassian-account/docs/manage-api-tokens-for-your-atlassian-account)

Teams and Outlook use pasted links. Automatic ticket creation, host-aware browser integration and Graph imports are later increments in [SCAFFOLD.md](SCAFFOLD.md).

## Verify

```powershell
dotnet run --project tests/WorklogDesk.Tests -c Release
pwsh -File scripts/Measure-WorklogDesk.ps1
```

The test runner uses local SQLite and fake Jira responses. The measurement script uses a separate synthetic store, captures the review screen, and records a 60-second idle sample after a 60-second warm-up. `-SkipIdle` runs only the synthetic workload and UI snapshot.
