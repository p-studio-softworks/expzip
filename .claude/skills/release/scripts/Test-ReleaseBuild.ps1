# 配るビルドが、今のコミットからそのまま作られたものかを確かめる。NG の数を終了コードで返す。
# 値は読むだけで、ファイルは変えない。
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
Set-Location (git rev-parse --show-toplevel)

$script:Ng = 0
function Ng([string]$Text) { $script:Ng++; Write-Output "NG    $Text" }
function Ok([string]$Text) { Write-Output "OK    $Text" }

$exe = 'publish\win-x64\Expzip.exe'
$dll = 'src\Expzip\bin\Release\net10.0-windows\win-x64\Expzip.dll'
$config = 'src\Expzip\bin\Release\net10.0-windows\win-x64\Expzip.runtimeconfig.json'
foreach ($path in $exe, $dll, $config) {
    if (-not (Test-Path $path)) {
        Write-Output "NG    $path がありません。先に build.cmd を実行してください"
        exit 1
    }
}

# ------------------------------------------------------------------ 作業ツリー
$dirty = @(git status --porcelain)
if ($dirty.Count -gt 0) { Ng "コミットしていない変更が $($dirty.Count) 個あります" } else { Ok '作業ツリーに変更はありません' }

$head = (git rev-parse --short HEAD).Trim()
$pushed = @(git log '@{u}..HEAD' --format=%h 2>$null)
if ($pushed.Count -gt 0) { Ng "push していないコミットが $($pushed.Count) 個あります" } else { Ok 'コミットは push ずみです' }

# ------------------------------------------------------------------ ビルドに入っているコミット (#104、#176)
# AssemblyMetadata は「長さ + 名前 + 長さ + 値」の形で DLL に入っている
$text = [Text.Encoding]::GetEncoding(28591).GetString([IO.File]::ReadAllBytes((Resolve-Path $dll)))
if ($text.Contains('RevisionModified')) {
    Ng '「変更あり」のビルドです。コミットしたあと何も変えずにビルドし直してください'
}
else {
    Ok '「変更あり」の印はありません'
}
$match = [regex]::Match($text, '\x08Revision[\x01-\x28]([0-9a-f]{6,40})')
if (-not $match.Success) {
    Ng 'DLL にリビジョンがありません'
}
elseif ($match.Groups[1].Value -ne $head) {
    Ng "ビルドのリビジョン $($match.Groups[1].Value) が、今のコミット $head と違います"
}
else {
    Ok "リビジョンは今のコミット $head です"
}

# 取り違えを防ぐ。exe はコミットより後に作られたはず
$committed = [DateTimeOffset]::FromUnixTimeSeconds([long](git log -1 --format=%ct)).LocalDateTime
$built = (Get-Item $exe).LastWriteTime
if ($built -lt $committed) { Ng "exe ($built) がコミット ($committed) より古いです" } else { Ok "exe はコミットの後に作られました ($built)" }

# ------------------------------------------------------------------ バージョン
$project = [xml](Get-Content 'src\Expzip\Expzip.csproj' -Encoding UTF8 -Raw)
$version = @($project.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
$file = (Get-Item $exe).VersionInfo
$fileVersion = '{0}.{1}.{2}' -f $file.FileMajorPart, $file.FileMinorPart, $file.FileBuildPart
if ($fileVersion -ne $version) { Ng "exe のバージョン $fileVersion が csproj の $version と違います" } else { Ok "バージョンは $version です" }

$tags = @(git tag --list "v$version")
if ($tags.Count -gt 0) { Ng "タグ v$version は既にあります。バージョンを上げ忘れていないか確かめてください" } else { Ok "タグ v$version はまだありません" }

# ------------------------------------------------------------------ チケットに書く値
$runtime = (Get-Content $config -Encoding UTF8 -Raw | ConvertFrom-Json).runtimeOptions.includedFrameworks
Write-Output ''
Write-Output ("同梱する .NET: " + (@($runtime | ForEach-Object { "$($_.name) $($_.version)" }) -join ', '))
Write-Output ("大きさ: {0:N0} バイト" -f (Get-Item $exe).Length)
Write-Output ("SHA-256: {0}" -f (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant())
$msix = @(Get-ChildItem 'publish\msix\*.msix' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1)
if ($msix.Count -gt 0 -and $msix[0]) { Write-Output "MSIX: $($msix[0].Name) ($($msix[0].LastWriteTime))" }

Write-Output ''
if ($script:Ng -eq 0) { Write-Output '配れるビルドです' } else { Write-Output "NG $script:Ng 件。このビルドは配りません" }
exit $script:Ng
