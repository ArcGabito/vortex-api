# Vortex API

Hub central de la suite **Vortex** (**ERP, CRM, WMS, POS**), escrito en ASP.NET Core (.NET 10).
Los cuatro sistemas son libres y de código abierto (AGPL-3.0). Cada uno funciona por separado, y
este hub permite usarlos juntos o reemplazar uno por otro sin perder usuarios ni datos.

| Fase | Contenido | Estado |
|---|---|---|
| 1. Identidad y accesos | Un usuario para los 4 sistemas, sistemas habilitados por organización, roles por sistema, equipos, modo sin conexión | ✅ |
| 2. Datos maestros | Clientes/proveedores, artículos, sedes/almacenes y datos de la empresa, con feed de cambios | ✅ |
| 3. Eventos de integración | Publicar y consumir eventos entre sistemas (`sale.completed`, `stock.adjusted`, …) y webhooks | ✅ |
| 4. Sincronización en el SDK | Módulo de sincronización para las apps MAUI | Pendiente |

## Estructura

| Proyecto | Descripción |
|---|---|
| `src/VortexApi` | Web API (controladores, EF Core + SQL Server, JWT RS256). |
| `src/VortexApi.Client` | Librería para las apps MAUI: login, refresh, heartbeat, selector de sistemas y validación offline. |
| `tests/VortexApi.Tests` | Pruebas de integración (SQLite en memoria). |

## Modelo

- **Product**: ERP, CRM, WMS, POS (se crean al iniciar). Cada uno tiene una *clave de aplicación*.
- **Organization**: la empresa que usa la suite.
- **User**: pertenece a una organización. Un mismo usuario entra a todos los sistemas a los que tenga acceso.
  Roles de administración: `User`, `OrgAdmin` (administra su organización) y `PlatformAdmin` (quien opera el hub).
- **OrganizationProduct**: sistema habilitado para la organización. Configuración opcional:
  - `autoAssignUsers`: todo usuario de la organización obtiene acceso en su primer login.
  - `maxUsers`, `maxDevicesPerUser`: límites. `null` (el valor por defecto) significa sin límite.
  - `features`: módulos habilitados (p. ej. `facturacion`, `nomina`). Los interpreta cada sistema.
  - `expiresAt`: vencimiento para accesos temporales (pruebas, hosting). `null` (por defecto) = permanente.
  - `offlineGraceDays`: cuántos días puede operar el sistema sin conexión (por defecto 7).
- **ProductMember**: usuario con acceso a un sistema, con un `role` propio de ese sistema (p. ej. `cajero` en POS).
- **Device**: equipo desde el que el miembro usa el sistema.
- **RefreshToken** (rotación con detección de reutilización) y **AuditLog**.

### Usar varios sistemas o cambiar de uno a otro

- **Usar ERP y CRM juntos**: se habilitan ambos y el usuario entra a cada uno con la misma cuenta.
  `GET /api/access/products` devuelve los sistemas disponibles, para mostrar un selector.
- **Reemplazar CRM por ERP**: se habilita ERP y se deshabilita CRM (`isEnabled: false`). Usuarios, miembros
  y roles se conservan. Si se vuelve a habilitar CRM, todo queda como estaba.

## Datos maestros

Son los datos que más de un sistema necesita. Viven en el hub con un Id único, separados por organización,
y cada sistema los referencia por ese Id. Así, si una empresa cambia de CRM a ERP, sus clientes ya están ahí.

| Tipo | Ruta | Lo usan | Únicos por organización |
|---|---|---|---|
| `party`: clientes, proveedores, contactos | `/api/master/parties` | CRM, ERP, POS | tipo + número de documento (RUC 11 dígitos, DNI 8) |
| `item`: productos y servicios | `/api/master/items` | ERP, WMS, POS | SKU |
| `site`: sedes, tiendas, almacenes | `/api/master/sites` | ERP, WMS, POS | código |
| `company`: razón social, RUC, moneda, impuesto, zona horaria | `/api/master/company` | todos | uno por organización |

Lo que **no** vive aquí: stock, ventas, facturas, oportunidades. Son datos de cada sistema y se compartirán
con eventos (fase 3).

### Reglas de escritura

- **`PUT /{id}` crea o actualiza con un Id generado por el sistema.** Un POS sin conexión puede crear un cliente
  con un GUID propio y sincronizarlo después; reintentar no lo duplica. `POST` también existe, con Id generado
  por el hub.
- **Versiones**: cada registro tiene `meta.version` y la respuesta incluye `ETag: "versión"`. Enviando
  `If-Match: "versión"`, el hub rechaza con **412 `version_conflict`** si otro sistema lo cambió antes.
- **Guardar sin cambios no genera versión ni cambio**, así dos sistemas que sincronizan no entran en bucle.
- **Borrado lógico**: `DELETE` lo oculta de los listados, pero sigue disponible por Id para quien lo referencie.
  Un `PUT` sobre un registro borrado lo restaura.
- **Antes de crear un cliente**, busque con `GET /api/master/parties?documentNumber=…`: si otro sistema ya lo
  registró, el hub responde 409 `duplicate_document` con el Id existente.
- Escriben los sistemas (cualquier usuario con acceso) y el OrgAdmin. Los datos de la empresa solo los cambia el OrgAdmin.

### Feed de cambios: cómo se sincroniza un sistema

```
GET /api/master/changes?since=<cursor>&types=party,item&limit=200
→ { "changes": [ { "sequence": 41, "entityType": "item", "entityId": "…", "operation": "Updated",
                   "version": 3, "source": "ERP", "changedAt": "…", "data": { …estado completo… } } ],
    "nextCursor": 41, "hasMore": false }
```

1. La primera vez, `since=0`: recibe todo el historial (o use los listados para una carga inicial).
2. Aplique cada cambio localmente: `Created`/`Updated` reemplazan el registro por `data`; `Deleted` lo marca como borrado.
3. Guarde `nextCursor`. Si `hasMore` es true, consulte de nuevo de inmediato; si no, espere (p. ej. 30 s) o hágalo
   al recuperar la conexión.
4. Puede ignorar los cambios cuyo `source` es el propio sistema.

Las secuencias son **correlativas por organización y se asignan en orden de commit** (cada escritura incrementa un
contador de la organización dentro de su transacción, lo que serializa las escrituras de esa organización). Un
sistema que pide "lo posterior a N" nunca se salta un cambio, aunque haya escrituras concurrentes.

## Eventos de integración

Cada sistema publica lo que pasa y los demás reaccionan. Si un sistema no está instalado, nadie consume sus
eventos y no pasa nada: así cada uno funciona solo o en conjunto.

| Ejemplo | Publica | Reacciona |
|---|---|---|
| `sale.completed` | POS | ERP la contabiliza, WMS descuenta stock, CRM actualiza el historial del cliente |
| `deal.won` | CRM | ERP genera la factura |
| `stock.adjusted` | WMS | ERP valoriza, POS actualiza disponibilidad |
| `master.item.updated` | hub (por un cambio de datos maestros) | todos los que usan artículos |

`GET /api/events/catalog` lista los eventos conocidos. Se puede publicar cualquier tipo con formato
`categoria.accion` (las categorías `master` y `hub` las publica solo el hub). El contenido de `data` lo define quien
publica; `schemaVersion` permite evolucionarlo.

### Publicar

```http
POST /api/events                       (token de un sistema)
{ "id": "<GUID generado por el sistema>", "type": "sale.completed", "occurredAt": "2026-11-01T15:04:05Z",
  "subjectType": "sale", "subjectId": "B001-000123",
  "data": { "total": 59.90, "currency": "PEN", "customerId": "…", "lines": [ { "itemId": "…", "qty": 2 } ] } }
```

- **Envíe siempre `id`**: si la red falla y se reintenta, el hub responde 200 con el evento ya registrado en vez de duplicarlo.
- **`POST /api/events/batch`** sube hasta 100 eventos en orden (p. ej. las ventas que un POS acumuló sin conexión);
  cada uno se procesa por separado y la respuesta indica el resultado de cada uno.
- `data` es un objeto JSON de hasta 64 KB.

### Consumir con el feed (sistemas sin backend, apps MAUI)

```
GET /api/events?since=<cursor>&types=sale.*,master.item.*&excludeSource=ERP&limit=200
→ { "events": [ … ], "nextCursor": 57, "hasMore": false }
```

Igual que el feed de datos maestros: secuencia correlativa por organización en orden de commit, se guarda
`nextCursor` y se consulta lo posterior. `types` acepta tipos exactos, prefijos (`sale.*`, `master.item.*`) o `*`.

### Consumir con webhooks (sistemas con backend)

El OrgAdmin registra el webhook en `POST /api/admin/webhooks`:

```json
{ "name": "Backend ERP", "url": "https://erp.miempresa.com/hooks/vortex",
  "eventTypes": ["sale.*", "master.item.*"], "receiverProduct": "ERP" }
```

- La respuesta incluye el **secreto de firma** (`whsec_…`); se muestra solo esa vez (`/rotate-secret` genera otro).
- `receiverProduct` evita que el ERP reciba los eventos que él mismo publica.
- Entrega **al menos una vez**: descarte duplicados por `id` del evento. Los reintentos usan espera exponencial
  (30 s, 1 min, 2 min… hasta 6 h, máx. 10 intentos); `GET …/deliveries` muestra el estado y `…/retry` reencola.
  El orden de llegada no está garantizado; use `sequence` si lo necesita.
- Varias réplicas del hub pueden enviar a la vez sin duplicar: cada entrega se toma con un *lease* en la base de datos.
- **SSRF**: no se aceptan URLs a redes privadas, `localhost` ni metadatos de la nube (169.254.169.254); la IP se
  vuelve a verificar al conectar, así un DNS que cambie después tampoco pasa. No se siguen redirecciones.
  En instalaciones on-premise, `Webhooks:AllowPrivateNetworks=true` permite backends en la red interna.

Cabeceras: `X-Vortex-Event`, `X-Vortex-Event-Id`, `X-Vortex-Delivery` y
`X-Vortex-Signature: t=<unix>,v1=<hex>`, donde `v1 = HMAC-SHA256(secreto, "<t>.<cuerpo>")`. Verificación en el receptor:

```csharp
app.MapPost("/hooks/vortex", async (HttpRequest req) =>
{
    var body = await new StreamReader(req.Body).ReadToEndAsync();
    var parts = req.Headers["X-Vortex-Signature"].ToString().Split(',')
        .Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
    var expected = Convert.ToHexStringLower(HMACSHA256.HashData(
        Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{parts["t"]}.{body}")));

    var fresh = Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - long.Parse(parts["t"])) < 300;
    if (!fresh || !CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(parts["v1"])))
        return Results.Unauthorized();

    // … procesar (descartando ids ya vistos) y responder 2xx rápido
    return Results.Ok();
});
```

## Seguridad

1. **Sistema reconocido**: el login exige `X-Vortex-Product` y `X-Vortex-Product-Key`.
2. **Credenciales**: PBKDF2-SHA512 (210k iteraciones), bloqueo tras 5 intentos fallidos (15 min),
   límite de peticiones por IP en autenticación y tiempo de respuesta constante si el correo no existe.
3. **Acceso**: el sistema debe estar habilitado (y vigente, si tiene vencimiento) y el usuario debe ser
   miembro, o el sistema debe tener `autoAssignUsers`.
4. **Equipo**: cada `deviceId` nuevo se registra; si hay `maxDevicesPerUser`, se respeta el límite.
   Un admin puede liberar o bloquear equipos.
5. **Tokens firmados con RSA** (RS256). Solo el hub tiene la llave privada; la pública está en `/.well-known/jwks.json`.
   - *Access token* (15 min): audiencia `vortex-erp`, `vortex-crm`, etc. Incluye sistema, rol, features y equipo.
   - *Refresh token* (30 días, opaco, guardado como hash): se rota en cada uso y está ligado al equipo.
     Reutilizar uno viejo cierra la sesión completa (indicio de robo).
   - *Offline token* (`typ: vortex-offline+jwt`): permite trabajar **sin conexión** durante `offlineGraceDays`.
     Está ligado al sistema y al equipo, y nunca vence después que el acceso.
6. **Revocación inmediata**: en cada petición se verifica en la base de datos usuario activo, *security stamp*,
   organización activa y, para tokens de sistema, equipo y acceso vigentes. Deshabilitar un sistema, desactivar
   un usuario, cambiar su contraseña o rol, quitarle el acceso o liberar un equipo corta la sesión al instante.
7. **Auditoría** de logins (exitosos y fallidos), activaciones de equipos y cambios administrativos.

> Como los sistemas son open source, el hub no es un mecanismo antipiratería: protege las cuentas y los
> datos de cada empresa. La clave de aplicación identifica qué sistema se conecta.

## Ejecutar en local

```bash
docker compose up -d                      # SQL Server en localhost:1433
cd src/VortexApi
dotnet run --launch-profile http          # http://localhost:5299 (aplica migraciones y crea datos iniciales)
```

En `Development`:
- Documentación interactiva: http://localhost:5299/scalar/v1 (OpenAPI en `/openapi/v1.json`).
- PlatformAdmin: `admin@vortex.local` / `Vortex-Admin-2026!`.
- Claves de sistema: `vxp_erp_dev_key_change_me`, `vxp_crm_…`, `vxp_wms_…`, `vxp_pos_…`.
- La llave de firma se genera en `keys/dev-signing-key.pem` (ignorado por git).

`VortexApi.http` tiene el flujo completo: crear organización → habilitar sistema → usuario → acceso → login.

Pruebas: `dotnet test` en la raíz.

## Endpoints

### Sistemas Vortex (ERP/CRM/WMS/POS)

| Método | Ruta | Descripción |
|---|---|---|
| POST | `/api/auth/login` | Headers `X-Vortex-Product`, `X-Vortex-Product-Key`. Body: `email`, `password`, `deviceId`, `deviceName`, `platform`. |
| POST | `/api/auth/refresh` | `refreshToken` + `deviceId`. Rota tokens y revalida el acceso. |
| POST | `/api/auth/logout` | Cierra la sesión (familia de refresh tokens). |
| POST | `/api/auth/change-password` | Usuario autenticado. Cierra todas sus sesiones. |
| POST | `/api/access/heartbeat` | Revalida y devuelve un offline token nuevo. |
| GET | `/api/access/products` | Sistemas a los que el usuario puede entrar. |
| GET | `/.well-known/jwks.json`, `/.well-known/openid-configuration` | Llaves públicas / discovery. |

### Administración (token de `/api/admin/auth/login`)

| Ruta | OrgAdmin | PlatformAdmin |
|---|---|---|
| `GET /api/admin/products`, `POST /{code}/rotate-key` | ver | ver, rotar clave de sistema |
| `/api/admin/organizations` (+ `/{id}/activate`, `/{id}/deactivate`) | ver la suya | CRUD |
| `/api/admin/users` (+ `/{id}/reset-password`, `/{id}/unlock`) | su organización | todos |
| `GET /api/admin/organizations/{orgId}/products[/{code}]` | su organización | todas |
| `PUT /api/admin/organizations/{orgId}/products/{code}` (habilitar/configurar) | — | sí |
| `PUT …/products/{code}/members/{userId}` (dar acceso / cambiar rol), `DELETE …/members/{userId}` | su organización | todas |
| `DELETE …/products/{code}/devices/{deviceId}` | su organización | todas |
| `GET /api/admin/audit` | su organización | todo |

### Datos maestros (token de un sistema o de OrgAdmin)

| Método | Ruta | Descripción |
|---|---|---|
| GET | `/api/master/{parties,items,sites}` | Listado con búsqueda y paginación (`includeDeleted` opcional). |
| GET | `/api/master/{tipo}/{id}` | Un registro (incluye borrados) con `ETag`. |
| POST | `/api/master/{tipo}` | Alta con Id generado por el hub. |
| PUT | `/api/master/{tipo}/{id}` | Alta o actualización con Id del sistema; `If-Match` opcional. |
| DELETE | `/api/master/{tipo}/{id}` | Borrado lógico; `If-Match` opcional. |
| GET / PUT | `/api/master/company` | Datos de la empresa (PUT solo OrgAdmin). |
| GET | `/api/master/changes?since=&types=&limit=` | Feed de cambios. |

### Eventos

| Método | Ruta | Quién | Descripción |
|---|---|---|---|
| POST | `/api/events` | sistemas | Publica un evento (201 nuevo, 200 si el `id` ya existía). |
| POST | `/api/events/batch` | sistemas | Hasta 100 eventos con resultado por evento. |
| GET | `/api/events?since=&types=&excludeSource=&limit=` | sistemas, OrgAdmin | Feed de eventos. |
| GET | `/api/events/{id}`, `/api/events/catalog` | sistemas, OrgAdmin | Un evento / catálogo. |
| GET, POST | `/api/admin/webhooks` | OrgAdmin | Listar / crear (devuelve el secreto). |
| GET, PUT, DELETE | `/api/admin/webhooks/{id}` | OrgAdmin | Ver / modificar o desactivar / eliminar. |
| POST | `/api/admin/webhooks/{id}/rotate-secret` | OrgAdmin | Nuevo secreto. |
| GET | `/api/admin/webhooks/{id}/deliveries?status=` | OrgAdmin | Historial de entregas. |
| POST | `/api/admin/webhooks/{id}/deliveries/{deliveryId}/retry` | OrgAdmin | Reencolar una entrega. |

Los errores se devuelven como ProblemDetails con un `code` estable (`product_not_enabled`, `product_disabled`,
`access_expired`, `no_access`, `user_limit_reached`, `device_limit_reached`, `device_revoked`,
`device_mismatch`, `account_locked`, `refresh_token_reused`, `invalid_product`, `version_conflict`,
`duplicate_document`, `duplicate_sku`, `duplicate_code`, `invalid_document`, …).

## Integración en una app MAUI

```csharp
var client = new VortexApiClient(
    new HttpClient { BaseAddress = new Uri("https://hub.miempresa.com/") },
    new VortexApiClientOptions { ProductCode = "POS", ProductKey = "<clave del sistema>" });

// deviceId: GUID generado una vez y guardado en SecureStorage (mínimo 16 caracteres).
var session = await client.LoginAsync(email, password, deviceId, DeviceInfo.Name, DeviceInfo.Platform.ToString());
await SecureStorage.SetAsync("refresh", session.RefreshToken);
await SecureStorage.SetAsync("offline", session.OfflineToken);
await SecureStorage.SetAsync("jwks", await client.GetSigningKeysAsync());

// Rol y módulos dentro del sistema
var esCajero = session.Access.Role == "cajero";
var conFacturacion = session.Access.HasFeature("facturacion");

// Selector de sistemas: a qué otros sistemas de la suite puede entrar el usuario
var sistemas = await client.GetAvailableProductsAsync(session.AccessToken);

// Cada pocos minutos: heartbeat. Si no hay red, validar el offline token guardado.
try
{
    var hb = await client.HeartbeatAsync(session.AccessToken);
    await SecureStorage.SetAsync("offline", hb.OfflineToken);
}
catch (VortexApiException ex) when (ex.IsAccessDenied) { /* cerrar sesión */ }
catch (HttpRequestException)
{
    var validator = new OfflineAccessValidator(await SecureStorage.GetAsync("jwks"), "https://hub.miempresa.com");
    var offline = await validator.ValidateAsync(await SecureStorage.GetAsync("offline"), "POS", deviceId);
    if (!offline.IsValid) { /* periodo sin conexión agotado: pedir conexión */ }
}
```

Si un sistema tiene backend propio, puede validar los access tokens sin llamar al hub:

```csharp
builder.Services.AddAuthentication().AddJwtBearer(o =>
{
    o.Authority = "https://hub.miempresa.com";   // usa /.well-known/openid-configuration
    o.Audience = "vortex-erp";
    o.MapInboundClaims = false;
});
```

En ese caso la revocación surte efecto al vencer el access token (máx. 15 min).

## Producción (Azure)

- `ConnectionStrings:Vortex` → Azure SQL.
- `Jwt:SigningKeyPem` → llave RSA privada (3072 bits) desde Azure Key Vault. **No** usar `GenerateKeyIfMissing`.
- `Jwt:Issuer` → URL pública del hub (debe coincidir con la que validan los clientes).
- `Seed:AdminEmail` / `Seed:AdminPassword` solo para el primer arranque; luego eliminarlos.
- Generar las claves de sistema con `POST /api/admin/products/{code}/rotate-key`.
- `ForwardedHeaders:Enabled=true` detrás de App Service / Front Door para registrar la IP real
  (la imagen Docker ya lo trae activado).
- `Webhooks:*`: `DispatcherEnabled` (por defecto true; puede correr en todas las réplicas), `PollIntervalSeconds`,
  `MaxAttempts`, `TimeoutSeconds`, `AllowPrivateNetworks` (solo on-premise) y `AllowInsecureHttp` (solo desarrollo).

### Docker / Azure Container Apps

La imagen usa `aspnet:10.0-noble-chiseled-extra` (sin shell, usuario no root), escucha en el puerto **8080**,
arranca en `Production` y aplica las migraciones al iniciar.

```bash
docker build -t vortex-api .
```

Despliegue (ejemplo con Azure CLI y Azure Container Registry):

```bash
az acr build --registry <acr> --image vortex-api:0.2.0 .

az containerapp create \
  --name vortex-api --resource-group <rg> --environment <env> \
  --image <acr>.azurecr.io/vortex-api:0.2.0 --registry-server <acr>.azurecr.io \
  --ingress external --target-port 8080 \
  --min-replicas 1 \
  --secrets sql-conn="<cadena Azure SQL>" jwt-key="<PEM RSA privado>" \
  --env-vars ConnectionStrings__Vortex=secretref:sql-conn \
             Jwt__SigningKeyPem=secretref:jwt-key \
             Jwt__Issuer=https://<dominio-publico>
```

- Las variables usan `__` en lugar de `:` (`Jwt__Issuer`, `Seed__AdminEmail`, …).
- Configure sondas *liveness/readiness* HTTP en `/health`.
- En producción es preferible guardar los secretos en Key Vault y referenciarlos con una identidad administrada.
- Todas las réplicas deben compartir la misma `Jwt__SigningKeyPem`.

## Limitaciones conocidas

- Dos logins simultáneos del mismo usuario en equipos distintos podrían, en una carrera muy estrecha,
  superar `maxDevicesPerUser` por uno (no se usa bloqueo serializable).
- Desactivar un usuario no le quita sus accesos; deja de poder entrar, pero sigue contando para `maxUsers`.
- Un equipo liberado puede volver a registrarse si el usuario tiene cupo (para bloquear a una persona,
  desactive al usuario o quítele el acceso).
- El feed de cambios guarda todas las versiones y no se compacta todavía; con mucho volumen convendrá
  archivar entradas antiguas.
- Las escrituras de datos maestros y la publicación de eventos de una misma organización se serializan (es lo que
  garantiza el orden de los feeds). Alcanza para cientos de eventos por segundo por organización; para volúmenes
  mayores habría que particionar el flujo.
- Los eventos y entregas de webhooks no se depuran todavía; con mucho volumen convendrá archivar los antiguos.
- El secreto de cada webhook se guarda en la base de datos (es necesario para firmar); proteja el acceso a ella.
- Los permisos de escritura de datos maestros no distinguen roles dentro de cada sistema: cada sistema decide
  qué roles pueden editar clientes o artículos.

## Licencia

[AGPL-3.0](LICENSE.md), igual que los sistemas de la suite.
