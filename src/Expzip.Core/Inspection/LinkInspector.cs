using System.Globalization;
using System.Text;
using Expzip.Archives;

namespace Expzip.Inspection;

/// <summary>
/// 展開先の外へ書かせようとするリンクを調べる (#391)。
/// </summary>
/// <remarks>
/// <para>
/// Expzip の展開は、リンクを全部のファイルのあとに、書庫の中を指すものだけ作る (<see cref="LinkExtractor"/>) ので、
/// ここで見つけるものはどれも Expzip では外に書かれない。検査で何も出ないのを見て、リンクをたどる別のツールで
/// 展開した人が外に書かれないように知らせる (7-Zip の CVE-2025-11001 の型)。
/// </para>
/// <para>
/// 名前は OS によらない決まりで比べる。<c>/</c> と <c>\</c> で区切り、空の区切りと <c>.</c> を除き、各区切りの末尾の
/// 点と空白を落とし、濁点の書き方をそろえ (FormC)、大文字と小文字を区別しない。ほかのツールより広く拾う側に倒す。
/// </para>
/// <para>
/// ZIP・7z・RAR4 のリンクの指す先は中身に書かれている。中身の検査 (<see cref="ContentInspector"/>) が読み通すついでに
/// 取っておいたものを使い、書庫をもう 1 回読まない。そのため、中身の検査のあとに呼ぶ。
/// </para>
/// </remarks>
internal static class LinkInspector
{
    /// <summary>リンクを指すリンクをたどる回数の上限。<see cref="LinkExtractor"/> と同じ。</summary>
    private const int MaxChain = 40;

    /// <summary>報告に添える指す先の長さの目安。超えたら途中を省く。</summary>
    private const int ShownLimit = 200;

    public static void Inspect(InspectionContext context)
    {
        if (context.Contents.Sequence is not { } sequence)
        {
            return;
        }

        // ZIP は目録の順と中身の並びの順が違うことがあり、どちらが前かを決められない
        var ordered = !ArchiveFormats.IsZipBased(context.Contents.Format);
        var links = new LinkTable(context, sequence);

        CheckLinks(context, links);
        CheckItems(context, sequence, links, ordered);
    }

    /// <summary>リンクごとに、指す先が外か、確かめられるかを調べる。</summary>
    private static void CheckLinks(InspectionContext context, LinkTable links)
    {
        foreach (var (position, item) in links.All)
        {
            context.Cancellation.ThrowIfCancellationRequested();
            context.Advance(0.999, Shown(item.Name));

            var read = links.TargetOf(position);
            if (read.Locked)
            {
                // パスワードが分からないことは、中身の検査が「検査できませんでした」で伝えている
                continue;
            }

            var shown = Shown(item.Name);
            var verdict = read.Target is not { } target
                ? Verdict.Unverified
                : links.Resolve(item, target);

            switch (verdict)
            {
                case Verdict.Outside:
                    context.Findings.Add(InspectionIssue.LinkOutside, shown, Visible(read.Target!));
                    break;
                case Verdict.Unverified:
                    context.Findings.Add(InspectionIssue.LinkUnverified, shown);
                    break;
            }
        }
    }

    /// <summary>リンクの下や、リンクと同じ名前に書かれる項目を調べる。</summary>
    private static void CheckItems(
        InspectionContext context, IReadOnlyList<SequenceItem> sequence, LinkTable links, bool ordered)
    {
        for (var position = 0; position < sequence.Count; position++)
        {
            var item = sequence[position];
            if (position % 2000 == 0)
            {
                context.Cancellation.ThrowIfCancellationRequested();

                // 中身の検査のあとに走るので、進み具合は終わりの手前のまま、見ている項目の名前だけを変える
                context.Advance(0.999, Shown(item.Name));
            }

            var shown = Shown(item.Name);
            var segments = Split(item.Name);

            // 途中の名前がリンクか。.. を解く前の並びで、先頭から順にたどる。
            // l/../x は、順にたどるツールでは l をたどってから上がる
            var stack = new List<string>();
            (int Position, SequenceItem Link)? through = null;
            for (var i = 0; i < segments.Count - 1 && through is null; i++)
            {
                var segment = segments[i];
                if (segment == "..")
                {
                    if (stack.Count > 0)
                    {
                        stack.RemoveAt(stack.Count - 1);
                    }

                    continue;
                }

                stack.Add(Fold(segment));
                through = links.Find(stack, position, ordered) ?? links.FindShortName(stack, position, ordered);
            }

            if (through is { } found)
            {
                var issue = !ordered || found.Position < position ? InspectionIssue.WriteThroughLink : InspectionIssue.BeforeLink;
                context.Findings.Add(issue, shown, Visible(Shown(found.Link.Name)));
                continue;
            }

            // 自分と同じ名前のリンクが前にあるか。リンクどうしは除く (後ろのリンクは置き換えて作られる)
            if (item.Link is null && Key(item.Name) is { } key && links.EarlierSameName(key, position, ordered))
            {
                context.Findings.Add(InspectionIssue.ReplacesLink, shown);
            }
        }
    }

    /// <summary>報告の対象の列に出す名前。一覧と同じ形にする。</summary>
    private static string Shown(string name) => ArchiveTreeBuilder.Trim(name);

    /// <summary>名前を区切りに分ける。空の区切りと <c>.</c> は除き、<c>..</c> は残す。</summary>
    private static List<string> Split(string name)
    {
        var result = new List<string>();
        foreach (var part in name.Split('/', '\\'))
        {
            var trimmed = part == ".." ? part : part.TrimEnd('.', ' ');
            if (trimmed.Length > 0 && part != ".")
            {
                result.Add(trimmed);
            }
        }

        return result;
    }

    /// <summary>1 区切りの比べ方をそろえる。</summary>
    private static string Fold(string segment)
        => segment.Normalize(NormalizationForm.FormC).ToUpperInvariant();

    /// <summary>名前の全体を、比べる形にする。<c>..</c> を解き、外へ出るものは <see langword="null"/>。</summary>
    private static string? Key(string name)
    {
        var stack = new List<string>();
        foreach (var segment in Split(name))
        {
            if (segment == "..")
            {
                if (stack.Count == 0)
                {
                    return null;
                }

                stack.RemoveAt(stack.Count - 1);
                continue;
            }

            stack.Add(Fold(segment));
        }

        return stack.Count == 0 ? null : string.Join('/', stack);
    }

    /// <summary>
    /// 報告に添える指す先。見えない文字と行を変える文字を <c>U+XXXX</c> の形にし、長ければ途中を省いて先頭と末尾を残す。
    /// </summary>
    /// <remarks>
    /// 文字の向きを変える記号で表示を入れ替えたり、U+2028 で報告に偽の行を見せたりさせない。
    /// 外へ出る <c>../..</c> は末尾にあることが多いので、前だけを残すと見えなくなる。
    /// </remarks>
    internal static string Visible(string text)
    {
        var pieces = new List<string>();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                var rune = new Rune(c, text[i + 1]);
                i++;
                pieces.Add(Hidden(Rune.GetUnicodeCategory(rune)) ? $"U+{rune.Value:X4}" : rune.ToString());
                continue;
            }

            pieces.Add(char.IsSurrogate(c) || Hidden(char.GetUnicodeCategory(c)) ? $"U+{(int)c:X4}" : c.ToString());
        }

        if (pieces.Sum(static p => p.Length) <= ShownLimit)
        {
            return string.Concat(pieces);
        }

        // 区切りの途中 (U+XXXX やサロゲートの対) で切らないよう、1 文字ずつ数える
        const int headLimit = ShownLimit * 2 / 5;
        const int tailLimit = ShownLimit - headLimit;
        var head = new StringBuilder();
        var first = 0;
        while (first < pieces.Count && head.Length + pieces[first].Length <= headLimit)
        {
            head.Append(pieces[first++]);
        }

        var tail = new List<string>();
        var tailLength = 0;
        var last = pieces.Count - 1;
        while (last >= first && tailLength + pieces[last].Length <= tailLimit)
        {
            tail.Add(pieces[last]);
            tailLength += pieces[last--].Length;
        }

        tail.Reverse();
        return head + "…" + string.Concat(tail);

        static bool Hidden(UnicodeCategory category) => category is UnicodeCategory.Control or UnicodeCategory.Format
            or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.Surrogate
            or UnicodeCategory.OtherNotAssigned;
    }

    private enum Verdict
    {
        Inside,
        Outside,
        Unverified,
    }

    /// <summary>書庫の中のリンクの一覧。名前は比べる形で引く。</summary>
    private sealed class LinkTable
    {
        private static readonly LinkRead Unknown = new(string.Empty, null);

        private readonly InspectionContext _context;

        /// <summary>比べる形の名前 → その名前のリンクの、書庫の並びでの位置 (前から順)。</summary>
        private readonly Dictionary<string, List<int>> _byName = new(StringComparer.Ordinal);

        /// <summary>
        /// 「比べる形のフォルダーの名前 + 区切り + 最後の区切りの頭 1〜6 文字 (空白と点を除く)」→ そのリンクの位置 (前から順)。
        /// 短い名前 (<c>LONGNA~1</c>) と 1 回で引く。項目ごとにフォルダーのリンクを全部なめると、2 乗の時間がかかる。
        /// </summary>
        private readonly Dictionary<string, List<int>> _byStem = new(StringComparer.Ordinal);

        private readonly IReadOnlyList<SequenceItem> _sequence;

        public LinkTable(InspectionContext context, IReadOnlyList<SequenceItem> sequence)
        {
            _context = context;
            _sequence = sequence;

            for (var position = 0; position < sequence.Count; position++)
            {
                var item = sequence[position];
                if (item.Link is null || Key(item.Name) is not { } key)
                {
                    continue;
                }

                All.Add((position, item));
                if (!_byName.TryGetValue(key, out var list))
                {
                    _byName[key] = list = [];
                }

                list.Add(position);

                // 短い名前は、空白と点を除いた長い名前の頭の文字から作られる
                var slash = key.LastIndexOf('/');
                var folder = slash < 0 ? string.Empty : key[..slash];
                var squeezed = key[(slash + 1)..].Replace(" ", string.Empty).Replace(".", string.Empty);
                for (var length = 1; length <= Math.Min(6, squeezed.Length); length++)
                {
                    var stemKey = StemKey(folder, squeezed[..length]);
                    if (!_byStem.TryGetValue(stemKey, out var stems))
                    {
                        _byStem[stemKey] = stems = [];
                    }

                    stems.Add(position);
                }
            }
        }

        private static string StemKey(string folder, string stem) => folder + "\0" + stem;

        /// <summary>書庫の並びの順のリンク。</summary>
        public List<(int Position, SequenceItem Item)> All { get; } = [];

        /// <summary>リンクの指す先。一覧に無いものは、中身の検査が読んだもの。</summary>
        public LinkRead TargetOf(int position)
        {
            var item = _sequence[position];
            var link = item.Link!;
            if (link.Unsupported)
            {
                return Unknown;
            }

            if (link.Target is { } listed)
            {
                return new LinkRead(item.Name, listed);
            }

            // 中身の検査が読んだ項目と、一覧の項目が同じものかを名前でも確かめる。違えば確かめられないとする
            return _context.LinkTargets.TryGetValue(item.Index, out var read)
                   && read.Name == ArchiveTreeBuilder.Trim(item.Name)
                ? read
                : Unknown;
        }

        /// <summary>
        /// <paramref name="stack"/> の名前のリンクを探す。並びに意味のある形式では、前にあるリンクを優先する (危険のほうを出す)。
        /// </summary>
        public (int Position, SequenceItem Link)? Find(List<string> stack, int position, bool ordered)
        {
            if (!_byName.TryGetValue(string.Join('/', stack), out var list))
            {
                return null;
            }

            var chosen = list[0];
            if (ordered)
            {
                foreach (var p in list)
                {
                    if (p < position)
                    {
                        chosen = p;
                    }
                }
            }

            return (chosen, _sequence[chosen]);
        }

        /// <summary>
        /// 最後の区切りが 8.3 形式の短い名前 (<c>LONGNA~1</c>) なら、同じフォルダーにある、その短い名前になりうる長い名前のリンクを探す。
        /// </summary>
        /// <remarks>短い名前が作られるドライブでは、長い名前で作られたリンクを短い名前でたどれる。どの番号になるかは分からないので、頭の文字だけで見る。</remarks>
        public (int Position, SequenceItem Link)? FindShortName(List<string> stack, int position, bool ordered)
        {
            var last = stack[^1];
            var tilde = last.IndexOf('~');
            if (tilde < 1 || tilde > 6 || !IsShortName(last, tilde))
            {
                return null;
            }

            var folder = string.Join('/', stack.Take(stack.Count - 1));
            if (!_byStem.TryGetValue(StemKey(folder, last[..tilde]), out var list))
            {
                return null;
            }

            // 並びに意味のある形式では、前にあるリンクを優先する (Find と同じ)
            var chosen = list[0];
            if (ordered)
            {
                foreach (var p in list)
                {
                    if (p < position)
                    {
                        chosen = p;
                    }
                }
            }

            return (chosen, _sequence[chosen]);
        }

        /// <summary>同じ名前のリンクが前にあるか。並びに意味の無い形式では、自分以外のどこかにあるか。</summary>
        public bool EarlierSameName(string key, int position, bool ordered)
            => _byName.TryGetValue(key, out var list) && list.Any(p => ordered ? p < position : p != position);

        /// <summary>
        /// 指す先を書庫の中で解く。シンボリックリンクとジャンクションはリンクのあるフォルダーから、ハードリンクとファイルのコピーは
        /// 書庫の先頭から。途中の名前がリンクなら、その指す先に置き換えて続ける。
        /// </summary>
        public Verdict Resolve(SequenceItem link, string target)
        {
            if (target.Length == 0)
            {
                return Verdict.Unverified;
            }

            var hard = link.Link!.Kind is RarLinkKind.HardLink or RarLinkKind.FileCopy;

            // 区切りごとの、そこまでの比べる形の名前。1 区切りごとにパス全体を作り直すと、深い指す先で 2 乗の時間がかかる
            var stack = new List<string>();
            void Push(string folded) => stack.Add(stack.Count == 0 ? folded : stack[^1] + "/" + folded);

            if (!hard)
            {
                if (Key(link.Name) is not { } name)
                {
                    return Verdict.Unverified;
                }

                foreach (var part in name.Split('/')[..^1])
                {
                    Push(part);
                }
            }

            if (Absolute(target) || (hard && ArchivePath.IsSuspicious(target)))
            {
                return Verdict.Outside;
            }

            // 読み残しの区切り。前から取り出し、リンクに当たったらその指す先を前に足す
            var pending = new LinkedList<string>(Split(target));
            var steps = 0;
            while (pending.First is { } next)
            {
                pending.RemoveFirst();
                var segment = next.Value;
                if (segment == "..")
                {
                    if (stack.Count == 0)
                    {
                        return Verdict.Outside;
                    }

                    stack.RemoveAt(stack.Count - 1);
                    continue;
                }

                // 一覧はフォルダーの深さを制限するので (#294)、それより深い名前のリンクは無い。名前を作らずに数だけ進める
                if (stack.Count > ArchiveLimits.MaxFolderDepth)
                {
                    stack.Add(string.Empty);
                    continue;
                }

                Push(Fold(segment));

                // ハードリンクはリンクをたどらずに作るツールが多いが、たどるツールもある。どちらでも外へ出るものは拾う
                if (!_byName.TryGetValue(stack[^1], out var found))
                {
                    continue;
                }

                var inner = _sequence[found[^1]];
                if (inner.Link!.Kind is RarLinkKind.HardLink or RarLinkKind.FileCopy)
                {
                    continue;
                }

                if (++steps > MaxChain)
                {
                    return Verdict.Unverified;
                }

                if (TargetOf(found[^1]).Target is not { } innerTarget)
                {
                    return Verdict.Unverified;
                }

                if (Absolute(innerTarget))
                {
                    return Verdict.Outside;
                }

                stack.RemoveAt(stack.Count - 1);
                var parts = Split(innerTarget);
                for (var i = parts.Count - 1; i >= 0; i--)
                {
                    pending.AddFirst(parts[i]);
                }
            }

            return Verdict.Inside;
        }

        /// <summary>絶対パス、ドライブ名、ネットワークの場所、<c>\\?\</c>、<c>\??\</c>、<c>:</c> を含むもの。</summary>
        private static bool Absolute(string target)
            => target.Length > 0 && (target[0] is '/' or '\\' || target.Contains(':'));

        private static bool IsShortName(string name, int tilde)
        {
            // 8.3 形式の短い名前では、点は必ず ~数字 より後ろにある (1.0~rc1 のような名前は短い名前ではない)
            var dot = name.IndexOf('.');
            if (dot >= 0 && dot < tilde)
            {
                return false;
            }

            var stemEnd = dot < 0 ? name.Length : dot;
            var digits = name[(tilde + 1)..stemEnd];
            return stemEnd <= 8 && digits.Length > 0 && digits.All(char.IsAsciiDigit)
                   && (dot < 0 || name.Length - dot - 1 <= 3);
        }
    }
}
