# VortexApi.Suite

Base común de los sistemas de la suite **Vortex** (ERP, CRM, WMS, POS), para que los cuatro calculen, muestren y
controlen lo mismo de la misma forma en **Perú, LATAM, EE. UU. y Europa**:

| Espacio de nombres | Qué trae |
|---|---|
| `VortexApi.Suite.Taxes` | Regímenes de impuesto por país (`TaxCatalog.For("MX")`), categorías de artículo (`standard`, `reduced`, `zero`, `exempt`…) y `TaxCalculator` con desglose por tasa, precios con o sin impuesto. |
| `VortexApi.Suite.Money` | Monedas ISO 4217 con sus decimales y símbolo (`Currencies.Get("CLP").Round(…)`). |
| `VortexApi.Suite.Regional` | Idiomas (español, inglés, portugués), cultura según idioma y país, hora de la empresa. |
| `VortexApi.Suite.Security` | `PermissionMatrix`: roles por sistema (los asigna el OrgAdmin en el hub) y qué puede hacer cada uno. |
| `VortexApi.Suite.Auditing` | `AuditEntry`, acciones comunes y el esquema SQLite de la bitácora. |
| `VortexApi.Suite.Text` | `TextCatalog`: textos de la interfaz por idioma, con los comunes de la suite incluidos. |

```csharp
var regime = TaxCatalog.For(company.CountryCode, company.DefaultTaxRate);   // la tasa del hub manda
var totals = TaxCalculator.Calculate(lines.Select(l => new TaxableLine(l.Total, l.TaxCode)), regime, "PEN");
// totals.Net, totals.Tax, totals.Total, totals.Groups (base e impuesto por tasa)
```

Sin dependencias; licencia AGPL-3.0.
