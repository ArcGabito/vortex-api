# Vortex API

API en ASP.NET Core (.NET 10) que administra **usuarios y licencias** de la suite Vortex
(**ERP, CRM, WMS, POS**). Cada sistema autentica a sus usuarios contra esta API, que valida
que la organización tenga una licencia vigente del producto, que el usuario tenga un puesto
asignado y que el equipo esté dentro del límite permitido.

## Estructura

| Proyecto | Descripción |
|---|---|
| `src/VortexApi` | Web API (controladores, EF Core + SQL Server, JWT RS256). |
| `src/VortexApi.Client` | Librería para las apps MAUI: login, refresh, heartbeat y validación offline. |
| `tests/VortexApi.Tests` | Pruebas de integración de los flujos de seguridad (SQLite en memoria). |

## Modelo

- **Product**: ERP, CRM, WMS, POS (sembrados al iniciar). Cada uno tiene una *clave de aplicación*.
- **Organization**: el cliente que compra licencias.
- **User**: pertenece a una organización. Roles: `User`, `OrgAdmin` (administra su organización),
  `PlatformAdmin` (personal de Vortex, sin organización).
- **License**: organización + producto, plan, `maxSeats`, `maxDevicesPerUser`, `offlineGraceDays`,
  `features`, vigencia (`startsAt`/`expiresAt`) y estado (`Active`, `Suspended`, `Revoked`).
- **LicenseSeat**: puesto de una licencia asignado a un usuario.
- **DeviceActivation**: equipo donde el usuario usa su puesto.
- **RefreshToken** (rotación con detección de reutilización) y **AuditLog**.

## Cómo protege el uso sin licencia

1. **App reconocida**: el login exige `X-Vortex-Product` y `X-Vortex-Product-Key`.
2. **Credenciales**: PBKDF2-SHA512 (210k iteraciones), bloqueo tras 5 intentos fallidos (15 min),
   límite de peticiones por IP en endpoints de autenticación, respuesta de tiempo constante si el correo no existe.
3. **Licencia**: debe existir, estar `Active` y dentro de su vigencia para ese producto y organización.
4. **Puesto**: el usuario debe tener un puesto asignado; los puestos están limitados por `maxSeats`.
5. **Equipo**: cada `deviceId` nuevo consume un cupo de `maxDevicesPerUser`; un admin puede liberar equipos.
6. **Tokens firmados con RSA** (RS256). Sólo esta API tiene la llave privada; la pública está en
   `/.well-known/jwks.json`.
   - *Access token* (15 min): audiencia `vortex-erp`, `vortex-crm`, etc. Incluye licencia, plan, features y equipo.
   - *Refresh token* (30 días, opaco, guardado como hash): se rota en cada uso y está ligado al equipo.
     Reutilizar uno viejo cierra la sesión completa (indicio de robo).
   - *License token* (`typ: vortex-license+jwt`): comprobante para operar **sin conexión** durante
     `offlineGraceDays`, nunca más allá del vencimiento de la licencia, ligado a producto y equipo.
7. **Revocación inmediata**: en cada petición autenticada se verifica en BD usuario activo, *security stamp*,
   organización activa y, para tokens de producto, equipo y licencia vigentes. Suspender una licencia,
   desactivar un usuario, cambiar su contraseña o liberar un equipo corta el acceso al instante.
   Cada renovación (`refresh`) y cada `heartbeat` vuelve a validar todo.
8. **Auditoría** de logins (exitosos y fallidos), activaciones de equipos y cambios administrativos.

> La clave de aplicación viaja dentro de la app y puede extraerse; sirve para identificar el sistema y frenar
> clientes genéricos, no como única barrera. La seguridad real está en credenciales + licencia + puesto +
> equipo + firma RSA.

## Ejecutar en local

```bash
docker compose up -d                      # SQL Server en localhost:1433
cd src/VortexApi
dotnet run --launch-profile http          # http://localhost:5299 (aplica migraciones y siembra datos)
```

En `Development`:
- Documentación interactiva: http://localhost:5299/scalar/v1 (OpenAPI en `/openapi/v1.json`).
- PlatformAdmin: `admin@vortex.local` / `Vortex-Admin-2026!`.
- Claves de app: `vxp_erp_dev_key_change_me`, `vxp_crm_…`, `vxp_wms_…`, `vxp_pos_…`.
- La llave de firma se genera en `keys/dev-signing-key.pem` (ignorado por git).

`VortexApi.http` tiene el flujo completo: crear organización → licencia → usuario → puesto → login.

Pruebas: `dotnet test` en la raíz.

## Endpoints

### Apps Vortex (ERP/CRM/WMS/POS)

| Método | Ruta | Descripción |
|---|---|---|
| POST | `/api/auth/login` | Headers `X-Vortex-Product`, `X-Vortex-Product-Key`. Body: `email`, `password`, `deviceId`, `deviceName`, `platform`. |
| POST | `/api/auth/refresh` | `refreshToken` + `deviceId`. Rota tokens y revalida licencia. |
| POST | `/api/auth/logout` | Cierra la sesión (familia de refresh tokens). |
| POST | `/api/auth/change-password` | Usuario autenticado. Cierra todas sus sesiones. |
| POST | `/api/licensing/heartbeat` | Revalida y devuelve un license token nuevo. |
| GET | `/.well-known/jwks.json`, `/.well-known/openid-configuration` | Llaves públicas / discovery. |

### Administración (token de `/api/admin/auth/login`)

| Ruta | OrgAdmin | PlatformAdmin |
|---|---|---|
| `GET /api/admin/products`, `POST /{code}/rotate-key` | ver | ver, rotar clave de app |
| `/api/admin/organizations` (+ `/{id}/activate`, `/{id}/deactivate`) | ver la suya | CRUD |
| `/api/admin/users` (+ `/{id}/reset-password`, `/{id}/unlock`) | su organización | todos |
| `/api/admin/licenses` (`POST`, `PUT`, `/{id}/suspend`, `/reactivate`, `/revoke`) | ver las suyas | todo |
| `POST /api/admin/licenses/{id}/seats`, `DELETE …/seats/{userId}`, `DELETE …/devices/{deviceId}` | su organización | todas |
| `GET /api/admin/audit` | su organización | todo |

Los errores se devuelven como ProblemDetails con un `code` estable (`no_license`, `no_seat`,
`license_expired`, `license_suspended`, `license_revoked`, `device_limit_reached`, `device_revoked`,
`device_mismatch`, `account_locked`, `refresh_token_reused`, `invalid_product`, …).

## Integración en una app MAUI

```csharp
var client = new VortexLicensingClient(
    new HttpClient { BaseAddress = new Uri("https://licencias.vortex.com/") },
    new VortexLicensingOptions { ProductCode = "POS", ProductKey = "<clave de la app>" });

// deviceId: GUID generado una vez y guardado en SecureStorage (mínimo 16 caracteres).
var session = await client.LoginAsync(email, password, deviceId, DeviceInfo.Name, DeviceInfo.Platform.ToString());
await SecureStorage.SetAsync("refresh", session.RefreshToken);
await SecureStorage.SetAsync("license", session.LicenseToken);
await SecureStorage.SetAsync("jwks", await client.GetSigningKeysAsync());

// Cada pocos minutos: heartbeat. Si no hay red, validar el license token guardado.
try
{
    var hb = await client.HeartbeatAsync(session.AccessToken);
    await SecureStorage.SetAsync("license", hb.LicenseToken);
}
catch (VortexApiException ex) when (ex.IsLicenseDenied) { /* bloquear la app */ }
catch (HttpRequestException)
{
    var validator = new OfflineLicenseValidator(await SecureStorage.GetAsync("jwks"), "https://licensing.vortex.local");
    var offline = await validator.ValidateAsync(await SecureStorage.GetAsync("license"), "POS", deviceId);
    if (!offline.IsValid) { /* periodo sin conexión agotado: bloquear */ }
}
```

`VortexLicense.HasFeature("facturacion")` / `OfflineLicense.HasFeature(...)` permiten habilitar módulos por plan.

Si un sistema tiene backend propio (p. ej. en Azure), puede validar los access tokens sin llamar a esta API:

```csharp
builder.Services.AddAuthentication().AddJwtBearer(o =>
{
    o.Authority = "https://licencias.vortex.com";   // usa /.well-known/openid-configuration
    o.Audience = "vortex-erp";
    o.MapInboundClaims = false;
});
```

En ese caso la revocación surte efecto al vencer el access token (máx. 15 min).

## Producción (Azure)

- `ConnectionStrings:Vortex` → Azure SQL.
- `Jwt:SigningKeyPem` → llave RSA privada (3072 bits) desde Azure Key Vault. **No** usar `GenerateKeyIfMissing`.
- `Jwt:Issuer` → URL pública de la API (debe coincidir con la que validan los clientes).
- `Seed:AdminEmail` / `Seed:AdminPassword` sólo para el primer arranque; luego eliminarlos.
- Generar las claves de app con `POST /api/admin/products/{code}/rotate-key`.
- `ForwardedHeaders:Enabled=true` detrás de App Service / Front Door para registrar la IP real.

## Limitaciones conocidas

- Dos activaciones simultáneas del mismo usuario en equipos distintos podrían, en una carrera muy estrecha,
  superar `maxDevicesPerUser` por uno (no se usa bloqueo serializable).
- Desactivar un usuario no libera su puesto; un admin debe liberarlo explícitamente.
- Un equipo revocado puede volver a activarse si el usuario tiene cupo libre (revocar sirve para liberar cupo;
  para bloquear a una persona, desactive al usuario o cambie su contraseña).
