using Adjutant.Core.Security;
using Microsoft.Extensions.Logging.Abstractions;

namespace Adjutant.Tests.Security;

[TestClass]
public sealed class AdminTokenStoreTests
{
    private string _dir = "";
    private string FilePath => Path.Combine(_dir, AdminTokenStore.FileName);

    [TestInitialize]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "adjutant-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private AdminTokenStore Create(string? configured = null) =>
        new(NullLogger<AdminTokenStore>.Instance, () => configured, FilePath);

    [TestMethod]
    public void WithNoTokenNothingMatches()
    {
        var store = Create();

        Assert.IsFalse(store.HasToken);
        Assert.IsFalse(store.Matches("anything"));
        Assert.IsFalse(store.Matches(""));
        Assert.IsFalse(store.Matches(null));
    }

    [TestMethod]
    public void AGeneratedTokenIsAcceptedAndOnlyItsHashIsStored()
    {
        var store = Create();
        var secret = store.EnsureToken();

        Assert.IsNotNull(secret);
        StringAssert.StartsWith(secret, AdminTokenStore.SecretPrefix);
        Assert.IsTrue(store.HasToken);
        Assert.IsFalse(store.IsConfigured);
        Assert.IsTrue(store.Matches(secret));
        Assert.IsFalse(store.Matches(secret + "x"));
        Assert.IsFalse(store.Matches("anything"));

        var file = File.ReadAllText(FilePath);
        Assert.IsFalse(file.Contains(secret), "the secret must not be written to disk");
        Assert.IsFalse(file.Contains(secret[AdminTokenStore.SecretPrefix.Length..]));
    }

    [TestMethod]
    public void TheStoredTokenSurvivesARestartAndIsNotRegenerated()
    {
        var secret = Create().EnsureToken()!;

        var restarted = Create();
        Assert.IsTrue(restarted.HasToken);
        Assert.IsNull(restarted.EnsureToken(), "a stored token must not be replaced on the next start");
        Assert.IsTrue(restarted.Matches(secret));
    }

    [TestMethod]
    public void AConfiguredTokenWinsOverTheStoredOne()
    {
        var stored = Create().EnsureToken()!;

        var store = Create(configured: "operator-token");
        Assert.IsTrue(store.IsConfigured);
        Assert.IsNull(store.EnsureToken());
        Assert.IsTrue(store.Matches("operator-token"));
        Assert.IsFalse(store.Matches(stored), "the generated token stops working once one is configured");
    }

    [TestMethod]
    public void AConfiguredTokenIsReadOnEveryCheck()
    {
        string? configured = null;
        var store = new AdminTokenStore(NullLogger<AdminTokenStore>.Instance, () => configured, FilePath);
        Assert.IsFalse(store.Matches("later"));

        configured = "later";
        Assert.IsTrue(store.Matches("later"));
    }

    [TestMethod]
    public void ABlankConfiguredTokenCountsAsNone()
    {
        // docker-compose passes ADJUTANT_ADMIN_TOKEN= when the variable is unset.
        var store = Create(configured: "  ");

        Assert.IsFalse(store.IsConfigured);
        Assert.IsFalse(store.Matches("  "));
        Assert.IsNotNull(store.EnsureToken());
    }

    [TestMethod]
    public void AMalformedFileIsReplacedByANewToken()
    {
        File.WriteAllText(FilePath, "{ not json");

        var store = Create();
        Assert.IsFalse(store.HasToken);

        var secret = store.EnsureToken();
        Assert.IsNotNull(secret);
        Assert.IsTrue(Create().Matches(secret));
    }
}
