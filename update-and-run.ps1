# Keeps .\latest\ in sync with the newest GitHub release, then starts it. Offline or on error it starts what is there.
#
# A release has a small app zip (our own files, ~1 MB) and a large runtime zip (.NET + Windows App SDK, ~60 MB) whose
# name carries a content hash. The runtime is only downloaded when that hash differs from latest\runtime.txt.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'      # the progress bar makes Windows PowerShell 5.1 downloads many times slower
$repo    = 'soldernerd/BatteryCharger'
$root    = $PSScriptRoot
$dir     = Join-Path $root 'latest'
$tagFile = Join-Path $dir 'version.txt'
$rtFile  = Join-Path $dir 'runtime.txt'
$exe     = Join-Path $dir 'BatteryCharger.exe'
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-Zip($asset, $tmp) {
    $zip = Join-Path $tmp $asset.name
    Write-Host ("  downloading {0} ({1:N1} MB) ..." -f $asset.name, ($asset.size / 1MB))
    Invoke-WebRequest $asset.browser_download_url -OutFile $zip -UseBasicParsing
    $zip
}

try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $rel = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest" -Headers @{ 'User-Agent' = 'BatteryCharger-launcher' } -TimeoutSec 10
    $have = if (Test-Path $tagFile) { (Get-Content $tagFile -Raw).Trim() } else { '' }
    if ($rel.tag_name -ne $have) {
        if (Get-Process BatteryCharger -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }) {
            Write-Host "BatteryCharger is already running; close it to update to $($rel.tag_name)."
        } else {
            Write-Host "Updating to $($rel.tag_name) ..."
            $tmp = Join-Path $env:TEMP 'BatteryCharger-update'
            if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
            New-Item -ItemType Directory $tmp | Out-Null

            $appAsset = $rel.assets | Where-Object name -like 'BatteryCharger-app-*-win-x64.zip' | Select-Object -First 1
            $rtAsset  = $rel.assets | Where-Object name -like 'BatteryCharger-runtime-*-win-x64.zip' | Select-Object -First 1
            $fullAsset = $rel.assets | Where-Object { $_.name -like 'BatteryCharger-*-win-x64.zip' -and $_.name -notlike '*-app-*' -and $_.name -notlike '*-runtime-*' } | Select-Object -First 1

            if (Test-Path $tagFile) { Remove-Item $tagFile -Force }       # an interrupted update is redone next time
            if ($appAsset -and $rtAsset) {
                $rtHash = [regex]::Match($rtAsset.name, 'runtime-([0-9a-f]+)-').Groups[1].Value
                $haveRt = if (Test-Path $rtFile) { (Get-Content $rtFile -Raw).Trim() } else { '' }
                if (-not (Test-Path $exe) -or $haveRt -ne $rtHash) {
                    $rtZip = Get-Zip $rtAsset $tmp
                    $appZip = Get-Zip $appAsset $tmp
                    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
                    [IO.Compression.ZipFile]::ExtractToDirectory($rtZip, $dir)
                    Set-Content $rtFile $rtHash
                } else {
                    $appZip = Get-Zip $appAsset $tmp
                    Get-ChildItem $dir -File -Filter 'BatteryCharger.*' | Remove-Item -Force
                    foreach ($d in 'Views', 'Assets') { if (Test-Path (Join-Path $dir $d)) { Remove-Item (Join-Path $dir $d) -Recurse -Force } }
                }
                [IO.Compression.ZipFile]::ExtractToDirectory($appZip, $dir)
            } else {
                # Older release without the split assets: take the full zip.
                $fullZip = Get-Zip $fullAsset $tmp
                if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
                [IO.Compression.ZipFile]::ExtractToDirectory($fullZip, $dir)
            }
            Set-Content $tagFile $rel.tag_name
            Remove-Item $tmp -Recurse -Force
        }
    }
} catch {
    Write-Host "Could not update: $($_.Exception.Message)"
}
if (Test-Path $exe) { Start-Process $exe -WorkingDirectory $dir } else { Write-Host 'No BatteryCharger.exe available.'; Read-Host 'Press Enter' }
