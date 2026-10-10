# 一覧の表示の形 (#216)。特大アイコン / 大アイコン / 中アイコン / 小アイコン / 一覧 / 詳細
. "$PSScriptRoot\..\Common.ps1"
Start-Suite 'View'
Set-Settings

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace ExpzipUi
{
    public static class ViewKeys
    {
        [DllImport("user32.dll")] static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);

        // Ctrl+Shift+数字。SendKeys の ^+1 は配列によって「!」に化けるので、仮想キーで送る
        public static void ControlShift(byte digit)
        {
            keybd_event(0x11, 0, 0, UIntPtr.Zero);
            keybd_event(0x10, 0, 0, UIntPtr.Zero);
            keybd_event((byte)(0x30 + digit), 0, 0, UIntPtr.Zero);
            keybd_event((byte)(0x30 + digit), 0, 2, UIntPtr.Zero);
            keybd_event(0x10, 0, 2, UIntPtr.Zero);
            keybd_event(0x11, 0, 2, UIntPtr.Zero);
        }
    }
}
'@

$viewNames = '特大アイコン / 大アイコン / 中アイコン / 小アイコン / 一覧 / 詳細'

function Set-View($App, [int]$Digit) {
    Focus-App $App
    [ExpzipUi.ViewKeys]::ControlShift([byte]$Digit)
    Start-Sleep -Milliseconds 900
}

# 詳細の形の行は DataItem、列の無い形の項目は ListItem として見える
function Get-Items($App) {
    $list = ById $App.Window 'EntryList'
    return @($list.FindAll($script:Scope::Children, (New-Object System.Windows.Automation.OrCondition(
        (Condition $script:Automation::ControlTypeProperty $script:ControlType::DataItem),
        (Condition $script:Automation::ControlTypeProperty $script:ControlType::ListItem)))))
}

function Find-Item($App, [string]$Name) {
    return Get-Items $App | Where-Object { $_.Current.Name -eq $Name } | Select-Object -First 1
}

function Get-ItemNames($App) {
    return @(Get-Items $App | ForEach-Object { $_.Current.Name })
}

function Is-Selected($Item) {
    return $Item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Current.IsSelected
}

function Selection-Text($App) {
    return (ById $App.Window 'SelectionInfo').Current.Name
}

function Checked-Names($Menu) {
    return @(ByType $Menu $script:ControlType::MenuItem | Where-Object { IsChecked $_ } | ForEach-Object { $_.Current.Name })
}

$entries = [ordered]@{}
foreach ($name in 'a.txt', 'b.png', 'c.docx', 'd.exe', 'e.zip', 'f.unknownext', 'g.csv', 'h.mp3') {
    # 名前の順と逆の大きさにする。サイズで並べ替えたことが並びで分かるように
    $entries[$name] = 'x' * (8 - [array]::IndexOf(@('a.txt', 'b.png', 'c.docx', 'd.exe', 'e.zip', 'f.unknownext', 'g.csv', 'h.mp3'), $name))
}
$entries['フォルダー/中.txt'] = 'y'
$sample = New-TestZip (Join-Path $script:Work '表示.zip') $entries
$other = New-TestZip (Join-Path $script:Work '別.zip') ([ordered]@{ 'z.txt' = 'z' })

Section '書庫を開いていないとき'
$app = Start-Expzip
$button = ById $app.Window 'ViewButton'
Check 'ツールバーに「表示」がある' ($button.Current.Name -eq '表示') $button.Current.Name
Check '説明' ($button.Current.HelpText -eq '項目の表示方法を変更する') $button.Current.HelpText
$menu = Open-DropDown $app 'ViewButton'
Check '一覧が開く' ($null -ne $menu)
if ($menu) {
    $names = @(ByType $menu $script:ControlType::MenuItem | ForEach-Object { $_.Current.Name }) -join ' / '
    Check '並び' ($names -eq $viewNames) $names
    $enabled = @(ByType $menu $script:ControlType::MenuItem | Where-Object { $_.Current.IsEnabled })
    Check '書庫が無ければ選べない' ($enabled.Count -eq 0) "$($enabled.Count) 個"
    Close-DropDown $app
}
Stop-Expzip $app

$app = Start-Expzip @($sample)

Section '最初は詳細'
$menu = Open-DropDown $app 'ViewButton'
Check '詳細に印' ($menu -and ((Checked-Names $menu) -join ',') -eq '詳細') $(if ($menu) { (Checked-Names $menu) -join ',' })
Close-DropDown $app
$all = @(Get-ItemNames $app)
Check '項目が並ぶ' ($all.Count -eq 9) ($all -join ', ')

Section 'アイコンの形は横に並べて折り返す'
foreach ($case in @(
        @{ Digit = 3; Name = '中アイコン'; Shot = 'medium.png' },
        @{ Digit = 2; Name = '大アイコン'; Shot = 'large.png' },
        @{ Digit = 1; Name = '特大アイコン'; Shot = 'extralarge.png' },
        @{ Digit = 4; Name = '小アイコン'; Shot = 'small.png' })) {
    Set-View $app $case.Digit
    $a = (Find-Item $app 'a.txt'); $b = (Find-Item $app 'b.png')
    Check "$($case.Name): 項目が見える" ($null -ne $a -and $null -ne $b)
    if ($a -and $b) {
        $ra = $a.Current.BoundingRectangle; $rb = $b.Current.BoundingRectangle
        # 次の項目は右に並ぶか、段の終わりなら次の段の左端へ折り返す (特大アイコンは 1 段に 2 個)
        $ok = ([Math]::Abs($ra.Y - $rb.Y) -lt 2 -and $rb.X -gt $ra.X) -or ($rb.Y -ge $ra.Bottom - 1 -and $rb.X -lt $ra.X)
        Check "$($case.Name): 次の項目は右か、次の段の左端" $ok "a $ra / b $rb"
    }
    $menu = Open-DropDown $app 'ViewButton'
    Check "$($case.Name): 印が移る" ($menu -and ((Checked-Names $menu) -join ',') -eq $case.Name) $(if ($menu) { (Checked-Names $menu) -join ',' })
    Close-DropDown $app
    Save-Shot $app (Join-Path $script:Work $case.Shot)
}

Section '一覧の形は縦に並べる'
Set-View $app 5
$a = (Find-Item $app 'a.txt'); $b = (Find-Item $app 'b.png')
if ($a -and $b) {
    $ra = $a.Current.BoundingRectangle; $rb = $b.Current.BoundingRectangle
    Check '2 個目は下に並ぶ' ([Math]::Abs($ra.X - $rb.X) -lt 2 -and $rb.Y -gt $ra.Y) "a $ra / b $rb"
}
else {
    Check '項目が見える' $false
}
Save-Shot $app (Join-Path $script:Work 'list.png')

Section '中アイコンで操作する'
Set-View $app 3
Check '形を変えても項目の数は同じ' ((Get-ItemNames $app).Count -eq 9) ((Get-ItemNames $app) -join ', ')

# 選んだものは形を変えても選んだまま
Select-Element (Find-Item $app 'c.docx')
Start-Sleep -Milliseconds 300
Set-View $app 2
$selected = @(Get-Items $app | Where-Object { (Is-Selected $_) })
Check '選んだまま形を変えられる' ($selected.Count -eq 1 -and $selected[0].Current.Name -eq 'c.docx') (($selected | ForEach-Object { $_.Current.Name }) -join ', ')
Set-View $app 3

# 項目の間の余白から囲む。左上の項目の上の余白から、2 個目の項目の中ほどまで
$ra = (Find-Item $app 'a.txt').Current.BoundingRectangle
$rb = (Find-Item $app 'b.png').Current.BoundingRectangle
$list = (ById $app.Window 'EntryList').Current.BoundingRectangle
$rows = @(Get-Items $app | ForEach-Object { $_.Current.BoundingRectangle.Bottom } | Measure-Object -Maximum)
$blankY = [int]($rows[0].Maximum + 20)
if ($blankY -lt $list.Bottom - 5) {
    Invoke-MouseDrag ([int]($ra.X + 5)) $blankY ([int]($rb.X + $rb.Width / 2)) ([int]($rb.Y + $rb.Height / 2))
    $text = Selection-Text $app
    Check '下の余白から囲むと、かかった項目を選ぶ' ($text -match '^選択 \d+ 個' -and (@(Get-Items $app | Where-Object { (Is-Selected $_) }).Count -ge 2)) $text
    $picked = @(Get-Items $app | Where-Object { (Is-Selected $_) } | ForEach-Object { $_.Current.Name })
    Check '囲んだ列の外は選ばない' (-not ($picked -contains 'g.csv' -and $rb.X -lt (Find-Item $app 'g.csv').Current.BoundingRectangle.X)) ($picked -join ', ')
}

# 名前の変更 (F2)
$box = $null
for ($attempt = 0; $attempt -lt 3 -and $null -eq $box; $attempt++) {
    Select-Element (Find-Item $app 'a.txt')
    (Find-Item $app 'a.txt').SetFocus()
    Send-Keys $app '{F2}'
    # 入力欄が支援技術から見えること。見えないと入力先がウィンドウとして読まれる
    $box = Wait-Until -TimeoutMs 3000 {
        $focused = $script:Automation::FocusedElement
        if ($focused.Current.ControlType -eq $script:ControlType::Edit -and $focused.Current.ProcessId -eq $app.Process.Id) { $focused }
    }
}
$focused = $script:Automation::FocusedElement
Check 'F2 で名前の入力欄が出る' ($null -ne $box) "入力先: $($focused.Current.ControlType.ProgrammaticName) '$($focused.Current.Name)' $($focused.Current.ProcessId)"
if ($box) {
    Set-Text $box 'a2.txt'
    Send-Keys $app '{ENTER}'
    Wait-Idle $app
    Check '名前が変わる' ($null -ne (Wait-Until { Find-Item $app 'a2.txt' })) ((Get-ItemNames $app) -join ', ')
    Check '書き換えたあとも中アイコンのまま' ((Find-Item $app 'b.png').Current.BoundingRectangle.Width -lt 200)
}

Section '右クリックの表示と並べ替え'
Select-Element (Find-Item $app 'b.png')
(Find-Item $app 'b.png').SetFocus()
Send-Keys $app '+{F10}'
$menu = Find-DropDown $app
$view = if ($menu) { ByName $menu '表示' }
$sort = if ($menu) { ByName $menu '並べ替え' }
Check '「表示」がある' ($null -ne $view)
Check '「並べ替え」がある' ($null -ne $sort)
if ($sort) {
    Expand-Element $sort
    Start-Sleep -Milliseconds 500
    $sizeItem = Wait-Until {
        $script:Automation::RootElement.FindAll($script:Scope::Descendants,
            (Condition $script:Automation::ProcessIdProperty $app.Process.Id)) |
            Where-Object { $_.Current.ControlType -eq $script:ControlType::MenuItem -and $_.Current.Name -eq 'サイズ' } |
            Select-Object -First 1
    }
    Check '列の名前で並べ替えられる' ($null -ne $sizeItem)
    if ($sizeItem) { Push $sizeItem }
}
Close-DropDown $app
$order = @(Get-ItemNames $app)
# フォルダーは先、ファイルは大きさの小さい順 (h.mp3 は 1 バイト、a2.txt は 8 バイト)
Check 'サイズの順になる' ($order[0] -eq 'フォルダー' -and $order[1] -eq 'h.mp3' -and $order[-1] -eq 'a2.txt') ($order -join ', ')

Section 'タブごとに覚える'
Stop-Expzip $app

$app = Start-Expzip @($sample)
Set-View $app 3
Invoke-Drop @($other) (Get-WindowCenter $app).X (Get-WindowCenter $app).Y | Out-Null
Wait-Idle $app
Check '別の書庫のタブを開く' ((Get-Tabs $app).Count -eq 2) ((Get-Tabs $app) -join ', ')
$z = Find-Item $app 'z.txt'
Check '新しいタブは詳細' ($z -and $z.Current.ControlType -eq $script:ControlType::DataItem) $(if ($z) { $z.Current.ControlType.ProgrammaticName })
Send-Keys $app '^{TAB}'
$a = Find-Item $app 'b.png'
Check '元のタブに戻ると中アイコン' ($a -and $a.Current.ControlType -eq $script:ControlType::ListItem) $(if ($a) { $a.Current.ControlType.ProgrammaticName })
Stop-Expzip $app

Section '次の起動では詳細に戻る'
$app = Start-Expzip @($sample)
$a = Find-Item $app 'b.png'
Check '詳細で始まる' ($a -and $a.Current.ControlType -eq $script:ControlType::DataItem)

Section '英語'
$menu = Open-DropDown $app 'LanguageButton'
Push (ByName $menu 'English') 1200
$button = ById $app.Window 'ViewButton'
Check '英語の名前' ($button.Current.Name -eq 'View') $button.Current.Name
$menu = Open-DropDown $app 'ViewButton'
if ($menu) {
    $names = @(ByType $menu $script:ControlType::MenuItem | ForEach-Object { $_.Current.Name }) -join ' / '
    Check '英語の並び' ($names -eq 'Extra large icons / Large icons / Medium icons / Small icons / List / Details') $names
    Close-DropDown $app
}

Stop-Expzip $app
Complete-Suite
