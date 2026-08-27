using System.IO;
using Expzip.Archives;
using ICSharpCode.SharpZipLib.Zip;

// 標準ライブラリにも同じ名前の型があるため、こちら側の名前をはっきりさせる
using SharpZipFile = ICSharpCode.SharpZipLib.Zip.ZipFile;

namespace Expzip.Inspection;

/// <summary>
/// 書庫が壊れていないかを、中身を読まずに分かる範囲で調べる (#54)。
/// </summary>
/// <remarks>
/// CRCの照合だけは書庫を丸ごと読む必要があるため、マルウェア検査 (#56) と
/// 同じ読み出しに相乗りさせてある (<see cref="ContentInspector"/>)。
/// ここで行うのは索引とヘッダの照合まで。
/// </remarks>
internal static class StructureInspector
{
    public static void Inspect(InspectionContext context)
    {
        context.BeginPhase(InspectionPhase.Structure, 0.08);

        CheckDuplicateNames(context);

        if (context.Contents.Format == ArchiveFormat.Zip)
        {
            CheckTail(context);
            CheckHeaders(context);
        }
    }

    /// <summary>
    /// 同じ名前のエントリが2つ以上ないかを調べる。
    /// </summary>
    /// <remarks>
    /// 組み立て済みの一覧から調べるため、形式によらず同じ判定になる。
    /// 展開すると片方がもう片方を上書きするので、後から入れたほうしか残らない。
    /// </remarks>
    private static void CheckDuplicateNames(InspectionContext context)
    {
        void Walk(ArchiveFolder folder)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var file in folder.Files)
            {
                if (!seen.Add(file.Name))
                {
                    context.Findings.Add(InspectionIssue.DuplicateName, file.FullPath);
                }
            }

            foreach (var child in folder.Folders)
            {
                Walk(child);
            }
        }

        Walk(context.Contents.Root);
    }

    /// <summary>書庫の末尾を調べる (#54)。</summary>
    private static void CheckTail(InspectionContext context)
    {
        var tail = ZipTail.Read(context.ArchivePath);

        if (!tail.Found)
        {
            // 一覧は読めているのに終端レコードが見つからないのは考えにくいが、
            // 見つからないまま先へ進むと後続の判定が意味を持たない
            context.Findings.Add(InspectionIssue.Truncated, string.Empty);
            return;
        }

        if (tail.CentralDirectoryTruncated)
        {
            context.Findings.Add(InspectionIssue.Truncated, string.Empty);
        }

        if (tail.TrailingBytes > 0)
        {
            context.Findings.Add(
                InspectionIssue.TrailingData, string.Empty, tail.TrailingBytes.ToString("N0"));
        }
    }

    /// <summary>
    /// 中央ディレクトリとローカルヘッダの食い違い、対応していない圧縮方式を調べる (#54)。
    /// </summary>
    /// <remarks>
    /// 照合そのものは SharpZipLib の <c>TestArchive</c> に任せる。名前・大きさ・CRC・
    /// 圧縮方式を1件ずつ突き合わせる処理で、手で書き起こしても同じものにしかならない。
    /// 中身は読ませない (第1引数が false)。読むのは <see cref="ContentInspector"/> の
    /// 1回だけにして、書庫を二度なめないようにする。
    /// </remarks>
    private static void CheckHeaders(InspectionContext context)
    {
        try
        {
            using var zip = new SharpZipFile(context.ArchivePath)
            {
                StringCodec = StringCodec.FromEncoding(ZipArchiveReader.EntryNameEncoding),
            };

            var total = Math.Max(1, zip.Count);
            var done = 0L;

            zip.TestArchive(false, TestStrategy.FindAllErrors, (status, message) =>
            {
                // TestArchive は中断の仕組みを持たない。ここから抜けるしかない
                context.Cancellation.ThrowIfCancellationRequested();

                var name = status.Entry?.Name ?? string.Empty;

                if (status.Operation == TestOperation.EntryComplete)
                {
                    context.Advance(++done / (double)total, name);
                }

                // 名前そのものが Windows で使えない項目も、ここでは食い違いとして
                // 上がってくる。中身は同じことを安全性の検査 (#55) が拾うため、
                // 二重に報告しない
                if (message is not null && ZipNameTransform.IsValidName(name))
                {
                    context.Findings.Add(
                        InspectionIssue.HeaderMismatch, ArchiveTreeBuilder.Trim(name), message);
                }
            });

            CheckCompressionMethods(context, zip);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is ZipException or IOException or UnauthorizedAccessException
                                   or InvalidDataException or NotSupportedException)
        {
            context.Findings.Add(InspectionIssue.Unreadable, string.Empty, ex.Message);
        }
    }

    /// <summary>取り出せない圧縮方式で入っているエントリを拾う (#54)。</summary>
    /// <remarks>
    /// 使っているライブラリが扱えないだけの方式もある (LZMA、PPMd、Deflate64 など)。
    /// それらは開き直せば読めるため、指摘しない。読めないものだけを挙げる (#66)。
    /// </remarks>
    private static void CheckCompressionMethods(InspectionContext context, SharpZipFile zip)
    {
        using var fallback = new ZipMethodFallback(context.ArchivePath);

        foreach (ZipEntry entry in zip)
        {
            if (!entry.IsFile || entry.CanDecompress)
            {
                continue;
            }

            using var opened = fallback.TryOpen(entry.Name);
            if (opened is not null)
            {
                continue;
            }

            context.Findings.Add(
                InspectionIssue.UnsupportedMethod,
                ArchiveTreeBuilder.Trim(entry.Name),
                entry.CompressionMethod.ToString());
        }
    }
}
