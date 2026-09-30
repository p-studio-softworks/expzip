# exe の隣に置かれた同じ名前の DLL を読み込まない (#185)
. "$PSScriptRoot\..\Common.ps1"
Start-Suite 'Dll'

# テスト用の Expzip をフォルダーごと写し、隣に偽の amsi.dll を置く。
# 偽物は害の無い Windows の DLL (version.dll) の写し。読み込まれても何もしない
$planted = Join-Path $script:Work 'app'
$script:Exe = Join-Path $planted 'Expzip.exe'
Stop-TestExpzip
New-Item -ItemType Directory -Force -Path $planted | Out-Null
Copy-Item (Join-Path $script:AppDir '*') $planted
Copy-Item (Join-Path $env:SystemRoot 'System32\version.dll') (Join-Path $planted 'amsi.dll')
$script:SettingsPath = Join-Path $planted 'Expzip.settings.json'
$script:RulesPath = Join-Path $planted 'Expzip.rules.json'
Remove-Item $script:RulesPath -ErrorAction SilentlyContinue
Set-Settings

$zip = New-TestZip (Join-Path $script:Work 'a.zip') ([ordered]@{ 'a.txt' = 'a' })
$app = Start-Expzip @($zip)

Section '検査でウイルス対策の部品を読み込む'
Push (ById $app.Window 'InspectButton')
Wait-Idle $app 60000
$window = Find-Window $app '検査結果 - a.zip' 30000
Check '検査が終わる' ($null -ne $window)
$amsi = @(Get-Process -Id $app.Process.Id | Select-Object -ExpandProperty Modules |
    Where-Object { $_.ModuleName -eq 'amsi.dll' } | ForEach-Object { $_.FileName })
Check '読み込む' ($amsi.Count -ge 1)
$system = Join-Path $env:SystemRoot 'System32'
Check 'System32 のものを読む' (($amsi | Where-Object { -not $_.StartsWith($system, [StringComparison]::OrdinalIgnoreCase) }).Count -eq 0) ($amsi -join ', ')
Check '隣の偽物は読まない' (($amsi | Where-Object { $_.StartsWith($planted, [StringComparison]::OrdinalIgnoreCase) }).Count -eq 0) ($amsi -join ', ')
if ($window) { Push (ById $window 'CloseButton') }
Stop-Expzip $app

Complete-Suite
