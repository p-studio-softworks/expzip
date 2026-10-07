# 一覧をマウスのドラッグで囲んで選ぶ (#205)
. "$PSScriptRoot\..\Common.ps1"
Start-Suite 'Select'
Set-Settings

# Ctrl を押したままにするため。キーを送る Send-Keys は押して放すまでを 1 度に済ませる
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace ExpzipUi
{
    public static class Keys
    {
        [DllImport("user32.dll")] static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
        public static void ControlDown() { keybd_event(0x11, 0, 0, UIntPtr.Zero); }
        public static void ControlUp() { keybd_event(0x11, 0, 2, UIntPtr.Zero); }
    }
}
'@

function Selection-Text($App) {
    return (ById $App.Window 'SelectionInfo').Current.Name
}

# 列見出しの横の真ん中。囲み始める位置を、名前の列とそれ以外とで選ぶのに使う
function Get-ColumnX($App, [string]$Header) {
    $list = ById $App.Window 'EntryList'
    $item = @($list.FindAll($script:Scope::Descendants,
        (Condition $script:Automation::ControlTypeProperty $script:ControlType::HeaderItem))) |
        Where-Object { $_.Current.Name -eq $Header -or (@(Texts $_) -contains $Header) } | Select-Object -First 1
    $r = $item.Current.BoundingRectangle
    return [int]($r.X + $r.Width / 2)
}

function Get-RowY($App, [string]$Name) {
    $r = (Find-Row $App $Name).Current.BoundingRectangle
    return [int]($r.Y + $r.Height / 2)
}

$small = New-TestZip (Join-Path $script:Work '少し.zip') ([ordered]@{
    'a.txt' = 'a'
    'b.txt' = 'b'
    'c.txt' = 'c'
    'd.txt' = 'd'
    'e.txt' = 'e'
})

$app = Start-Expzip @($small)
Focus-App $app
$sizeX = Get-ColumnX $app 'サイズ'

Section '名前以外の列から囲む'
Invoke-MouseDrag $sizeX (Get-RowY $app 'a.txt') $sizeX (Get-RowY $app 'c.txt')
Check '囲んだ 3 行を選ぶ' ((Selection-Text $app) -match '^選択 3 個') (Selection-Text $app)

Section '行の下の余白から囲む'
# 余白から上へ。下の 2 行にかかる
$blankY = [int]((Find-Row $app 'e.txt').Current.BoundingRectangle.Bottom + 40)
Invoke-MouseDrag $sizeX $blankY $sizeX (Get-RowY $app 'd.txt')
Check '囲み直すと前の選択は外れる' ((Selection-Text $app) -match '^選択 2 個') (Selection-Text $app)

Section 'Ctrl を押して囲む'
# 今の d.txt と e.txt に、a.txt を足す
[ExpzipUi.Keys]::ControlDown()
try {
    Invoke-MouseDrag $sizeX $blankY $sizeX (Get-RowY $app 'a.txt')
} finally {
    [ExpzipUi.Keys]::ControlUp()
}
Check '囲んだ行を今の選択に足す' ((Selection-Text $app) -match '^選択 5 個') (Selection-Text $app)
Stop-Expzip $app

Section '端まで来たら自動でスクロールする'
$many = [ordered]@{}
foreach ($i in 1..400) { $many[('f{0:000}.txt' -f $i)] = 'x' }
$big = New-TestZip (Join-Path $script:Work 'たくさん.zip') $many
$app = Start-Expzip @($big)
Focus-App $app
$sizeX = Get-ColumnX $app 'サイズ'
$visible = @(Get-Rows $app | Where-Object { -not $_.Current.IsOffscreen }).Count
$list = (ById $app.Window 'EntryList').Current.BoundingRectangle
# 一覧の下の外 (ステータスバーの辺り) まで運んで、しばらく止める
Invoke-MouseDrag $sizeX (Get-RowY $app 'f001.txt') $sizeX ([int]($list.Bottom + 15))
$text = Selection-Text $app
$count = if ($text -match '^選択 ([\d,]+) 個') { [int]($Matches[1] -replace ',', '') } else { 0 }
Check '見えていた行より多く選ぶ' ($count -gt $visible + 5) "$text / 見えていた行 $visible"
$first = Find-Row $app 'f001.txt'
Check '一覧が下へ動く' ($null -eq $first -or $first.Current.IsOffscreen)
Stop-Expzip $app

Complete-Suite
