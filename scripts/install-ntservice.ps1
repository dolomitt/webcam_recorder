$ErrorActionPreference = "Stop"

param(
    [Parameter(Mandatory = $true)]
    [string]$InstallDir,
    [string]$ServiceName = "webcam_recorder",
    [string]$DisplayName = "Webcam Recorder",
    [string]$Description = "HTTP webcam recorder service"
)

function Assert-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Administrator rights are required. Run PowerShell as Administrator."
    }
}

function Resolve-RequiredPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "$Label not found: $Path"
    }
    return (Resolve-Path -LiteralPath $Path).Path
}

Assert-Admin

$resolvedInstallDir = Resolve-RequiredPath -Path $InstallDir -Label "Install directory"
$exePath = Join-Path $resolvedInstallDir "webcam_recorder.exe"

if (-not (Test-Path -LiteralPath $exePath)) {
    throw "Application executable not found: $exePath"
}

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    throw "Service '$ServiceName' already exists. Remove it first or use another name."
}

$binPath = '"{0}" --service' -f $exePath
$quotedDisplayName = '"{0}"' -f $DisplayName
$quotedDescription = '"{0}"' -f $Description

Write-Host "Creating service '$ServiceName'..."
sc.exe create $ServiceName binPath= $binPath start= auto DisplayName= $quotedDisplayName | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "sc.exe create failed for service '$ServiceName'."
}

sc.exe description $ServiceName $quotedDescription | Out-Null

# Use LocalSystem by default and auto-start on boot.
sc.exe config $ServiceName obj= LocalSystem start= auto | Out-Null

Write-Host "Service installed successfully."
Write-Host "Start with: sc.exe start $ServiceName"
