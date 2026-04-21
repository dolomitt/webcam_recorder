$ErrorActionPreference = "Stop"

param(
    [string]$ServiceName = "webcam_recorder"
)

function Assert-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Administrator rights are required. Run PowerShell as Administrator."
    }
}

Assert-Admin

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $existing) {
    Write-Host "Service '$ServiceName' does not exist. Nothing to remove."
    exit 0
}

Write-Host "Stopping service '$ServiceName' (if running)..."
sc.exe stop $ServiceName | Out-Null

Write-Host "Removing service '$ServiceName'..."
sc.exe delete $ServiceName | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Failed to remove service '$ServiceName'."
}

Write-Host "Service removed successfully."
