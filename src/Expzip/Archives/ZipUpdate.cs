using System.IO;
using System.IO.Compression;
using ICSharpCode.SharpZipLib.Zip;

// 標準ライブラリにも同じ名前の型があるため、こちら側の名前をはっきりさせる
using SharpZipFile = ICSharpCode.SharpZipLib.Zip.ZipFile;

namespace Expzip.Archives;

/// <summary>
/// ZIP書庫を書き換えるときの土台 (#38)。
/// </summary>
/// <remarks>
/// <para>
/// <b>なぜ <see cref="System.IO.Compression"/> を使わないか。</b>
/// <c>ZipArchiveMode.Update</c> は<b>書庫全体をメモリに載せる</b>。800MB の書庫から
/// 1件消すだけでヒープが 800MB まで伸び、数GB級では失敗する。実測では
/// SharpZipLib の更新が同じ操作を <b>1MB</b> で済ませた (#38)。
/// </para>
/// <para>
/// <b>変えないエントリは圧縮し直さない。</b> SharpZipLib は圧縮済みのデータを
/// そのまま写す。300件の書庫に1件足しても、元の300件は圧縮後サイズもCRCも
/// 変わらないことを確かめてある。
/// </para>
/// <para>
/// <b>元の書庫は書き換え終わるまで触らない。</b> SharpZipLib は別のファイルへ
/// 書いてから差し替える。途中で失敗しても元の書庫はバイト単位でそのまま残る。
/// 書き込みの失敗で書庫を壊すことが最も避けたい事態のため、この性質に頼る。
/// </para>
/// </remarks>
internal static class ZipUpdate
{
    /// <summary>
    /// 書き換えのために書庫を開く。
    /// </summary>
    /// <remarks>
    /// EFSフラグが立たないエントリ名の解釈に、一覧で使っているのと同じ判定器 (#13) を
    /// 渡す。こうしないと、従来の日本語書庫を書き換えたときに名前が化ける。
    /// </remarks>
    public static SharpZipFile Open(string archivePath, string? password = null)
        => new(archivePath)
        {
            Password = password,
            StringCodec = StringCodec.FromEncoding(ZipArchiveReader.EntryNameEncoding),
        };

    /// <summary>
    /// 圧縮の強さを ZIP の圧縮方式に対応付ける (#11)。
    /// </summary>
    /// <remarks>
    /// 選べるのは「格納のみ」と「圧縮する」の2つ (仕様書 5.3)。SharpZipLib の更新は
    /// deflate の段階を渡せず、実測でも段階を上げてもほとんど縮まなかったため。
    /// </remarks>
    public static CompressionMethod MethodOf(CompressionLevel level)
        => level == CompressionLevel.NoCompression
            ? CompressionMethod.Stored
            : CompressionMethod.Deflated;

    /// <summary>書き出す1件分のエントリを組み立てる。</summary>
    /// <param name="name">書庫内での名前。区切りは <c>/</c>。</param>
    /// <param name="level">圧縮の強さ。</param>
    /// <param name="lastWriteTime">最終更新日時。</param>
    /// <remarks>
    /// 名前は UTF-8 + EFSフラグで書く。読み取りの CP932 判定 (#13) は古い書庫を
    /// 救うためのもので、こちらから書くものをあえて古い形式にする理由は無い。
    /// </remarks>
    public static ZipEntry NewEntry(string name, CompressionLevel level, DateTime lastWriteTime)
        => new(name)
        {
            CompressionMethod = MethodOf(level),
            IsUnicodeText = true,
            DateTime = lastWriteTime,
        };

    /// <summary>
    /// ディスク上のファイルを、中断を効かせながら渡す供給元。
    /// </summary>
    /// <remarks>
    /// SharpZipLib が中身を読むのは <c>CommitUpdate</c> の最中で、そこに割り込む口は
    /// 無い。読み取りの側で中断を投げると書き換えが取りやめになり、元の書庫は
    /// そのまま残る。大きなファイルの追加を途中で止められるのはこのため。
    /// </remarks>
    /// <param name="path">読み出すファイル。</param>
    /// <param name="cancellationToken">中断用。</param>
    /// <param name="onRead">読んだバイト数の通知先。進捗に使う。</param>
    public sealed class FileSource(
        string path, CancellationToken cancellationToken, Action<int>? onRead = null)
        : IStaticDataSource
    {
        public Stream GetSource()
            => new WatchedStream(File.OpenRead(path), cancellationToken, onRead);
    }

    /// <summary>読むたびに中断を確かめ、読んだ量を知らせる包み。</summary>
    private sealed class WatchedStream(
        Stream inner, CancellationToken cancellationToken, Action<int>? onRead) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var read = inner.Read(buffer, offset, count);
            if (read > 0)
            {
                onRead?.Invoke(read);
            }

            return read;
        }

        public override void Flush() => inner.Flush();

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
