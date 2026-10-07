using System.Diagnostics;
using System.Net;

namespace Voice2Txt.Core.Verification;

/// <summary>
/// 検証モードのモデルの取得元: 取得元の URL のファイル名を手元のフォルダのファイルから返す(ネットに出ない)。
/// <see cref="ModelProvisioner"/> の本物のダウンロード・ハッシュ検証・進み具合の経路をそのまま通し、
/// 取得中の表示を確かめられるように <paramref name="durationMs"/> かけて少しずつ返す。
/// </summary>
public sealed class LocalModelSourceHandler(string sourceDir, int durationMs) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = Path.Combine(sourceDir, Path.GetFileName(request.RequestUri!.LocalPath));
        if (!File.Exists(path))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
        var fs = File.OpenRead(path);
        var content = new StreamContent(new PacedStream(fs, durationMs), 1 << 20);
        content.Headers.ContentLength = fs.Length;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content, RequestMessage = request });
    }

    /// <summary>読んだ量が経過時間に比例するように待ちを入れる読み取り専用のストリーム(1 回の読みは全体の 1/100 まで)。</summary>
    private sealed class PacedStream(Stream inner, int durationMs) : Stream
    {
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private readonly long _length = inner.Length;
        private long _done;

        private int Chunk(int count) => (int)Math.Min(count, Math.Max(64 * 1024, _length / 100));

        private int DelayMs()
        {
            if (_length <= 0) return 0;
            long due = _done * durationMs / _length;
            return (int)Math.Max(0, due - _sw.ElapsedMilliseconds);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (DelayMs() is > 0 and var d) await Task.Delay(d, ct);
            int n = await inner.ReadAsync(buffer[..Chunk(buffer.Length)], ct);
            _done += n;
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (DelayMs() is > 0 and var d) Thread.Sleep(d);
            int n = inner.Read(buffer, offset, Chunk(count));
            _done += n;
            return n;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _done; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
