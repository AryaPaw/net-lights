using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using NetLights.Core;

namespace NetLights.App;

internal sealed class TrayIconRenderer : IDisposable
{
    private static readonly Dictionary<GroupAvailability, Color> Colors = new()
    {
        [GroupAvailability.Online] = Color.FromArgb(255, 0, 220, 92),
        [GroupAvailability.Limited] = Color.FromArgb(255, 228, 178, 12),
        [GroupAvailability.Offline] = Color.FromArgb(255, 232, 64, 64),
        [GroupAvailability.Unknown] = Color.FromArgb(255, 188, 190, 192)
    };

    private static readonly Color Paused = Color.FromArgb(255, 74, 144, 196);

    private static readonly int[] Sizes = [16, 20, 24, 32];
    private readonly Dictionary<(GroupAvailability Left, GroupAvailability Right, int Size, bool Paused), Icon> _cache = [];

    public int SystemSmallIconSize()
    {
        int px = GetSystemMetrics(49);
        if (px <= 0)
        {
            using var g = Graphics.FromHwnd(IntPtr.Zero);
            px = (int)Math.Round(16f * (g.DpiX / 96f));
        }

        return Sizes.MinBy(s => Math.Abs(s - px));
    }

    public Icon Get(GroupAvailability left, GroupAvailability right, int size, bool paused = false)
    {
        int nearest = Sizes.MinBy(s => Math.Abs(s - size));
        var key = (left, right, nearest, paused);
        if (_cache.TryGetValue(key, out Icon? icon))
        {
            return icon;
        }

        icon = Render(left, right, nearest, paused);
        _cache[key] = icon;
        return icon;
    }

    public void Dispose()
    {
        foreach (Icon icon in _cache.Values)
        {
            icon.Dispose();
        }

        _cache.Clear();
    }

    public static Bitmap RenderBitmap(GroupAvailability left, GroupAvailability right, int size, bool paused = false)
    {
        if (size <= 16)
        {
            using Bitmap hi = Draw(left, right, size * 2, paused);
            return Downsample(hi, size);
        }

        return Draw(left, right, size, paused);
    }

    private static Bitmap Draw(GroupAvailability left, GroupAvailability right, int size, bool paused)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.Clear(Color.Transparent);
        float pad = Math.Max(1f, size * 0.03f);
        float diameter = size - (pad * 2f);
        var bounds = new RectangleF(pad, pad, diameter, diameter);
        int gap = size >= 24 ? 3 : 2;
        float mid = bounds.X + (bounds.Width / 2f);
        Color leftColor = paused ? Paused : Colors[left];
        Color rightColor = paused ? Paused : Colors[right];
        using var leftBrush = new SolidBrush(leftColor);
        using var rightBrush = new SolidBrush(rightColor);
        g.FillPie(leftBrush, bounds, 90, 180);
        g.FillPie(rightBrush, bounds, 270, 180);
        using var clear = new SolidBrush(Color.Transparent);
        g.CompositingMode = CompositingMode.SourceCopy;
        g.FillRectangle(clear, mid - (gap / 2f), bounds.Y, gap, bounds.Height);
        return bmp;
    }

    private static Bitmap Downsample(Bitmap source, int size)
    {
        var dest = new Bitmap(size, size);
        using var g = Graphics.FromImage(dest);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.Clear(Color.Transparent);
        g.DrawImage(source, new Rectangle(0, 0, size, size));
        return dest;
    }

    private static Icon Render(GroupAvailability left, GroupAvailability right, int size, bool paused)
    {
        using Bitmap bmp = RenderBitmap(left, right, size, paused);
        IntPtr handle = bmp.GetHicon();
        using var temp = Icon.FromHandle(handle);
        var clone = (Icon)temp.Clone();
        DestroyIcon(handle);
        return clone;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
