using System.Text.Json;

namespace Adjutant.Tests.Helpers;

/// <summary>
/// Asserts the payload a fan-out read tool returns on a server with no (enabled) accounts:
/// an empty result plus a single account-less entry in <c>warnings</c>.
/// </summary>
internal static class NoAccountsAssert
{
    /// <param name="resultProperty">The result array that must be empty, or null when the
    /// tool's payload has none (get_contextual_email_summary).</param>
    public static void IsEmptyWithWarning(string json, string? resultProperty)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (resultProperty is not null)
            Assert.AreEqual(0, root.GetProperty(resultProperty).GetArrayLength(),
                $"Expected '{resultProperty}' to be empty");

        var warnings = root.GetProperty("warnings");
        Assert.AreEqual(JsonValueKind.Array, warnings.ValueKind, "Expected a warnings array");
        Assert.AreEqual(1, warnings.GetArrayLength());

        var entry = warnings[0];
        Assert.IsFalse(entry.TryGetProperty("accountId", out _),
            "The no-accounts warning is not about any one account");

        var text = entry.GetProperty("warning").GetString();
        Assert.IsNotNull(text);
        StringAssert.Contains(text, "No enabled accounts are configured");
        StringAssert.Contains(text, "admin console");
    }
}
