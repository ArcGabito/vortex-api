# Seguridad

`vortex-api` maneja identidad, sesiones y datos de empresas, así que nos tomamos en serio los reportes de
seguridad.

## Versiones con soporte

Se corrigen vulnerabilidades en la última versión publicada (ver [CHANGELOG.md](CHANGELOG.md)). Las
instalaciones deben actualizarse a esa versión para recibir las correcciones.

## Reportar una vulnerabilidad

**No abras un issue público.** Usa el reporte privado de GitHub:
[Security → Report a vulnerability](https://github.com/ArcGabito/vortex-api/security/advisories/new).

Incluye, si puedes:

- Qué componente afecta (API, SDK `VortexApi.Client`, plantilla de despliegue) y en qué versión.
- Pasos para reproducirlo o una prueba de concepto.
- El impacto que ves (p. ej. acceso a datos de otra organización, saltarse una revocación).

Qué esperar:

- Confirmación de recepción en un máximo de 5 días hábiles.
- Una evaluación inicial y, si se confirma, un plan de corrección.
- Crédito en el aviso de seguridad, si lo deseas.

Pedimos no divulgar la vulnerabilidad hasta que exista una versión corregida.

## Alcance

Sí: la API, el SDK, el `Dockerfile` y las plantillas de `infra/` y `deploy/`.

No: instalaciones concretas de terceros (repórtalo a quien la opera), configuraciones inseguras hechas a mano
(p. ej. `Webhooks:AllowInsecureHttp` en producción) ni las credenciales de desarrollo publicadas a propósito en
`appsettings.Development.json`.
