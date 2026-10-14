# 1 個だけ圧縮したファイル (.gz / .bz2 / .xz / .zst、#256) と Zstandard (#257) を開く
. "$PSScriptRoot\..\Common.ps1"
Start-Suite 'Compressed'
Set-Settings

# Windows には bzip2 / xz / Zstandard を作る標準のツールが無いので、作っておいたものを使う (tests\ui\fixtures)。
# - memo.txt.bz2 / .xz / .zst: 「zstandard no naka」と改行の 18 バイトを、bzip2 / xz コマンドと SharpCompress で圧縮したもの
# - named.gz: 見出しに元の名前「元の名前.txt」(UTF-8) と更新日時 2020-01-02 03:04:05 (UTC) を書いた gzip。
#   中身は「gzip no naka」と改行
# - tarball.gz: memo.txt と docs\a.txt を入れた tar を gzip で圧縮し、名前を .gz にしたもの
# - tarball.tar.zst: 同じ tar を Zstandard で圧縮したもの
# - zstd.zip: memo.txt と docs/a.txt を Zstandard (圧縮方式 93) で入れた ZIP。SharpCompress で作った
$fixtures = Join-Path $PSScriptRoot '..\fixtures'

function Use-Fixture([string]$Name, [string]$As = $Name) {
    $path = Join-Path $script:Work $As
    Copy-Item (Join-Path $fixtures $Name) $path
    return $path
}

# 書庫全体を展開して、展開先のフォルダーを返す
function Extract-All($App, [string]$Label) {
    $destination = Join-Path $script:Work $Label
    New-Item -ItemType Directory -Force -Path $destination | Out-Null
    Push (ById $App.Window 'ExtractButton')
    Check "$($Label): 展開先を尋ねる" (Complete-FileDialog $App '書庫全体の展開先を選択' $destination)
    $box = Find-MessageBox $App 30000
    if ($box) { Close-MessageBox $box 'OK' }
    return $destination
}

function Read-Out([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return '(無い)' }
    return [IO.File]::ReadAllText($Path)
}

Section 'gzip の見出しにある名前と日時'
$named = Use-Fixture 'named.gz'
$app = Start-Expzip @($named)
$names = (Get-RowNames $app) -join ', '
Check '元の名前で 1 個並ぶ' ($names -eq '元の名前.txt') $names
Check '読み取りのみと添える' ((Texts $app.Window) -match 'gzip / 読み取りのみ') (Texts $app.Window)
Check '追加できない' (-not (ById $app.Window 'AddButton').Current.IsEnabled)
$row = Texts (Find-Row $app '元の名前.txt')
$stamp = [DateTimeOffset]::FromUnixTimeSeconds(1577934245).LocalDateTime.ToString('yyyy/MM/dd HH:mm')
Check '元の大きさ' ($row -match '(^| \| )13( \| |$)') $row
Check '見出しの日時' ($row -match [regex]::Escape($stamp)) $row
$out = Extract-All $app 'named'
$text = Read-Out (Join-Path $out '元の名前.txt')
Check '中身が元と同じ' ($text -eq "gzip no naka`n") $text

Push (ById $app.Window 'InspectButton')
Wait-Idle $app 60000
$window = Find-Window $app '検査結果 - named.gz' 30000
Check '検査の窓が開く' ($null -ne $window)
if ($window) {
    Check '検査で問題は無い' ((ById $window 'Headline').Current.Name -eq '問題は見つかりませんでした') (ById $window 'Headline').Current.Name
    Push (ById $window 'CloseButton')
}
Stop-Expzip $app

Section '名前を持たない形式は、書庫の名前から拡張子を除く'
foreach ($kind in @(
        @{ File = 'memo.txt.bz2'; Format = 'bzip2' },
        @{ File = 'memo.txt.xz'; Format = 'xz' },
        @{ File = 'memo.txt.zst'; Format = 'Zstandard' })) {
    $path = Use-Fixture $kind.File
    $app = Start-Expzip @($path)
    $names = (Get-RowNames $app) -join ', '
    Check "$($kind.Format): memo.txt が並ぶ" ($names -eq 'memo.txt') $names
    Check "$($kind.Format): 形式の名前" ((Texts $app.Window) -match "$($kind.Format) / 読み取りのみ") (Texts $app.Window)
    $row = Texts (Find-Row $app 'memo.txt')
    Check "$($kind.Format): 伸長して数えた大きさ" ($row -match '(^| \| )18( \| |$)') $row
    $out = Extract-All $app $kind.Format
    $text = Read-Out (Join-Path $out 'memo.txt')
    Check "$($kind.Format): 中身が元と同じ" ($text -eq "zstandard no naka`n") $text
    Stop-Expzip $app
}

Section '中身が tar なら tar として開く'
foreach ($file in 'tarball.gz', 'tarball.tar.zst') {
    $path = Use-Fixture $file
    $app = Start-Expzip @($path)
    $names = ((Get-RowNames $app) | Sort-Object) -join ', '
    Check "$($file): tar の中身が並ぶ" ($names -eq 'docs, memo.txt') $names
    Check "$($file): tar と出る" ((Texts $app.Window) -match 'tar / 読み取りのみ') (Texts $app.Window)
    # 展開先のフォルダーの名前に「.gz」が付いていると、フォルダーを選ぶ画面がファイルとして扱う
    $out = Extract-All $app ($file -replace '\.', '_')
    $text = Read-Out (Join-Path $out 'docs\a.txt')
    Check "$($file): 中身が元と同じ" ($text -eq "tar no naka`n") $text
    Stop-Expzip $app
}

Section 'ZIP の Zstandard (圧縮方式 93)'
$zip = Use-Fixture 'zstd.zip'
$app = Start-Expzip @($zip)
$names = ((Get-RowNames $app) | Sort-Object) -join ', '
Check '一覧が出る' ($names -eq 'docs, memo.txt') $names
$out = Extract-All $app 'zstdzip'
$text = Read-Out (Join-Path $out 'memo.txt')
Check '中身が元と同じ' ($text -eq "zstandard no naka`n") $text
Push (ById $app.Window 'InspectButton')
Wait-Idle $app 60000
$window = Find-Window $app '検査結果 - zstd.zip' 30000
if ($window) {
    Check '検査で問題は無い' ((ById $window 'Headline').Current.Name -eq '問題は見つかりませんでした') (ById $window 'Headline').Current.Name
    Push (ById $window 'CloseButton')
}
else {
    Check '検査の窓が開く' $false
}
Stop-Expzip $app

Section '壊れたもの'
# 印は合っているが、途中が壊れている。開く時点で最後まで読むので、そこで分かる
$broken = Join-Path $script:Work '壊れ.txt.xz'
$bytes = [IO.File]::ReadAllBytes((Join-Path $fixtures 'memo.txt.xz'))
for ($i = 24; $i -lt 40; $i++) { $bytes[$i] = $bytes[$i] -bxor 0x5A }
[IO.File]::WriteAllBytes($broken, $bytes)
$app = Start-Expzip
$center = Get-WindowCenter $app
Invoke-Drop @($broken) $center.X $center.Y | Out-Null
$box = Find-MessageBox $app 30000
Check '知らせが出る' ($null -ne $box)
if ($box) {
    Check '理由はデータが壊れていること' ($box.Text -match '壊れ\.txt\.xz を開けませんでした。' -and $box.Text -match '書庫のデータが壊れています。$') $box.Text
    Close-MessageBox $box 'OK'
}

# 名前だけ .gz で、中身が gzip でないもの
$fake = Join-Path $script:Work 'nise.gz'
[IO.File]::WriteAllText($fake, 'gzip ではない')
Focus-App $app
Invoke-Drop @($fake) $center.X $center.Y | Out-Null
$box = Find-MessageBox $app 30000
Check '中身が違うものも知らせが出る' ($null -ne $box)
if ($box) {
    Check '開けなかったと出る' ($box.Text -match 'nise\.gz を開けませんでした。') $box.Text
    Close-MessageBox $box 'OK'
}
$app.Process.Refresh()
Check '落ちない' (-not $app.Process.HasExited)
Stop-Expzip $app

Complete-Suite
