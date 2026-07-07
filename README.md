# SoteroMap API

Backend de SoteroMap construido con ASP.NET Core 8, MVC/Razor, Entity Framework Core y SQLite.

Este repositorio administra el inventario real, usuarios, roles, dashboard, auditoria, formularios de entrega, respaldos de base de datos y la API que consume el mapa frontend.

## Arquitectura rapida

El proyecto se organiza asi:

- `Program.cs`: pipeline, autenticacion, CORS, cookies, seguridad, rutas MVC y API.
- `Controllers/`: login, dashboard, inventario, ubicaciones, cumplimiento, salud, backups y API para el frontend.
- `Services/`: LDAP/LDAPS, MFA, auditoria, respaldos, sincronizacion, formularios PDF y telemetry.
- `Models/`: entidades SQLite y contrato de datos.
- `ViewModels/`: modelos que alimentan vistas Razor y endpoints.
- `Views/`: dashboard MVC, formularios, inventario, ubicaciones y paneles administrativos.
- `Data/`: `AppDbContext`, seed, inicializacion y esquema extendido.
- `Infrastructure/`: utilidades de ruta SQLite, normalizadores y helpers de seguridad.

## Estado actual

- Dashboard principal en `http://localhost:5000/dashboard`.
- Login obligatorio para entrar al dashboard.
- Roles locales `admin`, `editor`, `viewer` y `auditor`.
- Inventario de equipos con asignacion manual a edificio, piso y sala.
- Ubicaciones sincronizadas desde el frontend, con overrides editables desde el dashboard.
- Edificios manuales y geometria editada desde el mapa.
- Historial de cambios con usuario, fecha y detalle.
- Importacion/exportacion de la base SQLite desde el dashboard.
- Formularios de entrega con vista previa PDF y opcion de agregar al inventario.
- Sesion normal con timeout de 15 minutos.
- Opcion `Recordarme` en el login, que deja una cookie persistente para recordar la cuenta y mantener el flujo MFA asociado.
- Panel de cumplimiento para Admin y Auditor con semaforo SGSI, backups, accesos, MFA, HTTPS, Swagger y LDAPS.
- Endpoint tecnico de integridad en `GET /api/health/integrity`.
- Panel tecnico de cumplimiento visible en `GET /dashboard/compliance`.
- Panel de red y riesgo en `GET /dashboard/network-telemetry`.

## Inicio rapido con Docker

Requisito:

- Docker Desktop

Comando unico despues de clonar:

```powershell
docker compose up -d --build
```

Ese comando:

- levanta el backend en `http://localhost:5000`
- usa `dotnet watch` dentro de Docker
- recarga cambios del codigo automaticamente durante desarrollo
- usa la carpeta local `./data` como almacenamiento visible del backend
- usa credenciales seed por defecto si no existe `.env`
- monta carpetas opcionales para data del frontend e importaciones

Para detener:

```powershell
docker compose down
```

Para detener y borrar tambien volumenes temporales de build:

```powershell
docker compose down -v
```

## Rutas utiles

- Dashboard: `http://localhost:5000/dashboard`
- Cumplimiento: `http://localhost:5000/dashboard/compliance`
- Red y riesgo: `http://localhost:5000/dashboard/network-telemetry`
- Login: `http://localhost:5000/Auth/Login`
- Inventario: `http://localhost:5000/dashboard/inventory`
- Ubicaciones: `http://localhost:5000/dashboard/locations`
- Actividad: `http://localhost:5000/dashboard/activity`
- Formulario de entrega: `http://localhost:5000/dashboard/delivery-form`
- Swagger: `http://localhost:5000/swagger`

API interna para consumo de resultados de telemetria:

- Resumen: `GET /api/network-telemetry/office/summary`
- Snapshots: `GET /api/network-telemetry/office/snapshots`
- Snapshot detalle: `GET /api/network-telemetry/office/snapshots/{snapshotId}`
- Equipos del snapshot: `GET /api/network-telemetry/office/snapshots/{snapshotId}/devices`
- Usuarios del snapshot: `GET /api/network-telemetry/office/snapshots/{snapshotId}/users`
- Riesgos del snapshot: `GET /api/network-telemetry/office/snapshots/{snapshotId}/risks`
- Export CSV de equipos: `GET /api/network-telemetry/office/snapshots/{snapshotId}/devices/export`

Nota: `/admin` se mantiene como compatibilidad interna y redirige visualmente a `/dashboard` en solicitudes GET.

## Credenciales por defecto

- Admin: `admin` / `Admin!Sotero2026Map`
- Viewer: `viewer` / `Viewer!Sotero2026Map`

Estas credenciales se pueden cambiar con variables de entorno:

- `SEED_ADMIN_USERNAME`
- `SEED_ADMIN_PASSWORD`
- `SEED_VIEWER_USERNAME`
- `SEED_VIEWER_PASSWORD`

## Autenticacion, roles y MFA

El login puede validar usuarios contra Active Directory por LDAPS y mantiene la autorizacion con roles locales en la base SQLite.

Configuracion principal:

- `AuthSettings:UseLdapAuthentication`: habilita autenticacion LDAP/LDAPS.
- `AuthSettings:AllowLocalBreakGlass`: permite usuario local de emergencia.
- `AuthSettings:BreakGlassUsernames`: usuarios locales permitidos aunque LDAP este activo.
- `AuthSettings:AutoProvisionLdapUsers`: crea usuarios LDAP validos en la tabla local.
- `AuthSettings:DefaultLdapRole`: rol inicial para usuarios LDAP nuevos.
- `LdapSettings:Host`: controlador de dominio principal.
- `LdapSettings:FallbackHost`: controlador alternativo o IP.
- `LdapSettings:Port`: puerto LDAP. Para LDAPS debe ser `636`.
- `LdapSettings:Domain`: dominio NetBIOS.
- `LdapSettings:BaseDn`: base DN del directorio.
- `LdapSettings:UpnSuffixes`: sufijos UPN permitidos.
- `LdapSettings:UseSsl`: debe quedar en `true` para LDAPS.
- `LdapSettings:TrustServerCertificate`: usar solo si el certificado del DC no esta confiado en el entorno.

Roles:

- `admin`: acceso completo, MFA obligatorio.
- `editor`: reservado para edicion operativa controlada.
- `viewer`: solo visualizacion.
- `auditor`: acceso a auditoria, cumplimiento e integridad sin modificar inventario.

MFA se configura en `MfaSettings`. Para administradores, `RequireForAdmins` debe quedar en `true`. Es compatible con Microsoft Authenticator y Google Authenticator mediante TOTP.

## Datos del proyecto

El proyecto usa SQLite y archivos locales de datos.

La carpeta viva del backend es:

- `data/soteromap.db`
- `data/inventory-forms/`
- `data/data-protection-keys/`
- `data/backups/`

Esa carpeta no se versiona en Git. El flujo recomendado es `camino B`:

- el repositorio guarda solo codigo
- el estado real se mueve en un paquete separado

Para mover datos entre equipos:

1. Exporta el paquete en el equipo origen:

```powershell
.\tools\export-project-data-package.ps1
```

Opcionalmente puedes incluir claves MFA/cookies y backups:

```powershell
.\tools\export-project-data-package.ps1 -IncludeBackups -IncludeDataProtectionKeys
```

2. Copia el `.zip` generado en `runtime/data-packages/`.
3. En el otro equipo, clona backend y frontend.
4. Levanta el backend al menos una vez con:

```powershell
docker compose up -d --build
```

5. Restaura el paquete:

```powershell
.\tools\import-project-data-package.ps1 -PackagePath .\runtime\data-packages\TU-PAQUETE.zip
```

6. Reinicia si hace falta:

```powershell
docker compose up -d --build
```

El script tambien restaura los respaldos offline del frontend en `../sotero_map/src/data` cuando ese repo existe al lado del backend.

Si no quieres mover claves MFA entre equipos, no uses `-IncludeDataProtectionKeys`. En ese caso puede hacer falta reenrolar MFA una sola vez en el nuevo entorno.

## Base de datos

La base principal es `data/soteromap.db`.

El dashboard sigue permitiendo exportar/importar la DB manualmente, pero para clonar un entorno completo ahora se recomienda el paquete de datos.

## Frontend offline y respaldos estaticos

El endpoint admin `POST /api/frontend-static-backup/save` actualiza directamente estos archivos del frontend:

- `walking_routes_backup.json`
- `sotero_buildings_backend_backup.json`
- `network_telemetry_backup.json`

Esos archivos tambien entran en el paquete exportado/importado para que el mapa pueda verse parecido incluso antes de reconectar API.

## Variables opcionales

El `docker-compose.yml` permite personalizar rutas y URLs:

- `FRONTEND_APP_URL`: URL del mapa que se muestra en el dashboard.
- `FRONTEND_DATA_HOST_PATH`: carpeta local `src/data` del frontend donde se guardan los respaldos estaticos del mapa.
- `IMPORT_HOST_PATH`: carpeta local para archivos de importacion.
- `PdfSettings:MaxUploadBytes`: tamano maximo permitido para PDFs adjuntos.
- `PdfSettings:AllowedMimeTypes`: MIME permitidos para formularios PDF.
- `NetworkTelemetrySettings:Enabled`: activa o desactiva la vista tecnica de red.
- `NetworkTelemetrySettings:IngestApiKey`: clave para ingesta de telemetry si se usa.

Tambien se controlan desde configuracion:

- `SecuritySettings:ForceHttps`: fuerza HTTPS fuera de desarrollo.
- `SecuritySettings:EnableSwaggerInProduction`: controla exposicion de Swagger en produccion.
- `SecuritySettings:CookieSecurePolicy`: politica `Secure` de cookies.
- `SecuritySettings:CookieSameSite`: politica SameSite.
- `SecuritySettings:ContentSecurityPolicy`: CSP basica.
- `BackupSettings:Enabled`: activa respaldos programados.
- `BackupSettings:Cron`: expresion cron opcional.
- `BackupSettings:IntervalHours`: intervalo si no se usa cron.
- `BackupSettings:RetentionDays`: retencion de backups.
- `BackupSettings:Path`: carpeta donde se almacenan los respaldos.

Si no se definen, se usan:

- `../sotero_map/src/data`
- `./import`

## Sesiones

Configuracion en `SoteroMap.API/appsettings.json`:

```json
"SessionSettings": {
  "IdleMinutes": 15,
  "WarningMinutes": 14,
  "RememberMeDays": 30
}
```

- Una sesion normal muestra aviso antes de expirar y caduca por inactividad.
- Si se marca `Recordarme`, se mantiene la cuenta recordada y la cookie dura `RememberMeDays`, pero el flujo operativo sigue pasando por login/MFA cuando corresponda.

## Funciones principales

### Inventario

- Buscar, filtrar, ordenar y paginar equipos.
- Agregar, editar y eliminar equipos como admin.
- Ver equipo como viewer.
- Asignar equipos a edificios, pisos y salas.
- Detectar incongruencias como seriales parecidos, IP repetida, MAC repetida y datos inconsistentes.
- Fusionar equipos relacionados desde la vista de incongruencias.
- Subir o eliminar PDF de formulario asociado a un equipo.
- Abrir el mapa directo al edificio/equipo asignado.

### Ubicaciones

- Listar edificios sincronizados.
- Buscar sin distinguir mayusculas, minusculas ni tildes.
- Editar overrides de edificio y salas como admin.
- Modificar pisos con seleccion por checkbox.
- Ver equipos asociados por edificio.
- Abrir el mapa directo a la ubicacion.

### Mapa y geometria

El backend expone endpoints para que el frontend pueda:

- crear edificios manuales desde el mapa
- editar geometria de edificios
- mover edificios
- ocultar edificios eliminados
- consultar sesion y permisos
- sincronizar inventario, historial y estado de BDD
- consultar integridad y cumplimiento tecnico

### Formularios

La vista de formulario permite:

- llenar datos minimos obligatorios
- generar vista previa PDF
- imprimir o guardar el PDF
- agregar el equipo al inventario desde la vista previa
- asociar el PDF generado al equipo creado

## Desarrollo local sin Docker

Tambien se puede ejecutar con .NET instalado:

```powershell
dotnet restore
dotnet run --project SoteroMap.API
```

Para el flujo habitual del proyecto se recomienda Docker, porque tambien instala dependencias necesarias para convertir formularios a PDF.

## Relacion con el frontend

Este backend esta pensado para trabajar junto al repositorio `sotero_map`.

- Frontend: mapa, edificios, salas, pisos, geometria visual y experiencia de navegacion.
- Backend: inventario, BDD, usuarios, dashboard, historial, formularios y sincronizacion.

Con ambos levantados:

- Frontend: `http://localhost:8080`
- Backend: `http://localhost:5000/dashboard`

## Agente Windows para telemetria real

Para capturar en vivo la sesion real del equipo, hostname, fabricante, modelo, memoria, disco y ultimo inicio, el repo incluye un agente Windows aparte:

- proyecto: `tools/SoteroMap.NetworkCollector`
- config ejemplo: `tools/SoteroMap.NetworkCollector/appsettings.example.json`
- lanzador rapido: `tools/run-network-collector.ps1`
- instalador automatico: `tools/install-network-collector-agent.ps1`
- desinstalador: `tools/uninstall-network-collector-agent.ps1`

### Modo recomendado institucional

Instalalo una sola vez en el equipo Windows que tenga alcance real a la red interna y permisos para consultar endpoints.

1. Si quieres que el agente quede para todo el equipo, abre PowerShell como administrador en el repo.
2. Ejecuta:

```powershell
.\tools\install-network-collector-agent.ps1
```

Si el script se ejecuta sin privilegios de administrador, crea una tarea para tu usuario actual al iniciar sesion. Si se ejecuta como administrador, la crea como `SYSTEM` al arrancar Windows.

Eso crea una tarea programada de Windows que:

- arranca automaticamente al iniciar el equipo
- mantiene el agente en modo escucha
- responde a las solicitudes del boton `Escanear ahora`
- publica latido de vida hacia el backend

Durante la instalacion, el script ajusta la configuracion del agente para modo desatendido:

- `WatchMode = true`
- `PromptForCredential = false`
- `SharedPath = ..\\..\\runtime\\network-telemetry-agent`
- `ResolveHardware = false` por defecto para que el barrido termine mas rapido y no se atasque por consultas WMI pesadas

Desde ese momento, cualquier usuario que entre a `http://10.8.93.101:5000/dashboard/network-telemetry` puede lanzar el escaneo desde la web, pero el trabajo real lo hara siempre ese agente central.

### Modo manual o de soporte

Si necesitas probarlo sin instalar la tarea programada:

1. Copia `appsettings.example.json` a `appsettings.local.json`.
2. Ajusta `ApiBaseUrl`, `ApiKey` y los rangos `ScanCidrs`.
3. Si quieres que pida la clave al ejecutar, deja `PromptForCredential: true`.
4. Ejecuta desde un Windows con visibilidad real a la red:

```powershell
.\tools\run-network-collector.ps1
```

Equivale a correr manualmente:

```powershell
dotnet run --project .\tools\SoteroMap.NetworkCollector\SoteroMap.NetworkCollector.csproj
```

Opcionalmente puedes indicar otro archivo:

```powershell
dotnet run --project .\tools\SoteroMap.NetworkCollector\SoteroMap.NetworkCollector.csproj -- --config .\tools\SoteroMap.NetworkCollector\appsettings.local.json
```

Notas importantes:

- este colector esta pensado para correr fuera del contenedor Docker, directamente en Windows
- asi puede usar `quser` y WMI remoto para leer sesion activa y datos del equipo
- la clave no se guarda si usas `PromptForCredential`
- el backend recibe el resultado por `POST /api/network-telemetry/ingest`
- en modo agente, el backend escribe la solicitud en `runtime/network-telemetry-agent` y el agente Windows la procesa
- la vista `Red y riesgo` muestra si el agente esta conectado o desconectado segun su ultimo latido

### Flujo operativo

1. El agente Windows queda instalado y corriendo en segundo plano.
2. Un usuario abre `Red y riesgo`.
3. La vista valida si el agente central esta conectado.
4. Si esta conectado, el boton `Escanear ahora` queda habilitado.
5. Al presionarlo, el backend encola la solicitud.
6. El agente ejecuta el escaneo real en Windows.
7. El dashboard se refresca cuando llega el nuevo snapshot.

### Snapshots y tipos de ejecucion

La telemetria guarda snapshots historicos en SQLite y distingue entre:

- ejecuciones manuales desde dashboard
- ejecuciones programadas
- ingestas del agente Windows

Las vistas del dashboard y la API interna permiten revisar una snapshot especifica, volver a la ultima y exportar el detalle para analisis externo.

### Mantencion del agente

- Instalar: `.\tools\install-network-collector-agent.ps1`
- Desinstalar: `.\tools\uninstall-network-collector-agent.ps1`
- Ejecutar manual: `.\tools\run-network-collector.ps1 -Watch`

Si el dashboard muestra el agente como desconectado:

- verifica que el equipo Windows este encendido
- confirma que la tarea programada siga registrada
- revisa que el repo exista en la misma ruta donde se instalo
- valida que ese equipo tenga conectividad a `http://localhost:5000` o a la URL configurada en `ApiBaseUrl`

## Cumplimiento

El panel de cumplimiento concentra:

- estado de base de datos
- HTTPS
- Swagger en produccion
- MFA obligatorio para administradores
- backups recientes
- LDAPS
- eventos criticos y accesos recientes
- integridad tecnica de la BDD
- estado de tablas criticas
- conteo de equipos y admins activos

Para monitoreo tecnico existe `GET /api/health/integrity`, pensado para administradores y auditores.

## Endpoints sensibles

- `POST /api/backups/run`: crea backup manual, solo admin.
- `GET /api/backups/latest`: lista respaldos, solo admin.
- `GET /api/backups/latest/verify`: verifica el ultimo respaldo, solo admin.
- `POST /api/backups/cleanup`: limpia respaldos expirados, solo admin.
- `GET /api/health/integrity`: health e integridad, admin/auditor.
- `GET /api/audit-log`: auditoria formal, admin/auditor.
- `POST /admin/database/upload`: restaura base SQLite, solo admin.
- `GET /admin/database/download`: exporta base SQLite, solo admin.
- `POST /admin/inventory/create`: crea equipo, solo admin.
- `POST /admin/editinventoryitem/{id}`: edita equipo, solo admin.
- `POST /admin/deleteinventoryitem/{id}`: elimina equipo, solo admin.

## Checklist produccion

- Confirmar `ASPNETCORE_ENVIRONMENT=Production`.
- Usar HTTPS real con certificado confiable.
- Mantener `SecuritySettings:ForceHttps=true`.
- Mantener `SecuritySettings:EnableSwaggerInProduction=false`, salvo proteccion explicita por admin.
- Mantener `LdapSettings:UseSsl=true` y puerto `636`.
- Validar confianza del certificado LDAPS del DC.
- Mantener `MfaSettings:RequireForAdmins=true`.
- Crear al menos un admin activo con MFA enrolado.
- Mantener `AuthSettings:AllowLocalBreakGlass=true` solo si existe procedimiento interno controlado.
- Configurar `BackupSettings:Enabled=true`, ruta persistente y retencion.
- Revisar `GET /api/health/integrity` antes de entregar.
- Revisar panel `Cumplimiento` y eventos criticos recientes.
- No versionar la base SQLite real ni archivos PDF productivos.

## Mantenimiento diario

- Si cambias codigo mientras Docker esta corriendo, revisa que el contenedor se haya recompilado o recreado.
- Si una vista Razor muestra error viejo, suele bastar con reconstruir la imagen o reiniciar el contenedor del backend.
- Para respaldos entre equipos, usa exportacion/importacion desde el dashboard, no copies la base dentro del repo.
