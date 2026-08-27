using System.IO;
using SharpCompress.Common;
using SharpCompress.Readers;
using Expzip.Localization;

namespace Expzip.Archives;

/// <summary>7z / tar 書庫から実ファイルへ展開する (#19)。</summary>
/// <remarks>
/// <para>
/// 判定や後始末は ZIP の展開 (<see cref="ArchiveExtractor"/>) と揃えてある。
/// 書庫の外を指すパスは弾き、書き出したファイルには更新日時と出所の印を移し、
/// 中断したら書きかけのファイルを消す。
/// </para>
/// <para>
/// 7z は<b>まとめて圧縮されている (ソリッド)</b> ため、エントリを1件ずつ開くと
/// そのたびに同じ塊を復号し直すことになる。2,000件の書庫で計測したところ
/// 1件ずつでは50.4秒かかり、先頭から順に読む方式では0.14秒だった (370倍)。
/// 2件以上を取り出すときは必ず順に読む。
/// </para>
/// </remarks>
internal static class SharpArchiveExtractor
{
    /// <summary>指定したエントリを展開する。引数の意味は <see cref="ArchiveExtractor.Extract"/> と同じ。</summary>
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

        if (format == ArchiveFormat.SevenZip)
        {
            ExtractSevenZip(archivePath, sourceNames, state, cancellationToken);
        }
        else
        {
            ExtractTar(archivePath, sourceNames, state, cancellationToken);
        }

        return state.ToResult();
    }

    private static void ExtractSevenZip(
        string archivePath, IReadOnlySet<string>? sourceNames, ExtractState state,
        CancellationToken cancellationToken)
    {
        using var archive = SharpArchiveAccess.OpenSevenZip(archivePath);

        var targets = archive.Entries
            .Where(e => !e.IsDirectory && e.Key is not null)
            .Where(e => sourceNames is null || sourceNames.Contains(e.Key!))
            .ToList();

        state.TotalBytes = targets.Sum(static e => e.Size);

        // 1件だけなら、その塊を復号するだけで済むので直接開く
        if (targets.Count == 1)
        {
            var only = targets[0];
            state.Write(only.Key!, only.Size, only.LastModifiedTime, () => only.OpenEntryStream(), cancellationToken);
            return;
        }

        if (targets.Count == 0)
        {
            return;
        }

        // 2件以上は先頭から順に読む。塊ごとに一度だけ復号すれば済む
        var wanted = targets.Select(e => e.Key!).ToHashSet(StringComparer.Ordinal);
        using var reader = archive.ExtractAllEntries();

        // 塊の復号に失敗すると、取り出しの手前 (次のエントリへ進む段階) で例外になる。
        // パスワード付きの書庫がこれに当たるため、1件ずつの失敗と同じように報告する
        try
        {
            while (reader.MoveToNextEntry())
            {
                if (state.Cancelled)
                {
                    return;
                }

                var entry = reader.Entry;
                if (entry.IsDirectory || entry.Key is not { } key || !wanted.Contains(key))
                {
                    continue;
                }

                state.Write(key, entry.Size, entry.LastModifiedTime, reader.OpenEntryStream, cancellationToken);
            }
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            state.Fail(Path.GetFileName(archivePath), Explain(ex));
        }
    }

    private static void ExtractTar(
        string archivePath, IReadOnlySet<string>? sourceNames, ExtractState state,
        CancellationToken cancellationToken)
    {
        using var stream = File.OpenRead(archivePath);
        using var reader = SharpArchiveAccess.OpenTarReader(stream);

        try
        {
            TarLoop(reader, sourceNames, state, cancellationToken);
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            state.Fail(Path.GetFileName(archivePath), Explain(ex));
        }
    }

    private static void TarLoop(
        IReader reader, IReadOnlySet<string>? sourceNames, ExtractState state,
        CancellationToken cancellationToken)
    {
        while (reader.MoveToNextEntry())
        {
            if (state.Cancelled)
            {
                return;
            }

            var entry = reader.Entry;
            if (entry.IsDirectory || entry.Key is not { } key)
            {
                continue;
            }

            if (sourceNames is not null && !sourceNames.Contains(key))
            {
                continue;
            }

            // tar は総量を先に知る手立てが無い。取り出した分を足しながら出す
            state.TotalBytes += entry.Size;
            state.Write(key, entry.Size, entry.LastModifiedTime, reader.OpenEntryStream, cancellationToken);
        }
    }

    /// <summary>書庫そのものを読み進められなくなる類の失敗か。</summary>
    private static bool IsReadFailure(Exception ex)
        => ex is System.Security.Cryptography.CryptographicException
            or SharpCompress.Common.CryptographicException
            or InvalidFormatException or ArchiveOperationException
            or IOException or InvalidDataException or NotSupportedException;

    /// <summary>失敗の理由を、画面に出せる言葉にする。</summary>
    private static string Explain(Exception ex)
        => ex is System.Security.Cryptography.CryptographicException
            or SharpCompress.Common.CryptographicException
            ? Strings.PasswordNotSupported
            : ex.Message;
}
