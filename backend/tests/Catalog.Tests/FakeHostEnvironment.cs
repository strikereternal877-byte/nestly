using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Nestly.Catalog.Tests;

/// <summary>
/// Hand-rolled <see cref="IHostEnvironment"/> fake, same no-mocking-library
/// convention as <see cref="FakeNotificationTemplateRepository"/> - these
/// tests never run inside a real host, so there is nothing to resolve one
/// from. Defaults to Production rather than Development: <see cref="SandboxNotificationProvider"/>'s
/// dev-only message logging must stay opt-in, and a test that actually wants
/// to exercise that path sets <see cref="EnvironmentName"/> explicitly.
/// </summary>
public sealed class FakeHostEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = Environments.Production;
    public string ApplicationName { get; set; } = "Catalog.Tests";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
