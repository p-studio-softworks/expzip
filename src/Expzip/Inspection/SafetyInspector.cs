using System.IO;
using Expzip.Archives;
using Expzip.Ui;

namespace Expzip.Inspection;

/// <summary>
/// 展開したときに困ることが起きないかを調べる (#55)。
/// </summary>
/// <remarks>
/// 中身は読まない。エントリの名前と大きさだけで判断できるものだけを扱う。
/// </remarks>
internal static class SafetyInspector
{
    /// <summary>圧縮率を問題にし始める展開後サイズ。これより小さいものは見ない。</summary>
    private const long RatioFloor = 1024 * 1024;

    /// <summary>これ以上に膨らむエントリは注意を促す。</summary>
    private const long RatioLimit = 100;

    /// <summary>書庫全体で問題にし始める展開後サイズ。</summary>
    private const long BombFloor = 1024L * 1024 * 1024;

    /// <summary>Windows が装置の名前として特別扱いする語。拡張子が付いていても同じ。</summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Windows のファイル名に使えない文字。区切りは正規化済みなので含めない。</summary>
    private static readonly char[] InvalidCharacters = ['<', '>', ':', '"', '|', '?', '*'];

    public static void Inspect(InspectionContext context)
    {
        context.BeginPhase(InspectionPhase.Safety, 0.04);

        var contents = context.Contents;
        var caseMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var collided = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var total = Math.Max(1, contents.FileCount);
        var done = 0;

        void Walk(ArchiveFolder folder)
        {
            foreach (var file in folder.Files)
            {
                if (++done % 2000 == 0)
                {
                    context.Cancellation.ThrowIfCancellationRequested();
                    context.Advance(done / (double)total, file.FullPath);
                }

                CheckPath(context, file);
                CheckName(context, file.FullPath, file.FullPath);
                CheckSize(context, file);
                CheckCase(context, file.FullPath, caseMap, collided);
            }

            foreach (var child in folder.Folders)
            {
                CheckCase(context, child.FullPath, caseMap, collided);

                // 中身のあるフォルダは、その中のファイルのパスを調べる時点で
                // 名前も一緒に見ている。何も入っていないフォルダだけここで拾う
                if (child.Files.Count == 0 && child.Folders.Count == 0)
                {
                    CheckName(context, child.FullPath, child.FullPath);
                }

                Walk(child);
            }
        }

        Walk(contents.Root);
        CheckWholeArchive(context);
        context.Advance(1, string.Empty);
    }

    /// <summary>展開先の外を指していないかを調べる。</summary>
    private static void CheckPath(InspectionContext context, ArchiveEntry file)
    {
        if (ArchivePath.IsEscaping(file.SourceName))
        {
            context.Findings.Add(InspectionIssue.EscapingPath, file.FullPath, file.SourceName);
            return;
        }

        // 先頭の / は展開先を起点として読み替えるので外へは出ない (ArchivePath) が、
        // 通常の書庫には現れない形なので、そのことは伝える
        if (ArchivePath.IsSuspicious(file.SourceName)
            || ArchivePath.Normalize(file.SourceName).StartsWith('/'))
        {
            context.Findings.Add(InspectionIssue.SuspiciousPath, file.FullPath, file.SourceName);
        }
    }

    /// <summary>
    /// Windows で作れない名前、拡張子を偽装する名前を調べる。
    /// </summary>
    /// <param name="target">報告に載せる書庫内のパス。</param>
    /// <param name="path">見るパス。ファイルなら全体、空のフォルダなら名前だけ。</param>
    private static void CheckName(InspectionContext context, string target, string path)
    {
        foreach (var segment in path.Split('/'))
        {
            // 空の区切りと、その場・親を指す区切りは名前ではない。
            // 名前として見ると「末尾がピリオド」に引っかかるうえ、
            // パスとしての問題は CheckPath が既に報告している
            if (segment.Length == 0 || segment == "." || segment == "..")
            {
                continue;
            }

            // 「CON.txt」も装置として扱われる。最初のピリオドまでで見る
            var stem = segment.Split('.')[0];
            if (ReservedNames.Contains(stem))
            {
                context.Findings.Add(InspectionIssue.ReservedName, target, segment);
            }

            if (segment[^1] is ' ' or '.')
            {
                context.Findings.Add(InspectionIssue.TrailingSpaceOrDot, target, segment);
            }

            if (segment.Any(char.IsControl))
            {
                context.Findings.Add(InspectionIssue.ControlCharacter, target, segment);
            }

            if (segment.IndexOfAny(InvalidCharacters) >= 0)
            {
                context.Findings.Add(InspectionIssue.InvalidCharacter, target, segment);
            }

            if (segment.Any(IsBidiControl))
            {
                context.Findings.Add(InspectionIssue.BidiOverride, target, segment);
            }
        }
    }

    /// <summary>
    /// 文字の向きを変える記号か。
    /// </summary>
    /// <remarks>
    /// U+202E (右横書き) を名前の途中に入れると、そこから先が逆さに表示される。
    /// <c>写真gpj.exe</c> を <c>写真exe.jpg</c> のように見せる古典的な細工で、
    /// 一覧の上では画像にしか見えない。
    /// </remarks>
    private static bool IsBidiControl(char c)
        => c is >= (char)0x202A and <= (char)0x202E   // LRE, RLE, PDF, LRO, RLO
            or >= (char)0x2066 and <= (char)0x2069    // LRI, RLI, FSI, PDI
            or (char)0x200E or (char)0x200F;          // LRM, RLM

    /// <summary>展開すると極端に大きくなるエントリと、開くと実行される拡張子を調べる。</summary>
    private static void CheckSize(InspectionContext context, ArchiveEntry file)
    {
        if (RiskyFileTypes.IsExecutable(file.Name))
        {
            context.Findings.Add(
                InspectionIssue.ExecutableExtension, file.FullPath, Path.GetExtension(file.Name));
        }

        // 7z のように、エントリごとの圧縮後サイズを持たない形式では比べようがない
        if (!file.CompressedLengthKnown || file.Length < RatioFloor)
        {
            return;
        }

        var ratio = file.Length / Math.Max(1, file.CompressedLength);
        if (ratio >= RatioLimit)
        {
            context.Findings.Add(
                InspectionIssue.HighRatio, file.FullPath, ratio.ToString("N0"));
        }
    }

    /// <summary>大文字小文字だけが違うエントリを拾う。</summary>
    /// <remarks>
    /// Windows は名前の大小を区別しないため、展開すると後のものが前のものを
    /// 上書きし、片方が失われる。書庫を作った側 (多くは Unix) では別物だった。
    /// </remarks>
    private static void CheckCase(
        InspectionContext context, string path,
        Dictionary<string, string> seen, HashSet<string> collided)
    {
        if (!seen.TryGetValue(path, out var first))
        {
            seen[path] = path;
            return;
        }

        if (string.Equals(first, path, StringComparison.Ordinal) || !collided.Add(path))
        {
            return;
        }

        context.Findings.Add(InspectionIssue.CaseCollision, path, first);
    }

    /// <summary>書庫全体が極端に膨らまないかを調べる (ZIP爆弾)。</summary>
    private static void CheckWholeArchive(InspectionContext context)
    {
        var contents = context.Contents;

        if (contents.TotalLength < BombFloor)
        {
            return;
        }

        long archiveLength;
        try
        {
            archiveLength = new FileInfo(context.ArchivePath).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var ratio = contents.TotalLength / Math.Max(1, archiveLength);
        if (ratio >= RatioLimit)
        {
            context.Findings.Add(InspectionIssue.ZipBomb, string.Empty, ratio.ToString("N0"));
        }
    }
}
