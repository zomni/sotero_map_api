Esta carpeta contiene los datos vivos locales del backend.

No se sube al repositorio principal.

Contenido esperado:

- `soteromap.db`: base SQLite principal
- `inventory-forms/`: PDFs asociados a equipos
- `data-protection-keys/`: claves locales para MFA/cookies
- `backups/`: respaldos generados por el sistema

Para mover el estado a otro equipo, usa los scripts:

- `tools/export-project-data-package.ps1`
- `tools/import-project-data-package.ps1`

Esos scripts generan o restauran un paquete separado del codigo.
