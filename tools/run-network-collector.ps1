param(
    [string]$ConfigPath = "",
    [switch]$Watch
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$collectorDir = Join-Path $repoRoot "tools\SoteroMap.NetworkCollector"
$projectPath = Join-Path $collectorDir "SoteroMap.NetworkCollector.csproj"
$exampleConfigPath = Join-Path $collectorDir "appsettings.example.json"

if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
    $ConfigPath = Join-Path $collectorDir "appsettings.local.json"
}

if (-not (Test-Path $ConfigPath)) {
    if (-not (Test-Path $exampleConfigPath)) {
        throw "No se encontro la configuracion base: $exampleConfigPath"
    }

    Copy-Item $exampleConfigPath $ConfigPath
    Write-Host "Se creo la configuracion inicial en:" -ForegroundColor Green
    Write-Host $ConfigPath -ForegroundColor Yellow
    Write-Host "Puedes editarla despues si necesitas cambiar URL o rangos." -ForegroundColor Cyan
}

Write-Host ""
Write-Host "=== Colector Windows de telemetria SoteroMap ===" -ForegroundColor Cyan
Write-Host "Proyecto : $projectPath"
Write-Host "Config    : $ConfigPath"
Write-Host "Modo      : $(if ($Watch) { 'agente' } else { 'manual' })"
Write-Host ""
Write-Host "Este proceso pedira tu clave solo si PromptForCredential=true." -ForegroundColor DarkCyan
Write-Host "La clave no se guarda en el archivo." -ForegroundColor DarkCyan
Write-Host ""

Push-Location $repoRoot
try {
    if ($Watch) {
        dotnet run --project $projectPath -- --config $ConfigPath --watch
    }
    else {
        dotnet run --project $projectPath -- --config $ConfigPath
    }
}
finally {
    Pop-Location
}
