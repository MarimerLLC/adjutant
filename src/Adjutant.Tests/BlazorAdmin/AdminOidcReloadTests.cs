using Adjutant.Auth;
using Adjutant.Core.Configuration;
using Adjutant.HttpServer.BlazorAdmin;
using Adjutant.Tests.Identity;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Adjutant.Tests.BlazorAdmin;

/// <summary>
/// A provider added or changed in the console must reach the OIDC handler without a restart.
/// The handler reads <see cref="OpenIdConnectOptions"/> from the options monitor on every request,
/// so what the monitor returns after a configuration reload is what the next sign-in uses.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class AdminOidcReloadTests
{
    private AdjutantApp _app = null!;

    [TestInitialize]
    public void Start() => _app = new AdjutantApp();

    [TestCleanup]
    public void Stop() => _app.Dispose();

    private static object Settings(string? microsoftClientId = null, string? microsoftSecret = null)
    {
        var providers = new Dictionary<string, object>
        {
            ["google"] = new { Authority = "https://accounts.google.com", ClientId = "test-client", ClientSecret = "test-secret" }
        };

        if (microsoftClientId is not null)
        {
            providers["microsoft"] = new
            {
                Authority = "https://login.microsoftonline.com/common/v2.0",
                ClientId = microsoftClientId,
                ClientSecret = microsoftSecret
            };
        }

        return new
        {
            Adjutant = new { Accounts = Array.Empty<object>() },
            AdminAuth = new { AllowedEmails = new[] { AdjutantApp.Owner }, Providers = providers }
        };
    }

    [TestMethod]
    public async Task ProviderEditsReachTheOidcHandlerOnEveryReload()
    {
        _app.UpdateSettings(Settings());

        // A request first, so the authentication middleware has built every scheme's options the
        // way a running server has by the time anyone opens the settings page.
        using var client = _app.CreateClient();
        await client.GetAsync(AdminSignInProcessor.LoginPath);

        var monitor = _app.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>();
        Assert.AreEqual("unconfigured", monitor.Get(AdminOidc.MicrosoftScheme).ClientId);

        // Several edits in a row: the order in which reload callbacks run is not fixed, so a single
        // edit could pass by luck.
        for (var i = 1; i <= 4; i++)
        {
            _app.UpdateSettings(Settings($"client-{i}", $"secret-{i}"));

            var options = monitor.Get(AdminOidc.MicrosoftScheme);
            Assert.AreEqual($"client-{i}", options.ClientId, $"edit {i}: client ID");
            Assert.AreEqual($"secret-{i}", options.ClientSecret, $"edit {i}: client secret");
            Assert.AreEqual("https://login.microsoftonline.com/common/v2.0", options.Authority, $"edit {i}: authority");
        }

        _app.UpdateSettings(Settings());
        Assert.AreEqual("unconfigured", monitor.Get(AdminOidc.MicrosoftScheme).ClientId, "provider removed");
    }

    /// <summary>
    /// The console's own path: the settings page writes the file through
    /// <see cref="IAdminAuthConfigurationService"/> and nothing calls Reload — the file watcher does.
    /// </summary>
    [TestMethod]
    public async Task ProviderSavedFromTheConsoleReachesTheOidcHandler()
    {
        using var client = _app.CreateClient();
        await client.GetAsync(AdminSignInProcessor.LoginPath);

        var oidc = _app.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>();
        var adminAuth = _app.Services.GetRequiredService<IOptionsMonitor<AdminAuthConfiguration>>();
        var writer = _app.Services.GetRequiredService<IAdminAuthConfigurationService>();
        Assert.AreEqual("unconfigured", oidc.Get(AdminOidc.MicrosoftScheme).ClientId);

        for (var i = 1; i <= 4; i++)
        {
            await writer.SetProviderAsync(
                AdminOidc.MicrosoftScheme, "https://login.microsoftonline.com/common/v2.0", $"client-{i}", $"secret-{i}");

            // Wait for the watcher to deliver the change to the configuration the console reads.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (adminAuth.CurrentValue.GetProvider(AdminOidc.MicrosoftScheme)?.ClientId != $"client-{i}")
            {
                Assert.IsTrue(DateTime.UtcNow < deadline, $"edit {i}: the file watcher never reloaded configuration");
                await Task.Delay(50);
            }

            // The watcher thread may still be running the other reload callbacks, so give the OIDC
            // options the same chance to catch up. What must never happen is that they settle on
            // the previous values.
            deadline = DateTime.UtcNow.AddSeconds(5);
            while (oidc.Get(AdminOidc.MicrosoftScheme).ClientId != $"client-{i}" && DateTime.UtcNow < deadline)
                await Task.Delay(50);

            var options = oidc.Get(AdminOidc.MicrosoftScheme);
            Assert.AreEqual($"client-{i}", options.ClientId, $"edit {i}: client ID");
            Assert.AreEqual($"secret-{i}", options.ClientSecret, $"edit {i}: client secret");
        }
    }
}
