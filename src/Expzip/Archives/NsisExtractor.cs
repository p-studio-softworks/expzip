using System.IO;

namespace Expzip.Archives;

/// <summary>NSIS 製インストーラーから中身を取り出す (#68)。</summary>
/// <remarks>
/// <para>
/// 中身は <c>[4バイトの大きさ][中身]</c> の塊として並んでいて、どの塊がどのファイルかは
/// 命令の並びから分かる (<see cref="NsisReader.ReadLayout"/>)。塊ごとに独立して
/// 展開できるため、選んだものだけを取り出せる。
/// </para>
/// <para>
/// 判定や後始末は他の形式と揃えてある (<see cref="ExtractState"/>)。書庫の外を指す
/// パスは弾き、出所の印を引き継ぎ、中断したら書きかけのファイルを消す。
/// </para>
/// <para>
/// まとめ圧縮 (LZMA) の書庫はまだ読めない。そちらは塊ごとに独立しておらず、
/// 前から順に展開する必要がある。
/// </para>
/// </remarks>
internal static class NsisExtractor
{
    /// <summary>指定したエントリを展開する。引数の意味は <see cref="ArchiveExtractor.Extract"/> と同じ。</summary>
    public static ExtractResult Extract(
        string archivePath,
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

        var layout = NsisReader.ReadLayout(archivePath, cancellationToken);

        var targets = layout.Files
            .Where(f => sourceNames is null || sourceNames.Contains(NsisReader.ToArchivePath(f.Name)))
            .ToList();

        // 展開後の大きさは書いていない。進捗は塊の大きさで測る
        state.TotalBytes = targets.Sum(static f => f.StoredLength);

        using var source = File.OpenRead(archivePath);

        foreach (var file in targets)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            state.Write(
                NsisReader.ToArchivePath(file.Name),
                file.StoredLength,
                lastWriteTime: null,
                () => layout.OpenFile(source, file),
                cancellationToken);
        }

        return state.ToResult();
    }
}
