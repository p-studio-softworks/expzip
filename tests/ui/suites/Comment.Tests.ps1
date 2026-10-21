# ZIP の書庫全体のコメントを見る・書く (#258)
. "$PSScriptRoot\..\Common.ps1"
Start-Suite 'Comment'
Set-Settings

$cp932 = [System.Text.Encoding]::GetEncoding(932)
$utf8 = New-Object System.Text.UTF8Encoding($false)

# 末尾の終端レコードを探して、その位置を返す
function Find-End([byte[]]$Bytes) {
    for ($i = $Bytes.Length - 22; $i -ge 0; $i--) {
        if ($Bytes[$i] -eq 0x50 -and $Bytes[$i + 1] -eq 0x4B -and $Bytes[$i + 2] -eq 5 -and $Bytes[$i + 3] -eq 6) {
            return $i
        }
    }
    throw '終端レコードがありません'
}

# 書庫のコメントをバイト列のまま書き込む。ZipArchive はコメントを書けないので、終端レコードを直接いじる
function Set-RawComment([string]$Path, [byte[]]$Comment) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    $end = Find-End $bytes
    $head = New-Object byte[] ($end + 22)
    [Array]::Copy($bytes, $head, $end + 22)
    $length = [BitConverter]::GetBytes([uint16]$Comment.Length)
    $head[$end + 20] = $length[0]
    $head[$end + 21] = $length[1]
    [IO.File]::WriteAllBytes($Path, [byte[]]($head + $Comment))
}

function Get-RawComment([string]$Path) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    $end = Find-End $bytes
    $length = [BitConverter]::ToUInt16($bytes, $end + 20)
    $comment = New-Object byte[] $length
    [Array]::Copy($bytes, $end + 22, $comment, 0, $length)
    return [pscustomobject]@{ Bytes = $comment; Trailing = $bytes.Length - ($end + 22 + $length) }
}

function Same([byte[]]$A, [byte[]]$B) { return [Convert]::ToBase64String($A) -eq [Convert]::ToBase64String($B) }

# コメントの窓を開く。ステータスバーの印を押すか、右クリックのメニューから (何もない所で出す。#289)
function Open-Comment($App, [string]$Name, [switch]$FromMenu) {
    if ($FromMenu) {
        $menu = Open-BlankMenu $App
        $item = if ($menu) { ByName $menu '書庫のコメント' }
        if ($null -eq $item -or -not $item.Current.IsEnabled) {
            Close-DropDown $App
            return $null
        }
        Push $item
    }
    else {
        Push (ById $App.Window 'CommentButton')
    }
    return Find-Window $App "書庫のコメント - $Name" 10000
}

function Test-CommentShown($App) {
    $item = ById $App.Window 'CommentButton'
    return ($null -ne $item) -and -not $item.Current.IsOffscreen
}

Section 'コメントのある書庫を開く'
$archive = New-TestZip (Join-Path $script:Work '配布物.zip') ([ordered]@{ 'readme.txt' = 'readme' })
# 古いツールが書く CP932 のコメント。端末向けの色の指定 (ESC) も入っている
$original = $cp932.GetBytes("配布元: P studio`r`n連絡先: なし`r`n") + [byte[]](0x1B) + $cp932.GetBytes('[31m赤')
Set-RawComment $archive $original
$app = Start-Expzip @($archive)
Check 'ステータスバーで知らせる' (Test-CommentShown $app)
Check '知らせの文言' ((ById $app.Window 'CommentButton').Current.Name -eq 'コメントがあります') (ById $app.Window 'CommentButton').Current.Name

$window = Open-Comment $app '配布物.zip'
Check '押すとコメントが開く' ($null -ne $window)
if ($window) {
    $text = ValueOf (ById $window 'CommentBox')
    Check 'CP932 のコメントを読める' ($text -match '配布元: P studio' -and $text -match '連絡先: なし') $text
    Check '制御文字は記号にして出す' ($text -match '␛\[31m赤' -and $text -notmatch [char]0x1B) $text
    Check '書き換えられる' (-not (IsReadOnly (ById $window 'CommentBox')))

    # 直さずに閉じたら書き込まない
    Push (ById $window 'CancelButton')
}
$raw = Get-RawComment $archive
Check '直さなければバイト列もそのまま' (Same $raw.Bytes $original)

Section '直して保存しても、記号は書き込まない'
# 画面では ESC を ␛ にして出す。直して保存したときに ␛ を文字として書くと、CP932 で表せず全体が UTF-8 になる (#262)
$window = Open-Comment $app '配布物.zip'
if ($window) {
    $box = ById $window 'CommentBox'
    Set-Text $box ((ValueOf $box) + ' 追記')
    Push (ById $window 'SaveButton')
}
Wait-Idle $app
$raw = Get-RawComment $archive
$expected = $cp932.GetBytes("配布元: P studio`r`n連絡先: なし`r`n[31m赤 追記")
Check '記号を除いて CP932 で書く' (Same $raw.Bytes $expected) ([BitConverter]::ToString($raw.Bytes))

Section '日本語のコメントを書く'
$window = Open-Comment $app '配布物.zip' -FromMenu
Check '右クリックからも開く' ($null -ne $window)
if ($window) {
    Set-Text (ById $window 'CommentBox') "新しいコメント`r`n2 行目"
    Push (ById $window 'SaveButton')
}
Wait-Idle $app
Check '保存したと出る' ((Get-Status $app) -eq 'コメントを保存しました') (Get-Status $app)
$raw = Get-RawComment $archive
Check 'CP932 で書く' (Same $raw.Bytes $cp932.GetBytes("新しいコメント`r`n2 行目")) ([BitConverter]::ToString($raw.Bytes))
Check '後ろに余りが無い' ($raw.Trailing -eq 0) $raw.Trailing
Check '中のファイルはそのまま' (((Get-ZipNames $archive) -join ', ') -eq 'readme.txt') ((Get-ZipNames $archive) -join ', ')
Check '書庫は開いたまま' (((Get-RowNames $app) -join ', ') -eq 'readme.txt') ((Get-RowNames $app) -join ', ')

Section 'CP932 で書けない文字'
$window = Open-Comment $app '配布物.zip'
if ($window) {
    Check '書いたものが読める' ((ValueOf (ById $window 'CommentBox')) -eq "新しいコメント`r`n2 行目") (ValueOf (ById $window 'CommentBox'))
    # 「𠮷」は CP932 に無い
    Set-Text (ById $window 'CommentBox') '𠮷野家'
    Push (ById $window 'SaveButton')
}
Wait-Idle $app
$raw = Get-RawComment $archive
Check 'UTF-8 で書く' (Same $raw.Bytes $utf8.GetBytes('𠮷野家')) ([BitConverter]::ToString($raw.Bytes))
Check '短くなった分の余りを切る' ($raw.Trailing -eq 0) $raw.Trailing

Section 'コメントを消す'
$window = Open-Comment $app '配布物.zip'
if ($window) {
    Set-Text (ById $window 'CommentBox') ''
    Push (ById $window 'SaveButton')
}
Wait-Idle $app
Check '削除したと出る' ((Get-Status $app) -eq 'コメントを削除しました') (Get-Status $app)
Check 'ステータスバーの知らせが消える' (-not (Test-CommentShown $app))
$raw = Get-RawComment $archive
Check 'コメントが空になる' ($raw.Bytes.Length -eq 0 -and $raw.Trailing -eq 0) "$($raw.Bytes.Length) / $($raw.Trailing)"
Stop-Expzip $app

Section 'コメントの無い書庫に付ける'
$plain = New-TestZip (Join-Path $script:Work 'plain.zip') ([ordered]@{ 'a.txt' = 'a' })
$app = Start-Expzip @($plain)
Check '知らせは出ない' (-not (Test-CommentShown $app))
$window = Open-Comment $app 'plain.zip' -FromMenu
Check '右クリックから付けられる' ($null -ne $window)
if ($window) {
    Set-Text (ById $window 'CommentBox') 'hello'
    Push (ById $window 'SaveButton')
}
Wait-Idle $app
Check '付けると知らせが出る' (Test-CommentShown $app)
Check 'ASCII はそのまま' (Same (Get-RawComment $plain).Bytes $utf8.GetBytes('hello'))
Stop-Expzip $app

Section 'コメントの中に終端レコードと同じ並びがある'
# ほかのツールが書いたバイナリのコメント。中の並びを終端レコードと取り違えない (#263)
$tricky = New-TestZip (Join-Path $script:Work 'tricky.zip') ([ordered]@{ 'a.txt' = 'a' })
$fake = [byte[]](0x50, 0x4B, 0x05, 0x06) + (New-Object byte[] 18)
Set-RawComment $tricky ($utf8.GetBytes('ABCDE') + $fake + $utf8.GetBytes('tail'))
$app = Start-Expzip @($tricky)
$window = Open-Comment $app 'tricky.zip'
if ($window) {
    $text = ValueOf (ById $window 'CommentBox')
    Check 'コメント全体を読む' ($text.StartsWith('ABCDEPK') -and $text.EndsWith('tail')) $text
    Set-Text (ById $window 'CommentBox') 'fixed'
    Push (ById $window 'SaveButton')
}
Wait-Idle $app
$raw = Get-RawComment $tricky
Check '本当のコメントの位置に書く' ((Same $raw.Bytes $utf8.GetBytes('fixed')) -and $raw.Trailing -eq 0) "$([BitConverter]::ToString($raw.Bytes)) / $($raw.Trailing)"
Check '中のファイルはそのまま' (((Get-ZipNames $tricky) -join ', ') -eq 'a.txt') ((Get-ZipNames $tricky) -join ', ')
Stop-Expzip $app

Section 'パスワード付きの書庫'
# 暗号化の印だけを立てる。コメントの書き換えは中身に触らないので、パスワードを尋ねず、印もそのまま残る
$locked = New-TestZip (Join-Path $script:Work 'locked.zip') ([ordered]@{ 'secret.txt' = 'secret' })
$bytes = [IO.File]::ReadAllBytes($locked)
$bytes[6] = $bytes[6] -bor 1
$end = Find-End $bytes
$directory = [BitConverter]::ToUInt32($bytes, $end + 16)
$bytes[$directory + 8] = $bytes[$directory + 8] -bor 1
[IO.File]::WriteAllBytes($locked, $bytes)
$app = Start-Expzip @($locked)
$window = Open-Comment $app 'locked.zip' -FromMenu
if ($window) {
    Set-Text (ById $window 'CommentBox') 'locked comment'
    Push (ById $window 'SaveButton')
}
$asked = Find-Window $app 'パスワード' 2000
Check 'パスワードを尋ねない' ($null -eq $asked)
if ($asked) { Send-Keys $app '{ESC}' }
Wait-Idle $app
Check '保存できる' ((Get-Status $app) -eq 'コメントを保存しました') (Get-Status $app)
$after = [IO.File]::ReadAllBytes($locked)
Check '暗号化の印はそのまま' (($after[6] -band 1) -eq 1)
Check 'コメントが入る' (Same (Get-RawComment $locked).Bytes $utf8.GetBytes('locked comment'))
Stop-Expzip $app

Section '書き換えられない書庫は見るだけ'
$package = New-TestZip (Join-Path $script:Work 'app.msix') ([ordered]@{ 'AppxManifest.xml' = '<Package />' })
Set-RawComment $package $utf8.GetBytes('パッケージのコメント')
$app = Start-Expzip @($package)
Check '知らせが出る' (Test-CommentShown $app)
$window = Open-Comment $app 'app.msix'
Check '開く' ($null -ne $window)
if ($window) {
    Check 'UTF-8 のコメントを読める' ((ValueOf (ById $window 'CommentBox')) -eq 'パッケージのコメント') (ValueOf (ById $window 'CommentBox'))
    Check '書き換えられない' (IsReadOnly (ById $window 'CommentBox'))
    $save = ById $window 'SaveButton'
    Check '保存は出ない' ($null -eq $save -or $save.Current.IsOffscreen)
    Check '見るだけと断る' ((Texts $window) -match '表示のみです') (Texts $window)
    Check '閉じるだけ' ((ById $window 'CancelButton').Current.Name -eq '閉じる') (ById $window 'CancelButton').Current.Name
    Push (ById $window 'CancelButton')
}
Stop-Expzip $app

Complete-Suite
