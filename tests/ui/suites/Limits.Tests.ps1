# 開けないほど大きな書庫を、落ちずに断る (#294)
. "$PSScriptRoot\..\Common.ps1"
Start-Suite 'Limits'
Set-Settings

$fixtures = Join-Path $PSScriptRoot '..\fixtures'

# 階層の深さだけが違う ZIP を作る。中身は 1 個で、書庫の大きさは名前の分だけ
function New-DeepZip([string]$Name, [int]$Depth) {
    $path = Join-Path $script:Work $Name
    Add-Type -AssemblyName System.IO.Compression
    $stream = [System.IO.File]::Create($path)
    $zip = New-Object System.IO.Compression.ZipArchive($stream, [System.IO.Compression.ZipArchiveMode]::Create)
    $entry = $zip.CreateEntry((('a/' * $Depth) + 'x.txt'))
    $writer = New-Object System.IO.StreamWriter($entry.Open())
    $writer.Write('deep')
    $writer.Dispose()
    $zip.Dispose()
    return $path
}

# 落として開く。知らせが出れば理由を返す
function Open-Refused($App, [string]$Path) {
    Focus-App $App
    $center = Get-WindowCenter $App
    Invoke-Drop @($Path) $center.X $center.Y | Out-Null
    $box = Find-MessageBox $App
    if ($null -eq $box) {
        return $null
    }

    $text = $box.Text
    Close-MessageBox $box 'OK'
    return $text
}

function Use-Fixture([string]$Name) {
    $path = Join-Path $script:Work $Name
    Copy-Item (Join-Path $fixtures $Name) $path
    return $path
}

$first = New-TestZip (Join-Path $script:Work '先に開く.zip') ([ordered]@{ 'memo.txt' = 'hello' })
$app = Start-Expzip @($first)

Section '階層が深い書庫'
# 3 万段の名前でも ZIP は 100 KB ほど。以前はスタックが溢れて、受け止められずに落ちていた
$text = Open-Refused $app (New-DeepZip '3万段.zip' 30000)
Check '落ちない' (-not $app.Process.HasExited)
Check '階層が深すぎると出す' ($text -match '3万段\.zip を開けませんでした。' -and $text -match '階層が深すぎます。$') $text

$text = Open-Refused $app (New-DeepZip '65段.zip' 65)
Check '上限を超えると断る' ($text -match '階層が深すぎます。$') $text

Section '数だけを大きく書いた 7z'
# 数十バイトで 1 億個と書いたもの。以前は作りかけの部品で数 GB を使っていた。
# どれも 1 億と書いてあり、読み方の差でこちらの確認だけをすり抜けようとする形を含む (ID を 2 バイトで書く、知らない属性)
foreach ($name in 'limit_files.7z', 'limit_files_encoded.7z', 'limit_long_id.7z', 'limit_unknown_attr.7z', 'limit_coders.7z') {
    $text = Open-Refused $app (Use-Fixture $name)
    Check "項目が多すぎると出す: $name" ($text -match '項目が多すぎます。$') $text
}

Section '読めない見出しの 7z'
# 読めないものを SharpCompress に任せると、確認をすり抜けられる。読めなければ断る。
# 以前は桁あふれの例外がそのまま上がり、「処理中に問題が発生しました」とライブラリの英語の文が出ていた
foreach ($name in 'bad_header_size.7z', 'bad_long_props.7z', 'bad_twice.7z', 'odd_header.7z', 'bad_unused_aes.7z',
    'limit_streams.7z', 'bad_no_inputs.7z', 'bad_folder_crc_twice.7z', 'bad_repeated_times.7z',
    'bad_old_short.7z', 'bad_old_long.7z', 'bad_old_zero.7z', 'bad_old_times.7z', 'bad_old_encoded.7z') {
    $text = Open-Refused $app (Use-Fixture $name)
    Check "データが壊れていると出す: $name" ($text -match '書庫のデータが壊れています。$') $text
}

Section '途中で切れた LZMA2'
# SharpCompress は入力の終わりに気付かず、読み込み中のまま終わらなくなっていた
foreach ($name in 'tail_header.7z', 'tail_header_more.7z', 'tail.xz', 'tail.tar.xz', 'xz_named.7z') {
    $text = Open-Refused $app (Use-Fixture $name)
    Check "止まらずに断る: $name" ($text -match '書庫のデータが壊れています。$') $text
}

Section 'ほかのタブ'
Check '先に開いた書庫のタブは残る' ((Get-Tabs $app).Count -eq 1) ((Get-Tabs $app).Count)
Check '一覧もそのまま' ((Get-RowNames $app) -contains 'memo.txt') ((Get-RowNames $app) -join ', ')

Section '上限までは開ける'
$center = Get-WindowCenter $app
Invoke-Drop @((New-DeepZip '64段.zip' 64)) $center.X $center.Y | Out-Null
Wait-Until { (Get-Tabs $app).Count -eq 2 } | Out-Null
Check '上限の深さの書庫を開く' ((Get-Tabs $app).Count -eq 2) ((Get-Tabs $app).Count)
Check '一覧に一段目が出る' ((Get-RowNames $app) -contains 'a') ((Get-RowNames $app) -join ', ')

# 書庫をたどる処理は再帰なので、上限の深さでも溢れないことを確かめる
Push (ById $app.Window 'InspectButton')
Wait-Idle $app 60000
$window = Find-Window $app '検査結果 - 64段.zip' 30000
Check '上限の深さの書庫を検査できる' ($null -ne $window)
if ($window) { Push (ById $window 'CloseButton') }
Check '検査のあとも落ちない' (-not $app.Process.HasExited)

# ツリーの表示は入れ子のレイアウトが再帰になる。上限はここで溢れないように決めた
$item = @(ByType (ById $app.Window 'FolderTree') $script:ControlType::TreeItem)[0]
$opened = 0
while ($null -ne $item) {
    $pattern = $item.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    if ($pattern.Current.ExpandCollapseState -eq [System.Windows.Automation.ExpandCollapseState]::LeafNode) { break }
    $pattern.Expand()
    Start-Sleep -Milliseconds 100
    $children = @($item.FindAll($script:Scope::Children,
        (Condition $script:Automation::ControlTypeProperty $script:ControlType::TreeItem)))
    if ($children.Count -eq 0) { break }
    $item = $children[0]
    $opened++
}
Check 'ツリーを 1 段ずつ一番下まで開ける' ($opened -eq 64) $opened
Check '開いたあとも落ちない' (-not $app.Process.HasExited)

# いちばん下のフォルダーへ移ると、祖先がまとめて開く形になる。タブを切り替えると作り直す
Select-Element $item
Start-Sleep -Milliseconds 400
Send-Keys $app '^{TAB}'
Start-Sleep -Milliseconds 500
Send-Keys $app '^+{TAB}'
Start-Sleep -Milliseconds 500
Check 'タブを切り替えて作り直しても落ちない' (-not $app.Process.HasExited)

Section '上限を超える編集は行わない'
# 一番下のフォルダーの中に作ると 65 段になり、保存はできても開き直せない書庫になる
$deep = Join-Path $script:Work '64段.zip'
$before = (Get-FileHash $deep).Hash
Focus-App $app
Select-Element $item
Start-Sleep -Milliseconds 400
Send-Keys $app '^+n'
$box = Find-MessageBox $app
Check '階層が深すぎると出す' ($box -and $box.Text -match '階層が深すぎます。$') $(if ($box) { $box.Text })
if ($box) { Close-MessageBox $box 'OK' }
Wait-Idle $app
Check '書庫は変わらない' ((Get-FileHash $deep).Hash -eq $before)

Section '正しい書庫'

# 7z は見出しの圧縮が 2 通りある。7-Zip は LZMA、ほかのツールには LZMA2 のものがある
$center = Get-WindowCenter $app
Invoke-Drop @((Use-Fixture 'ok.7z')) $center.X $center.Y | Out-Null
Wait-Until { (Get-Tabs $app).Count -eq 3 } | Out-Null
Check 'LZMA の見出しの 7z を開く' ((Get-Tabs $app).Count -eq 3) ((Get-Tabs $app).Count)

$center = Get-WindowCenter $app
Invoke-Drop @((Use-Fixture 'lzma2_header.7z')) $center.X $center.Y | Out-Null
Wait-Until { (Get-Tabs $app).Count -eq 4 } | Out-Null
Check 'LZMA2 の見出しの 7z を開く' ((Get-Tabs $app).Count -eq 4) ((Get-Tabs $app).Count)

$center = Get-WindowCenter $app
Invoke-Drop @((Use-Fixture 'ok_plain_header.7z')) $center.X $center.Y | Out-Null
Wait-Until { (Get-Tabs $app).Count -eq 5 } | Out-Null
Check '圧縮しない見出しの 7z を開く' ((Get-Tabs $app).Count -eq 5) ((Get-Tabs $app).Count)

# 0.2 までのバージョンの番号でも、中身が正しければ開く
$center = Get-WindowCenter $app
Invoke-Drop @((Use-Fixture 'ok_old_version.7z')) $center.X $center.Y | Out-Null
Wait-Until { (Get-Tabs $app).Count -eq 6 } | Out-Null
Check '古いバージョンの番号の 7z を開く' ((Get-Tabs $app).Count -eq 6) ((Get-Tabs $app).Count)

Section '中身が途中で切れた 7z'
# 一覧は開ける。中身を読むと、以前は止まらなくなっていた
$center = Get-WindowCenter $app
Invoke-Drop @((Use-Fixture 'tail_body.7z')) $center.X $center.Y | Out-Null
Wait-Until { (Get-Tabs $app).Count -eq 7 } | Out-Null
Check '一覧は開く' ((Get-Tabs $app).Count -eq 7) ((Get-Tabs $app).Count)
Push (ById $app.Window 'InspectButton')
Wait-Idle $app 15000
$cancel = ById $app.Window 'CancelButton'
Check '検査が止まらずに終わる' (($null -eq $cancel) -or $cancel.Current.IsOffscreen)
$window = Find-Window $app '検査結果 - tail_body.7z' 5000
if ($window) { Push (ById $window 'CloseButton') }
$box = Find-MessageBox $app 1000
if ($box) { Close-MessageBox $box 'OK' }
Stop-Expzip $app

Section '読み取りが止まったままでも閉じられる'
# 確認ではたどれない形の 7z。検査を始めると、ライブラリの中で止まったまま戻らない
$hang = Use-Fixture 'hang_behind_copy.7z'
$app = Start-Expzip @($hang)
Push (ById $app.Window 'InspectButton') 2000
$cancel = ById $app.Window 'CancelButton'
Check '検査が止まっている' ($cancel -and -not $cancel.Current.IsOffscreen)
Check '止まっている間もウィンドウは応答する' $app.Process.Responding
$app.Process.CloseMainWindow() | Out-Null
Check '閉じる操作で終了する' ($app.Process.WaitForExit(15000))
if (-not $app.Process.HasExited) { $app.Process.Kill() }

Section '未保存の編集があれば先に確かめる'
$outer = New-TestZip (Join-Path $script:Work '外側.zip') ([ordered]@{ '内側.zip' = 'x' })
$inner = Join-Path $script:Work '内側の元.zip'
New-TestZip $inner ([ordered]@{ '消すメモ.txt' = 'a'; '残すメモ.txt' = 'b' }) | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::Open($outer, 'Update')
$zip.GetEntry('内側.zip').Delete()
[IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $inner, '内側.zip') | Out-Null
$zip.Dispose()
$before = (Get-FileHash $outer).Hash

$app = Start-Expzip @($hang)
$center = Get-WindowCenter $app
Invoke-Drop @($outer) $center.X $center.Y | Out-Null
Wait-Until { (Get-Tabs $app).Count -eq 2 } | Out-Null
Select-Row $app '内側.zip' | Out-Null
Send-Keys $app '{ENTER}'
Wait-Idle $app
Select-Row $app '消すメモ.txt' | Out-Null
Send-Keys $app '{DEL}'
$box = Find-MessageBox $app
if ($box) { Close-MessageBox $box 'はい(Y)' }
Wait-Idle $app

Select-Element (Get-Tabs $app)[0]
Start-Sleep -Milliseconds 500
Push (ById $app.Window 'InspectButton') 2000
$app.Process.CloseMainWindow() | Out-Null
$box = Find-MessageBox $app 15000
Check '破棄して閉じるかを尋ねる' ($box -and $box.Text -match '変更を破棄して終了しますか') $(if ($box) { $box.Text })
Check '止まっているので保存は選べない' ($box -and -not ($box.Buttons -contains 'キャンセル')) $(if ($box) { $box.Buttons -join ', ' })
if ($box) { Close-MessageBox $box 'いいえ(N)' }
Start-Sleep -Milliseconds 800
Check '「いいえ」なら終了しない' (-not $app.Process.HasExited)

$app.Process.CloseMainWindow() | Out-Null
$box = Find-MessageBox $app 15000
if ($box) { Close-MessageBox $box 'はい(Y)' }
Check '「はい」なら終了する' ($app.Process.WaitForExit(10000))
if (-not $app.Process.HasExited) { $app.Process.Kill() }
Check '親の書庫は変わらない' ((Get-FileHash $outer).Hash -eq $before)

Complete-Suite
