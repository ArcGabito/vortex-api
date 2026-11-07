# Hoja de ruta de la suite Vortex

Objetivo: que **Vortex ERP, CRM, WMS y POS** compitan en el mercado internacional con funciones empresariales, para
**Perú, el resto de LATAM, EE. UU. y Europa**, en **español, inglés y portugués**. Cada sistema tiene su hoja de ruta
(`docs/ROADMAP.md` en su repo); aquí va lo que comparten y el orden en que se construye.

| Sistema | Se compara con | Hoja de ruta |
|---|---|---|
| Vortex ERP | Odoo, SAP Business One, Microsoft Dynamics 365 Business Central, NetSuite | [vortex-erp](https://github.com/ArcGabito/vortex-erp/blob/main/docs/ROADMAP.md) |
| Vortex CRM | Salesforce Sales Cloud, HubSpot, Pipedrive, Zoho CRM | [vortex-crm](https://github.com/ArcGabito/vortex-crm/blob/main/docs/ROADMAP.md) |
| Vortex WMS | Manhattan, SAP EWM, Odoo Inventory, Fishbowl | [vortex-wms](https://github.com/ArcGabito/vortex-wms/blob/main/docs/ROADMAP.md) |
| Vortex POS | Square, Lightspeed, Shopify POS, Loyverse | [vortex-pos](https://github.com/ArcGabito/vortex-pos/blob/main/docs/ROADMAP.md) |

## Principios

- **Solo el hub.** Los cuatro sistemas usan el hub para identidad, datos maestros e integración; ninguno tiene
  servidor ni usuarios propios. Lo que un sistema necesita y el hub no tiene se propone en `vortex-api`.
- **Funcionan solos, juntos o reemplazándose** ([SISTEMAS.md](SISTEMAS.md), sección 7).
- **Sin conexión primero** y **varios equipos por organización**: toda función nueva dice cómo se replica.
- **Internacional por diseño**: nada fijo para un país (ni el IGV, ni el sol, ni el formato de fecha). Lo propio de
  cada país (facturación electrónica, libros contables, guías) va en **paquetes de localización** que se activan
  por el país de la empresa.
- **Módulos por `features`** del hub, para venderlos por plan.

## Fase 1: base común · L

Una librería compartida, **`VortexApi.Suite`** (en este repo, junto al SDK), que los cuatro sistemas usan igual:

| Pieza | Qué resuelve |
|---|---|
| **Impuestos por país** | Tasas y tipos (estándar, reducido, cero, exento…) para Perú, México, Colombia, Chile, Argentina, Ecuador, Bolivia, Brasil, EE. UU. y la UE; precios con o sin impuesto; desglose por tasa. Reemplaza el IGV fijo. |
| **Monedas** | ISO 4217 con decimales y símbolo; redondeo según la moneda (el peso chileno no tiene decimales). |
| **Regiones** | Cultura (formato de fecha, número y moneda) según el idioma del usuario y el país de la empresa; zona horaria de la empresa. |
| **Idiomas** | Infraestructura de textos en español, inglés y portugués para las cuatro apps. |
| **Permisos** | Roles por sistema (los asigna el OrgAdmin en el hub) con una matriz de acciones; se validan en los servicios de Core y funcionan sin conexión. |
| **Auditoría** | Bitácora de acciones sensibles (anular, cancelar, cambiar precios, descuentos, cierres) con usuario, equipo y hora. |

Se entrega en dos partes:

- **1a (motor):** impuestos, monedas, regiones, permisos y auditoría en `VortexApi.Suite` e integrados en los cuatro
  sistemas. **Hecho.**
- **1b (idiomas):** la interfaz de las cuatro apps en español, inglés y portugués, con selector de idioma. **Hecho:**
  cada app lleva `Texts/{es,en,pt}.json` en Core y una prueba que verifica que estén completos. Los mensajes de
  validación de Core y los documentos para el cliente (PDF y WhatsApp del CRM) también salen en el idioma elegido.

**Terminado cuando:** una empresa de México vende en el POS con IVA 16 %, el ERP factura en dólares a un cliente de
EE. UU. sin impuesto, un cajero no puede anular una venta, la anulación de un supervisor queda en la auditoría, y la
app se usa en inglés.

## Fase 2: lo esencial de cada sistema · XL

Lo que un cliente empresarial da por hecho en cada tipo de sistema (detalle en cada hoja de ruta):

- **ERP:** contabilidad (plan de cuentas, asientos automáticos, libros y estados financieros), notas de crédito y
  débito, multimoneda con tipo de cambio, cuentas por pagar, bancos y conciliación, costos.
- **CRM:** embudos y etapas configurables, leads y conversión, actividades con calendario, plantillas de cotización,
  productos con listas de precios, metas y pronósticos, reportes de ventas.
- **WMS:** costo promedio y kardex valorizado, lotes y vencimientos, series, ubicaciones, picking y empaque, conteos
  cíclicos (fases 0 a 5 de su hoja de ruta).
- **POS:** catálogo con variantes y modificadores, listas de precios y promociones, clientes y fidelización, impresión
  de tickets y cajón, devoluciones y cambios con política, reportes Z y por cajero.

## Fase 3: localización fiscal · XL

Paquetes por país, empezando por **Perú** (factura, boleta, notas y guías electrónicas UBL 2.1 vía OSE/SUNAT; PLE),
luego **México** (CFDI 4.0 vía PAC), **Colombia** (factura electrónica DIAN), **Chile** (DTE SII), y para EE. UU. y la
UE los formatos y reportes de impuestos (sales tax por estado, IVA y factura electrónica de la UE). Cada paquete es
un módulo por `features`.

## Fase 4: escala y ecosistema · L

Reportes y tableros configurables, importación masiva, API pública y webhooks por sistema (sobre los del hub),
integraciones (comercio electrónico, pasarelas de pago, transportistas, correo y WhatsApp Business), y aplicaciones
listas para tiendas (Microsoft Store, Google Play, App Store).

## Propuestas al hub que salen de este plan

- ~~**Tipos de cambio**~~: hecho (`exchange_rate`, `OnExchangeRates`, `GetExchangeRateAsync`, 2026-11).
- ~~**Artículo de almacén y venta**~~: hecho (presentaciones, varios códigos, unidad UN/ECE, peso, lote/serie; 2026-11).
- **Idioma preferido del usuario** en la sesión.
- ~~**Sedes por usuario**~~: hecho (`Access.SiteIds`, 2026-11).
- **Eventos de cancelación** (`order.cancelled`, `invoice.voided`, `purchase_order.cancelled`) para replicar estados.
