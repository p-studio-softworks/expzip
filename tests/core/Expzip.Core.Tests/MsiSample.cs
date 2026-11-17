using System.Runtime.InteropServices;
using System.Text;

namespace Expzip.Tests;

/// <summary>
/// テストのための MSI を、Windows の <c>msi.dll</c> と makecab でその場で作る (#390)。
/// </summary>
/// <remarks>
/// 中身は 3 個。埋め込んだ CAB (<c>#inside.cab</c>) に 1 個、隣の CAB (<see cref="ExternalCab"/>) に 1 個、
/// 圧縮せずに MSI の隣の元の置き場所 (<see cref="LoosePath"/>) に 1 個。表は読むのに要る列だけで、入れられる MSI ではない。
/// </remarks>
internal static class MsiSample
{
    /// <summary>MSI の隣の CAB の名前。</summary>
    public const string ExternalCab = "ext.cab";

    /// <summary>圧縮していないファイルの、MSI のフォルダーから見た置き場所。</summary>
    public const string LoosePath = @"PFiles\App\loose.txt";

    /// <summary>MSI を作る。</summary>
    /// <param name="folder">MSI と隣のファイルを置くフォルダー。</param>
    /// <param name="looseName">圧縮していないファイルの、File 表の名前。</param>
    /// <returns>MSI のパス。</returns>
    public static string Create(string folder, string looseName = "loose.txt")
    {
        var work = Path.Combine(folder, "msiwork");
        Directory.CreateDirectory(work);
        File.WriteAllText(Path.Combine(work, "f1"), "inside", Encoding.ASCII);
        File.WriteAllText(Path.Combine(work, "f2"), "external", Encoding.ASCII);
        Cab(work, "inside.cab", "f1");
        Cab(work, ExternalCab, "f2");
        File.Move(Path.Combine(work, ExternalCab), Path.Combine(folder, ExternalCab));

        var loose = Path.Combine(folder, LoosePath);
        Directory.CreateDirectory(Path.GetDirectoryName(loose)!);
        File.WriteAllText(loose, "loose", Encoding.ASCII);

        var msi = Path.Combine(folder, "sample.msi");
        Check(MsiOpenDatabaseW(msi, CreateDirect, out var database));

        try
        {
            Sql(database, "CREATE TABLE `Directory` (`Directory` CHAR(72) NOT NULL, `Directory_Parent` CHAR(72), `DefaultDir` CHAR(255) NOT NULL PRIMARY KEY `Directory`)");
            Sql(database, "CREATE TABLE `Component` (`Component` CHAR(72) NOT NULL, `Directory_` CHAR(72) NOT NULL PRIMARY KEY `Component`)");
            Sql(database, "CREATE TABLE `File` (`File` CHAR(72) NOT NULL, `Component_` CHAR(72) NOT NULL, `FileName` CHAR(255) NOT NULL, `FileSize` LONG NOT NULL, `Attributes` SHORT, `Sequence` SHORT NOT NULL PRIMARY KEY `File`)");
            Sql(database, "CREATE TABLE `Media` (`DiskId` SHORT NOT NULL, `LastSequence` SHORT NOT NULL, `Cabinet` CHAR(255) PRIMARY KEY `DiskId`)");
            Sql(database, "INSERT INTO `Directory` (`Directory`, `Directory_Parent`, `DefaultDir`) VALUES ('TARGETDIR', '', 'SourceDir')");
            Sql(database, "INSERT INTO `Directory` (`Directory`, `Directory_Parent`, `DefaultDir`) VALUES ('ProgramFilesFolder', 'TARGETDIR', 'PFiles')");
            Sql(database, "INSERT INTO `Directory` (`Directory`, `Directory_Parent`, `DefaultDir`) VALUES ('APPDIR', 'ProgramFilesFolder', 'App')");
            Sql(database, "INSERT INTO `Component` (`Component`, `Directory_`) VALUES ('App', 'APPDIR')");
            Sql(database, "INSERT INTO `File` (`File`, `Component_`, `FileName`, `FileSize`, `Attributes`, `Sequence`) VALUES ('f1', 'App', 'inside.txt', 6, 0, 1)");
            Sql(database, "INSERT INTO `File` (`File`, `Component_`, `FileName`, `FileSize`, `Attributes`, `Sequence`) VALUES ('f2', 'App', 'external.txt', 8, 0, 2)");
            // 0x2000 は「圧縮しない」。MSI の隣の、元の置き場所から読む
            Sql(database, $"INSERT INTO `File` (`File`, `Component_`, `FileName`, `FileSize`, `Attributes`, `Sequence`) VALUES ('f3', 'App', '{looseName}', 5, 8192, 3)");
            Sql(database, "INSERT INTO `Media` (`DiskId`, `LastSequence`, `Cabinet`) VALUES (1, 1, '#inside.cab')");
            Sql(database, $"INSERT INTO `Media` (`DiskId`, `LastSequence`, `Cabinet`) VALUES (2, 3, '{ExternalCab}')");

            // 埋め込む CAB
            Check(MsiDatabaseOpenViewW(database, "INSERT INTO `_Streams` (`Name`, `Data`) VALUES (?, ?)", out var view));
            var record = MsiCreateRecord(2);
            try
            {
                Check(MsiRecordSetStringW(record, 1, "inside.cab"));
                Check(MsiRecordSetStreamW(record, 2, Path.Combine(work, "inside.cab")));
                Check(MsiViewExecute(view, record));
            }
            finally
            {
                MsiCloseHandle(record);
                MsiCloseHandle(view);
            }

            // 要約情報の語数 2 は「既定で圧縮、長い名前」
            Check(MsiGetSummaryInformationW(database, null, 1, out var summary));
            try
            {
                Check(MsiSummaryInfoSetPropertyW(summary, 15, 3, 2, IntPtr.Zero, null));
                Check(MsiSummaryInfoPersist(summary));
            }
            finally
            {
                MsiCloseHandle(summary);
            }

            Check(MsiDatabaseCommit(database));
        }
        finally
        {
            MsiCloseHandle(database);
        }

        Directory.Delete(work, recursive: true);
        return msi;
    }

    /// <summary>1 個のファイルを makecab で CAB にする。CAB の中の名前は File 表の鍵。</summary>
    private static void Cab(string work, string name, string key)
    {
        var ddf = Path.Combine(work, name + ".ddf");
        File.WriteAllLines(ddf,
        [
            $".Set CabinetNameTemplate={name}",
            $".Set DiskDirectoryTemplate={work}",
            ".Set Cabinet=on",
            ".Set Compress=on",
            $"\"{Path.Combine(work, key)}\" \"{key}\"",
        ], Encoding.ASCII);
        SourceMarkTests.Run("makecab.exe", $"/F \"{ddf}\"", work);
    }

    private static void Sql(IntPtr database, string query)
    {
        Check(MsiDatabaseOpenViewW(database, query, out var view));
        try
        {
            Check(MsiViewExecute(view, IntPtr.Zero));
        }
        finally
        {
            MsiCloseHandle(view);
        }
    }

    private static void Check(uint result)
    {
        if (result != 0)
        {
            throw new InvalidOperationException($"msi.dll の失敗 {result}");
        }
    }

    // MSIDBOPEN_CREATEDIRECT
    private static readonly IntPtr CreateDirect = 4;

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiOpenDatabaseW(string path, IntPtr persist, out IntPtr database);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiDatabaseOpenViewW(IntPtr database, string query, out IntPtr view);

    [DllImport("msi.dll", ExactSpelling = true)]
    private static extern uint MsiViewExecute(IntPtr view, IntPtr record);

    [DllImport("msi.dll", ExactSpelling = true)]
    private static extern IntPtr MsiCreateRecord(uint fields);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiRecordSetStringW(IntPtr record, uint field, string value);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiRecordSetStreamW(IntPtr record, uint field, string path);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiGetSummaryInformationW(IntPtr database, string? path, uint updateCount, out IntPtr summary);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint MsiSummaryInfoSetPropertyW(
        IntPtr summary, uint property, uint type, int value, IntPtr time, string? text);

    [DllImport("msi.dll", ExactSpelling = true)]
    private static extern uint MsiSummaryInfoPersist(IntPtr summary);

    [DllImport("msi.dll", ExactSpelling = true)]
    private static extern uint MsiDatabaseCommit(IntPtr database);

    [DllImport("msi.dll", ExactSpelling = true)]
    private static extern uint MsiCloseHandle(IntPtr handle);
}
