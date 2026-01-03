# Publish script for LASTE-Mate
# Creates a self-contained single-file executable
# Zips the output with version in the filename

param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$projectFile = "LASTE-Mate.csproj"
$baseOutputDir = "publish"

# Extract version from .csproj
$version = "1.0.0"
$csprojContent = Get-Content $projectFile -Raw
if ($csprojContent -match '<Version>([^<]+)</Version>') {
    $version = $matches[1]
}

Write-Host "Publishing LASTE-Mate v$version..." -ForegroundColor Green
Write-Host "Configuration: $Configuration" -ForegroundColor Cyan
Write-Host "Runtime: $Runtime" -ForegroundColor Cyan
Write-Host ""

# Clean previous publish directory
$outputDir = "$baseOutputDir\win-x64"

if (Test-Path $outputDir) {
    Remove-Item -Path $outputDir -Recurse -Force
}

# Build self-contained version
Write-Host "Building self-contained version (includes .NET runtime)..." -ForegroundColor Yellow
$publishArgs = @(
    "publish",
    $projectFile,
    "-c", $Configuration,
    "-r", $Runtime,
    "-o", $outputDir,
    "--self-contained", "true",
    "-p:PublishSingleFile=true"
)

$result = & dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    Write-Host "`n✗ Build failed!" -ForegroundColor Red
    exit $LASTEXITCODE
}

# Create zip file
Write-Host "`nCreating zip archive..." -ForegroundColor Yellow

$zipFile = "$baseOutputDir\LASTE-Mate-v$version-win-x64.zip"

# Remove existing zip file
if (Test-Path $zipFile) {
    Remove-Item -Path $zipFile -Force
}

# Create zip
Compress-Archive -Path "$outputDir\*" -DestinationPath $zipFile -Force

# Get file size
$zipSize = (Get-Item $zipFile).Length / 1MB

Write-Host "`n✓ Publish successful!" -ForegroundColor Green
Write-Host ""
Write-Host "Build output:" -ForegroundColor Cyan
Write-Host "  Directory: $PWD\$outputDir" -ForegroundColor Gray
Write-Host "  Zip: $PWD\$zipFile ($([math]::Round($zipSize, 2)) MB)" -ForegroundColor Gray
Write-Host ""
