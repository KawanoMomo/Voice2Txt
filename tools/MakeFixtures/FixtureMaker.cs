using System.Globalization;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using System.Text;

namespace Voice2Txt.Tools;

/// <summary>
/// 台本(tests/scenarios)が使う合成音声の素材を作る。Windows の System.Speech(日本語の声)で 16 kHz モノラル 16 bit に書く。
/// 素材はリポジトリに入れず、テスト実行時に <c>test-results/fixtures/</c> へ作る(本物の声は入れない)。同じ PC・同じ声なら毎回同じバイト列になる。
/// 合成の声は語を読み違えることがあるので、作るときに合成した読み(音素)を返す。正解文と違う読みになったら、その語をかなにして書き直す。
/// </summary>
public static class FixtureMaker
{
    /// <summary>リポジトリ直下からの素材の置き場(台本の audio はこの下を指す)。</summary>
    public const string RelativeDir = "test-results/fixtures";

    /// <summary>名前 = 合成に渡す文(台本の期待はこれと同じ文を書く)。</summary>
    public static readonly IReadOnlyDictionary<string, string> Items = new Dictionary<string, string>
    {
        ["ja-one-sentence"] = "今日の会議の資料をチャットで送ります。",
        ["utt-01"] = "今日の設計レビューを始めます。",
        ["utt-02"] = "認証まわりのモジュールを分割する案について確認したいです。",
        ["utt-03"] = "テストの結果を共有しますので、各自で目を通してください。",
        ["utt-04"] = "来週のリリースに向けて、残りの課題を洗い出しましょう。",
        ["utt-05"] = "ログの出力形式を統一する必要があると考えています。",
        ["utt-06"] = "この資料は後でチャットに貼り付けておきます。",
        ["utt-07"] = "画面の配置について意見をください。",
        ["utt-08"] = "明日の打ち合わせは会議室でやります。",
        ["utt-09"] = "新しい担当者を紹介します。",
        ["utt-10"] = "以上で報告を終わります。",
        ["utt-11"] = "同じ窓の別の入力欄に貼られることを確認します。",
        ["utt-12"] = "申請書は、ええと、ええと、総務に出してください。",
        ["utt-13"] = "はい、了解です。",
        ["utt-14"] = "お願いします。",
        ["utt-15"] = "テストの結果を共有しますので、各自で目を通してください。以上で報告を終わります。",
        ["utt-16"] = "新しい資料は、共有フォルダに保存してあります。",
        ["utt-17"] = "ええ、あの、来週の打ち合わせは、えっと、会議室でやります。",
        ["utt-18"] = "設計書の第三章に、認証まわりのモジュールを分割する案と、ログの出力形式を統一する案を追記しましたので、確認をお願いします。",
        ["utt-19"] = "了解です。",
        ["utt-20"] = "送ります。",
        ["utt-21"] = "来週のリリースに向けて、設計書の第三章に認証まわりのモジュールを分割する案と、ログの出力形式を統一する案を追記しましたので、各自で目を通したうえで、明日の打ち合わせまでに画面の配置についても意見をください。",
        ["utt-22"] = "来週の出張について、Please confirm the hotel booking.と英語で書かれたメールが届いたので、返信をお願いします。", // Mixed: 英文は英語の男性の声
        ["utt-23"] = "部長が会議の最後に、Let's meet again next week.と言っていました。", // Mixed: 英文は英語の男性の声
        ["utt-24"] = "本日の定例会議では、まず先月の進捗状況について報告します。設計の見直しは予定通り終わり、試作品の組み立ても順調に進んでいます。次に、課題として上がっていた部品の調達遅れについてですが、取引先と相談した結果、来週の前半には納品される見込みです。そのため、検証作業の開始日は当初の計画から二日ほど遅れる可能性があります。ただし、全体の納期には影響がないと考えています。続いて、予算の執行状況を説明します。今月の支出は計画の範囲内に収まっており、追加の費用は発生していません。最後に、来月の予定を確認します。新しい担当者が二人加わるため、最初の一週間は研修に充てる予定です。質問や意見があれば、会議の後でも構いませんので、遠慮なくお知らせください。", // Mixed: 10 文を間を置いて読む(約 60 秒。whisper の窓を 2 つ以上使う)
        ["utt-25"] = "ひとつ、ふたつ、みっつ、よっつ、いつつ、ここのつ、とお。", // かなで数える。末尾の「とお」(約 0.4 秒)は「つ」が付かない数
        ["utt-26"] = "ファイル名は、レポート、アンダーバー、最終、ドット、ピーディーエフです。", // 記号を読み上げる語。確定版の復号が「report.pdf」にまとめて間の語を飛ばす
        // 約 33 秒で末尾が短い結び「以上です」(声の芯 0.45 秒ほど)。whisper が 30 秒の窓の手前で返し終え、結びだけが後ろに残る
        ["utt-27"] = "それでは定例の会議を始めます。本日の議題は三つです。一つ目は先週のリリースの振り返り、二つ目は来月の開発計画、三つ目は新しい担当者の割り当てです。まず先週のリリースについて、大きな問題は起きませんでしたが、設定画面の表示崩れが二件報告されています。原因は調査中で、担当は田中さんです。次の会議までに結果を共有してもらいます。以上です。",
        ["utt-28"] = "障害対応の手順の見直しについて報告します。先月の夜間の障害では、連絡が担当者に届くまでに三十分かかりました。連絡網が古いままだったことが原因です。そこで、当番表を新しくし、通知の方法も一本化します。また、対応の記録を残す書式を決め、障害のあとの振り返りを必ず行うことにします。準備は来週中に終える予定です。以上です。",
    };

    /// <summary>無音だけの素材(秒)。無音で押して離したら取り消し、を確かめる。</summary>
    public static readonly IReadOnlyDictionary<string, double> Silences = new Dictionary<string, double> { ["silence"] = 3 };

    /// <summary>前後に置く無音(秒)。押してからしばらく黙り、話し終えてもしばらく離さない発話(長い無音で音声に無い文が足されないか)。</summary>
    public static readonly IReadOnlyDictionary<string, (int Head, int Tail)> Pads = new Dictionary<string, (int, int)>
    {
        ["utt-15"] = (5, 20),
        ["utt-16"] = (1, 1),
    };

    /// <summary>
    /// 小さい声(最も大きい 30 ms 区間の RMS: 最初の息継ぎまで, その後)。無音の取り消しのしきい値(初期値 0.01)を少し超えるだけの声で話し始め、
    /// 息継ぎの後はしきい値を下回る声になる録音で、後ろの語が削られないか。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (double Level, double Tail)> Levels = new Dictionary<string, (double, double)>
    {
        ["utt-16"] = (0.015, 0.006),
        ["utt-19"] = (0.005, 0),
        ["utt-20"] = (0.004, 0),
    };

    /// <summary>
    /// 録音機器の雑音(RMS)を重ねる。実際のマイクは黙っていても 0 にならない。小さい声(<see cref="Levels"/>)と組み合わせ、
    /// 無音のしきい値を下回る声でも、雑音よりはっきり大きければ取り消されないかを確かめる。乱数の種は固定(毎回同じバイト列)。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, double> Noises = new Dictionary<string, double>
    {
        ["utt-19"] = 0.0003,
        ["utt-20"] = 0.0003,
    };

    /// <summary>
    /// 文ごとに声を選び、間を置いて読む発話: 日本語の文の間に別の話者(英語の声)が読む英文を挟む、文を重ねて 30 秒を超える、など。名前 = 読む順の (声の言語, 声の性別, 文)。
    /// <see cref="Items"/> の文はこれをつないだもの(台本の期待と同じ)。声の切り替わりの前後に <see cref="MixedPause"/> 秒の間を置く。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (string Culture, VoiceGender Gender, string Text)[]> Mixed = new Dictionary<string, (string, VoiceGender, string)[]>
    {
        ["utt-22"] = [("ja-JP", VoiceGender.Female, "来週の出張について、"), ("en-US", VoiceGender.Male, "Please confirm the hotel booking."), ("ja-JP", VoiceGender.Female, "と英語で書かれたメールが届いたので、返信をお願いします。")],
        ["utt-23"] = [("ja-JP", VoiceGender.Female, "部長が会議の最後に、"), ("en-US", VoiceGender.Male, "Let's meet again next week."), ("ja-JP", VoiceGender.Female, "と言っていました。")],
        ["utt-24"] = [("ja-JP", VoiceGender.Female, "本日の定例会議では、まず先月の進捗状況について報告します。"), ("ja-JP", VoiceGender.Female, "設計の見直しは予定通り終わり、試作品の組み立ても順調に進んでいます。"), ("ja-JP", VoiceGender.Female, "次に、課題として上がっていた部品の調達遅れについてですが、取引先と相談した結果、来週の前半には納品される見込みです。"), ("ja-JP", VoiceGender.Female, "そのため、検証作業の開始日は当初の計画から二日ほど遅れる可能性があります。"), ("ja-JP", VoiceGender.Female, "ただし、全体の納期には影響がないと考えています。"), ("ja-JP", VoiceGender.Female, "続いて、予算の執行状況を説明します。"), ("ja-JP", VoiceGender.Female, "今月の支出は計画の範囲内に収まっており、追加の費用は発生していません。"), ("ja-JP", VoiceGender.Female, "最後に、来月の予定を確認します。"), ("ja-JP", VoiceGender.Female, "新しい担当者が二人加わるため、最初の一週間は研修に充てる予定です。"), ("ja-JP", VoiceGender.Female, "質問や意見があれば、会議の後でも構いませんので、遠慮なくお知らせください。")],
    };

    /// <summary><see cref="Mixed"/> の文と文の間に置く間(秒)。</summary>
    public const double MixedPause = 0.5;

    public static IEnumerable<string> Names => Items.Keys.Concat(Silences.Keys);

    /// <summary>
    /// 足りない素材を <paramref name="dir"/> に作る(<paramref name="force"/> なら全部作り直す)。<paramref name="only"/> を渡せばその名前だけ。
    /// 作った素材ごとに "名前: 読み" を <paramref name="log"/> へ返す。
    /// </summary>
    public static int Ensure(string dir, bool force = false, IReadOnlyCollection<string>? only = null, Action<string>? log = null)
    {
        Directory.CreateDirectory(dir);
        int made = 0;
        foreach (var name in Names)
        {
            if (only is { Count: > 0 } && !only.Contains(name)) continue;
            var wav = Path.Combine(dir, name + ".wav");
            if (!force && File.Exists(wav)) continue;
            var tmp = wav + ".tmp";
            string said;
            if (Silences.TryGetValue(name, out var sec)) { WriteSilence(tmp, sec); said = $"無音 {sec} 秒"; }
            else
            {
                var pad = Pads.TryGetValue(name, out var p) ? p : (0, 0);
                said = Mixed.TryGetValue(name, out var parts) ? SpeakMixed(parts, tmp) : Speak(Items[name], tmp, pad.Item1, pad.Item2);
                if (Levels.TryGetValue(name, out var lv)) said += $" ({Quiet(tmp, lv.Level, lv.Tail)})";
                if (Noises.TryGetValue(name, out var nz)) { AddNoise(tmp, nz); said += string.Format(CultureInfo.InvariantCulture, " (雑音 RMS {0})", nz); }
            }
            File.Move(tmp, wav, overwrite: true); // 並行して走る別の実行が書きかけを読まないよう、書き終えてから置く
            made++;
            log?.Invoke($"{name}: {said}");
        }
        return made;
    }

    /// <summary>リポジトリ直下(Voice2Txt.sln のあるフォルダ)を <paramref name="start"/> から上へ探す。</summary>
    public static string? FindRepoRoot(string start)
    {
        for (var d = new DirectoryInfo(Path.GetFullPath(start)); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Voice2Txt.sln"))) return d.FullName;
        return null;
    }

    private static string Speak(string text, string wav, int headSec, int tailSec)
    {
        using var s = new SpeechSynthesizer();
        foreach (var v in s.GetInstalledVoices())
            if (v.VoiceInfo.Culture.Name == "ja-JP") { s.SelectVoice(v.VoiceInfo.Name); break; }
        if (s.Voice.Culture.Name != "ja-JP") throw new InvalidOperationException("日本語(ja-JP)の音声合成の声が入っていない(Windows の設定 → 時刻と言語 → 音声 で日本語の音声を追加する)");
        s.Rate = 0;
        var sb = new StringBuilder();
        s.PhonemeReached += (_, e) => sb.Append(e.Phoneme);
        s.SetOutputToWaveFile(wav, new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
        var p = new PromptBuilder(new CultureInfo("ja-JP"));
        if (headSec > 0) p.AppendBreak(TimeSpan.FromSeconds(headSec));
        p.AppendText(text);
        if (tailSec > 0) p.AppendBreak(TimeSpan.FromSeconds(tailSec));
        s.Speak(p);
        s.SetOutputToNull();
        return sb.ToString();
    }

    /// <summary>文ごとに声を切り替えて 1 本に読む(<see cref="Mixed"/>)。その言語・性別の声が入っていなければ失敗する。</summary>
    private static string SpeakMixed((string Culture, VoiceGender Gender, string Text)[] parts, string wav)
    {
        using var s = new SpeechSynthesizer();
        var voices = s.GetInstalledVoices().Select(v => v.VoiceInfo).ToList();
        var sb = new StringBuilder();
        s.PhonemeReached += (_, e) => sb.Append(e.Phoneme);
        s.SetOutputToWaveFile(wav, new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
        var p = new PromptBuilder(new CultureInfo("ja-JP"));
        for (int i = 0; i < parts.Length; i++)
        {
            var (culture, gender, text) = parts[i];
            var v = voices.FirstOrDefault(x => x.Culture.Name == culture && x.Gender == gender)
                ?? throw new InvalidOperationException($"{culture} の{(gender == VoiceGender.Male ? "男性" : "女性")}の音声合成の声が入っていない(Windows の設定 → 時刻と言語 → 音声 で追加する)");
            if (i > 0) p.AppendBreak(TimeSpan.FromSeconds(MixedPause));
            p.StartVoice(v.Name);
            p.AppendText(text);
            p.EndVoice();
        }
        s.Speak(p);
        s.SetOutputToNull();
        return sb.ToString();
    }

    /// <summary>RMS が <paramref name="rms"/> の雑音(正規分布、種 1)を全体に重ねる。</summary>
    private static void AddNoise(string wav, double rms)
    {
        var b = File.ReadAllBytes(wav);
        int o = 12;
        while (Encoding.ASCII.GetString(b, o, 4) != "data") o += 8 + BitConverter.ToInt32(b, o + 4);
        int d = o + 8, n = BitConverter.ToInt32(b, o + 4) / 2;
        var rnd = new Random(1);
        for (int j = 0; j < n; j++)
        {
            double g = Math.Sqrt(-2 * Math.Log(1 - rnd.NextDouble())) * Math.Cos(2 * Math.PI * rnd.NextDouble());
            double x = BitConverter.ToInt16(b, d + j * 2) / 32768.0 + g * rms;
            short v = (short)Math.Clamp(Math.Round(x * 32768.0), short.MinValue, short.MaxValue);
            b[d + j * 2] = (byte)(v & 0xFF); b[d + 1 + j * 2] = (byte)((v >> 8) & 0xFF);
        }
        File.WriteAllBytes(wav, b);
    }

    /// <summary>16 kHz モノラル 16 bit の無音(標準の 44 バイトのヘッダ)。</summary>
    private static void WriteSilence(string wav, double seconds)
    {
        int bytes = (int)(seconds * 16000) * 2;
        using var w = new BinaryWriter(File.Create(wav));
        w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(16000); w.Write(32000); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(bytes); w.Write(new byte[bytes]);
    }

    /// <summary>
    /// 小さい声・遠いマイクの録音: 最も大きい 30 ms 区間の RMS が level になるまで音量を下げる。
    /// tail &gt; 0 なら、最初の息継ぎ(0.15 秒以上の無音)より後ろを、最も大きい区間が tail になるまでさらに下げる(話すうちに声が小さくなる)。
    /// </summary>
    private static string Quiet(string wav, double level, double tail)
    {
        var b = File.ReadAllBytes(wav);
        int o = 12;
        while (Encoding.ASCII.GetString(b, o, 4) != "data") o += 8 + BitConverter.ToInt32(b, o + 4);
        int d = o + 8, n = BitConverter.ToInt32(b, o + 4) / 2, frame = 480;
        var x = new double[n];
        for (int j = 0; j < n; j++) x[j] = BitConverter.ToInt16(b, d + j * 2) / 32768.0;
        int frames = (n + frame - 1) / frame;
        var rms = new double[frames];
        for (int f = 0; f < frames; f++)
        {
            double acc = 0; int a = f * frame, m = Math.Min(n, a + frame) - a;
            for (int j = a; j < a + m; j++) acc += x[j] * x[j];
            rms[f] = Math.Sqrt(acc / m);
        }
        double peak = 0; foreach (var r in rms) peak = Math.Max(peak, r);
        int split = frames;
        if (tail > 0)
        {
            int first = 0; while (first < frames && rms[first] < peak * 0.05) first++;
            for (int f = first, quiet = 0; f < frames; f++)
            {
                quiet = rms[f] < peak * 0.01 ? quiet + 1 : 0;
                if (quiet >= 5) { split = f; break; }
            }
        }
        double head = 0, rest = 0;
        for (int f = 0; f < frames; f++) { if (f < split) head = Math.Max(head, rms[f]); else rest = Math.Max(rest, rms[f]); }
        double g1 = level / peak, g2 = rest > 0 ? tail / rest : g1;
        for (int j = 0; j < n; j++)
        {
            short v = (short)Math.Round(x[j] * 32768.0 * (j / frame < split ? g1 : g2));
            b[d + j * 2] = (byte)(v & 0xFF); b[d + 1 + j * 2] = (byte)((v >> 8) & 0xFF);
        }
        File.WriteAllBytes(wav, b);
        return string.Format(CultureInfo.InvariantCulture, "x{0:N3}, {1:N1} 秒から x{2:N3}", g1, split * 0.03, g2);
    }
}
