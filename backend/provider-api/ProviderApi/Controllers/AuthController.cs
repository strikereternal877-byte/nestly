using Asp.Versioning;
using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.RateLimiting;
using Nestly.Application.ProviderIdentity;
using Nestly.BuildingBlocks.Extensions;

namespace Nestly.ProviderApi.Controllers;

/// <summary>
/// Provider authentication (task 146a/146b, PROVIDER.md API surface "Auth").
/// OTP-only — there is no password login for providers, so this is
/// structurally simpler than consumer-api's <c>AuthController</c>, which it
/// otherwise mirrors.
/// </summary>
[ApiController]
[ApiVersion(1)]
[Route("api/v{version:apiVersion}/auth")]
public class AuthController : ControllerBase
{
    private readonly IProviderRegistrationService _registrationService;
    private readonly IProviderLoginService _loginService;
    private readonly IValidator<RequestProviderRegistrationOtpRequest> _registrationOtpValidator;
    private readonly IValidator<RegisterProviderRequest> _registerValidator;
    private readonly IValidator<RequestProviderLoginOtpRequest> _loginOtpValidator;
    private readonly IValidator<LoginProviderWithOtpRequest> _loginWithOtpValidator;
    private readonly IValidator<RefreshProviderTokenRequest> _refreshValidator;
    private readonly IValidator<LogoutProviderRequest> _logoutValidator;

    public AuthController(
        IProviderRegistrationService registrationService,
        IProviderLoginService loginService,
        IValidator<RequestProviderRegistrationOtpRequest> registrationOtpValidator,
        IValidator<RegisterProviderRequest> registerValidator,
        IValidator<RequestProviderLoginOtpRequest> loginOtpValidator,
        IValidator<LoginProviderWithOtpRequest> loginWithOtpValidator,
        IValidator<RefreshProviderTokenRequest> refreshValidator,
        IValidator<LogoutProviderRequest> logoutValidator)
    {
        _registrationService = registrationService;
        _loginService = loginService;
        _registrationOtpValidator = registrationOtpValidator;
        _registerValidator = registerValidator;
        _loginOtpValidator = loginOtpValidator;
        _loginWithOtpValidator = loginWithOtpValidator;
        _refreshValidator = refreshValidator;
        _logoutValidator = logoutValidator;
    }

    /// <summary>Step 1: send a registration OTP to a mobile number.</summary>
    [EnableRateLimiting("otp")]
    [HttpPost("registration/otp")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RequestRegistrationOtp([FromBody] RequestProviderRegistrationOtpRequest request)
    {
        var validation = await _registrationOtpValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _registrationService.RequestOtpAsync(request);
        return result.IsSuccess ? NoContent() : result.ToProblemResult();
    }

    /// <summary>Step 2: complete registration once the OTP has been verified.</summary>
    [HttpPost("registration")]
    [ProducesResponseType(typeof(ProviderSummaryResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Register([FromBody] RegisterProviderRequest request)
    {
        var validation = await _registerValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _registrationService.RegisterAsync(request);
        return result.IsSuccess ? StatusCode(StatusCodes.Status201Created, result.Value) : result.ToProblemResult();
    }

    /// <summary>Send a login OTP to an already-registered mobile number.</summary>
    [EnableRateLimiting("otp")]
    [HttpPost("login/otp")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RequestLoginOtp([FromBody] RequestProviderLoginOtpRequest request)
    {
        var validation = await _loginOtpValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _loginService.RequestOtpAsync(request);
        return result.IsSuccess ? NoContent() : result.ToProblemResult();
    }

    /// <summary>Login via mobile OTP.</summary>
    [EnableRateLimiting("login")]
    [HttpPost("login/otp/verify")]
    [ProducesResponseType(typeof(ProviderLoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> LoginWithOtp([FromBody] LoginProviderWithOtpRequest request)
    {
        var validation = await _loginWithOtpValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _loginService.LoginWithOtpAsync(request);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Exchange a still-valid refresh token for a new access+refresh pair (rotation).</summary>
    [HttpPost("refresh")]
    [ProducesResponseType(typeof(ProviderLoginResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Refresh([FromBody] RefreshProviderTokenRequest request)
    {
        var validation = await _refreshValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _loginService.RefreshAsync(request);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Invalidate a session's refresh token.</summary>
    [HttpPost("logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Logout([FromBody] LogoutProviderRequest request)
    {
        var validation = await _logoutValidator.ValidateAsync(request);
        if (!validation.IsValid)
        {
            return ValidationProblem(ToModelState(validation));
        }

        var result = await _loginService.LogoutAsync(request);
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
