using System.IO;
using ICSharpCode.SharpZipLib.Zip;

namespace Expzip.Archives;

/// <summary>
/// SharpZipLib に、決めた場所の作業用ファイルを使わせる (<see cref="ZipArchiveWriter.WorkFilePathFor"/>)。
/// </summary>
/// <remarks>
/// <para>
/// SharpZipLib の既定 (<c>DiskArchiveStorage</c>) は、書庫の隣に自分で決めた名前の作業用ファイルを作り、
/// 差し替えるときに書庫をもう一つ別の名前へ退避する。その間に止まると、元の名前に書庫が無い (#320)。
/// 作業用ファイルを置ける場所や名前に決まりのある環境では、その名前も作れない。
/// </para>
/// <para>
/// ここでは決めた場所に書き、できたら書庫の上へ移して (rename) 差し替える。移すのは 1 回で、
/// 途中で失敗しても元の書庫はそのまま残る。作業用ファイルは、うまくいかなかったときも消す。
/// </para>
/// </remarks>
internal sealed class WorkFileStorage(string archivePath, string workPath) : BaseArchiveStorage(FileUpdateMode.Safe)
{
    private FileStream? _work;

    public override Stream GetTemporaryOutput()
    {
        _work = new FileStream(workPath, ZipArchiveWriter.WorkFileMode, FileAccess.ReadWrite, FileShare.None);
        return _work;
    }

    public override Stream ConvertTemporaryToFinal()
    {
        if (_work is null)
        {
            throw new ZipException("No temporary stream has been created");
        }

        _work.Dispose();
        _work = null;

        AtomicFile.Replace(workPath, archivePath);
        return new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    public override Stream MakeTemporaryCopy(Stream stream)
    {
        stream.Dispose();
        File.Copy(archivePath, workPath, overwrite: true);
        _work = new FileStream(workPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        return _work;
    }

    public override Stream OpenForDirectUpdate(Stream stream)
    {
        // 直接の書き換えは使わない (Safe で作る)。呼ばれたときは SharpZipLib の既定と同じく、書庫を開き直して渡す
        if (stream is null || !stream.CanWrite)
        {
            stream?.Dispose();
            return new FileStream(archivePath, FileMode.Open, FileAccess.ReadWrite);
        }

        return stream;
    }

    public override void Dispose()
    {
        _work?.Dispose();
        _work = null;
        ZipArchiveWriter.TryDelete(workPath);
    }
}
