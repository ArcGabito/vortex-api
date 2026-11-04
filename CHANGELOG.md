# Changelog

Todos los cambios relevantes de este proyecto se documentan en este archivo.

El formato se basa en [Keep a Changelog](https://keepachangelog.com/es-ES/1.1.0/)
y el proyecto sigue [Versionado Semántico](https://semver.org/lang/es/).

## [Unreleased]

### Added
- Paquete **`VortexApi.Suite`**, base común de ERP, CRM, WMS y POS: impuestos por país (Perú, LATAM, EE. UU., Reino
  Unido y UE) con desglose por tasa, monedas ISO 4217 con su redondeo, formatos regionales y hora de la empresa,
  permisos por rol, auditoría y textos en español, inglés y portugués. Se empaqueta con el SDK (`scripts/pack-sdk.sh`,
  CI y Release).
- `docs/ROADMAP-SUITE.md`: hoja de ruta internacional de la suite.
- Catálogo de eventos: `crm.record.upserted` y `crm.record.deleted` (replicación de los datos propios del CRM
  entre los equipos de una organización).
- `docs/SISTEMAS.md`: tabla de repositorios de la suite y regla para los eventos internos de un sistema.

### Changed
- Catálogo de eventos: `order.created` también lo publica el WMS (cuando funciona sin ERP ni CRM) y lo consume el ERP.

## [0.4.0] - 2026-11-01

### Added
- **Despliegue reproducible en la infraestructura de cada empresa** (`docs/DESPLIEGUE.md`):
  - Azure: plantilla Bicep (`infra/main.bicep`) con Container Apps, Azure SQL con autenticación solo Entra
    (identidad administrada, sin contraseñas), Key Vault con la llave de firma generada dentro de Azure y
    Log Analytics. Se despliega con `azd up`, Azure CLI o el botón "Deploy to Azure" (`infra/main.json`).
  - Servidores propios u otra nube: `deploy/docker/compose.yaml` con el hub, SQL Server y Caddy (HTTPS automático).
- Workflow **Release**: con un tag `v*` publica la imagen en `ghcr.io/arcgabito/vortex-api` (`:<versión>`,
  `:<mayor>.<menor>`, `:latest`) y el SDK en GitHub Packages y, si existe el secreto `NUGET_API_KEY`, en nuget.org.
- CI: job **Infraestructura** (lint y build de Bicep, `main.json` al día, validación del compose).
- `CONTRIBUTING.md`, `SECURITY.md` y plantilla de pull request.
- Guía `docs/SISTEMAS.md` para construir Vortex ERP, CRM, WMS o POS sobre el hub (también para agentes de IA):
  levantar el hub, referenciar el SDK, sesión, qué datos son del hub, sincronización, formato de los eventos
  (versión 1), reglas para que los sistemas se puedan reemplazar, errores y plantilla de `CLAUDE.md`.
- Organización demo de desarrollo (`Seed:Demo`): **Vortex Demo** con los 4 sistemas habilitados y acceso
  automático, y los usuarios `ana@vortex.local` (OrgAdmin) y `luis@vortex.local`.
- SDK `VortexApi.Client` como paquete NuGet (metadatos, README, documentación XML y símbolos).
  `scripts/pack-sdk.sh` lo empaqueta en un feed local con una versión `-dev` nueva en cada ejecución.
- CI: el paquete del SDK queda como artefacto `sdk-nupkg`.

### Changed
- El README se reduce a una presentación; la descripción técnica pasa a `docs/DESCRIPTION.md`.

### Fixed
- `.dockerignore` sin barras finales: los builders clásicos (p. ej. `az acr build`) copiaban `bin/` y `obj/` locales a
  la imagen y la compilación fallaba con `NETSDK1064`.
- Plantilla de Azure: el script que genera la llave de firma confundía los avisos de la CLI con un secreto
  existente y no creaba la llave. Ahora usa la CLI 2.70.0 (Azure Linux).
- Arranque con varias réplicas a la vez: los datos iniciales se crean bajo un bloqueo de aplicación de SQL Server
  y se reintenta la migración si otra réplica creó la base en el mismo instante.

## [0.3.0] - 2026-11-01

### Added
- **Fase 5: integraciones externas.** Aplicaciones de terceros registradas por el OrgAdmin
  (`/api/admin/integrations`) que se autentican con OAuth 2.0 `client_credentials` en `POST /connect/token`
  (`client_secret_basic` y `client_secret_post`, errores según RFC 6749).
- Scopes `master.read`, `master.write`, `events.read`, `events.publish`, `events.publish:<patrón>` y
  `webhooks.manage`; tokens con un subconjunto de scopes; todo lo escrito queda con `source: "EXT:<slug>"`.
- Revocación inmediata al rotar el secreto, cambiar scopes o desactivar la aplicación.
- Webhooks propios de cada aplicación (`webhooks.manage`), que nunca reciben sus propios eventos.
- Límite de peticiones por aplicación (`RateLimiting:IntegrationPermitsPerMinute`, 429 con `Retry-After`).
- Documentación pública: `/docs` (Scalar) y `/openapi/v1.json` en todos los entornos (`Docs:Enabled`), con el
  esquema OAuth2 y sus scopes; discovery con `token_endpoint`, `grant_types_supported` y `scopes_supported`.
- Guía para desarrolladores `docs/INTEGRACIONES.md` con ejemplos en curl, C#, JavaScript y Python.
- Dependabot para NuGet, GitHub Actions y Docker (`.github/dependabot.yml`).
- Migración `IntegrationClients`.

### Changed
- El catálogo de eventos (`GET /api/events/catalog`) es público.
- Las columnas `Source` de cambios y eventos, y `ReceiverProduct` de webhooks, pasan de 16 a 64 caracteres.
- La documentación interactiva pasa de `/scalar/v1` (solo desarrollo) a `/docs`.
- El límite de peticiones se aplica después de la autenticación (necesario para limitar por aplicación).

## [0.2.0] - 2026-11-01

### Added
- `Directory.Build.props` con la versión común de la solución (0.2.0) y metadatos AGPL-3.0.
- **Fase 4: SDK para las apps MAUI** (`VortexApi.Client`):
  - `VortexSessionManager`: login, renovación automática del access token con refresh de un solo vuelo (evita que
    llamadas concurrentes disparen la detección de reutilización), reintento ante 401, heartbeat que guarda el offline
    token, validación sin conexión con las llaves guardadas, cierre de sesión y evento `SessionEnded`.
    Persistencia mediante `ISessionStore` (la app lo implementa con `SecureStorage`).
  - `VortexSync`: sincronización offline-first. Cola local (outbox) de eventos y escrituras de datos maestros que se
    sube en orden; lectura de los feeds con handlers tipados (`OnItems`, `OnParties`, `OnSites`, `OnCompany`,
    `OnEvent("sale.*")`); conflictos 412 resueltos por `OnConflict`; rechazos definitivos a una lista de fallidos sin
    bloquear la cola; cortes de red sin pérdida (`Completed = false`); cursor que no avanza si un handler falla;
    `RunPeriodicAsync` para sincronizar en segundo plano.
  - `IVortexSyncStore` con implementaciones `InMemorySyncStore` y `JsonFileSyncStore` (escritura atómica).
  - `VortexApiClient`: métodos de datos maestros (`GetMasterChangesAsync`, `GetMasterAsync`, `PutMasterAsync`,
    `DeleteMasterAsync`) y eventos (`GetEventsAsync`, `PublishEventsAsync`), modelos tipados (`VortexParty`,
    `VortexItem`, `VortexSite`, `VortexCompany`, `VortexEvent`) y `VortexJson.Options`.
- **Fase 3: eventos de integración** entre sistemas: `POST /api/events` (idempotente por `id`),
  `POST /api/events/batch` (hasta 100, resultado por evento), feed `GET /api/events?since=&types=&excludeSource=`
  con secuencia correlativa por organización y catálogo `GET /api/events/catalog`.
- Los cambios de datos maestros se publican también como eventos `master.{party,item,site,company}.{created,updated,deleted}`.
- Webhooks por organización (`/api/admin/webhooks`, solo OrgAdmin): firma HMAC-SHA256
  (`X-Vortex-Signature: t=…,v1=…`), entrega al menos una vez con reintentos exponenciales, *lease* para varias
  réplicas, historial de entregas, reencolado manual, rotación de secreto y `receiverProduct` para no recibir los
  eventos propios.
- Protección SSRF para webhooks: rechazo de redes privadas, loopback y metadatos de la nube al registrar y al
  conectar (resolución DNS en `ConnectCallback`), sin redirecciones. Opciones `Webhooks:AllowPrivateNetworks` y
  `Webhooks:AllowInsecureHttp`.
- Migración `IntegrationEvents`.
- **Fase 2: datos maestros** compartidos entre los sistemas, separados por organización:
  clientes/proveedores (`/api/master/parties`), artículos (`/api/master/items`), sedes/almacenes
  (`/api/master/sites`) y datos de la empresa (`/api/master/company`).
- Feed de cambios `GET /api/master/changes?since=` con secuencia correlativa por organización, asignada en orden
  de commit, y el estado completo de cada versión.
- Control de versiones con `ETag` / `If-Match` (412 `version_conflict`), alta idempotente con Id del cliente
  (`PUT /{id}`), borrado lógico con restauración y escrituras sin cambios que no generan versión.
- Validación de RUC (11 dígitos) y DNI (8 dígitos); unicidad de documento, SKU y código de sede
  (409 `duplicate_document`, `duplicate_sku`, `duplicate_code`).
- Políticas `organization-data` (sistemas y OrgAdmin) y `organization-admin`.
- Migración `MasterData`.
- `Dockerfile` multi-etapa (SDK 10.0 → ASP.NET 10.0 *chiseled*, usuario no root, puerto 8080)
  para desplegar en Azure Container Apps.
- `.dockerignore` que excluye artefactos de compilación, pruebas, llaves y configuración local.
- Workflow de GitHub Actions (`.github/workflows/ci.yml`): restore, build y pruebas en cada push y
  pull request a `main`, más build de la imagen Docker.
- Sección de despliegue en Docker / Azure Container Apps en el README.
- Licencia AGPL-3.0, igual que los sistemas de la suite.
- `GET /api/access/products`: sistemas de la suite a los que el usuario puede entrar (selector de sistemas).
- Rol por sistema (`ProductMember.role`, claim `prd_role`), p. ej. `cajero` en POS y `contador` en ERP.
- Asignación automática (`autoAssignUsers`): todo usuario de la organización obtiene acceso en su primer login.
- `VortexApiClient.GetAvailableProductsAsync` en la librería cliente.

### Changed
- **Cambio de enfoque (incompatible con 0.1.0):** la API pasa de ser un gestor de licencias antipiratería
  a ser el hub de la suite (fase 1: identidad y accesos), en coherencia con sistemas open source.
  - `License` → `OrganizationProduct` (sistema habilitado para la organización). Se eliminan `licenseKey`,
    `plan`, `startsAt` y los estados `Suspended`/`Revoked`; se reemplazan por `isEnabled` + `disabledReason`.
  - `LicenseSeat` → `ProductMember`, `DeviceActivation` → `Device`.
  - `expiresAt`, `maxUsers` (antes `maxSeats`) y `maxDevicesPerUser` son opcionales; `null` = permanente / sin límite.
  - `offlineGraceDays` por defecto pasa de 3 a 7 días.
  - Rutas de administración: `/api/admin/licenses/*` → `/api/admin/organizations/{orgId}/products/{code}`
    (`PUT` para habilitar/configurar, `…/members/{userId}`, `…/devices/{deviceId}`).
  - `/api/licensing/heartbeat` → `/api/access/heartbeat`.
  - *License token* → *offline token* (`typ: vortex-offline+jwt`, audiencia `vortex-offline`, claim `acc`).
  - Códigos de error: `no_license` → `product_not_enabled`, `license_suspended`/`license_revoked` → `product_disabled`,
    `license_expired` → `access_expired`, `no_seat` → `no_access`, `seat_limit_reached` → `user_limit_reached`,
    `seats_in_use` → `limit_in_use`.
  - Librería cliente: `VortexLicensingClient` → `VortexApiClient`, `OfflineLicenseValidator` → `OfflineAccessValidator`,
    `VortexLicense` → `VortexAccess`, `IsLicenseDenied` → `IsAccessDenied`.
  - Migración inicial regenerada; base de datos de desarrollo `VortexLicensing` → `VortexHub`.
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

[Unreleased]: https://github.com/ArcGabito/vortex-api/compare/v0.4.0...HEAD
[0.4.0]: https://github.com/ArcGabito/vortex-api/compare/v0.3.0...v0.4.0
[0.3.0]: https://github.com/ArcGabito/vortex-api/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/ArcGabito/vortex-api/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/ArcGabito/vortex-api/releases/tag/v0.1.0
