# Desplegar el hub Vortex

Cómo instalar `vortex-api` en la infraestructura de una empresa. Hay dos caminos con la misma imagen
(`ghcr.io/arcgabito/vortex-api`):

| Camino | Para quién | Qué crea |
|---|---|---|
| [A. Azure](#a-azure) | Empresas con suscripción de Azure | Container Apps, Azure SQL, Key Vault, identidad administrada y Log Analytics |
| [B. Docker Compose](#b-servidores-propios-u-otra-nube-docker-compose) | Servidores propios, otra nube o una VM | Hub, SQL Server y Caddy (HTTPS automático) |

Cada instalación es independiente: su base de datos, su llave de firma y sus usuarios. Una instalación puede
alojar una sola empresa o varias organizaciones (p. ej. un proveedor que aloja a varios clientes).

## Versiones

Cada versión del hub publica la imagen `ghcr.io/arcgabito/vortex-api:<versión>` (p. ej. `0.4.0`), además de
`:<mayor>.<menor>` y `:latest`. **En producción, fijar una versión concreta** y actualizar a propósito (ver
[Actualizar](#actualizar)); las notas de cada versión están en [CHANGELOG.md](../CHANGELOG.md).

---

## A. Azure

### Qué se crea

| Recurso | Detalle |
|---|---|
| Container App `ca-<nombre>` | El hub, con HTTPS, 1–3 réplicas (0.5 vCPU / 1 GiB) y sondas en `/health`. |
| Azure SQL (`sql-<nombre>-…` / base `vortex`) | Plan `S0` por defecto. **Solo autenticación Entra**: no hay contraseñas de SQL. |
| Key Vault (`kv<nombre>…`) | Llave de firma de los tokens (RSA 3072, generada dentro de Azure) y contraseña inicial del admin. |
| Identidad administrada `id-<nombre>-…` | El hub la usa para leer Key Vault y entrar a SQL. |
| Log Analytics `log-<nombre>-…` | Logs del hub (30 días). |

La llave de firma se genera **una sola vez** con un script de despliegue (identidad `id-<nombre>-setup-…`) y
nunca sale de Key Vault. Volver a desplegar no la cambia.

### Opción 1: botón (portal de Azure)

[![Deploy to Azure](https://aka.ms/deploytoazurebutton)](https://portal.azure.com/#create/Microsoft.Template/uri/https%3A%2F%2Fraw.githubusercontent.com%2FArcGabito%2Fvortex-api%2Fmain%2Finfra%2Fmain.json)

Elegir suscripción, grupo de recursos (uno nuevo, p. ej. `rg-vortex`), región, email y contraseña del primer
administrador, y crear. Tarda entre 5 y 10 minutos. Al terminar, en **Salidas** del despliegue está la `url` del
hub.

### Opción 2: Azure Developer CLI (`azd`)

Requisitos: [Azure Developer CLI](https://aka.ms/azd-install) y una cuenta con permiso de **Owner** (o
Contributor + User Access Administrator) sobre la suscripción o el grupo de recursos: la plantilla asigna roles.

```bash
git clone https://github.com/ArcGabito/vortex-api && cd vortex-api
azd auth login
azd env new vortex-miempresa             # nombre del entorno (local, solo para azd)
azd env set VORTEX_IMAGE ghcr.io/arcgabito/vortex-api:0.4.0
azd up                                   # pide suscripción, región, grupo de recursos, email y contraseña del admin
```

Variables opcionales (`azd env set …` antes de `azd up`):

| Variable | Por defecto | Para qué |
|---|---|---|
| `VORTEX_NAME` | `vortex` | Prefijo de los recursos (2–20 caracteres, minúsculas y guiones). |
| `VORTEX_IMAGE` | `ghcr.io/arcgabito/vortex-api:latest` | Versión del hub. |
| `VORTEX_PUBLIC_URL` | URL de Container Apps | Dominio propio (ver abajo). |
| `VORTEX_SQL_SKU` | `S0` | `Basic`, `S0`, `S1`, `S2`, `GP_S_Gen5_1`, `GP_S_Gen5_2`. |

### Opción 3: Azure CLI

```bash
az group create -n rg-vortex -l brazilsouth
az deployment group create -g rg-vortex -f infra/main.bicep \
  -p image=ghcr.io/arcgabito/vortex-api:0.4.0 adminEmail=ti@miempresa.com adminPassword='<contraseña>'
```

Todos los parámetros están descritos al inicio de [infra/main.bicep](../infra/main.bicep) (réplicas, CPU,
memoria, `docsEnabled`, registro privado…).

### Dominio propio

1. Desplegar con `publicUrl=https://hub.miempresa.com` (`VORTEX_PUBLIC_URL` en azd). Es el *issuer* de los
   tokens: conviene decidirlo **antes** de que los sistemas inicien sesión, porque cambiarlo invalida las
   sesiones y los offline tokens existentes.
2. En el DNS: `CNAME hub → <fqdn de la Container App>` y el registro `TXT asuid.hub` que indica el portal.
3. Agregar el dominio con certificado administrado (gratis):

   ```bash
   az containerapp hostname add -g rg-vortex -n ca-vortex --hostname hub.miempresa.com
   az containerapp hostname bind -g rg-vortex -n ca-vortex --hostname hub.miempresa.com \
     --environment <cae-…> --validation-method CNAME
   ```

### Base de datos

- **Plan**: `S0` alcanza para la mayoría de empresas (identidad, datos maestros y eventos son tablas pequeñas). El
  hub consulta la base cada pocos segundos (despachador de webhooks), así que los planes *serverless* nunca se
  pausan y no resultan más baratos; se ofrecen para cargas mayores. Revisar precios en la
  [calculadora de Azure](https://azure.microsoft.com/pricing/calculator/).
- **Respaldos**: Azure SQL hace respaldos automáticos con restauración a un punto en el tiempo (7 días por
  defecto; configurable hasta 35 en Standard).
- **Acceso de personas** (consultas, soporte): el administrador Entra del servidor es la identidad del hub. Para
  dar acceso a personas sin quitárselo al hub, crear un grupo de Entra con la identidad `id-<nombre>-…` y las
  personas, y ponerlo como administrador:

  ```bash
  az sql server ad-admin update -g rg-vortex -s <sql-…> --display-name "Vortex SQL Admins" --object-id <id del grupo>
  ```

  Además, el firewall solo permite servicios de Azure: agregar la IP de quien se conecta.

---

## B. Servidores propios u otra nube (Docker Compose)

Requisitos: un servidor Linux con Docker y Docker Compose, 2 vCPU y 4 GB de RAM (SQL Server necesita 2 GB), un
dominio que apunte al servidor y los puertos 80 y 443 abiertos.

```bash
git clone https://github.com/ArcGabito/vortex-api && cd vortex-api/deploy/docker
cp .env.example .env
nano .env                                  # dominio, versión, admin y contraseña de SQL Server

mkdir -p secrets && chmod 700 secrets
openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -out secrets/signing-key.pem
chmod 644 secrets/signing-key.pem          # el hub corre con un usuario sin privilegios dentro del contenedor

docker compose up -d
docker compose logs -f hub                 # "PlatformAdmin inicial creado" = listo
```

En 1–2 minutos el hub responde en `https://<VORTEX_DOMAIN>` con un certificado de Let's Encrypt.

- **La llave `secrets/signing-key.pem` es crítica**: si se pierde, todas las sesiones y offline tokens dejan de
  valer. Respaldarla aparte (no junto a la base) y no regenerarla.
- **SQL Server**: `SQL_EDITION=Express` es gratis hasta 10 GB por base, suficiente para el hub. Para Standard o
  Enterprise se necesita licencia. También se puede usar un SQL Server existente: quitar el servicio
  `sqlserver` y cambiar `ConnectionStrings__Vortex` en `compose.yaml`.
- **Respaldos**:

  ```bash
  docker compose exec sqlserver /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$SQL_SA_PASSWORD" \
    -Q "BACKUP DATABASE Vortex TO DISK = '/var/opt/mssql/backup/vortex.bak' WITH INIT, COMPRESSION"
  docker compose cp sqlserver:/var/opt/mssql/backup/vortex.bak ./vortex-$(date +%F).bak
  ```

- **Red interna sin dominio público**: en el `Caddyfile`, usar `hub.empresa.local { tls internal … }` (Caddy crea
  su propia CA; instalar su certificado raíz en los equipos) o montar un certificado propio con
  `tls /ruta/cert.pem /ruta/key.pem`. Si los webhooks van a sistemas de la misma red, poner
  `VORTEX_WEBHOOKS_PRIVATE_NETWORKS=true`.
- **Varias réplicas**: `docker compose up -d --scale hub=2` (todas comparten la llave y la base; las migraciones y
  los datos iniciales son seguros con varias réplicas arrancando a la vez).

---

## Después de instalar

1. **Entrar como PlatformAdmin**: `POST /api/admin/auth/login` con el email y la contraseña del despliegue (o
   desde `/docs`). Cambiar la contraseña con `POST /api/auth/change-password` (con el token de administración).
2. **Generar las claves de los sistemas** que se usarán (no existen hasta generarlas):
   `POST /api/admin/products/{ERP|CRM|WMS|POS}/rotate-key`. La clave se muestra una sola vez: se configura en
   cada sistema como `Vortex:ProductKey`.
3. **Crear la organización**, habilitar sus sistemas y crear los usuarios (ver `src/VortexApi/VortexApi.http`):
   `POST /api/admin/organizations`, `PUT /api/admin/organizations/{id}/products/{code}`, `POST /api/admin/users`.
   Conviene crear un `OrgAdmin` de la empresa para que administre sus propios usuarios.
4. **Configurar los sistemas** con `Vortex:HubUrl` = URL del hub y, para la validación sin conexión, el issuer
   (`Jwt:Issuer`: la salida `issuer` en Azure o `https://<VORTEX_DOMAIN>` en Docker).
5. Opcional: `Docs__Enabled=false` si no se quiere publicar `/docs`.

## Actualizar

Leer antes las notas de la versión en el [CHANGELOG](../CHANGELOG.md). Las migraciones de base de datos se
aplican solas al arrancar; **respaldar la base antes** de actualizar.

```bash
# Azure (azd)
azd env set VORTEX_IMAGE ghcr.io/arcgabito/vortex-api:0.5.0 && azd provision

# Azure (CLI)
az containerapp update -g rg-vortex -n ca-vortex --image ghcr.io/arcgabito/vortex-api:0.5.0

# Docker Compose: cambiar VORTEX_IMAGE en .env y
docker compose pull hub && docker compose up -d
```

Para volver atrás, desplegar la imagen anterior **y** restaurar el respaldo de la base si la versión nueva tenía
migraciones.

## Configuración

El hub se configura con variables de entorno (`__` en lugar de `:`). Las más usadas:

| Variable | Para qué |
|---|---|
| `ConnectionStrings__Vortex` | Cadena de conexión a SQL Server / Azure SQL. |
| `Jwt__Issuer` | URL pública del hub; debe coincidir con la que validan los sistemas. |
| `Jwt__SigningKeyPem` o `Jwt__SigningKeyPath` | Llave RSA privada (PEM) con la que se firman los tokens. Todas las réplicas, la misma. |
| `Seed__AdminEmail`, `Seed__AdminPassword` | Primer PlatformAdmin; solo se usan si aún no existe ninguno. |
| `Docs__Enabled` | Publicar `/docs` y `/openapi/v1.json` (por defecto `true`). |
| `Webhooks__AllowPrivateNetworks` | Permitir webhooks a redes privadas (solo on-premise). |
| `RateLimiting__AuthPermitsPerMinute`, `RateLimiting__IntegrationPermitsPerMinute` | Límites de peticiones. |
| `ForwardedHeaders__Enabled` | Ya activado en la imagen (detrás de Container Apps o Caddy). |

La lista completa está en [DESCRIPTION.md](DESCRIPTION.md#producción-azure) y en
`src/VortexApi/appsettings.json`.

## Problemas comunes

| Síntoma | Causa probable |
|---|---|
| Azure: la Container App no arranca y los logs dicen `Login failed for user '<token-identified principal>'` | El administrador Entra de SQL se cambió y ya no incluye la identidad del hub. |
| Azure: error de Key Vault al crear la Container App | Los permisos recién asignados tardan unos minutos: volver a ejecutar `azd provision` o el despliegue. |
| Docker: `hub` se reinicia con `Permission denied` sobre `/run/secrets/signing_key` | Falta `chmod 644 secrets/signing-key.pem`. |
| Docker: Caddy no obtiene el certificado | El DNS no apunta al servidor o los puertos 80/443 están cerrados. |
| Los sistemas reciben `invalid_product` | No se generó la clave del sistema (paso 2) o está mal configurada. |
| La validación sin conexión falla en los sistemas | El issuer configurado en el sistema no coincide con `Jwt__Issuer`. |
