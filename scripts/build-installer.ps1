param(
    [string]$Version = "1.0.0",
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",
    [switch]$CleanStage
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "webcam_recorder.csproj"
$issPath = Join-Path $repoRoot "installer\webcam_recorder.iss"
$buildOutput = Join-Path $repoRoot ("bin\{0}\net452" -f $Configuration)
$stageRoot = Join-Path $repoRoot "installer\stage"
$stageDir = Join-Path $stageRoot ("build-{0}" -f (Get-Date -Format "yyyyMMdd-HHmmss"))
$distDir = Join-Path $repoRoot "installer\dist"
$rootConfig = Join-Path $repoRoot "appsettings.json"

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

if ($CleanStage -and (Test-Path -LiteralPath $stageRoot)) {
    Get-ChildItem -LiteralPath $stageRoot -Force -ErrorAction SilentlyContinue | ForEach-Object {
        if ($_.Name -like "build-*") {
            try {
                Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction Stop
            }
            catch {
                Write-Warning ("Could not remove old stage folder: {0}" -f $_.FullName)
            }
        }
    }
}

New-Item -ItemType Directory -Force -Path $stageDir | Out-Null
New-Item -ItemType Directory -Force -Path $distDir | Out-Null

Write-Host "Restoring packages..."
Invoke-CheckedCommand -Command { dotnet restore $projectPath } -ErrorMessage "dotnet restore failed."

Write-Host "Building project..."
Invoke-CheckedCommand -Command { dotnet build $projectPath -c $Configuration } -ErrorMessage "dotnet build failed."

if (-not (Test-Path -LiteralPath $buildOutput)) {
    throw "Build output not found: $buildOutput"
}

Write-Host "Staging files..."
Copy-Item -Path (Join-Path $buildOutput "*") -Destination $stageDir -Recurse -Force

# Keep the repo-level configuration as the default installed config.
if (Test-Path -LiteralPath $rootConfig) {
    Copy-Item -LiteralPath $rootConfig -Destination (Join-Path $stageDir "appsettings.json") -Force
}

$candidateIscc = @(
    (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
    (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
)

$cmd = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
if ($cmd -and $cmd.Source) {
    $candidateIscc += $cmd.Source
}

$isccPath = $candidateIscc | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1

if (-not $isccPath) {
    throw "ISCC.exe was not found. Install Inno Setup 6 from https://jrsoftware.org/isinfo.php and run this script again."
}

Write-Host "Compiling installer with Inno Setup..."
Invoke-CheckedCommand -Command {
    & $isccPath "/DMyAppVersion=$Version" "/DBuildDir=$stageDir" "/DOutputDir=$distDir" $issPath
} -ErrorMessage "Inno Setup compilation failed."

Write-Host ""
Write-Host "Installer created in: $distDir"
