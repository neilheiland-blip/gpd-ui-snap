namespace GpdUiSnap;

using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

internal sealed class TargetOverlayForm : Form
{
    private const int UlwAlpha = 0x00000002;
    private const byte AcSrcOver = 0x00;
    private const byte AcSrcAlpha = 0x01;
    private IReadOnlyList<UiTarget> _targets = Array.Empty<UiTarget>();
    private UiTarget? _selected;
    private Rectangle _virtualScreen;
    private string? _message;
    private readonly System.Windows.Forms.Timer _animationTimer = new() { Interval = 16 };
    private long _selectionChangedAt;

    public TargetOverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Color.Black;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);

        _animationTimer.Tick += (_, _) =>
        {
            if (SelectionProgress() >= 1)
                _animationTimer.Stop();
            if (Visible)
                RenderLayeredWindow();
        };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_LAYERED;
            return cp;
        }
    }

    public void ShowTargets(IReadOnlyList<UiTarget> targets, UiTarget? selected)
    {
        _virtualScreen = SystemInformation.VirtualScreen;
        Bounds = _virtualScreen;
        _targets = targets.ToArray();
        _selected = selected;
        _selectionChangedAt = Environment.TickCount64;
        _message = null;
        if (!Visible)
            Show();
        RenderLayeredWindow();
    }

    public void ShowMessage(string message)
    {
        _virtualScreen = SystemInformation.VirtualScreen;
        Bounds = _virtualScreen;
        _targets = Array.Empty<UiTarget>();
        _selected = null;
        _selectionChangedAt = Environment.TickCount64;
        _message = message;
        if (!Visible)
            Show();
        RenderLayeredWindow();
    }

    public void UpdateSelection(UiTarget? selected)
    {
        if (!ReferenceEquals(_selected, selected))
        {
            _selectionChangedAt = Environment.TickCount64;
            _animationTimer.Start();
        }
        _selected = selected;
        if (Visible)
            RenderLayeredWindow();
    }

    public void HideTargets()
    {
        _targets = Array.Empty<UiTarget>();
        _selected = null;
        _message = null;
        _animationTimer.Stop();
        Hide();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        DrawOverlay(e.Graphics);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // Per-pixel alpha rendering is handled by UpdateLayeredWindow.
    }

    private void DrawOverlay(Graphics graphics)
    {
        graphics.Clear(Color.Transparent);
        if (!string.IsNullOrWhiteSpace(_message))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var textBrush = new SolidBrush(Color.FromArgb(245, 255, 255, 255));
            using var backBrush = new SolidBrush(Color.FromArgb(156, 24, 28, 34));
            using var borderPen = new Pen(Color.FromArgb(74, 238, 244, 255), 1.2f);
            using var font = new Font("Segoe UI Variable Text", 15, FontStyle.Bold);
            var textSize = graphics.MeasureString(_message, font);
            var rect = new RectangleF(24, 24, textSize.Width + 32, textSize.Height + 20);
            using var path = RoundedRect(rect, 14);
            graphics.FillPath(backBrush, path);
            graphics.DrawPath(borderPen, path);
            graphics.DrawString(_message, font, textBrush, rect.Left + 16, rect.Top + 9);
            return;
        }

        if (_targets.Count == 0)
            return;

        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.CompositingQuality = CompositingQuality.HighSpeed;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;

        foreach (var target in HintTargets().Where(IsTriggerableHint))
        {
            if (ReferenceEquals(target, _selected))
                continue;

            var rect = ToOverlayRect(target.Bounds);
            rect.Inflate(_selected is null ? 2 : 1, _selected is null ? 2 : 1);
            DrawCandidateOutline(graphics, rect, _selected is null);
        }

        if (_selected is not null)
            DrawSelected(graphics, _selected);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _animationTimer.Dispose();
        base.Dispose(disposing);
    }

    private void RenderLayeredWindow()
    {
        if (!IsHandleCreated || Width <= 0 || Height <= 0)
            return;

        using var bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
            DrawOverlay(graphics);

        var screenDc = GetDC(IntPtr.Zero);
        var memoryDc = CreateCompatibleDC(screenDc);
        var bitmapHandle = bitmap.GetHbitmap(Color.FromArgb(0));
        var oldBitmap = SelectObject(memoryDc, bitmapHandle);

        try
        {
            var position = new LayeredPoint(Left, Top);
            var size = new LayeredSize(Width, Height);
            var source = new LayeredPoint(0, 0);
            var blend = new BlendFunction
            {
                BlendOp = AcSrcOver,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = AcSrcAlpha,
            };

            UpdateLayeredWindow(Handle, screenDc, ref position, ref size, memoryDc, ref source, 0, ref blend, UlwAlpha);
        }
        finally
        {
            SelectObject(memoryDc, oldBitmap);
            DeleteObject(bitmapHandle);
            DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private Rectangle ToOverlayRect(Rectangle screenRect)
    {
        return new Rectangle(
            screenRect.Left - _virtualScreen.Left,
            screenRect.Top - _virtualScreen.Top,
            screenRect.Width,
            screenRect.Height);
    }

    private IEnumerable<UiTarget> HintTargets()
    {
        if (_selected is null)
            return _targets;

        return _targets
            .Where(target => !ReferenceEquals(target, _selected))
            .OrderBy(target => Distance(target.ClickablePoint, _selected.ClickablePoint))
            .Take(80);
    }

    private void DrawSelected(Graphics graphics, UiTarget selected)
    {
        var progress = SelectionProgress();
        var ease = 1 - Math.Pow(1 - progress, 3);
        var rect = ToOverlayRect(selected.Bounds);
        rect.Inflate(6, 6);

        var scale = 1.025 - 0.012 * ease;
        using var path = TargetRectPath(Scale(rect, scale), 13);

        using var materialFill = new SolidBrush(Color.FromArgb((int)(108 + 34 * ease), 238, 246, 255));
        using var blueRing = new Pen(Color.FromArgb((int)(178 + 42 * ease), 0, 122, 255), 2.7f);
        using var innerHighlight = new Pen(Color.FromArgb((int)(130 + 58 * ease), 255, 255, 255), 1.15f);
        using var softShadow = new Pen(Color.FromArgb((int)(28 + 12 * ease), 0, 0, 0), 5.5f);

        softShadow.LineJoin = LineJoin.Round;
        blueRing.LineJoin = LineJoin.Round;
        innerHighlight.LineJoin = LineJoin.Round;
        blueRing.Alignment = PenAlignment.Inset;
        innerHighlight.Alignment = PenAlignment.Inset;

        graphics.DrawPath(softShadow, path);
        graphics.FillPath(materialFill, path);
        graphics.DrawPath(blueRing, path);
        graphics.DrawPath(innerHighlight, path);
    }

    private double SelectionProgress()
    {
        var elapsed = Environment.TickCount64 - _selectionChangedAt;
        return Math.Clamp(elapsed / 135.0, 0, 1);
    }

    private static RectangleF Scale(Rectangle rect, double scale)
    {
        var width = (float)(rect.Width * scale);
        var height = (float)(rect.Height * scale);
        return new RectangleF(
            rect.Left + (rect.Width - width) / 2,
            rect.Top + (rect.Height - height) / 2,
            width,
            height);
    }

    private static double Distance(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static void DrawCandidateOutline(Graphics graphics, RectangleF rect, bool noSelection)
    {
        var aligned = PixelAligned(rect);
        if (aligned.Width <= 1 || aligned.Height <= 1)
            return;

        using var fill = new SolidBrush(Color.FromArgb(noSelection ? 58 : 28, 18, 21, 26));
        using var pen = new Pen(
            Color.FromArgb(noSelection ? 68 : 32, 255, 255, 255),
            noSelection ? 0.9f : 0.7f);
        pen.Alignment = PenAlignment.Center;

        if (aligned.Width < 16 || aligned.Height < 12)
        {
            graphics.FillRectangle(fill, aligned);
            graphics.DrawRectangle(pen, aligned.X, aligned.Y, aligned.Width, aligned.Height);
            return;
        }

        using var path = RoundedRect(aligned, noSelection ? 10 : 7);
        graphics.FillPath(fill, path);
        graphics.DrawPath(pen, path);
    }

    private static bool IsTriggerableHint(UiTarget target)
    {
        return target.ControlType is
            "Button" or "SplitButton" or "Hyperlink" or "MenuItem" or
            "Edit" or "ComboBox" or "CheckBox" or "RadioButton" or
            "TabItem" or "ListItem" or "TreeItem" or "DataItem";
    }

    private static void DrawRoundedStroke(Graphics graphics, RectangleF rect, float radius, Pen pen)
    {
        radius = MathF.Min(radius, MathF.Min(rect.Width, rect.Height) / 2f);
        if (radius < 1.5f)
        {
            graphics.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
            return;
        }

        var diameter = radius * 2;
        var left = rect.Left;
        var top = rect.Top;
        var right = rect.Right;
        var bottom = rect.Bottom;

        graphics.DrawLine(pen, left + radius, top, right - radius, top);
        graphics.DrawLine(pen, right, top + radius, right, bottom - radius);
        graphics.DrawLine(pen, right - radius, bottom, left + radius, bottom);
        graphics.DrawLine(pen, left, bottom - radius, left, top + radius);

        graphics.DrawArc(pen, left, top, diameter, diameter, 180, 90);
        graphics.DrawArc(pen, right - diameter, top, diameter, diameter, 270, 90);
        graphics.DrawArc(pen, right - diameter, bottom - diameter, diameter, diameter, 0, 90);
        graphics.DrawArc(pen, left, bottom - diameter, diameter, diameter, 90, 90);
    }

    private static GraphicsPath TargetRectPath(RectangleF rect, float radius)
    {
        if (rect.Width < 18 || rect.Height < 14)
        {
            var path = new GraphicsPath();
            path.AddRectangle(PixelAligned(rect));
            return path;
        }

        return RoundedRect(rect, radius);
    }

    private static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        rect = PixelAligned(rect);

        if (rect.Width <= 3 || rect.Height <= 3)
        {
            path.AddRectangle(rect);
            return path;
        }

        var maxRadius = MathF.Max(0, MathF.Min(rect.Width, rect.Height) / 2f);
        radius = MathF.Min(MathF.Max(0, radius), maxRadius);
        if (radius < 1.5f)
        {
            path.AddRectangle(rect);
            return path;
        }

        var diameter = radius * 2;
        var arc = new RectangleF(rect.Location, new SizeF(diameter, diameter));
        path.AddArc(arc, 180, 90);
        arc.X = rect.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = rect.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = rect.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static RectangleF PixelAligned(RectangleF rect)
    {
        return RectangleF.FromLTRB(
            MathF.Round(rect.Left) + 0.5f,
            MathF.Round(rect.Top) + 0.5f,
            MathF.Round(rect.Right) - 0.5f,
            MathF.Round(rect.Bottom) - 0.5f);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct LayeredPoint
    {
        public LayeredPoint(int x, int y)
        {
            X = x;
            Y = y;
        }

        public readonly int X;
        public readonly int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct LayeredSize
    {
        public LayeredSize(int cx, int cy)
        {
            Cx = cx;
            Cy = cy;
        }

        public readonly int Cx;
        public readonly int Cy;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BlendFunction
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(
        IntPtr hwnd,
        IntPtr hdcDst,
        ref LayeredPoint pptDst,
        ref LayeredSize psize,
        IntPtr hdcSrc,
        ref LayeredPoint pptSrc,
        int crKey,
        ref BlendFunction pblend,
        int dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr ho);
}
