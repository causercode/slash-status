using System.ComponentModel;
using System.Runtime.InteropServices;

namespace TokenStatus.App.UI;

internal sealed class PaddedTextBox : TextBox
{
    private const int PaintMessage = 0x000F;
    private const int PrintClientMessage = 0x0318;
    private int _horizontalContentPadding = LayoutMetrics.Small;
    private string _placeholderText = string.Empty;

    public PaddedTextBox()
    {
        AutoSize = false;
        Height = 34;
        MinimumSize = new Size(0, 34);
    }

    [DefaultValue(LayoutMetrics.Small)]
    public int HorizontalContentPadding
    {
        get => _horizontalContentPadding;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _horizontalContentPadding = value;
            ApplyContentMargins();
        }
    }

    [DefaultValue("")]
    [Localizable(true)]
    public new string PlaceholderText
    {
        get => _placeholderText;
        set
        {
            _placeholderText = value ?? string.Empty;
            base.PlaceholderText = string.Empty;
            Invalidate();
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyContentMargins();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        ApplyContentMargins();
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Invalidate();
    }

    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        if (message.Msg is PaintMessage or PrintClientMessage)
        {
            DrawPlaceholder();
        }
    }

    private void ApplyContentMargins()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        NativeEditMargins.Apply(this, _horizontalContentPadding);
    }

    private void DrawPlaceholder()
    {
        if (Focused || TextLength != 0 || string.IsNullOrEmpty(_placeholderText) || ClientSize.Width <= 0)
        {
            return;
        }

        var inset = NativeEditMargins.Scale(_horizontalContentPadding, DeviceDpi);
        var bounds = new Rectangle(
            inset,
            0,
            Math.Max(0, ClientSize.Width - (inset * 2)),
            ClientSize.Height);
        var placeholderColor = SystemInformation.HighContrast
            ? SystemColors.GrayText
            : Blend(ForeColor, BackColor, 0.55f);
        using var graphics = Graphics.FromHwnd(Handle);
        TextRenderer.DrawText(
            graphics,
            _placeholderText,
            Font,
            bounds,
            placeholderColor,
            TextFormatFlags.Left |
            TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine |
            TextFormatFlags.EndEllipsis |
            TextFormatFlags.NoPadding |
            TextFormatFlags.NoPrefix);
    }

    private static Color Blend(Color foreground, Color background, float foregroundWeight)
    {
        var backgroundWeight = 1f - foregroundWeight;
        return Color.FromArgb(
            (int)((foreground.R * foregroundWeight) + (background.R * backgroundWeight)),
            (int)((foreground.G * foregroundWeight) + (background.G * backgroundWeight)),
            (int)((foreground.B * foregroundWeight) + (background.B * backgroundWeight)));
    }
}

internal sealed class PaddedNumericUpDown : NumericUpDown
{
    private int _horizontalContentPadding = LayoutMetrics.Small;

    [DefaultValue(LayoutMetrics.Small)]
    public int HorizontalContentPadding
    {
        get => _horizontalContentPadding;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _horizontalContentPadding = value;
            ApplyContentMargins();
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyContentMargins();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        ApplyContentMargins();
    }

    internal void ApplyContentMargins()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        foreach (Control child in Controls)
        {
            if (child is TextBoxBase)
            {
                NativeEditMargins.Apply(child, _horizontalContentPadding);
            }
        }
    }
}

internal static class NativeEditMargins
{
    private const uint SetMarginsMessage = 0x00D3;
    private const int LeftMargin = 0x0001;
    private const int RightMargin = 0x0002;

    public static int Scale(int logicalPixels, int deviceDpi) =>
        Math.Max(1, logicalPixels * deviceDpi / 96);

    public static void Apply(Control control, int logicalPixels)
    {
        if (!control.IsHandleCreated)
        {
            return;
        }

        var inset = Scale(logicalPixels, control.DeviceDpi);
        var packedInsets = (nint)((inset & 0xFFFF) | (inset << 16));
        _ = SendMessage(control.Handle, SetMarginsMessage, LeftMargin | RightMargin, packedInsets);
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint window, uint message, nint parameter, nint value);
}
