namespace TokenStatus.App.UI;

internal class VerticalStackLayout : TableLayoutPanel
{
    public VerticalStackLayout()
    {
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        ColumnCount = 1;
        RowCount = 0;
        GrowStyle = TableLayoutPanelGrowStyle.AddRows;
        Margin = new Padding(0);
        Padding = new Padding(0);
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
    }

    public void AddRow(Control control, bool stretch = true)
    {
        var row = RowCount++;
        RowStyles.Add(new RowStyle(SizeType.AutoSize));
        control.Dock = stretch ? DockStyle.Fill : DockStyle.Left;
        Controls.Add(control, 0, row);
    }

    public void ClearRows()
    {
        var removed = Controls.Cast<Control>().ToArray();
        Controls.Clear();
        RowStyles.Clear();
        RowCount = 0;
        foreach (var control in removed)
        {
            control.Dispose();
        }
    }
}
