using System.IO;
using System.IO.Compression;
using SharpCompress.Archives;

namespace Expzip.Archives;

/// <summary>
/// 標準の ZIP 実装が復号できない圧縮方式のエントリを、SharpCompress で読む (#66)。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="System.IO.Compression"/> が復号できるのは<b>格納・Deflate・Deflate64</b>
/// の3つだけで、7-Zip などが作る LZMA / PPMd / BZip2 方式の ZIP は
/// <see cref="ZipArchiveEntry.Open"/> の時点で例外になる。一覧は中央ディレクトリを
/// 読むだけなので方式に関わらず出てしまい、利用者からは「中身は見えているのに
/// 取り出せない」状態に見える。
/// </para>
/// <para>
/// ふだんの ZIP の道筋は変えない。標準の実装で開けなかったエントリだけをここへ回す。
/// ZIP の書き換えは「一部の差し替え」で作り込んであり (#38)、文字コード判定 (#13) や
/// 出所の印の扱いも標準側に寄せてあるため、丸ごと置き換える利点がないため。
/// </para>
/// <para>
/// 書庫を開き直すのは<b>実際に必要になったときだけ</b>。ふつうの ZIP では
/// SharpCompress を触らない。
/// </para>
/// </remarks>
internal sealed class ZipMethodFallback(string archivePath, string? password = null)
    : IDisposable
{
    private IArchive? _archive;
    private Dictionary<string, IArchiveEntry>? _entries;
    private bool _unavailable;

    /// <summary>
    /// まず標準の道で開き、扱えなかった場合だけ開き直す。
    /// </summary>
    /// <param name="entryName">書庫内でのエントリ名。</param>
    /// <param name="primary">標準の道で中身を開く手続き。</param>
    /// <remarks>
    /// 例外の型で絞らないのは、扱えない方式や鍵長に当たったときに何が飛んでくるかが
    /// ライブラリ任せのため。SharpZipLib は鍵長を受け付けないとき<b>素の
    /// <see cref="Exception"/></b> を投げ (#67)、標準の ZIP 実装は方式を扱えないとき
    /// <see cref="InvalidDataException"/> を投げる (#66)。型を並べて追いかけると
    /// 拾い漏れる。
    /// 開き直して読めなければ元の失敗をそのまま投げ直すので、取り違えても結果は変わらない。
    /// </remarks>
    public Stream Open(string entryName, Func<Stream> primary)
    {
        try
        {
            return primary();
        }
        catch (Exception ex) when (ex is not OperationCanceledException
                                   and not OutOfMemoryException)
        {
            var source = TryOpen(entryName);

            // 開き直しても読めなければ、元の失敗として報告する
            if (source is null)
            {
                throw;
            }

            _ = ex;
            return source;
        }
    }

    /// <summary>
    /// 指定したエントリの中身を読む流れを返す。扱えない場合は <see langword="null"/>。
    /// </summary>
    public Stream? TryOpen(string entryName)
    {
        if (!TryLoad())
        {
            return null;
        }

        if (!_entries!.TryGetValue(entryName, out var entry))
        {
            return null;
        }

        try
        {
            return new TranslatedStream(entry.OpenEntryStream());
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException
                                   or NotSupportedException or InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>
    /// 読み取りの失敗を <see cref="InvalidDataException"/> に揃える包み (#66)。
    /// </summary>
    /// <remarks>
    /// SharpCompress は方式ごとに独自の例外を投げる (LZMA なら
    /// <c>DataErrorException</c> など)。呼ぶ側はどれも「読めなかった」として
    /// 同じに扱えばよいのに、型を並べて追いかけると新しい方式が増えるたびに
    /// 拾い漏れ、書庫1つで画面ごと落ちる。境目でここに揃えておく。
    /// </remarks>
    private sealed class TranslatedStream(Stream inner) : Stream
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
            try
            {
                return inner.Read(buffer, offset, count);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidDataException(ex.Message, ex);
            }
        }

        public override void Flush()
        {
        }

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

    private bool TryLoad()
    {
        if (_entries is not null)
        {
            return true;
        }

        if (_unavailable)
        {
            return false;
        }

        try
        {
            _archive = ArchiveFactory.OpenArchive(
                archivePath, SharpArchiveAccess.Options(password));

            // 同じ名前のエントリが複数ある細工された書庫では、先に出てきたものを使う。
            // 標準の実装が一覧に出すのも先頭のエントリのため、見えているものと揃う
            _entries = new Dictionary<string, IArchiveEntry>(StringComparer.Ordinal);

            foreach (var entry in _archive.Entries)
            {
                if (!entry.IsDirectory && entry.Key is { } key)
                {
                    _entries.TryAdd(key, entry);
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or InvalidOperationException or NotSupportedException
                                   or InvalidDataException)
        {
            _unavailable = true;
            _archive?.Dispose();
            _archive = null;
            return false;
        }
    }

    public void Dispose()
    {
        _archive?.Dispose();
        _archive = null;
        _entries = null;
    }
}
