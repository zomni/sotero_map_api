param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath,
    [switch]$RestoreDataProtectionKeys
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptRoot
$backendDataRoot = Join-Path $repoRoot "data"
$frontendRepoRoot = Join-Path (Split-Path -Parent $repoRoot) "sotero_map"
$frontendDataRoot = Join-Path $frontendRepoRoot "src\data"
$composeFile = Join-Path $repoRoot "docker-compose.yml"

if (-not (Test-Path $PackagePath)) {
    throw "No existe el paquete indicado: $PackagePath"
}

New-Item -ItemType Directory -Force -Path $backendDataRoot,(Join-Path $backendDataRoot "inventory-forms"),(Join-Path $backendDataRoot "backups"),(Join-Path $backendDataRoot "data-protection-keys") | Out-Null

$tempRoot = Join-Path $env:TEMP ("soteromap-data-import-" + [guid]::NewGuid().ToString("N"))
$composeRunning = $false

try {
    Expand-Archive -Path $PackagePath -DestinationPath $tempRoot -Force

    $backendPackageRoot = Join-Path $tempRoot "backend-data"
    $frontendPackageRoot = Join-Path $tempRoot "frontend-data"

    $containerId = (docker compose -f $composeFile ps -q api 2>$null)
    if (-not [string]::IsNullOrWhiteSpace($containerId)) {
        $composeRunning = $true
        docker compose -f $composeFile stop api | Out-Null
    }

    $dbSource = Join-Path $backendPackageRoot "soteromap.db"
    if (Test-Path $dbSource) {
        Copy-Item $dbSource (Join-Path $backendDataRoot "soteromap.db") -Force
    }

    $formsSource = Join-Path $backendPackageRoot "inventory-forms"
    if (Test-Path $formsSource) {
        Remove-Item (Join-Path $backendDataRoot "inventory-forms") -Recurse -Force -ErrorAction SilentlyContinue
        Copy-Item $formsSource (Join-Path $backendDataRoot "inventory-forms") -Recurse -Force
    }

    $backupsSource = Join-Path $backendPackageRoot "backups"
    if (Test-Path $backupsSource) {
        Remove-Item (Join-Path $backendDataRoot "backups") -Recurse -Force -ErrorAction SilentlyContinue
        Copy-Item $backupsSource (Join-Path $backendDataRoot "backups") -Recurse -Force
    }

    if ($RestoreDataProtectionKeys) {
        $keysSource = Join-Path $backendPackageRoot "data-protection-keys"
        if (Test-Path $keysSource) {
            Remove-Item (Join-Path $backendDataRoot "data-protection-keys") -Recurse -Force -ErrorAction SilentlyContinue
            Copy-Item $keysSource (Join-Path $backendDataRoot "data-protection-keys") -Recurse -Force
        }
    }

    if (Test-Path $frontendDataRoot -and (Test-Path $frontendPackageRoot)) {
        Get-ChildItem $frontendPackageRoot -File | ForEach-Object {
            Copy-Item $_.FullName (Join-Path $frontendDataRoot $_.Name) -Force
        }
    }

    Write-Host "Paquete restaurado correctamente." -ForegroundColor Green
    Write-Host "Backend data: $backendDataRoot"
    if (Test-Path $frontendDataRoot) {
        Write-Host "Frontend backups: $frontendDataRoot"
    }
}
finally {
    if ($composeRunning) {
        docker compose -f $composeFile start api | Out-Null
    }

    if (Test-Path $tempRoot) {
        Remove-Item $tempRoot -Recurse -Force
    }
}
