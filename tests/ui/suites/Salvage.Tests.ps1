# 末尾が欠けて開けない ZIP から、無事なファイルだけで新しい書庫を作る (#217)
. "$PSScriptRoot\..\Common.ps1"
Start-Suite 'Salvage'
Set-Settings

# フォルダーは数に入れない。中身の大きさを揃えて、どこで切ればどのファイルの途中になるかを決めやすくする
function New-Body([char]$Letter) { return ([string]$Letter) * 10000 }

$source = New-TestZip (Join-Path $script:Work 'source.zip') ([ordered]@{
    'docs/'       = ''
    'docs/a.txt'  = (New-Body 'a')
    'b.txt'       = (New-Body 'b')
    'c.txt'       = (New-Body 'c')
}) @('docs/a.txt', 'b.txt', 'c.txt')

# 中央ディレクトリの大きさは終端レコード (末尾 22 バイト) に書いてある
$bytes = [System.IO.File]::ReadAllBytes($source)
$directorySize = [BitConverter]::ToUInt32($bytes, $bytes.Length - 22 + 12)
$filesEnd = $bytes.Length - 22 - $directorySize

# 書庫を先頭から $Length バイトだけ残したものを作る
function New-CutZip([string]$Name, [long]$Length) {
    $path = Join-Path $script:Work $Name
    [System.IO.File]::WriteAllBytes($path, $bytes[0..($Length - 1)])
    return $path
}

# 起動してすぐ出たダイアログは、前に出ていないことがある。前に出ていないと押しても効かない (BM_CLICK の決まり)
function Find-FrontBox($App, [int]$TimeoutMs = 10000) {
    $box = Find-MessageBox $App $TimeoutMs
    if ($box) {
        [ExpzipUi.Native]::SetForegroundWindow([IntPtr]$box.Element.Current.NativeWindowHandle) | Out-Null
        Start-Sleep -Milliseconds 300
    }
    return $box
}

function Get-Hash([string]$Path) { return (Get-FileHash -Path $Path -Algorithm SHA256).Hash }

Section '途中で切れた書庫'
# 最後の c.txt の途中で切る
$cut = New-CutZip '切れた.zip' ($filesEnd - 5000)
$mark = "[ZoneTransfer]`r`nZoneId=3`r`nHostUrl=https://example.com/files/archive.zip"
Set-Content -Path $cut -Stream Zone.Identifier -Value $mark -Encoding Ascii
$expectedMark = (Get-Content -Path $cut -Stream Zone.Identifier) -join "`n"
$before = Get-Hash $cut
$repaired = Join-Path $script:Work '切れた (修復).zip'

$app = Start-Expzip @($cut)
$box = Find-FrontBox $app
Check '開けなかったときに尋ねる' ($null -ne $box)
if ($box) {
    Check '断りの文' ($box.Text -match '^.*切れた\.zip を開けませんでした。') $box.Text
    Check '末尾が欠けていると言う' ($box.Text -match '書庫の末尾が欠けています。') $box.Text
    Check '尋ねる文' ($box.Text -match '無事なファイルのみで新しい書庫にしますか。元の書庫はそのまま残ります。$') $box.Text
    Check 'はい と いいえ' (($box.Buttons -contains 'はい(Y)') -and ($box.Buttons -contains 'いいえ(N)')) ($box.Buttons -join ', ')
    Close-MessageBox $box 'はい(Y)'
}

$box = Find-FrontBox $app
Check '結果を知らせる' ($null -ne $box)
if ($box) {
    Check '取り戻した数' ($box.Text -match '^無事なファイル 2 個で 切れた \(修復\)\.zip を作成しました。') $box.Text
    Check '失ったファイルと訳' ($box.Text -match 'c\.txt … 途中で切れています。') $box.Text
    Check '後ろは分からないと言う' ($box.Text -match 'それより後ろにあったファイルは取り戻せません。$') $box.Text
    Close-MessageBox $box 'OK'
}
Wait-Idle $app

Check '隣に作る' (Test-Path -LiteralPath $repaired) $repaired
Check '元の書庫は変わらない' ((Get-Hash $cut) -eq $before)
if (Test-Path -LiteralPath $repaired) {
    $names = Get-ZipNames $repaired
    Check '無事なファイルが入る' ((($names -contains 'docs/a.txt') -and ($names -contains 'b.txt') -and ($names -contains 'docs/'))) ($names -join ', ')
    Check '切れたファイルは入らない' (-not ($names -contains 'c.txt')) ($names -join ', ')

    $zip = [System.IO.Compression.ZipFile]::OpenRead($repaired)
    try {
        $reader = New-Object System.IO.StreamReader(($zip.GetEntry('b.txt')).Open())
        $text = $reader.ReadToEnd()
        $reader.Dispose()
    }
    finally { $zip.Dispose() }
    Check '中身もそのまま' ($text -eq (New-Body 'b'))

    $zone = (Get-Content -Path $repaired -Stream Zone.Identifier -ErrorAction SilentlyContinue) -join "`n"
    Check '出所の印を引き継ぐ' ($zone -eq $expectedMark) $zone
}

$tabs = @(Get-Tabs $app | ForEach-Object { $_.Current.Name })
Check '作った書庫をタブで開く' (@($tabs | Where-Object { $_ -match '切れた \(修復\)\.zip' }).Count -eq 1) ($tabs -join ', ')
Check 'ステータスバー' ((Get-Status $app) -eq '無事なファイル 2 個で 切れた (修復).zip を作成しました') (Get-Status $app)
Stop-Expzip $app

Section '断ったとき'
$app = Start-Expzip @($cut)
$box = Find-FrontBox $app
if ($box) { Close-MessageBox $box 'いいえ(N)' }
Wait-Idle $app
Check '何も作らない' (-not (Test-Path -LiteralPath (Join-Path $script:Work '切れた (修復 2).zip')))
Check 'ほかに尋ねない' ($null -eq (Find-MessageBox $app 1500))
Stop-Expzip $app

Section '一覧の途中で切れた書庫'
# ファイルはすべて無事。失ったものは無いので、そのことは書かない
$listCut = New-CutZip '一覧.zip' ($filesEnd + 10)
$app = Start-Expzip @($listCut)
$box = Find-FrontBox $app
if ($box) { Close-MessageBox $box 'はい(Y)' }
$box = Find-FrontBox $app
Check '結果を知らせる' ($null -ne $box)
if ($box) {
    Check 'すべて取り戻す' ($box.Text -eq '無事なファイル 3 個で 一覧 (修復).zip を作成しました。') $box.Text
    Close-MessageBox $box 'OK'
}
Wait-Idle $app
$names = Get-ZipNames (Join-Path $script:Work '一覧 (修復).zip')
Check '中身がすべて入る' ($names.Count -eq 4) ($names -join ', ')
Stop-Expzip $app

Section '書庫でないもの'
# ZIP の頭で始まらないものには提案しない
$text = Join-Path $script:Work 'ただの文字.zip'
Set-Content -Path $text -Value 'これは書庫ではありません' -Encoding UTF8
$app = Start-Expzip @($text)
$box = Find-FrontBox $app
Check '断りだけ' (($null -ne $box) -and ($box.Buttons -contains 'OK') -and -not ($box.Buttons -contains 'はい(Y)')) ($box.Buttons -join ', ')
Check '書き直しは持ち掛けない' ($box.Text -notmatch '新しい書庫にしますか') $box.Text
if ($box) { Close-MessageBox $box 'OK' }
Stop-Expzip $app

Complete-Suite
