// Despliegue del hub Vortex en Azure: Container Apps + Azure SQL + Key Vault, sin contraseñas guardadas.
//
//   azd up                                              (Azure Developer CLI, ver docs/DESPLIEGUE.md)
//   az deployment group create -g <rg> -f infra/main.bicep -p adminEmail=... adminPassword=...
//
// Después de cambiar este archivo, regenerar main.json (botón "Deploy to Azure"):
//   az bicep build --file infra/main.bicep --outfile infra/main.json

targetScope = 'resourceGroup'

@description('Nombre corto de la instalación (minúsculas y guiones). Se usa en el nombre de los recursos.')
@minLength(2)
@maxLength(20)
param name string = 'vortex'

@description('Región de los recursos. Por defecto, la del grupo de recursos.')
param location string = resourceGroup().location

@description('Imagen del hub. Fijar una versión (p. ej. ghcr.io/arcgabito/vortex-api:0.4.0) en producción.')
param image string = 'ghcr.io/arcgabito/vortex-api:latest'

@description('Email del primer PlatformAdmin (solo se usa si aún no existe ninguno).')
param adminEmail string

@description('Contraseña del primer PlatformAdmin: mínimo 12 caracteres, no debe contener el email.')
@secure()
@minLength(12)
param adminPassword string

@description('URL pública del hub si se usa un dominio propio (https://hub.miempresa.com). Vacío = la URL de Container Apps.')
param publicUrl string = ''

@description('Plan de Azure SQL. Basic/S0/S1 (DTU) son los más económicos; GP_S_Gen5_1 es serverless (vCore).')
@allowed(['Basic', 'S0', 'S1', 'S2', 'GP_S_Gen5_1', 'GP_S_Gen5_2'])
param sqlSku string = 'S0'

@description('Réplicas mínimas. Al menos 1: el despachador de webhooks corre dentro del hub.')
@minValue(1)
param minReplicas int = 1

@description('Réplicas máximas.')
@minValue(1)
param maxReplicas int = 3

@description('vCPU por réplica.')
@allowed(['0.25', '0.5', '1', '2'])
param cpu string = '0.5'

@description('Memoria por réplica (debe corresponder a la vCPU: 0.25→0.5Gi, 0.5→1Gi, 1→2Gi, 2→4Gi).')
@allowed(['0.5Gi', '1Gi', '2Gi', '4Gi'])
param memory string = '1Gi'

@description('Publicar la documentación de la API en /docs y /openapi/v1.json.')
param docsEnabled bool = true

@description('Registro privado (opcional): servidor, usuario y contraseña. Vacío para imágenes públicas como ghcr.io/arcgabito.')
param registryServer string = ''
param registryUsername string = ''
@secure()
param registryPassword string = ''

param tags object = {}

var token = uniqueString(resourceGroup().id, name)
var appName = 'ca-${name}'
var allTags = union({ 'vortex-installation': name }, tags)
var signingKeySecretName = 'jwt-signing-key'
var adminPasswordSecretName = 'seed-admin-password'
var registryPasswordSecretName = 'registry-password'
var useRegistry = !empty(registryServer)

var sqlSkus = {
  Basic: { name: 'Basic', tier: 'Basic' }
  S0: { name: 'S0', tier: 'Standard' }
  S1: { name: 'S1', tier: 'Standard' }
  S2: { name: 'S2', tier: 'Standard' }
  GP_S_Gen5_1: { name: 'GP_S_Gen5', tier: 'GeneralPurpose', family: 'Gen5', capacity: 1 }
  GP_S_Gen5_2: { name: 'GP_S_Gen5', tier: 'GeneralPurpose', family: 'Gen5', capacity: 2 }
}
var serverless = startsWith(sqlSku, 'GP_S_')

// Roles integrados de Azure.
var keyVaultSecretsUser = '4633458b-17de-408a-b874-0445c86b69e6'
var keyVaultSecretsOfficer = 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7'

// ---------- Identidades ----------

// Identidad del hub: lee los secretos de Key Vault y es el administrador Entra de Azure SQL.
resource appIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-${name}-${token}'
  location: location
  tags: allTags
}

// Identidad del script que genera la llave de firma (solo escribe ese secreto).
resource setupIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-${name}-setup-${token}'
  location: location
  tags: allTags
}

// ---------- Key Vault ----------

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: take('kv${replace(name, '-', '')}${token}', 24)
  location: location
  tags: allTags
  properties: {
    tenantId: subscription().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 30
  }
}

resource adminPasswordSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' = {
  parent: vault
  name: adminPasswordSecretName
  properties: { value: adminPassword }
}

resource appReadsSecrets 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: vault
  name: guid(vault.id, appIdentity.id, keyVaultSecretsUser)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUser)
    principalId: appIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource setupWritesSecrets 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: vault
  name: guid(vault.id, setupIdentity.id, keyVaultSecretsOfficer)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsOfficer)
    principalId: setupIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

// Llave RSA de 3072 bits con la que el hub firma los tokens. Se genera una sola vez dentro de Azure y nunca sale
// de Key Vault; volver a desplegar no la cambia (cambiarla invalida todas las sesiones y offline tokens).
resource signingKey 'Microsoft.Resources/deploymentScripts@2023-08-01' = {
  name: 'generate-signing-key'
  location: location
  tags: allTags
  kind: 'AzureCLI'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${setupIdentity.id}': {} }
  }
  dependsOn: [setupWritesSecrets]
  properties: {
    azCliVersion: '2.70.0'
    retentionInterval: 'PT1H'
    cleanupPreference: 'OnSuccess'
    timeout: 'PT15M'
    environmentVariables: [
      { name: 'VAULT', value: vault.name }
      { name: 'SECRET', value: signingKeySecretName }
    ]
    scriptContent: '''
      set -euo pipefail
      # El permiso recién asignado puede tardar unos minutos en propagarse.
      # Solo stdout: la CLI escribe avisos en stderr que no deben confundirse con el resultado.
      count=""
      for i in $(seq 1 30); do
        if count=$(az keyvault secret list --vault-name "$VAULT" --query "length([?name=='$SECRET'])" -o tsv --only-show-errors 2>/dev/null); then break; fi
        count=""; echo "Esperando permisos sobre Key Vault ($i)..."; sleep 10
      done
      if [ -z "$count" ]; then echo "Sin acceso a Key Vault $VAULT." >&2; exit 1; fi
      if [ "$count" != "0" ]; then echo "La llave de firma ya existe."; exit 0; fi
      key=$(mktemp)
      if command -v openssl >/dev/null; then
        openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -out "$key" 2>/dev/null
      else
        python3 -c "from cryptography.hazmat.primitives.asymmetric import rsa; from cryptography.hazmat.primitives import serialization as s; k=rsa.generate_private_key(65537,3072); open('$key','wb').write(k.private_bytes(s.Encoding.PEM,s.PrivateFormat.PKCS8,s.NoEncryption()))"
      fi
      az keyvault secret set --vault-name "$VAULT" --name "$SECRET" --file "$key" --content-type application/x-pem-file -o none --only-show-errors
      rm -f "$key"
      echo "Llave de firma creada."
    '''
  }
}

// ---------- Azure SQL ----------

// Solo autenticación Entra: el hub entra con su identidad administrada, sin contraseñas de SQL.
resource sqlServer 'Microsoft.Sql/servers@2021-11-01' = {
  name: 'sql-${name}-${token}'
  location: location
  tags: allTags
  properties: {
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    administrators: {
      administratorType: 'ActiveDirectory'
      azureADOnlyAuthentication: true
      login: appIdentity.name
      sid: appIdentity.properties.clientId
      tenantId: subscription().tenantId
      principalType: 'Application'
    }
  }
}

// Permite conexiones desde servicios de Azure (Container Apps sin red virtual propia).
resource sqlAllowAzure 'Microsoft.Sql/servers/firewallRules@2021-11-01' = {
  parent: sqlServer
  name: 'AllowAllWindowsAzureIps'
  properties: { startIpAddress: '0.0.0.0', endIpAddress: '0.0.0.0' }
}

resource database 'Microsoft.Sql/servers/databases@2021-11-01' = {
  parent: sqlServer
  name: 'vortex'
  location: location
  tags: allTags
  sku: sqlSkus[sqlSku]
  properties: serverless
    ? { autoPauseDelay: -1, minCapacity: json('0.5') }
    : {}
}

// ---------- Container Apps ----------

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-${name}-${token}'
  location: location
  tags: allTags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: 'cae-${name}-${token}'
  location: location
  tags: allTags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    }
  }
}

var issuer = empty(publicUrl) ? 'https://${appName}.${environment.properties.defaultDomain}' : publicUrl
var connectionString = 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Database=${database.name};Authentication=Active Directory Managed Identity;User Id=${appIdentity.properties.clientId};Encrypt=True;TrustServerCertificate=False;Connection Timeout=60'

resource app 'Microsoft.App/containerApps@2024-03-01' = {
  name: appName
  location: location
  tags: allTags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${appIdentity.id}': {} }
  }
  dependsOn: [appReadsSecrets, signingKey, sqlAllowAzure]
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
      }
      secrets: concat([
        { name: signingKeySecretName, keyVaultUrl: '${vault.properties.vaultUri}secrets/${signingKeySecretName}', identity: appIdentity.id }
        { name: adminPasswordSecretName, keyVaultUrl: adminPasswordSecret.properties.secretUri, identity: appIdentity.id }
      ], useRegistry ? [{ name: registryPasswordSecretName, value: registryPassword }] : [])
      registries: useRegistry
        ? [{ server: registryServer, username: registryUsername, passwordSecretRef: registryPasswordSecretName }]
        : []
    }
    template: {
      containers: [
        {
          name: 'hub'
          image: image
          resources: { cpu: json(cpu), memory: memory }
          env: [
            { name: 'ConnectionStrings__Vortex', value: connectionString }
            { name: 'Jwt__Issuer', value: issuer }
            { name: 'Jwt__SigningKeyPem', secretRef: signingKeySecretName }
            { name: 'Seed__AdminEmail', value: adminEmail }
            { name: 'Seed__AdminPassword', secretRef: adminPasswordSecretName }
            { name: 'Docs__Enabled', value: string(docsEnabled) }
          ]
          probes: [
            // Al arrancar se aplican las migraciones: se le da hasta 5 minutos.
            { type: 'Startup', httpGet: { path: '/health', port: 8080 }, periodSeconds: 10, failureThreshold: 30 }
            { type: 'Liveness', httpGet: { path: '/health', port: 8080 }, periodSeconds: 30 }
            { type: 'Readiness', httpGet: { path: '/health', port: 8080 }, periodSeconds: 10 }
          ]
        }
      ]
      scale: {
        minReplicas: minReplicas
        maxReplicas: maxReplicas
        rules: [{ name: 'http', http: { metadata: { concurrentRequests: '50' } } }]
      }
    }
  }
}

// ---------- Salidas ----------

@description('URL del hub (configurar en los sistemas como Vortex:HubUrl).')
output url string = 'https://${app.properties.configuration.ingress.fqdn}'

@description('Issuer de los tokens (para la validación sin conexión en los sistemas).')
output issuer string = issuer

output containerAppName string = app.name
output keyVaultName string = vault.name
output sqlServerName string = sqlServer.name
output databaseName string = database.name
output identityName string = appIdentity.name
