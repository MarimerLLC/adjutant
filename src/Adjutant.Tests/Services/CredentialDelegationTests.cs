using Adjutant.Core.Models;
using Adjutant.Core.Services;
using Adjutant.Tests.Helpers;

namespace Adjutant.Tests.Services;

[TestClass]
public class CredentialDelegationTests
{
    private static AccountInfo JsonAccount(string id, string? source, string? authAccountId, string provider = "json")
    {
        var config = new Dictionary<string, string> { ["oneDrivePath"] = "/cal.json" };
        if (source is not null) config["source"] = source;
        if (authAccountId is not null) config["authAccountId"] = authAccountId;
        return TestData.CreateAccount(id: id, provider: provider, providerConfig: config);
    }

    [TestMethod]
    [DataRow("json")]
    [DataRow("json-calendar")]
    public void GetLenderId_OneDriveJsonWithAuthAccountId_ReturnsIt(string provider)
    {
        Assert.AreEqual("work", CredentialDelegation.GetLenderId(JsonAccount("cal", "onedrive", "work", provider)));
    }

    [TestMethod]
    public void GetLenderId_CaseInsensitiveKeys()
    {
        var account = TestData.CreateAccount(id: "cal", provider: "json", providerConfig: new()
        {
            ["Source"] = "OneDrive",
            ["AuthAccountId"] = "work"
        });

        Assert.AreEqual("work", CredentialDelegation.GetLenderId(account));
    }

    [TestMethod]
    public void GetLenderId_LocalSource_ReturnsNull()
    {
        // A local file needs no credentials, so a leftover authAccountId borrows nothing.
        Assert.IsNull(CredentialDelegation.GetLenderId(JsonAccount("cal", "local", "work")));
        Assert.IsNull(CredentialDelegation.GetLenderId(JsonAccount("cal", null, "work")));
    }

    [TestMethod]
    public void GetLenderId_NoAuthAccountId_ReturnsNull()
    {
        Assert.IsNull(CredentialDelegation.GetLenderId(JsonAccount("cal", "onedrive", null)));
        Assert.IsNull(CredentialDelegation.GetLenderId(JsonAccount("cal", "onedrive", " ")));
    }

    [TestMethod]
    public void GetLenderId_NonJsonProvider_ReturnsNull()
    {
        var account = TestData.CreateAccount(id: "work", provider: "microsoft365",
            providerConfig: new() { ["source"] = "onedrive", ["authAccountId"] = "other" });

        Assert.IsNull(CredentialDelegation.GetLenderId(account));
    }

    [TestMethod]
    public void FindBorrowers_ReturnsOnlyAccountsBorrowingFromTheLender()
    {
        var accounts = new[]
        {
            TestData.CreateAccount(id: "work"),
            JsonAccount("team-cal", "onedrive", "work"),
            JsonAccount("other-cal", "onedrive", "home"),
            JsonAccount("local-cal", "local", "work"),
        };

        var borrowers = CredentialDelegation.FindBorrowers(accounts, "work");

        CollectionAssert.AreEqual(new[] { "team-cal" }, borrowers.Select(b => b.Id).ToArray());
    }

    [TestMethod]
    public void CheckLender_EnabledLender_IsAllowed()
    {
        Assert.IsNull(CredentialDelegation.CheckLender(
            JsonAccount("team-cal", "onedrive", "work"), "work", TestData.CreateAccount(id: "work")));
    }

    [TestMethod]
    public void CheckLender_DisabledLender_IsRefused()
    {
        var problem = CredentialDelegation.CheckLender(
            JsonAccount("team-cal", "onedrive", "work"), "work", TestData.CreateAccount(id: "work", enabled: false));

        Assert.AreEqual(
            "Account 'team-cal' gets its OneDrive credentials from 'work', which is disabled. " +
            "Re-enable 'work' or point authAccountId at another account.",
            problem);
    }

    [TestMethod]
    public void CheckLender_MissingLender_IsRefused()
    {
        var problem = CredentialDelegation.CheckLender(JsonAccount("team-cal", "onedrive", "work"), "work", null);

        Assert.IsNotNull(problem);
        StringAssert.Contains(problem, "'work' which was not found");
    }
}
