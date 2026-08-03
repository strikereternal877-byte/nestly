namespace Nestly.Domain;

/// <summary>Who authored a <see cref="ChatMessage"/> (task 189/191).</summary>
public enum ChatSenderType
{
    Customer,
    Admin,

    /// <summary>Reserved for the provider app/portal reply view (task 193, PROVIDER.md). Not yet a message-sending path - provider-api has no chat controller yet (see PRODUCT-ENHANCEMENTS.md IN-APP CHAT for the documented gap) - kept in the enum now so the persisted value never needs a later migration once it is.</summary>
    Provider
}
