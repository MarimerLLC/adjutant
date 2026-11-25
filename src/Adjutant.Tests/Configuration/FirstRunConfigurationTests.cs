using Adjutant.Auth;
using Adjutant.Core.Configuration;
using Adjutant.Core.Models;
using Adjutant.Core.Services;
using Adjutant.HttpServer;
using Adjutant.Tests.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Adjutant.Tests.Configuration;

/// <summary>
/// #130: on a first run the data directory has no appsettings.json. The first save from the admin
/// console creates it, and that save must reach the running server without a restart.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class FirstRunConfigurationTests
{
    [TestMethod]
    public async Task AccountAddedOnAFreshDataDirectoryReachesTheRunningServer()
    {
        using var app = new AdjutantApp(freshInstall: true);
        var settingsFile = Path.Combine(app.DataDirectory, "appsettings.json");

        // Starting the server builds configuration; the settings file must still not exist, or
        // the test is not covering a first run.
        using var client = app.CreateClient();
        await client.GetAsync("/health");
        Assert.IsFalse(File.Exists(settingsFile), "the server created appsettings.json itself; not a first run");

        var monitor = app.Services.GetRequiredService<IOptionsMonitor<AdjutantConfiguration>>();
        var registry = app.Services.GetRequiredService<IAccountRegistry>();
        Assert.IsEmpty(monitor.CurrentValue.Accounts);

        // The console's own path: the service writes the file and nothing calls Reload.
        var writer = app.Services.GetRequiredService<IAccountConfigurationService>();
        await writer.AddAccountAsync(new AccountInfo
        {
            Id = "first",
            DisplayName = "First account",
            Provider = "ics",
            ProviderConfig = new Dictionary<string, string> { ["IcsUrl"] = "https://example.com/cal.ics" }
        });
        Assert.IsTrue(File.Exists(settingsFile));

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (await registry.GetAccountAsync("first") is null)
        {
            Assert.IsTrue(DateTime.UtcNow < deadline, "the account never reached the running server");
            await Task.Delay(50);
        }

        Assert.AreEqual("first", monitor.CurrentValue.Accounts.Single().Id);
    }

    [TestMethod]
    public void OperatorSettingsLayerOverTheShippedDefaults()
    {
        var dir = Path.Combine(Path.GetTempPath(), "adjutant-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "app"));
        Directory.CreateDirectory(Path.Combine(dir, "data"));
        try
        {
            var appFile = Path.Combine(dir, "app", "appsettings.json");
            var dataFile = Path.Combine(dir, "data", "appsettings.json");
            File.WriteAllText(appFile, """
                { "Adjutant": { "Accounts": [], "ExternalBaseUrl": "https://shipped.example" },
                  "Kestrel": { "Endpoints": { "Http": { "Url": "http://+:8080" } } } }
                """);

            var builder = new ConfigurationBuilder();
            var loaded = ServerConfigurationSources.AddSettingsFiles(builder, dataFile, appFile);
            CollectionAssert.AreEqual(new[] { appFile }, loaded.ToArray());
            Assert.AreEqual("http://+:8080", builder.Build()["Kestrel:Endpoints:Http:Url"]);

            File.WriteAllText(dataFile, """
                { "Adjutant": { "Accounts": [ { "Id": "a" } ], "ExternalBaseUrl": "https://operator.example" } }
                """);

            builder = new ConfigurationBuilder();
            loaded = ServerConfigurationSources.AddSettingsFiles(builder, dataFile, appFile);
            using var root = (IDisposable)builder.Build();
            var config = (IConfiguration)root;

            CollectionAssert.AreEqual(new[] { appFile, dataFile }, loaded.ToArray());
            Assert.AreEqual("https://operator.example", config["Adjutant:ExternalBaseUrl"], "operator settings win");
            Assert.AreEqual("a", config["Adjutant:Accounts:0:Id"]);
            Assert.AreEqual("http://+:8080", config["Kestrel:Endpoints:Http:Url"],
                "the shipped endpoint stays in effect once the operator's file exists");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [TestMethod]
    public void SameFileForBothIsRegisteredOnce()
    {
        var dir = Path.Combine(Path.GetTempPath(), "adjutant-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "appsettings.json");
            File.WriteAllText(file, "{}");

            var builder = new ConfigurationBuilder();
            var loaded = ServerConfigurationSources.AddSettingsFiles(builder, file, file);

            Assert.HasCount(1, builder.Sources);
            CollectionAssert.AreEqual(new[] { file }, loaded.ToArray());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
