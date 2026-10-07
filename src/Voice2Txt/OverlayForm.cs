using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Voice2Txt.Core;
using Voice2Txt.Core.Verification;

namespace Voice2Txt;

/// <summary>
/// オーバーレイ。画面下中央・フォーカスを奪わない・クリック素通し・常に最前面。見た目は docs/moc/2026-10-06-ptt-overlay.html に従う。
/// </summary>
internal sealed class OverlayForm : Form
{
    private static readonly Color Bg = Color.FromArgb(28, 28, 30), Rec = Color.FromArgb(0xE5, 0x38, 0x3B),
        Ok = Color.FromArgb(0x7E, 0xE2, 0xC8), Warn = Color.FromArgb(0xFF, 0xD1, 0x66), Muted = Color.FromArgb(0xBB, 0xBB, 0xBB);

    private string _talkKeyName;

    /// <summary>文言に出すトークキーの名前(検証モードの restart で設定を読み直したときに変える。UI スレッド)。</summary>
    public void SetTalkKeyName(string name) => _talkKeyName = name;

    private readonly System.Windows.Forms.Timer _anim = new() { Interval = 90 };
    private readonly System.Windows.Forms.Timer _hide = new();
    private OverlayView _view = OverlayView.Hidden;
    private int _tick;
    private readonly Func<double> _inputLevel;
    private double _meter;

    /// <param name="inputLevel">録音中の音量バーの値(0〜1)を返す。描画のたびに呼ぶ(UI スレッド)。</param>
    public OverlayForm(string talkKeyName, Func<double>? inputLevel = null)
    {
        _talkKeyName = talkKeyName;
        _inputLevel = inputLevel ?? (() => 0);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Bg;
        Opacity = 0.92;
        DoubleBuffered = true;
        Font = new Font("Yu Gothic UI", 10f);
        _rowH = Math.Max(36, Font.Height + 16);
        _anim.Tick += (_, _) => { _tick++; SampleMeter(); Invalidate(); };
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

    /// <summary>音量バーに今描いている値(0〜1)。</summary>
    public double MeterLevel => _meter;

    /// <summary>録れている音の大きさを取り込み、音量バーの値を更新して返す(UI スレッド)。</summary>
    public double SampleMeter()
    {
        double target = _view.State is OverlayState.Recording or OverlayState.ProcessingAndRecording ? _inputLevel() : 0;
        return _meter = InputMeter.Follow(_meter, target);
    }

    public void Apply(OverlayView v)
    {
        if (v.State is not (OverlayState.Recording or OverlayState.ProcessingAndRecording)) _meter = 0;
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

    // MOC の .row の gap:10px、.kbd(枠・padding 0 5px・11px)、.badge(丸・padding 1px 8px・11px)、.meter(幅 3px×5 本・間 2px)
    private const int Gap = 10, IconW = 10, PadX = 16, KeyPad = 5, BadgePad = 8, MeterW = 5 * 3 + 4 * 2;
    private const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
    private static readonly Color KeyBorder = Color.FromArgb(128, 255, 255, 255), BadgeFill = Color.FromArgb(46, 255, 255, 255);
    private readonly Font _small = new("Yu Gothic UI", 8.25f);

    private int PartWidth(OverlayPart p) => p.Kind switch
    {
        OverlayPartKind.Meter => MeterW,
        OverlayPartKind.Key => TextRenderer.MeasureText(p.Text, _small, Size.Empty, Flags).Width + KeyPad * 2,
        OverlayPartKind.Badge => TextRenderer.MeasureText(p.Text, _small, Size.Empty, Flags).Width + BadgePad * 2,
        _ => TextRenderer.MeasureText(p.Text, Font, Size.Empty, Flags).Width,
    };

    private int PartsWidth(IReadOnlyList<OverlayPart> parts) => parts.Sum(PartWidth) + Gap * Math.Max(0, parts.Count - 1);

    // 途中経過の欄(MOC の .partial: 本文の下に区切り線、折り返して数行。収まらなければ頭を削って末尾を残す)
    private const int InterimMaxW = 520, InterimTop = 4, InterimBottom = 10;
    private const TextFormatFlags WrapFlags = TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl;
    private static readonly Color InterimColor = Color.FromArgb(0xEB, 0xEB, 0xEB), Rule = Color.FromArgb(51, 255, 255, 255);
    private readonly Font _interimFont = new("Yu Gothic UI", 10.5f);
    private int _rowH;
    private string? _interimShown;
    private Rectangle _interimBox;

    /// <summary>今オーバーレイに描いている途中経過(欄に収めた後の文字列。無ければ null)。</summary>
    public string? InterimShown => _interimShown;

    private Size MeasureWrapped(string s, int width) => TextRenderer.MeasureText(s, _interimFont, new Size(width, int.MaxValue), WrapFlags);

    private void LayoutFor(OverlayView v)
    {
        int w = PadX + IconW + Gap + PartsWidth(v.Parts(_talkKeyName)) + PadX, h = _rowH;
        _interimShown = null;
        if (!string.IsNullOrEmpty(v.Interim))
        {
            int lineH = TextRenderer.MeasureText("あ", _interimFont, Size.Empty, Flags).Height;
            int oneLine = TextRenderer.MeasureText(v.Interim, _interimFont, Size.Empty, Flags).Width + 2;
            int tw = Math.Min(InterimMaxW, Math.Max(w - 2 * PadX, oneLine));
            _interimShown = OverlayView.FitInterim(v.Interim, s => MeasureWrapped(s, tw).Height <= lineH * OverlayView.InterimMaxLines);
            _interimBox = new Rectangle(PadX, _rowH + InterimTop, tw, MeasureWrapped(_interimShown, tw).Height);
            w = Math.Max(w, tw + 2 * PadX);
            h = _interimBox.Bottom + InterimBottom;
        }
        var wa = Screen.PrimaryScreen!.WorkingArea;
        Bounds = new Rectangle(wa.Left + (wa.Width - w) / 2, wa.Bottom - 18 - h, w, h);
        using var path = Rounded(new Rectangle(0, 0, w, h), Radius);
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
        int cy = _rowH / 2, x = 16;
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
            case OverlayState.Evacuated or OverlayState.DeliveryFailed:
                TextRenderer.DrawText(g, "!", Font, new Point(x + 2, cy - Font.Height / 2), Warn);
                break;
            case OverlayState.CancelledSilence or OverlayState.Cancelled:
                TextRenderer.DrawText(g, "—", Font, new Point(x - 2, cy - Font.Height / 2), Muted);
                break;
        }
        x += IconW + Gap;
        // 本文は MOC の並び・色: 地の文は白、補足は灰色、キー名は枠付き、処理待ちは丸いバッジ、音量バーは「録音中」の直後
        foreach (var p in v.Parts(_talkKeyName))
        {
            int pw = PartWidth(p);
            switch (p.Kind)
            {
                case OverlayPartKind.Meter:
                    // 録れている音の大きさに連動(無音なら低く揃って止まる)
                    var bars = InputMeter.BarHeights(_meter, _tick);
                    using (var b = new SolidBrush(Color.White))
                        for (int i = 0; i < bars.Length; i++)
                            g.FillRectangle(b, x + i * 5, cy - bars[i] / 2, 3, bars[i]);
                    break;
                case OverlayPartKind.Key or OverlayPartKind.Badge:
                    int th = TextRenderer.MeasureText(p.Text, _small, Size.Empty, Flags).Height, bh = th + 2;
                    var box = new Rectangle(x, cy - bh / 2, pw - 1, bh);
                    if (p.Kind == OverlayPartKind.Key)
                        using (var path = Rounded(box, 4)) using (var pen = new Pen(KeyBorder)) g.DrawPath(pen, path);
                    else
                        using (var path = Rounded(box, bh)) using (var b = new SolidBrush(BadgeFill)) g.FillPath(b, path);
                    TextRenderer.DrawText(g, p.Text, _small, new Point(x + (p.Kind == OverlayPartKind.Key ? KeyPad : BadgePad), cy - th / 2), Color.White, Flags);
                    break;
                default:
                    int h = TextRenderer.MeasureText(p.Text, Font, Size.Empty, Flags).Height;
                    TextRenderer.DrawText(g, p.Text, Font, new Point(x, cy - h / 2), p.Kind == OverlayPartKind.Note ? Muted : Color.White, Flags);
                    break;
            }
            x += pw + Gap;
        }
        if (_interimShown is not null)
        {
            using (var pen = new Pen(Rule)) g.DrawLine(pen, PadX, _rowH - 1, size.Width - PadX, _rowH - 1);
            TextRenderer.DrawText(g, _interimShown, _interimFont, _interimBox, InterimColor, WrapFlags);
        }
    }

    /// <summary>
    /// 今の表示を PNG に保存する。まず画面から撮り(実機のスクショ)、それが自分の表示と言えるときだけ使う。
    /// 撮れない・一色・上に別の窓が重なって自前の描画と食い違う(並行して走る別の検証モードのオーバーレイ等)ときは自前の描画で代える。
    /// 戻り値は "screen" か "render (理由)"。重なりなら理由に食い違いの割合と上に重なった窓を残す。
    /// </summary>
    public string SaveScreenshot(string path)
    {
        Native.DwmFlush();
        var b = Bounds;
        using var own = new Bitmap(Math.Max(1, b.Width), Math.Max(1, b.Height), PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(own)) PaintContent(g, own.Size);
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
            if (IsUniform(bmp)) reason = "uniform " + bmp.GetPixel(0, 0).Name;
            else
            {
                double mismatch = OverlayCapture.MismatchRatio(Pixels(bmp), Pixels(own), b.Width, b.Height, Radius);
                AppLog.Write($"capture-check mismatch={mismatch:0.0000}");
                if (OverlayCapture.LooksLikeOwn(mismatch)) { bmp.Save(path, ImageFormat.Png); return "screen"; }
                var above = WindowsAbove(b);
                reason = $"overlapped: {mismatch:P0} differs from own drawing; above: {(above.Count == 0 ? "unknown" : string.Join(", ", above))}";
            }
        }
        catch (Exception ex) { reason = ex.GetType().Name + ": " + ex.Message; }
        own.Save(path, ImageFormat.Png);
        return $"render ({reason})";
    }

    private const int Radius = 18;

    private static int[] Pixels(Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var px = new int[bmp.Width * bmp.Height];
            for (int y = 0; y < bmp.Height; y++)
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, px, y * bmp.Width, bmp.Width);
            return px;
        }
        finally { bmp.UnlockBits(data); }
    }

    /// <summary>z 順で自分より上にあり、見えていて自分の範囲に重なる別プロセスの窓("プロセス名#pid"、最大 3 つ)。</summary>
    private List<string> WindowsAbove(Rectangle bounds)
    {
        var found = new List<string>();
        int self = Environment.ProcessId;
        for (nint h = Native.GetWindow(Handle, Native.GW_HWNDPREV); h != 0 && found.Count < 3; h = Native.GetWindow(h, Native.GW_HWNDPREV))
        {
            if (!Native.IsWindowVisible(h) || !Native.GetWindowRect(h, out var r)) continue;
            if (!bounds.IntersectsWith(Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom))) continue;
            Native.GetWindowThreadProcessId(h, out uint pid);
            if (pid == self) continue;
            string name;
            try { name = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { name = "?"; }
            found.Add($"{name}#{pid}");
        }
        return found;
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
