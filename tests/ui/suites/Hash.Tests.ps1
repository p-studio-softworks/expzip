# ハッシュ値を出して、配布元の値と照らし合わせる (#259)
. "$PSScriptRoot\..\Common.ps1"
Start-Suite 'Hash'
Set-Settings

# 「abc」のハッシュ値はよく知られた値なので、計算が正しいかをそのまま確かめられる
$abc = @{
    'SHA-256' = 'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad'
    'SHA-1'   = 'a9993e364706816aba3e25717850c26c9cd0d89d'
    'MD5'     = '900150983cd24fb0d6963f7d28e17f72'
    'CRC32'   = '352441C2'
}
$sample = New-TestZip (Join-Path $script:Work 'hash.zip') ([ordered]@{
    'abc.txt'      = 'abc'
    'docs/one.txt' = 'one'
    'docs/two.txt' = 'two'
})

# 右クリックのメニューから押す。押せなかったら $null
function Open-HashWindow($App, [string]$Row, [string]$Item) {
    $menu = Open-RowMenu $App $Row
    $entry = if ($menu) { ByName $menu $Item }
    if ($null -eq $entry -or -not $entry.Current.IsEnabled) {
        Close-DropDown $App
        return $null
    }
    Push $entry
    $window = Find-Window $App 'ハッシュ値 - hash.zip' 30000
    if ($window) {
        Wait-Until { (ById $window 'Headline').Current.Name -match 'ハッシュ値を計算しました$' } 30000 | Out-Null
    }
    return $window
}

function Get-HashRows($Window) {
    $list = ById $Window 'HashList'
    return @($list.FindAll($script:Scope::Children,
        (Condition $script:Automation::ControlTypeProperty $script:ControlType::DataItem)) |
        ForEach-Object { $_.Current.Name })
}

$app = Start-Expzip @($sample)

Section '選んだファイル'
$window = Open-HashWindow $app 'abc.txt' 'ハッシュ値を計算'
Check '窓が開く' ($null -ne $window)
if ($window) {
    Check '計算し終わる' ((ById $window 'Headline').Current.Name -eq '1 個のファイルのハッシュ値を計算しました') (ById $window 'Headline').Current.Name
    $rows = Get-HashRows $window
    Check '4 種類並ぶ' ($rows.Count -eq 4) ($rows -join ' / ')
    foreach ($kind in $abc.Keys) {
        Check "値: $kind" ($rows -contains "abc.txt、$($kind): $($abc[$kind])") ($rows -join ' / ')
    }

    # sha256sum の出力をそのまま貼る。大文字でも一致とみなす
    $box = ById $window 'CompareBox'
    Set-Text $box ($abc['SHA-256'].ToUpperInvariant() + ' *abc.txt')
    $result = (ById $window 'CompareResult').Current.Name
    Check '貼り付けた値と一致する' ($result -eq '一致しました: abc.txt の SHA-256') $result

    # 一致した行が選ばれ、写せる
    $copy = ById $window 'CopyButton'
    Check '一致した行を写せる' ($copy.Current.IsEnabled)
    Push $copy
    $clip = [System.Windows.Forms.Clipboard]::GetText()
    Check '写したのは値だけ' ($clip -eq $abc['SHA-256']) $clip

    Set-Text $box 'a9993e36 4706816a ba3e2571 7850c26c 9cd0d89d'
    $result = (ById $window 'CompareResult').Current.Name
    Check '空白で区切った値も読む' ($result -eq '一致しました: abc.txt の SHA-1') $result

    Set-Text $box '0000000000000000000000000000000000000000000000000000000000000000'
    $result = (ById $window 'CompareResult').Current.Name
    Check '違う値は一致しない' ($result -eq '一致するものはありません') $result

    Set-Text $box 'ダウンロード'
    $result = (ById $window 'CompareResult').Current.Name
    Check '値でないものは断る' ($result -eq '16 進の値として読めません') $result

    Push (ById $window 'CloseButton')
}
Check '窓が閉じる' (Test-WindowGone $app 'ハッシュ値 - hash.zip')

Section 'フォルダーを選ぶと中のファイルすべて'
$window = Open-HashWindow $app 'docs' 'ハッシュ値を計算'
Check '窓が開く' ($null -ne $window)
if ($window) {
    $rows = Get-HashRows $window
    Check '2 個 × 4 種類' ($rows.Count -eq 8) ($rows -join ' / ')
    Check '書庫の中のパスで出る' (@($rows | Where-Object { $_ -like 'docs/one.txt、*' }).Count -eq 4) ($rows -join ' / ')
    Push (ById $window 'CloseButton')
}

Section '書庫ファイルそのもの'
$window = Open-HashWindow $app 'abc.txt' '書庫ファイルのハッシュ値を計算'
Check '窓が開く' ($null -ne $window)
if ($window) {
    $expected = (Get-FileHash -Algorithm SHA256 $sample).Hash.ToLowerInvariant()
    $rows = Get-HashRows $window
    Check '書庫ファイルの値' ($rows -contains "hash.zip、SHA-256: $expected") ($rows -join ' / ')
    Push (ById $window 'CloseButton')
}

Section '取り出したものを残さない'
$left = @(Get-ChildItem (Join-Path $env:TEMP 'Expzip') -Recurse -Directory -Filter '*-hash' -ErrorAction SilentlyContinue)
Check '一時フォルダーが消えている' ($left.Count -eq 0) (($left | ForEach-Object { $_.FullName }) -join ', ')

Stop-Expzip $app
Complete-Suite
