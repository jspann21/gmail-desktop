using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using ImageSource = System.Windows.Media.ImageSource;

namespace GmailDesktop.Services;

public static class AppIconFactory
{
    public static Icon CreateIcon(int size = 32)
    {
        var executablePath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            using var associatedIcon = Icon.ExtractAssociatedIcon(executablePath);
            if (associatedIcon is not null)
                return new Icon(associatedIcon, size, size);
        }

        return DrawFallbackIcon(size);
    }

    private static Icon DrawFallbackIcon(int size)
    {
        using var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(System.Drawing.Color.Transparent);

            var margin = size * 0.09f;
            var rect = new RectangleF(margin, margin, size - margin * 2, size - margin * 2);
            using var background = new SolidBrush(System.Drawing.Color.FromArgb(234, 67, 53));
            using var path = RoundedRectangle(rect, size * 0.2f);
            graphics.FillPath(background, path);

            using var pen = new System.Drawing.Pen(System.Drawing.Color.White, Math.Max(1.8f, size * 0.105f))
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };
            var left = size * 0.24f;
            var right = size * 0.76f;
            var top = size * 0.31f;
            var middle = size * 0.55f;
            var bottom = size * 0.71f;
            graphics.DrawLines(pen, [new PointF(left, bottom), new PointF(left, top), new PointF(size / 2f, middle), new PointF(right, top), new PointF(right, bottom)]);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    public static ImageSource ToImageSource(Icon icon)
    {
        var source = Imaging.CreateBitmapSourceFromHIcon(
            icon.Handle,
            System.Windows.Int32Rect.Empty,
            BitmapSizeOptions.FromEmptyOptions());
        source.Freeze();
        return source;
    }

    private static GraphicsPath RoundedRectangle(RectangleF rectangle, float radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}
