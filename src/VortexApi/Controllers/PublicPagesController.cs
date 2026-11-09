using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using VortexApi.Contracts;
using VortexApi.Data;
using VortexApi.Domain;
using VortexApi.Infrastructure;
using VortexApi.Security;
using VortexApi.Services;

namespace VortexApi.Controllers;

/// <summary>
/// Lo que ve y responde el cliente con el enlace público (sin usuario): la página <c>/p/{token}</c> y su API. El token
/// es la única credencial; un token inválido responde 404 igual que uno inexistente.
/// </summary>
[ApiController]
[AllowAnonymous]
[EnableRateLimiting(RateLimitPolicies.Public)]
public sealed class PublicPagesController(
    VortexDbContext db, PublicLinkTokens tokens, EventPublisher publisher, AuditService audit, TimeProvider clock) : ControllerBase
{
    /// <summary>Tamaño máximo de la firma (PNG en data URL).</summary>
    public const int MaxSignatureChars = 300_000;

    private const string PngPrefix = "data:image/png;base64,";

    /// <summary>Página para ver el documento y aceptarlo con firma o rechazarlo (en el idioma del enlace).</summary>
    [HttpGet("p/{token}")]
    [ApiExplorerSettings(IgnoreApi = true)]
    public async Task<IActionResult> Page(string token)
    {
        var link = await FindAsync(token, countView: true);
        var nonce = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        Response.Headers.ContentSecurityPolicy =
            $"default-src 'none'; style-src 'unsafe-inline'; img-src data:; script-src 'nonce-{nonce}'; connect-src 'self'; " +
            "base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
        SetPrivateHeaders();
        return Content(PublicLinkPage.Render(View(link), token, nonce), "text/html; charset=utf-8");
    }

    [HttpGet("api/public/links/{token}")]
    public async Task<PublicLinkView> Get(string token)
    {
        SetPrivateHeaders();
        return View(await FindAsync(token, countView: true));
    }

    /// <summary>El cliente acepta el documento con su nombre y su firma dibujada (PNG).</summary>
    [HttpPost("api/public/links/{token}/accept")]
    public async Task<PublicLinkView> Accept(string token, PublicLinkAcceptRequest request)
    {
        if (!IsPng(request.Signature))
            throw ApiException.BadRequest(ErrorCodes.Validation, "La firma debe ser una imagen PNG (data URL).");
        return View(await RespondAsync(token, PublicLinkStatus.Accepted, request.SignerName, request.SignerEmail, request.Comment,
            request.Signature));
    }

    /// <summary>El cliente rechaza el documento (con el motivo, si quiere).</summary>
    [HttpPost("api/public/links/{token}/reject")]
    public async Task<PublicLinkView> Reject(string token, PublicLinkRejectRequest? request) =>
        View(await RespondAsync(token, PublicLinkStatus.Rejected, request?.SignerName, null, request?.Comment, null));

    private async Task<PublicLink> RespondAsync(string token, PublicLinkStatus response, string? signerName, string? signerEmail,
        string? comment, string? signature)
    {
        var link = await FindAsync(token, countView: false);
        var now = clock.GetUtcNow();
        EnsureOpen(link, now);

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var userAgent = Truncate(Request.Headers.UserAgent.ToString(), 300);
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync();
            // Solo una respuesta: si otro pedido respondió primero, este no cambia nada.
            var changed = await db.PublicLinks.Where(l => l.Id == link.Id && l.Status == PublicLinkStatus.Open).ExecuteUpdateAsync(s => s
                .SetProperty(l => l.Status, response)
                .SetProperty(l => l.RespondedAt, now)
                .SetProperty(l => l.UpdatedAt, now)
                .SetProperty(l => l.SignerName, Clean(signerName))
                .SetProperty(l => l.SignerEmail, Clean(signerEmail))
                .SetProperty(l => l.Comment, Clean(comment))
                .SetProperty(l => l.Signature, signature)
                .SetProperty(l => l.ResponderIp, ip)
                .SetProperty(l => l.ResponderUserAgent, userAgent));
            if (changed == 0)
                throw ApiException.Conflict(ErrorCodes.LinkAlreadyResponded, "Este documento ya fue respondido.");

            var accepted = response == PublicLinkStatus.Accepted;
            var data = new PublicLinkResponded(link.Id, link.Product, link.DocumentType, link.DocumentId, link.DocumentNumber,
                accepted ? "accepted" : "rejected", now, Clean(signerName), Clean(signerEmail), Clean(comment));
            await publisher.AppendAsync(new EventActor(link.OrganizationId, "HUB", null, null),
                accepted ? PublicLinkEvents.Accepted : PublicLinkEvents.Rejected, JsonSerializer.Serialize(data, PublicLinkJson.Options),
                subjectType: link.DocumentType, subjectId: link.DocumentId.ToString());
            audit.Record(accepted ? AuditActions.PublicLinkAccepted : AuditActions.PublicLinkRejected, true, organizationId: link.OrganizationId,
                productCode: link.Product, targetId: link.Id.ToString(), detail: $"{link.DocumentType} {link.DocumentNumber}: {Clean(signerName)}");
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        });
        db.ChangeTracker.Clear();
        return await db.PublicLinks.AsNoTracking().SingleAsync(l => l.Id == link.Id);
    }

    private static void EnsureOpen(PublicLink link, DateTimeOffset now)
    {
        switch (link.Status)
        {
            case PublicLinkStatus.Accepted or PublicLinkStatus.Rejected:
                throw ApiException.Conflict(ErrorCodes.LinkAlreadyResponded, "Este documento ya fue respondido.");
            case PublicLinkStatus.Revoked:
                throw new ApiException(StatusCodes.Status410Gone, ErrorCodes.LinkRevoked, "Este enlace fue retirado.");
        }
        if (link.ExpiresAt <= now)
            throw new ApiException(StatusCodes.Status410Gone, ErrorCodes.LinkExpired, "Este enlace venció.");
    }

    private async Task<PublicLink> FindAsync(string token, bool countView)
    {
        var id = tokens.Verify(token) ?? throw ApiException.NotFound("Enlace");
        var link = await db.PublicLinks.SingleOrDefaultAsync(l => l.Id == id) ?? throw ApiException.NotFound("Enlace");
        if (countView)
        {
            var now = clock.GetUtcNow();
            link.ViewCount++;
            link.FirstViewedAt ??= now;
            link.LastViewedAt = now;
            await db.SaveChangesAsync();
        }
        return link;
    }

    private PublicLinkView View(PublicLink l) => new(
        l.Status, l.Status == PublicLinkStatus.Open && l.ExpiresAt <= clock.GetUtcNow(), l.ExpiresAt, l.Language,
        JsonSerializer.Deserialize<PublicDocument>(l.Document, PublicLinkJson.Options)!, l.RespondedAt, l.SignerName);

    private void SetPrivateHeaders()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        Response.Headers.XContentTypeOptions = "nosniff";
    }

    private static bool IsPng(string? dataUrl)
    {
        if (dataUrl is null || dataUrl.Length > MaxSignatureChars || !dataUrl.StartsWith(PngPrefix, StringComparison.Ordinal))
            return false;
        var payload = dataUrl.AsSpan(PngPrefix.Length);
        var buffer = new byte[payload.Length * 3 / 4 + 3];
        return Convert.TryFromBase64Chars(payload, buffer, out var n) && n > 8
               && buffer.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? null : value.Length > max ? value[..max] : value;
}
