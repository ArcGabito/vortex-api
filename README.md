# Vortex API

![Wallpaper](docs/vortex-api-wallpaper.png)

Hub central de la suite **Vortex** (**ERP, CRM, WMS, POS**), escrito en ASP.NET Core (.NET 10).
Los cuatro sistemas son libres y de código abierto (AGPL-3.0). Cada uno funciona por separado, y
este hub permite usarlos juntos o reemplazar uno por otro sin perder usuarios ni datos.

## Documentación

| Documento | Para quién |
|---|---|
| [docs/DESCRIPTION.md](docs/DESCRIPTION.md) | Descripción técnica del hub: modelo, datos maestros, eventos, seguridad y endpoints. |
| [docs/SISTEMAS.md](docs/SISTEMAS.md) | Quien construye Vortex ERP, CRM, WMS o POS sobre el hub (personas o agentes de IA). |
| [docs/INTEGRACIONES.md](docs/INTEGRACIONES.md) | Aplicaciones de terceros que se integran con OAuth2. |
| [docs/DESPLIEGUE.md](docs/DESPLIEGUE.md) | Instalar el hub en Azure o en servidores propios. |
| [CONTRIBUTING.md](CONTRIBUTING.md) | Cómo contribuir. |
| [SECURITY.md](SECURITY.md) | Reportar vulnerabilidades. |

[![Deploy to Azure](https://aka.ms/deploytoazurebutton)](https://portal.azure.com/#create/Microsoft.Template/uri/https%3A%2F%2Fraw.githubusercontent.com%2FArcGabito%2Fvortex-api%2Fmain%2Finfra%2Fmain.json)

Licencia [AGPL-3.0](LICENSE.md).
