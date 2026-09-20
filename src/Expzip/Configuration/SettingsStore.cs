using System.IO;
using System.Text.Json;

namespace Expzip.Configuration;

/// <summary>設定ファイルの読み書き。</summary>
/// <remarks>
/// <para>
/// 保存先は exe と同じフォルダ (docs/SPEC.md 7章)。<c>%APPDATA%</c> は使わない。
/// フォルダにコピーするだけで動き、消すときはフォルダごと消せる状態を保つため。
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

    /// <summary>設定ファイルのパス。</summary>
    public static string FilePath { get; } = Path.Combine(BaseDirectory(), FileName);

    /// <summary>
    /// exe が置かれているフォルダ。
    /// 単一ファイルとして発行した場合、実行時に展開される一時フォルダではなく
    /// exe 自身の場所を指す必要があるため <see cref="Environment.ProcessPath"/> を使う。
    /// </summary>
    private static string BaseDirectory()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(processPath))
        {
            var directory = Path.GetDirectoryName(processPath);
            if (!string.IsNullOrEmpty(directory))
            {
                return directory;
            }
        }

        return AppContext.BaseDirectory;
    }

    /// <summary>
    /// 設定を読み込む。ファイルが無い場合や壊れている場合は既定値を返す。
    /// </summary>
    /// <remarks>
    /// 壊れた設定ファイルで起動できなくなるのが最も困るので、
    /// 読めなければ黙って既定値で動かす。
    /// </remarks>
    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(FilePath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();

            // 古い設定ファイルや手で編集されたものには項目が無いことがある
            settings.RecentArchives ??= [];
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or JsonException or NotSupportedException)
        {
            return new AppSettings();
        }
    }

    /// <summary>設定を保存する。</summary>
    /// <returns>保存できたら true。書き込めない場所に置かれている場合は false。</returns>
    public static bool TrySave(AppSettings settings)
    {
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or NotSupportedException)
        {
            return false;
        }
    }
}
