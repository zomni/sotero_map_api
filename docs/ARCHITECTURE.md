# Arquitectura de Codigo - SoteroMap API

Este documento describe la arquitectura practica del backend `sotero_map_api`. La idea es que sirva como guia diaria para mantener, extender y diagnosticar el proyecto sin tener que reconstruir mentalmente todo el sistema cada vez.

## Resumen ejecutivo

SoteroMap API es un backend ASP.NET Core 8 con MVC/Razor, API REST, Entity Framework Core y SQLite. Cumple tres funciones principales:

- Administrar el inventario real de equipos, usuarios, permisos, auditoria, respaldos y formularios.
- Exponer endpoints consumidos por el mapa frontend `sotero_map`.
- Mantener datos sincronizados o editables sobre edificios, salas, geometria y rutas caminables.

El backend no es solo una API: tambien renderiza el dashboard administrativo con Razor. Por eso conviven controladores MVC con vistas (`AdminController`, `AuthController`) y controladores JSON para el mapa o integraciones (`InventoryImportController`, `WalkingRoutesController`, `ManualBuildingsController`, etc.).

## Stack principal

- Plataforma: ASP.NET Core 8.
- UI backend: Razor MVC.
- Persistencia: SQLite con Entity Framework Core.
- Auth: cookies ASP.NET Core, AD/LDAPS opcional, usuarios locales break-glass.
- MFA: TOTP compatible con Microsoft Authenticator y Google Authenticator.
- Auditoria: tabla `AuditLogEntries`.
- Backups: servicio programado sobre SQLite y tabla `BackupHistories`.
- Documentos: plantilla Word `Templates/FormularioEntregaEquipo.docx` y conversion PDF con LibreOffice.
- Despliegue local recomendado: Docker Compose.

## Estructura de carpetas

```text
SoteroMap.API/
  Controllers/       MVC y API REST.
  Data/              DbContext, seed inicial y normalizadores de esquema.
  Infrastructure/    Utilidades transversales de infraestructura.
  Models/            Entidades persistentes y constantes de dominio.
  Services/          Logica de negocio reutilizable.
  Templates/         Plantillas DOCX para formularios.
  ViewModels/        Modelos para vistas Razor y requests complejos.
  Views/             Vistas Razor del dashboard y login.
  Program.cs         Bootstrap, DI, middleware, auth, seguridad y rutas.
```

## Bootstrap y middleware

El arranque vive en `SoteroMap.API/Program.cs`.

Responsabilidades clave:

- Lee configuraciones `SecuritySettings`, `CorsSettings`, `MfaSettings`, `SessionSettings`, `BackupSettings` y otras.
- Configura MVC con Razor Runtime Compilation en Development.
- Configura Swagger, cache, `IHttpContextAccessor`, CORS y cookies de auth.
- Configura dos cookies: `SoteroMap.Auth` para sesion final y `SoteroMap.MfaPending` para flujo intermedio de MFA.
- Resuelve la ruta SQLite con `SqliteDatabasePathResolver`.
- Registra servicios de dominio mediante DI.
- Crea o migra la base con `EnsureCreated`/`Migrate`.
- Ejecuta `ExtendedSchemaInitializer.EnsureAsync` para columnas/tablas agregadas fuera de migraciones EF formales.
- Ejecuta `SeedData.InitializeAsync` y `BackendAuthService.EnsureSeedUsersAsync`.
- Agrega headers de seguridad: CSP, X-Frame-Options, X-Content-Type-Options, Referrer-Policy y Permissions-Policy.
- Hace rewrite visual entre `/dashboard` y `/admin` para mantener URLs amigables.
- Audita respuestas 403 autenticadas.

### Rutas `/dashboard` y `/admin`

El dashboard se presenta al usuario como `/dashboard`, pero internamente la mayoria de acciones MVC estan en `AdminController`, que usa rutas `/admin/...`.

Regla importante:

- GET a `/admin/...` redirige visualmente a `/dashboard/...`.
- `/dashboard/...` se reescribe internamente a `/admin/...`.

Cuando agregues una nueva vista administrativa, verifica ambas cosas:

- Que la ruta real del action exista.
- Que los links del layout apunten a una URL coherente para usuario final.

## Modelo de datos

La base vive en SQLite y se modela en `AppDbContext`.

### Inventario

- `ImportedInventoryItems`: inventario real importado, creado manualmente o generado desde formulario.
- `InventoryAliasRules`: reglas para mapear ubicaciones textuales del inventario a edificios/salas.
- `SyncedEquipment`: modelo historico/sincronizado de equipos por edificio/sala. Actualmente el inventario principal operativo es `ImportedInventoryItems`.

Campos importantes de `ImportedInventoryItems`:

- `SerialNumber`: identificador prioritario del equipo.
- `InferredCategory`: categoria normalizada (`pc`, `printer`, `scanner`, `other`, etc.).
- `InferredStatus`: estado operativo.
- `AssignedBuildingExternalId`: edificio asignado en el mapa.
- `AssignedRoomExternalId`: sala asignada.
- `AssignedFloor`: piso asignado.
- `DeliveryFormPdfFileName`: PDF asociado al equipo, si existe.
- `MatchedBuildingExternalId` y `MatchedRoomExternalId`: sugerencias automaticas de conciliacion.
- `AssignmentUpdatedAtUtc`: fecha de ultima asignacion manual.

### Ubicaciones

- `SyncedBuildings`: edificios sincronizados o administrados por el backend.
- `SyncedRooms`: salas ligadas a edificios.
- `ManualBuildings`: edificios creados desde el mapa.
- `BuildingGeometryOverrides`: poligonos editados o movidos desde el mapa.

Regla de diseño:

- El frontend mantiene la experiencia visual y geometria base.
- El backend guarda overrides, eliminaciones, edificios manuales y metadatos editables.
- El frontend mezcla ambas fuentes.

### Rutas caminables

- `WalkingRouteNodes`: nodos de rutas.
- `WalkingRouteEdges`: tramos entre nodos.

Las rutas soportan:

- Creacion por puntos.
- Dibujo libre desde frontend.
- Union/split de vertices.
- Conexion manual a edificios.
- Estado de ruta (`open`, `closed`, etc.).
- Respaldo local/estatico en frontend.

### Seguridad y auditoria

- `AuthUsers`: usuarios locales/provisionados desde LDAP.
- `AuditLogEntries`: auditoria formal.
- `BackupHistories`: historial de backups programados o manuales.

## Roles

Los roles estan en `Models/AppRoles.cs`.

- `admin`: acceso completo, MFA obligatorio.
- `editor`: reservado para ediciones operativas controladas.
- `viewer`: visualizacion.
- `auditor`: auditoria, cumplimiento e integridad sin modificar inventario.

Cuando agregues una accion sensible, usa `[Authorize(Roles = AppRoles.Admin)]` o combina roles con interpolacion:

```csharp
[Authorize(Roles = $"{AppRoles.Admin},{AppRoles.Auditor}")]
```

## Autenticacion y sesion

### Flujo de login

La entrada principal esta en `AuthController`.

Flujo general:

1. Usuario envia credenciales.
2. `BackendAuthService` decide si valida contra AD/LDAPS o usuario local break-glass.
3. Si el usuario es valido, se revisa rol local.
4. Si requiere MFA, se emite cookie `MfaPending` y se redirige a flujo MFA.
5. Si MFA no aplica o fue validado, se emite cookie final `SoteroMap.Auth`.
6. Se audita login exitoso, fallido, MFA y logout.

### LDAPS

La validacion AD esta encapsulada en `LdapAuthenticationService`.

Configuracion relevante:

- `LdapSettings:Host`
- `LdapSettings:FallbackHost`
- `LdapSettings:Port`
- `LdapSettings:Domain`
- `LdapSettings:BaseDn`
- `LdapSettings:UpnSuffixes`
- `LdapSettings:UseSsl`
- `LdapSettings:TrustServerCertificate`

Regla operativa:

- Produccion debe usar LDAPS puerto 636.
- `TrustServerCertificate` solo debe usarse como excepcion temporal si el certificado del DC no esta confiado.

### MFA

`MfaService` administra enrolamiento, secretos protegidos y validacion TOTP.

Puntos de cuidado:

- Admin requiere MFA si `MfaSettings:RequireForAdmins=true`.
- El secreto MFA se guarda protegido, no en texto simple.
- Si el equipo servidor tiene desfase horario, TOTP puede fallar.
- `MfaSettings:WindowSteps` da tolerancia; no lo subas demasiado en produccion.

## Seguridad base

La seguridad transversal se configura desde `Program.cs` y `appsettings.json`.

Controles implementados:

- HTTPS forzado fuera de desarrollo.
- HSTS en produccion.
- Cookies `HttpOnly`.
- Cookies `Secure` segun configuracion/entorno.
- SameSite configurable.
- CORS restrictivo por origen.
- CSP basica.
- X-Frame-Options DENY.
- X-Content-Type-Options nosniff.
- Referrer-Policy.
- Permissions-Policy.
- Swagger restringido segun entorno/configuracion.

Regla diaria:

- Si agregas una integracion frontend nueva, actualiza `CorsSettings:AllowedOrigins`.
- Si agregas scripts externos o recursos remotos, revisa `SecuritySettings:ContentSecurityPolicy`.

## Controladores principales

### `AdminController`

Es el controlador mas grande del proyecto. Maneja dashboard, inventario, ubicaciones, actividad, formularios, DB import/export y cumplimiento.

Responsabilidades:

- `Index`: dashboard principal.
- `Compliance`: panel SGSI/cumplimiento.
- Base de datos: descargar, subir, restaurar y eliminar respaldos.
- Actividad: filtro de auditoria.
- Formulario de entrega: generar PDF, preview y crear equipo desde formulario.
- Ubicaciones: listar/editar edificios y salas sincronizadas.
- Inventario: listar, crear, editar, eliminar, asignar, limpiar asignacion, fusionar incongruencias.
- PDF de equipo: subir, ver, eliminar.

Recomendacion:

- No agregues nueva logica compleja directamente aqui si puede vivir en un servicio.
- Para cambios repetitivos de inventario, preferir helpers privados o servicios.
- Toda accion admin que modifique datos debe auditarse.

### `AuthController`

Maneja:

- Login.
- Logout.
- AccessDenied.
- MFA setup.
- MFA verify.
- Estado de sesion para frontend: `/api/auth/session`.

### `InventoryImportController`

Expone datos de inventario al mapa:

- `GET /api/inventory-import/sync-state`: revision global para saber si el mapa debe refrescar.
- `GET /api/inventory-import/items`: items para popup/buscador del mapa.
- `GET /api/inventory-import/building-summary`: conteo por edificio/tipo.
- `POST /api/inventory-import/run`: importacion Excel, admin.

`sync-state` es clave para el contenedor "API activa" del mapa.

### `ManualBuildingsController`

Administra edificios creados desde mapa:

- listar manuales
- crear edificio
- eliminar edificio

### `BuildingGeometryOverridesController`

Guarda overrides de poligonos de edificios existentes.

Se usa cuando el admin edita forma o mueve edificio desde el mapa.

### `WalkingRoutesController`

Administra red caminable:

- `GET /api/walking-routes`: carga nodos/tramos.
- `POST /api/walking-routes/paths`: crea ruta.
- `PUT /api/walking-routes/edges/{externalId}`: edita tramo.
- `PUT /api/walking-routes/nodes/{externalId}`: mueve nodo.
- `POST /api/walking-routes/nodes/{externalId}/split`: separa nodo compartido.
- `POST /api/walking-routes/restore`: restaura red.
- `DELETE /api/walking-routes/edges/{externalId}`: elimina tramo.

Regla importante:

- Las rutas se guardan en backend y el frontend tambien mantiene respaldo local/estatico.
- Si cambias el contrato JSON de rutas, actualiza `sotero_map/src/utils/walkingRouteStorage.js` y editores de ruta.

### `FrontendStaticBackupController`

Permite que el backend guarde respaldos estaticos dentro de `sotero_map/src/data`:

- `walking_routes_backup.json`
- `sotero_buildings_backend_backup.json`

Esto permite que el mapa funcione sin API, por ejemplo en publicacion estatica.

### `BackupsController`

API admin para backups:

- listar ultimos backups
- ejecutar backup manual
- limpiar expirados

### `HealthController`

Endpoint de integridad:

- `GET /api/health/integrity`

Devuelve estado de DB, tablas criticas, equipos, admins activos, backup reciente y eventos criticos.

### Controladores legacy o secundarios

- `LocationsController` y `EquipmentsController`: API CRUD mas antigua basada en `Location`/`Equipment`.
- `SyncedBuildingsController`, `SyncedRoomsController`, `SyncedEquipmentsController`: lectura de entidades sincronizadas.
- `InventoryAliasRulesController`: reglas para matching de inventario.
- `InventoryReconciliationController`: conciliacion inventario/ubicaciones.
- `FrontendSyncController`: sincronizacion frontend/backend.
- `AuditLogController`: consulta de auditoria para mapa/dashboard.

## Servicios principales

### `BackendAuthService`

Orquesta login local/LDAP, provisionamiento de usuarios LDAP y seed de usuarios.

### `LdapAuthenticationService`

Valida credenciales contra AD/LDAPS.

### `MfaService`

Genera secretos TOTP, QR, valida codigos y administra estado MFA.

### `AuditLogService`

Crea entradas formales de auditoria con usuario, IP, user-agent, recurso, resultado, severidad, valor anterior y nuevo.

Usar este servicio cuando:

- Se modifica inventario.
- Se exporta/importa/restaura DB.
- Se sube/descarga/elimina PDF.
- Hay login/logout/MFA/acceso denegado.
- Se cambia una configuracion critica.

### `DatabaseBackupService` y `DatabaseBackupHostedService`

Crean backups SQLite, calculan hash, registran historial y eliminan respaldos expirados.

`DatabaseBackupHostedService` corre en segundo plano segun `BackupSettings`.

### `ExcelInventoryImportService`

Importa inventario Excel y mapea columnas al modelo `ImportedInventoryItem`.

### `InventoryReconciliationService`

Apoya matching/conciliacion de inventario con edificios/salas.

### `FrontendSyncService`

Gestiona sincronizacion entre datos del frontend y tablas sincronizadas.

### `EquipmentDeliveryDocumentService`

Genera documento de entrega desde plantilla Word y lo convierte a PDF.

Puntos de cuidado:

- La plantilla vive en `Templates/FormularioEntregaEquipo.docx`.
- La conversion requiere LibreOffice/`soffice`.
- Cambios de layout de PDF son sensibles: probar visualmente antes de cerrar.

### `BuildingFloorNormalizer`

Normaliza pisos para edificios, incluyendo la regla de piso base y rangos permitidos.

## Vistas Razor

Las vistas viven en `Views`.

### `Views/Auth`

- `Login.cshtml`
- `MfaSetup.cshtml`
- `MfaVerify.cshtml`
- `MfaMethod.cshtml`
- `AccessDenied.cshtml`

### `Views/Admin`

- `Index.cshtml`: dashboard.
- `Equipments.cshtml`: inventario.
- `Locations.cshtml`: edificios/salas.
- `Activity.cshtml`: auditoria.
- `Compliance.cshtml`: cumplimiento.
- `DeliveryForm.cshtml`: formulario.
- `DeliveryFormPreview.cshtml`: preview PDF.
- `CreateInventoryItem.cshtml`, `EditInventoryItem.cshtml`: ABM inventario.
- `EditSyncedBuilding.cshtml`, `EditSyncedRoom.cshtml`: overrides de ubicaciones.
- `InventoryInconsistency.cshtml`: detalle de incongruencias y acciones.

### Layout

`Views/Shared/_Layout.cshtml` concentra:

- Sidebar.
- Usuario logeado.
- Botones comunes.
- Aviso de expiracion de sesion.
- Integracion de scripts del dashboard.

Si agregas una vista nueva, revisa que:

- Este linkeada desde el sidebar si aplica.
- Respete roles.
- Tenga ruta amigable `/dashboard/...` o compatibilidad.

## Flujos principales

### Login AD/LDAPS + MFA admin

```text
Login.cshtml
  -> AuthController.Login POST
  -> BackendAuthService
  -> LdapAuthenticationService o usuario local break-glass
  -> AuthUser local + rol
  -> MfaService si aplica
  -> cookie final SoteroMap.Auth
  -> AuditLogService
```

### Inventario en dashboard

```text
AdminController.Equipments
  -> ImportedInventoryItems
  -> filtros/paginacion/orden
  -> Equipments.cshtml
  -> acciones admin: crear, editar, eliminar, fusionar, subir PDF
  -> AuditLogService
```

### Inventario en mapa

```text
featureDisplay.js / autocompleteSearchBox.js
  -> GET /api/inventory-import/items
  -> GET /api/inventory-import/building-summary
  -> popup edificio / burbuja conteo / busqueda equipo
```

### Ubicaciones editadas

```text
Mapa base JSON
  + SyncedBuildings/SyncedRooms backend
  + ManualBuildings
  + BuildingGeometryOverrides
  -> frontend mezcla y renderiza
```

### Crear edificio desde mapa

```text
manualBuildingEditor.js
  -> POST /api/manual-buildings
  -> ManualBuildings
  -> AuditLogEntries
  -> frontend refresca mapa y cache
```

### Editar/mover edificio desde mapa

```text
buildingGeometryEditor.js
  -> POST /api/building-geometry-overrides
  -> BuildingGeometryOverrides
  -> frontend refresca mapa
```

### Rutas caminables

```text
walkingRouteEditor.js
  -> POST/PUT/DELETE /api/walking-routes
  -> WalkingRouteNodes + WalkingRouteEdges
  -> walkingRouteLayer.js
  -> routePlanner.js calcula ruta mas corta
```

### Respaldo DB

```text
DatabaseBackupHostedService
  -> DatabaseBackupService
  -> copia SQLite
  -> hash
  -> BackupHistories
  -> AuditLogEntries
```

### Subir/restaurar DB

```text
AdminController.UploadDatabase
  -> valida extension SQLite
  -> ValidateSqliteFile
  -> backup previo
  -> reemplaza DB
  -> AuditLogService
```

### Formulario de entrega

```text
DeliveryForm.cshtml
  -> AdminController.DeliveryForm POST
  -> EquipmentDeliveryDocumentService
  -> PDF preview
  -> opcional crear equipo en inventario
  -> PDF asociado a ImportedInventoryItem
```

## Contratos con el frontend

Endpoints que el mapa usa con frecuencia:

- `GET /api/auth/session`
- `GET /api/inventory-import/sync-state`
- `GET /api/inventory-import/items`
- `GET /api/inventory-import/building-summary`
- `GET /api/activity-log/building`
- `GET /api/synced-buildings`
- `GET /api/synced-rooms`
- `GET /api/manual-buildings`
- `GET /api/building-geometry-overrides`
- `GET /api/walking-routes`

Cuando cambies uno de estos contratos:

- Actualiza el frontend correspondiente.
- Revisa `featureDisplay.js`, `soteroSearchMetadata.js`, `buildingBackupStorage.js`, `walkingRouteStorage.js`.
- Actualiza respaldos estaticos si corresponde.

## Backups y archivos

### SQLite

La DB real no debe versionarse. Se mueve entre equipos con export/import desde dashboard.

### PDFs

Los PDFs asociados a equipos se guardan en la carpeta `inventory-forms` junto al archivo SQLite resuelto.

Reglas actuales:

- Extension `.pdf`.
- MIME permitido por `PdfSettings:AllowedMimeTypes`.
- Header real `%PDF-`.
- Tamano maximo `PdfSettings:MaxUploadBytes`.
- Nombre seguro generado por backend.

## Configuracion importante

### `AuthSettings`

Controla LDAP, break-glass y provisionamiento.

### `LdapSettings`

Controla AD/LDAPS.

### `MfaSettings`

Controla TOTP.

### `SessionSettings`

Controla expiracion de sesion y "mantener sesion iniciada".

### `SecuritySettings`

Controla HTTPS, Swagger, cookies, CSP y headers.

### `BackupSettings`

Controla servicio programado de backups.

### `PdfSettings`

Controla subida de PDFs.

## Como agregar una funcionalidad nueva

1. Identifica si es dashboard, API de mapa o servicio transversal.
2. Si modifica datos, agrega auditoria.
3. Si requiere permiso, agrega `[Authorize]` con rol explicito.
4. Si toca frontend, revisa contratos JSON y cache local.
5. Si agrega tabla/campo, actualiza `AppDbContext` y `ExtendedSchemaInitializer`.
6. Si agrega configuracion, documentala en README y `appsettings.json`.
7. Ejecuta `dotnet build`.

## Convenciones de mantenimiento

- Mantener `AdminController` lo mas delgado posible para nuevas funcionalidades.
- Preferir servicios para logica repetible o critica.
- No confiar en datos del frontend para permisos.
- No usar rutas de archivos enviadas por usuario; siempre `Path.GetFileName` o nombres generados.
- No guardar contrasenas AD.
- Auditar acciones criticas.
- Mantener DB fuera de Git.
- Probar Docker si el cambio afecta runtime real.

## Diagnostico rapido

### Dashboard no carga login

Revisar:

- `Views/Auth/Login.cshtml` incluido.
- Contenedor reconstruido.
- Ruta `/Auth/Login`.

### LDAP codigo 81

Revisar:

- Host y fallback.
- Puerto 636.
- Certificado LDAPS confiable.
- DNS/red.
- `TrustServerCertificate` solo para prueba controlada.

### MFA invalido

Revisar:

- Hora servidor.
- Hora telefono.
- QR actual, no entrada vieja en Authenticator.
- `MfaSettings:WindowSteps`.

### Mapa no detecta cambios

Revisar:

- `GET /api/inventory-import/sync-state`.
- Fechas `SyncedAtUtc`, `UpdatedAtUtc`, `AssignmentUpdatedAtUtc`.
- Cache del frontend.

### PDF no genera

Revisar:

- LibreOffice/`soffice` instalado en contenedor.
- Plantilla DOCX presente.
- Permisos de carpeta temporal.

### Backup no aparece

Revisar:

- `BackupSettings:Enabled`.
- `BackupHistories`.
- permisos de escritura en carpeta de DB/backups.
- logs del contenedor.

## Comandos diarios

```powershell
docker compose up -d --build
docker compose down
dotnet build .\SoteroMap.API\SoteroMap.API.csproj
```

## Checklist antes de commit

- Backend compila con `dotnet build`.
- No se versiono SQLite real.
- No se versionaron PDFs productivos.
- Acciones admin tienen `[Authorize(Roles = AppRoles.Admin)]`.
- Cambios criticos generan auditoria.
- README/arquitectura actualizados si cambia un flujo o contrato.
- Si cambia API consumida por mapa, frontend probado con `npm run build`.
