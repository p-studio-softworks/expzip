# ツリーの右クリックとキー (#282)。対象はツリーで選んでいる節のフォルダー
. "$PSScriptRoot\..\Common.ps1"
Start-Suite 'TreeMenu'
Set-Settings
$archive = New-TestZip (Join-Path $script:Work 'tree.zip') ([ordered]@{
    'docs/memo.txt'  = 'memo'
    'docs/sub/b.txt' = 'b'
    'top.txt'        = 'top'
})

# ツリーの項目。自分の名前 (最初の字) で探す。子の名前まで見ると親の項目が当たる
function Find-TreeItem($App, [string]$Name) {
    $tree = ById $App.Window 'FolderTree'
    return ByType $tree $script:ControlType::TreeItem | Where-Object {
        $text = ByType $_ $script:ControlType::Text | Select-Object -First 1
        $text -and $text.Current.Name -eq $Name
    } | Select-Object -First 1
}

# ツリーの節を選び、フォーカスを置く
function Focus-TreeItem($App, [string]$Name) {
    $item = Find-TreeItem $App $Name
    if ($null -eq $item) { throw "ツリーにありません: $Name" }
    Select-Element $item
    Start-Sleep -Milliseconds 600
    Wait-Idle $App
    (Find-TreeItem $App $Name).SetFocus()
    Start-Sleep -Milliseconds 300
}

function Open-TreeMenu($App, [string]$Name) {
    Focus-TreeItem $App $Name
    Send-Keys $App '+{F10}'
    return Find-DropDown $App
}

function Menu-Names($Menu) {
    return @(ByType $Menu $script:ControlType::MenuItem | ForEach-Object { $_.Current.Name })
}

function Is-Enabled($Menu, [string]$Name) {
    $item = ByName $Menu $Name
    return $null -ne $item -and $item.Current.IsEnabled
}

$app = Start-Expzip @($archive)

Section '項目の並び'
$menu = Open-TreeMenu $app 'docs'
Check 'メニューが出る' ($null -ne $menu)
if ($menu) {
    $names = Menu-Names $menu
    $expected = @('新しい書庫を作成', 'ハッシュ値を計算', '書庫のコメント', '名前の変更', '削除', '新しいフォルダー', '元に戻す', '最新の情報に更新')
    Check '一覧と同じ並び (フォルダーに意味の無い項目は除く)' (($names -join ',') -eq ($expected -join ',')) ($names -join ', ')
    foreach ($name in '新しい書庫を作成', 'ハッシュ値を計算', '名前の変更', '削除', '新しいフォルダー', '最新の情報に更新') {
        Check "押せる: $name" (Is-Enabled $menu $name)
    }
    Close-DropDown $app
}

Section 'いちばん上の節'
$menu = Open-TreeMenu $app 'tree.zip'
if ($menu) {
    foreach ($name in '新しい書庫を作成', '名前の変更', '削除') {
        Check "押せない: $name" (-not (Is-Enabled $menu $name))
    }
    Check '押せる: ハッシュ値を計算 (書庫ファイルの値)' (Is-Enabled $menu 'ハッシュ値を計算')
    Check '押せる: 新しいフォルダー' (Is-Enabled $menu '新しいフォルダー')
    Close-DropDown $app
}
else { Check 'メニューが出る' $false }

Section 'ハッシュ値は節のフォルダーの中のファイル'
$menu = Open-TreeMenu $app 'docs'
$entry = if ($menu) { ByName $menu 'ハッシュ値を計算' }
if ($entry) {
    Push $entry
    $window = Find-Window $app 'ハッシュ値 - tree.zip' 30000
    Check '窓が開く' ($null -ne $window)
    if ($window) {
        Wait-Until { (ById $window 'Headline').Current.Name -match 'ハッシュ値を計算しました$' } 30000 | Out-Null
        $headline = (ById $window 'Headline').Current.Name
        Check 'docs の中の 2 個' ($headline -eq '2 個のファイルのハッシュ値を計算しました') $headline
        Push (ById $window 'CloseButton')
    }
}
else { Check 'ハッシュ値を計算がある' $false }

Section 'F2 でツリーの上で名前を変える'
Focus-TreeItem $app 'docs'
Send-Keys $app '{F2}'
$box = Wait-Until { ByType (ById $app.Window 'FolderTree') $script:ControlType::Edit | Select-Object -First 1 }
Check 'ツリーに入力欄が開く' ($null -ne $box)
if ($box) {
    Check '今の名前が入っている' ($box.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -eq 'docs')
    # 入力欄の見た目を目で確かめるための写し
    Save-Shot @{ Window = $app.Window } (Join-Path $script:Work 'tree-rename.png')
    Set-Text $box 'papers'
    $box.SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
    Start-Sleep -Milliseconds 900
    Wait-Idle $app
    $names = Get-ZipNames $archive
    Check '書庫の中の名前が変わる' (($names -contains 'papers/memo.txt') -and -not ($names | Where-Object { $_ -like 'docs/*' })) ($names -join ', ')
    Check '名前を変えたフォルダーを見たまま' ((Get-RowNames $app) -contains 'memo.txt') ((Get-RowNames $app) -join ', ')
    Check 'ツリーにも新しい名前' ($null -ne (Find-TreeItem $app 'papers'))
}

Section '右クリックの名前の変更、Esc で取りやめる'
# メニューが閉じたときにフォーカスが戻っても、入力欄が閉じない
$menu = Open-TreeMenu $app 'papers'
$entry = if ($menu) { ByName $menu '名前の変更' }
if ($entry) { Push $entry }
Start-Sleep -Milliseconds 600
$box = ByType (ById $app.Window 'FolderTree') $script:ControlType::Edit | Select-Object -First 1
Check 'メニューから入力欄が開いたまま' ($null -ne $box)
if ($box) {
    Set-Text $box 'other'
    $box.SetFocus()
    [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
    Start-Sleep -Milliseconds 600
    Check '入力欄が閉じる' ($null -eq (ByType (ById $app.Window 'FolderTree') $script:ControlType::Edit | Select-Object -First 1))
    Check '名前は変わらない' ((Get-ZipNames $archive) -contains 'papers/memo.txt')
}
else { Check '入力欄が開く' $false }

Section 'Del でツリーの節を消す'
# 一覧では別の行を選んでおく。消えるのはツリーの節のほう
Focus-TreeItem $app 'papers'
Expand-Element (Find-TreeItem $app 'papers')
Start-Sleep -Milliseconds 400
Select-Row $app 'memo.txt' | Out-Null
Focus-TreeItem $app 'sub'
Send-Keys $app '{DEL}'
$box = Find-MessageBox $app
Check '確認が出る' ($null -ne $box)
if ($box) {
    Check '消すのは sub' ($box.Text -match '\bsub\b' -and $box.Text -notmatch 'memo') $box.Text
    Close-MessageBox $box 'はい(Y)'
    Wait-Idle $app
    $names = Get-ZipNames $archive
    Check 'sub が中身ごと消える' (-not ($names | Where-Object { $_ -like 'papers/sub*' })) ($names -join ', ')
    Check 'memo.txt は残る' ($names -contains 'papers/memo.txt') ($names -join ', ')
    Check '一つ上のフォルダーへ移る' ((Get-RowNames $app) -contains 'memo.txt') ((Get-RowNames $app) -join ', ')
}

Section '右クリックの削除'
$menu = Open-TreeMenu $app 'papers'
$entry = if ($menu) { ByName $menu '削除' }
if ($entry) {
    Push $entry
    $box = Find-MessageBox $app
    if ($box) { Close-MessageBox $box 'はい(Y)' }
    Wait-Idle $app
    $names = Get-ZipNames $archive
    Check 'papers が消える' (($names -join ',') -eq 'top.txt') ($names -join ', ')
    Check 'いちばん上を見る' ((Get-RowNames $app) -contains 'top.txt') ((Get-RowNames $app) -join ', ')
}
else { Check '削除がある' $false }

Stop-Expzip $app
Complete-Suite
