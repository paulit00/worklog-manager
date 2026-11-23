namespace WorklogDesk.Windows;

/// <summary>Saves a Tempo API token after a read-only check that it can see the user's own worklogs.</summary>
internal sealed class TempoConfigDialog : Form
{
    private readonly TextBox token = new() { MaxLength = 2048, UseSystemPasswordChar = true, Width = 360 };
    private readonly Label notice = new() { AutoSize = true, MaximumSize = new Size(520, 0) };
    private readonly CancellationTokenSource cancellation = new();
    private readonly JiraSettings jira;
    private readonly Func<JiraCatalog> openJira;
    private readonly Button connect;

    public bool Connected { get; private set; }

    public TempoConfigDialog(JiraSettings jira, bool connected, Func<JiraCatalog> openJira)
    {
        this.jira = jira;
        this.openJira = openJira;
        Connected = connected;
        Text = "Connect Tempo";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = MaximizeBox = ShowInTaskbar = false;
        Font = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Palette.Surface;
        ForeColor = Palette.Text;
        Palette.TitleBar(this);
        ClientSize = new Size(560, 250);
        var layout = Ui.DialogLayout();
        Controls.Add(layout);
        var help = new Label
        {
            AutoSize = true, MaximumSize = new Size(520, 0),
            Text = "In Jira, open Apps → Tempo → Settings → API integration → New token. Give it Worklogs " +
                "view and manage access. The token is kept in Windows Credential Manager."
        };
        layout.Controls.Add(help, 0, layout.RowCount++);
        layout.SetColumnSpan(help, 2);
        token.PlaceholderText = connected ? "Leave blank to keep the saved token" : "Paste the Tempo API token";
        Ui.Field(layout, "Tempo API token", token);
        notice.ForeColor = Palette.Muted;
        notice.Text = connected ? "Tempo is connected." : "The check only reads today's worklogs. Nothing is written.";
        layout.Controls.Add(notice, 0, layout.RowCount++);
        layout.SetColumnSpan(notice, 2);
        var actions = Ui.Actions(layout);
        var cancel = Ui.Button("Cancel", (_, _) => Close(), 85);
        cancel.DialogResult = DialogResult.Cancel;
        CancelButton = cancel;
        actions.Controls.Add(cancel);
        if (connected)
            actions.Controls.Add(Ui.Button("Disconnect", (_, _) => { Connected = false; DialogResult = DialogResult.OK; }, 100));
        connect = Ui.Button("Test and save", async (_, _) => await Connect(), 116);
        Ui.Primary(connect);
        AcceptButton = connect;
        actions.Controls.Add(connect);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        cancellation.Cancel();
        cancellation.Dispose();
        base.OnFormClosed(e);
    }

    private async Task Connect()
    {
        connect.Enabled = false;
        notice.Text = "Checking Tempo…";
        try
        {
            var provided = token.Text.Trim();
            var secret = provided.Length > 0 ? provided : CredentialVault.Read(jira.TempoCredentialTarget);
            if (string.IsNullOrEmpty(secret))
                throw new ArgumentException("Paste the Tempo API token.");
            var cancel = cancellation.Token;
            using var catalog = openJira();
            using var tempo = new TempoClient(secret);
            var account = await catalog.AccountAsync(cancel);
            var today = await tempo.DayAsync(account, DateOnly.FromDateTime(DateTime.Today), cancel);
            if (IsDisposed || cancel.IsCancellationRequested)
                return;
            if (provided.Length > 0)
                CredentialVault.Write(jira.TempoCredentialTarget, jira.Email, provided);
            token.Clear();
            Connected = true;
            MessageBox.Show(this, $"Tempo is connected. It shows {today.Count} worklog(s) for you today.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            DialogResult = DialogResult.OK;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or HttpRequestException or
            TaskCanceledException or System.Text.Json.JsonException or KeyNotFoundException or InvalidDataException)
        {
            if (!IsDisposed)
                notice.Text = error is TaskCanceledException ? "Tempo did not respond in time." : error.Message;
        }
        finally
        {
            if (!IsDisposed)
                connect.Enabled = true;
        }
    }
}
