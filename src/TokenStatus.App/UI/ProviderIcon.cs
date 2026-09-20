using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;

namespace TokenStatus.App.UI;

internal enum ProviderIconKind
{
    Codex,
    OpenCode
}

internal sealed class ProviderIcon : Control
{
    private static readonly Lazy<Bitmap> CodexImage = new(() => LoadImage("codex.png"));
    private static readonly Lazy<Bitmap> OpenCodeImage = new(() => LoadImage("opencode.png"));
    private readonly ProviderIconKind _kind;

    public ProviderIcon(ProviderIconKind kind)
    {
        _kind = kind;
        Size = new Size(22, 22);
        Tag = "accent";
        AccessibleRole = AccessibleRole.Graphic;
        AccessibleName = kind == ProviderIconKind.Codex ? "Codex" : "OpenCode";
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.SupportsTransparentBackColor |
            ControlStyles.UserPaint,
            true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var image = _kind == ProviderIconKind.Codex ? CodexImage.Value : OpenCodeImage.Value;
        var scale = Math.Min(ClientSize.Width / (float)image.Width, ClientSize.Height / (float)image.Height);
        var width = Math.Max(1, (int)Math.Round(image.Width * scale));
        var height = Math.Max(1, (int)Math.Round(image.Height * scale));
        var destination = new Rectangle((ClientSize.Width - width) / 2, (ClientSize.Height - height) / 2, width, height);
        var color = WindowsTheme.GetAccentColor();
        var colorMatrix = new ColorMatrix([
            [0, 0, 0, 0, 0],
            [0, 0, 0, 0, 0],
            [0, 0, 0, 0, 0],
            [0, 0, 0, 1, 0],
            [color.R / 255f, color.G / 255f, color.B / 255f, 0, 1]
        ]);

        using var attributes = new ImageAttributes();
        attributes.SetColorMatrix(colorMatrix);
        e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        e.Graphics.DrawImage(
            image,
            destination,
            0,
            0,
            image.Width,
            image.Height,
            GraphicsUnit.Pixel,
            attributes);
    }

    private static Bitmap LoadImage(string fileName)
    {
        var resourceName = $"TokenStatus.App.UI.Assets.{fileName}";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing embedded icon resource: {resourceName}");
        using var image = Image.FromStream(stream);
        return new Bitmap(image);
    }
}
