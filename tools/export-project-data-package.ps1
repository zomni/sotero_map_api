param(
    [string]$OutFile = "",
    [switch]$IncludeBackups,
    [switch]$IncludeDataProtectionKeys
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptRoot
$backendDataRoot = Join-Path $repoRoot "data"
$frontendRepoRoot = Join-Path (Split-Path -Parent $repoRoot) "sotero_map"
$frontendDataRoot = Join-Path $frontendRepoRoot "src\data"
$packageRoot = Join-Path $repoRoot "runtime\data-packages"

if (-not (Test-Path $backendDataRoot)) {
    throw "No existe la carpeta data del backend: $backendDataRoot"
}

New-Item -ItemType Directory -Force -Path $packageRoot | Out-Null

if ([string]::IsNullOrWhiteSpace($OutFile)) {
    $OutFile = Join-Path $packageRoot ("soteromap-data-package-{0}.zip" -f (Get-Date -Format "yyyyMMdd-HHmmss"))
}

$tempRoot = Join-Path $env:TEMP ("soteromap-data-export-" + [guid]::NewGuid().ToString("N"))
$stagingRoot = Join-Path $tempRoot "package"
$backendStaging = Join-Path $stagingRoot "backend-data"
$frontendStaging = Join-Path $stagingRoot "frontend-data"

New-Item -ItemType Directory -Force -Path $backendStaging,$frontendStaging | Out-Null

$composeRunning = $false
try {
    $containerId = (docker compose -f (Join-Path $repoRoot "docker-compose.yml") ps -q api 2>$null)
    if (-not [string]::IsNullOrWhiteSpace($containerId)) {
        $composeRunning = $true
        docker compose -f (Join-Path $repoRoot "docker-compose.yml") stop api | Out-Null
    }

    $backendEntries = @("soteromap.db", "inventory-forms")
    if ($IncludeBackups) {
        $backendEntries += "backups"
    }
    if ($IncludeDataProtectionKeys) {
        $backendEntries += "data-protection-keys"
    }

    foreach ($entry in $backendEntries) {
        $source = Join-Path $backendDataRoot $entry
        if (Test-Path $source) {
            Copy-Item $source (Join-Path $backendStaging $entry) -Recurse -Force
        }
    }

    $frontendFiles = @(
        "walking_routes_backup.json",
        "sotero_buildings_backend_backup.json",
        "network_telemetry_backup.json"
    )

    foreach ($file in $frontendFiles) {
        $source = Join-Path $frontendDataRoot $file
        if (Test-Path $source) {
            Copy-Item $source (Join-Path $frontendStaging $file) -Force
        }
    }

    $manifest = [ordered]@{
        exportedAt = (Get-Date).ToString("o")
        backendRepo = $repoRoot
        frontendRepo = $frontendRepoRoot
        includes = [ordered]@{
            database = Test-Path (Join-Path $backendDataRoot "soteromap.db")
            inventoryForms = Test-Path (Join-Path $backendDataRoot "inventory-forms")
            backups = [bool]$IncludeBackups
            dataProtectionKeys = [bool]$IncludeDataProtectionKeys
            frontendBackups = @(
                "walking_routes_backup.json",
                "sotero_buildings_backend_backup.json",
                "network_telemetry_backup.json"
            )
        }
    } | ConvertTo-Json -Depth 6

    Set-Content -Path (Join-Path $stagingRoot "manifest.json") -Value $manifest -Encoding UTF8

    if (Test-Path $OutFile) {
        Remove-Item $OutFile -Force
    }

    Compress-Archive -Path (Join-Path $stagingRoot "*") -DestinationPath $OutFile -CompressionLevel Optimal
    Write-Host "Paquete exportado en: $OutFile" -ForegroundColor Green
}
finally {
    if ($composeRunning) {
        docker compose -f (Join-Path $repoRoot "docker-compose.yml") start api | Out-Null
    }

    if (Test-Path $tempRoot) {
        Remove-Item $tempRoot -Recurse -Force
    }
}
