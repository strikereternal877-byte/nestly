using Nestly.BuildingBlocks.Results;

namespace Nestly.Application.ProviderIdentity;

public interface IProviderRegistrationService
{
    Task<Result> RequestOtpAsync(RequestProviderRegistrationOtpRequest request);

    Task<Result<ProviderSummaryResponse>> RegisterAsync(RegisterProviderRequest request);
}
