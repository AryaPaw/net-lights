using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace NetLights.App;

internal sealed class CountryTrayIconRenderer : IDisposable
{
    private static readonly int[] Sizes = [16, 20, 24, 32];
    private static readonly Color Fill = Color.FromArgb(255, 252, 252, 252);
    private static readonly Color Outline = Color.FromArgb(255, 32, 36, 40);
    private readonly Dictionary<(string Letters, int Size, GeoCountryLetterScale Scale), Icon> _cache = [];

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

    public Icon Get(string letters, int size, GeoCountryLetterScale scale)
    {
        string text = Normalize(letters);
        int nearest = Sizes.MinBy(s => Math.Abs(s - size));
        var key = (text, nearest, scale);
        if (_cache.TryGetValue(key, out Icon? icon))
        {
            return icon;
        }

        icon = ToAlphaIcon(RenderBitmap(text, nearest, scale));
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

    public static Bitmap RenderBitmap(string letters, int size, GeoCountryLetterScale scale = GeoCountryLetterScale.Regular)
        => Draw(Normalize(letters), size, scale);

    private static string Normalize(string letters)
        => letters.Length == 2 ? letters : "??";

    private static Bitmap Draw(string text, int size, GeoCountryLetterScale scale)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.CompositingMode = CompositingMode.SourceOver;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.Clear(Color.Transparent);
        using FontFamily family = CreateFamily();
        using GraphicsPath path = FitGlyph(text, family, size, GeoCountryLetterScales.Fill(scale));
        RectangleF glyph = path.GetBounds();
        if (glyph.Width < 1 || glyph.Height < 1)
        {
            return bmp;
        }

        float dx = ((size - glyph.Width) / 2f) - glyph.X;
        float dy = ((size - glyph.Height) / 2f) - glyph.Y;
        var matrix = new Matrix();
        using (matrix)
        {
            matrix.Translate(dx, dy);
            path.Transform(matrix);
        }
        float stroke = Math.Max(1f, size / 16f);
        using var pen = new Pen(Outline, stroke)
        {
            LineJoin = LineJoin.Round,
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        using var fill = new SolidBrush(Fill);
        g.DrawPath(pen, path);
        g.FillPath(fill, path);
        return bmp;
    }

    private static GraphicsPath FitGlyph(string text, FontFamily family, int size, float fill)
    {
        float em = size + 4f;
        float limit = Math.Max(8f, (size - 0.5f) * fill);
        while (em >= 8f)
        {
            var path = new GraphicsPath();
            path.AddString(
                text,
                family,
                (int)FontStyle.Bold,
                em,
                PointF.Empty,
                StringFormat.GenericTypographic);
            RectangleF bounds = path.GetBounds();
            if (bounds.Width <= limit && bounds.Height <= limit)
            {
                return path;
            }

            path.Dispose();
            em -= 0.5f;
        }

        var fallback = new GraphicsPath();
        fallback.AddString(
            text,
            family,
            (int)FontStyle.Bold,
            Math.Max(8f, em),
            PointF.Empty,
            StringFormat.GenericTypographic);
        return fallback;
    }

    private static FontFamily CreateFamily()
    {
        try
        {
            return new FontFamily("Consolas");
        }
        catch (ArgumentException)
        {
            return FontFamily.GenericMonospace;
        }
    }

    private static Icon ToAlphaIcon(Bitmap bmp)
    {
        using (bmp)
        {
            IntPtr color = CreateColorDib(bmp);
            IntPtr mask = CreateBitmap(bmp.Width, bmp.Height, 1, 1, IntPtr.Zero);
            var info = new IconInfo
            {
                fIcon = true,
                xHotspot = 0,
                yHotspot = 0,
                hbmMask = mask,
                hbmColor = color
            };
            IntPtr handle = CreateIconIndirect(ref info);
            DeleteObject(color);
            DeleteObject(mask);
            if (handle == IntPtr.Zero)
            {
                IntPtr fallback = bmp.GetHicon();
                using var tempFallback = Icon.FromHandle(fallback);
                var cloneFallback = (Icon)tempFallback.Clone();
                DestroyIcon(fallback);
                return cloneFallback;
            }

            using var temp = Icon.FromHandle(handle);
            var clone = (Icon)temp.Clone();
            DestroyIcon(handle);
            return clone;
        }
    }

    private static IntPtr CreateColorDib(Bitmap bmp)
    {
        var header = new BitmapInfo
        {
            biSize = 40,
            biWidth = bmp.Width,
            biHeight = -bmp.Height,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0
        };
        IntPtr bits = IntPtr.Zero;
        IntPtr dib = CreateDIBSection(IntPtr.Zero, ref header, 0, out bits, IntPtr.Zero, 0);
        if (dib == IntPtr.Zero || bits == IntPtr.Zero)
        {
            return bmp.GetHbitmap();
        }

        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
        int bytes = Math.Abs(data.Stride) * bmp.Height;
        byte[] buffer = new byte[bytes];
        Marshal.Copy(data.Scan0, buffer, 0, bytes);
        Marshal.Copy(buffer, 0, bits, bytes);
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        return dib;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateIconIndirect(ref IconInfo icon);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateBitmap(int width, int height, int planes, int bitCount, IntPtr bits);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(
        IntPtr hdc,
        ref BitmapInfo bmi,
        int usage,
        out IntPtr bits,
        IntPtr section,
        int offset);
}
