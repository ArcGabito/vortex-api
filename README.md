# Vortex API

Hub central de la suite **Vortex** (**ERP, CRM, WMS, POS**), escrito en ASP.NET Core (.NET 10).
Los cuatro sistemas son libres y de código abierto (AGPL-3.0). Cada uno funciona por separado, y
este hub permite usarlos juntos o reemplazar uno por otro sin perder usuarios ni datos.

| Fase | Contenido | Estado |
|---|---|---|
| 1. Identidad y accesos | Un usuario para los 4 sistemas, sistemas habilitados por organización, roles por sistema, equipos, modo sin conexión | ✅ |
| 2. Datos maestros | Clientes, productos/artículos, sedes/almacenes, datos de la empresa, con historial de cambios | Pendiente |
| 3. Eventos de integración | Publicar y consumir eventos entre sistemas (`sale.completed`, `stock.adjusted`, …) y webhooks | Pendiente |
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

Los errores se devuelven como ProblemDetails con un `code` estable (`product_not_enabled`, `product_disabled`,
`access_expired`, `no_access`, `user_limit_reached`, `device_limit_reached`, `device_revoked`,
`device_mismatch`, `account_locked`, `refresh_token_reused`, `invalid_product`, …).

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

## Licencia

[AGPL-3.0](LICENSE.md), igual que los sistemas de la suite.
