param(
    [string]$Version = "1.0.0",
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "webcam_recorder.csproj"
$sampleProjectPath = Join-Path $repoRoot "SampleClient\SampleClient.csproj"
$buildOutput = Join-Path $repoRoot ("bin\{0}\net452" -f $Configuration)
$sampleBuildOutput = Join-Path $repoRoot ("SampleClient\bin\{0}\net452" -f $Configuration)
$distDir = Join-Path $repoRoot "installer\dist"
$tmpRoot = Join-Path $repoRoot "tmp_build"
$stageDir = Join-Path $tmpRoot ("zip-{0}" -f (Get-Date -Format "yyyyMMdd-HHmmss"))
$packageRoot = Join-Path $stageDir "webcam_recorder"

$zipName = "webcam_recorder-ntservice-{0}.zip" -f $Version
$zipPath = Join-Path $distDir $zipName

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory = $true)]
        [scriptblock]$Command,
        [Parameter(Mandatory = $true)]
        [string]$ErrorMessage
    )

    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw $ErrorMessage
    }
}

Write-Host "Restoring packages..."
Invoke-CheckedCommand -Command { dotnet restore $projectPath } -ErrorMessage "dotnet restore failed."
Invoke-CheckedCommand -Command { dotnet restore $sampleProjectPath } -ErrorMessage "dotnet restore (SampleClient) failed."

Write-Host "Building project..."
Invoke-CheckedCommand -Command { dotnet build $projectPath -c $Configuration } -ErrorMessage "dotnet build failed."
Invoke-CheckedCommand -Command { dotnet build $sampleProjectPath -c $Configuration } -ErrorMessage "dotnet build (SampleClient) failed."

if (-not (Test-Path -LiteralPath $buildOutput)) {
    throw "Build output not found: $buildOutput"
}

if (-not (Test-Path -LiteralPath $sampleBuildOutput)) {
    throw "SampleClient build output not found: $sampleBuildOutput"
}

New-Item -ItemType Directory -Force -Path $packageRoot | Out-Null
New-Item -ItemType Directory -Force -Path $distDir | Out-Null

Write-Host "Copying runtime files..."
Copy-Item -Path (Join-Path $buildOutput "*") -Destination $packageRoot -Recurse -Force

Write-Host "Copying SampleClient files..."
$samplePackageDir = Join-Path $packageRoot "SampleClient"
New-Item -ItemType Directory -Force -Path $samplePackageDir | Out-Null
Copy-Item -Path (Join-Path $sampleBuildOutput "*") -Destination $samplePackageDir -Recurse -Force

# Ensure repo-level config is used by default in the package.
$rootConfig = Join-Path $repoRoot "appsettings.json"
if (Test-Path -LiteralPath $rootConfig) {
    Copy-Item -LiteralPath $rootConfig -Destination (Join-Path $packageRoot "appsettings.json") -Force
}

# Add NT service helper scripts.
$scriptsDir = Join-Path $packageRoot "scripts"
New-Item -ItemType Directory -Force -Path $scriptsDir | Out-Null
Copy-Item -LiteralPath (Join-Path $repoRoot "scripts\install-ntservice.ps1") -Destination (Join-Path $scriptsDir "install-ntservice.ps1") -Force
Copy-Item -LiteralPath (Join-Path $repoRoot "scripts\uninstall-ntservice.ps1") -Destination (Join-Path $scriptsDir "uninstall-ntservice.ps1") -Force

# Include README instructions with the package.
$readmePath = Join-Path $repoRoot "README.md"
if (Test-Path -LiteralPath $readmePath) {
    Copy-Item -LiteralPath $readmePath -Destination (Join-Path $packageRoot "README.md") -Force
}

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

Write-Host "Creating ZIP package..."
Compress-Archive -Path (Join-Path $packageRoot "*") -DestinationPath $zipPath -CompressionLevel Optimal

Write-Host ""
Write-Host ("ZIP created: {0}" -f $zipPath)
