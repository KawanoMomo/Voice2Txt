using System.Security.Cryptography;

namespace Voice2Txt.Core;

/// <summary>取得元は whisper.cpp の公式モデル(Hugging Face ggerganov/whisper.cpp)に固定。ハッシュは取得元の SHA-256。</summary>
public sealed record ModelEntry(string Name, string FileName, string Url, string Sha256, long Size);

public static class ModelCatalog
{
    public const string DefaultName = "large-v3-turbo";
    private const string Source = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/";

    private static ModelEntry Official(string name, string sha256, long size) =>
        new(name, $"ggml-{name}.bin", Source + $"ggml-{name}.bin", sha256, size);

    /// <summary>設定の model で選べるモデル(小さい順)。多言語版のみ(日本語を話すため .en は置かない)。</summary>
    public static readonly IReadOnlyList<ModelEntry> Entries =
    [
        Official("tiny", "be07e048e1e599ad46341c8d2a135645097a538221678b7acdd1b1919c6e1b21", 77691713),
        Official("base", "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe", 147951465),
        Official("small", "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b", 487601967),
        Official("medium", "6c14d5adee5f86394037b4e4e8b59f1673b6cee10e3cf0b11bbdbee79c156208", 1533763059),
        Official(DefaultName, "1fc70f774d38eb169993ac391eea357ef47c88757ef72ee5943879b7e8e2bc69", 1624555275),
        Official("large-v3", "64d182b440b98d5203c4f9bd541544d84c605196c4f7b845dfa11fb23594d1e2", 3095033483),
    ];

    public static readonly IReadOnlyDictionary<string, ModelEntry> All =
        Entries.ToDictionary(e => e.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>選べる名前の一覧(知らせる文に入れる)。</summary>
    public static string Names => string.Join(", ", Entries.Select(e => e.Name));

    /// <summary>名前(大文字小文字は問わない)かファイル名(例 ggml-small.bin)で引く。</summary>
    public static bool TryGet(string? name, out ModelEntry entry)
    {
        var n = name?.Trim() ?? "";
        entry = All.TryGetValue(n, out var e) ? e
            : Entries.FirstOrDefault(x => string.Equals(x.FileName, n, StringComparison.OrdinalIgnoreCase))!;
        return entry is not null;
    }

    public static ModelEntry Get(string? name) =>
        TryGet(name, out var e) ? e : throw new ArgumentException(UnknownMessage(name));

    /// <summary>model を引けないときの知らせ(エラー文・ログ・トレイの通知に使う)。</summary>
    public static string UnknownMessage(string? name) =>
        $"未知のモデル: {name}(設定の model に選べる名前: {Names}。変更は再起動で効く)";

    public static string DefaultModelsDirectory => Path.Combine(AppSettings.DefaultDirectory, "models");
}

/// <summary>モデルの用意: 決まったフォルダにあればハッシュを確かめて使う(手動配置も可)。無ければ取得元からダウンロードし、ハッシュで検証してから置く。</summary>
public sealed class ModelProvisioner(string modelsDir, HttpClient? http = null)
{
    public string PathOf(ModelEntry e) => Path.Combine(modelsDir, e.FileName);

    /// <param name="progress">0.0〜1.0。ダウンロードと検証の進み具合。</param>
    public async Task<string> EnsureAsync(ModelEntry e, IProgress<(string Phase, double Ratio)>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(modelsDir);
        var path = PathOf(e);
        var marker = path + ".verified";
        if (File.Exists(path))
        {
            var fi = new FileInfo(path);
            var stamp = $"{fi.Length}:{fi.LastWriteTimeUtc.Ticks}:{e.Sha256}";
            if (File.Exists(marker) && File.ReadAllText(marker) == stamp) return path;
            progress?.Report(("検証", 0));
            var h = await HashFileAsync(path, fi.Length, r => progress?.Report(("検証", r)), ct);
            if (h == e.Sha256) { File.WriteAllText(marker, stamp); return path; }
            File.Move(path, path + ".bad", overwrite: true); // 壊れた・違うファイルは残して取り直す
        }
        await DownloadAsync(e, path, progress, ct);
        var nfi = new FileInfo(path);
        File.WriteAllText(marker, $"{nfi.Length}:{nfi.LastWriteTimeUtc.Ticks}:{e.Sha256}");
        return path;
    }

    private async Task DownloadAsync(ModelEntry e, string path, IProgress<(string, double)>? progress, CancellationToken ct)
    {
        var client = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var part = path + ".part";
        using var resp = await client.GetAsync(e.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? e.Size, done = 0;
        using var sha = SHA256.Create();
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(part))
        {
            var buf = new byte[1 << 20];
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                sha.TransformBlock(buf, 0, n, null, 0);
                done += n;
                progress?.Report(("ダウンロード", total > 0 ? (double)done / total : 0));
            }
        }
        sha.TransformFinalBlock([], 0, 0);
        var got = Convert.ToHexStringLower(sha.Hash!);
        if (got != e.Sha256)
        {
            File.Delete(part);
            throw new InvalidDataException("モデルのハッシュが一致しない");
        }
        File.Move(part, path, overwrite: true);
    }

    public static async Task<string> HashFileAsync(string path, long length, Action<double>? onProgress, CancellationToken ct)
    {
        using var sha = SHA256.Create();
        await using var fs = File.OpenRead(path);
        var buf = new byte[1 << 22];
        long done = 0; int n;
        while ((n = await fs.ReadAsync(buf, ct)) > 0)
        {
            sha.TransformBlock(buf, 0, n, null, 0);
            done += n;
            onProgress?.Invoke(length > 0 ? (double)done / length : 0);
        }
        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexStringLower(sha.Hash!);
    }
}
