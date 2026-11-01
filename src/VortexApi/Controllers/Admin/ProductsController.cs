using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using VortexApi.Contracts;
using VortexApi.Data;
using VortexApi.Infrastructure;
using VortexApi.Security;
using VortexApi.Services;

namespace VortexApi.Controllers.Admin;

[ApiController]
[Route("api/admin/products")]
[Authorize(Policy = Policies.Admin)]
public sealed class ProductsController(VortexDbContext db, AuditService audit) : ControllerBase
{
    [HttpGet]
    public async Task<IReadOnlyList<ProductDto>> List() =>
        await db.Products.OrderBy(p => p.Code)
            .Select(p => new ProductDto(p.Id, p.Code, p.Name, p.IsActive, p.ClientKeyHash != null))
            .ToListAsync();

    /// <summary>
    /// Genera una nueva clave de aplicación para el producto. La clave se muestra una sola vez y debe
    /// configurarse en la app (ERP/CRM/WMS/POS). La clave anterior deja de funcionar de inmediato.
    /// </summary>
    [HttpPost("{code}/rotate-key")]
    [Authorize(Policy = Policies.PlatformAdmin)]
    public async Task<ProductKeyResponse> RotateKey(string code)
    {
        var product = await db.Products.SingleOrDefaultAsync(p => p.Code == code.ToUpperInvariant())
                      ?? throw ApiException.NotFound("Producto");

        var key = $"vxp_{product.Code.ToLowerInvariant()}_{Hashing.RandomToken(32)}";
        product.ClientKeyHash = Hashing.Sha256(key);
        audit.Record(AuditActions.ProductKeyRotated, true, User.UserId(), productCode: product.Code);
        await db.SaveChangesAsync();
        return new ProductKeyResponse(product.Code, key);
    }
}
