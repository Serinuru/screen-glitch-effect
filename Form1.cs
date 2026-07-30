using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

using WinFormTimer = System.Windows.Forms.Timer;
namespace homework;

public partial class Form1 : Form
{
    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_LAYERED = 0x00080000;
    
    private System.Windows.Forms.Timer updateTimer = null!;
    private Bitmap? currentFrame;
    private int time;
    private readonly Random rng = new Random();

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TRANSPARENT | WS_EX_LAYERED;
            return cp;
        }
    }

    public Form1()
    {
         InitializeComponent();

        this.Opacity = 0.999;
        this.KeyPreview = true;
        this.Load += MainForm_Load;
        this.FormClosed += (s, e) => updateTimer?.Stop();

        // Reduce flicker from repeated full-form Invalidate/Paint calls.
        this.DoubleBuffered = true;

        updateTimer = new System.Windows.Forms.Timer();
        updateTimer.Interval = 42; // ~15 fps
        updateTimer.Tick += UpdateLoop;

    }
     private void MainForm_Load(object? sender, EventArgs e)
    {
        GoFullScreen();

        // Must have a valid window handle before this call.
        SetWindowDisplayAffinity(this.Handle, WDA_EXCLUDEFROMCAPTURE);

        updateTimer.Start();
    }

    private void GoFullScreen()
    {
        this.FormBorderStyle = FormBorderStyle.None;
        this.Bounds = Screen.PrimaryScreen!.Bounds; // cover the actual screen, not just "maximized"
        this.TopMost = true;
    }

    private void ExitFullScreen()
    {
        this.FormBorderStyle = FormBorderStyle.Sizable;
        this.WindowState = FormWindowState.Normal;
        this.TopMost = false;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.KeyCode == Keys.Escape)
        {
            updateTimer.Stop();
            ExitFullScreen();
            // Or Application.Exit(); if you'd rather just quit outright.
        }
    }

    // ---------------- Update loop ----------------

    private void UpdateLoop(object? sender, EventArgs e)
    {
        Rectangle bounds = Screen.PrimaryScreen!.Bounds;

        Bitmap capture = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(capture))
        {
            // Because of SetWindowDisplayAffinity above, this sees the real
            // desktop underneath our own topmost window, not our own glitch.
            g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        }
        
        time += 1;
        ApplyMultLayer(capture);
        ApplyGlitch(capture);

        currentFrame?.Dispose();
        currentFrame = capture;

        this.Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        if (currentFrame != null)
        {
            e.Graphics.DrawImageUnscaled(currentFrame, 0, 0);
        }
    }
    // Multiply Layer
    private void ApplyMultLayer(Bitmap basebmp, Bitmap multbmp)
    {

        if (basebmp.Width != blendImg.Width || basebmp.Height != blendImg.Height)
        {
            throw new ArgumentException("Images must share identical dimensions.");
        }

        int width = baseImg.Width;
        int height = baseImg.Height;
        Rectangle rect = new Rectangle(0, 0, width, height);

        Bitmap resultImg = new Bitmap(width, height, PixelFormat.Format32bppArgb);

        BitmapData baseData = baseImg.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        BitmapData blendData = blendImg.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        BitmapData resultData = resultImg.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

        int byteCount = baseData.Stride * height;
        byte[] baseBytes = new byte[byteCount];
        byte[] blendBytes = new byte[byteCount];
        byte[] resultBytes = new byte[byteCount];

        Marshal.Copy(baseData.Scan0, baseBytes, 0, byteCount);
        Marshal.Copy(blendData.Scan0, blendBytes, 0, byteCount);

        for (int i = 0; i < byteCount; i += 4)
        {
            // Blue, Green, and Red channels multiplied and scaled
            resultBytes[i]     = (byte)((baseBytes[i]     * blendBytes[i])     / 255); // B
            resultBytes[i + 1] = (byte)((baseBytes[i + 1] * blendBytes[i + 1]) / 255); // G
            resultBytes[i + 2] = (byte)((baseBytes[i + 2] * blendBytes[i + 2]) / 255); // R
            
            // Retain the base image alpha channel transparency
            resultBytes[i + 3] = baseBytes[i + 3]; 
        }

        Marshal.Copy(resultBytes, 0, resultData.Scan0, byteCount);

        baseImg.UnlockBits(baseData);
        blendImg.UnlockBits(blendData);
        resultImg.UnlockBits(resultData);

        return resultImg;
    }
    // ---------------- Glitch effect ----------------

    private void ApplyGlitch(Bitmap bmp)
    {
        Rectangle rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        BitmapData data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);

        int stride = data.Stride; // bytes per row (may include padding)
        int width = bmp.Width;
        int height = bmp.Height;
        int byteCount = stride * height;

        byte[] pixels = new byte[byteCount];
        Marshal.Copy(data.Scan0, pixels, 0, byteCount);

        // --- 1. Row displacement: shift random scanlines left/right ---
        byte[] rowBuf = new byte[stride];
        for (int y = 0; y < height; y++)
        {
            if (rng.Next(40) == 0)
            {
                int shift = rng.Next(-40, 41);
                if (shift == 0) continue;

                int rowStart = y * stride;
                Array.Copy(pixels, rowStart, rowBuf, 0, stride);

                for (int x = 0; x < width; x++)
                {
                    int srcX = x - shift;
                    if (srcX < 0) srcX = 0;
                    if (srcX >= width) srcX = width - 1;

                    Array.Copy(rowBuf, srcX * 4, pixels, rowStart + x * 4, 4);
                }
            }
        }
        byte[] src = (byte[])pixels.Clone(); // read from a snapshot while writing pixels

        // --- 2. RGB channel shift (chromatic aberration) ---
        int rShift = -(int)Math.Floor((Math.Sin(time)*40)-20);
        int bShift = (int)Math.Floor((Math.Sin(time)*40)-20);

        
        for (int y = 0; y < height; y++)
        {
            int rowStart = y * stride;
            for (int x = 0; x < width; x++)
            {
                int rx = x - rShift; if (rx < 0) rx = 0; if (rx >= width) rx = width - 1;
                int bx = x - bShift; if (bx < 0) bx = 0; if (bx >= width) bx = width - 1;

                int dstOffset = rowStart + x * 4;
                int rSrcOffset = rowStart + rx * 4;
                int bSrcOffset = rowStart + bx * 4;

                // Format32bppArgb byte order in memory: B, G, R, A
                pixels[dstOffset + 2] = src[rSrcOffset + 2]; // R, shifted
                pixels[dstOffset + 0] = src[bSrcOffset + 0]; // B, shifted
                //pixels[dstOffset + 0] = 0;
                // G (offset+1) and A (offset+3) left as-is
            }
        }


        // -- 3. Green waveform
        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                int gwave = (int)Math.Floor(Math.Sin(time+x)*1);
                int rwave = (int)Math.Floor(Math.Cos(time+x)*1);
                int bwave = (int)Math.Floor(Math.Tan(time+x)*1);
                int gy = y - gwave; if (gy < 0) gy = 0; if (gy >= height) gy = height - 1;
                int ry = y - gwave; if (ry < 0) ry = 0; if (ry >= height) ry = height - 1;
                int by = y - gwave; if (by < 0) by = 0; if (by >= height) by = height - 1;
                int dstOffset = (y * stride) + (x * 4);
                int gSrcOffset = (gy * stride) + (x * 4);
                int rSrcOffset = (ry * stride) + (x * 4);
                int bSrcOffset = (by * stride) + (x * 4);
                pixels[dstOffset + 1] = src[gSrcOffset + 1];
                pixels[dstOffset + 2] = src[rSrcOffset + 2];
                pixels[dstOffset + 0] = src[bSrcOffset + 0];
            }
        }

        Marshal.Copy(pixels, 0, data.Scan0, byteCount);
        bmp.UnlockBits(data);
    }

    
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            updateTimer?.Dispose();
            currentFrame?.Dispose();
        }
        base.Dispose(disposing);
    }

}
