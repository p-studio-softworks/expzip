using System.IO;
using ICSharpCode.SharpZipLib.Zip;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

// 標準ライブラリにも同じ名前の型があるため、こちら側の名前をはっきりさせる
using SharpZipFile = ICSharpCode.SharpZipLib.Zip.ZipFile;

namespace Expzip.Archives;

/// <summary>書庫の中のファイル 1 個。<see cref="ArchiveContentReader.Visit"/> が渡す。</summary>
/// <param name="Name">書庫の中のパス。一覧に出す <see cref="ArchiveEntry.FullPath"/> と同じ形。</param>
/// <param name="Size">展開後の大きさ。読むまで分からないものは -1。</param>
/// <param name="ExpectedCrc">書庫に書かれた CRC32。持たない形式では -1。</param>
/// <param name="Locked">パスワードが要るのに渡されていない。開いても読めない。</param>
/// <param name="Open">中身を読む流れを開く。流れは呼び出しの中で閉じる。次のファイルへ進むと読めなくなる。</param>
internal sealed record ContentItem(string Name, long Size, long ExpectedCrc, bool Locked, Func<Stream> Open);

/// <summary>
/// 書庫の中のファイルを、形式ごとの読み方で 1 個ずつ、中身の流れとして渡す。
/// 書庫検査 (#53) とハッシュ値 (#265) で共通に使う。
/// </summary>
/// <remarks>
/// <para>
/// <b>ディスクには何も書かない。</b>取り出さずに中身を読むので、取り出すと何十 GB にもなる
/// 書庫 (ZIP 爆弾) でも、一時フォルダーのドライブをあふれさせない (#265)。
/// </para>
/// <para>
/// 書庫に入っている順に渡す。7z はまとめて圧縮されている (ソリッド) ため、先頭から順にしか読めない。
/// </para>
/// <para>
/// ZIP の検査はコアの数だけ並べて読むので、ここを通さず自前で読む (#199)。ここの ZIP は 1 本で読む。
/// </para>
/// </remarks>
internal static class ArchiveContentReader
{
    /// <summary>中身を 1 個ずつ渡す。</summary>
    /// <param name="archivePath">書庫ファイル。</param>
    /// <param name="contents">開いたときに読んだ一覧。形式と、単体の圧縮ファイルの名前に使う。</param>
    /// <param name="password">ZIP と 7z のパスワード。要らなければ <see langword="null"/>。</param>
    /// <param name="visit">1 個ごとに呼ぶ。</param>
    /// <param name="cancellationToken">中断用。</param>
    /// <exception cref="OperationCanceledException">中断された場合。</exception>
    /// <remarks>書庫そのものが読めない場合は、読み方ごとの例外がそのまま出る。</remarks>
    public static void Visit(
        string archivePath, ArchiveContents contents, string? password, Action<ContentItem> visit,
        CancellationToken cancellationToken)
    {
        var format = contents.Format;

        if (ArchiveFormats.IsZipBased(format))
        {
            VisitZip(archivePath, format, password, visit, cancellationToken);
        }
        else if (format == ArchiveFormat.Pe)
        {
            var items = PeReader.ReadLayout(archivePath, cancellationToken);
            using var source = ArchiveFile.OpenRead(archivePath);

            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                visit(new ContentItem(ArchiveTreeBuilder.Trim(item.Path), item.Length, -1, false,
                    () => item.Open(source)));
            }
        }
        else if (format == ArchiveFormat.Nsis)
        {
            // 以前は tar の読み方に回っていて、壊れていないインストーラーでも読めなかった (#68)
            var layout = NsisReader.ReadLayout(archivePath, cancellationToken);

            NsisExtractor.Visit(archivePath, layout, layout.Files, (file, size, open) =>
                visit(new ContentItem(
                    ArchiveTreeBuilder.Trim(NsisReader.ToArchivePath(file.Name)), size, -1, false, open)),
                cancellationToken);
        }
        else if (format is ArchiveFormat.Cab or ArchiveFormat.CabExe)
        {
            // CAB は塊ごとに照合の値を持っていて、cabinet.dll が展開しながら確かめる。
            // 合わなければ読み取りが失敗する
            CabReader.Visit(archivePath, null, (entry, open) =>
                visit(new ContentItem(ArchiveTreeBuilder.Trim(entry.Name), entry.Length, -1, false, open)),
                cancellationToken, format);
        }
        else if (format == ArchiveFormat.Msi)
        {
            MsiReader.Visit(archivePath, null, (file, _, open) =>
                visit(new ContentItem(ArchiveTreeBuilder.Trim(file.Path), file.Length, -1, false, open)),
                cancellationToken);
        }
        else if (format == ArchiveFormat.Burn)
        {
            BurnReader.Visit(archivePath, null, (payload, _, open) =>
                visit(new ContentItem(ArchiveTreeBuilder.Trim(payload.Path), payload.Length, -1, false, open)),
                cancellationToken);
        }
        else if (format == ArchiveFormat.MsiExe)
        {
            // 中の MSI の中身までは見ない。書庫の中の書庫と同じく、開いた先で扱う
            var items = MsiExeReader.Find(archivePath, cancellationToken: cancellationToken);
            using var source = ArchiveFile.OpenRead(archivePath);

            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                visit(new ContentItem(ArchiveTreeBuilder.Trim(item.Name), item.Length, -1, false,
                    () => MsiExeReader.Slice(source, item)));
            }
        }
        else if (ArchiveFormats.IsCompressedFile(format))
        {
            // 照らし合わせる CRC はこちらでは持たない。検査値を持つ形式は伸長の部品が自分で確かめ、
            // 合わなければ読み取りが失敗する
            if (contents.Root.Files.FirstOrDefault() is { } entry)
            {
                visit(new ContentItem(entry.FullPath, entry.Length, -1, false,
                    () => CompressedFile.OpenContent(archivePath, format)));
            }
        }
        else
        {
            VisitSharp(archivePath, format, password, visit, cancellationToken);
        }
    }

    /// <summary>
    /// 失敗が「書庫や中身を読めなかった」ことによるものか。読み方ごとに投げる例外が違う。
    /// </summary>
    public static bool IsReadFailure(Exception ex)
        => ex is System.Security.Cryptography.CryptographicException
            or SharpCompress.Common.CryptographicException
            or InvalidFormatException or ArchiveOperationException
            or IOException or InvalidDataException or NotSupportedException;

    /// <summary>ZIP を 1 本で読む。</summary>
    private static void VisitZip(
        string archivePath, ArchiveFormat format, string? password, Action<ContentItem> visit,
        CancellationToken cancellationToken)
    {
        using var zip = OpenZip(archivePath, password);

        // 標準の実装が扱えないエントリ用 (#66、#67)。要るまで開かない
        using var fallback = new ZipMethodFallback(archivePath, password);

        foreach (ZipEntry entry in zip)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!entry.IsFile)
            {
                continue;
            }

            var name = ArchiveTreeBuilder.Trim(ArchiveFormats.EntryName(format, entry.Name));

            // SharpZipLib が扱えない方式 (LZMA、PPMd、Deflate64) や鍵長 (AES-192) は
            // 開き直して読む (#66、#67)
            visit(new ContentItem(
                name, Math.Max(0, entry.Size), ZipEncryption.ExpectedCrc(entry),
                entry.IsCrypted && password is null,
                () => fallback.Open(entry.Name, () => zip.GetInputStream(entry))));
        }
    }

    /// <summary>ZIP を開く。</summary>
    public static SharpZipFile OpenZip(string archivePath, string? password)
        // 自己解凍書庫は先頭にプログラムが付いている。その分を隠して渡す (#32)
        => new(ZipPrefix.Open(archivePath), leaveOpen: false)
        {
            Password = password,
            StringCodec = StringCodec.FromEncoding(ZipArchiveReader.EntryNameEncoding),
        };

    /// <summary>7z と tar を、先頭から順に読む。</summary>
    private static void VisitSharp(
        string archivePath, ArchiveFormat format, string? password, Action<ContentItem> visit,
        CancellationToken cancellationToken)
    {
        IArchive? archive = null;
        Stream? file = null;
        IReader? reader = null;

        try
        {
            if (format == ArchiveFormat.SevenZip)
            {
                archive = SharpArchiveAccess.OpenSevenZip(archivePath, password);
                reader = archive.ExtractAllEntries();
            }
            else
            {
                file = ArchiveFile.OpenRead(archivePath);
                reader = SharpArchiveAccess.OpenTarReader(file);
            }

            while (reader.MoveToNextEntry())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var entry = reader.Entry;
                if (entry.IsDirectory || entry.Key is not { } key)
                {
                    continue;
                }

                // tar は CRC を持たない。7z は持つ (0 は「無い」の意味で使われる)
                visit(new ContentItem(
                    ArchiveTreeBuilder.Trim(key), Math.Max(0, entry.Size), entry.Crc == 0 ? -1 : entry.Crc,
                    entry.IsEncrypted && password is null, reader.OpenEntryStream));
            }
        }
        finally
        {
            reader?.Dispose();
            archive?.Dispose();
            file?.Dispose();
        }
    }
}
