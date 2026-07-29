namespace CodexSync.App;

public sealed class MainForm : Form
{
    private readonly TextBox _firstSource = CreatePathTextBox();
    private readonly TextBox _secondSource = CreatePathTextBox();
    private readonly TextBox _output = CreatePathTextBox();
    private readonly TextBox _sqlite = CreatePathTextBox();
    private readonly Button _mergeButton = new() { Text = "Fusionar en carpeta de salida", AutoSize = true, Padding = new Padding(12, 6, 12, 6) };
    private readonly RichTextBox _log = new() { ReadOnly = true, BackColor = SystemColors.Window, BorderStyle = BorderStyle.FixedSingle, Dock = DockStyle.Fill, Font = new Font("Consolas", 9F) };
    private readonly ProgressBar _progress = new() { Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 28, Visible = false, Dock = DockStyle.Fill };

    public MainForm()
    {
        Text = "CodexSync — Fusión de historiales";
        MinimumSize = new Size(800, 580);
        StartPosition = FormStartPosition.CenterScreen;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            ColumnCount = 3,
            RowCount = 9,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 115));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        layout.Controls.Add(new Label { Text = "Fusión local de historiales Codex", Font = new Font(Font, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 0, 0, 10) }, 0, 0);
        layout.SetColumnSpan(layout.GetControlFromPosition(0, 0)!, 3);
        AddPathRow(layout, 1, "Carpeta .codex A", _firstSource, SelectFolder);
        AddPathRow(layout, 2, "Carpeta .codex B", _secondSource, SelectFolder);
        AddPathRow(layout, 3, "Carpeta de salida", _output, SelectFolder);
        AddPathRow(layout, 4, "sqlite3.exe (opcional)", _sqlite, SelectSqlite);

        var hint = new Label
        {
            Text = "La salida debe ser una carpeta vacía. Se fusionan sessions/, archived_sessions/, session_index.jsonl y state_5.sqlite. Las carpetas originales nunca se modifican.",
            AutoSize = true,
            MaximumSize = new Size(900, 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 10, 0, 10),
        };
        layout.Controls.Add(hint, 0, 5);
        layout.SetColumnSpan(hint, 3);

        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
        actions.Controls.Add(_mergeButton);
        layout.Controls.Add(actions, 0, 6);
        layout.SetColumnSpan(actions, 3);

        layout.Controls.Add(_log, 0, 7);
        layout.SetColumnSpan(_log, 3);
        layout.Controls.Add(_progress, 0, 8);
        layout.SetColumnSpan(_progress, 3);
        Controls.Add(layout);

        _mergeButton.Click += MergeButton_Click;
    }

    private static TextBox CreatePathTextBox() => new() { Dock = DockStyle.Fill, Margin = new Padding(3), AllowDrop = true };

    private void AddPathRow(TableLayoutPanel layout, int row, string label, TextBox textBox, EventHandler browseHandler)
    {
        layout.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 8, 8) }, 0, row);
        layout.Controls.Add(textBox, 1, row);
        var button = new Button { Text = "Examinar…", AutoSize = true, Anchor = AnchorStyles.Right };
        button.Click += browseHandler;
        layout.Controls.Add(button, 2, row);
    }

    private void SelectFolder(object? sender, EventArgs e)
    {
        var target = ReferenceEquals(sender, GetButtonAt(1)) ? _firstSource
            : ReferenceEquals(sender, GetButtonAt(2)) ? _secondSource : _output;
        using var dialog = new FolderBrowserDialog { Description = "Seleccione una carpeta .codex o una carpeta de salida" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            target.Text = dialog.SelectedPath;
    }

    private Button? GetButtonAt(int row) => Controls.OfType<TableLayoutPanel>().Single().GetControlFromPosition(2, row) as Button;

    private void SelectSqlite(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog { Filter = "SQLite CLI (sqlite3.exe)|sqlite3.exe|Ejecutables (*.exe)|*.exe|Todos los archivos (*.*)|*.*", Title = "Seleccione sqlite3.exe" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            _sqlite.Text = dialog.FileName;
    }

    private async void MergeButton_Click(object? sender, EventArgs e)
    {
        _log.Clear();
        SetBusy(true);
        try
        {
            var merger = new CodexHistoryMerger(AppendLog);
            await merger.MergeAsync(_firstSource.Text, _secondSource.Text, _output.Text, _sqlite.Text, CancellationToken.None);
            AppendLog("\r\nFusión terminada correctamente.");
            MessageBox.Show(this, "La carpeta de salida está lista para usarse como .codex.", "CodexSync", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            AppendLog($"\r\nERROR: {ex.Message}");
            MessageBox.Show(this, ex.Message, "No se pudo fusionar", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _mergeButton.Enabled = !busy;
        _progress.Visible = busy;
        UseWaitCursor = busy;
    }

    private void AppendLog(string message)
    {
        if (InvokeRequired) { BeginInvoke(() => AppendLog(message)); return; }
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\r\n");
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }
}
