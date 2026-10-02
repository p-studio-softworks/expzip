using System.IO;

namespace Expzip.Archives;

/// <summary>7z / tar 書庫を読み込み、ZIP と同じ形の内容に組み立てる (#19)。</summary>
internal static class SharpArchiveReader
{
    /// <summary>7z / tar 書庫を開いて内容を読み取る。</summary>
    /// <param name="path">書庫ファイルのパス。</param>
    /// <param name="format">書庫の形式。</param>
    /// <param name="progress">進捗の通知先。</param>
    /// <param name="cancellationToken">中断用。</param>
    /// <exception cref="InvalidDataException">書庫として解釈できない場合。</exception>
    /// <exception cref="IOException">ファイルを読めない場合。</exception>
    /// <exception cref="DamagedDataException">書庫のデータが壊れている場合。</exception>
    /// <exception cref="OperationCanceledException">中断された場合。</exception>
    public static ArchiveContents Open(
        string path,
        ArchiveFormat format,
        IProgress<OpenProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return format == ArchiveFormat.SevenZip
                ? OpenSevenZip(path, progress, cancellationToken)
                : OpenTar(path, progress, cancellationToken);
        }
        catch (SharpCompress.Common.SharpCompressException ex)
            when (ex is not SharpCompress.Common.CryptographicException)
        {
            // 圧縮された tar は、一覧を作るのにも中身を読み進める。壊れていると
            // SharpCompress 独自の例外 (ZlibException など) になり、受け止める側を
            // すり抜けて「処理中に問題が発生しました」と英語の文で出ていた (#167)
            throw new DamagedDataException(ex);
        }
    }

    private static ArchiveContents OpenSevenZip(
        string path, IProgress<OpenProgress>? progress, CancellationToken cancellationToken)
    {
        var builder = new ArchiveTreeBuilder(path);
        using var archive = SharpArchiveAccess.OpenSevenZip(path);

        var entries = archive.Entries.ToList();
        var reportStep = Math.Max(1, entries.Count / 100);
        var done = 0;
        var encrypted = false;

        foreach (var entry in entries)
        {
            if (++done % reportStep == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new OpenProgress(done, entries.Count));
            }

            if (entry.Key is not { } key)
            {
                continue;
            }

            encrypted |= entry.IsEncrypted;

            if (entry.IsDirectory)
            {
                builder.AddFolder(key);
                continue;
            }

            // 7z はまとめて圧縮する (ソリッド) ため、エントリごとの圧縮後サイズを持たない
            builder.AddFile(
                key, entry.Size, 0, compressedLengthKnown: false,
                ReadTime(entry.LastModifiedTime), entry.IsEncrypted);
        }

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new OpenProgress(entries.Count, entries.Count));

        // エントリごとの内訳が無いので、書庫そのものの大きさを圧縮後の合計として出す
        return builder.Build(
            path, ArchiveFormat.SevenZip, FileLength(path), encrypted,
            isSelfExtracting: SharpArchiveAccess.SevenZipOffset(path) > 0);
    }

    private static ArchiveContents OpenTar(
        string path, IProgress<OpenProgress>? progress, CancellationToken cancellationToken)
    {
        var builder = new ArchiveTreeBuilder(path);

        using var stream = ArchiveFile.OpenRead(path);
        using var reader = SharpArchiveAccess.OpenTarReader(stream);

        // tar は中央の索引を持たないため、総数は最後まで読むまで分からない。
        // 進捗は「読んだ件数」だけを出し、割合は出せる範囲で近似する。
        var done = 0;

        while (reader.MoveToNextEntry())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entry = reader.Entry;
            if (entry.Key is not { } key)
            {
                continue;
            }

            if (entry.IsDirectory)
            {
                builder.AddFolder(key);
            }
            else
            {
                // 圧縮された tar では、エントリ単位の圧縮後サイズは意味を持たない
                // (書庫全体をまとめて圧縮しているため)。ここでは展開後サイズを使う
                builder.AddFile(key, entry.Size, entry.Size, compressedLengthKnown: false, ReadTime(entry.LastModifiedTime));
            }

            if (++done % 100 == 0)
            {
                progress?.Report(new OpenProgress(done, done));
            }
        }

        progress?.Report(new OpenProgress(done, done));

        return builder.Build(path, ArchiveFormat.Tar, FileLength(path));
    }

    /// <summary>更新日時を読む。持たない書庫もある。</summary>
    private static DateTime ReadTime(DateTime? time) => time ?? default;

    /// <summary>書庫ファイルの大きさ。読めない場合は 0。</summary>
    private static long FileLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
