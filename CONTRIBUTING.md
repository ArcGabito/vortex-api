# Contribuir a Vortex API

Gracias por tu interés. `vortex-api` es el hub de la suite Vortex (ERP, CRM, WMS, POS): identidad, datos
maestros, eventos e integraciones. Es software libre (AGPL-3.0) y las contribuciones son bienvenidas: código,
documentación, pruebas, reportes de errores e ideas.

## Antes de empezar

- **Errores y propuestas**: abre un [issue](https://github.com/ArcGabito/vortex-api/issues) describiendo el
  problema o la necesidad. Para cambios grandes (un endpoint nuevo, un dato maestro, un cambio de contrato),
  discútelo en un issue antes de programar.
- **Vulnerabilidades**: **no** abras un issue público; sigue [SECURITY.md](SECURITY.md).
- **Si construyes un sistema de la suite** y te falta algo en el hub, lee primero
  [docs/SISTEMAS.md](docs/SISTEMAS.md) (sección 10).

## Entorno de desarrollo

Requisitos: [.NET SDK 10](https://dotnet.microsoft.com/download) y Docker.

```bash
git clone https://github.com/ArcGabito/vortex-api && cd vortex-api
docker compose up -d                                       # SQL Server en localhost:1433
dotnet run --project src/VortexApi --launch-profile http   # http://localhost:5299, referencia en /docs
dotnet test                                                # pruebas (no necesitan SQL Server)
```

En `Development` se crean un PlatformAdmin, las claves de los sistemas y la organización **Vortex Demo**; los
usuarios y claves están en [docs/DESCRIPTION.md](docs/DESCRIPTION.md#ejecutar-en-local) y
[docs/SISTEMAS.md](docs/SISTEMAS.md#1-levantar-el-hub-en-local).

| Carpeta | Contenido |
|---|---|
| `src/VortexApi` | La API: `Controllers`, `Domain` (entidades), `Data` (EF Core y migraciones), `Services`, `Security`, `Infrastructure`. |
| `src/VortexApi.Client` | SDK para los sistemas (paquete NuGet `VortexApi.Client`). |
| `tests/VortexApi.Tests` | Pruebas de integración con `WebApplicationFactory` y SQLite en memoria. |
| `infra/` | Plantilla Bicep para Azure (`main.bicep`; `main.json` se genera). |
| `deploy/docker/` | Despliegue con Docker Compose en servidores propios. |
| `docs/` | Documentación. |

## Flujo de trabajo

1. Haz un fork (o una rama, si tienes acceso) desde `main`: `feat/…`, `fix/…`, `docs/…`, `chore/…`.
2. Haz los cambios **con sus pruebas** y actualiza la documentación afectada.
3. Agrega una línea en [CHANGELOG.md](CHANGELOG.md) bajo `## [Unreleased]` (`Added`, `Changed`, `Fixed`,
   `Removed`, `Security`) si el cambio es visible para quien usa el hub o el SDK.
4. Abre un pull request hacia `main` y completa la plantilla.

`main` está protegida:

- El CI debe pasar: **Build y pruebas** e **Imagen Docker** (y conviene que pase **Infraestructura** si tocas
  `infra/` o `deploy/`).
- La rama debe estar al día con `main` y el historial es lineal: los PR se fusionan con *rebase*. Actualiza tu
  rama con `git rebase origin/main` (no con merge).
- Todas las conversaciones de la revisión deben quedar resueltas.

## Convenciones

**Idioma.** Identificadores de código en inglés (`PartyController`, `ChangeSequence`); comentarios, mensajes
de error, documentación XML, docs y mensajes de commit en español. Los nombres de las pruebas son frases en
inglés (`Demo_user_can_log_in_to_every_system`).

**Código.** Sigue el estilo del código que rodea al cambio: C# moderno (constructores primarios, `record` para
contratos, *nullable* habilitado), sin dependencias nuevas salvo que sean necesarias (coméntalo en el PR).
Comentarios solo donde expliquen el *porqué*.

**API.**

- Los errores se lanzan con `ApiException` y un código estable de `ErrorCodes` (`snake_case`): los sistemas
  dependen de esos códigos, **no se renombran**.
- Los cambios de contrato deben ser compatibles: agregar campos opcionales está bien; quitar o cambiar el
  significado de campos, rutas o códigos de error es un cambio incompatible y requiere discutirlo antes.
- Los eventos nuevos se agregan al catálogo (`EventCatalog` en `src/VortexApi/Domain/Integration.cs`) y a la
  tabla de [docs/SISTEMAS.md](docs/SISTEMAS.md#6-eventos-entre-sistemas), con su formato de datos.
- Todo endpoint nuevo necesita política de autorización explícita y pruebas de quién **no** puede usarlo.

**Base de datos.** Los cambios del modelo van con una migración:

```bash
dotnet tool install --global dotnet-ef        # una vez
dotnet ef migrations add <NombreEnPascalCase> --project src/VortexApi
```

Revisa la migración generada antes de subirla. Las migraciones ya publicadas en una versión no se modifican:
se agrega una nueva.

**Pruebas.** Cada cambio de comportamiento lleva una prueba en `tests/VortexApi.Tests`. Las pruebas usan
`VortexApiFactory` (hub completo en memoria con reloj falso) y los helpers `PostOkAsync`, `PutOkAsync`,
`AdminClientAsync`. Antes de abrir el PR: `dotnet test` sin fallos y `dotnet build` sin advertencias nuevas.

**Infraestructura.** Si cambias `infra/main.bicep`, regenera la plantilla del botón "Deploy to Azure":

```bash
az bicep build --file infra/main.bicep --outfile infra/main.json
```

**Commits.** Mensajes en español, en imperativo o descriptivos, con un resumen corto en la primera línea y el
detalle debajo si hace falta (`Agregar organización demo al seed de desarrollo`).

## Versiones

El proyecto sigue [Versionado Semántico](https://semver.org/lang/es/). Las versiones las publica quien mantiene
el repo: actualiza `Directory.Build.props` y el CHANGELOG, y crea el tag `vX.Y.Z`; el workflow **Release**
publica la imagen en `ghcr.io/arcgabito/vortex-api` y el SDK en NuGet.

## Licencia

Al contribuir aceptas que tu contribución se publique bajo la licencia del proyecto,
[AGPL-3.0-only](LICENSE.md).

## Uso de agentes de IA

Puedes usar asistentes de IA para contribuir. Revisa y entiende todo lo que generen: eres responsable del
cambio como si lo hubieras escrito tú, y las pruebas y convenciones aplican igual. Para el contexto del
proyecto, apunta al agente a este archivo y a [docs/SISTEMAS.md](docs/SISTEMAS.md).
