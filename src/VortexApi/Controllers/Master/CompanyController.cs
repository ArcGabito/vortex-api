using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using VortexApi.Contracts;
using VortexApi.Data;
using VortexApi.Domain;
using VortexApi.Infrastructure;
using VortexApi.Security;
using VortexApi.Services;

namespace VortexApi.Controllers.Master;

/// <summary>Datos de la empresa (razón social, RUC, moneda, impuesto, zona horaria) que usan todos los sistemas.</summary>
[ApiController]
[Route("api/master/company")]
[Authorize(Policy = Policies.OrganizationData)]
public sealed class CompanyController(VortexDbContext db, MasterDataWriter writer) : ControllerBase
{
    /// <summary>Si aún no se configuró, devuelve valores por defecto tomados de la organización (versión 0).</summary>
    [HttpGet]
    public async Task<CompanyProfileDto> Get()
    {
        var orgId = User.OrganizationId()!.Value;
        var profile = await db.CompanyProfiles.AsNoTracking().SingleOrDefaultAsync(c => c.Id == orgId);
        if (profile is null)
        {
            var org = await db.Organizations.AsNoTracking().SingleAsync(o => o.Id == orgId);
            profile = new CompanyProfile { Id = orgId, OrganizationId = orgId, LegalName = org.Name, TaxId = org.TaxId, CreatedAt = org.CreatedAt, UpdatedAt = org.CreatedAt };
        }
        Response.Headers[HeaderNames.ETag] = $"\"{profile.Version}\"";
        return ToDto(profile);
    }

    [HttpPut]
    [Authorize(Policy = Policies.OrganizationAdmin)]
    public async Task<CompanyProfileDto> Put(CompanyProfileRequest request)
    {
        var orgId = User.OrganizationId()!.Value;
        var expected = int.TryParse(Request.Headers.IfMatch.ToString().Replace("W/", "").Trim('"', ' '), out var v) ? v : (int?)null;

        var profile = await writer.WriteAsync(User, MasterEntityTypes.Company, async () =>
        {
            var entity = await db.CompanyProfiles.SingleOrDefaultAsync(c => c.Id == orgId);
            var operation = ChangeOperation.Updated;
            if (entity is null)
            {
                entity = new CompanyProfile { Id = orgId, OrganizationId = orgId };
                db.CompanyProfiles.Add(entity);
                operation = ChangeOperation.Created;
            }
            if (expected is not null && entity.Version != expected)
                throw MasterDataWriter.VersionConflict();

            entity.LegalName = request.LegalName.Trim();
            entity.TradeName = Clean(request.TradeName);
            entity.TaxId = Clean(request.TaxId);
            entity.CountryCode = request.CountryCode.ToUpperInvariant();
            entity.CurrencyCode = request.CurrencyCode.ToUpperInvariant();
            entity.TimeZone = request.TimeZone.Trim();
            entity.DefaultTaxRate = request.DefaultTaxRate;
            entity.Email = Clean(request.Email)?.ToLowerInvariant();
            entity.Phone = Clean(request.Phone);
            entity.AddressLine = Clean(request.Address?.Line);
            entity.District = Clean(request.Address?.District);
            entity.City = Clean(request.Address?.City);
            entity.Region = Clean(request.Address?.Region);
            entity.PostalCode = Clean(request.Address?.PostalCode);

            if (TimeZoneInfo.TryFindSystemTimeZoneById(entity.TimeZone, out _) is false)
                throw ApiException.BadRequest(ErrorCodes.Validation, $"Zona horaria desconocida: {entity.TimeZone}.");
            return (entity, operation);
        }, ToDto);

        Response.Headers[HeaderNames.ETag] = $"\"{profile.Version}\"";
        return ToDto(profile);
    }

    private static CompanyProfileDto ToDto(CompanyProfile c) => new(
        c.OrganizationId, c.LegalName, c.TradeName, c.TaxId, c.CountryCode, c.CurrencyCode, c.TimeZone, c.DefaultTaxRate,
        c.Email, c.Phone,
        c.AddressLine is null && c.City is null
            ? null
            : new AddressDto(c.AddressLine, c.District, c.City, c.Region, c.PostalCode, c.CountryCode),
        new MasterMeta(c.Version, c.CreatedAt, c.UpdatedAt, c.DeletedAt));

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
