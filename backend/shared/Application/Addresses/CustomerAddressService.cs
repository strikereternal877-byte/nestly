using Nestly.Application.Geography;
using Nestly.Application.MonthlyService;
using Nestly.BuildingBlocks.Results;
using Nestly.Domain;

namespace Nestly.Application.Addresses;

/// <summary>
/// Address book CRUD (SRS 11.3). Every read/write is scoped by the caller's
/// own <c>customerId</c> (from the JWT, never from the request body) so one
/// customer can never read or mutate another's addresses (SRS 28.3 IDOR).
/// </summary>
public class CustomerAddressService : ICustomerAddressService
{
    private readonly ICustomerAddressRepository _repository;
    private readonly IGeographyRepository _geographyRepository;
    private readonly IMonthlyServiceContractRepository _monthlyServiceContracts;

    public CustomerAddressService(
        ICustomerAddressRepository repository,
        IGeographyRepository geographyRepository,
        IMonthlyServiceContractRepository monthlyServiceContracts)
    {
        _monthlyServiceContracts = monthlyServiceContracts;
        _repository = repository;
        _geographyRepository = geographyRepository;
    }

    public async Task<Result<IReadOnlyList<CustomerAddressResponse>>> ListAsync(Guid customerId)
    {
        var addresses = await _repository.GetByCustomerAsync(customerId);
        return Result.Success<IReadOnlyList<CustomerAddressResponse>>(addresses.Select(ToResponse).ToList());
    }

    public async Task<Result<CustomerAddressResponse>> AddAsync(Guid customerId, UpsertAddressRequest request)
    {
        var existing = await _repository.GetByCustomerAsync(customerId);

        // The very first address always becomes the default so a customer
        // is never left with none — anything after that follows the request.
        bool isDefault = request.IsDefault || existing.Count == 0;

        if (isDefault)
        {
            var currentDefault = await _repository.GetDefaultAsync(customerId);
            if (currentDefault is not null)
            {
                currentDefault.ClearDefault();
                await _repository.UpdateAsync(currentDefault);
            }
        }

        var address = new CustomerAddress(
            Guid.NewGuid(), customerId, request.Label, request.Line1, request.Line2, request.Landmark,
            request.Pincode, request.City, request.State, request.Latitude, request.Longitude,
            request.ContactName, request.ContactMobile, isDefault);

        await LinkToGeographyAsync(address);
        await _repository.AddAsync(address);
        return Result.Success(ToResponse(address));
    }

    public async Task<Result<CustomerAddressResponse>> UpdateAsync(Guid customerId, Guid addressId, UpsertAddressRequest request)
    {
        var address = await _repository.GetByIdAsync(addressId);
        if (address is null || address.CustomerId != customerId)
        {
            return Result.Failure<CustomerAddressResponse>(Error.NotFound("Address.NotFound", "Address not found."));
        }

        address.Update(
            request.Label, request.Line1, request.Line2, request.Landmark, request.Pincode,
            request.City, request.State, request.Latitude, request.Longitude, request.ContactName, request.ContactMobile);
        await LinkToGeographyAsync(address);
        await _repository.UpdateAsync(address);

        if (request.IsDefault && !address.IsDefault)
        {
            await SetDefaultInternalAsync(customerId, address);
        }

        return Result.Success(ToResponse(address));
    }

    public async Task<Result> DeleteAsync(Guid customerId, Guid addressId)
    {
        var address = await _repository.GetByIdAsync(addressId);
        if (address is null || address.CustomerId != customerId)
        {
            return Result.Failure(Error.NotFound("Address.NotFound", "Address not found."));
        }

        // A monthly service still running here would lose its address - the
        // customer is told to stop it first rather than meeting a database
        // error (docs/MONTHLY-SERVICE.md).
        if (await _monthlyServiceContracts.HasRunningContractAtAddressAsync(addressId))
        {
            return Result.Failure(Error.Business(
                "Address.InUseByMonthlyService",
                "This address has a monthly service running. Cancel that service first, then delete the address."));
        }

        // A real delete (SRS 11.3.3): once bookings copy address fields at
        // booking time instead of referencing this row, there is nothing
        // left for this delete to cascade into.
        await _repository.DeleteAsync(address);

        if (address.IsDefault)
        {
            var remaining = await _repository.GetByCustomerAsync(customerId);
            var next = remaining.FirstOrDefault();
            if (next is not null)
            {
                next.MarkAsDefault();
                await _repository.UpdateAsync(next);
            }
        }

        return Result.Success();
    }

    public async Task<Result> SetDefaultAsync(Guid customerId, Guid addressId)
    {
        var address = await _repository.GetByIdAsync(addressId);
        if (address is null || address.CustomerId != customerId)
        {
            return Result.Failure(Error.NotFound("Address.NotFound", "Address not found."));
        }

        if (!address.IsDefault)
        {
            await SetDefaultInternalAsync(customerId, address);
        }

        return Result.Success();
    }

    private async Task SetDefaultInternalAsync(Guid customerId, CustomerAddress address)
    {
        // Clear the old default and save before setting the new one: the
        // partial unique index (WHERE is_default = true) allows only one row
        // at a time, so both can never be true together mid-transaction.
        var currentDefault = await _repository.GetDefaultAsync(customerId);
        if (currentDefault is not null && currentDefault.Id != address.Id)
        {
            currentDefault.ClearDefault();
            await _repository.UpdateAsync(currentDefault);
        }

        address.MarkAsDefault();
        await _repository.UpdateAsync(address);
    }

    /// <summary>
    /// Re-resolves the address's link into the geography master (SRS 12.9.1)
    /// every time the pincode text is set - LocalityId always resolves to
    /// null (see CustomerAddress's doc comment) until the address form gains
    /// a locality field to match against.
    /// </summary>
    private async Task LinkToGeographyAsync(CustomerAddress address)
    {
        var pincodeId = await _geographyRepository.FindActivePincodeIdByCodeAsync(address.Pincode);
        address.LinkToGeography(pincodeId, localityId: null);
    }

    private static CustomerAddressResponse ToResponse(CustomerAddress a) => new(
        a.Id, a.Label, a.Line1, a.Line2, a.Landmark, a.Pincode, a.City, a.State,
        a.Latitude, a.Longitude, a.ContactName, a.ContactMobile, a.IsDefault, a.PincodeId, a.LocalityId);
}
