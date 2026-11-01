namespace VortexApi.Domain;

public enum UserRole
{
    User = 0,
    OrgAdmin = 1,
    PlatformAdmin = 2,
}

public enum LicenseStatus
{
    Active = 0,
    Suspended = 1,
    Revoked = 2,
}

public static class ProductCodes
{
    public const string Erp = "ERP";
    public const string Crm = "CRM";
    public const string Wms = "WMS";
    public const string Pos = "POS";

    public static readonly IReadOnlyDictionary<string, string> Defaults = new Dictionary<string, string>
    {
        [Erp] = "Vortex ERP",
        [Crm] = "Vortex CRM",
        [Wms] = "Vortex WMS",
        [Pos] = "Vortex POS",
    };
}
