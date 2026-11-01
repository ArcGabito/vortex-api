using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VortexApi.Contracts;
using VortexApi.Data;
using VortexApi.Domain;
using VortexApi.Infrastructure;
using VortexApi.Security;
using VortexApi.Services;

namespace VortexApi.Controllers.Admin;

/// <summary>
/// Aplicaciones externas autorizadas a integrarse con la organización (solo su OrgAdmin). Cada una recibe un
/// <c>client_id</c> y un <c>client_secret</c> para obtener tokens en <c>POST /connect/token</c>.
/// </summary>
[ApiController]
[Route("api/admin/integrations")]
[Authorize(Policy = Policies.OrganizationAdmin)]
public sealed class IntegrationsController(VortexDbContext db, AuditService audit, TimeProvider clock) : ControllerBase
{
    private Guid OrgId => User.OrganizationId()!.Value;

    /// <summary>Scopes disponibles y qué permite cada uno.</summary>
    [HttpGet("scopes")]
    public IReadOnlyList<ScopeInfo> Scopes() =>
        IntegrationScopes.Descriptions.Select(d => new ScopeInfo(d.Key, d.Value)).ToList();

    [HttpGet]
    public async Task<IReadOnlyList<IntegrationClientDto>> List() =>
        (await db.IntegrationClients.AsNoTracking().Where(c => c.OrganizationId == OrgId).OrderBy(c => c.Name).ToListAsync())
        .Select(ToDto).ToList();

    [HttpGet("{id:guid}")]
    public async Task<IntegrationClientDto> Get(Guid id) => ToDto(await FindAsync(id));

    [HttpPost]
    public async Task<ActionResult<IntegrationCredentialsResponse>> Create(CreateIntegrationClientRequest request)
    {
        var slug = request.Slug.Trim().ToLowerInvariant();
        if (!IntegrationScopes.IsValidSlug(slug))
            throw ApiException.BadRequest(ErrorCodes.Validation, "slug: minúsculas, números y guiones (2 a 40 caracteres).");
        if (await db.IntegrationClients.AnyAsync(c => c.OrganizationId == OrgId && c.Slug == slug))
            throw ApiException.Conflict(ErrorCodes.SlugInUse, $"Ya existe una aplicación con slug {slug}.");

        var secret = NewSecret();
        var client = new IntegrationClient
        {
            OrganizationId = OrgId,
            Name = request.Name.Trim(),
            Slug = slug,
            ClientId = $"vxc_{Hashing.RandomToken(12)}",
            SecretHash = Hashing.Sha256(secret),
            SecretHint = secret[^4..],
            Scopes = ValidateScopes(request.Scopes),
            ExpiresAt = request.ExpiresAt,
            CreatedAt = clock.GetUtcNow(),
            CreatedByUserId = User.UserId(),
        };
        db.IntegrationClients.Add(client);
        audit.Record(AuditActions.IntegrationCreated, true, User.UserId(), OrgId, targetId: client.Id.ToString(),
            detail: $"{client.Slug}: {string.Join(' ', client.Scopes)}");
        await db.SaveChangesAsync();
        return CreatedAtAction(nameof(Get), new { id = client.Id }, Credentials(client, secret));
    }

    /// <summary>Cambiar scopes o desactivar invalida de inmediato los tokens ya emitidos.</summary>
    [HttpPut("{id:guid}")]
    public async Task<IntegrationClientDto> Update(Guid id, UpdateIntegrationClientRequest request)
    {
        var client = await FindAsync(id);
        var scopes = ValidateScopes(request.Scopes);
        if (!scopes.SequenceEqual(client.Scopes) || client.IsActive != request.IsActive || client.ExpiresAt != request.ExpiresAt)
            client.SecurityStamp = Guid.NewGuid().ToString("N");

        client.Name = request.Name.Trim();
        client.Scopes = scopes;
        client.IsActive = request.IsActive;
        client.ExpiresAt = request.ExpiresAt;
        audit.Record(AuditActions.IntegrationUpdated, true, User.UserId(), OrgId, targetId: client.Id.ToString(),
            detail: $"active={client.IsActive}: {string.Join(' ', client.Scopes)}");
        await db.SaveChangesAsync();
        return ToDto(client);
    }

    /// <summary>Genera un secreto nuevo; el anterior y los tokens emitidos con él dejan de funcionar.</summary>
    [HttpPost("{id:guid}/rotate-secret")]
    public async Task<IntegrationCredentialsResponse> RotateSecret(Guid id)
    {
        var client = await FindAsync(id);
        var secret = NewSecret();
        client.SecretHash = Hashing.Sha256(secret);
        client.SecretHint = secret[^4..];
        client.SecurityStamp = Guid.NewGuid().ToString("N");
        audit.Record(AuditActions.IntegrationSecretRotated, true, User.UserId(), OrgId, targetId: client.Id.ToString());
        await db.SaveChangesAsync();
        return Credentials(client, secret);
    }

    /// <summary>Elimina la aplicación y sus webhooks. Los eventos que publicó se conservan (con su <c>source</c>).</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var client = await FindAsync(id);
        await db.WebhookSubscriptions.Where(s => s.OwnerClientId == id).ExecuteDeleteAsync();
        db.IntegrationClients.Remove(client);
        audit.Record(AuditActions.IntegrationDeleted, true, User.UserId(), OrgId, targetId: client.Id.ToString(), detail: client.Slug);
        await db.SaveChangesAsync();
        return NoContent();
    }

    private static List<string> ValidateScopes(IEnumerable<string> scopes)
    {
        var list = scopes.Select(s => s.Trim()).Where(s => s.Length > 0).Distinct().Order().ToList();
        var invalid = list.FirstOrDefault(s => !IntegrationScopes.IsValid(s));
        if (invalid is not null)
            throw ApiException.BadRequest(ErrorCodes.InvalidScope,
                $"Scope desconocido: {invalid}. Use GET /api/admin/integrations/scopes para ver los disponibles.");
        if (list.Count == 0)
            throw ApiException.BadRequest(ErrorCodes.InvalidScope, "Indique al menos un scope.");
        return list;
    }

    private async Task<IntegrationClient> FindAsync(Guid id) =>
        await db.IntegrationClients.SingleOrDefaultAsync(c => c.Id == id && c.OrganizationId == OrgId)
        ?? throw ApiException.NotFound("Aplicación de integración");

    private IntegrationCredentialsResponse Credentials(IntegrationClient client, string secret) =>
        new(ToDto(client), secret, $"{Request.Scheme}://{Request.Host}{Request.PathBase}/connect/token");

    private static IntegrationClientDto ToDto(IntegrationClient c) => new(
        c.Id, c.Name, c.Slug, c.Source, c.ClientId, $"…{c.SecretHint}", c.Scopes, c.IsActive, c.ExpiresAt, c.CreatedAt, c.LastUsedAt);

    private static string NewSecret() => $"vxs_{Hashing.RandomToken(32)}";
}
