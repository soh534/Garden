# 1. Chocolatey — must be first so refreshenv is available
if (-not (Get-Command choco -ErrorAction SilentlyContinue)) {
    Write-Host "Installing Chocolatey..."
    Set-ExecutionPolicy Bypass -Scope Process -Force
    [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.ServicePointManager]::SecurityProtocol -bor 3072
    Invoke-Expression ((New-Object System.Net.WebClient).DownloadString('https://community.chocolatey.org/install.ps1'))
} else {
    Write-Host "Chocolatey already installed."
}

# 2. Import Chocolatey profile so refreshenv works in this session
Import-Module "$env:ChocolateyInstall\helpers\chocolateyProfile.psm1"

# 3. Add to $PROFILE so refreshenv works in all future sessions
$chocoImport = 'Import-Module "$env:ChocolateyInstall\helpers\chocolateyProfile.psm1"'
if (-not (Test-Path $PROFILE) -or -not (Select-String -Path $PROFILE -Pattern "chocolateyProfile" -Quiet)) {
    Add-Content -Path $PROFILE -Value "`n$chocoImport"
    Write-Host "Added Chocolatey profile to `$PROFILE."
} else {
    Write-Host "Chocolatey profile already in `$PROFILE."
}

# 4. Install tools
Write-Host "Installing .NET 8 SDK..."
winget install Microsoft.DotNet.SDK.8 --accept-source-agreements --accept-package-agreements

Write-Host "Installing scrcpy..."
winget install Genymobile.scrcpy --accept-source-agreements --accept-package-agreements

# 5. Refresh env vars so PATH changes take effect immediately
refreshenv

# 6. GARDEN_DATA
if (-not $env:GARDEN_DATA) {
    $gardenData = Read-Host "Enter the full path to your GardenData directory"
    [System.Environment]::SetEnvironmentVariable("GARDEN_DATA", $gardenData, "User")
    $env:GARDEN_DATA = $gardenData
    Write-Host "GARDEN_DATA set."
} else {
    Write-Host "GARDEN_DATA already set to: $env:GARDEN_DATA"
}

Write-Host "Setup complete. Run: cd Garden && dotnet run"
