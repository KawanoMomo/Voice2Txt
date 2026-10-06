using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Voice2Txt.Core;

namespace Voice2Txt;

/// <summary>
/// オーバーレイ。画面下中央・フォーカスを奪わない・クリック素通し・常に最前面。見た目は docs/moc/2026-10-06-ptt-overlay.html に従う。
/// </summary>
internal sealed class OverlayForm : Form
{
    private static readonly Color Bg = Color.FromArgb(28, 28, 30), Rec = Color.FromArgb(0xE5, 0x38, 0x3B),
        Ok = Color.FromArgb(0x7E, 0xE2, 0xC8), Warn = Color.FromArgb(0xFF, 0xD1, 0x66), Muted = Color.FromArgb(0xBB, 0xBB, 0xBB);

    private readonly string _talkKeyName;
    private readonly System.Windows.Forms.Timer _anim = new() { Interval = 90 };
    private readonly System.Windows.Forms.Timer _hide = new();
    private OverlayView _view = OverlayView.Hidden;
    private int _tick;

    public OverlayForm(string talkKeyName)
    {
        _talkKeyName = talkKeyName;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Bg;
        Opacity = 0.92;
        DoubleBuffered = true;
        Font = new Font("Yu Gothic UI", 10f);
        _anim.Tick += (_, _) => { _tick++; Invalidate(); };
        _hide.Tick += (_, _) => { _hide.Stop(); HideOverlay(); };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST | Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED;
            return cp;
        }
    }

    // クリックを素通しする
    protected override void WndProc(ref Message m)
    {
        const int WM_NCHITTEST = 0x84, HTTRANSPARENT = -1, WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3;
        if (m.Msg == WM_NCHITTEST) { m.Result = HTTRANSPARENT; return; }
        if (m.Msg == WM_MOUSEACTIVATE) { m.Result = MA_NOACTIVATE; return; }
        base.WndProc(ref m);
    }

    public OverlayView View => _view;

    public void Apply(OverlayView v)
    {
        _view = v;
        _hide.Stop();
        if (v.State == OverlayState.Hidden) { HideOverlay(); return; }
        LayoutFor(v);
        if (!Visible) Show();
        _anim.Enabled = v.State is OverlayState.Recording or OverlayState.ProcessingAndRecording or OverlayState.Processing
            or OverlayState.ModelPreparing or OverlayState.Preparing;
        if (v.TransientMs is { } ms) { _hide.Interval = ms; _hide.Start(); }
        Invalidate();
    }

    private void HideOverlay() { _anim.Stop(); if (Visible) Hide(); }

    private void LayoutFor(OverlayView v)
    {
        var text = v.Text(_talkKeyName);
        var sz = TextRenderer.MeasureText(text, Font);
        int extra = v.State is OverlayState.Recording or OverlayState.ProcessingAndRecording ? 30 : 0;
        int w = 16 + 10 + 10 + sz.Width + extra + 16, h = Math.Max(36, sz.Height + 16);
        var wa = Screen.PrimaryScreen!.WorkingArea;
        Bounds = new Rectangle(wa.Left + (wa.Width - w) / 2, wa.Bottom - 18 - h, w, h);
        using var path = Rounded(new Rectangle(0, 0, w, h), 18);
        Region = new Region(path);
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        int d = Math.Min(radius * 2, r.Height);
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    protected override void OnPaint(PaintEventArgs e) => PaintContent(e.Graphics, ClientSize);

    public void PaintContent(Graphics g, Size size)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Bg);
        var v = _view;
        int cy = size.Height / 2, x = 16;
        switch (v.State)
        {
            case OverlayState.Preparing:
                using (var b = new SolidBrush(Color.FromArgb(0x88, 0x88, 0x88))) g.FillEllipse(b, x, cy - 5, 10, 10);
                break;
            case OverlayState.Recording or OverlayState.ProcessingAndRecording:
                float s = 10 + 3 * (float)Math.Abs(Math.Sin(_tick * 0.35));
                using (var b = new SolidBrush(Rec)) g.FillEllipse(b, x + 5 - s / 2, cy - s / 2, s, s);
                break;
            case OverlayState.Processing or OverlayState.ModelPreparing:
                using (var pen = new Pen(Color.White, 2)) g.DrawArc(pen, x, cy - 6, 12, 12, _tick * 40 % 360, 270);
                break;
            case OverlayState.Pasted:
                TextRenderer.DrawText(g, "✓", Font, new Point(x, cy - Font.Height / 2), Ok);
                break;
            case OverlayState.Evacuated:
                TextRenderer.DrawText(g, "!", Font, new Point(x + 2, cy - Font.Height / 2), Warn);
                break;
            case OverlayState.CancelledSilence or OverlayState.Cancelled:
                TextRenderer.DrawText(g, "—", Font, new Point(x - 2, cy - Font.Height / 2), Muted);
                break;
        }
        x += 20;
        var text = v.Text(_talkKeyName);
        var color = v.State == OverlayState.Evacuated ? Warn : Color.White;
        TextRenderer.DrawText(g, text, Font, new Point(x, cy - TextRenderer.MeasureText(text, Font).Height / 2), color);
        if (v.State is OverlayState.Recording or OverlayState.ProcessingAndRecording)
        {
            int mx = x + TextRenderer.MeasureText(text, Font).Width + 6;
            using var b = new SolidBrush(Color.White);
            for (int i = 0; i < 5; i++)
            {
                int hgt = 3 + (int)(11 * Math.Abs(Math.Sin(_tick * 0.6 + i * 0.9)));
                g.FillRectangle(b, mx + i * 5, cy - hgt / 2, 3, hgt);
            }
        }
    }

    /// <summary>今の表示を PNG に保存する。まず画面から撮り(実機のスクショ)、撮れなければ自前の描画で代える。戻り値は "screen" か "render"。</summary>
    public string SaveScreenshot(string path)
    {
        Native.DwmFlush();
        var b = Bounds;
        string reason;
        try
        {
            using var bmp = new Bitmap(b.Width, b.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                // 重ね合わせ(layered)ウィンドウも写るよう CAPTUREBLT 付きで画面から写す
                nint dst = g.GetHdc(), src = Native.GetDC(0);
                try { if (!Native.BitBlt(dst, 0, 0, b.Width, b.Height, src, b.X, b.Y, Native.SRCCOPY | Native.CAPTUREBLT)) throw new InvalidOperationException("BitBlt 失敗"); }
                finally { Native.ReleaseDC(0, src); g.ReleaseHdc(dst); }
            }
            if (!IsUniform(bmp)) { bmp.Save(path, ImageFormat.Png); return "screen"; }
            reason = "uniform " + bmp.GetPixel(0, 0).Name;
        }
        catch (Exception ex) { reason = ex.GetType().Name + ": " + ex.Message; }
        using var r = new Bitmap(Math.Max(1, b.Width), Math.Max(1, b.Height));
        using (var g = Graphics.FromImage(r)) PaintContent(g, r.Size);
        r.Save(path, ImageFormat.Png);
        return $"render ({reason})";
    }

    private static bool IsUniform(Bitmap bmp)
    {
        var c0 = bmp.GetPixel(0, 0);
        for (int y = 0; y < bmp.Height; y += 3)
            for (int x = 0; x < bmp.Width; x += 3)
                if (bmp.GetPixel(x, y) != c0) return false;
        return true;
    }
}
