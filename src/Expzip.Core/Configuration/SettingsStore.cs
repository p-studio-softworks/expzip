using System.IO;
using System.Text.Json;
using Expzip.Archives;

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
    /// 保存できたら true。書き込めない場所に置かれている場合と、設定ファイルを読めなかったセッション (#399) では false。
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

        try
        {
            // 途中で止まっても、前の設定が残る (#320)
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(settings, Options));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or NotSupportedException)
        {
            return false;
        }
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
