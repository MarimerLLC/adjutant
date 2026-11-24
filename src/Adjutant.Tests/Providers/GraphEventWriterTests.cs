using System.Text.Json;
using Adjutant.Core.Providers;
using Microsoft.Graph.Models;
using Microsoft.Kiota.Serialization.Json;

namespace Adjutant.Tests.Providers;

/// <summary>
/// The PATCH body the Graph providers (Microsoft 365, Outlook.com) send for update_event.
/// </summary>
[TestClass]
public class GraphEventWriterTests
{
    [TestMethod]
    public void Update_SendsOnlyTheChangedFields()
    {
        var json = Serialize(GraphEventWriter.BuildUpdate(
            "New title", null, null, null, null, null, null, "<p>Agenda</p>", null, null));

        CollectionAssert.AreEquivalent(new[] { "subject", "body" },
            json.EnumerateObject().Select(p => p.Name).Where(n => !n.StartsWith('@')).ToArray());
        Assert.AreEqual("<p>Agenda</p>", json.GetProperty("body").GetProperty("content").GetString());
        Assert.AreEqual("html", json.GetProperty("body").GetProperty("contentType").GetString());
    }

    [TestMethod]
    public void Update_EmptyLocationAndBodyClearThem()
    {
        var json = Serialize(GraphEventWriter.BuildUpdate(
            null, null, null, "", null, null, null, "", null, null));

        Assert.AreEqual("", json.GetProperty("location").GetProperty("displayName").GetString());
        Assert.AreEqual("", json.GetProperty("body").GetProperty("content").GetString());
    }

    [TestMethod]
    public void Update_ClearingRecurrenceSendsAnExplicitNull()
    {
        var json = Serialize(GraphEventWriter.BuildUpdate(
            null, null, null, null, null, null, null, null, RecurrenceChange.Clear, null));

        Assert.IsTrue(json.TryGetProperty("recurrence", out var recurrence), "recurrence must be sent to remove it");
        Assert.AreEqual(JsonValueKind.Null, recurrence.ValueKind);
    }

    [TestMethod]
    public void Update_SetsRecurrenceAnchoredToTheSeriesStart()
    {
        var json = Serialize(GraphEventWriter.BuildUpdate(
            null, null, null, null, null, "America/Chicago", null, null,
            RecurrenceRule.Parse("FREQ=WEEKLY;BYDAY=TU;COUNT=4"), new DateTime(2026, 12, 1, 9, 0, 0)));

        var recurrence = json.GetProperty("recurrence");
        Assert.AreEqual("weekly", recurrence.GetProperty("pattern").GetProperty("type").GetString());
        Assert.AreEqual("tuesday", recurrence.GetProperty("pattern").GetProperty("daysOfWeek")[0].GetString());
        Assert.AreEqual("numbered", recurrence.GetProperty("range").GetProperty("type").GetString());
        Assert.AreEqual("2026-12-01", recurrence.GetProperty("range").GetProperty("startDate").GetString());
        Assert.AreEqual(4, recurrence.GetProperty("range").GetProperty("numberOfOccurrences").GetInt32());
    }

    [TestMethod]
    public void Update_RecurrenceWithoutAKnownStartIsRefused()
    {
        Assert.ThrowsExactly<Adjutant.Core.Services.ProviderOperationException>(() => GraphEventWriter.BuildUpdate(
            null, null, null, null, null, null, null, null, RecurrenceRule.Parse("FREQ=DAILY"), null));
    }

    [TestMethod]
    public void LocalStart_ConvertsUtcToTheRequestedZone()
    {
        // 2026-12-02 03:30Z is the evening of Dec 1 in Chicago, so the series starts on Dec 1.
        var evt = new Event { Start = new DateTimeTimeZone { DateTime = "2026-12-02T03:30:00.0000000", TimeZone = "UTC" } };

        Assert.AreEqual(new DateTime(2026, 12, 1, 21, 30, 0), GraphEventWriter.LocalStart(evt, "America/Chicago"));
        Assert.AreEqual(new DateTime(2026, 12, 2, 3, 30, 0), GraphEventWriter.LocalStart(evt, null));
    }

    [TestMethod]
    public void LocalStart_KeepsAnAllDayDate()
    {
        var evt = new Event { IsAllDay = true, Start = new DateTimeTimeZone { DateTime = "2026-12-02T00:00:00.0000000", TimeZone = "UTC" } };

        Assert.AreEqual(new DateTime(2026, 12, 2), GraphEventWriter.LocalStart(evt, "America/Chicago"));
    }

    private static JsonElement Serialize(Event evt)
    {
        using var writer = new JsonSerializationWriter();
        writer.WriteObjectValue(null, evt);
        using var stream = writer.GetSerializedContent();
        using var reader = new StreamReader(stream);
        return JsonDocument.Parse(reader.ReadToEnd()).RootElement.Clone();
    }
}
