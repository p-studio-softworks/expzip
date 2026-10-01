# exe / dll の中の部品を見る (#183)
. "$PSScriptRoot\..\Common.ps1"
Start-Suite 'Pe'
Set-Settings

# 見本はテストしている Expzip 自身。アイコン・バージョン情報・動作の設定を持っている
$program = Join-Path $script:Work '見本.exe'
Copy-Item $script:Exe $program
$library = Join-Path $script:Work '部品.dll'
Copy-Item $script:Exe $library
$archive = New-TestZip (Join-Path $script:Work '資料.zip') ([ordered]@{ 'メモ.txt' = 'memo' })

$app = Start-Expzip @($program)

Section '開く'
$names = @(Get-RowNames $app)
Check '区画と部品のフォルダーが並ぶ' (($names -contains '.text') -and ($names -contains '.rsrc')) ($names -join ', ')
Check '読み取りのみと添える' ((Texts $app.Window) -match '\(exe / dll / 読み取りのみ\)')
Check 'タイトルバーに形式が出る' ($app.Window.Current.Name -eq "$program [exe / dll] - Expzip") $app.Window.Current.Name
Check '追加できない' (-not (ById $app.Window 'AddButton').Current.IsEnabled)
Select-Row $app '.rsrc' | Out-Null
Send-Keys $app '{ENTER}'
$names = @(Get-RowNames $app)
Check '部品の種類が並ぶ' ((@('ICON', 'MANIFEST', 'VERSION') | Where-Object { $names -notcontains $_ }).Count -eq 0) ($names -join ', ')
Send-Keys $app '{BACKSPACE}'

Section '書庫全体を展開する'
$destination = Join-Path $script:Work 'out'
New-Item -ItemType Directory -Force -Path $destination | Out-Null
Push (ById $app.Window 'ExtractButton')
Check '展開先を尋ねる' (Complete-FileDialog $app '書庫全体の展開先を選択' $destination)
$box = Find-MessageBox $app 30000
if ($box) { Close-MessageBox $box 'OK' }
$version = Join-Path $destination '.rsrc\VERSION\1.txt'
$text = if (Test-Path -LiteralPath $version) { Get-Content -LiteralPath $version -Raw -Encoding UTF8 } else { '' }
Check 'バージョン情報は読める文字で出る' ($text -match 'LegalCopyright: Copyright \(c\) \d{4} P studio') $text
$icon = @(Get-ChildItem -LiteralPath (Join-Path $destination '.rsrc\ICON') -Filter '*.ico' -ErrorAction SilentlyContinue)
Check 'アイコンは .ico で出る' ($icon.Count -ge 1)
if ($icon.Count -ge 1) {
    $bytes = [System.IO.File]::ReadAllBytes($icon[0].FullName)
    Check 'アイコンのファイルの形' ($bytes[0] -eq 0 -and $bytes[1] -eq 0 -and $bytes[2] -eq 1 -and $bytes[3] -eq 0)
}

Section '検査'
Push (ById $app.Window 'InspectButton')
Wait-Idle $app 60000
$window = Find-Window $app '検査結果 - 見本.exe' 30000
Check '窓が開く' ($null -ne $window)
if ($window) {
    $headline = (ById $window 'Headline').Current.Name
    Check '問題は無い' ($headline -eq '問題は見つかりませんでした') ((Texts $window) -join ' / ')
    Push (ById $window 'CloseButton')
}
Stop-Expzip $app

Section 'dll'
$app = Start-Expzip @($library)
$names = @(Get-RowNames $app)
Check 'dll も開ける' (($names -contains '.text') -and ($names -contains '.rsrc')) ($names -join ', ')
Stop-Expzip $app

Section '書庫を開いていないときに落とす'
$app = Start-Expzip
$center = Get-WindowCenter $app
Check '落とせる' (Invoke-Drop @($program) $center.X $center.Y)
Wait-Idle $app
Check '新しい書庫を作るか尋ねない' ($null -eq (Find-MessageBox $app 2000))
Check 'タブで開く' ((Get-Tabs $app).Count -eq 1) (Get-Tabs $app).Count
Check '中身が並ぶ' ((Get-RowNames $app) -contains '.rsrc') ((Get-RowNames $app) -join ', ')
Stop-Expzip $app

Section '書庫を開いているときに落とす'
$app = Start-Expzip @($archive)
$center = Get-WindowCenter $app
Invoke-Drop @($program) $center.X $center.Y | Out-Null
Wait-Idle $app
Check 'タブは増えない' ((Get-Tabs $app).Count -eq 1) (Get-Tabs $app).Count
Check '開いている書庫に入る' ((Get-ZipNames $archive) -contains '見本.exe') ((Get-ZipNames $archive) -join ', ')
Check 'ZIP もタイトルバーに形式が出る' ($app.Window.Current.Name -eq "$archive [ZIP] - Expzip") $app.Window.Current.Name
Stop-Expzip $app

Section '後ろに開けない大きなデータが付いた exe (#188)'
# 開けない形式のインストーラーの代わり。Windows に付いている小さな exe の写しの後ろに、意味の無いデータを付け足す
$padded = Join-Path $script:Work 'padded.exe'
$stream = [System.IO.File]::Create($padded)
$image = [System.IO.File]::ReadAllBytes((Join-Path $env:SystemRoot 'System32\find.exe'))
$stream.Write($image, 0, $image.Length)
$stream.Write((New-Object byte[] 300000), 0, 300000)
$stream.Dispose()
$app = Start-Expzip @($padded)
Check '開けていないデータがあると断る' ((Texts $app.Window) -match '\(exe / dll / 後ろのデータは開けません / 読み取りのみ\)')
Stop-Expzip $app

Complete-Suite
