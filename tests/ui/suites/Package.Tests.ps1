# アプリのパッケージ (MSIX / AppX) を開く (#182)
. "$PSScriptRoot\..\Common.ps1"
Start-Suite 'Package'
Set-Settings

# MSIX の中の名前は、空白や日本語などが %20 のような形で入っている。
# makeappx で作ったものと同じ形にする
$package = New-TestZip (Join-Path $script:Work 'アプリ.msix') ([ordered]@{
    'AppxManifest.xml'                                          = '<Package />'
    '[Content_Types].xml'                                       = '<Types />'
    'sub%20dir/%E6%97%A5%E6%9C%AC%E8%AA%9E/a%20b%23%25%2B.txt' = 'naka'
})
$bundle = New-TestZip (Join-Path $script:Work 'まとめ.msixbundle') ([ordered]@{
    'AppxMetadata/AppxBundleManifest.xml' = '<Bundle />'
    'アプリ.msix'                         = [System.IO.File]::ReadAllBytes($package)
})

function Sorted($Names) { return (@($Names) | Sort-Object) -join ', ' }

$app = Start-Expzip @($package)

Section '開く'
$names = Sorted (Get-RowNames $app)
Check '元の名前で並ぶ' ($names -eq (Sorted @('sub dir', '[Content_Types].xml', 'AppxManifest.xml'))) $names
Check '読み取りのみと添える' ((Texts $app.Window) -match 'MSIX は読み取りのみに対応')
Check '追加できない' (-not (ById $app.Window 'AddButton').Current.IsEnabled)

Section '検査'
Push (ById $app.Window 'InspectButton')
Wait-Idle $app 60000
$window = Find-Window $app '検査結果 - アプリ.msix' 30000
Check '窓が開く' ($null -ne $window)
if ($window) {
    # 名前を戻し忘れると、中身と索引の食い違いなどとして誤って指摘される
    Check '問題は無い' ((ById $window 'Headline').Current.Name -eq '問題は見つかりませんでした') (ById $window 'Headline').Current.Name
    Push (ById $window 'CloseButton')
}

Section '書庫全体を展開する'
$destination = Join-Path $script:Work 'out'
New-Item -ItemType Directory -Force -Path $destination | Out-Null
Push (ById $app.Window 'ExtractButton')
Check '展開先を尋ねる' (Complete-FileDialog $app '書庫全体の展開先を選択' $destination)
$box = Find-MessageBox $app 30000
if ($box) {
    Check '件数' ($box.Text -match '展開したファイル: 3 個') $box.Text
    Close-MessageBox $box 'OK'
}
$file = Join-Path $destination 'sub dir\日本語\a b#%+.txt'
Check 'フォルダーも元の名前で出る' ((Test-Path -LiteralPath $file) -and (Get-Content -LiteralPath $file -Raw) -eq 'naka') ((Get-ChildItem $destination -Recurse | ForEach-Object { $_.Name }) -join ', ')

Section 'フォルダーに入る'
Select-Row $app 'sub dir' | Out-Null
Send-Keys $app '{ENTER}'
Check '日本語のフォルダー' (((Get-RowNames $app) -join ', ') -eq '日本語') ((Get-RowNames $app) -join ', ')
Select-Row $app '日本語' | Out-Null
Send-Keys $app '{ENTER}'
Check '記号の入った名前' (((Get-RowNames $app) -join ', ') -eq 'a b#%+.txt') ((Get-RowNames $app) -join ', ')

Section '選択した項目を展開する'
$picked = Join-Path $script:Work 'picked'
New-Item -ItemType Directory -Force -Path $picked | Out-Null
Select-Row $app 'a b#%+.txt' | Out-Null
Push (ById $app.Window 'ExtractButton')
Check '展開先を尋ねる' (Complete-FileDialog $app '選択した項目の展開先を選択' $picked)
$box = Find-MessageBox $app 30000
if ($box) { Close-MessageBox $box 'OK' }
$file = Join-Path $picked 'a b#%+.txt'
Check '元の名前で出る' ((Test-Path -LiteralPath $file) -and (Get-Content -LiteralPath $file -Raw) -eq 'naka') ((Get-ChildItem $picked -Recurse | ForEach-Object { $_.Name }) -join ', ')
Stop-Expzip $app

Section 'まとめたパッケージの中のパッケージ'
$app = Start-Expzip @($bundle)
$names = Sorted (Get-RowNames $app)
Check '中のパッケージが並ぶ' ($names -eq (Sorted @('AppxMetadata', 'アプリ.msix'))) $names
Select-Row $app 'アプリ.msix' | Out-Null
Send-Keys $app '{ENTER}'
Wait-Idle $app
$names = Sorted (Get-RowNames $app)
Check '中のパッケージを開ける' ($names -eq (Sorted @('sub dir', '[Content_Types].xml', 'AppxManifest.xml'))) $names
Stop-Expzip $app

Section '戻すと書庫の外を指す名前'
# %2E%2E は「..」。戻したあとの名前で、ほかの形式と同じく弾く
$risky = New-TestZip (Join-Path $script:Work 'ayashii.msix') ([ordered]@{
    '%2E%2E/evil.txt' = 'x'
    'ok.txt'          = 'ok'
})
$app = Start-Expzip @($risky)
$warning = ById $app.Window 'SuspiciousWarningText'
Check '開いた時点で知らせる' ($warning -and $warning.Current.Name -match '^パスが通常と異なる項目が \d+ 個あります$') $(if ($warning) { $warning.Current.Name })
$outside = Join-Path $script:Work 'risky\out'
New-Item -ItemType Directory -Force -Path $outside | Out-Null
Push (ById $app.Window 'ExtractButton')
Complete-FileDialog $app '書庫全体の展開先を選択' $outside | Out-Null
$box = Find-MessageBox $app 30000
if ($box) { Close-MessageBox $box 'OK' }
Check '外に書き出さない' (-not (Test-Path (Join-Path $script:Work 'risky\evil.txt')))
Check 'ほかは出る' (Test-Path (Join-Path $outside 'ok.txt'))
Stop-Expzip $app

Complete-Suite
