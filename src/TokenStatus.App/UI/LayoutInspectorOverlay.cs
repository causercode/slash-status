#if DEBUG
using System.Drawing.Drawing2D;

namespace TokenStatus.App.UI;

internal sealed class LayoutInspectorOverlay : Form
{
    private const int WmNcHitTest = 0x0084;
    private static readonly IntPtr HtTransparent = new(-1);

    private readonly Control _root;
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private Control? _hoveredControl;

    public LayoutInspectorOverlay(Control root)
    {
        _root = root;
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        TabStop = false;
        Visible = false;
        BackColor = Color.Fuchsia;
        TransparencyKey = Color.Fuchsia;

        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.UserPaint,
            true);

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 100 };
        _refreshTimer.Tick += (_, _) => RefreshInspection();
    }

    public bool IsInspecting { get; private set; }

    public void Toggle()
    {
        if (IsInspecting)
        {
            Disable();
        }
        else
        {
            IsInspecting = true;
            SyncBounds();
            Show(_root.FindForm());
            _refreshTimer.Start();
            RefreshInspection();
        }
    }

    public void Disable()
    {
        if (!IsInspecting)
        {
            return;
        }

        IsInspecting = false;
        Hide();
        _refreshTimer.Stop();
        _hoveredControl = null;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int wsExTransparent = 0x00000020;
            const int wsExNoActivate = 0x08000000;
            var parameters = base.CreateParams;
            parameters.ExStyle |= wsExTransparent | wsExNoActivate;
            return parameters;
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(TransparencyKey);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.None;

        using var boundsPen = new Pen(Color.FromArgb(255, 92, 92));
        using var marginPen = new Pen(Color.FromArgb(255, 185, 72)) { DashStyle = DashStyle.Dash };
        using var paddingPen = new Pen(Color.FromArgb(72, 205, 255)) { DashStyle = DashStyle.Dot };

        foreach (var item in EnumerateControls(_root))
        {
            DrawControlMetrics(e.Graphics, item.Control, boundsPen, marginPen, paddingPen);
        }

        if (_hoveredControl is { } hovered)
        {
            DrawHoveredControl(e.Graphics, hovered);
        }

        DrawLegend(e.Graphics);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmNcHitTest)
        {
            message.Result = HtTransparent;
            return;
        }

        base.WndProc(ref message);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refreshTimer.Dispose();
        }

        base.Dispose(disposing);
    }

    private void RefreshInspection()
    {
        SyncBounds();
        var cursor = Cursor.Position;
        _hoveredControl = EnumerateControls(_root)
            .Where(item => GetScreenBounds(item.Control).Contains(cursor))
            .OrderByDescending(item => item.Depth)
            .Select(item => item.Control)
            .FirstOrDefault();
        Invalidate();
    }

    private void DrawControlMetrics(
        Graphics graphics,
        Control control,
        Pen boundsPen,
        Pen marginPen,
        Pen paddingPen)
    {
        var bounds = GetOverlayBounds(control);
        if (!bounds.IntersectsWith(ClientRectangle))
        {
            return;
        }

        DrawRectangle(graphics, marginPen, Rectangle.FromLTRB(
            bounds.Left - control.Margin.Left,
            bounds.Top - control.Margin.Top,
            bounds.Right + control.Margin.Right,
            bounds.Bottom + control.Margin.Bottom));
        DrawRectangle(graphics, boundsPen, bounds);

        if (control.Padding != Padding.Empty)
        {
            DrawRectangle(graphics, paddingPen, Rectangle.FromLTRB(
                bounds.Left + control.Padding.Left,
                bounds.Top + control.Padding.Top,
                bounds.Right - control.Padding.Right,
                bounds.Bottom - control.Padding.Bottom));
        }
    }

    private void DrawHoveredControl(Graphics graphics, Control control)
    {
        var bounds = GetOverlayBounds(control);
        using var highlightPen = new Pen(Color.FromArgb(72, 205, 255), 2);
        DrawRectangle(graphics, highlightPen, bounds);

        var typeName = control.GetType().Name;
        var identifier = string.IsNullOrWhiteSpace(control.Name) ? typeName : $"{typeName} #{control.Name}";
        var details = $"{identifier}  {bounds.Width}×{bounds.Height}\n" +
                      $"Bounds {control.Bounds.X},{control.Bounds.Y} {control.Bounds.Width}×{control.Bounds.Height}\n" +
                      $"Margin {Format(control.Margin)}   Padding {Format(control.Padding)}";
        DrawInformationBox(graphics, details, PointToClient(Cursor.Position));
    }

    private void DrawLegend(Graphics graphics)
    {
        const string text = "Inspect  Ctrl+Shift+I   red bounds   yellow margin   blue padding";
        var textSize = TextRenderer.MeasureText(text, Font);
        var bounds = new Rectangle(
            8,
            Math.Max(8, ClientSize.Height - textSize.Height - 14),
            Math.Min(ClientSize.Width - 16, textSize.Width + 12),
            textSize.Height + 6);
        using var background = new SolidBrush(Color.FromArgb(225, 16, 16, 18));
        graphics.FillRectangle(background, bounds);
        TextRenderer.DrawText(
            graphics,
            text,
            Font,
            new Point(bounds.Left + 6, bounds.Top + 3),
            Color.White,
            TextFormatFlags.NoPadding);
    }

    private void DrawInformationBox(Graphics graphics, string text, Point cursor)
    {
        var textSize = TextRenderer.MeasureText(text, Font, Size.Empty, TextFormatFlags.NoPadding);
        var box = new Rectangle(cursor.X + 14, cursor.Y + 18, textSize.Width + 14, textSize.Height + 12);
        if (box.Right > ClientSize.Width - 6)
        {
            box.X = Math.Max(6, ClientSize.Width - box.Width - 6);
        }

        if (box.Bottom > ClientSize.Height - 6)
        {
            box.Y = Math.Max(6, cursor.Y - box.Height - 12);
        }

        using var background = new SolidBrush(Color.FromArgb(238, 18, 18, 20));
        using var border = new Pen(Color.FromArgb(255, 72, 205, 255));
        graphics.FillRectangle(background, box);
        DrawRectangle(graphics, border, box);
        TextRenderer.DrawText(
            graphics,
            text,
            Font,
            new Rectangle(box.X + 7, box.Y + 6, box.Width - 14, box.Height - 12),
            Color.White,
            TextFormatFlags.NoPadding);
    }

    private Rectangle GetOverlayBounds(Control control)
    {
        var screenBounds = GetScreenBounds(control);
        var location = PointToClient(screenBounds.Location);
        return new Rectangle(location, screenBounds.Size);
    }

    private static Rectangle GetScreenBounds(Control control) =>
        control.RectangleToScreen(control.ClientRectangle);

    private void SyncBounds()
    {
        var targetBounds = GetScreenBounds(_root);
        if (Bounds != targetBounds)
        {
            Bounds = targetBounds;
        }
    }

    private static IEnumerable<(Control Control, int Depth)> EnumerateControls(Control parent, int depth = 0)
    {
        foreach (Control child in parent.Controls)
        {
            if (child is LayoutInspectorOverlay || !child.Visible)
            {
                continue;
            }

            yield return (child, depth);
            foreach (var descendant in EnumerateControls(child, depth + 1))
            {
                yield return descendant;
            }
        }
    }

    private static string Format(Padding value) =>
        $"{value.Left},{value.Top},{value.Right},{value.Bottom}";

    private static void DrawRectangle(Graphics graphics, Pen pen, Rectangle bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        graphics.DrawRectangle(pen, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
    }
}
#endif
