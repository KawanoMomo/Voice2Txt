using System.Diagnostics;
using System.Globalization;
using System.Drawing.Imaging;
using Voice2Txt.Core;
using Voice2Txt.Core.Verification;

namespace Voice2Txt;

/// <summary>
/// 設定画面(トレイの「設定…」)。<see cref="SettingsSchema.Items"/> の各項目を名前・説明・選択肢付きで並べ、
/// 保存すると settings.json に書く(手で書いた JSON も読める・書ける)。トークキー・モデルなどは再起動で効く。
/// 検証モードは <see cref="SetValue"/> と <see cref="ClickSave"/> で、利用者と同じ部品を操作する。
/// </summary>
internal sealed class SettingsForm : Form
{
    private readonly AppSettings _original;
    private readonly string _path;
    private readonly Dictionary<string, Control> _inputs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Label _error = new() { AutoSize = true, ForeColor = Color.Firebrick, MaximumSize = new Size(560, 0) };
    private readonly Button _save = new() { Text = "保存", AutoSize = true };
    private readonly Button _cancel = new() { Text = "キャンセル", AutoSize = true, DialogResult = DialogResult.Cancel };

    /// <summary>保存した(前の設定, 保存した設定, 変わった項目のキー)。</summary>
    public event Action<AppSettings, AppSettings, List<string>>? Saved;

    public SettingsForm(AppSettings current, string settingsPath)
    {
        _original = SettingsSchema.Clone(current);
        _path = settingsPath;
        Text = "Voice2Txt の設定";
        Font = new Font("Yu Gothic UI", 9.5f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);
        AcceptButton = _save; CancelButton = _cancel;

        // 説明の列を広く取り、項目が増えても画面の高さに収まるようにする(収まらなければ項目の欄だけ縦に送る)
        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Location = Point.Empty, Padding = new Padding(0, 0, 0, 8) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, DescWidth));
        foreach (var item in SettingsSchema.Items)
        {
            var name = new Label { Text = item.Label, AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 8, 8, 0) };
            var input = MakeInput(item, SettingsSchema.Read(_original, item.Key));
            input.Name = item.Key;
            input.Margin = new Padding(0, 4, 0, 0);
            _inputs[item.Key] = input;
            var desc = new Label { Text = item.Description, AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(DescWidth, 0), Margin = new Padding(0, 2, 0, 6) };
            grid.Controls.Add(name, 0, grid.RowCount);
            grid.Controls.Add(input, 1, grid.RowCount);
            grid.RowCount++;
            grid.Controls.Add(new Label { AutoSize = true }, 0, grid.RowCount);
            grid.Controls.Add(desc, 1, grid.RowCount);
            grid.RowCount++;
        }

        var note = new Label
        {
            Text = $"保存すると {_path} に書きます(このファイルを手で直しても構いません)。トークキー・モデルなどは再起動で効きます。",
            AutoSize = true, MaximumSize = new Size(190 + DescWidth, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(0, 4, 0, 4),
        };
        var openFile = new LinkLabel { Text = "設定ファイルを開く", AutoSize = true, Margin = new Padding(0, 0, 0, 8) };
        openFile.LinkClicked += (_, _) => Process.Start(new ProcessStartInfo("notepad.exe", $"\"{_path}\"") { UseShellExecute = true });
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Top };
        buttons.Controls.Add(_cancel);
        buttons.Controls.Add(_save);
        _save.Click += (_, _) => Save();
        _cancel.Click += (_, _) => Close();

        var items = new Panel { AutoScroll = true, Margin = Padding.Empty };
        items.Controls.Add(grid);
        var stack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        stack.Controls.AddRange([items, note, openFile, _error, buttons]);
        Controls.Add(stack);
        // 項目の欄の高さ: 全部が見える高さ。画面の作業領域に収まらなければ、注記・ボタンの分を残して縦に送る
        var pref = grid.GetPreferredSize(Size.Empty);
        int maxH = Math.Max(300, (Screen.PrimaryScreen?.WorkingArea.Height ?? 1000) - ReservedHeight);
        items.Size = pref.Height <= maxH ? pref : new Size(pref.Width + SystemInformation.VerticalScrollBarWidth, maxH);
    }

    /// <summary>説明の列の幅(px)。</summary>
    private const int DescWidth = 520;

    /// <summary>項目の欄の下の注記・リンク・ボタンと、枠・題の分として残す高さ(px)。</summary>
    private const int ReservedHeight = 185;

    private static Control MakeInput(SettingItem item, string value)
    {
        switch (item.Kind)
        {
            case SettingKind.Choice:
            {
                var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260, DisplayMember = nameof(SettingChoice.Label) };
                var choices = item.Choices!.ToList();
                // 手で書いた選択肢に無い値も、そのまま残せるように並べる
                if (!choices.Any(c => string.Equals(c.Value, value, StringComparison.OrdinalIgnoreCase)))
                    choices.Add(new SettingChoice(value, $"{value}(設定ファイルの値)"));
                foreach (var c in choices) cb.Items.Add(c);
                cb.SelectedItem = choices.First(c => string.Equals(c.Value, value, StringComparison.OrdinalIgnoreCase));
                return cb;
            }
            case SettingKind.Number:
            {
                var d = double.Parse(value, CultureInfo.InvariantCulture);
                return new NumericUpDown
                {
                    Minimum = (decimal)item.Min, Maximum = (decimal)item.Max, Increment = (decimal)item.Step, DecimalPlaces = item.Decimals,
                    Value = Math.Clamp((decimal)d, (decimal)item.Min, (decimal)item.Max), Width = 100,
                };
            }
            case SettingKind.Toggle:
                return new CheckBox { Text = "オン", Checked = value == "true", AutoSize = true };
            default:
                return new TextBox { Text = value, Width = 370 };
        }
    }

    /// <summary>部品の今の値(設定ファイルに書く形)。</summary>
    private string ValueOf(Control c) => c switch
    {
        ComboBox cb => ((SettingChoice)cb.SelectedItem!).Value,
        NumericUpDown n => n.Value.ToString(CultureInfo.InvariantCulture),
        CheckBox x => x.Checked ? "true" : "false",
        _ => c.Text,
    };

    /// <summary>利用者が部品を操作するのと同じように値を入れる(検証モードの setSetting)。値は設定ファイルの形か画面の名前。</summary>
    public void SetValue(string key, string value)
    {
        var item = SettingsSchema.Get(key);
        var c = _inputs[item.Key];
        switch (c)
        {
            case ComboBox cb:
                var hit = cb.Items.Cast<SettingChoice>().FirstOrDefault(x =>
                    string.Equals(x.Value, value, StringComparison.OrdinalIgnoreCase) || string.Equals(x.Label, value, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException($"{item.Label} の選択肢に「{value}」が無い");
                cb.SelectedItem = hit;
                break;
            case NumericUpDown n:
                n.Value = decimal.Parse(value, CultureInfo.InvariantCulture);
                break;
            case CheckBox x:
                var probe = SettingsSchema.Clone(_original);
                if (SettingsSchema.Apply(probe, item.Key, value) is { } err) throw new InvalidDataException(err);
                x.Checked = SettingsSchema.Read(probe, item.Key) == "true";
                break;
            default:
                c.Text = value;
                break;
        }
    }

    /// <summary>保存ボタンを押す(検証モードの saveSettings)。保存できなければ理由(画面にも出す)。</summary>
    public string? ClickSave()
    {
        _save.PerformClick();
        return _error.Text.Length > 0 ? _error.Text : null;
    }

    /// <summary>
    /// 画面の今の姿(検証モードのスクショ)。フォーカスや重なりに左右されないよう部品に自分を描かせる(render)。
    /// 選択式の項目の値が欄に写らなければ、描き直させ、それでも写らなければ画面から写す。画面にも写らなければ値を書き足し、そのことを capture に残す。
    /// </summary>
    public SettingsShot SaveScreenshot(string path)
    {
        // 撮った時点で画面に出ている各項目の値(設定ファイルの形)と、選択式の項目の文字の欄(画の座標)
        var values = SettingsSchema.Items.ToDictionary(i => i.Key, i => ValueOf(_inputs[i.Key]), StringComparer.Ordinal);
        var fields = _inputs.Where(kv => kv.Value is ComboBox).Select(kv => Field(kv.Key, (ComboBox)kv.Value)).ToList();

        // 1. 部品に自分を描かせる(render)。選択式の欄が空なら、部品を描き直させてもう一度
        var render = Render();
        var renderBlank = SettingsCapture.BlankFields(Pixels(render), render.Width, fields);
        if (renderBlank.Count > 0)
        {
            foreach (var cb in _inputs.Values.OfType<ComboBox>()) { cb.Invalidate(); cb.Update(); }
            render.Dispose();
            render = Render();
            renderBlank = SettingsCapture.BlankFields(Pixels(render), render.Width, fields);
        }
        try
        {
            if (renderBlank.Count == 0)
            {
                render.Save(path, ImageFormat.Png);
                return new(SettingsCapture.Describe("render", [], [], null), values, []);
            }
            // 2. 空欄の部品だけを画面から写して差し込む(その部品の上に別の窓が重なっていないときだけ。利用者に見えているもの)
            var fromScreen = new List<string>();
            var notes = new List<string>();
            using (var g = Graphics.FromImage(render))
                foreach (var key in renderBlank)
                {
                    var cb = (ComboBox)_inputs[key];
                    var why = CopyFromScreen(cb, g);
                    if (why is null) fromScreen.Add(key); else notes.Add($"{key} {why}");
                }
            // 3. 画面にも写らない: 部品の値を欄に書き足して残し、書き足したことを capture と filled に残す(黙って写ったことにしない)
            var filled = renderBlank.Except(fromScreen).ToList();
            using (var g = Graphics.FromImage(render))
                foreach (var f in fields.Where(f => filled.Contains(f.Key)))
                {
                    var cb = (ComboBox)_inputs[f.Key];
                    TextRenderer.DrawText(g, f.Text, cb.Font, new Rectangle(f.X, f.Y, f.W, f.H), cb.ForeColor, cb.BackColor,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                }
            render.Save(path, ImageFormat.Png);
            if (fromScreen.Count > 0) notes.Insert(0, "from screen: " + string.Join(",", fromScreen));
            return new(SettingsCapture.Describe("render", renderBlank, filled, notes.Count == 0 ? null : string.Join("; ", notes)), values, filled);
        }
        finally { render.Dispose(); }
    }

    private Bitmap Render()
    {
        var bmp = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        DrawToBitmap(bmp, new Rectangle(Point.Empty, Size));
        return bmp;
    }

    /// <summary>
    /// 部品 <paramref name="cb"/> を画面から写し、値が写っていれば render の同じ位置へ描き込む(null)。部品の上に別の窓が重なっている・写っていなければ理由。
    /// </summary>
    private string? CopyFromScreen(ComboBox cb, Graphics into)
    {
        try
        {
            Native.DwmFlush();
            var r = cb.RectangleToScreen(cb.ClientRectangle);
            foreach (var pt in new[] { new Point(r.X + 4, r.Y + r.Height / 2), new Point(r.X + r.Width / 2, r.Y + r.Height / 2), new Point(r.Right - 4, r.Y + r.Height / 2) })
                if (Native.WindowFromPoint(new Native.POINT { X = pt.X, Y = pt.Y }) != cb.Handle) return "overlapped on screen";
            using var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                nint dst = g.GetHdc(), src = Native.GetDC(0);
                try { if (!Native.BitBlt(dst, 0, 0, r.Width, r.Height, src, r.X, r.Y, Native.SRCCOPY | Native.CAPTUREBLT)) return "BitBlt failed"; }
                finally { Native.ReleaseDC(0, src); g.ReleaseHdc(dst); }
            }
            int inset = 3, arrow = SystemInformation.VerticalScrollBarWidth + 2;
            if (!SettingsCapture.HasInk(Pixels(bmp), bmp.Width, inset, 2, bmp.Width - inset - arrow, bmp.Height - 4)) return "blank on screen too";
            into.DrawImageUnscaled(bmp, r.X - Bounds.X, r.Y - Bounds.Y);
            return null;
        }
        catch (Exception ex) { return ex.GetType().Name; }
    }

    /// <summary>選択式の部品の文字の欄(枠と▼を除いた内側。上の欄で見えない所は除く)を、窓全体の画の座標で。</summary>
    private (string Key, string Text, int X, int Y, int W, int H) Field(string key, ComboBox cb)
    {
        var r = cb.RectangleToScreen(cb.ClientRectangle);
        for (Control? p = cb.Parent; p is not null && p != this; p = p.Parent) r.Intersect(p.RectangleToScreen(p.ClientRectangle));
        r.Intersect(RectangleToScreen(ClientRectangle));
        var inner = cb.RectangleToScreen(new Rectangle(3, 2, Math.Max(0, cb.ClientSize.Width - 3 - SystemInformation.VerticalScrollBarWidth - 2), Math.Max(0, cb.ClientSize.Height - 4)));
        inner.Intersect(r);
        return (key, cb.Text, inner.X - Bounds.X, inner.Y - Bounds.Y, inner.Width, inner.Height);
    }

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

    private void Save()
    {
        var next = SettingsSchema.Clone(_original);
        var errors = new List<string>();
        foreach (var item in SettingsSchema.Items)
            if (SettingsSchema.Apply(next, item.Key, ValueOf(_inputs[item.Key])) is { } err) errors.Add(err);
        if (errors.Count > 0) { _error.Text = string.Join(Environment.NewLine, errors); return; }
        _error.Text = "";
        try { next.Save(_path); }
        catch (Exception ex) { _error.Text = "保存できませんでした: " + ex.Message; return; }
        var changed = SettingsSchema.Changed(_original, next);
        AppLog.Write($"settings-saved changed={string.Join(",", changed)}");
        Saved?.Invoke(_original, next, changed);
        DialogResult = DialogResult.OK;
        Close();
    }
}

/// <summary>検証モードが撮った設定画面: 結果の capture(写し方と注記)、撮った時点で画面に出ている各項目の値(設定ファイルの形)、値が写らず書き足した項目。</summary>
internal sealed record SettingsShot(string Capture, Dictionary<string, string> Values, List<string> Filled);
