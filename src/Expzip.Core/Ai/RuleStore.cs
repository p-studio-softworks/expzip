using System.IO;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Expzip.Archives;
using Expzip.Configuration;

namespace Expzip.Ai;

/// <summary>確かめた決まりの読み書き (#26)。</summary>
/// <remarks>
/// <para>
/// 置き場は設定ファイルと同じフォルダ (<see cref="DataFolder"/>) で、**別の1枚**にする。設定は
/// ウィンドウの大きさや言語といった「使い勝手」で、決まりは書庫の中身についての取り決め。
/// 混ぜると、どちらかを配りたいときに片方が付いてくる。
/// </para>
/// <para>
/// **決まりは持ち運べる。**APIキーはその PC のその利用者しか戻せない形にした
/// (#24) が、こちらはただの文章なので、ファイルを渡せば同じ決まりを他の人も
/// 使える。チームで決めた書庫の作り方を配る、という使い方ができる。
/// </para>
/// <para>
/// **人が開いて直せる形で書く。**日本語をそのまま載せ、字下げして並べる。
/// 種類と当てる先の言葉は AI に頼むときと同じもの (<see cref="RuleWords"/>)。
/// 読めない行があっても、その行だけ落として残りを読む。1行の書き損じで
/// 決まりが全部消えるほうが困る。
/// </para>
/// </remarks>
internal static class RuleStore
{
    private const string FileName = "Expzip.rules.json";

    /// <summary>いまの形式の番号。読めない番号のものは読まない。</summary>
    /// <remarks>
    /// <para>
    /// 2 で場所 (<c>where</c>) が加わった (#81)。**1 のファイルもそのまま読める。**
    /// 場所は任意で、無ければ書庫全体に当てる。古い版で書かれたものは、
    /// これまでどおりの意味になる。
    /// </para>
    /// <para>
    /// **場所の数 (<c>places</c>) を足したが、番号は上げない** (#172)。任意の項目で、
    /// 読み落としても決まりの意味は変わらない。番号を上げると、この項目を知らない
    /// 古い版がファイルごと読まなくなり、決まりが全部消えたように見える。
    /// </para>
    /// </remarks>
    private const int Version = 2;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // 同じ項目が 2 つあるファイルは読まない (#406)。あとの "Rules": [] だけを読むと、0 件として読めてしまい、
        // 写しも残さずに次の「適用」でファイルごと消す
        AllowDuplicateProperties = false,
    };

    /// <summary>決まりを書いたファイルのパス。</summary>
    public static string FilePath { get; } = Path.Combine(DataFolder.Path, FileName);

    /// <summary>決まりが保存されているか。</summary>
    public static bool Exists => File.Exists(FilePath);

    /// <summary>決まりのファイルが無いことを確かめられたか (#421)。確かめられないとき (アクセス権が無い、など) は false。</summary>
    /// <remarks>
    /// 「ルールに合っているか検査…」を押せるかに使う。<see cref="Exists"/> (<c>File.Exists</c>) は確かめられないときも「無い」と返すので、
    /// 押せず「ルールが保存されていません」と出て、読めなかったことを知らせる警告 (#406) に届かない。
    /// </remarks>
    public static bool ConfirmedMissing => GuardedFile.ConfirmedMissing(FilePath);

    /// <summary>保存された決まりを読む。</summary>
    /// <param name="keepCopy">
    /// 読めなかった中身や、落とした行のあるファイルの写しを残すか (#406)。書く道のあるルールの推定のウィンドウでは残す。
    /// 当てはめは読むだけで書かないので、残さない。タブを切り替えるたびに写しを読み比べないように。
    /// </param>
    /// <remarks>
    /// <para>
    /// 読めなかったとき (壊れている、ほかのプログラムが開いている、アクセス権が無い、新しいバージョンの形式) は
    /// <see cref="RuleLoad.Problem"/> が付き、<see cref="TrySave"/> と <see cref="TryDelete"/> はそのファイルに書かない (#406)。
    /// 読めなかったまま「適用」すると、0 件として消すか、新しいルールだけで置き換えてしまう。
    /// </para>
    /// <para>
    /// 読めない行はその行だけ落とす (1 行の書き損じで全部消さない)。落とした行は次の「適用」でファイルから消えるので、
    /// 1 行でも落としたら、読んだ中身の写しを残す (#406)。新しいバージョンで足された種類の行は、形式の番号を
    /// 上げないことがある (#172) ので、ここで守る。当てる先と出所が知らない言葉の行も、同じく落とす (#421)。
    /// </para>
    /// </remarks>
    public static RuleLoad Load(bool keepCopy) => Load(FilePath, keepCopy);

    /// <summary>決まりを読む。テストは置き場所を渡して呼ぶ。</summary>
    internal static RuleLoad Load(string path, bool keepCopy)
    {
        var read = GuardedFile.Read(path);
        switch (read.Status)
        {
            case GuardedReadStatus.Missing:
                return new RuleLoad(path, fingerprint: null);

            // 大きすぎるものは中身を読まないので、写しは残さない
            case GuardedReadStatus.TooLarge:
                return Unreadable(path, keptPath: null, RuleLoadFailure.Damaged);
            case GuardedReadStatus.InUse:
                return Unreadable(path, keptPath: null, RuleLoadFailure.InUse);
            case GuardedReadStatus.Inaccessible:
                return Unreadable(path, keptPath: null, RuleLoadFailure.Inaccessible);
        }

        var bytes = read.Bytes!;
        var text = GuardedFile.Decode(bytes);

        // 新しいバージョンの Expzip で作られたもの。中身は正しいかもしれないので、壊れているとは知らせない。
        // 番号を上げるのは古い版が読めない形に変えたときなので、全体を読む前に番号だけを読む。
        // 全体を読んでから比べると、形が変わったファイルは「壊れている」になり、削除を勧めてしまう
        if (ReadVersion(text) > Version)
        {
            return Unreadable(path, keepCopy ? GuardedFile.TryKeepCopy(path, bytes) : null, RuleLoadFailure.Newer);
        }

        if (Parse(text) is not { } document)
        {
            return Unreadable(path, keepCopy ? GuardedFile.TryKeepCopy(path, bytes) : null, RuleLoadFailure.Damaged);
        }

        var book = ToBook(document, out var dropped);

        // 知らない項目は、書く道のあるルールの推定のウィンドウでだけ持ち、適用で書き戻す (#421)。
        // 当てはめは書かないので持たない。整えるために全体を書き出してみる手間も、タブを切り替えるたびにかけない
        var discarded = false;
        book = keepCopy ? TidyUnknown(book, ref discarded) : WithoutUnknown(book);

        // 落とした行と捨てた知らない項目は、次の「適用」でファイルから消える。写しを残して知らせる (#406、#421)
        return new RuleLoad(path, SHA256.HashData(bytes))
        {
            Book = book,
            Dropped = dropped,
            UnknownDiscarded = discarded,
            DroppedKeptPath = (dropped > 0 || discarded) && keepCopy ? GuardedFile.TryKeepCopy(path, bytes) : null,
        };
    }

    /// <summary>
    /// 知らない項目 (#421) を、書き戻せる形に整える。整え方は設定ファイル (#409) と同じ (<see cref="UnknownItems"/>)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ファイルの頭と全部の行を合わせて数え、多すぎる・大きすぎるときは、1 つずつ確かめずにすべて捨てる。
    /// 1 つずつは、書き出せないものと、知っている項目と大文字・小文字だけ違う名前のもの (ファイルの頭だけ。行のものは、その行を落としてある) を捨てる。
    /// 残ったものがあれば全体を書き出してみて、書き出せないか大きすぎるときは、知らない項目を除いて書き出せるときだけ、すべて捨てる。
    /// 除いても書き出せないなら、知っている項目だけで大きすぎるので、捨てずに持つ (適用で大きすぎると知らせる)。
    /// </para>
    /// <para>
    /// 設定ファイルと違い、捨てたものがあれば知らせる (<paramref name="discarded"/>)。ルールのファイルには写しを残す仕組みがあり、配られたファイルを使うこともあるため。
    /// </para>
    /// </remarks>
    private static RuleBook TidyUnknown(RuleBook book, ref bool discarded)
    {
        var measured = UnknownItems.Measure([book.Unknown, .. book.Rules.Select(static entry => entry.Unknown)]);
        if (measured.Count == 0)
        {
            return book;
        }

        if (UnknownItems.TooMany(measured))
        {
            discarded = true;
            return WithoutUnknown(book);
        }

        var documentNames = UnknownItems.KnownNames(Options, typeof(Document));
        var rowNames = UnknownItems.KnownNames(Options, typeof(Row));
        var head = UnknownItems.Tidy(book.Unknown, documentNames, Options, ref discarded);
        var entries = new List<RuleEntry>(book.Rules.Count);
        foreach (var entry in book.Rules)
        {
            entries.Add(entry with { Unknown = UnknownItems.Tidy(entry.Unknown, rowNames, Options, ref discarded) });
        }

        var tidied = book with { Unknown = head, Rules = entries };
        var remains = head is not null || entries.Exists(static entry => entry.Unknown is not null);
        if (!remains || GuardedFile.Serialize(ToDocument(tidied), Options) is not null)
        {
            return tidied;
        }

        // 知らない項目を除けば書き出せるときだけ、知らない項目のせいとして捨てる。除いても書き出せないなら、知っている項目だけで
        // 大きすぎる。捨てずに持ち、「適用」で大きすぎると知らせる (ルールを減らせば、知らない項目ごと書ける)
        var without = WithoutUnknown(tidied);
        if (GuardedFile.Serialize(ToDocument(without), Options) is null)
        {
            return tidied;
        }

        discarded = true;
        return without;
    }

    /// <summary>知らない項目を持たない決まり。</summary>
    private static RuleBook WithoutUnknown(RuleBook book)
        => book.Unknown is null && book.Rules.All(static entry => entry.Unknown is null)
            ? book
            : book with { Unknown = null, Rules = [.. book.Rules.Select(static entry => entry with { Unknown = null })] };

    private static RuleLoad Unreadable(string path, string? keptPath, RuleLoadFailure failure)
        => new(path, fingerprint: null) { Problem = new RuleLoadProblem(path, keptPath, failure) };

    /// <summary>ルールのファイルとして読む。読めなければ null。</summary>
    /// <remarks>
    /// <c>Rules</c> の項目が無いものは、ルールのファイルとして読まない。Expzip が書くファイルには必ずあり、
    /// 0 件として扱うと、次の「適用」で中身ごと消してしまう。
    /// </remarks>
    private static Document? Parse(string text)
    {
        try
        {
            return JsonSerializer.Deserialize<Document>(text, Options) is { Rules: not null } document
                ? document
                : null;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException
                                   or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>形式の番号だけを読む。ほかの項目の形は見ない。読めなければ null。</summary>
    private static int? ReadVersion(string text)
    {
        try
        {
            return JsonSerializer.Deserialize<VersionOnly>(text, Options)?.Version;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException
                                   or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>読んだ形から決まりを組み立てる。読めない行は落とし、その数を返す。</summary>
    private static RuleBook ToBook(Document document, out int dropped)
    {
        dropped = 0;
        var entries = new List<RuleEntry>();

        var rowNames = UnknownItems.KnownNames(Options, typeof(Row));

        foreach (var row in document.Rules!)
        {
            // 読めない行はその行だけ落とす。1 行の書き損じで全部消さない。中身の無い行 (null) も同じ。
            // 当てる先と出所が知らない言葉の行も落とす (#421)。書庫全体・AI として読むと、「適用」で書き直されて意味が変わる。
            // 知っている項目と大文字・小文字だけ違う名前 ("scope") がある行も落とす。項目名は区別して読むので、当てる先が無い扱いで
            // 書庫全体になり、適用で "Scope": "all" と書き直されてしまう
            if (row is null
                || (row.Unknown?.Keys.Any(rowNames.Contains) ?? false)
                || RuleWords.ToKind(row.Kind) is not { } kind
                || RuleWords.TryScope(row.Scope) is not { } scope
                || RuleWords.TrySource(row.Source) is not { } source)
            {
                dropped++;
                continue;
            }

            var rule = ArchiveRule.TryCreate(
                kind, scope, row.Value ?? string.Empty,
                row.Description ?? string.Empty, row.Evidence ?? string.Empty,
                source, row.Where ?? string.Empty);

            if (rule is null)
            {
                dropped++;
                continue;
            }

            // 数え上げた場所の数。無い古いファイルは 0 のまま (#172)。知らない項目はその行と一緒に持つ (#421)
            entries.Add(new RuleEntry(rule with { Places = row.Places ?? 0 }, row.Enabled) { Unknown = row.Unknown });
        }

        return new RuleBook(document.LearnedFrom ?? string.Empty, document.LearnedAt, entries) { Unknown = document.Unknown };
    }

    /// <summary>ファイルに書く形にする。知らない項目 (#421) は、ファイルの頭とその行に書き戻す。</summary>
    private static Document ToDocument(RuleBook book) => new()
    {
        Version = Version,
        LearnedFrom = book.LearnedFrom,
        LearnedAt = book.LearnedAt,
        Rules = [.. book.Rules.Select(static entry => new Row
        {
            Kind = RuleWords.FromKind(entry.Rule.Kind),
            Scope = RuleWords.FromScope(entry.Rule.Scope),
            Value = entry.Rule.Value,

            // 画面に出るのと同じ文を書く。補った分は組み立てたもの (#172)。
            // 読むときは Places から組み立て直すので、言語を切り替えても付いてくる
            Description = entry.Rule.DescriptionText,
            Evidence = entry.Rule.EvidenceText,
            Where = entry.Rule.Where.Length == 0 ? null : entry.Rule.Where,
            Places = entry.Rule.Places == 0 ? null : entry.Rule.Places,
            Source = RuleWords.FromSource(entry.Rule.Source),
            Enabled = entry.Enabled,
            Unknown = entry.Unknown,
        })],
        Unknown = book.Unknown,
    };

    /// <summary>決まりを保存する。</summary>
    /// <param name="book">保存する決まり。</param>
    /// <param name="basis">
    /// ルールの推定のウィンドウを開いたときに読んだ結果。読めなかったファイルにも、読んだあとに変わったファイルにも書かない (#406)。
    /// 保存できたら、書いた中身で覚え直す。
    /// </param>
    public static RuleWriteResult TrySave(RuleBook book, RuleLoad basis)
    {
        if (Check(basis) is { } refused)
        {
            return refused;
        }

        // 書くバイト列を 1 度だけ作り、同じ配列で覚え直す。書き方が変わっても、次の適用で「変わった」と誤らない。
        // 書き出せないか、次に読めない大きさになるなら、書かずに前のファイルを残す (#421)。字下げで、読んだときより大きくなることがある
        if (GuardedFile.Serialize(ToDocument(book), Options) is not { } bytes)
        {
            return RuleWriteResult.TooLarge;
        }

        try
        {
            // 途中で止まっても、前の決まりが残る (#320)
            using var file = AtomicFile.Create(basis.FilePath);
            file.Stream.Write(bytes);
            file.Commit();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or NotSupportedException)
        {
            return RuleWriteResult.Failed;
        }

        basis.Fingerprint = SHA256.HashData(bytes);
        return RuleWriteResult.Done;
    }

    /// <summary>保存された決まりを消す。</summary>
    /// <param name="basis">ルールの推定のウィンドウを開いたときに読んだ結果。<see cref="TrySave"/> と同じく確かめる (#406)。</param>
    public static RuleWriteResult TryDelete(RuleLoad basis)
    {
        if (Check(basis) is { } refused)
        {
            return refused;
        }

        try
        {
            File.Delete(basis.FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return RuleWriteResult.Failed;
        }

        basis.Fingerprint = null;
        return RuleWriteResult.Done;
    }

    /// <summary>書いてよいかを確かめる。書いてはいけなければ、その理由を返す。</summary>
    /// <remarks>
    /// 書く直前に読み直し、読んだときと中身が同じかを比べる (#406)。ルールの推定のウィンドウを開いている間に、
    /// 別の Expzip で適用された、手でファイルが置かれた、などで変わっていたら書かない。
    /// 開いたときに無かったファイルが置かれたときも同じ。読み直してから書くまでの短い間に変わったものは防げない。
    /// </remarks>
    private static RuleWriteResult? Check(RuleLoad basis)
    {
        if (basis.Problem is not null)
        {
            return RuleWriteResult.Unreadable;
        }

        var now = GuardedFile.Read(basis.FilePath);
        return now.Status switch
        {
            GuardedReadStatus.Missing when basis.Fingerprint is null => null,
            GuardedReadStatus.Read when basis.Fingerprint is { } had
                                        && SHA256.HashData(now.Bytes!).AsSpan().SequenceEqual(had) => null,

            // 開けなかっただけなら、中身は変わっていないかもしれない。開き直させずに、もう一度押してもらう
            GuardedReadStatus.InUse or GuardedReadStatus.Inaccessible => RuleWriteResult.Busy,
            _ => RuleWriteResult.Changed,
        };
    }

    /// <summary>形式の番号だけを読む形 (#406)。ほかの項目は読み飛ばす。</summary>
    private sealed class VersionOnly
    {
        public int? Version { get; set; }
    }

    /// <summary>ファイルに書く形。人が開いて直せるように、素直な名前で並べる。</summary>
    private sealed class Document
    {
        public int Version { get; set; } = RuleStore.Version;

        public string? LearnedFrom { get; set; }

        public DateTimeOffset LearnedAt { get; set; }

        /// <summary>決まりの並び。手で書かれたファイルでは、中に null の行があることもある。</summary>
        public List<Row?>? Rules { get; set; }

        /// <summary>このバージョンが知らない項目 (#421)。読んだまま持ち、適用で書き戻す。</summary>
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Unknown { get; set; }
    }

    private sealed class Row
    {
        public string? Kind { get; set; }

        public string? Scope { get; set; }

        public string? Value { get; set; }

        public string? Description { get; set; }

        public string? Evidence { get; set; }

        /// <summary>当てる場所。項目を含むフォルダのパスの形 (#81)。無ければ書庫全体。</summary>
        public string? Where { get; set; }

        /// <summary>数え上げた場所の数 (#172)。Expzip が補った分だけに付く。</summary>
        public int? Places { get; set; }

        public string? Source { get; set; }

        public bool Enabled { get; set; } = true;

        /// <summary>この行の、このバージョンが知らない項目 (#421)。行と一緒に持ち、適用で書き戻す。</summary>
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Unknown { get; set; }
    }
}

/// <summary>ルールのファイルを読んだ結果 (#406)。</summary>
internal sealed class RuleLoad
{
    internal RuleLoad(string filePath, byte[]? fingerprint)
    {
        FilePath = filePath;
        Fingerprint = fingerprint;
    }

    /// <summary>読んだファイル。</summary>
    public string FilePath { get; }

    /// <summary>読めた決まり。ファイルが無いときと、読めなかったときは <see langword="null"/>。</summary>
    public RuleBook? Book { get; init; }

    /// <summary>ファイルがあるのに読めなかったときの理由。付いていれば、このファイルには書かない。</summary>
    public RuleLoadProblem? Problem { get; init; }

    /// <summary>読めずに落とした行の数。</summary>
    public int Dropped { get; init; }

    /// <summary>
    /// このバージョンが知らない項目のうち、書き戻せないものを捨てたか (#421)。捨てたものは、次の「適用」でファイルから消える。
    /// </summary>
    public bool UnknownDiscarded { get; init; }

    /// <summary>
    /// 落とした行か、捨てた知らない項目があったときに、元のファイルの写しを残した場所。残していなければ <see langword="null"/>。
    /// </summary>
    public string? DroppedKeptPath { get; init; }

    /// <summary>読んだ (または最後に書いた) 中身の SHA-256。ファイルが無かったときは <see langword="null"/>。</summary>
    internal byte[]? Fingerprint { get; set; }
}

/// <summary>ルールのファイルを読めなかったこと (#406)。</summary>
/// <param name="FilePath">ルールのファイル。</param>
/// <param name="KeptPath">中身の写しを残した場所。残していなければ <see langword="null"/>。</param>
/// <param name="Failure">読めなかった理由。</param>
internal sealed record RuleLoadProblem(string FilePath, string? KeptPath, RuleLoadFailure Failure);

/// <summary>ルールのファイルを読めなかった理由 (#406)。知らせる文の案内を分ける。</summary>
internal enum RuleLoadFailure
{
    /// <summary>中身を読めたが、ルールのファイルとして読めない。大きすぎて読まなかったときも。</summary>
    Damaged,

    /// <summary>新しいバージョンの Expzip で作られた形式。中身は正しいかもしれないので、削除を勧めない。</summary>
    Newer,

    /// <summary>ほかのプログラムが開いたままにしている。</summary>
    InUse,

    /// <summary>アクセス権が無い、同じ名前のフォルダーがある、など。</summary>
    Inaccessible,
}

/// <summary>ルールを保存した・消した結果 (#406)。</summary>
internal enum RuleWriteResult
{
    /// <summary>書いた (または消した)。</summary>
    Done,

    /// <summary>書き込めなかった。書き込めない場所に置かれている、など。</summary>
    Failed,

    /// <summary>読めなかったファイルなので、書かなかった。</summary>
    Unreadable,

    /// <summary>読んだあとにファイルが変わっていたので、書かなかった。</summary>
    Changed,

    /// <summary>書く前に読み直せなかった (ほかのプログラムが開いている、など) ので、書かなかった。</summary>
    Busy,

    /// <summary>書く中身が、次に読めない大きさになる (または書き出せない) ので、書かなかった (#421)。</summary>
    TooLarge,
}

/// <summary>確かめた決まりのひとまとまり (#26)。</summary>
/// <param name="LearnedFrom">最後にお手本にした書庫の名前。</param>
/// <param name="LearnedAt">最後に保存した日時。</param>
/// <param name="Rules">決まりと、それを使うかどうか。</param>
internal sealed record RuleBook(
    string LearnedFrom, DateTimeOffset LearnedAt, IReadOnlyList<RuleEntry> Rules)
{
    /// <summary>
    /// ファイルの頭の、このバージョンが知らない項目 (#421)。新しいバージョンの Expzip が足したものなど。無ければ null。
    /// </summary>
    /// <remarks>中身は使わない。適用でそのまま書き戻す。持たないと、古いバージョンで適用したときに消える。</remarks>
    public Dictionary<string, JsonElement>? Unknown { get; init; }
}

/// <summary>決まり1つと、それを使うかどうか (#26)。</summary>
/// <remarks>
/// <see cref="Unknown"/> は、その行の、このバージョンが知らない項目 (#421)。行と一緒に持ち、行を消せば一緒に消える。
/// </remarks>
internal readonly record struct RuleEntry(ArchiveRule Rule, bool Enabled)
{
    /// <summary>その行の、このバージョンが知らない項目 (#421)。無ければ null。</summary>
    public Dictionary<string, JsonElement>? Unknown { get; init; }
}
