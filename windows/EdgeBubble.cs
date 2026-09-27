using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace MultipleMouse;

// A separate UI thread keeps animation paints and GDI work out of the mouse hook.
sealed class EdgeBubble : IDisposable
{
    readonly Thread thread;
    BubbleWindow? window;
    volatile bool disposed;
    public EdgeBubble()
    {
        thread = new Thread(() =>
        {
            using var form = new BubbleWindow(); _ = form.Handle;
            Volatile.Write(ref window, form);
            if (!disposed) Application.Run();
            Volatile.Write(ref window, null);
        }) { IsBackground = true, Name = "MultipleMouse edge animation" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
    }
    public void Play(Rectangle screen, bool right, int y)
    {
        var form = Volatile.Read(ref window);
        if (disposed || form == null) return;
        try { form.BeginInvoke((Action)(() => { if (!disposed) form.Play(screen, right, y); })); }
        catch (InvalidOperationException) { /* Closing; decoration must never affect input. */ }
    }
    public void Dispose()
    {
        disposed = true; var form = Volatile.Read(ref window);
        if (form != null)
        {
            try { form.BeginInvoke((Action)(() => { form.Close(); Application.ExitThread(); })); }
            catch (InvalidOperationException) { }
        }
    }

    sealed class BubbleWindow : Form
    {
        readonly System.Windows.Forms.Timer timer = new() { Interval = 16 };
        long started;
        Point position;
        float centerX, centerY;
        int side;
        public BubbleWindow()
        {
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.None; StartPosition = FormStartPosition.Manual;
            timer.Tick += (_, _) => Frame();
        }
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get { var p = base.CreateParams; p.ExStyle |= 0x80000 | 0x20 | 0x08000000 | 0x80; return p; } // Layered, transparent, no-activate, tool window.
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x84) { m.Result = -1; return; } // HTTRANSPARENT
            if (m.Msg == 0x21) { m.Result = 3; return; } // MA_NOACTIVATE
            base.WndProc(ref m);
        }
        public void Play(Rectangle screen, bool right, int y)
        {
            timer.Stop(); side = Math.Min(180, Math.Min(screen.Width, screen.Height));
            if (side <= 0) return;
            y = Math.Clamp(y, screen.Top, screen.Bottom - 1);
            position = new Point(right ? screen.Right - side : screen.Left, Math.Clamp(y - side / 2, screen.Top, screen.Bottom - side));
            centerX = right ? side : 0; centerY = y - position.Y;
            started = Stopwatch.GetTimestamp(); Frame();
            SetWindowPos(Handle, -1, position.X, position.Y, side, side, 0x10 | 0x40); // Topmost; never activate.
            timer.Start();
        }
        void Frame()
        {
            float t = (float)(Stopwatch.GetElapsedTime(started).TotalSeconds / 0.46);
            if (t >= 1) { timer.Stop(); ShowWindow(Handle, 0); return; }
            float fade = t < 0.12f ? t / 0.12f : t < 0.5f ? 1 - (t - 0.12f) / 0.38f * 0.3f : (1 - t) * 1.4f;
            float ease = 1 - MathF.Pow(1 - t, 3);
            using var image = new Bitmap(side, side, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(image))
            {
                g.Clear(Color.Transparent); g.SmoothingMode = SmoothingMode.AntiAlias;
                for (int i = 0; i < 3; i++)
                {
                    float scale = (0.12f + i * 0.04f) + (0.88f - i * 0.04f) * ease;
                    float r = (i == 0 ? 78 : i == 1 ? 61 : 43) * scale;
                    var bounds = new RectangleF(centerX - r * 0.72f, centerY - r, r * 1.44f, r * 2);
                    using var fill = new SolidBrush(Color.FromArgb((int)((i == 0 ? 31 : 13) * fade), 64, 199, 255));
                    using var stroke = new Pen(Color.FromArgb((int)((i == 0 ? 230 : 128) * fade), 140, 232, 255), i == 0 ? 2 : 1);
                    g.FillEllipse(fill, bounds); g.DrawEllipse(stroke, bounds);
                }
            }
            Present(image, position);
        }
        void Present(Bitmap image, Point destination)
        {
            nint screenDC = GetDC(0), memoryDC = 0, bitmap = 0, old = 0;
            try
            {
                if (screenDC == 0) return;
                memoryDC = CreateCompatibleDC(screenDC); if (memoryDC == 0) return;
                bitmap = image.GetHbitmap(Color.FromArgb(0)); old = SelectObject(memoryDC, bitmap);
                var source = new Point(0, 0); var size = new Size(image.Width, image.Height);
                var blend = new Blend { Alpha = 255, Format = 1 };
                UpdateLayeredWindow(Handle, screenDC, ref destination, ref size, memoryDC, ref source, 0, ref blend, 2);
            }
            finally
            {
                if (old != 0) SelectObject(memoryDC, old);
                if (bitmap != 0) DeleteObject(bitmap);
                if (memoryDC != 0) DeleteDC(memoryDC);
                if (screenDC != 0) ReleaseDC(0, screenDC);
            }
        }
        protected override void Dispose(bool disposing) { if (disposing) timer.Dispose(); base.Dispose(disposing); }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] struct Blend { public byte Operation, Flags, Alpha, Format; }
        [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(nint window, nint destinationDC, ref Point position, ref Size size, nint sourceDC, ref Point source, uint key, ref Blend blend, uint flags);
        [DllImport("user32.dll")] static extern nint GetDC(nint window);
        [DllImport("user32.dll")] static extern int ReleaseDC(nint window, nint dc);
        [DllImport("user32.dll")] static extern bool ShowWindow(nint window, int command);
        [DllImport("user32.dll")] static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
        [DllImport("gdi32.dll")] static extern nint CreateCompatibleDC(nint dc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(nint dc);
        [DllImport("gdi32.dll")] static extern nint SelectObject(nint dc, nint obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(nint obj);
    }
}
