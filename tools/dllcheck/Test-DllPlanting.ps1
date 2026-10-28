<#
.SYNOPSIS
    配る Expzip.exe が、exe の隣に置かれた DLL を読み込まないことを確かめる (#305)。

.DESCRIPTION
    ダウンロードフォルダーに version.dll などを置かれると、そこで Expzip.exe を起動しただけで
    その DLL が動く (DLL の差し込み)。1 つにまとめた exe でだけ起きる分があるので、画面テスト
    (1 つにまとめないビルドで動く) では確かめられない。このスクリプトは publish\win-x64\Expzip.exe で確かめる。

    1. 一時フォルダーで Expzip.exe をふつうに起動し、System32 から読み込んだ DLL を集める
    2. それらと、.NET が名前で読み込む Windows の DLL (kernel32.dll など) の写しを exe の隣に並べて起動する
    3. 隣の写しを 1 つでも読み込んだら、または起動できなかったら失敗

    - 写しは System32 の本物をそのままコピーしたもの。読み込まれても害は無い
    - 確かめるのは起動してウィンドウが出たところまで。書庫を開いたあとに読み込む DLL は見ていない
    - 先に build.cmd で publish\win-x64\Expzip.exe を作っておく

.EXAMPLE
    .\tools\dllcheck\Test-DllPlanting.ps1
#>
param([string]$Exe)

$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not $Exe) { $Exe = Join-Path $repo 'publish\win-x64\Expzip.exe' }
if (-not (Test-Path $Exe)) {
    Write-Host "Expzip.exe がありません: $Exe (先に build.cmd を実行してください)"
    exit 1
}

$system = Join-Path $env:SystemRoot 'System32'
$work = Join-Path $env:TEMP 'ExpzipDllCheck'

# .NET の土台 (System.Private.CoreLib) が名前で読み込む Windows の DLL。
# Windows が別に守っている DLL (KnownDLLs) だが、.NET は場所付きで探すので守りが効かない
$coreLib = 'kernel32.dll', 'advapi32.dll', 'ole32.dll', 'oleaut32.dll', 'ntdll.dll', 'user32.dll',
    'shell32.dll', 'crypt32.dll', 'secur32.dll', 'ucrtbase.dll', 'normaliz.dll', 'bcrypt.dll'

# 起動してウィンドウが出るまで待ち、少し置いてから読み込んだ DLL を返す。出なければ $null
function Start-AndCollect([string]$Path) {
    $process = Start-Process $Path -PassThru
    try {
        $deadline = (Get-Date).AddSeconds(30)
        while ((Get-Date) -lt $deadline) {
            $process.Refresh()
            if ($process.HasExited) { return $null }
            if ($process.MainWindowHandle -ne 0) { break }
            Start-Sleep -Milliseconds 200
        }
        if ($process.MainWindowHandle -eq 0) { return $null }

        # ウィンドウが出たあとにも読み込むものがある
        Start-Sleep -Seconds 3
        $process.Refresh()
        return @($process.Modules | ForEach-Object FileName)
    }
    finally {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        $process.WaitForExit(5000) | Out-Null
    }
}

Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path (Join-Path $work 'plain'), (Join-Path $work 'planted') | Out-Null

# 1. ふつうに起動して、System32 から読み込んだ DLL を集める
Copy-Item $Exe (Join-Path $work 'plain')
$loaded = Start-AndCollect (Join-Path $work 'plain\Expzip.exe')
if ($null -eq $loaded) {
    Write-Host 'NG: Expzip が起動しませんでした (隣に何も置いていない状態)'
    exit 1
}
$names = @($loaded |
    Where-Object { $_.StartsWith($system + '\', [StringComparison]::OrdinalIgnoreCase) -and $_ -like '*.dll' } |
    ForEach-Object { [IO.Path]::GetFileName($_).ToLowerInvariant() }) + $coreLib |
    Sort-Object -Unique

# 2. 写しを隣に並べて起動する
$planted = Join-Path $work 'planted'
Copy-Item $Exe $planted
$copied = 0
foreach ($name in $names) {
    $source = Join-Path $system $name
    if (Test-Path $source) {
        Copy-Item $source $planted
        $copied++
    }
}
Write-Host "隣に並べた DLL: $copied 個"

$loaded = Start-AndCollect (Join-Path $planted 'Expzip.exe')
Start-Sleep -Seconds 1
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue

if ($null -eq $loaded) {
    Write-Host 'NG: 隣に DLL を並べると Expzip が起動しませんでした (隣の DLL を読み込んで落ちた見込み)'
    exit 1
}

$hits = @($loaded | Where-Object {
    $_.StartsWith($planted + '\', [StringComparison]::OrdinalIgnoreCase) -and $_ -notlike '*.exe' })
if ($hits.Count -gt 0) {
    Write-Host "NG: 隣の DLL を $($hits.Count) 個読み込みました"
    $hits | ForEach-Object { Write-Host "  $([IO.Path]::GetFileName($_))" }
    exit 1
}

Write-Host 'OK: 隣の DLL を 1 つも読み込みませんでした'
exit 0
