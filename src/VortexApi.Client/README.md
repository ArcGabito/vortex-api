# VortexApi.Client

SDK para que los sistemas de la suite **Vortex** (ERP, CRM, WMS, POS) usen el hub
[vortex-api](https://github.com/ArcGabito/vortex-api): un usuario para los 4 sistemas, modo sin conexión,
datos maestros compartidos (clientes, artículos, sedes, empresa) y eventos entre sistemas.

| Clase | Para qué |
|---|---|
| `VortexApiClient` | Llamadas HTTP al hub (cada una recibe el access token). |
| `VortexSessionManager` | Login, renovación automática del token, heartbeat, validación sin conexión y cierre de sesión. |
| `VortexSync` | Sincronización offline-first: cola local de salida y lectura de los feeds con handlers. |

```csharp
using VortexApi.Client;
using VortexApi.Client.Sessions;
using VortexApi.Client.Sync;

var client = new VortexApiClient(
    new HttpClient { BaseAddress = new Uri("http://localhost:5299/") },
    new VortexApiClientOptions { ProductCode = "POS", ProductKey = "vxp_pos_dev_key_change_me" });

var sessions = new VortexSessionManager(client, new InMemorySessionStore(), deviceId: "3f0c2a8e-6a1b-4c1e-9d55-0f2b7d8e9a10");
var session = await sessions.LoginAsync("luis@vortex.local", "Suite-Local-2026!", "Caja 1", "windows");
```

Guía completa para construir un sistema de la suite:
[docs/SISTEMAS.md](https://github.com/ArcGabito/vortex-api/blob/main/docs/SISTEMAS.md).

Licencia AGPL-3.0-only.
