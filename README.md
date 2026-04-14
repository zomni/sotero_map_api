# sotero_map_api

Backend de SoteroMap con ASP.NET Core 8, MVC, Entity Framework Core y SQLite.

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
- recarga los cambios automaticamente cuando editas archivos del repo
- mantiene la base SQLite en un volumen Docker
- usa credenciales por defecto aunque no exista `.env`

## Rutas utiles

- Login: `http://localhost:5000/Auth/Login`
- Dashboard: `http://localhost:5000/admin`
- Swagger: `http://localhost:5000/swagger`

## Credenciales por defecto

- Admin: `admin` / `Admin!Sotero2026Map`
- Viewer: `viewer` / `Viewer!Sotero2026Map`

## Datos opcionales externos

Si quieres que el backend lea la data real del frontend o los excels desde otra carpeta, puedes definir estas variables de entorno antes de levantar Docker:

- `FRONTEND_DATA_HOST_PATH`
- `IMPORT_HOST_PATH` (solo si luego quieres importar excels desde una carpeta local)

Si no defines esas variables, el proyecto usa por defecto:
- `./frontend-data`
- `./import`

## Detener

```powershell
docker compose down
```

