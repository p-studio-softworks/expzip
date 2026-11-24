using System.IO;
using System.Text;
using System.Text.Json;
using Expzip.Archives;
using Expzip.Speed;

namespace Expzip.Configuration;

/// <summary>設定ファイルの読み書き。</summary>
/// <remarks>
/// <para>
/// 保存先は <see cref="DataFolder"/> が決める。exe 単体の版は exe と同じフォルダ、
/// Store 版は <c>%LOCALAPPDATA%\Expzip</c> (docs/SPEC.md 7章)。
/// </para>
/// <para>
/// exe を書き込めない場所 (Program Files や読み取り専用のUSBメモリなど) に
/// 置かれることがある。その場合でも設定を保存できないだけで、アプリ自体は
/// 通常どおり動かす。読み書きの失敗でアプリを止めない。
/// </para>
/// </remarks>
internal static class SettingsStore
{
    private const string FileName = "Expzip.settings.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        // 利用者が直接開いて編集することも想定して整形する
        WriteIndented = true,
    };

    /// <summary>読む大きさの上限 (#399)。読み方はルールのファイルと同じ (<see cref="GuardedFile"/>)。</summary>
    internal const int MaxBytes = GuardedFile.MaxBytes;

    /// <summary>最近開いた書庫を覚える数 (#10)。手で書き足されていても、読んだときにここまで切る。</summary>
    internal const int RecentLimit = 10;

    /// <summary>
    /// 書き戻す知らない項目 (#409) の数の上限。測った結果の中のものも合わせて数える。超えたら知らない項目をすべて捨てる。
    /// ルールのファイルと同じ (<see cref="UnknownItems.MaxCount"/>)。
    /// </summary>
    internal const int UnknownMaxCount = UnknownItems.MaxCount;

    /// <summary>
    /// 書き戻す知らない項目 (#409) の、読んだときの大きさの合計の上限。超えたら知らない項目をすべて捨てる。
    /// ルールのファイルと同じ (<see cref="UnknownItems.MaxBytes"/>)。
    /// </summary>
    internal const int UnknownMaxBytes = UnknownItems.MaxBytes;

    /// <summary>設定ファイルのパス。</summary>
    public static string FilePath { get; } = Path.Combine(DataFolder.Path, FileName);

    /// <summary>画面から起動したときに設定を読み込む。</summary>
    /// <remarks>
    /// <para>
    /// 壊れた設定ファイルで起動できなくなるのが最も困るので、読めなくても既定の値で動かす。
    /// ただし設定ファイルがあったのに読めなかったときは、<see cref="AppSettings.LoadProblem"/> を付けて、
    /// そのセッションでは保存しない (#399)。保存すると、元の設定が既定の値で上書きされて失われる。
    /// </para>
    /// <para>
    /// 中身を読めたのに設定として読めなかったとき (壊れている) は、読んだ中身を別の名前で残す。
    /// ほかのプログラムが開いていて読めなかっただけなら、次の起動で読めるので、残さない。
    /// 元のファイルは読むだけで、名前の変更も削除もしない。
    /// </para>
    /// </remarks>
    public static AppSettings Load() => Load(FilePath, keepCopy: true);

    /// <summary>コマンドラインから実行したときに設定を読み込む。読めなかった中身の写しは作らない。</summary>
    /// <remarks>コマンドラインからの実行は設定を保存しないので、写しは要らない。スクリプトから繰り返し呼ばれても写しを増やさない。</remarks>
    public static AppSettings LoadForCommand() => Load(FilePath, keepCopy: false);

    /// <summary>設定を読み込む。テストは置き場所を渡して呼ぶ。</summary>
    internal static AppSettings Load(string path, bool keepCopy)
    {
        // 無い・0 バイトなら既定の値で、保存してよい。大きすぎるものは中身を読まないので、残さない。
        // ふつうの設定ファイルは数 KB なので、大きすぎるものは壊れているものとして知らせる
        var read = GuardedFile.Read(path);
        switch (read.Status)
        {
            case GuardedReadStatus.Missing:
                return new AppSettings();
            case GuardedReadStatus.TooLarge:
                return Unreadable(path, keptPath: null, SettingsLoadFailure.Damaged);
            case GuardedReadStatus.InUse:
                return Unreadable(path, keptPath: null, SettingsLoadFailure.InUse);
            case GuardedReadStatus.Inaccessible:
                return Unreadable(path, keptPath: null, SettingsLoadFailure.Inaccessible);
        }

        var bytes = read.Bytes!;
        var settings = Parse(bytes);
        if (settings is null)
        {
            return Unreadable(path, keepCopy ? GuardedFile.TryKeepCopy(path, bytes) : null, SettingsLoadFailure.Damaged);
        }

        // 知らない項目は保存で書き戻すので、書き戻せる形に整える (#409)
        TidyUnknown(settings);

        // 古い設定ファイルや手で編集されたものには項目が無いことがある
        settings.RecentArchives ??= [];
        settings.RecentArchives.RemoveAll(p => p is null);
        if (settings.RecentArchives.Count > RecentLimit)
        {
            settings.RecentArchives.RemoveRange(RecentLimit, settings.RecentArchives.Count - RecentLimit);
        }

        return settings;
    }

    /// <summary>設定を保存する。</summary>
    /// <returns>
    /// 保存できたら true。書き込めない場所に置かれている場合、設定ファイルを読めなかったセッション (#399)、
    /// 中身を書き出せないか次に読めない大きさになる場合 (#409) は false。
    /// </returns>
    public static bool TrySave(AppSettings settings) => TrySave(settings, FilePath);

    /// <summary>設定を保存する。テストは置き場所を渡して呼ぶ。</summary>
    internal static bool TrySave(AppSettings settings, string path)
    {
        // 読めなかった設定ファイルを、既定の値で上書きしない (#399)
        if (settings.LoadProblem is not null)
        {
            return false;
        }

        // 書き出せない、または次に読めない大きさなら、前の設定ファイルを残す (#409)
        var json = ToJson(settings);
        if (json is null)
        {
            return false;
        }

        try
        {
            // 途中で止まっても、前の設定が残る (#320)
            AtomicFile.WriteAllText(path, json);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// 保存する中身を作る。作れないか、次に読めない大きさ (<see cref="MaxBytes"/> を超える) なら null。
    /// </summary>
    /// <remarks>
    /// 読めたものでも書き出せないことがある。知らない項目 (#409) の中の、対になっていないサロゲート (<c>"\uD800"</c>) など。
    /// 整形と <c>\uXXXX</c> への置き換えで、読んだときより大きくなることもある。作り方はルールのファイルと同じ (<see cref="GuardedFile.Serialize{T}"/>)。
    /// </remarks>
    private static string? ToJson(AppSettings settings)
        => GuardedFile.Serialize(settings, Options) is { } bytes ? Encoding.UTF8.GetString(bytes) : null;

    /// <summary>
    /// 知らない項目 (#409) のうち、書き戻せないものと、知っている項目と大文字・小文字だけ違う名前のものを捨てる。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 書き戻せないものを持っていると、保存のたびに失敗して、知っている項目も保存できなくなる。
    /// 多すぎる・大きすぎるとき (<see cref="UnknownMaxCount"/>、<see cref="UnknownMaxBytes"/>) と、
    /// 1 つずつは書き戻せても、全体で書き出せないか大きすぎるときは、知らない項目をすべて捨てる。
    /// 捨てたものは、#409 より前と同じく、次の保存でファイルから消える。
    /// </para>
    /// <para>
    /// 大文字・小文字だけ違う名前 (<c>language</c>) は、この読み方では知らない項目に入る。
    /// 書き戻すと知っている項目の後ろに残り続け、大文字・小文字を区別しない読み手は後ろの値を使うか、読めなくなる。
    /// </para>
    /// </remarks>
    private static void TidyUnknown(AppSettings settings)
    {
        var speed = settings.SpeedResult;
        var kinds = speed?.Kinds?.Where(k => k is not null).ToList() ?? [];

        // 知らない項目が無い、ふつうのファイルでは何もしない
        var measured = UnknownItems.Measure([settings.Unknown, speed?.Unknown, .. kinds.Select(static k => k.Unknown)]);
        if (measured.Count == 0)
        {
            return;
        }

        // 多すぎる・大きすぎるものは、書き出してみる前に捨てる。字下げで何十倍にも膨らむ形 (深い入れ子に小さな値を並べたもの) は、
        // 書き出すと文字列の長さの上限を超えて、メモリー不足で起動できなくなる
        if (UnknownItems.TooMany(measured))
        {
            DropUnknown(settings, kinds);
            return;
        }

        // 設定では、捨てたことを知らせない (#409)
        var discarded = false;
        settings.Unknown = UnknownItems.Tidy(settings.Unknown, UnknownItems.KnownNames(Options, typeof(AppSettings)), Options, ref discarded);
        if (speed is not null)
        {
            speed.Unknown = UnknownItems.Tidy(speed.Unknown, UnknownItems.KnownNames(Options, typeof(SpeedResult)), Options, ref discarded);
        }

        var kindNames = UnknownItems.KnownNames(Options, typeof(SpeedKindResult));
        kinds.ForEach(k => k.Unknown = UnknownItems.Tidy(k.Unknown, kindNames, Options, ref discarded));

        var remains = settings.Unknown is not null || speed?.Unknown is not null || kinds.Exists(k => k.Unknown is not null);
        if (remains && ToJson(settings) is null)
        {
            DropUnknown(settings, kinds);
        }
    }

    private static void DropUnknown(AppSettings settings, List<SpeedKindResult> kinds)
    {
        settings.Unknown = null;
        if (settings.SpeedResult is { } speed)
        {
            speed.Unknown = null;
        }

        kinds.ForEach(k => k.Unknown = null);
    }

    private static AppSettings Unreadable(string path, string? keptPath, SettingsLoadFailure failure)
        => new() { LoadProblem = new SettingsLoadProblem(path, keptPath, failure) };

    /// <summary>設定として読む。読めなければ null。文字にする読み方は <see cref="GuardedFile.Decode"/>。</summary>
    private static AppSettings? Parse(byte[] bytes)
    {
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(GuardedFile.Decode(bytes), Options);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException
                                   or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }
}
