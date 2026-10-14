# Keeps .\latest\ in sync with the newest GitHub release, then starts it. Offline or on error it starts what is there.
$repo   = 'soldernerd/BatteryCharger'
$root   = $PSScriptRoot
$dir    = Join-Path $root 'latest'
$tagFile = Join-Path $dir 'version.txt'
$exe    = Join-Path $dir 'BatteryCharger.exe'
try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $rel = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest" -Headers @{ 'User-Agent' = 'BatteryCharger-launcher' } -TimeoutSec 10
    $have = if (Test-Path $tagFile) { (Get-Content $tagFile -Raw).Trim() } else { '' }
    if ($rel.tag_name -ne $have) {
        if (Get-Process BatteryCharger -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }) {
            Write-Host "BatteryCharger is already running; close it to update to $($rel.tag_name)."
        } else {
            $asset = $rel.assets | Where-Object name -like '*-win-x64.zip' | Select-Object -First 1
            Write-Host "Updating to $($rel.tag_name) ..."
            $zip = Join-Path $env:TEMP $asset.name
            $new = Join-Path $root 'latest.new'
            Invoke-WebRequest $asset.browser_download_url -OutFile $zip -UseBasicParsing
            if (Test-Path $new) { Remove-Item $new -Recurse -Force }
            Expand-Archive $zip -DestinationPath $new -Force
            Remove-Item $zip -Force
            Set-Content (Join-Path $new 'version.txt') $rel.tag_name
            if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
            Move-Item $new $dir
        }
    }
} catch {
    Write-Host "Could not check for updates: $($_.Exception.Message)"
}
if (Test-Path $exe) { Start-Process $exe -WorkingDirectory $dir } else { Write-Host 'No BatteryCharger.exe available.'; Read-Host 'Press Enter' }
