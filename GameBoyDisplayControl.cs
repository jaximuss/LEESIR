using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LEESIR;

/// <summary>Pixel-preserving WinForms presentation surface for the 160x144 DMG framebuffer.</summary>
public sealed class GameBoyDisplayControl : Control
{
    public const int FrameWidth = 160;
    public const int FrameHeight = 144;

    private readonly Bitmap _bitmap = new(FrameWidth, FrameHeight, PixelFormat.Format32bppArgb);
    private readonly int[] _pixels = new int[FrameWidth * FrameHeight];

    // A restrained green DMG-style presentation palette. The core still exposes shade indices 0..3.
    private static readonly int[] Palette =
    {
        Color.FromArgb(224, 248, 208).ToArgb(),
        Color.FromArgb(136, 192, 112).ToArgb(),
        Color.FromArgb(52, 104, 86).ToArgb(),
        Color.FromArgb(8, 24, 32).ToArgb()
    };

    public GameBoyDisplayControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw |
                 ControlStyles.UserPaint, true);
        BackColor = Color.Black;
        MinimumSize = new Size(FrameWidth * 2, FrameHeight * 2);
        TabStop = false;
        AccessibleName = "Game Boy video display";
        ClearFrame();
    }

    public void SetFrame(ReadOnlySpan<byte> shadeIndices)
    {
        if (shadeIndices.Length < _pixels.Length)
            throw new ArgumentException($"A frame requires {_pixels.Length:N0} shade indices.", nameof(shadeIndices));

        for (int i = 0; i < _pixels.Length; i++)
        {
            byte shade = shadeIndices[i];
            if (shade > 3)
                throw new ArgumentOutOfRangeException(nameof(shadeIndices), $"Framebuffer shade {shade} at index {i} is outside 0..3.");
            _pixels[i] = Palette[shade];
        }

        UploadPixels();
        Invalidate();
    }

    public void ClearFrame()
    {
        Array.Fill(_pixels, Palette[0]);
        UploadPixels();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.Clear(BackColor);
        e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
        e.Graphics.CompositingMode = CompositingMode.SourceCopy;

        int scale = Math.Max(1, Math.Min(ClientSize.Width / FrameWidth, ClientSize.Height / FrameHeight));
        int width = FrameWidth * scale;
        int height = FrameHeight * scale;
        int left = (ClientSize.Width - width) / 2;
        int top = (ClientSize.Height - height) / 2;
        e.Graphics.DrawImage(_bitmap, new Rectangle(left, top, width, height),
            0, 0, FrameWidth, FrameHeight, GraphicsUnit.Pixel);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _bitmap.Dispose();
        base.Dispose(disposing);
    }

    private void UploadPixels()
    {
        Rectangle rectangle = new(0, 0, FrameWidth, FrameHeight);
        BitmapData data = _bitmap.LockBits(rectangle, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < FrameHeight; y++)
            {
                IntPtr row = IntPtr.Add(data.Scan0, y * data.Stride);
                Marshal.Copy(_pixels, y * FrameWidth, row, FrameWidth);
            }
        }
        finally
        {
            _bitmap.UnlockBits(data);
        }
    }
}
