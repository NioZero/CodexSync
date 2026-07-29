namespace CodexSync.App;

public sealed class ConversationAnalysisDialog : Form
{
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = false,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false,
        RowHeadersVisible = false,
    };

    public ConversationAnalysisDialog(HistoryMergePlan plan)
    {
        Text = "CodexSync — Análisis previo";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(1000, 560);
        Size = new Size(1180, 720);

        var conflicts = plan.Entries.Count(entry => entry.Status == ConversationStatus.Conflict);
        var summary = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            Padding = new Padding(14, 14, 14, 8),
            Text = $"{plan.Entries.Count} sesiones detectadas · {plan.Entries.Count(entry => entry.Status == ConversationStatus.NewInA)} nuevas en A · {plan.Entries.Count(entry => entry.Status == ConversationStatus.NewInB)} nuevas en B · {conflicts} conflictos.\r\nEn cada conflicto se preselecciona el archivo con fecha de modificación más reciente; puede cambiarlo antes de continuar.",
        };
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 56, Padding = new Padding(10), FlowDirection = FlowDirection.RightToLeft };
        var continueButton = new Button { Text = "Usar estas decisiones", DialogResult = DialogResult.OK, AutoSize = true, Padding = new Padding(10, 5, 10, 5) };
        var cancelButton = new Button { Text = "Cancelar", DialogResult = DialogResult.Cancel, AutoSize = true, Padding = new Padding(10, 5, 10, 5) };
        footer.Controls.Add(continueButton);
        footer.Controls.Add(cancelButton);
        Controls.Add(_grid);
        Controls.Add(footer);
        Controls.Add(summary);
        AcceptButton = continueButton;
        CancelButton = cancelButton;

        ConfigureColumns();
        AddRows(plan.Entries);
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _grid.CellValueChanged += ChoiceChanged;
    }

    private void ConfigureColumns()
    {
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Folder", HeaderText = "Ubicación", ReadOnly = true, Width = 140 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Session", HeaderText = "Sesión / archivo", ReadOnly = true, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 260 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Status", HeaderText = "Resultado", ReadOnly = true, Width = 175 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "FirstModified", HeaderText = "Modificada en A", ReadOnly = true, Width = 145 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "SecondModified", HeaderText = "Modificada en B", ReadOnly = true, Width = 145 });
        _grid.Columns.Add(new DataGridViewComboBoxColumn { Name = "Choice", HeaderText = "Conservar", Width = 125, FlatStyle = FlatStyle.Flat });
    }

    private void AddRows(IEnumerable<ConversationEntry> entries)
    {
        foreach (var entry in entries.OrderByDescending(entry => entry.Status == ConversationStatus.Conflict).ThenBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            var rowIndex = _grid.Rows.Add(entry.Folder, entry.RelativePath, DescribeStatus(entry), FormatDate(entry.FirstModifiedUtc), FormatDate(entry.SecondModifiedUtc));
            var row = _grid.Rows[rowIndex];
            row.Tag = entry;
            row.Cells["Choice"] = CreateChoiceCell(entry);
            if (entry.Status != ConversationStatus.Conflict)
                row.Cells["Choice"].ReadOnly = true;
            row.DefaultCellStyle.BackColor = entry.Status switch
            {
                ConversationStatus.Conflict => Color.Moccasin,
                ConversationStatus.NewInA => Color.Honeydew,
                ConversationStatus.NewInB => Color.AliceBlue,
                _ => SystemColors.ControlLight,
            };
        }
    }

    private static DataGridViewCell CreateChoiceCell(ConversationEntry entry)
    {
        if (entry.Status != ConversationStatus.Conflict)
            return new DataGridViewTextBoxCell { Value = entry.Selection == ConflictChoice.A ? "A" : "B" };

        var cell = new DataGridViewComboBoxCell();
        cell.Items.Add("A");
        cell.Items.Add("B");
        cell.Value = entry.Selection.ToString();
        return cell;
    }

    private void ChoiceChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0 || _grid.Columns[e.ColumnIndex].Name != "Choice" || _grid.Rows[e.RowIndex].Tag is not ConversationEntry entry || entry.Status != ConversationStatus.Conflict)
            return;
        if (_grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value is string choice && Enum.TryParse<ConflictChoice>(choice, out var selection))
            entry.Selection = selection;
    }

    private static string DescribeStatus(ConversationEntry entry) => entry.Status switch
    {
        ConversationStatus.NewInA => "Nueva en A",
        ConversationStatus.NewInB => "Nueva en B",
        ConversationStatus.Identical => "Idéntica (una copia)",
        ConversationStatus.Conflict => $"Conflicto · {DescribeNewest(entry)}",
        _ => string.Empty,
    };

    private static string DescribeNewest(ConversationEntry entry)
    {
        if (entry.FirstModifiedUtc is null || entry.SecondModifiedUtc is null) return "elija una versión";
        if (entry.FirstModifiedUtc == entry.SecondModifiedUtc) return "misma fecha; elija una versión";
        return entry.FirstModifiedUtc > entry.SecondModifiedUtc ? "A más reciente" : "B más reciente";
    }

    private static string FormatDate(DateTime? date) => date?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "—";
}
