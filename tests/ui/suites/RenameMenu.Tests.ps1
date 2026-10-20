# 名前の入力欄の右クリック (#284)。エクスプローラーと同じく、名前の一部をコピーしたり貼り付けたりできる
. "$PSScriptRoot\..\Common.ps1"
Start-Suite 'RenameMenu'
Set-Settings
$archive = New-TestZip (Join-Path $script:Work 'rename.zip') ([ordered]@{
    'docs/memo.txt' = 'memo'
    'report.txt'    = 'top'
})

# マウスの右ボタンで押す。キーボードで出すメニューと違い、押したときに入力欄からフォーカスが外れかける
if (-not ('ExpzipUi.RightClicker' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace ExpzipUi
{
    public static class RightClicker
    {
        [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
        [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

        // UI Automation の座標は画面の実際の px なので、スレッドを合わせてから動かす
        public static void Click(int x, int y)
        {
            SetThreadDpiAwarenessContext(new IntPtr(-4));
            SetCursorPos(x, y);
            Thread.Sleep(200);
            mouse_event(0x0008, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(80);
            mouse_event(0x0010, 0, 0, 0, UIntPtr.Zero);
        }
    }
}
'@
}

function Get-Box($App, [string]$Id) {
    return ByType (ById $App.Window $Id) $script:ControlType::Edit | Select-Object -First 1
}

# 入力欄の左寄りを右クリックして、出たメニューを返す
function Open-BoxMenu($App, $Box) {
    Focus-App $App
    $rect = $Box.Current.BoundingRectangle
    [ExpzipUi.RightClicker]::Click([int]($rect.X + 10), [int]($rect.Y + $rect.Height / 2))
    return Find-DropDown $App
}

function Menu-Names($Menu) {
    return @(ByType $Menu $script:ControlType::MenuItem | ForEach-Object { $_.Current.Name })
}

function Is-Enabled($Menu, [string]$Name) {
    $item = ByName $Menu $Name
    return $null -ne $item -and $item.Current.IsEnabled
}

# 入力欄の中の選んでいる文字
function Get-BoxSelection($Box) {
    $ranges = $Box.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern).GetSelection()
    return ($ranges | ForEach-Object { $_.GetText(-1) }) -join ''
}

$app = Start-Expzip @($archive)

Section '一覧の入力欄'
Select-Row $app 'report.txt' | Out-Null
Send-Keys $app '{F2}'
$box = Wait-Until { Get-Box $app 'EntryList' }
Check '入力欄が開く' ($null -ne $box)
if ($box) {
    # 開いた直後は拡張子を除いた「report」を選んでいる
    Check '名前の一部を選んでいる' ((Get-BoxSelection $box) -eq 'report') (Get-BoxSelection $box)
    $menu = Open-BoxMenu $app $box
    Check 'メニューが出る' ($null -ne $menu)
    if ($menu) {
        $names = Menu-Names $menu
        $expected = @('元に戻す', '切り取り', 'コピー', '貼り付け', '削除', 'すべて選択')
        Check 'エクスプローラーと同じ項目' (($names -join ',') -eq ($expected -join ',')) ($names -join ', ')
        Check '入力欄が開いたまま' ($null -ne (Get-Box $app 'EntryList'))
        foreach ($name in 'コピー', '切り取り', '削除', 'すべて選択') {
            Check "押せる: $name" (Is-Enabled $menu $name)
        }
        Check '押せない: 元に戻す (まだ書き換えていない)' (-not (Is-Enabled $menu '元に戻す'))
        Save-Shot @{ Window = $app.Window } (Join-Path $script:Work 'rename-menu.png')

        [System.Windows.Forms.Clipboard]::Clear()
        Push (ByName $menu 'コピー')
        $copied = Wait-Until { [System.Windows.Forms.Clipboard]::GetText() } 3000
        Check '選んだ部分だけコピーされる' ($copied -eq 'report') $copied
        $box = Get-Box $app 'EntryList'
        Check 'コピーしても入力欄は開いたまま' ($null -ne $box)
        Check '名前は変わらない' ((Get-ZipNames $archive) -contains 'report.txt')
    }
}

Section '貼り付けて確定する'
$box = Get-Box $app 'EntryList'
if ($box) {
    # 「report」を選んだまま、選んだ部分の上を右クリックする。貼り付けると選んだ部分が置き換わる
    [System.Windows.Forms.Clipboard]::SetText('memo')
    $menu = Open-BoxMenu $app $box
    Check '押せる: 貼り付け' (Is-Enabled $menu '貼り付け')
    if ($menu) { Push (ByName $menu '貼り付け') }
    $box = Get-Box $app 'EntryList'
    Check '貼り付けても入力欄は開いたまま' ($null -ne $box)
    if ($box) {
        Check '選んだ部分が貼り付けた文字になる' ((ValueOf $box) -eq 'memo.txt') (ValueOf $box)
        $menu = Open-BoxMenu $app $box
        Check '押せる: 元に戻す (書き換えたあと)' (Is-Enabled $menu '元に戻す')
        if ($menu) { Push (ByName $menu '元に戻す') }
        $box = Get-Box $app 'EntryList'
        Check '元に戻すで貼り付ける前に戻る' ($box -and (ValueOf $box) -eq 'report.txt') $(if ($box) { ValueOf $box })
        if ($box) {
            Set-Text $box 'report2.txt'
            $box.SetFocus()
            [System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
            Start-Sleep -Milliseconds 900
            Wait-Idle $app
            Check 'Enter で確定する' ((Get-ZipNames $archive) -contains 'report2.txt') ((Get-ZipNames $archive) -join ', ')
        }
    }
}

Section 'Esc でメニューを閉じても書き換えを続けられる'
Select-Row $app 'report2.txt' | Out-Null
Send-Keys $app '{F2}'
$box = Wait-Until { Get-Box $app 'EntryList' }
if ($box) {
    $menu = Open-BoxMenu $app $box
    Check 'メニューが出る' ($null -ne $menu)
    Close-DropDown $app
    Start-Sleep -Milliseconds 400
    Check '入力欄が開いたまま' ($null -ne (Get-Box $app 'EntryList'))
    [System.Windows.Forms.SendKeys]::SendWait('x{ENTER}')
    Start-Sleep -Milliseconds 900
    Wait-Idle $app
    # 「report2」を選んでいたので、打った字で置き換わる
    Check '入力欄へ戻って続けて打てる' ((Get-ZipNames $archive) -contains 'x.txt') ((Get-ZipNames $archive) -join ', ')
}
else { Check '入力欄が開く' $false }

Section 'ツリーの入力欄 (#282)'
$tree = ById $app.Window 'FolderTree'
$node = ByType $tree $script:ControlType::TreeItem | Where-Object {
    $text = ByType $_ $script:ControlType::Text | Select-Object -First 1
    $text -and $text.Current.Name -eq 'docs'
} | Select-Object -First 1
if ($node) {
    Select-Element $node
    Start-Sleep -Milliseconds 600
    Wait-Idle $app
    $node.SetFocus()
    Send-Keys $app '{F2}'
    $box = Wait-Until { Get-Box $app 'FolderTree' }
    Check '入力欄が開く' ($null -ne $box)
    if ($box) {
        $menu = Open-BoxMenu $app $box
        Check 'ツリーの右クリックではなく、入力欄のメニューが出る' ($menu -and ((Menu-Names $menu) -join ',') -eq '元に戻す,切り取り,コピー,貼り付け,削除,すべて選択') $(if ($menu) { (Menu-Names $menu) -join ', ' })
        if ($menu) {
            [System.Windows.Forms.Clipboard]::Clear()
            Push (ByName $menu 'コピー')
            $copied = Wait-Until { [System.Windows.Forms.Clipboard]::GetText() } 3000
            Check 'フォルダーの名前をコピーできる' ($copied -eq 'docs') $copied
        }
        Check '入力欄が開いたまま' ($null -ne (Get-Box $app 'FolderTree'))
        $box = Get-Box $app 'FolderTree'
        if ($box) {
            $box.SetFocus()
            [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
            Start-Sleep -Milliseconds 400
        }
        Check '名前は変わらない' ((Get-ZipNames $archive) -contains 'docs/memo.txt')
    }
}
else { Check 'ツリーに docs がある' $false }

Section '英語の表示'
Stop-Expzip $app
Set-Settings @{ Language = 'en' }
$app = Start-Expzip @($archive)
Select-Row $app 'x.txt' | Out-Null
Send-Keys $app '{F2}'
$box = Wait-Until { Get-Box $app 'EntryList' }
if ($box) {
    $menu = Open-BoxMenu $app $box
    $names = if ($menu) { Menu-Names $menu }
    Check '表示の言語の文言' (($names -join ',') -eq 'Undo,Cut,Copy,Paste,Delete,Select all') ($names -join ', ')
    Close-DropDown $app
}
else { Check '入力欄が開く' $false }

Stop-Expzip $app
Complete-Suite
