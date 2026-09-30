using System.Runtime.InteropServices;
using System.Windows;

// Windows の DLL (amsi.dll、crypt32.dll、kernel32.dll、cabinet.dll) は System32 からだけ読み込む (#185)。
// 指定が無いと exe の隣を先に探すので、ダウンロードフォルダーに同じ名前の DLL を置かれると
// そちらを読み込んでしまう (実測: 隣に置いた偽の amsi.dll が、検査のときに読み込まれた)。
// ここに書けば、この中のすべての DllImport に効く。新しく足すものも漏れない
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

[assembly:ThemeInfo(
    ResourceDictionaryLocation.None,            //where theme specific resource dictionaries are located
                                                //(used if a resource is not found in the page,
                                                // or application resource dictionaries)
    ResourceDictionaryLocation.SourceAssembly   //where the generic resource dictionary is located
                                                //(used if a resource is not found in the page,
                                                // app, or any theme specific resource dictionaries)
)]
