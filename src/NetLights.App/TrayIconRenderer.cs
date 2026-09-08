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

    private static readonly int[] Sizes = [16, 20, 24, 32];
    private readonly Dictionary<(GroupAvailability Left, GroupAvailability Right, int Size), Icon> _cache = [];

    public Icon Get(GroupAvailability left, GroupAvailability right, int size)
    {
        int nearest = Sizes.MinBy(s => Math.Abs(s - size));
        var key = (left, right, nearest);
        if (_cache.TryGetValue(key, out Icon? icon))
        {
            return icon;
        }

        icon = Render(left, right, nearest);
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

    public static Bitmap RenderBitmap(GroupAvailability left, GroupAvailability right, int size)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        float pad = Math.Max(1f, size * 0.08f);
        float diameter = size - (pad * 2f);
        var bounds = new Rectangle((int)pad, (int)pad, (int)diameter, (int)diameter);
        int gap = size >= 24 ? 2 : 1;
        float mid = bounds.X + (bounds.Width / 2f);
        using var leftPath = new GraphicsPath();
        leftPath.AddPie(bounds, 90, 180);
        using var rightPath = new GraphicsPath();
        rightPath.AddPie(bounds, 270, 180);
        using var leftBrush = new SolidBrush(Colors[left]);
        using var rightBrush = new SolidBrush(Colors[right]);
        g.FillPath(leftBrush, leftPath);
        g.FillPath(rightBrush, rightPath);
        using var clear = new SolidBrush(Color.Transparent);
        using var gapPen = new Pen(Color.FromArgb(0, 0, 0, 0), gap);
        g.CompositingMode = CompositingMode.SourceCopy;
        g.FillRectangle(clear, mid - (gap / 2f), bounds.Y, gap, bounds.Height);
        return bmp;
    }

    private static Icon Render(GroupAvailability left, GroupAvailability right, int size)
    {
        using Bitmap bmp = RenderBitmap(left, right, size);
        IntPtr handle = bmp.GetHicon();
        using var temp = Icon.FromHandle(handle);
        var clone = (Icon)temp.Clone();
        DestroyIcon(handle);
        return clone;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
