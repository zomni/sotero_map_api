# sotero_map_api

Backend de **SoteroMap** — API REST + panel admin construido con ASP.NET Core 8 MVC, Entity Framework Core y **SQLite** (sin servidor, sin configuración de red).

Funciona como backend del frontend [sotero_map](https://github.com/zomni/sotero_map), sirviendo ubicaciones en formato GeoJSON compatible con Leaflet.

---

## Stack

| Capa | Tecnología |
|---|---|
| Framework | ASP.NET Core 8 MVC |
| ORM | Entity Framework Core 8 |
| Base de datos | SQLite (archivo local `soteromap.db`) |
| Contenedor | Docker |
| Documentación API | Swagger / OpenAPI |

> **¿Por qué SQLite?** No requiere instalar ningún servidor de base de datos. EF Core crea el archivo `.db` automáticamente al iniciar la app.

---

## Estructura del proyecto

```
sotero_map_api/
├── SoteroMap.API/
│   ├── Controllers/
│   │   ├── LocationsController.cs   → API REST /api/locations
│   │   ├── EquipmentsController.cs  → API REST /api/equipments
│   │   └── AdminController.cs       → Panel admin Razor /admin
│   ├── Models/
│   │   ├── Location.cs
│   │   └── Equipment.cs
│   ├── Data/
│   │   ├── AppDbContext.cs
│   │   └── SeedData.cs
│   └── Views/Admin/                 → Razor Views del panel
├── Dockerfile
├── docker-compose.yml
└── README.md
```

---

## Setup — opción A: Docker (recomendado)

```bash
# 1. Clonar
git clone https://github.com/TU_USER/sotero_map_api
cd sotero_map_api

# 2. Levantar (no necesita .env ni configuración previa)
docker compose up --build
```

El archivo `soteromap.db` se crea automáticamente dentro del volumen Docker y persiste entre reinicios.

---

## Setup — opción B: Sin Docker

```bash
cd SoteroMap.API
dotnet run
```

El archivo `soteromap.db` se crea en la carpeta del proyecto. No se necesita ninguna configuración adicional.

---

## Migraciones

La app aplica migraciones automáticamente al iniciar.

Para crear una nueva migración:

```bash
cd SoteroMap.API
dotnet ef migrations add NombreMigracion
```

---

## API Endpoints

| Método | Ruta | Descripción |
|---|---|---|
| GET | `/api/locations` | Lista ubicaciones (GeoJSON) |
| GET | `/api/locations?campus=sotero&floor=0` | Filtrar por campus y piso |
| GET | `/api/locations/{id}` | Detalle de una ubicación |
| POST | `/api/locations` | Crear ubicación |
| PUT | `/api/locations/{id}` | Editar ubicación |
| DELETE | `/api/locations/{id}` | Soft delete |
| GET | `/api/equipments` | Lista equipos |
| GET | `/api/equipments?locationId=1` | Equipos por ubicación |
| GET | `/api/equipments/summary` | Resumen por estado |
| POST | `/api/equipments` | Crear equipo |
| DELETE | `/api/equipments/{id}` | Eliminar equipo |

Documentación interactiva en `/swagger`.

---

## Panel Admin

Disponible en `/admin`:

- **Dashboard** con estadísticas generales
- **Ubicaciones** — listado, filtros, creación y edición
- **Equipos** — listado, filtros por ubicación, creación y eliminación

---

## Integración con sotero_map (frontend)

```js
// Antes (JSON estático):
fetch('/src/data/cs_sotero_0.json')

// Después (API):
fetch('http://localhost:5000/api/locations?campus=sotero&floor=0')
```
