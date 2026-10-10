using FluentAssertions;
using Nestly.Application.Addresses;
using Nestly.Domain;
using Nestly.Infrastructure.Persistence.Repositories;

namespace Nestly.Identity.Tests;

/// <summary>
/// Covers the address-to-geography link added while building the booking
/// flow (Phase 3): CustomerAddress previously stored city/pincode as free
/// text with no way to resolve a localityId/pincodeId for the slot and
/// serviceability APIs. AddAsync/UpdateAsync now resolve PincodeId from the
/// geography master; LocalityId always resolves to null for now, since the
/// address form has no locality field to match against yet.
/// </summary>
public sealed class CustomerAddressServiceTests : IClassFixture<TestDatabase>
{
    private readonly TestDatabase _db;

    public CustomerAddressServiceTests(TestDatabase db) => _db = db;

    private static UpsertAddressRequest Request(string pincode) => new(
        Label: "Home",
        Line1: "221B Baker Street",
        Line2: null,
        Landmark: null,
        Pincode: pincode,
        City: "Bengaluru",
        State: "Karnataka",
        Latitude: 12.9716m,
        Longitude: 77.5946m,
        ContactName: "Asha Rao",
        ContactMobile: "+919876543210",
        IsDefault: true);

    private CustomerAddressService BuildService(Nestly.Infrastructure.Persistence.NestlyDbContext context) =>
        new(new CustomerAddressRepository(context), new GeographyRepository(context), new MonthlyServiceContractRepository(context));

    [Fact]
    public async Task AddAsync_resolves_PincodeId_when_the_code_matches_an_active_pincode()
    {
        var customerId = Guid.NewGuid();
        var state = new State(Guid.NewGuid(), "Karnataka", "KA" + Guid.NewGuid().ToString("N")[..6]);
        var city = new City(Guid.NewGuid(), state.Id, "Bengaluru");
        var pincode = new Pincode(Guid.NewGuid(), city.Id, "560001");

        using (var context = _db.CreateContext())
        {
            context.States.Add(state);
            context.Cities.Add(city);
            context.Pincodes.Add(pincode);
            context.SaveChanges();
        }

        using var serviceContext = _db.CreateContext();
        var result = await BuildService(serviceContext).AddAsync(customerId, Request("560001"));

        result.IsSuccess.Should().BeTrue();
        result.Value.PincodeId.Should().Be(pincode.Id);
        result.Value.LocalityId.Should().BeNull();
    }

    [Fact]
    public async Task AddAsync_leaves_PincodeId_null_when_no_active_pincode_matches()
    {
        var customerId = Guid.NewGuid();

        using var context = _db.CreateContext();
        var result = await BuildService(context).AddAsync(customerId, Request("999999"));

        result.IsSuccess.Should().BeTrue();
        result.Value.PincodeId.Should().BeNull();
    }

    [Fact]
    public async Task AddAsync_leaves_PincodeId_null_when_the_matching_pincode_is_inactive()
    {
        var customerId = Guid.NewGuid();
        var state = new State(Guid.NewGuid(), "Maharashtra", "MH" + Guid.NewGuid().ToString("N")[..6]);
        var city = new City(Guid.NewGuid(), state.Id, "Pune");
        var pincode = new Pincode(Guid.NewGuid(), city.Id, "411001");
        pincode.Deactivate();

        using (var context = _db.CreateContext())
        {
            context.States.Add(state);
            context.Cities.Add(city);
            context.Pincodes.Add(pincode);
            context.SaveChanges();
        }

        using var serviceContext = _db.CreateContext();
        var result = await BuildService(serviceContext).AddAsync(customerId, Request("411001"));

        result.IsSuccess.Should().BeTrue();
        result.Value.PincodeId.Should().BeNull();
    }

    [Fact]
    public async Task UpdateAsync_re_resolves_PincodeId_when_the_pincode_text_changes()
    {
        var customerId = Guid.NewGuid();
        var state = new State(Guid.NewGuid(), "Delhi", "DL" + Guid.NewGuid().ToString("N")[..6]);
        var city = new City(Guid.NewGuid(), state.Id, "New Delhi");
        var oldPincode = new Pincode(Guid.NewGuid(), city.Id, "110001");
        var newPincode = new Pincode(Guid.NewGuid(), city.Id, "110002");

        using (var context = _db.CreateContext())
        {
            context.States.Add(state);
            context.Cities.Add(city);
            context.Pincodes.AddRange(oldPincode, newPincode);
            context.SaveChanges();
        }

        Guid addressId;
        using (var context = _db.CreateContext())
        {
            var added = await BuildService(context).AddAsync(customerId, Request("110001"));
            added.Value.PincodeId.Should().Be(oldPincode.Id);
            addressId = added.Value.Id;
        }

        using var updateContext = _db.CreateContext();
        var updated = await BuildService(updateContext).UpdateAsync(customerId, addressId, Request("110002"));

        updated.IsSuccess.Should().BeTrue();
        updated.Value.PincodeId.Should().Be(newPincode.Id);
    }
}
