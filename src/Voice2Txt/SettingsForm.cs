using System.Diagnostics;
using System.Globalization;
using Voice2Txt.Core;

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

        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(0, 0, 0, 8) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 380));
        foreach (var item in SettingsSchema.Items)
        {
            var name = new Label { Text = item.Label, AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 8, 8, 0) };
            var input = MakeInput(item, SettingsSchema.Read(_original, item.Key));
            input.Name = item.Key;
            input.Margin = new Padding(0, 4, 0, 0);
            _inputs[item.Key] = input;
            var desc = new Label { Text = item.Description, AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(380, 0), Margin = new Padding(0, 2, 0, 6) };
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
            AutoSize = true, MaximumSize = new Size(570, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(0, 4, 0, 4),
        };
        var openFile = new LinkLabel { Text = "設定ファイルを開く", AutoSize = true, Margin = new Padding(0, 0, 0, 8) };
        openFile.LinkClicked += (_, _) => Process.Start(new ProcessStartInfo("notepad.exe", $"\"{_path}\"") { UseShellExecute = true });
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Top };
        buttons.Controls.Add(_cancel);
        buttons.Controls.Add(_save);
        _save.Click += (_, _) => Save();
        _cancel.Click += (_, _) => Close();

        var stack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        stack.Controls.AddRange([grid, note, openFile, _error, buttons]);
        Controls.Add(stack);
    }

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

    /// <summary>画面の今の姿(検証モードのスクショ)。フォーカスや重なりに左右されないよう自前で描く。</summary>
    public void SaveScreenshot(string path)
    {
        using var bmp = new Bitmap(Width, Height);
        DrawToBitmap(bmp, new Rectangle(Point.Empty, Size));
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
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
