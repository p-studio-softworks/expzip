using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Expzip.Ai;

/// <summary>確かめた決まりの読み書き (#26)。</summary>
/// <remarks>
/// <para>
/// 置き場は exe と同じフォルダで、設定ファイルとは**別の1枚**にする。設定は
/// 窓の大きさや言語といった「使い勝手」で、決まりは書庫の中身についての取り決め。
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
    /// 2 で場所 (<c>where</c>) が加わった (#81)。**1 のファイルもそのまま読める。**
    /// 場所は任意で、無ければ書庫全体に当てる。古い版で書かれたものは、
    /// これまでどおりの意味になる。
    /// </remarks>
    private const int Version = 2;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>決まりを書いたファイルのパス。</summary>
    public static string FilePath { get; } = Path.Combine(BaseDirectory(), FileName);

    /// <summary>決まりが保存されているか。</summary>
    public static bool Exists => File.Exists(FilePath);

    /// <summary>保存された決まりを読む。無い・読めないときは <see langword="null"/>。</summary>
    public static RuleBook? Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return null;
            }

            var document = JsonSerializer.Deserialize<Document>(
                File.ReadAllText(FilePath), Options);

            if (document is null || document.Version > Version)
            {
                return null;
            }

            var entries = new List<RuleEntry>();

            foreach (var row in document.Rules ?? [])
            {
                // 読めない行はその行だけ落とす。1行の書き損じで全部消さない
                if (RuleWords.ToKind(row.Kind) is not { } kind)
                {
                    continue;
                }

                var rule = ArchiveRule.TryCreate(
                    kind, RuleWords.ToScope(row.Scope), row.Value ?? string.Empty,
                    row.Description ?? string.Empty, row.Evidence ?? string.Empty,
                    row.Source == "hand" ? RuleSource.Hand : RuleSource.Ai,
                    row.Where ?? string.Empty);

                if (rule is not null)
                {
                    entries.Add(new RuleEntry(rule, row.Enabled));
                }
            }

            return new RuleBook(
                document.LearnedFrom ?? string.Empty, document.LearnedAt, entries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>決まりを保存する。</summary>
    /// <returns>保存できたら true。書き込めない場所に置かれている場合は false。</returns>
    public static bool TrySave(RuleBook book)
    {
        var document = new Document
        {
            Version = Version,
            LearnedFrom = book.LearnedFrom,
            LearnedAt = book.LearnedAt,
            Rules = [.. book.Rules.Select(static entry => new Row
            {
                Kind = RuleWords.FromKind(entry.Rule.Kind),
                Scope = RuleWords.FromScope(entry.Rule.Scope),
                Value = entry.Rule.Value,
                Description = entry.Rule.Description,
                Evidence = entry.Rule.Evidence,
                Where = entry.Rule.Where.Length == 0 ? null : entry.Rule.Where,
                Source = entry.Rule.Source == RuleSource.Hand ? "hand" : "ai",
                Enabled = entry.Enabled,
            })],
        };

        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(document, Options));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>保存された決まりを消す。</summary>
    public static bool TryDelete()
    {
        try
        {
            File.Delete(FilePath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// exe が置かれているフォルダ。単一ファイルとして発行した場合、実行時に
    /// 展開される一時フォルダではなく exe 自身の場所を指す必要がある。
    /// </summary>
    private static string BaseDirectory()
    {
        var processPath = Environment.ProcessPath;

        if (!string.IsNullOrEmpty(processPath)
            && Path.GetDirectoryName(processPath) is { Length: > 0 } directory)
        {
            return directory;
        }

        return AppContext.BaseDirectory;
    }

    /// <summary>ファイルに書く形。人が開いて直せるように、素直な名前で並べる。</summary>
    private sealed class Document
    {
        public int Version { get; set; } = RuleStore.Version;

        public string? LearnedFrom { get; set; }

        public DateTimeOffset LearnedAt { get; set; }

        public List<Row>? Rules { get; set; }
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

        public string? Source { get; set; }

        public bool Enabled { get; set; } = true;
    }
}

/// <summary>確かめた決まりのひとまとまり (#26)。</summary>
/// <param name="LearnedFrom">最後にお手本にした書庫の名前。</param>
/// <param name="LearnedAt">最後に保存した日時。</param>
/// <param name="Rules">決まりと、それを使うかどうか。</param>
internal sealed record RuleBook(
    string LearnedFrom, DateTimeOffset LearnedAt, IReadOnlyList<RuleEntry> Rules);

/// <summary>決まり1つと、それを使うかどうか (#26)。</summary>
internal readonly record struct RuleEntry(ArchiveRule Rule, bool Enabled);
