# Changelog

Todos los cambios relevantes de este proyecto se documentan en este archivo.

El formato se basa en [Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/)
y el proyecto sigue [Versionado Semántico](https://semver.org/lang/es/).

## [Unreleased]

### Added
- `Dockerfile` multi-etapa (SDK 10.0 → ASP.NET 10.0 *chiseled*, usuario no root, puerto 8080)
  para desplegar en Azure Container Apps.
- `.dockerignore` que excluye artefactos de compilación, pruebas, llaves y configuración local.
- Workflow de GitHub Actions (`.github/workflows/ci.yml`): restore, build y pruebas en cada push y
  pull request a `main`, más build de la imagen Docker.
- Sección de despliegue en Docker / Azure Container Apps en el README.

### Changed
- Con `ForwardedHeaders:Enabled` ya no se aplica `UseHttpsRedirection`: detrás del ingress de
  Azure Container Apps el TLS se termina en el proxy.

## [0.1.0] - 2026-11-01

### Added
- API ASP.NET Core (.NET 10) de usuarios y licencias para Vortex ERP, CRM, WMS y POS.
- Login de las apps con validación de clave de aplicación, credenciales, licencia vigente,
  puesto asignado y límite de equipos por usuario.
- Tokens JWT firmados con RSA (RS256): access token por producto, refresh token rotativo con
  detección de reutilización y license token para operar sin conexión.
- Revocación inmediata: en cada petición se revalidan usuario, *security stamp*, organización,
  equipo y licencia.
- Consola de administración con roles `PlatformAdmin` y `OrgAdmin`: organizaciones, usuarios,
  licencias (suspender, reactivar, revocar), puestos, equipos, rotación de claves de app y auditoría.
- Bloqueo de cuenta por intentos fallidos, política de contraseñas y límite de peticiones por IP.
- Endpoints `/.well-known/jwks.json` y `/.well-known/openid-configuration`.
- Persistencia en SQL Server con EF Core y migración inicial.
- Librería `VortexApi.Client` para apps MAUI con validación offline del license token.
- Pruebas de integración de los flujos de seguridad.
- `docker-compose.yml` con SQL Server para desarrollo local.

[Unreleased]: https://github.com/ArcGabito/vortex-api/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/ArcGabito/vortex-api/releases/tag/v0.1.0
