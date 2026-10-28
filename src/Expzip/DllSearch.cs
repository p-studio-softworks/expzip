using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Expzip;

/// <summary>
/// 名前だけで読み込まれる DLL を、exe の隣から探さないようにする (#305)。
/// </summary>
/// <remarks>
/// 指定が無いと Windows は exe の隣を先に探すので、ダウンロードフォルダーに version.dll などを
/// 置かれると、Expzip を起動しただけでそれが動く。DllImport の分は #185 で System32 に限っているが、
/// .NET や WPF が後から読み込む DLL には効かない。起動のいちばん最初に、プロセス全体の探し方を
/// System32 と .NET の部品の置き場だけにする。
/// .NET 自身がアセンブリの隣を探す分は、発行の設定で塞ぐ (win-x64.pubxml の IncludeAllContentForSelfExtract)。
/// </remarks>
internal static class DllSearch
{
    private const uint LoadLibrarySearchUserDirs = 0x400;
    private const uint LoadLibrarySearchSystem32 = 0x800;

    [ModuleInitializer]
    internal static void Restrict()
    {
        var exeFolder = Path.GetDirectoryName(Environment.ProcessPath);

        // WPF の部品 (wpfgfx など) は一時フォルダーに取り出されて、互いを名前で読み込む。
        // その置き場は探す先に残す。exe の隣は残さない
        if (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is string folders)
        {
            foreach (var folder in folders.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
                if (!string.Equals(path, exeFolder, StringComparison.OrdinalIgnoreCase) && Directory.Exists(path))
                {
                    AddDllDirectory(path);
                }
            }
        }

        SetDefaultDllDirectories(LoadLibrarySearchSystem32 | LoadLibrarySearchUserDirs);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDefaultDllDirectories(uint directoryFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AddDllDirectory(string newDirectory);
}
