using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VortexApi.Contracts;
using VortexApi.Controllers.Admin;
using VortexApi.Data;
using VortexApi.Domain;
using VortexApi.Infrastructure;
using VortexApi.Services;

namespace VortexApi.Controllers.Master;

/// <summary>Clientes, proveedores y contactos compartidos por CRM, ERP y POS.</summary>
[Route("api/master/parties")]
public sealed class PartiesController(VortexDbContext db, MasterDataWriter writer)
    : MasterDataControllerBase<Party, PartyRequest, PartyDto>(db, writer)
{
    protected override string EntityType => MasterEntityTypes.Party;
    protected override string DisplayName => "Cliente o proveedor";

    /// <summary>
    /// Lista y busca. Use <c>documentNumber</c> antes de crear un cliente para no duplicar uno que ya registró
    /// otro sistema.
    /// </summary>
    [HttpGet]
    public Task<PagedResult<PartyDto>> List(string? search, string? documentNumber, bool? isCustomer, bool? isSupplier,
        bool includeDeleted = false, int page = 1, int pageSize = 50)
    {
        var query = Scoped(includeDeleted);
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(p => p.Name.Contains(search) || (p.TradeName != null && p.TradeName.Contains(search)) ||
                                     (p.Email != null && p.Email.Contains(search)));
        if (!string.IsNullOrWhiteSpace(documentNumber))
            query = query.Where(p => p.DocumentNumber == NormalizeDocument(documentNumber));
        if (isCustomer is not null)
            query = query.Where(p => p.IsCustomer == isCustomer);
        if (isSupplier is not null)
            query = query.Where(p => p.IsSupplier == isSupplier);
        return query.OrderBy(p => p.Name).ToPageAsync(page, pageSize, ToDto);
    }

    protected override void Apply(Party p, PartyRequest r)
    {
        p.Name = r.Name.Trim();
        p.TradeName = Clean(r.TradeName);
        p.IsCustomer = r.IsCustomer;
        p.IsSupplier = r.IsSupplier;
        p.Kind = r.Kind;
        p.DocumentType = Clean(r.DocumentType)?.ToUpperInvariant();
        p.DocumentNumber = r.DocumentNumber is null ? null : NormalizeDocument(r.DocumentNumber);
        p.Email = Clean(r.Email)?.ToLowerInvariant();
        p.Phone = Clean(r.Phone);
        p.AddressLine = Clean(r.Address?.Line);
        p.District = Clean(r.Address?.District);
        p.City = Clean(r.Address?.City);
        p.Region = Clean(r.Address?.Region);
        p.PostalCode = Clean(r.Address?.PostalCode);
        p.CountryCode = Clean(r.Address?.CountryCode)?.ToUpperInvariant();
        p.Notes = Clean(r.Notes);
        p.IsActive = r.IsActive;
    }

    protected override async Task ValidateAsync(Party p)
    {
        if ((p.DocumentType is null) != (p.DocumentNumber is null))
            throw ApiException.BadRequest(ErrorCodes.InvalidDocument, "documentType y documentNumber van juntos.");

        var expectedDigits = p.DocumentType switch { "RUC" => 11, "DNI" => 8, _ => (int?)null };
        if (expectedDigits is { } digits && (p.DocumentNumber!.Length != digits || !p.DocumentNumber.All(char.IsAsciiDigit)))
            throw ApiException.BadRequest(ErrorCodes.InvalidDocument, $"El {p.DocumentType} debe tener {digits} dígitos.");

        if (p.DocumentNumber is null)
            return;
        var existing = await Db.Parties.AsNoTracking()
            .Where(x => x.OrganizationId == p.OrganizationId && x.Id != p.Id && x.DeletedAt == null &&
                        x.DocumentType == p.DocumentType && x.DocumentNumber == p.DocumentNumber)
            .Select(x => (Guid?)x.Id).FirstOrDefaultAsync();
        if (existing is not null)
            throw ApiException.Conflict(ErrorCodes.DuplicateDocument,
                $"Ya existe un registro con {p.DocumentType} {p.DocumentNumber} (id {existing}).");
    }

    protected override PartyDto ToDto(Party p) => new(
        p.Id, p.IsCustomer, p.IsSupplier, p.Kind, p.Name, p.TradeName, p.DocumentType, p.DocumentNumber, p.Email, p.Phone,
        p.AddressLine is null && p.City is null && p.CountryCode is null
            ? null
            : new AddressDto(p.AddressLine, p.District, p.City, p.Region, p.PostalCode, p.CountryCode),
        p.Notes, p.IsActive, Meta(p));

    private static string NormalizeDocument(string value) =>
        new string(value.Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray()).ToUpperInvariant();
}
