using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using LEESIR.Cores.Nes;

namespace LEESIR;

/// <summary>Nearest-neighbour presenter for the NES 256x240 palette-index framebuffer.</summary>
internal sealed class NesDisplayControl : Control
{
    private static readonly int[] Palette =
    {
        C(0x66,0x66,0x66), C(0x00,0x2A,0x88), C(0x14,0x12,0xA7), C(0x3B,0x00,0xA4),
        C(0x5C,0x00,0x7E), C(0x6E,0x00,0x40), C(0x6C,0x06,0x00), C(0x56,0x1D,0x00),
        C(0x33,0x35,0x00), C(0x0B,0x48,0x00), C(0x00,0x52,0x00), C(0x00,0x4F,0x08),
        C(0x00,0x40,0x4D), C(0x00,0x00,0x00), C(0x00,0x00,0x00), C(0x00,0x00,0x00),
        C(0xAD,0xAD,0xAD), C(0x15,0x5F,0xD9), C(0x42,0x40,0xFF), C(0x75,0x27,0xFE),
        C(0xA0,0x1A,0xCC), C(0xB7,0x1E,0x7B), C(0xB5,0x31,0x20), C(0x99,0x4E,0x00),
        C(0x6B,0x6D,0x00), C(0x38,0x87,0x00), C(0x0C,0x93,0x00), C(0x00,0x8F,0x32),
        C(0x00,0x7C,0x8D), C(0x00,0x00,0x00), C(0x00,0x00,0x00), C(0x00,0x00,0x00),
        C(0xFF,0xFE,0xFF), C(0x64,0xB0,0xFF), C(0x92,0x90,0xFF), C(0xC6,0x76,0xFF),
        C(0xF3,0x6A,0xFF), C(0xFE,0x6E,0xCC), C(0xFE,0x81,0x70), C(0xEA,0x9E,0x22),
        C(0xBC,0xBE,0x00), C(0x88,0xD8,0x00), C(0x5C,0xE4,0x30), C(0x45,0xE0,0x82),
        C(0x48,0xCD,0xDE), C(0x4F,0x4F,0x4F), C(0x00,0x00,0x00), C(0x00,0x00,0x00),
        C(0xFF,0xFE,0xFF), C(0xC0,0xDF,0xFF), C(0xD3,0xD2,0xFF), C(0xE8,0xC8,0xFF),
        C(0xFB,0xC2,0xFF), C(0xFE,0xC4,0xEA), C(0xFE,0xCC,0xC5), C(0xF7,0xD8,0xA5),
        C(0xE4,0xE5,0x94), C(0xCF,0xEF,0x96), C(0xBD,0xF4,0xAB), C(0xB3,0xF3,0xCC),
        C(0xB5,0xEB,0xF2), C(0xB8,0xB8,0xB8), C(0x00,0x00,0x00), C(0x00,0x00,0x00),
    };

    private readonly Bitmap _bitmap = new(NesCore.VideoWidth, NesCore.VideoHeight, PixelFormat.Format32bppArgb);
    private readonly int[] _pixels = new int[NesCore.VideoWidth * NesCore.VideoHeight];
    private bool _hasFrame;

    internal NesDisplayControl()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Color.Black;
        TabStop = false;
    }

    internal void SetFrame(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < _pixels.Length)
            throw new ArgumentException($"NES video requires {_pixels.Length:N0} palette-index pixels.", nameof(frame));

        for (int i = 0; i < _pixels.Length; i++)
            _pixels[i] = Palette[frame[i] & 0x3F];

        Rectangle rect = new(0, 0, _bitmap.Width, _bitmap.Height);
        BitmapData data = _bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            if (data.Stride == _bitmap.Width * 4)
            {
                Marshal.Copy(_pixels, 0, data.Scan0, _pixels.Length);
            }
            else
            {
                for (int y = 0; y < _bitmap.Height; y++)
                    Marshal.Copy(_pixels, y * _bitmap.Width, IntPtr.Add(data.Scan0, y * data.Stride), _bitmap.Width);
            }
        }
        finally
        {
            _bitmap.UnlockBits(data);
        }

        _hasFrame = true;
        Invalidate();
    }

    internal void ClearFrame()
    {
        _hasFrame = false;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.Clear(Color.Black);
        if (!_hasFrame || ClientSize.Width <= 0 || ClientSize.Height <= 0)
            return;

        double rawScale = Math.Min(
            ClientSize.Width / (double)NesCore.VideoWidth,
            ClientSize.Height / (double)NesCore.VideoHeight);
        double scale = rawScale >= 1.0 ? Math.Floor(rawScale) : rawScale;
        int width = Math.Max(1, (int)Math.Round(NesCore.VideoWidth * scale));
        int height = Math.Max(1, (int)Math.Round(NesCore.VideoHeight * scale));
        int x = (ClientSize.Width - width) / 2;
        int y = (ClientSize.Height - height) / 2;

        e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
        e.Graphics.DrawImage(_bitmap, new Rectangle(x, y, width, height), 0, 0, _bitmap.Width, _bitmap.Height, GraphicsUnit.Pixel);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _bitmap.Dispose();
        base.Dispose(disposing);
    }

    private static int C(int r, int g, int b)
        => unchecked((int)(0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | (uint)b));
}
