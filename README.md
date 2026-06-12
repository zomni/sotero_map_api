# SoteroMap API

Backend de SoteroMap construido con ASP.NET Core 8, MVC/Razor, Entity Framework Core y SQLite.

Este repositorio administra el inventario real, usuarios, roles, dashboard, auditoria, formularios de entrega, respaldos de base de datos y la API que consume el mapa frontend.

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
- Opcion `Mantener sesion iniciada`, que evita el timeout visual y usa una cookie persistente.
- Panel de cumplimiento para Admin y Auditor con semaforo SGSI, backups, accesos, MFA, HTTPS, Swagger y LDAPS.
- Endpoint tecnico de integridad en `GET /api/health/integrity`.

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
- mantiene SQLite en un volumen Docker llamado `sqlite_data`
- usa credenciales seed por defecto si no existe `.env`
- monta carpetas opcionales para data del frontend e importaciones

Para detener:

```powershell
docker compose down
```

Para detener y borrar tambien volumenes, incluida la BDD local de Docker:

```powershell
docker compose down -v
```

## Rutas utiles

- Dashboard: `http://localhost:5000/dashboard`
- Login: `http://localhost:5000/Auth/Login`
- Inventario: `http://localhost:5000/dashboard/inventory`
- Ubicaciones: `http://localhost:5000/dashboard/locations`
- Actividad: `http://localhost:5000/dashboard/activity`
- Formulario de entrega: `http://localhost:5000/dashboard/delivery-form`
- Cumplimiento: `http://localhost:5000/admin/compliance`
- Swagger: `http://localhost:5000/swagger`

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

## Base de datos

El proyecto usa SQLite. En Docker, la base queda dentro del volumen `sqlite_data`, no dentro del repositorio.

El repositorio debe mantenerse sin una base real versionada. Para mover datos entre equipos:

1. Entra a `http://localhost:5000/dashboard`.
2. Descarga la DB desde la seccion Base de datos.
3. En el otro equipo, levanta el proyecto.
4. Entra al dashboard.
5. Sube/restaura la DB descargada.

El dashboard crea respaldos automaticos antes de reemplazar la base actual.
Tambien expone el historial de backups y el panel de cumplimiento para administradores y auditores.

## Variables opcionales

El `docker-compose.yml` permite personalizar rutas y URLs:

- `FRONTEND_APP_URL`: URL del mapa que se muestra en el dashboard.
- `FRONTEND_DATA_HOST_PATH`: carpeta local `src/data` del frontend donde se guardan los respaldos estaticos del mapa.
- `IMPORT_HOST_PATH`: carpeta local para archivos de importacion.
- `PdfSettings:MaxUploadBytes`: tamano maximo permitido para PDFs adjuntos.
- `PdfSettings:AllowedMimeTypes`: MIME permitidos para formularios PDF.

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

Si no se definen, se usan:

- `../sotero_map/src/data`
- `./import`

El endpoint admin `POST /api/frontend-static-backup/save` actualiza directamente:

- `walking_routes_backup.json`
- `sotero_buildings_backend_backup.json`

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
- Si se marca `Mantener sesion iniciada`, el timeout visual queda desactivado y la cookie dura `RememberMeDays`.

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

## Cumplimiento

El panel de cumplimiento concentra:

- estado de base de datos
- HTTPS
- Swagger en produccion
- MFA obligatorio para administradores
- backups recientes
- LDAPS
- eventos criticos y accesos recientes

Para monitoreo tecnico existe `GET /api/health/integrity`, pensado para administradores y auditores.

## Endpoints sensibles

- `POST /api/backups/run`: crea backup manual, solo admin.
- `GET /api/backups/latest`: lista respaldos, solo admin.
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
