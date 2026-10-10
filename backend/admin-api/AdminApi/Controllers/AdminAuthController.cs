using Asp.Versioning;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.RateLimiting;
using Nestly.Application.Identity;
using Nestly.BuildingBlocks.Extensions;
using Nestly.Domain;
using Nestly.Infrastructure;

namespace Nestly.AdminApi.Controllers;

/// <summary>Admin panel authentication (SRS 12.1, tasks 95a-95g).</summary>
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/admin/auth")]
public class AdminAuthController : ControllerBase
{
    private const string SettingsWritePolicy = AdminModules.Settings + ".write";

    private readonly IAdminLoginService _loginService;
    private readonly IValidator<AdminLoginRequest> _loginValidator;
    private readonly IValidator<RefreshTokenRequest> _refreshValidator;
    private readonly IValidator<LogoutRequest> _logoutValidator;

    public AdminAuthController(
        IAdminLoginService loginService,
        IValidator<AdminLoginRequest> loginValidator,
        IValidator<RefreshTokenRequest> refreshValidator,
        IValidator<LogoutRequest> logoutValidator)
    {
        _loginService = loginService;
        _loginValidator = loginValidator;
        _refreshValidator = refreshValidator;
        _logoutValidator = logoutValidator;
    }

    /// <summary>
    /// Admin login (SRS 12.1.1): email + password, JWT issuance, lockout and
    /// login audit. Throttled per-IP by the "login" rate-limit policy
    /// (task 95c); per-account throttling/lockout (95d) happens inside
    /// <see cref="IAdminLoginService"/> itself.
    /// </summary>
    [EnableRateLimiting("login")]
    [HttpPost("login")]
    [ProducesResponseType(typeof(AdminLoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Login([FromBody] AdminLoginRequest request)
    {
        var validation = await _loginValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _loginService.LoginAsync(request);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Exchange a still-valid refresh token for a new access+refresh pair (rotation, SRS 28.3).</summary>
    [HttpPost("refresh")]
    [ProducesResponseType(typeof(AdminLoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Refresh([FromBody] RefreshTokenRequest request)
    {
        var validation = await _refreshValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _loginService.RefreshAsync(request);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Invalidate a session's refresh token (SRS 12.1.2: logout invalidates the active session).</summary>
    [HttpPost("logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Logout([FromBody] LogoutRequest request)
    {
        var validation = await _logoutValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _loginService.LogoutAsync(request);
        return result.IsSuccess ? NoContent() : result.ToProblemResult();
    }

    /// <summary>
    /// Administrative unlock of a locked account (task 95d's unlock path). A
    /// lockout also clears itself automatically once its window elapses;
    /// this only clears it sooner.
    ///
    /// Gated behind "settings.write" (task 96b/96c) - unlocking someone
    /// else's account is admin-user administration (SRS 12.2.1), the same
    /// module as assigning roles or deactivating an account. Only Super
    /// Admin holds this permission in the seeded matrix (task 96a).
    /// </summary>
    [Authorize(AuthenticationSchemes = DependencyInjection.AdminJwtBearerScheme,
        Policy = SettingsWritePolicy)]
    [HttpPost("unlock/{adminUserId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Unlock(Guid adminUserId)
    {
        var result = await _loginService.UnlockAsync(adminUserId);
        return result.IsSuccess ? NoContent() : result.ToProblemResult();
    }

    private static ModelStateDictionary ToModelState(ValidationResult validation)
    {
        var modelState = new ModelStateDictionary();
        foreach (var error in validation.Errors)
        {
            modelState.AddModelError(error.PropertyName, error.ErrorMessage);
        }

        return modelState;
    }
}
