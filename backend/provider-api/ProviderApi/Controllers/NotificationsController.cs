using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nestly.Application.Notifications;
using Nestly.BuildingBlocks.Extensions;
using Nestly.Infrastructure;

namespace Nestly.ProviderApi.Controllers;

/// <summary>
/// A provider's in-app notification inbox (Provider Management UX pass) -
/// backs the header bell and the dedicated /notifications list in
/// provider-web. Every action is scoped to the caller's own provider id
/// taken from the JWT (SRS 28.3 IDOR), same pattern as
/// <see cref="EarningsController"/>.
/// </summary>
[ApiController]
[ApiVersion(1)]
[Authorize(AuthenticationSchemes = DependencyInjection.ProviderJwtBearerScheme)]
[Route("api/v{version:apiVersion}/notifications")]
public class NotificationsController : ControllerBase
{
    private readonly IProviderNotificationService _notificationService;

    public NotificationsController(IProviderNotificationService notificationService)
    {
        _notificationService = notificationService;
    }

    /// <summary>Newest first, paged, plus the caller's current unread count.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ProviderNotificationListResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] int page = 1, [FromQuery] int pageSize = 20)
    {
        var result = await _notificationService.ListAsync(CurrentProviderId(), page, pageSize);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Marks a single notification read - 404s if it belongs to a different provider.</summary>
    [HttpPost("{id:guid}/read")]
    [ProducesResponseType(typeof(ProviderNotificationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> MarkRead(Guid id)
    {
        var result = await _notificationService.MarkReadAsync(CurrentProviderId(), id);
        return result.IsSuccess ? Ok(result.Value) : result.ToProblemResult();
    }

    /// <summary>Marks every one of the caller's unread notifications read in one call.</summary>
    [HttpPost("read-all")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> MarkAllRead()
    {
        await _notificationService.MarkAllReadAsync(CurrentProviderId());
        return NoContent();
    }

    private Guid CurrentProviderId() =>
        User.GetSubjectId();
}
