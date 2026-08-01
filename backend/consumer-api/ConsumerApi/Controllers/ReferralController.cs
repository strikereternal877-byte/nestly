using System.IdentityModel.Tokens.Jwt;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nestly.Application.Referral;

namespace Nestly.ConsumerApi.Controllers;

/// <summary>
/// Customer-facing Refer &amp; Earn surface (REFERRAL.md "API SURFACE", task
/// 168): the caller's own code/share link/lifetime stats and their referral
/// history. Both endpoints resolve the caller's customer id from the JWT
/// "sub" claim, same pattern as <c>CouponsController.CurrentCustomerId</c> -
/// there is no route parameter for the customer id, this is always "my own"
/// data.
/// </summary>
[ApiController]
[ApiVersion(1)]
[Authorize]
[Route("api/v{version:apiVersion}/me/referral")]
public class ReferralController : ControllerBase
{
    private readonly IReferralCustomerService _referralCustomerService;

    public ReferralController(IReferralCustomerService referralCustomerService)
    {
        _referralCustomerService = referralCustomerService;
    }

    /// <summary>Code (lazily generated on first call), shareable link, and lifetime stats (REFERRAL.md "GET /me/referral").</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ReferralSummaryResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSummary() => Ok(await _referralCustomerService.GetSummaryAsync(CurrentCustomerId()));

    /// <summary>This customer's own referrals as referrer, newest first (REFERRAL.md "GET /me/referral/history").</summary>
    [HttpGet("history")]
    [ProducesResponseType(typeof(IReadOnlyList<ReferralHistoryItemResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetHistory() => Ok(await _referralCustomerService.GetHistoryAsync(CurrentCustomerId()));

    private Guid CurrentCustomerId() =>
        Guid.Parse(User.FindFirst(JwtRegisteredClaimNames.Sub)!.Value);
}
