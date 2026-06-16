param(
    [string]$TaskName = "SoteroMap Network Collector Agent",
    [string]$ConfigPath = ""
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$runnerPath = Join-Path $repoRoot "tools\run-network-collector.ps1"
$collectorDir = Join-Path $repoRoot "tools\SoteroMap.NetworkCollector"
$exampleConfigPath = Join-Path $collectorDir "appsettings.example.json"

if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
    $ConfigPath = Join-Path $collectorDir "appsettings.local.json"
}

if (-not (Test-Path $ConfigPath)) {
    Copy-Item $exampleConfigPath $ConfigPath
    Write-Host "Se creo la configuracion inicial del agente en:" -ForegroundColor Green
    Write-Host $ConfigPath -ForegroundColor Yellow
}

$config = Get-Content $ConfigPath -Raw | ConvertFrom-Json
$config.WatchMode = $true
$config.PromptForCredential = $false
$config.SharedPath = "..\\..\\runtime\\network-telemetry-agent"
$config | ConvertTo-Json -Depth 10 | Set-Content $ConfigPath -Encoding UTF8

$currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
$currentPrincipal = New-Object Security.Principal.WindowsPrincipal($currentIdentity)
$isElevated = $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$runRegistryPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$runRegistryName = "SoteroMapNetworkCollectorAgent"

$escapedRunner = $runnerPath.Replace("'", "''")
$escapedConfig = $ConfigPath.Replace("'", "''")
$arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File '$escapedRunner' -Watch -ConfigPath '$escapedConfig'"
$commandLine = "powershell.exe $arguments"

$action = New-ScheduledTaskAction -Execute "powershell.exe" -Argument $arguments
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -MultipleInstances IgnoreNew
$taskMode = ""

if ($isElevated) {
    $trigger = New-ScheduledTaskTrigger -AtStartup
    $principal = New-ScheduledTaskPrincipal -UserId "SYSTEM" -RunLevel Highest -LogonType ServiceAccount
    $taskMode = "SYSTEM al iniciar Windows"
}
else {
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $currentIdentity.Name
    $principal = New-ScheduledTaskPrincipal -UserId $currentIdentity.Name -RunLevel Limited -LogonType Interactive
    $taskMode = "usuario actual al iniciar sesion"
    Write-Host ""
    Write-Host "No se detectaron privilegios de administrador." -ForegroundColor Yellow
    Write-Host "Se instalara el agente para tu usuario actual ($($currentIdentity.Name))." -ForegroundColor Yellow
    Write-Host "Si luego quieres que corra para todo el equipo, vuelve a ejecutar este script como administrador." -ForegroundColor DarkYellow
}

try {
    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force | Out-Null

    try {
        Start-ScheduledTask -TaskName $TaskName -ErrorAction Stop
        $startedMessage = "La tarea fue iniciada de inmediato."
    }
    catch {
        $startedMessage = "La tarea quedo registrada, pero no pudo iniciarse en este momento. Se ejecutara con su proximo trigger."
    }
}
catch {
    if ($isElevated) {
        Write-Host ""
        Write-Host "No fue posible registrar la tarea programada." -ForegroundColor Red
        Write-Host $_.Exception.Message -ForegroundColor Yellow
        exit 1
    }

    Write-Host ""
    Write-Host "No fue posible registrar la tarea programada para el usuario actual." -ForegroundColor Yellow
    Write-Host "Se usara inicio automatico por registro HKCU como alternativa." -ForegroundColor Yellow

    New-ItemProperty -Path $runRegistryPath -Name $runRegistryName -PropertyType String -Value $commandLine -Force | Out-Null
    Start-Process -FilePath "powershell.exe" -ArgumentList $arguments -WindowStyle Hidden
    $taskMode = "usuario actual por HKCU\\Run"
    $startedMessage = "El agente fue iniciado ahora y volvera a arrancar al iniciar sesion."
}

Write-Host ""
Write-Host "Agente instalado correctamente para inicio automatico." -ForegroundColor Green
Write-Host "Nombre tarea : $TaskName"
Write-Host "Config       : $ConfigPath"
Write-Host "Modo         : $taskMode"
Write-Host "Inicio       : $startedMessage"
Write-Host ""
Write-Host "Desde ahora el boton 'Escanear ahora' del dashboard puede usar este agente central." -ForegroundColor Cyan
