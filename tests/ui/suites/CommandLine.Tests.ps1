# コマンドラインから、ウィンドウを出さずに圧縮する (#316)
. "$PSScriptRoot\..\Common.ps1"
Start-Suite 'CommandLine'
Set-Settings

# 終わるまで待ち、終了コードと出力を返す。ウィンドウが出たままなら終わらないので、時間切れになる
function Invoke-Cli([string[]]$Arguments) {
    $out = Join-Path $script:Work 'stdout.txt'
    $err = Join-Path $script:Work 'stderr.txt'
    $quoted = @($Arguments | ForEach-Object { '"' + $_ + '"' })
    $process = Start-Process $script:Exe -ArgumentList $quoted -PassThru -NoNewWindow `
        -RedirectStandardOutput $out -RedirectStandardError $err
    $null = $process.Handle
    $exited = $process.WaitForExit(60000)
    if (-not $exited) { $process.Kill(); $process.WaitForExit() }
    return [pscustomobject]@{
        Exited = $exited
        Code   = $process.ExitCode
        Out    = [IO.File]::ReadAllText($out, [Text.Encoding]::UTF8)
        Err    = [IO.File]::ReadAllText($err, [Text.Encoding]::UTF8)
    }
}

function Get-ZipNames([string]$Path) {
    $zip = [IO.Compression.ZipFile]::OpenRead($Path)
    try { return (@($zip.Entries | ForEach-Object { $_.FullName }) | Sort-Object) -join ', ' }
    finally { $zip.Dispose() }
}

function Write-Bytes([string]$Path, [string]$Text, [Text.Encoding]$Encoding) {
    [IO.File]::WriteAllBytes($Path, $Encoding.GetBytes($Text))
}

$utf8 = New-Object Text.UTF8Encoding($false)
$src = Join-Path $script:Work 'src'
$out = Join-Path $script:Work 'out'
New-Item -ItemType Directory -Force -Path (Join-Path $src 'data\sub'), $out | Out-Null
$file = Join-Path $src 'a.txt'
$folder = Join-Path $src 'data'
[IO.File]::WriteAllText($file, 'one', $utf8)
[IO.File]::WriteAllText((Join-Path $folder 'x.txt'), 'two', $utf8)
[IO.File]::WriteAllText((Join-Path $folder 'sub\y.txt'), 'three', $utf8)

Section 'ひとつ圧縮する'
$target = Join-Path $out 'file.zip'
$r = Invoke-Cli @('--compress', $file, $target)
Check 'ウィンドウを出さずに終わる' $r.Exited
Check 'ファイル: 終了コード 0' ($r.Code -eq 0) "$($r.Code) $($r.Err)"
Check 'ファイル: 中身' ((Get-ZipNames $target) -eq 'a.txt') (Get-ZipNames $target)
Check '結果を出力する' ($r.Out -match 'file\.zip を作成しました') $r.Out
$r = Invoke-Cli @('--compress', $folder, (Join-Path $out 'folder.zip'))
Check 'フォルダー: 終了コード 0' ($r.Code -eq 0) "$($r.Code) $($r.Err)"
$names = Get-ZipNames (Join-Path $out 'folder.zip')
Check 'フォルダー: 階層が残る' ($names -match 'data/x\.txt' -and $names -match 'data/sub/y\.txt') $names
Check '一時ファイルが残らない' (@(Get-ChildItem $out -Filter '*.tmp').Count -eq 0)

Section '既にある書庫'
$before = [IO.File]::ReadAllBytes($target)
$r = Invoke-Cli @('--compress', $folder, $target)
Check '既定では失敗する' ($r.Code -eq 2) $r.Code
Check '理由を出す' ($r.Err -match '書庫が既にあります') $r.Err
Check '元の書庫のまま' ([Convert]::ToBase64String([IO.File]::ReadAllBytes($target)) -eq [Convert]::ToBase64String($before))
$r = Invoke-Cli @('--compress', $folder, $target, '--overwrite')
Check '--overwrite で置き換える' ($r.Code -eq 0 -and (Get-ZipNames $target) -match 'data/x\.txt') "$($r.Code) $($r.Err)"

Section '指定の不備は終了コード 2'
$r = Invoke-Cli @('--compress', (Join-Path $src 'nothing.txt'), (Join-Path $out 'n.zip'))
Check 'ソースが無い' ($r.Code -eq 2 -and $r.Err -match 'ソースが見つかりません') "$($r.Code) $($r.Err)"
$r = Invoke-Cli @('--compress', $file, (Join-Path $out 'a.7z'))
Check '.zip 以外' ($r.Code -eq 2 -and $r.Err -match '\.zip だけ') "$($r.Code) $($r.Err)"
$r = Invoke-Cli @('--compress', $file, (Join-Path $out 'none\a.zip'))
Check '保存先のフォルダーが無い' ($r.Code -eq 2 -and $r.Err -match '保存先のフォルダーがありません') "$($r.Code) $($r.Err)"
$r = Invoke-Cli @('--compress', $folder, (Join-Path $folder 'self.zip'))
Check 'ソースの中に書庫を作る' ($r.Code -eq 2 -and $r.Err -match 'の外に作成') "$($r.Code) $($r.Err)"
Check '  その書庫は作らない' (-not (Test-Path (Join-Path $folder 'self.zip')))
$r = Invoke-Cli @('--bad')
Check '知らない指定' ($r.Code -eq 2 -and $r.Err -match '--bad は使用できません') "$($r.Code) $($r.Err)"
$r = Invoke-Cli @('--compress', $file)
Check '引数が足りない' ($r.Code -eq 2 -and $r.Err -match '引数の指定が正しくありません') "$($r.Code) $($r.Err)"

Section '説明'
$r = Invoke-Cli @('--help')
Check '日本語' ($r.Code -eq 0 -and $r.Out -match 'Expzip をコマンドラインで使用する方法') "$($r.Code) $($r.Out)"
Set-Settings @{ Language = 'en' }
$r = Invoke-Cli @('--help')
Check '英語' ($r.Code -eq 0 -and $r.Out -match 'Using Expzip from the command line') "$($r.Code) $($r.Out)"
Set-Settings

Section '一覧ファイル'
$comma = Join-Path $src '売上,2026'
New-Item -ItemType Directory -Force -Path $comma | Out-Null
[IO.File]::WriteAllText((Join-Path $comma '集計.txt'), 'sales', $utf8)
$list = Join-Path $src 'list.txt'
$text = "$file,$out\list1.zip`r`n`r`n`"$comma`",$out\売上.zip`r`ndata,..\out\list3.zip`r`n"
Write-Bytes $list $text $utf8
$r = Invoke-Cli @('--list', $list)
Check 'UTF-8: 終了コード 0' ($r.Code -eq 0) "$($r.Code) $($r.Err)"
Check '1 行目' ((Get-ZipNames "$out\list1.zip") -eq 'a.txt')
Check 'カンマを含むパスと日本語' ((Get-ZipNames "$out\売上.zip") -match '集計\.txt')
Check '相対パスは一覧ファイルのフォルダーから' ((Get-ZipNames "$out\list3.zip") -match 'data/x\.txt')
Check '集計を出す' ($r.Out -match '成功 3 個、失敗 0 個') $r.Out

Write-Bytes $list ("`"$comma`",$out\sjis.zip`r`n") ([Text.Encoding]::GetEncoding(932))
$r = Invoke-Cli @('--list', $list)
Check 'Shift_JIS' ($r.Code -eq 0 -and (Test-Path "$out\sjis.zip")) "$($r.Code) $($r.Err)"

[IO.File]::WriteAllBytes($list, ([byte[]](0xEF, 0xBB, 0xBF) + $utf8.GetBytes("$file,$out\bom.zip`r`n")))
$r = Invoke-Cli @('--list', $list)
Check 'BOM 付きの UTF-8' ($r.Code -eq 0 -and (Test-Path "$out\bom.zip")) "$($r.Code) $($r.Err)"

Section '一覧の不備では何も作らない'
Write-Bytes $list "$file,$out\dup1.zip`r`n$folder,$out\DUP1.ZIP`r`n" $utf8
$r = Invoke-Cli @('--list', $list)
Check '同じ書庫が 2 行' ($r.Code -eq 2 -and $r.Err -match '2 行目: 同じ書庫が 1 行目にもあります') "$($r.Code) $($r.Err)"
Check '  作らない' (-not (Test-Path "$out\dup1.zip"))

Write-Bytes $list "$file,$out\dup2.zip`r`n$folder,$out\sub\..\dup2.zip`r`n" $utf8
$r = Invoke-Cli @('--list', $list)
Check '書き方が違っても同じ書庫' ($r.Code -eq 2 -and $r.Err -match '同じ書庫') "$($r.Code) $($r.Err)"

Write-Bytes $list "$file,$out\ok.zip`r`njust-one-column`r`n$file,$out\b.zip,extra`r`n`"$file,$out\c.zip`r`n" $utf8
$r = Invoke-Cli @('--list', $list)
Check '形式の間違いを行番号付きで全部出す' ($r.Code -eq 2 -and $r.Err -match '2 行目' -and $r.Err -match '3 行目' -and $r.Err -match '4 行目') "$($r.Code) $($r.Err)"
Check '  正しい行も作らない' (-not (Test-Path "$out\ok.zip"))

Write-Bytes $list "$folder,$folder\sub\inner.zip`r`n" $utf8
$r = Invoke-Cli @('--list', $list)
Check 'ソースの中の書庫' ($r.Code -eq 2 -and $r.Err -match 'の外に作成') "$($r.Code) $($r.Err)"

Write-Bytes $list "$folder,$out\o1.zip`r`n$file,$folder\o2.zip`r`n" $utf8
$r = Invoke-Cli @('--list', $list)
Check 'ほかの行のソースの中の書庫' ($r.Code -eq 2 -and $r.Err -match '2 行目: 書庫') "$($r.Code) $($r.Err)"

Write-Bytes $list "`r`n  `r`n" $utf8
$r = Invoke-Cli @('--list', $list)
Check '空の一覧' ($r.Code -eq 2 -and $r.Err -match '何も書かれていません') "$($r.Code) $($r.Err)"

$r = Invoke-Cli @('--list', (Join-Path $src 'nothing.txt'))
Check '一覧ファイルが無い' ($r.Code -eq 2 -and $r.Err -match '読み込めませんでした') "$($r.Code) $($r.Err)"

Section '圧縮中の失敗では残りを続ける'
$lockedFolder = Join-Path $src 'locked'
New-Item -ItemType Directory -Force -Path $lockedFolder | Out-Null
$lockedFile = Join-Path $lockedFolder 'held.txt'
[IO.File]::WriteAllText((Join-Path $lockedFolder 'free.txt'), 'free', $utf8)
$hold = [IO.File]::Open($lockedFile, 'Create', 'ReadWrite', 'None')
try {
    Write-Bytes $list "$lockedFolder,$out\fail.zip`r`n$file,$out\after.zip`r`n" $utf8
    $r = Invoke-Cli @('--list', $list)
    Check '終了コード 1' ($r.Code -eq 1) "$($r.Code) $($r.Err)"
    Check '読めないファイルの名前を出す' ($r.Err -match 'held\.txt') $r.Err
    Check '欠けた書庫を作らない' (-not (Test-Path "$out\fail.zip"))
    Check '次の行は作る' (Test-Path "$out\after.zip")
    Check '集計を出す' ($r.Out -match '成功 1 個、失敗 1 個') $r.Out
    Check '一時ファイルが残らない' (@(Get-ChildItem $out -Filter '*.tmp').Count -eq 0)
}
finally { $hold.Dispose() }

Section 'これまでの起動'
$app = Start-Expzip @($target)
Check '書庫を渡すとウィンドウで開く' ($null -ne $app.Window)
Stop-Expzip $app

Complete-Suite
