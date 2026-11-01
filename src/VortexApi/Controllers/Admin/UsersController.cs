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
/// Gestión de usuarios. Un OrgAdmin solo ve y administra usuarios de su organización y no puede
/// crear ni modificar PlatformAdmins.
/// </summary>
[ApiController]
[Route("api/admin/users")]
[Authorize(Policy = Policies.Admin)]
public sealed class UsersController(
    VortexDbContext db, AuthService auth, IPasswordHasher hasher, AuditService audit, TimeProvider clock) : ControllerBase
{
    [HttpGet]
    public Task<PagedResult<UserDto>> List(Guid? organizationId, string? search, int page = 1, int pageSize = 50)
    {
        var query = db.Users.AsQueryable();
        if (!User.IsPlatformAdmin())
            organizationId = User.OrganizationId();
        if (organizationId is not null)
            query = query.Where(u => u.OrganizationId == organizationId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToUpperInvariant();
            query = query.Where(u => u.NormalizedEmail.Contains(s) || u.FullName.Contains(search));
        }

        var now = clock.GetUtcNow();
        return query.OrderBy(u => u.Email).ToPageAsync(page, pageSize, u => u.ToDto(now));
    }

    [HttpGet("{id:guid}")]
    public async Task<UserDto> Get(Guid id) => (await FindScopedAsync(id)).ToDto(clock.GetUtcNow());

    [HttpPost]
    public async Task<ActionResult<UserDto>> Create(CreateUserRequest request)
    {
        var orgId = User.IsPlatformAdmin() ? request.OrganizationId : User.OrganizationId();
        EnsureRoleAllowed(request.Role);

        if (request.Role == UserRole.PlatformAdmin)
        {
            if (orgId is not null)
                throw ApiException.BadRequest(ErrorCodes.Validation, "Un PlatformAdmin no pertenece a una organización.");
        }
        else
        {
            if (orgId is null)
                throw ApiException.BadRequest(ErrorCodes.Validation, "organizationId es obligatorio.");
            if (!await db.Organizations.AnyAsync(o => o.Id == orgId))
                throw ApiException.NotFound("Organización");
        }

        var email = request.Email.Trim();
        var normalized = email.ToUpperInvariant();
        if (await db.Users.AnyAsync(u => u.NormalizedEmail == normalized))
            throw ApiException.Conflict(ErrorCodes.EmailInUse, "Ya existe un usuario con ese correo.");

        PasswordPolicy.Ensure(request.Password, email);
        var user = new User
        {
            OrganizationId = orgId,
            Email = email,
            NormalizedEmail = normalized,
            FullName = request.FullName.Trim(),
            PasswordHash = hasher.Hash(request.Password),
            Role = request.Role,
            CreatedAt = clock.GetUtcNow(),
        };
        db.Users.Add(user);
        audit.Record(AuditActions.UserCreated, true, User.UserId(), orgId, targetId: user.Id.ToString(),
            detail: $"{email} ({request.Role})");
        await db.SaveChangesAsync();
        return CreatedAtAction(nameof(Get), new { id = user.Id }, user.ToDto(clock.GetUtcNow()));
    }

    /// <summary>Actualiza nombre, rol y estado. Desactivar o cambiar el rol cierra todas las sesiones del usuario.</summary>
    [HttpPut("{id:guid}")]
    public async Task<UserDto> Update(Guid id, UpdateUserRequest request)
    {
        var user = await FindScopedAsync(id);
        EnsureRoleAllowed(request.Role);
        if ((user.Role == UserRole.PlatformAdmin) != (request.Role == UserRole.PlatformAdmin))
            throw ApiException.BadRequest(ErrorCodes.Validation, "No se puede convertir un usuario de organización en PlatformAdmin ni viceversa.");
        if (id == User.UserId() && (!request.IsActive || request.Role != user.Role))
            throw ApiException.BadRequest(ErrorCodes.Validation, "No puede desactivarse ni cambiarse el rol a sí mismo.");

        var invalidate = user.IsActive != request.IsActive || user.Role != request.Role;
        user.FullName = request.FullName.Trim();
        user.Role = request.Role;
        user.IsActive = request.IsActive;
        if (invalidate)
            await auth.InvalidateUserSessionsAsync(user);

        audit.Record(AuditActions.UserUpdated, true, User.UserId(), user.OrganizationId, targetId: user.Id.ToString(),
            detail: $"role={user.Role}, active={user.IsActive}");
        await db.SaveChangesAsync();
        return user.ToDto(clock.GetUtcNow());
    }

    [HttpPost("{id:guid}/reset-password")]
    public async Task<IActionResult> ResetPassword(Guid id, ResetPasswordRequest request)
    {
        var user = await FindScopedAsync(id);
        await auth.SetPasswordAsync(user, request.NewPassword);
        user.FailedLoginCount = 0;
        user.LockoutEnd = null;
        audit.Record(AuditActions.UserPasswordReset, true, User.UserId(), user.OrganizationId, targetId: user.Id.ToString());
        await db.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("{id:guid}/unlock")]
    public async Task<UserDto> Unlock(Guid id)
    {
        var user = await FindScopedAsync(id);
        user.FailedLoginCount = 0;
        user.LockoutEnd = null;
        audit.Record(AuditActions.UserUnlocked, true, User.UserId(), user.OrganizationId, targetId: user.Id.ToString());
        await db.SaveChangesAsync();
        return user.ToDto(clock.GetUtcNow());
    }

    private async Task<User> FindScopedAsync(Guid id)
    {
        var user = await db.Users.FindAsync(id) ?? throw ApiException.NotFound("Usuario");
        if (!User.IsPlatformAdmin() && (user.OrganizationId != User.OrganizationId() || user.Role == UserRole.PlatformAdmin))
            throw ApiException.NotFound("Usuario");
        return user;
    }

    private void EnsureRoleAllowed(UserRole role)
    {
        if (role == UserRole.PlatformAdmin && !User.IsPlatformAdmin())
            throw ApiException.Forbidden();
    }
}
