using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using SharpCompress.Common;
using SharpCompress.Compressors.BZip2;
using SharpCompress.Compressors.Xz;
using SharpCompress.Compressors.ZStandard;

namespace Expzip.Archives;

/// <summary>
/// ファイル 1 個を圧縮しただけのもの (<c>.gz</c> <c>.bz2</c> <c>.xz</c> <c>.zst</c>) を、
/// 中身 1 個の書庫として読む (#256、#257)。
/// </summary>
/// <remarks>
/// <para>
/// 読み取りのみ。1 個しか入らないので、追加や名前の変更が意味を持たない。
/// </para>
/// <para>
/// <b>開くときに最後まで伸長して、元の大きさを数える。</b>gzip は末尾に元の大きさを持つが、
/// 4 GB で一周する値なので、大きなファイルでは当てにならない。bzip2 / xz / Zstandard は
/// 伸長しないと分からない。圧縮された tar も一覧を作るのに最後まで読んでいるので、
/// かかる時間は同じ程度になる。読み通すので、壊れていれば開いた時点で分かる。
/// </para>
/// <para>
/// 伸長は、gzip は標準ライブラリ、ほかは SharpCompress の部品で行う。
/// SharpCompress の <c>ReaderFactory</c> は、圧縮の層の下が tar でないと bzip2 / xz /
/// Zstandard を読まない (実測で確認)。
/// </para>
/// </remarks>
internal static class CompressedFile
{
    /// <summary>進み具合を知らせる間隔。読んだ量がこの割合だけ進むごと。</summary>
    private const double ReportStep = 0.005;

    /// <summary>tar の見出しの大きさ。中身が tar かどうかはこれを見て決める。</summary>
    private const int TarBlock = 512;

    /// <summary>gzip の見出しを探す範囲。名前と注釈がこれより長いものは名前を使わない。</summary>
    private const int GZipHeaderLimit = 64 * 1024;

    /// <summary>中身 1 個を読み通して、書庫の内容に組み立てる。</summary>
    /// <exception cref="InvalidDataException">その形式の圧縮ファイルではない場合。</exception>
    /// <exception cref="DamagedDataException">伸長の途中で壊れたデータに当たった場合。</exception>
    /// <exception cref="IOException">ファイルを読めない場合。</exception>
    /// <exception cref="OperationCanceledException">中断された場合。</exception>
    public static ArchiveContents Open(
        string path,
        ArchiveFormat format,
        IProgress<OpenProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var header = ReadHeader(path, format);
        long length = 0;
        long compressed;

        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            using var file = ArchiveFile.OpenRead(path);
            compressed = file.Length;

            using var content = Decompress(file, format);

            var reported = 0.0;
            int read;

            while ((read = Read(content, buffer)) > 0)
            {
                length += read;
                cancellationToken.ThrowIfCancellationRequested();

                // 伸長の部品は先読みするので、元のファイルの位置で測ると少し先を指す。
                // 目安なので構わない
                var share = compressed == 0 ? 1 : Math.Min(1, (double)file.Position / compressed);
                if (share - reported >= ReportStep)
                {
                    reported = share;
                    progress?.Report(new OpenProgress(0, 1, share));
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        progress?.Report(new OpenProgress(1, 1));

        var builder = new ArchiveTreeBuilder(path);
        builder.AddFile(header.Name, length, compressed, compressedLengthKnown: true, header.LastWriteTime);

        return builder.Build(path, format);
    }

    /// <summary>中身を取り出す。引数の意味は <see cref="ArchiveExtractor.Extract"/> と同じ。</summary>
    public static ExtractResult Extract(
        string archivePath,
        ArchiveFormat format,
        IReadOnlySet<string>? sourceNames,
        string destinationDirectory,
        bool overwrite,
        IProgress<ExtractProgress>? progress,
        CancellationToken cancellationToken,
        string? zoneIdentifier = null,
        string? basePath = null)
    {
        var state = new ExtractState(
            Path.GetFullPath(destinationDirectory), overwrite, zoneIdentifier, basePath, progress);

        var header = ReadHeader(archivePath, format);

        if (sourceNames is null || sourceNames.Contains(header.Name))
        {
            // 進み具合は 1 件ごとにしか出ない。中身は 1 個なので、終わったときに 100% になるだけ
            state.Write(
                header.Name, 0, header.LastWriteTime,
                () => OpenContent(archivePath, format), cancellationToken);
        }

        return state.ToResult();
    }

    /// <summary>伸長した中身を読む流れを開く。</summary>
    /// <exception cref="InvalidDataException">その形式の圧縮ファイルではない場合。</exception>
    public static Stream OpenContent(string path, ArchiveFormat format)
        => Decompress(ArchiveFile.OpenRead(path), format);

    /// <summary>中身の名前。書庫の中のパスとしても使う。</summary>
    public static string EntryName(string path, ArchiveFormat format) => ReadHeader(path, format).Name;

    /// <summary>
    /// 中身が tar か (#256)。伸長した先頭の 512 バイトが tar の見出しになっているかを見る。
    /// </summary>
    /// <remarks>
    /// <c>ustar</c> の印は古い形式の tar には無い。見出しの検査値で確かめる。
    /// </remarks>
    public static bool ContainsTar(string path, ArchiveFormat format)
    {
        try
        {
            using var content = OpenContent(path, format);

            var block = new byte[TarBlock];
            return content.ReadAtLeast(block, TarBlock, throwOnEndOfStream: false) == TarBlock
                   && IsTarHeader(block);
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            return false;
        }
    }

    /// <summary>tar の見出しとして辻褄が合うか。</summary>
    private static bool IsTarHeader(ReadOnlySpan<byte> block)
    {
        // 名前の無い見出しは無い。全部 0 の塊は書庫の終わりの印
        if (block[0] == 0)
        {
            return false;
        }

        // 検査値は 8 進の文字で書かれ、0 か空白で終わる
        var field = block.Slice(148, 8);
        long recorded = 0;
        var digits = 0;

        foreach (var b in field)
        {
            if (b is (byte)' ' or 0)
            {
                if (digits > 0)
                {
                    break;
                }

                continue;
            }

            if (b is < (byte)'0' or > (byte)'7')
            {
                return false;
            }

            recorded = (recorded * 8) + (b - '0');
            digits++;
        }

        if (digits == 0)
        {
            return false;
        }

        // 検査値の欄は空白で埋めたものとして足す。符号付きで足す古い tar もある
        long unsigned = 0;
        long signed = 0;

        for (var i = 0; i < TarBlock; i++)
        {
            var b = i is >= 148 and < 156 ? (byte)' ' : block[i];
            unsigned += b;
            signed += (sbyte)b;
        }

        return recorded == unsigned || recorded == signed;
    }

    /// <summary>形式ごとに、伸長する流れを被せる。</summary>
    /// <remarks>先頭の印が合わなければ、その形式ではないとして断る。</remarks>
    private static Stream Decompress(Stream source, ArchiveFormat format)
    {
        try
        {
            if (!HasSignature(source, format))
            {
                throw new InvalidDataException("Not a compressed file of the expected format.");
            }

            source.Position = 0;

            Stream content = format switch
            {
                // 標準の gzip は、いくつか繋げたもの (cat a.gz b.gz) も最後まで読む
                ArchiveFormat.GZip => new GZipStream(source, CompressionMode.Decompress),
                ArchiveFormat.BZip2 => BZip2Stream.Create(
                    source, SharpCompress.Compressors.CompressionMode.Decompress,
                    decompressConcatenated: true, leaveOpen: true, tolerateTruncatedStream: false),
                ArchiveFormat.Xz => new XZStream(source),
                ArchiveFormat.Zstandard => new DecompressionStream(
                    source, bufferSize: 0, checkEndOfStream: true, leaveOpen: true),
                _ => throw new ArgumentOutOfRangeException(nameof(format)),
            };

            return new OwningStream(content, source);
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    /// <summary>先頭の印がその形式のものか。</summary>
    private static bool HasSignature(Stream source, ArchiveFormat format)
    {
        ReadOnlySpan<byte> signature = format switch
        {
            ArchiveFormat.GZip => [0x1F, 0x8B],
            ArchiveFormat.BZip2 => "BZh"u8,
            ArchiveFormat.Xz => [0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00],
            ArchiveFormat.Zstandard => [0x28, 0xB5, 0x2F, 0xFD],
            _ => [],
        };

        Span<byte> head = stackalloc byte[6];
        var read = source.ReadAtLeast(head, signature.Length, throwOnEndOfStream: false);

        return signature.Length > 0 && read >= signature.Length && head[..signature.Length].SequenceEqual(signature);
    }

    /// <summary>1 回分を読む。壊れたデータに当たったら、壊れていると言い直す。</summary>
    private static int Read(Stream content, byte[] buffer)
    {
        try
        {
            return content.Read(buffer, 0, buffer.Length);
        }
        catch (Exception ex) when (ex is EndOfStreamException
                                   || ex is not (OperationCanceledException or OutOfMemoryException
                                       or IOException or UnauthorizedAccessException))
        {
            // 伸長の部品は、壊れたデータに当たったときの例外がまちまち。見分けは展開と揃える
            // (CancellableCopy)。印は合っていたので、形式の取り違えではなく壊れているとみなす
            throw new DamagedDataException(ex);
        }
    }

    /// <summary>中身の名前と更新日時。</summary>
    private readonly record struct Header(string Name, DateTime LastWriteTime);

    /// <summary>
    /// 中身の名前と更新日時を決める。
    /// </summary>
    /// <remarks>
    /// <para>
    /// gzip は見出しに元の名前と更新日時を持つことがある (<c>gzip</c> コマンドは既定で入れる)。
    /// 名前の文字コードは決まっていないので、書庫の中の名前と同じ判定 (#13) で読む。
    /// </para>
    /// <para>
    /// 持たないもの (bzip2 / xz / Zstandard と、持たない gzip) は、書庫の名前から拡張子を
    /// 除いたものと、書庫ファイルの更新日時にする。伸長のコマンドが出すものと同じ。
    /// </para>
    /// </remarks>
    private static Header ReadHeader(string path, ArchiveFormat format)
    {
        // 分割されたもの (#61) は、番号を外した名前で考える
        var shown = SplitVolumes.IsFirstVolume(path) ? path[..^4] : path;
        var fallback = Path.GetFileNameWithoutExtension(shown);
        if (fallback.Length == 0)
        {
            fallback = Path.GetFileName(shown);
        }

        var time = FileTime(path);

        if (format != ArchiveFormat.GZip)
        {
            return new Header(fallback, time);
        }

        var (name, stamp) = ReadGZipHeader(path);

        if (stamp > 0)
        {
            time = DateTimeOffset.FromUnixTimeSeconds(stamp).LocalDateTime;
        }

        return new Header(name is not null && IsPlainName(name) ? name : fallback, time);
    }

    /// <summary>gzip の見出しから、元の名前と更新日時 (1970 年からの秒) を読む。</summary>
    private static (string? Name, long Stamp) ReadGZipHeader(string path)
    {
        const byte Extra = 0x04;
        const byte NameFlag = 0x08;

        try
        {
            using var stream = ArchiveFile.OpenRead(path);

            var buffer = new byte[(int)Math.Min(stream.Length, GZipHeaderLimit)];
            var length = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            var head = buffer.AsSpan(0, length);

            if (head.Length < 10 || head[0] != 0x1F || head[1] != 0x8B)
            {
                return (null, 0);
            }

            var flags = head[3];
            long stamp = BinaryPrimitives.ReadUInt32LittleEndian(head[4..]);
            var at = 10;

            if ((flags & Extra) != 0)
            {
                if (head.Length < at + 2)
                {
                    return (null, stamp);
                }

                at += 2 + BinaryPrimitives.ReadUInt16LittleEndian(head[at..]);
            }

            if ((flags & NameFlag) == 0 || at >= head.Length)
            {
                return (null, stamp);
            }

            var end = head[at..].IndexOf((byte)0);
            if (end <= 0)
            {
                return (null, stamp);
            }

            var raw = ZipArchiveReader.EntryNameEncoding.GetString(head.Slice(at, end));

            // 元の名前にフォルダーが付いていることがある。名前だけを使う
            var name = raw[(raw.LastIndexOfAny(['/', '\\']) + 1)..].Trim();
            return (name, stamp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, 0);
        }
    }

    /// <summary>ファイルの名前としてそのまま使えるか。</summary>
    private static bool IsPlainName(string name)
        => name.Length > 0
           && name is not ("." or "..")
           && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    /// <summary>書庫ファイルの更新日時。読めない場合は「分からない」。</summary>
    private static DateTime FileTime(string path)
    {
        try
        {
            return File.GetLastWriteTime(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return default;
        }
    }

    /// <summary>形式の取り違えや壊れたデータで、読めなかったときの例外か。</summary>
    private static bool IsReadFailure(Exception ex)
        => ex is IOException or InvalidDataException or UnauthorizedAccessException
            or SharpCompressException or InvalidOperationException or EndOfStreamException
            or IndexOutOfRangeException or ArgumentException;

    /// <summary>伸長の流れを閉じるときに、元のファイルも閉じる。</summary>
    /// <remarks>
    /// 伸長の部品によって、元の流れを閉じるかどうかが違う。ここで揃える。
    /// </remarks>
    private sealed class OwningStream(Stream inner, Stream source) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => inner.Read(buffer);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                source.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
