# 書庫の中を見て回る: ツリー、場所の区切り (#90)、キー操作 (#12, #46)、タブ (#22)、中の書庫 (#30)
. "$PSScriptRoot\..\Common.ps1"
Start-Suite 'Browse'
Set-Settings

$innerPath = New-TestZip (Join-Path $script:Work 'inner-src.zip') ([ordered]@{
    '内側メモ.txt'   = 'memo'
    '資料/'          = ''
    '資料/表.csv'    = 'a,b'
})
$archive = New-TestZip (Join-Path $script:Work '外側.zip') ([ordered]@{
    '資料/'              = ''
    '資料/2024/'         = ''
    '資料/2024/報告.txt' = 'report'
    '資料/空/'           = ''
    'readme.txt'         = 'readme'
    '内側.zip'           = [System.IO.File]::ReadAllBytes($innerPath)
})

function Crumbs($App) {
    return @(ByType (ById $App.Window 'Crumbs') $script:ControlType::Button |
        ForEach-Object { $_.Current.Name } | Where-Object { $_ -notmatch ' の中$' }) -join ' > '
}

$app = Start-Expzip @($archive)

Section '開いた直後'
Check 'ルートの中身が並ぶ' (((Get-RowNames $app) -join ', ') -eq '資料, readme.txt, 内側.zip') ((Get-RowNames $app) -join ', ')
Check '場所の区切りは書庫の名前だけ' ((Crumbs $app) -eq '外側.zip') (Crumbs $app)
# 支援技術が読む行の名前。入れていないと行の型の名前が読まれる (#109)
$rowNames = @(Get-Rows $app | ForEach-Object { $_.Current.Name })
Check '行の名前は項目の名前' (($rowNames -join ', ') -eq '資料, readme.txt, 内側.zip') ($rowNames -join ', ')
Check '件数' ((Texts $app.Window) -match '3 個のファイル')

Section '区切りの一覧'
# 区切り (›) を押すと、その場所の中のフォルダーが並ぶ (#90)
$inside = ByType (ById $app.Window 'Crumbs') $script:ControlType::Button |
    Where-Object { $_.Current.Name -match ' の中$' } | Select-Object -First 1
Focus-App $app
Push $inside 900
$menu = Find-DropDown $app
$items = @(if ($menu) { ByType $menu $script:ControlType::MenuItem })
Check '中のフォルダーが並ぶ' ((($items | ForEach-Object { $_.Current.Name }) -join ', ') -eq '資料') (($items | ForEach-Object { $_.Current.Name }) -join ', ')
# 名前が読める字で描かれている (#144)。区切りのボタンは絵の書体なので、それを受け継ぐと
# 名前がすべて □ になる。項目の名前 (支援技術が読むもの) は正しいままなので、描かれたものを見る。
# 一覧の行に同じ名前があるので、描かれた字の幅と本来の幅の比を、行のものと比べる
if ($items.Count -gt 0) {
    $itemText = ByType $items[0] $script:ControlType::Text | Select-Object -First 1
    $itemRect = if ($itemText) { $itemText.Current.BoundingRectangle } else { $items[0].Current.BoundingRectangle }
    $rowRect = (ByName (Find-Row $app '資料') '資料').Current.BoundingRectangle
    $menuInk = Measure-InkWidth $itemRect
    $rowInk = Measure-InkWidth $rowRect
    Check '一覧の名前が読める字で描かれる' ($rowInk -gt 0 -and [Math]::Abs($menuInk / $rowInk - 1) -lt 0.15) "一覧 $menuInk px / 行 $rowInk px"
}
Close-DropDown $app

Section 'Enter でフォルダーに入る'
Select-Row $app '資料' | Out-Null
Send-Keys $app '{ENTER}'
Check '中身が並ぶ' (((Get-RowNames $app) -join ', ') -eq '2024, 空') ((Get-RowNames $app) -join ', ')
Check '区切りが伸びる' ((Crumbs $app) -eq '外側.zip > 資料') (Crumbs $app)

Select-Row $app '空' | Out-Null
Send-Keys $app '{ENTER}'
Check '空のフォルダーはそう言う' ((Texts $app.Window) -match 'このフォルダーは空です')

Section 'Backspace で戻る'
$list = ById $app.Window 'EntryList'
$list.SetFocus()
Send-Keys $app '{BACKSPACE}'
Check '1 つ上に戻る' ((Crumbs $app) -eq '外側.zip > 資料') (Crumbs $app)
Select-Row $app '2024' | Out-Null
Send-Keys $app '{BACKSPACE}'
Send-Keys $app '{BACKSPACE}'
Check 'ルートまで戻る' ((Crumbs $app) -eq '外側.zip') (Crumbs $app)

Section 'ツリー'
$tree = ById $app.Window 'FolderTree'
$root = ByType $tree $script:ControlType::TreeItem | Select-Object -First 1
Check 'ルートは書庫の名前' ($root.Current.Name -match '外側\.zip' -or (Texts $root) -match '外側\.zip') (Texts $root)
Expand-Element $root
$folder = ByType $tree $script:ControlType::TreeItem | Where-Object { $null -ne (ByName $_ '資料') } | Select-Object -Last 1
Check 'フォルダーが並ぶ' ($null -ne $folder)
if ($folder) {
    Select-Element $folder
    Start-Sleep -Milliseconds 600
    Check '選ぶと一覧が移る' ((Crumbs $app) -eq '外側.zip > 資料') (Crumbs $app)
}
Select-Element $root
Start-Sleep -Milliseconds 600

Section '中の書庫を開く'
Select-Row $app '内側.zip' | Out-Null
Send-Keys $app '{ENTER}'
Wait-Idle $app
$tabs = Get-Tabs $app
Check '新しいタブで開く' ($tabs.Count -eq 2) (($tabs | ForEach-Object { $_.Current.Name }) -join ', ')
Check 'どこの中かを知らせる' ((Get-Status $app) -eq '外側.zip 内の 内側.zip を新しいタブで開きました') (Get-Status $app)
$save = ById $app.Window 'SaveButton'
Check '保存の口が出る' ($save -and -not $save.Current.IsOffscreen -and $save.Current.IsEnabled)
Check '保存の口の名前' ($save.Current.Name -eq '保存') $save.Current.Name
Check '中身が並ぶ' (((Get-RowNames $app) -join ', ') -eq '資料, 内側メモ.txt') ((Get-RowNames $app) -join ', ')

Select-Row $app '内側メモ.txt' | Out-Null
Send-Keys $app '{DEL}'
$box = Find-MessageBox $app
if ($box) { Close-MessageBox $box 'はい(Y)' }
Wait-Idle $app
Check '保存するまでは親はそのまま' ((Get-InnerZipNames $archive '内側.zip') -contains '内側メモ.txt')

Send-Keys $app '^s'
Wait-Idle $app
Check '反映したと知らせる' ((Get-Status $app) -eq '内側.zip を 外側.zip に反映しました') (Get-Status $app)
$inner = Get-InnerZipNames $archive '内側.zip'
Check '親の書庫の中から消える' (-not ($inner -contains '内側メモ.txt')) ($inner -join ', ')
Check 'ほかの中身は残る' ($inner -contains '資料/表.csv')

Send-Keys $app '^s'
Check '変更が無ければそう言う' ((Get-Status $app) -eq '変更されていないため、反映する内容はありません') (Get-Status $app)

Section 'タブを閉じる'
Send-Keys $app '^w'
Wait-Idle $app
Check 'タブが 1 つに戻る' ((Get-Tabs $app).Count -eq 1)
$save = ById $app.Window 'SaveButton'
Check '保存の口が消える' (($null -eq $save) -or $save.Current.IsOffscreen)
Stop-Expzip $app

Section '開けなかったとき'
$broken = New-TestZip (Join-Path $script:Work '壊れ.zip') ([ordered]@{
    'memo.txt'    = ('hello ' * 2000)
    'wavpack.txt' = 'wavpack'
    'stored.txt'  = ('stored ' * 200)
}) @('stored.txt')
# 先頭の項目の中身を壊す。中身はヘッダー (30 バイト) と名前、拡張フィールドの後ろから始まる
$bytes = [System.IO.File]::ReadAllBytes($broken)
$start = 30 + [BitConverter]::ToUInt16($bytes, 26) + [BitConverter]::ToUInt16($bytes, 28)
foreach ($i in ($start + 3)..($start + 23)) { $bytes[$i] = $bytes[$i] -bxor 0x5A }
# 2 つ目の項目は、圧縮方式をどのライブラリも知らない番号 (97 = WavPack) にする。
# 各項目の見出し (PK 3 4) と一覧 (PK 1 2) の両方に方式が書いてある
for ($i = 0; $i -lt $bytes.Length - 46; $i++) {
    if ($bytes[$i] -ne 0x50 -or $bytes[$i + 1] -ne 0x4B) { continue }
    if ($bytes[$i + 2] -eq 3 -and $bytes[$i + 3] -eq 4) { $method, $nameAt, $lengthAt = 8, 30, 26 }
    elseif ($bytes[$i + 2] -eq 1 -and $bytes[$i + 3] -eq 2) { $method, $nameAt, $lengthAt = 10, 46, 28 }
    else { continue }
    $name = [System.Text.Encoding]::UTF8.GetString($bytes, $i + $nameAt, [BitConverter]::ToUInt16($bytes, $i + $lengthAt))
    if ($name -eq 'wavpack.txt') { $bytes[$i + $method] = 97; $bytes[$i + $method + 1] = 0 }
    # 3 つ目の項目は格納 (圧縮しない)。中身を 1 バイト変えても最後まで読めてしまい、
    # CRC を照合しないと気づけない (#168)
    if ($name -eq 'stored.txt' -and $nameAt -eq 30) {
        $data = $i + 30 + [BitConverter]::ToUInt16($bytes, $i + 26) + [BitConverter]::ToUInt16($bytes, $i + 28)
        $bytes[$data + 100] = $bytes[$data + 100] -bxor 0x01
    }
}
[System.IO.File]::WriteAllBytes($broken, $bytes)

$app = Start-Expzip @($broken)
$idle = Get-Status $app
Select-Row $app 'memo.txt' | Out-Null
Send-Keys $app '{ENTER}'
$box = Find-MessageBox $app
Check '知らせが出る' ($null -ne $box)
if ($box) {
    Check '文言' ($box.Text -match '^memo\.txt を開けませんでした。') $box.Text
    # 圧縮方式は対応しているもの。「対応していない圧縮方式」と取り違えない (#167)
    Check '理由はデータが壊れていること' ($box.Text -match '書庫のデータが壊れています。$') $box.Text
    Close-MessageBox $box 'OK'
}
# 「展開しています…」を残すと、まだ作業中に見える (#157)
Check 'ステータスバーは書庫の説明に戻る' ((Get-Status $app) -eq $idle) (Get-Status $app)

# 本当に知らない方式なら、今どおり方式のせいだと言う (#66)
Select-Row $app 'wavpack.txt' | Out-Null
Send-Keys $app '{ENTER}'
$box = Find-MessageBox $app
Check '知らない方式でも知らせが出る' ($null -ne $box)
if ($box) {
    Check '理由は圧縮方式' ($box.Text -match 'この圧縮方式には対応していません$') $box.Text
    Close-MessageBox $box 'OK'
}

# 格納の項目は、読めても中身が違う。黙って開かない (#168)
Select-Row $app 'stored.txt' | Out-Null
Send-Keys $app '{ENTER}'
$box = Find-MessageBox $app
Check '格納の項目が壊れていても知らせが出る' ($null -ne $box)
if ($box) {
    Check '理由は格納でもデータが壊れていること' ($box.Text -match '^stored\.txt を開けませんでした。' -and $box.Text -match '書庫のデータが壊れています。$') $box.Text
    Close-MessageBox $box 'OK'
}
$leftover = Join-Path $env:TEMP 'Expzip' | Get-ChildItem -Recurse -Filter 'stored.txt' -ErrorAction SilentlyContinue
Check '壊れた中身を一時フォルダーに残さない' (@($leftover).Count -eq 0) (($leftover | ForEach-Object FullName) -join ', ')

# ZIP 以外は別のライブラリで読む。壊れたときの例外もそちらのものになる (#167)。
# 圧縮された tar は一覧を作るのにも中身を読み進めるので、書庫を開く時点で分かる
$source = Join-Path $script:Work 'tgz'
New-Item -ItemType Directory -Force -Path $source | Out-Null
[System.IO.File]::WriteAllText((Join-Path $source 'memo.txt'), ('hello ' * 2000))
$tgz = Join-Path $script:Work '壊れ.tar.gz'
& "$env:SystemRoot\System32\tar.exe" -czf $tgz -C $source memo.txt
# 先頭の 10 バイトは gzip のヘッダー。その後ろの圧縮したデータを壊す
$bytes = [System.IO.File]::ReadAllBytes($tgz)
foreach ($i in 40..60) { $bytes[$i] = $bytes[$i] -bxor 0x5A }
[System.IO.File]::WriteAllBytes($tgz, $bytes)

# 起動時に渡すと、知らせが出ている間は窓を掴めずに待たされる。開いている窓へ落として開く
$center = Get-WindowCenter $app
Invoke-Drop @($tgz) $center.X $center.Y | Out-Null
$box = Find-MessageBox $app
Check 'tar.gz でも知らせが出る' ($null -ne $box)
if ($box) {
    # 以前は「処理中に問題が発生しました」とライブラリの英語の文が出ていた
    Check 'tar.gz でも理由はデータが壊れていること' ($box.Text -match '壊れ\.tar\.gz を開けませんでした。' -and $box.Text -match '書庫のデータが壊れています。$') $box.Text
    Close-MessageBox $box 'OK'
}

Stop-Expzip $app
Complete-Suite
