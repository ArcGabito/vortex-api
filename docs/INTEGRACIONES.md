# Guía de integración con Vortex

Esta guía es para desarrolladores que quieren conectar un sistema externo (tienda online, facturación
electrónica, pasarela de pago, BI, ERP de terceros…) con la suite Vortex a través de Vortex Hub.

- Referencia interactiva de la API: `https://<su-hub>/docs`
- Especificación OpenAPI (para generar clientes en cualquier lenguaje): `https://<su-hub>/openapi/v1.json`
- Descubrimiento OAuth: `https://<su-hub>/.well-known/openid-configuration`
- Catálogo de eventos: `GET https://<su-hub>/api/events/catalog`

## 1. Qué puede hacer una integración

| Necesidad | Cómo |
|---|---|
| Leer clientes, artículos, sedes y datos de la empresa | `GET /api/master/...` (scope `master.read`) |
| Crear o actualizar clientes, artículos o sedes | `PUT /api/master/{tipo}/{id}` (scope `master.write`) |
| Enterarse de lo que pasa en la suite | Feed `GET /api/events?since=` (scope `events.read`) o webhooks (`webhooks.manage`) |
| Avisar algo a la suite (un pedido, un pago) | `POST /api/events` (scope `events.publish` o `events.publish:order.*`) |

Ejemplos:

| Integración | Scopes típicos | Flujo |
|---|---|---|
| Tienda WooCommerce / Shopify | `master.read`, `events.publish:order.*`, `webhooks.manage` | Lee catálogo y precios; publica `order.created`; recibe `stock.*` por webhook |
| Facturación electrónica (OSE/PSE) | `events.read`, `events.publish:invoice.*` | Lee `sale.completed`; publica `invoice.issued` con el CDR |
| Pasarela de pago (Niubiz, Culqi, Mercado Pago) | `events.publish:payment.*` | Publica `payment.received` |
| Power BI / data warehouse | `master.read`, `events.read` | Lee los feeds periódicamente |

## 2. Registrar la aplicación

El administrador de la organización (OrgAdmin) la registra:

```http
POST /api/admin/integrations
Authorization: Bearer <token del OrgAdmin>
Content-Type: application/json

{ "name": "Tienda WooCommerce", "slug": "tienda-woo",
  "scopes": ["master.read", "events.publish:order.*", "webhooks.manage"] }
```

La respuesta trae `clientId` y `clientSecret`. **El secreto se muestra una sola vez**: guárdelo en un gestor de
secretos. Si se filtra, `POST /api/admin/integrations/{id}/rotate-secret` genera otro e invalida al instante los
tokens emitidos con el anterior. `GET /api/admin/integrations/scopes` lista los scopes disponibles.

Pida solo los scopes que necesita. Todo lo que la aplicación escribe queda marcado con `source: "EXT:<slug>"`.

## 3. Obtener un token (OAuth 2.0 client credentials)

```bash
curl -u "$CLIENT_ID:$CLIENT_SECRET" -d grant_type=client_credentials https://<su-hub>/connect/token
# {"access_token":"eyJ…","token_type":"Bearer","expires_in":3600,"scope":"events.publish:order.* master.read webhooks.manage"}
```

- También acepta `client_id` y `client_secret` en el cuerpo (`client_secret_post`).
- `scope=master.read` (separados por espacio) pide un token con menos permisos que la aplicación.
- No hay refresh token: al vencer (`expires_in`), pida otro. Reutilice el token mientras sea válido.
- Cualquier cliente OAuth estándar sirve: n8n, Make, Zapier, Power Automate, Postman, `Microsoft.Identity`, `requests-oauthlib`, etc.

**C#**

```csharp
var http = new HttpClient { BaseAddress = new Uri("https://<su-hub>/") };
var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}"));
var request = new HttpRequestMessage(HttpMethod.Post, "connect/token")
{
    Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials" }),
};
request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
var token = (await (await http.SendAsync(request)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString();
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
```

**JavaScript (Node 18+)**

```js
const res = await fetch(`${HUB}/connect/token`, {
  method: "POST",
  headers: {
    Authorization: "Basic " + Buffer.from(`${CLIENT_ID}:${CLIENT_SECRET}`).toString("base64"),
    "Content-Type": "application/x-www-form-urlencoded",
  },
  body: "grant_type=client_credentials",
});
const { access_token } = await res.json();
```

**Python**

```python
import requests
token = requests.post(f"{HUB}/connect/token", auth=(CLIENT_ID, CLIENT_SECRET),
                      data={"grant_type": "client_credentials"}).json()["access_token"]
headers = {"Authorization": f"Bearer {token}"}
```

## 4. Leer y escribir datos maestros

```bash
curl -H "Authorization: Bearer $TOKEN" "https://<su-hub>/api/master/items?search=café"
```

Para crear o actualizar use `PUT` con un **Id generado por su sistema** (un GUID que guarda junto a su registro):
reintentar no duplica.

```bash
curl -X PUT -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
     -H 'If-Match: "3"' \
     -d '{"name":"Bodega Don Pepe","documentType":"RUC","documentNumber":"20123456789"}' \
     https://<su-hub>/api/master/parties/0b6c1f7e-2d3a-4c55-9a11-6f0e8b2d4c01
```

- `If-Match` (opcional) con la versión que leyó: si otro sistema lo cambió antes, responde **412 `version_conflict`**.
- Antes de crear un cliente, búsquelo: `GET /api/master/parties?documentNumber=20123456789`.
- Para mantener una copia sincronizada, lea `GET /api/master/changes?since=<cursor>` (ver sección 6).

## 5. Publicar eventos

```bash
curl -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" https://<su-hub>/api/events -d '{
  "id": "5d1f0c3e-8a8b-4f0e-9b6a-2c7d9e1f0a11",
  "type": "order.created",
  "subjectType": "order", "subjectId": "WC-1001",
  "data": { "number": "WC-1001", "customerId": "0b6c1f7e-…", "total": 149.90, "currency": "PEN",
            "lines": [ { "itemId": "…", "sku": "CAFE-250", "qty": 2, "price": 74.95 } ] }
}'
```

- **Envíe siempre `id`** (GUID): si reintenta, el hub responde 200 con el evento ya registrado, sin duplicarlo.
- `POST /api/events/batch` acepta hasta 100 eventos y devuelve el resultado de cada uno.
- Solo puede publicar los tipos que permiten sus scopes (`events.publish:order.*` → `order.created`, `order.cancelled`…).
- `master.*` y `hub.*` son reservados.

## 6. Recibir eventos

### Opción A: feed (consulta periódica)

```
GET /api/events?since=<cursor>&types=sale.*,stock.*&limit=200
→ { "events": [ … ], "nextCursor": 57, "hasMore": false }
```

Guarde `nextCursor` y úselo en la siguiente consulta; si `hasMore` es true, consulte de nuevo de inmediato.
Los eventos llegan en orden y sin huecos. Es la opción más simple y no requiere exponer un endpoint.

### Opción B: webhooks

Con el scope `webhooks.manage`, su aplicación registra sus propios webhooks:

```http
POST /api/admin/webhooks
Authorization: Bearer <token de la aplicación>

{ "name": "Stock para la tienda", "url": "https://tienda.example.com/vortex/hooks", "eventTypes": ["stock.*"] }
```

La respuesta trae el secreto de firma (`whsec_…`). Su aplicación nunca recibe los eventos que ella misma publica.
La URL debe ser `https` y pública (no se aceptan redes privadas ni `localhost`).

Cada entrega es un `POST` JSON con las cabeceras `X-Vortex-Event`, `X-Vortex-Event-Id`, `X-Vortex-Delivery` y
`X-Vortex-Signature: t=<unix>,v1=<hex>`, donde `v1 = HMAC-SHA256(secreto, "<t>.<cuerpo>")`.

**Verificación en Node (Express)**

```js
app.post("/vortex/hooks", express.raw({ type: "application/json" }), (req, res) => {
  const sig = Object.fromEntries(req.get("X-Vortex-Signature").split(",").map(p => p.split("=", 2)));
  const expected = crypto.createHmac("sha256", SECRET).update(`${sig.t}.${req.body}`).digest("hex");
  const fresh = Math.abs(Date.now() / 1000 - Number(sig.t)) < 300;
  if (!fresh || !crypto.timingSafeEqual(Buffer.from(expected), Buffer.from(sig.v1))) return res.sendStatus(401);
  const event = JSON.parse(req.body);
  // … procesar en segundo plano, descartando event.id ya vistos
  res.sendStatus(204);
});
```

**Verificación en Python (Flask)**

```python
@app.post("/vortex/hooks")
def hook():
    body = request.get_data()
    sig = dict(p.split("=", 1) for p in request.headers["X-Vortex-Signature"].split(","))
    expected = hmac.new(SECRET.encode(), f"{sig['t']}.".encode() + body, hashlib.sha256).hexdigest()
    if abs(time.time() - int(sig["t"])) > 300 or not hmac.compare_digest(expected, sig["v1"]):
        abort(401)
    event = json.loads(body)
    # … procesar, descartando event["id"] ya vistos
    return "", 204
```

(La verificación en C# está en [DESCRIPTION.md](DESCRIPTION.md).)

Reglas de entrega: **al menos una vez** (descarte duplicados por `id`), sin orden garantizado (use `sequence`),
reintentos con espera exponencial hasta 10 intentos si su endpoint no responde 2xx en 10 s. Responda rápido y
procese en segundo plano. El historial está en `GET /api/admin/webhooks/{id}/deliveries`.

## 7. Límites y errores

- **Límite de peticiones**: 600 por minuto por aplicación (configurable por el operador del hub). Al superarlo:
  **429** con cabecera `Retry-After`.
- Los errores de la API son ProblemDetails con un `code` estable:

| HTTP | `code` | Qué hacer |
|---|---|---|
| 401 | — | Token vencido o revocado: pida otro en `/connect/token`. |
| 403 | `insufficient_scope` / — | La aplicación no tiene el scope necesario. |
| 409 | `duplicate_document`, `duplicate_sku`, `duplicate_code` | Ya existe: búsquelo y use su Id. |
| 412 | `version_conflict` | Otro sistema lo modificó: vuelva a leer y decida. |
| 400 | `validation_error`, `invalid_document`, `invalid_event_type`, … | Corrija los datos; no reintente igual. |
| 429 | — | Espere `Retry-After` segundos. |
| 5xx | — | Reintente con espera exponencial (sus operaciones son idempotentes si envía Ids). |

- Los errores de `/connect/token` siguen RFC 6749: `{"error":"invalid_client"|"invalid_scope"|"unsupported_grant_type", "error_description":"…"}`.

## 8. Buenas prácticas

- Genere y guarde Ids (GUID) para todo lo que cree: hace seguros los reintentos.
- Guarde los cursores de los feeds en su base de datos, en la misma transacción que los datos que aplica.
- Trate los cambios del feed como "estado completo en esa versión": aplique por Id y versión, ignorando versiones menores.
- Nunca ponga el `client_secret` en una app móvil o en un navegador: las integraciones son servidor a servidor.
