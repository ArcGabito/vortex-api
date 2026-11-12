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
        if (r.Credit is { } credit)
            (p.PaymentTermDays, p.CreditLimit, p.DiscountPercent, p.PriceListId) = (credit.PaymentTermDays, credit.CreditLimit,
                credit.DiscountPercent is 0 ? null : credit.DiscountPercent, credit.PriceListId);
        if (r.Account is { } account)
            (p.OwnerUserId, p.OwnerName) = (account.OwnerUserId, account.OwnerUserId is null ? null : Clean(account.OwnerName));
        if (r.Consents is { } consents)
        {
            var channels = (consents.Channels ?? []).Select(x => x.Trim().ToLowerInvariant()).Where(ConsentChannels.Contains).Distinct().ToList();
            p.ConsentDataProcessing = consents.DataProcessing;
            p.ConsentMarketing = consents.Marketing;
            p.ConsentChannels = consents.Marketing == true && channels.Count > 0 ? string.Join(',', channels) : null;
            p.ConsentPolicyVersion = Clean(consents.PolicyVersion);
            var now = DateTimeOffset.UtcNow;
            p.ConsentUpdatedAt = consents.UpdatedAt is { } at && at <= now ? at : now;
            p.ConsentSource = Clean(consents.Source)?.ToUpperInvariant();
            p.ConsentCapturedBy = Clean(consents.CapturedBy);
            if (p.ConsentDataProcessing is null && p.ConsentMarketing is null)
                (p.ConsentUpdatedAt, p.ConsentSource, p.ConsentCapturedBy, p.ConsentPolicyVersion) = (null, null, null, null);
        }
    }

    private static readonly string[] ConsentChannels = ["email", "whatsapp", "sms", "phone"];

    protected override async Task ValidateAsync(Party p)
    {
        if ((p.DocumentType is null) != (p.DocumentNumber is null))
            throw ApiException.BadRequest(ErrorCodes.InvalidDocument, "documentType y documentNumber van juntos.");

        var expectedDigits = p.DocumentType switch { "RUC" => 11, "DNI" => 8, _ => (int?)null };
        if (expectedDigits is { } digits && (p.DocumentNumber!.Length != digits || !p.DocumentNumber.All(char.IsAsciiDigit)))
            throw ApiException.BadRequest(ErrorCodes.InvalidDocument, $"El {p.DocumentType} debe tener {digits} dígitos.");

        if (p.PriceListId is { } listId && !await Db.PriceLists.AsNoTracking()
                .AnyAsync(l => l.OrganizationId == p.OrganizationId && l.Id == listId && l.DeletedAt == null))
            throw ApiException.BadRequest(ErrorCodes.Validation, "La lista de precios del cliente no existe.");

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
        p.Notes, p.IsActive, Meta(p),
        p.PaymentTermDays is null && p.CreditLimit is null && p.DiscountPercent is null && p.PriceListId is null
            ? null
            : new PartyCreditDto(p.PaymentTermDays, p.CreditLimit, p.DiscountPercent, p.PriceListId),
        p.OwnerUserId is null ? null : new PartyAccountDto(p.OwnerUserId, p.OwnerName),
        p.ConsentDataProcessing is null && p.ConsentMarketing is null
            ? null
            : new PartyConsentsDto(p.ConsentDataProcessing, p.ConsentMarketing, p.ConsentChannels?.Split(',') ?? [], p.ConsentPolicyVersion,
                p.ConsentUpdatedAt, p.ConsentSource, p.ConsentCapturedBy));

    private static string NormalizeDocument(string value) =>
        new string(value.Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray()).ToUpperInvariant();
}
