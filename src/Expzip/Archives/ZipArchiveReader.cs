using System.IO;
using System.IO.Compression;
using System.Text;

namespace Expzip.Archives;

/// <summary>書庫の読み込みの進捗。</summary>
/// <param name="DoneEntries">組み立て済みのエントリ数。</param>
/// <param name="TotalEntries">書庫に入っているエントリの総数。</param>
internal readonly record struct OpenProgress(int DoneEntries, int TotalEntries)
{
    /// <summary>0〜100 の進捗率。</summary>
    public double Percent => TotalEntries <= 0 ? 100 : (double)DoneEntries / TotalEntries * 100.0;
}

/// <summary>ZIP書庫を読み込み、フォルダ階層に組み立てる。</summary>
internal static class ZipArchiveReader
{
    /// <summary>従来の日本語書庫で使われてきたコードページ。</summary>
    private const int LegacyJapaneseCodePage = 932;

    private static Encoding? _entryNameEncoding;

    /// <summary>
    /// エントリ名の解釈に使う <see cref="Encoding"/>。
    /// CP932は.NET Core以降 既定では登録されていないため、初回に取得を試みる。
    /// 展開時も同じ解釈でなければエントリを引き当てられないため、共有している。
    /// </summary>
    internal static Encoding EntryNameEncoding
    {
        get
        {
            if (_entryNameEncoding is not null)
            {
                return _entryNameEncoding;
            }

            Encoding legacy;
            try
            {
                legacy = Encoding.GetEncoding(LegacyJapaneseCodePage);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                // プロバイダーが登録されていない環境ではUTF-8のみで動作させる。
                // 古い書庫のファイル名は化けるが、書庫自体は開ける。
                legacy = Encoding.UTF8;
            }

            return _entryNameEncoding = new ArchiveEntryNameEncoding(legacy);
        }
    }

    /// <summary>ZIP書庫を開いて内容を読み取る。</summary>
    /// <param name="path">書庫ファイルのパス。</param>
    /// <param name="progress">進捗の通知先。</param>
    /// <param name="cancellationToken">中断用。</param>
    /// <remarks>
    /// 中断できるのはエントリを組み立てる段階から。その手前の中央ディレクトリの
    /// 読み取りは <see cref="ZipArchive"/> の内部で一息に行われるため割り込めない。
    /// 30万エントリでこの部分が約0.3秒 (#14)。
    /// </remarks>
    /// <exception cref="InvalidDataException">書庫として解釈できない場合。</exception>
    /// <exception cref="IOException">ファイルを読めない場合。</exception>
    /// <exception cref="OperationCanceledException">中断された場合。</exception>
    public static ArchiveContents Open(
        string path,
        IProgress<OpenProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var builder = new ArchiveTreeBuilder(path);

        using var stream = File.OpenRead(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false, EntryNameEncoding);

        var entries = zip.Entries;

        // 進捗通知が多すぎるとUI側が詰まるため、1%刻みに間引く
        var reportStep = Math.Max(1, entries.Count / 100);
        var done = 0;

        foreach (var entry in entries)
        {
            if (++done % reportStep == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new OpenProgress(done, entries.Count));
            }

            // 末尾が区切り文字のエントリはフォルダそのものを表す
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                builder.AddFolder(entry.FullName);
                continue;
            }

            builder.AddFile(
                entry.FullName,
                entry.Length,
                entry.CompressedLength,
                compressedLengthKnown: true,
                ReadLastWriteTime(entry));
        }

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new OpenProgress(entries.Count, entries.Count));

        return builder.Build(path, ArchiveFormat.Zip);
    }

    /// <summary>
    /// 最終更新日時を読む。書庫によっては範囲外の日付が入っており、
    /// <see cref="ZipArchiveEntry.LastWriteTime"/> が例外を投げることがある。
    /// </summary>
    private static DateTime ReadLastWriteTime(ZipArchiveEntry entry)
    {
        try
        {
            return entry.LastWriteTime.LocalDateTime;
        }
        catch (ArgumentOutOfRangeException)
        {
            return default;
        }
    }
}
