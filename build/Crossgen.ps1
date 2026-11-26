<#
.SYNOPSIS
    ReadyToRun に使うツール (crossgen) のパッケージの指紋の控え (build\crossgen.sha512) を作り直す (#398)。

.DESCRIPTION
    crossgen は発行のときに、ビルドする PC の上で動くプログラム。NuGet はこれを lock ファイルに書かない
    取り方 (PackageDownload) で取ってくるので、lock ファイルでは中身を固定できない (#323)。
    そこで指紋を控えておき、発行の中で crossgen を動かす前に、Expzip.csproj の CheckCrossgenPin が比べる。
    控えるのも比べるのも、NuGet の置き場に届いたパッケージのファイル (.nupkg) の指紋だけ。実際に動く、
    展開済みの tools\ の下のファイルは比べず、NuGet の置き場を信じる。

    控えを書く前に、nuget.org のカタログが公開している指紋 (packageHash) と比べる。
    置き場にあるパッケージがすり替えられていても、そのまま控えにしないため。

    Update-LockFiles.ps1 から読み込んで使う。
#>

$script:CrossgenPinPath = Join-Path $PSScriptRoot 'crossgen.sha512'

# 配布用の復元の記録 (project.assets.json) から、取ってきた crossgen のパッケージをすべて探す。
# 版は SDK が決める
function Get-CrossgenPackages([string]$Repo) {
    $assets = Join-Path $Repo 'src\Expzip\obj\project.assets.json'
    if (-not (Test-Path $assets)) { return @() }

    $json = Get-Content $assets -Raw -Encoding UTF8 | ConvertFrom-Json
    $packages = $json.project.restore.packagesPath
    $found = @()
    foreach ($framework in $json.project.frameworks.PSObject.Properties) {
        foreach ($download in @($framework.Value.downloadDependencies)) {
            if ($download.name -notlike 'Microsoft.NETCore.App.Crossgen2.*') { continue }

            # 版は "[10.0.15, 10.0.15]" の形で書かれている
            $version = ($download.version -replace '[\[\]\s]', '' -split ',')[0]
            $id = $download.name.ToLowerInvariant()
            $found += [pscustomobject]@{
                Name    = $download.name
                Version = $version
                Nupkg   = Join-Path $packages "$id\$version\$id.$version.nupkg"
            }
        }
    }
    return $found
}

# パッケージの SHA512 を、NuGet と同じく Base64 で返す。
# NuGet が置き場に残した記録 (.nupkg.sha512) は使わず、パッケージそのものから計算する
function Get-PackageHash([string]$Path) {
    $sha = [System.Security.Cryptography.SHA512]::Create()
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        return [Convert]::ToBase64String($sha.ComputeHash($stream))
    }
    finally {
        $stream.Dispose()
        $sha.Dispose()
    }
}

# nuget.org のカタログが公開している、そのパッケージの SHA512
function Get-PublishedHash($Package) {
    $id = $Package.Name.ToLowerInvariant()
    $registration = Invoke-RestMethod "https://api.nuget.org/v3/registration5-semver1/$id/$($Package.Version).json"
    $entry = Invoke-RestMethod $registration.catalogEntry
    if ($entry.packageHashAlgorithm -ne 'SHA512') {
        throw "nuget.org のカタログの指紋が SHA512 ではありません: $($entry.packageHashAlgorithm)"
    }
    return $entry.packageHash
}

# 控えを作り直す。nuget.org と合わないものがあれば書かずに理由を返す。書いたら $null
function Update-CrossgenPin([string]$Repo) {
    $packages = @(Get-CrossgenPackages $Repo)
    if ($packages.Count -eq 0) {
        return '配布用の復元の記録に crossgen のパッケージがありません。'
    }

    $lines = @()
    foreach ($package in $packages) {
        if (-not (Test-Path $package.Nupkg)) {
            return "crossgen のパッケージがありません: $($package.Nupkg)"
        }
        $local = Get-PackageHash $package.Nupkg
        $published = Get-PublishedHash $package
        # 大文字と小文字を区別して比べる
        if (-not [string]::Equals($local, $published, [StringComparison]::Ordinal)) {
            return "置き場の crossgen のパッケージが、nuget.org の公開しているものと違います: $($package.Name) $($package.Version)`n  置き場: $local`n  nuget.org: $published"
        }
        $lines += '{0} {1} {2}' -f $package.Name, $package.Version, $local
    }

    [System.IO.File]::WriteAllText($script:CrossgenPinPath, (($lines | ForEach-Object { "$_`r`n" }) -join ''), (New-Object System.Text.UTF8Encoding($false)))
    foreach ($line in $lines) { Write-Host "crossgen のパッケージの控えを作り直しました: $(($line -split ' ')[0..1] -join ' ') (nuget.org の指紋と一致)" }
    return $null
}
