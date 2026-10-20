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
function Open-HashWindow($App, [string]$Row, [string]$Item, [string]$Archive = 'hash.zip') {
    $menu = Open-RowMenu $App $Row
    $entry = if ($menu) { ByName $menu $Item }
    if ($null -eq $entry -or -not $entry.Current.IsEnabled) {
        Close-DropDown $App
        return $null
    }
    Push $entry
    $window = Find-Window $App "ハッシュ値 - $Archive" 30000
    if ($window) {
        Wait-Until { (ById $window 'Headline').Current.Name -match 'ハッシュ値を計算しました$' } 30000 | Out-Null
    }
    return $window
}

function Get-HashRows($Window, [string]$Id = 'HashList') {
    $list = ById $Window $Id
    return @($list.FindAll($script:Scope::Children,
        (Condition $script:Automation::ControlTypeProperty $script:ControlType::DataItem)) |
        ForEach-Object { $_.Current.Name })
}

$app = Start-Expzip @($sample)
$archiveSha256 = (Get-FileHash -Algorithm SHA256 $sample).Hash.ToLowerInvariant()

Section '右クリックの項目は 1 つ (#283)'
$menu = Open-RowMenu $app 'abc.txt'
Check '書庫ファイル用の項目は無い' ($null -eq (ByName $menu '書庫ファイルのハッシュ値を計算'))
Close-DropDown $app

Section '選んだファイルと書庫ファイル'
$window = Open-HashWindow $app 'abc.txt' 'ハッシュ値を計算'
Check '窓が開く' ($null -ne $window)
if ($window) {
    # 数えるのは選んだファイルだけ。書庫ファイルは数えない
    Check '計算し終わる' ((ById $window 'Headline').Current.Name -eq '1 個のファイルのハッシュ値を計算しました') (ById $window 'Headline').Current.Name
    $rows = Get-HashRows $window
    Check '選んだファイルの 4 種類だけ並ぶ' ($rows.Count -eq 4) ($rows -join ' / ')
    foreach ($kind in $abc.Keys) {
        Check "値: $kind" ($rows -contains "abc.txt、$($kind): $($abc[$kind])") ($rows -join ' / ')
    }
    # 書庫ファイルは別の欄に出る
    $heading = (ById $window 'ArchiveHeading').Current.Name
    Check '書庫ファイルの欄の見出し' ($heading -eq '書庫ファイル: hash.zip') $heading
    $archiveRows = Get-HashRows $window 'ArchiveList'
    Check '書庫ファイルの欄に 4 種類' ($archiveRows.Count -eq 4) ($archiveRows -join ' / ')
    Check '書庫ファイルの値' ($archiveRows -contains "hash.zip (書庫ファイル)、SHA-256: $archiveSha256") ($archiveRows -join ' / ')
    # 欄の分かれ方を目で確かめるための写し
    Save-Shot @{ Window = $window } (Join-Path $script:Work 'hash.png')

    # 書庫ファイルの値も照らし合わせる
    Set-Text (ById $window 'CompareBox') $archiveSha256
    $result = (ById $window 'CompareResult').Current.Name
    Check '書庫ファイルの値とも一致する' ($result -eq '一致しました: hash.zip (書庫ファイル) の SHA-256') $result
    $copy = ById $window 'CopyButton'
    Push $copy
    $clip = [System.Windows.Forms.Clipboard]::GetText()
    Check '書庫ファイルの値を写せる' ($clip -eq $archiveSha256) $clip

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

    # 値の隣に 16 進の文字だけでできた単語があっても読む (#264)
    Set-Text $box ('BAD ' + $abc['SHA-256'])
    $result = (ById $window 'CompareResult').Current.Name
    Check '前に 16 進だけの単語があっても読む' ($result -eq '一致しました: abc.txt の SHA-256') $result

    Set-Text $box ($abc['SHA-256'] + ' be')
    $result = (ById $window 'CompareResult').Current.Name
    Check '後ろに 16 進だけの単語があっても読む' ($result -eq '一致しました: abc.txt の SHA-256') $result

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
    # 取り出さずに、中身を読みながら計算する (#265)
    $made = @(Get-ChildItem (Join-Path $env:TEMP 'Expzip') -Recurse -Directory -Filter '*-hash' -ErrorAction SilentlyContinue)
    Check '一時ファイルの置き場を使わない' ($made.Count -eq 0) (($made | ForEach-Object { $_.FullName }) -join ', ')

    $rows = Get-HashRows $window
    Check '2 個 × 4 種類' ($rows.Count -eq 8) ($rows -join ' / ')
    Check '書庫の中のパスで出る' (@($rows | Where-Object { $_ -like 'docs/one.txt、*' }).Count -eq 4) ($rows -join ' / ')
    Push (ById $window 'CloseButton')
}

Section '何も選んでいなければ書庫ファイルだけ'
$row = Select-Row $app 'abc.txt'
$row.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).RemoveFromSelection()
Start-Sleep -Milliseconds 300
Send-Keys $app '+{F10}'
$menu = Find-DropDown $app
$entry = if ($menu) { ByName $menu 'ハッシュ値を計算' }
Check '押せる' ($null -ne $entry -and $entry.Current.IsEnabled)
$window = $null
if ($entry -and $entry.Current.IsEnabled) {
    Push $entry
    $window = Find-Window $app 'ハッシュ値 - hash.zip' 30000
    if ($window) {
        Wait-Until { (ById $window 'Headline').Current.Name -match 'ハッシュ値を計算しました$' } 30000 | Out-Null
    }
} else {
    Close-DropDown $app
}
Check '窓が開く' ($null -ne $window)
if ($window) {
    $headline = (ById $window 'Headline').Current.Name
    Check '書庫ファイルだけを計算した' ($headline -eq '書庫ファイルのハッシュ値を計算しました') $headline
    $list = ById $window 'HashList'
    Check '選んだファイルの一覧は出さない' ($null -eq $list -or $list.Current.IsOffscreen)
    $rows = Get-HashRows $window 'ArchiveList'
    Check '書庫ファイルの 4 種類' ($rows.Count -eq 4) ($rows -join ' / ')
    Save-Shot @{ Window = $window } (Join-Path $script:Work 'hash-archive.png')
    Check '書庫ファイルの値' ($rows -contains "hash.zip (書庫ファイル)、SHA-256: $archiveSha256") ($rows -join ' / ')
    Push (ById $window 'CloseButton')
}

Section '一時ファイルの置き場に何も残さない'
$left = @(Get-ChildItem (Join-Path $env:TEMP 'Expzip') -Recurse -Directory -Filter '*-hash' -ErrorAction SilentlyContinue)
Check '一時フォルダーが消えている' ($left.Count -eq 0) (($left | ForEach-Object { $_.FullName }) -join ', ')

Stop-Expzip $app

Section 'フォルダーの属性が付いたファイル'
# 名前は / で終わらず中身もあるのに、属性だけがフォルダーを指す。一覧に出るファイルは
# すべて計算する (#266)
$attr = New-TestZip (Join-Path $script:Work 'zokusei.zip') ([ordered]@{
    'bin/readme.txt' = 'readme'
    'bin/setup.exe'  = 'abc'
})
Set-DosDirectoryAttribute $attr 'bin/setup.exe'
$app = Start-Expzip @($attr)
$window = Open-HashWindow $app 'bin' 'ハッシュ値を計算' 'zokusei.zip'
Check '窓が開く' ($null -ne $window)
if ($window) {
    $rows = Get-HashRows $window
    Check '2 個 × 4 種類' ($rows.Count -eq 8) ($rows -join ' / ')
    Check '属性の付いたファイルも出る' ($rows -contains "bin/setup.exe、SHA-256: $($abc['SHA-256'])") ($rows -join ' / ')
    Push (ById $window 'CloseButton')
}
Stop-Expzip $app

Complete-Suite
