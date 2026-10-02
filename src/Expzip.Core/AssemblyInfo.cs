using System.Runtime.InteropServices;

// Windows の DLL (amsi.dll、crypt32.dll、kernel32.dll、cabinet.dll、msi.dll) は System32 からだけ読み込む (#185)。
// 指定が無いと exe の隣を先に探すので、ダウンロードフォルダーに同じ名前の DLL を置かれると
// そちらを読み込んでしまう (実測: 隣に置いた偽の amsi.dll が、検査のときに読み込まれた)。
// ここに書けば、この中のすべての DllImport に効く。新しく足すものも漏れない。
// **画面の側 (Expzip) にも同じ指定がある。**指定は部品 (アセンブリ) ごとに効くので、両方に要る
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
