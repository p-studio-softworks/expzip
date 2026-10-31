# 変更の漏れを機械で確かめる。NG の数を終了コードで返す。
#
#   .\Test-Change.ps1                 コミットしていない変更を見る
#   .\Test-Change.ps1 -Base HEAD~1    そのコミットから今までを見る
param([string]$Base = 'HEAD')

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$OutputEncoding = [Text.Encoding]::UTF8
Set-Location (git rev-parse --show-toplevel)

$script:Ng = 0
$script:Notes = 0
function Ng([string]$Text) { $script:Ng++; Write-Output "NG    $Text" }
function Note([string]$Text) { $script:Notes++; Write-Output "注意  $Text" }

$self = '.claude/skills/finish-ticket/scripts/Test-Change.ps1'
$tracked = @(git -c core.quotepath=off diff --name-only --diff-filter=d $Base)
$untracked = @(git -c core.quotepath=off ls-files -o --exclude-standard)
$files = @(@($tracked) + @($untracked) | Where-Object { $_ })

# ------------------------------------------------------------------ 足した行
$added = New-Object System.Collections.ArrayList
$current = $null
foreach ($line in @(git -c core.quotepath=off diff -U0 --diff-filter=d $Base)) {
    if ($line -match '^\+\+\+ b/(.+)$') { $current = $Matches[1]; continue }
    if ($line -match '^\+(.*)$' -and $current) {
        $null = $added.Add([pscustomobject]@{ File = $current; Text = $Matches[1] })
    }
}
foreach ($file in $untracked) {
    if (-not $file) { continue }
    foreach ($line in [IO.File]::ReadAllLines((Join-Path (Get-Location) $file), [Text.Encoding]::UTF8)) {
        $null = $added.Add([pscustomobject]@{ File = $file; Text = $line })
    }
}

# ------------------------------------------------------------------ ファイルの形式
foreach ($file in $files) {
    $path = Join-Path (Get-Location) $file
    if (-not (Test-Path -LiteralPath $path)) { continue }
    $extension = [IO.Path]::GetExtension($file).ToLowerInvariant()
    $bytes = [IO.File]::ReadAllBytes($path)
    if ($extension -in '.cs', '.ps1') {
        if ($bytes.Length -lt 3 -or $bytes[0] -ne 0xEF -or $bytes[1] -ne 0xBB -or $bytes[2] -ne 0xBF) {
            Ng "$file : BOM がありません"
        }
    }
    if ($extension -in '.cs', '.ps1', '.xaml', '.csproj', '.md') {
        $text = [Text.Encoding]::GetEncoding(28591).GetString($bytes)
        if ($text -match '(?<!\r)\n') { Ng "$file : 改行が CRLF でない行があります" }
    }
    if ($extension -eq '.cmd') {
        if (@($bytes | Where-Object { $_ -gt 127 }).Count -gt 0) { Ng "$file : .cmd に ASCII 以外の文字があります" }
    }
}

# ------------------------------------------------------------------ 一緒に変えるもの
$hasJa = $files -contains 'README.ja.md'
$hasEn = $files -contains 'README.md'
if ($hasJa -ne $hasEn) { Ng 'README.ja.md と README.md は同じコミットで変えます' }

$textChanged = $files -contains 'src/Expzip.Core/Localization/Strings.Text.cs'
if ($textChanged -and ($files -notcontains 'tools/stringdump/strings.txt')) {
    Ng '文言を変えましたが tools/stringdump/strings.txt が変わっていません (build.cmd -Debug のあとで stringdump)'
}
if ($textChanged -and -not @($files | Where-Object { $_ -like 'tests/ui/suites/*' }).Count) {
    Note '文言を変えましたが tests/ui/suites が変わっていません。期待値を確かめてください'
}
$srcChanged = @($files | Where-Object { $_ -like 'src/*' }).Count -gt 0
if ($srcChanged -and ($files -notcontains 'docs/SPEC.md')) {
    Note 'src を変えましたが docs/SPEC.md が変わっていません。動きが変わったなら直してください'
}

# ------------------------------------------------------------------ 足した行の中身
# 決まりの元は .claude/CLAUDE.md
$words = @(
    @{ P = '色弱|色盲'; M = '「色の見え方の違い」か「色覚特性」と書きます' }
    @{ P = 'パソコン|コンピューター(?!名)'; M = '「PC」と書きます' }
    @{ P = '道具'; M = '「ツール」と書きます' }
    @{ P = '台本'; M = '「スクリプト」と書きます' }
    @{ P = '模型'; M = '「モデル」と書きます' }
    @{ P = '建てる|建てた'; M = '「ビルド」と書きます' }
    @{ P = '窓'; M = '「ウィンドウ」と書きます' }
    @{ P = '製インストーラー'; M = '「製」は付けません' }
    @{ P = '包んだ'; M = '「〜が入った」と書きます' }
    @{ P = 'フォルダ(?!ー)|ヘッダ(?!ー)'; M = '長音を書きます (フォルダー、ヘッダー)' }
    @{ P = 'すでに'; M = '「既に」と書きます' }
    @{ P = '\d(個|件)'; M = '数と単位の間に半角の空白を入れます' }
)
foreach ($item in $added) {
    if ($item.File -eq $self -or $item.File -eq 'tools/stringdump/strings.txt') { continue }
    $where = "$($item.File) : $($item.Text.Trim())"
    if ($where.Length -gt 110) { $where = $where.Substring(0, 110) + '…' }

    if ($item.Text -match '192\.168\.\d+\.\d+|/issues/\d+|/projects/expzip') {
        Ng "課題管理へのリンクはリポジトリに書きません: $where"
    }
    if ($item.File -like '*.md') {
        # 奇数個目の ** が開く印、偶数個目が閉じる印。開く印の前に句読点があるのは問題ない
        $marks = @([regex]::Matches($item.Text, '\*\*'))
        for ($i = 1; $i -lt $marks.Count; $i += 2) {
            if ($marks[$i].Index -gt 0 -and '。、．！？'.Contains($item.Text[$marks[$i].Index - 1])) {
                Ng "太字は句読点の前で閉じます (**…**。): $where"
                break
            }
        }
        if ($item.Text -cmatch '[^\s「（(*]\*\*「') { Ng "太字は「 の内側で開きます (「**…**」): $where" }
    }
    if ($item.Text -cmatch '\bMac\b|macOS') {
        Note "特定の環境の話を公開のリポジトリに書いていないか確かめてください (#204): $where"
    }
    foreach ($word in $words) {
        if ($item.Text -cmatch $word.P) { Note "$($word.M): $where" }
    }
}

# ------------------------------------------------------------------ push していないコミット
$range = @(git log '@{u}..HEAD' --format='%h%x09%ae%x09%s' 2>$null)
foreach ($commit in $range) {
    if (-not $commit) { continue }
    $parts = $commit -split "`t", 3
    if ($parts[1] -ne '193758124+paliensup@users.noreply.github.com') { Ng "$($parts[0]) : 作者が paliensup ではありません ($($parts[1]))" }
    if ($parts[2] -notmatch '\(#\d+') { Note "$($parts[0]) : 件名にチケットの番号がありません: $($parts[2])" }
    if ((git log -1 --format=%B $parts[0]) -match 'Co-Authored-By') { Ng "$($parts[0]) : Co-Authored-By を付けません" }
}

Write-Output "変更したファイル $($files.Count) 個 / NG $script:Ng 件 / 注意 $script:Notes 件"
exit $script:Ng
