using Adjutant.HttpServer.BlazorAdmin;

namespace Adjutant.Tests.BlazorAdmin;

/// <summary>#132: the startup "next steps" block for a server with no accounts.</summary>
[TestClass]
public sealed class FirstRunGuideTests
{
    private static FirstRunGuide.State Fresh(
        string baseUrl = "http://<host>:8080",
        string[]? providers = null,
        bool tokenLoginAllowed = true,
        bool adminTokenConfigured = false,
        bool claimNeeded = true,
        bool mcpKeyRequired = true) =>
        new(baseUrl, providers ?? [], tokenLoginAllowed, adminTokenConfigured, claimNeeded, mcpKeyRequired);

    private static string Text(FirstRunGuide.State state) => string.Join("\n", FirstRunGuide.BuildLines(state));

    [TestMethod]
    public void AFreshServerPointsAtTheGeneratedTokenAndTheRealAddress()
    {
        var text = Text(Fresh());

        StringAssert.Contains(text, "no accounts are configured yet");
        StringAssert.Contains(text, "1. Open http://<host>:8080/admin/ui and sign in with the admin token from the first-start banner");
        StringAssert.Contains(text, "admin-token.json");
        StringAssert.Contains(text, "2. Add an email/calendar account (http://<host>:8080/admin/ui/accounts/add)");
        StringAssert.Contains(text, "3. Point your MCP client at http://<host>:8080/ with an MCP API key");
        StringAssert.Contains(text, FirstRunGuide.DocsUrl);
        Assert.IsFalse(text.Contains("claim code"), "with no provider there is nothing to claim with yet");
    }

    [TestMethod]
    public void AConfiguredTokenIsNamedByItsVariable()
    {
        var text = Text(Fresh(adminTokenConfigured: true));

        StringAssert.Contains(text, "sign in with the configured admin token (ADJUTANT_ADMIN_TOKEN or Adjutant:AdminToken).");
        Assert.IsFalse(text.Contains("first-start banner)"));
    }

    [TestMethod]
    public void ProvidersAreOfferedAlongsideTheTokenWithTheClaimCodeWhileUnclaimed()
    {
        var text = Text(Fresh(providers: ["Google", "Microsoft"], adminTokenConfigured: true));

        StringAssert.Contains(text, "sign in with the configured admin token (ADJUTANT_ADMIN_TOKEN or Adjutant:AdminToken), or with Google or Microsoft, then enter the claim code above.");
    }

    [TestMethod]
    public void OnceTokenLoginIsOffOnlyProvidersAreNamed()
    {
        var text = Text(Fresh(providers: ["Google"], tokenLoginAllowed: false, claimNeeded: false));

        StringAssert.Contains(text, "and sign in with Google.");
        Assert.IsFalse(text.Contains("admin token"));
        Assert.IsFalse(text.Contains("claim code"));
    }

    [TestMethod]
    public void WithNoSignInMethodTheStepSaysHowToEnableOne()
    {
        var text = Text(Fresh(tokenLoginAllowed: false));

        StringAssert.Contains(text, "no sign-in method is enabled");
        StringAssert.Contains(text, "AdminAuth:AllowTokenLogin");
    }

    [TestMethod]
    public void WithoutKeyEnforcementTheMcpStepSaysNoKeyIsNeeded()
    {
        StringAssert.Contains(Text(Fresh(mcpKeyRequired: false)), "no API key needed");
    }

    [TestMethod]
    [DataRow("https://adjutant.example.ts.net/", new[] { "http://[::]:8080" }, "https://adjutant.example.ts.net")]
    [DataRow(null, new[] { "http://[::]:8080" }, "http://<host>:8080")]
    [DataRow(null, new[] { "http://+:8080" }, "http://<host>:8080")]
    [DataRow(null, new[] { "http://0.0.0.0:5000", "https://0.0.0.0:5001" }, "https://<host>:5001")]
    [DataRow(null, new[] { "http://localhost:5000" }, "http://localhost:5000")]
    [DataRow("not a url", new[] { "http://localhost:5000" }, "http://localhost:5000")]
    [DataRow("/adjutant", new[] { "http://localhost:5000" }, "http://localhost:5000")]
    [DataRow("ftp://files.example.com", new[] { "http://localhost:5000" }, "http://localhost:5000")]
    [DataRow("file:///srv/adjutant", new[] { "http://localhost:5000" }, "http://localhost:5000")]
    [DataRow(null, new string[0], "http://<host>:8080")]
    public void TheBaseUrlIsTheExternalOneOrTheListeningAddress(string? external, string[] urls, string expected)
    {
        Assert.AreEqual(expected, FirstRunGuide.ResolveBaseUrl(external, urls));
    }
}
