param(
    [string]$TaskName = "SoteroMap Network Collector Agent"
)

$ErrorActionPreference = "Stop"
$runRegistryPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$runRegistryName = "SoteroMapNetworkCollectorAgent"

try {
    Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue | Out-Null
}
catch {
}

try {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction Stop
    Write-Host "Agente desinstalado correctamente: $TaskName" -ForegroundColor Green
}
catch {
    Write-Host "No se encontro una tarea llamada '$TaskName'." -ForegroundColor Yellow
}

try {
    Remove-ItemProperty -Path $runRegistryPath -Name $runRegistryName -ErrorAction Stop
    Write-Host "Inicio automatico HKCU removido: $runRegistryName" -ForegroundColor Green
}
catch {
}
