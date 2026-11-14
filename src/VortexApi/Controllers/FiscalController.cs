using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VortexApi.Data;
using VortexApi.Fiscal;
using VortexApi.Infrastructure;
using VortexApi.Security;
using VortexApi.Services;

namespace VortexApi.Controllers;

/// <summary>
/// Facturación electrónica: el OrgAdmin configura el perfil (ambiente, clave SOL, certificado) y los sistemas piden sus
/// comprobantes. El hub los firma y los envía en segundo plano; el resultado llega como <c>hub.fiscal.accepted</c> o
/// <c>hub.fiscal.rejected</c> y también se consulta aquí.
/// </summary>
[ApiController]
[Route("api/fiscal")]
public sealed class FiscalController(FiscalService fiscal, VortexDbContext db) : ControllerBase
{
    public const int MaxCertificateBytes = 64 * 1024;

    /// <summary>Perfil fiscal de la organización (sin secretos: solo si están cargados).</summary>
    [HttpGet("profile")]
    [Authorize(Policy = Policies.MasterRead)]
    public Task<FiscalProfileDto> GetProfile() => fiscal.GetProfileAsync(User.OrganizationId()!.Value);

    /// <summary>
    /// Guarda el perfil. Las claves y el certificado se envían solo para cambiarlos (null = se dejan como están; una clave SOL
    /// vacía la borra). Para habilitarlo hacen falta el RUC de la empresa, el usuario y la clave SOL y un certificado vigente.
    /// </summary>
    [HttpPut("profile")]
    [Authorize(Policy = Policies.OrganizationAdmin)]
    [RequestSizeLimit(MaxCertificateBytes * 2)]
    public Task<FiscalProfileDto> PutProfile(FiscalProfileRequest request) =>
        fiscal.SaveProfileAsync(User.OrganizationId()!.Value, request, User.ActingUserId());

    /// <summary>
    /// Pide un comprobante. Responde 202 con el comprobante en cola (o 200 con el que ya existía para ese <c>sourceId</c>).
    /// </summary>
    [HttpPost("documents")]
    [Authorize(Policy = Policies.ProductClient)]
    public async Task<ActionResult<FiscalDocumentDto>> Issue(FiscalDocumentRequest request)
    {
        var (document, created) = await fiscal.EnqueueAsync(User, request);
        return created ? AcceptedAtAction(nameof(Get), new { id = document.Id }, document) : Ok(document);
    }

    [HttpGet("documents/{id:guid}")]
    [Authorize(Policy = Policies.ProductClient)]
    public async Task<FiscalDocumentDto> Get(Guid id) => FiscalService.ToDto(await FindAsync(id));

    /// <summary>Comprobantes del sistema: por <c>sourceId</c>, por estado o los cambiados desde una fecha.</summary>
    [HttpGet("documents")]
    [Authorize(Policy = Policies.ProductClient)]
    public async Task<IReadOnlyList<FiscalDocumentDto>> List(Guid? sourceId = null, FiscalStatus? status = null, DateTimeOffset? updatedSince = null)
    {
        var (orgId, product) = Caller();
        var query = db.FiscalDocuments.AsNoTracking().Where(d => d.OrganizationId == orgId && d.Product == product);
        if (sourceId is { } source)
            query = query.Where(d => d.SourceId == source);
        if (status is { } s)
            query = query.Where(d => d.Status == s);
        if (updatedSince is { } since)
            query = query.Where(d => d.UpdatedAt > since);
        var documents = await query.OrderByDescending(d => d.CreatedAt).Take(200).ToListAsync();
        return documents.Select(FiscalService.ToDto).ToList();
    }

    /// <summary>XML firmado que se envió a la autoridad.</summary>
    [HttpGet("documents/{id:guid}/xml")]
    [Authorize(Policy = Policies.ProductClient)]
    public async Task<IActionResult> Xml(Guid id) =>
        (await FindAsync(id)).SignedXml is { } xml ? Content(xml, "application/xml") : throw ApiException.NotFound("XML firmado");

    /// <summary>Constancia de recepción (CDR) de la autoridad.</summary>
    [HttpGet("documents/{id:guid}/cdr")]
    [Authorize(Policy = Policies.ProductClient)]
    public async Task<IActionResult> Cdr(Guid id) =>
        (await FindAsync(id)).CdrXml is { } cdr ? Content(cdr, "application/xml") : throw ApiException.NotFound("CDR");

    /// <summary>Vuelve a enviar un comprobante que falló.</summary>
    [HttpPost("documents/{id:guid}/retry")]
    [Authorize(Policy = Policies.ProductClient)]
    public Task<FiscalDocumentDto> Retry(Guid id)
    {
        var (orgId, product) = Caller();
        return fiscal.RetryAsync(orgId, product, id, User.ActingUserId());
    }

    /// <summary>
    /// Anula un comprobante aceptado (comunicación de baja o resumen diario, dentro de los 7 días): vuelve en cola y el
    /// resultado llega como <c>hub.fiscal.voided</c> o <c>hub.fiscal.void_rejected</c>. Se pide por el Id del documento en el sistema.
    /// </summary>
    [HttpPost("documents/by-source/{sourceId:guid}/void")]
    [Authorize(Policy = Policies.ProductClient)]
    public async Task<ActionResult<FiscalVoidDto>> Void(Guid sourceId, FiscalVoidRequest request) =>
        Accepted(await fiscal.RequestVoidAsync(User, sourceId, request.Reason));

    [HttpGet("documents/by-source/{sourceId:guid}/voids")]
    [Authorize(Policy = Policies.ProductClient)]
    public Task<IReadOnlyList<FiscalVoidDto>> Voids(Guid sourceId)
    {
        var (orgId, product) = Caller();
        return fiscal.GetVoidsAsync(orgId, product, sourceId);
    }

    /// <summary>Trae de SUNAT el CDR de un comprobante que quedó sin respuesta (solo en producción).</summary>
    [HttpPost("documents/{id:guid}/check")]
    [Authorize(Policy = Policies.ProductClient)]
    public Task<FiscalDocumentDto> Check(Guid id)
    {
        var (orgId, product) = Caller();
        return fiscal.CheckCdrAsync(orgId, product, id, User.ActingUserId());
    }

    private (Guid OrganizationId, string Product) Caller() => (User.OrganizationId()!.Value, MasterDataWriter.SourceOf(User));

    private async Task<FiscalDocument> FindAsync(Guid id)
    {
        var (orgId, product) = Caller();
        return await db.FiscalDocuments.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id && d.OrganizationId == orgId && d.Product == product)
               ?? throw ApiException.NotFound("Comprobante");
    }
}
