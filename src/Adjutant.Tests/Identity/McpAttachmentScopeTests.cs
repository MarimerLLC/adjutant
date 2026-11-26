using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Adjutant.Core.Models;
using Adjutant.Core.Security;
using Adjutant.Core.Services;
using Adjutant.HttpServer.Endpoints;
using Microsoft.Extensions.DependencyInjection;

namespace Adjutant.Tests.Identity;

[TestClass]
[DoNotParallelize]
public class McpAttachmentScopeTests
{
    [TestMethod]
    public async Task KeyScope_AppliesWhenDownloadingAndDeletingStashedAttachments()
    {
        await using var app = new AdjutantApp(AdjutantApp.DefaultSettings(accounts:
        [
            new AccountInfo { Id = "allowed", DisplayName = "Allowed", Provider = "imap" },
            new AccountInfo { Id = "excluded", DisplayName = "Excluded", Provider = "imap" }
        ]));
        var keys = app.Services.GetRequiredService<IMcpKeyStore>();
        var (_, scopedKey) = keys.Create("scoped", allowedAccounts: ["allowed"]);
        var (_, unrestrictedKey) = keys.Create("unrestricted");
        var store = app.Services.GetRequiredService<IAttachmentStore>();
        var bytes = "test attachment"u8.ToArray();
        var excluded = store.Put("excluded.txt", "text/plain", bytes, sourceAccountId: "excluded")!;
        var allowed = store.Put("allowed.txt", "text/plain", bytes, sourceAccountId: "allowed")!;
        var uploaded = store.Put("uploaded.txt", "text/plain", bytes)!;

        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", scopedKey);
        Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync($"/attachments/{excluded.Id}")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await client.DeleteAsync($"/attachments/{excluded.Id}")).StatusCode);
        CollectionAssert.AreEqual(bytes, await client.GetByteArrayAsync($"/attachments/{allowed.Id}"));
        CollectionAssert.AreEqual(bytes, await client.GetByteArrayAsync($"/attachments/{uploaded.Id}"));

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", unrestrictedKey);
        CollectionAssert.AreEqual(bytes, await client.GetByteArrayAsync($"/attachments/{excluded.Id}"));

        app.UpdateSettings(AdjutantApp.DefaultSettings(accounts:
        [
            new AccountInfo { Id = "excluded", DisplayName = "Now owned", Provider = "imap", Owner = AdjutantApp.Owner }
        ]));
        Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync($"/attachments/{excluded.Id}")).StatusCode);
    }

    [TestMethod]
    public async Task Uploaded_attachments_can_only_be_downloaded_or_deleted_by_their_owner()
    {
        const string otherOwner = "other@example.com";
        await using var app = new AdjutantApp(AdjutantApp.DefaultSettings(
            allowedEmails: [AdjutantApp.Owner, otherOwner], requireApiKey: false));
        var keys = app.Services.GetRequiredService<IMcpKeyStore>();
        var (_, ownerKey) = keys.Create("owner", owner: AdjutantApp.Owner);
        var (_, anotherOwnerKey) = keys.Create("same user", owner: AdjutantApp.Owner);
        var (_, otherKey) = keys.Create("other", owner: otherOwner);
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerKey);
        using var upload = new MultipartFormDataContent
        {
            { new StringContent("test attachment"), "file", "test.txt" }
        };
        using var response = await client.PostAsync("/attachments", upload);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        var stored = await response.Content.ReadFromJsonAsync<AttachmentEndpoints.UploadResponse>();
        var path = $"/attachments/{stored!.AttachmentId}";

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", otherKey);
        Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await client.DeleteAsync(path)).StatusCode);
        client.DefaultRequestHeaders.Authorization = null;
        Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", anotherOwnerKey);
        Assert.AreEqual("test attachment", await client.GetStringAsync(path));
        Assert.AreEqual(HttpStatusCode.NoContent, (await client.DeleteAsync(path)).StatusCode);
    }
}
