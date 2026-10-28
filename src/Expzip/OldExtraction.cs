using System.IO;

namespace Expzip;

/// <summary>
/// 古いバージョンが一時フォルダーに取り出したアセンブリを片付ける (#305)。
/// </summary>
/// <remarks>
/// 1 つにまとめた exe は、起動したときに中身を <c>%TEMP%\.net\Expzip\&lt;印&gt;</c> に取り出して使う
/// (win-x64.pubxml の IncludeAllContentForSelfExtract)。印はビルドごとに変わり、.NET は古いものを消さないので、
/// バージョンを上げるたびに 1 つ 127 MB ほど残っていく。
/// </remarks>
internal static class OldExtraction
{
    /// <summary>これより新しいものは、別のバージョンが取り出している途中かもしれないので触らない。</summary>
    private static readonly TimeSpan Settling = TimeSpan.FromMinutes(10);

    public static void CleanUp()
    {
        var current = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        var exeFolder = Path.GetDirectoryName(Environment.ProcessPath);
        var parent = Path.GetDirectoryName(current);

        // 取り出して動いているときだけ。開発中のビルドは bin フォルダーで動いていて、
        // その隣を消してはいけない
        if (parent is null
            || string.Equals(current, exeFolder, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(Path.GetDirectoryName(parent)), ".net", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string[] folders;
        try
        {
            folders = Directory.GetDirectories(parent);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var folder in folders)
        {
            if (string.Equals(folder, current, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(folder) < Settling || InUse(folder))
                {
                    continue;
                }

                // 名前を変えてから消す。途中で失敗しても .NET が中途半端なものを使うことは無く
                // (同じバージョンを起動すれば取り出し直す)、残りは次に起動したときに消える
                var removing = folder.EndsWith(".old", StringComparison.OrdinalIgnoreCase) ? folder : folder + ".old";
                if (!string.Equals(removing, folder, StringComparison.OrdinalIgnoreCase))
                {
                    Directory.Move(folder, removing);
                }

                Directory.Delete(removing, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 使用中か、消せないものがある。次に起動したときにまた試す
            }
        }
    }

    /// <summary>動いている別のバージョンが使っているか。</summary>
    /// <remarks>
    /// 使っているバージョンは、取り出した DLL を読み込んだままにしている。読み込まれた DLL は
    /// 書き込み用に開けない。フォルダーの名前は、使用中でも変えられてしまうので当てにならない (実測)
    /// </remarks>
    private static bool InUse(string folder)
    {
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            try
            {
                using var probe = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return true;
            }
        }

        return false;
    }
}
