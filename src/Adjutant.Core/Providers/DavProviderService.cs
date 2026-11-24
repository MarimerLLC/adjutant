using System.Collections.Concurrent;
using System.Net;
using Adjutant.Core.Models;
using Adjutant.Core.Providers.Dav;
using Adjutant.Core.Security;
using Adjutant.Core.Services;
using Microsoft.Extensions.Logging;

namespace Adjutant.Core.Providers;

/// <summary>
/// CalDAV + CardDAV provider. Email methods are unsupported — pair with IMAP for mail.
/// Auth is HTTP Basic (username + app-specific password), encrypted at rest via
/// <see cref="PasswordProtector"/>.
/// </summary>
public sealed class DavProviderService : IDavProviderService
{
    private readonly IAccountRegistry _accountRegistry;
    private readonly PasswordProtector _passwordProtector;
    private readonly DavHttpClient _http;
    private readonly DavDiscovery _discovery;
    private readonly ILogger<DavProviderService> _logger;

    // Per account + CardDAV entry URL: whether the server honours an empty addressbook-query
    // filter. Learned on first use so compliant servers never pay for the fallback probe.
    private readonly ConcurrentDictionary<string, AddressbookFilterMode> _addressbookFilterModes = new();

    private enum AddressbookFilterMode { Unknown, EmptyFilterWorks, NeedsPropertyFilter }

    public DavProviderService(
        IAccountRegistry accountRegistry,
        PasswordProtector passwordProtector,
        DavHttpClient http,
        DavDiscovery discovery,
        ILogger<DavProviderService> logger)
    {
        _accountRegistry = accountRegistry;
        _passwordProtector = passwordProtector;
        _http = http;
        _discovery = discovery;
        _logger = logger;
    }

    // ── Config ───────────────────────────────────────────────────────

    internal async Task<DavAccountConfig> ResolveConfigAsync(string accountId)
    {
        var account = await _accountRegistry.GetAccountAsync(accountId)
            ?? throw new InvalidOperationException($"Account '{accountId}' not found in registry.");

        var pc = new Dictionary<string, string>(account.ProviderConfig, StringComparer.OrdinalIgnoreCase);
        DavHostPolicy.ApplyPreset(Get(pc, "preset"), pc);

        var username = Get(pc, "username");
        var storedPassword = Get(pc, "password");
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(storedPassword))
            throw new InvalidOperationException(
                $"Account '{accountId}' is missing username or password in providerConfig.");

        var caldavUrl = Get(pc, "caldavUrl");
        var carddavUrl = Get(pc, "carddavUrl");
        var enableCalendar = DavHostPolicy.IsEnabled(Get(pc, "enableCalendar"), defaultValue: !string.IsNullOrWhiteSpace(caldavUrl));
        var enableContacts = DavHostPolicy.IsEnabled(Get(pc, "enableContacts"), defaultValue: !string.IsNullOrWhiteSpace(carddavUrl));

        if (!enableCalendar && !enableContacts)
            throw new InvalidOperationException(
                $"Account '{accountId}' has neither calendar nor contacts enabled for the dav provider.");

        return new DavAccountConfig(
            AccountId: accountId,
            Username: username,
            Password: _passwordProtector.Unprotect(storedPassword),
            Preset: Get(pc, "preset"),
            CalDavUrl: caldavUrl,
            CardDavUrl: carddavUrl,
            CalendarHomeSet: Get(pc, "calendarHomeSet"),
            AddressbookHomeSet: Get(pc, "addressbookHomeSet"),
            EnableCalendar: enableCalendar,
            EnableContacts: enableContacts);

        static string? Get(IDictionary<string, string> d, string key) =>
            d.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
    }

    private static Uri EntryUri(DavAccountConfig cfg, DavServiceKind kind)
    {
        var raw = kind == DavServiceKind.CalDav ? cfg.CalDavUrl : cfg.CardDavUrl;
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException($"{kind} URL is not configured for account '{cfg.AccountId}'.");
        return new Uri(raw.EndsWith('/') ? raw : raw + "/");
    }

    // ── Calendar ─────────────────────────────────────────────────────

    public async Task<IEnumerable<CalendarInfo>> ListCalendarsAsync(
        string accountId, CancellationToken cancellationToken = default)
    {
        var cfg = await ResolveConfigAsync(accountId);
        EnsureCalendar(cfg);

        var home = await _discovery.GetCalendarHomeAsync(cfg, cancellationToken);
        var entry = EntryUri(cfg, DavServiceKind.CalDav);
        var result = await _http.PropfindAsync(
            home, cfg.Username, cfg.Password, DavXml.PropfindCalendarCollections(),
            cfg.Preset, DavServiceKind.CalDav, entry, depth: 1, cancellationToken);
        EnsureHttpSuccess(result, "list calendars");

        var responses = DavXml.ParseMultiStatus(result.Body, result.FinalUri);
        var calendars = new List<CalendarInfo>();
        var first = true;

        foreach (var r in responses.Where(r => r.IsCalendar && r.SupportsVevent))
        {
            calendars.Add(new CalendarInfo
            {
                Id = DavResourceId.Encode(r.Href),
                AccountId = accountId,
                Name = string.IsNullOrWhiteSpace(r.DisplayName) ? "Calendar" : r.DisplayName,
                CanEdit = r.CanWrite,
                IsDefault = first,
                Owner = cfg.Username
            });
            first = false;
        }

        return calendars;
    }

    public async Task<IEnumerable<CalendarEvent>> GetCalendarEventsAsync(
        string accountId,
        string? calendarId = null,
        DateTime? startDate = null,
        DateTime? endDate = null,
        int count = 50,
        CancellationToken cancellationToken = default)
    {
        var cfg = await ResolveConfigAsync(accountId);
        EnsureCalendar(cfg);

        var calendars = (await ListCalendarsAsync(accountId, cancellationToken)).ToList();
        if (calendars.Count == 0)
            return [];

        // "primary"/"default" mean the same as an omitted calendarId: every calendar.
        List<CalendarInfo> targets;
        if (CalendarIdAliases.IsDefaultCalendar(calendarId))
        {
            targets = calendars;
        }
        else
        {
            targets = calendars.Where(c => c.Id == calendarId).ToList();
            if (targets.Count == 0)
                throw new ProviderOperationException(CalendarIdAliases.UnknownCalendarMessage(calendarId));
        }

        // The bounds are UTC instants whatever their Kind (IProviderService contract).
        var start = DateTime.SpecifyKind(startDate ?? DateTime.UtcNow.Date, DateTimeKind.Utc);
        var end = DateTime.SpecifyKind(endDate ?? start.AddDays(7), DateTimeKind.Utc);
        var windowStart = new DateTimeOffset(start);
        var windowEnd = new DateTimeOffset(end);
        var entry = EntryUri(cfg, DavServiceKind.CalDav);
        var events = new List<CalendarEvent>();

        foreach (var cal in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var calHref = DavResourceId.Decode(cal.Id);
            // Servers evaluate floating and all-day times in their own zone, so ask for a day
            // either side and filter on the mapped UTC range below.
            var report = await _http.ReportAsync(
                calHref, cfg.Username, cfg.Password,
                DavXml.CalendarQuery(start.AddDays(-1), end.AddDays(1)),
                cfg.Preset, DavServiceKind.CalDav, entry, cancellationToken);
            EnsureHttpSuccess(report, "calendar-query");

            foreach (var item in DavXml.ParseMultiStatus(report.Body, report.FinalUri))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(item.CalendarData))
                    continue;

                var calendar = IcalMapper.LoadCalendar(item.CalendarData);
                if (calendar is null)
                    continue;

                foreach (var expanded in IcalMapper.Expand(calendar, start, end, cancellationToken))
                {
                    var mapped = IcalMapper.ToCalendarEvent(
                        expanded.Event, accountId, cal.Id, DavEventId.Format(item.Href, expanded.RecurrenceKey),
                        expanded.Occurrence, cfg.Username);
                    if (mapped.Start < windowEnd && mapped.End > windowStart)
                        events.Add(mapped);
                }

                // Keep only the earliest `count` events so a broad range can't grow the list unbounded.
                if (count >= 0 && events.Count > count * 2 + 100)
                {
                    events.Sort((a, b) => a.Start.CompareTo(b.Start));
                    events.RemoveRange(count, events.Count - count);
                }
            }
        }

        return events.OrderBy(e => e.Start).Take(count).ToList();
    }

    public async Task<CalendarEvent?> GetCalendarEventDetailsAsync(
        string accountId, string calendarId, string eventId,
        CancellationToken cancellationToken = default)
    {
        var cfg = await ResolveConfigAsync(accountId);
        EnsureCalendar(cfg);

        var entry = EntryUri(cfg, DavServiceKind.CalDav);
        var target = await ResolveEventAsync(accountId, calendarId, eventId, cancellationToken);
        var get = await _http.GetAsync(target.Href, cfg.Username, cfg.Password, cfg.Preset,
            DavServiceKind.CalDav, entry, cancellationToken);
        if (get.StatusCode == HttpStatusCode.NotFound)
            return null;
        EnsureHttpSuccess(get, "get event");

        var calendar = IcalMapper.LoadCalendar(get.Body);
        if (calendar is null)
            return null;

        if (target.RecurrenceKey is null)
        {
            var evt = IcalMapper.GetPrimaryEvent(calendar);
            return evt is null
                ? null
                : IcalMapper.ToCalendarEvent(evt, accountId, target.CalendarId, eventId, ownerEmail: cfg.Username);
        }

        var occurrence = IcalMapper.FindOccurrence(calendar, target.RecurrenceKey);
        return occurrence is null
            ? null
            : IcalMapper.ToCalendarEvent(occurrence.Value.Event, accountId, target.CalendarId, eventId,
                occurrence.Value.Occurrence, cfg.Username);
    }

    public async Task<EventWriteResult> CreateEventAsync(
        string accountId,
        string? calendarId,
        string subject,
        DateTime start,
        DateTime end,
        string? location = null,
        List<string>? attendees = null,
        string? body = null,
        string? timeZone = null,
        bool isAllDay = false,
        string? recurrence = null,
        bool notifyAttendees = true,
        CancellationToken cancellationToken = default)
    {
        var change = RecurrenceRule.Parse(recurrence);
        var cfg = await ResolveConfigAsync(accountId);
        EnsureCalendar(cfg);

        var calendars = await ListCalendarsAsync(accountId, cancellationToken);
        CalendarInfo calendar;
        if (CalendarIdAliases.IsDefaultCalendar(calendarId))
        {
            calendar = calendars.FirstOrDefault(c => c.CanEdit)
                ?? throw new ProviderOperationException($"Account '{accountId}' has no writable calendars.");
        }
        else
        {
            calendar = calendars.FirstOrDefault(c => c.Id == calendarId)
                ?? throw new ProviderOperationException(CalendarIdAliases.UnknownCalendarMessage(calendarId));
        }

        var calHref = DavResourceId.Decode(calendar.Id);
        var uid = Guid.NewGuid().ToString();
        var objectHref = new Uri(calHref, $"{uid}.ics");
        var ics = IcalMapper.BuildNewEventIcs(uid, subject, start, end, location, attendees, body, timeZone, isAllDay, change);
        var entry = EntryUri(cfg, DavServiceKind.CalDav);

        var put = await _http.PutAsync(
            objectHref, cfg.Username, cfg.Password, ics, "text/calendar; charset=utf-8",
            cfg.Preset, DavServiceKind.CalDav, entry, ifMatch: null, cancellationToken);
        EnsureHttpSuccess(put, "create event");

        _logger.LogInformation("Created CalDAV event {Href} on account {AccountId}", objectHref, accountId);
        // CalDAV has no client-side send; servers with implicit scheduling notify by themselves.
        return new EventWriteResult(DavResourceId.Encode(objectHref), AttendeeNotifications.ServerDependent);
    }

    public async Task<EventWriteResult> UpdateEventAsync(
        string accountId,
        string calendarId,
        string eventId,
        string? subject = null,
        DateTime? start = null,
        DateTime? end = null,
        string? location = null,
        List<string>? attendees = null,
        string? timeZone = null,
        bool? isAllDay = null,
        string? body = null,
        string? recurrence = null,
        bool notifyAttendees = true,
        CancellationToken cancellationToken = default)
    {
        var change = RecurrenceRule.Parse(recurrence);
        var cfg = await ResolveConfigAsync(accountId);
        EnsureCalendar(cfg);

        var entry = EntryUri(cfg, DavServiceKind.CalDav);
        var target = await ResolveEventAsync(accountId, calendarId, eventId, cancellationToken);
        RejectSingleOccurrence(target, "Updating");
        var get = await _http.GetAsync(target.Href, cfg.Username, cfg.Password, cfg.Preset,
            DavServiceKind.CalDav, entry, cancellationToken);
        EnsureHttpSuccess(get, "get event for update");

        var updated = IcalMapper.ApplyUpdates(get.Body, subject, start, end, location, attendees, timeZone, isAllDay, body, change);
        var put = await _http.PutAsync(
            target.Href, cfg.Username, cfg.Password, updated, "text/calendar; charset=utf-8",
            cfg.Preset, DavServiceKind.CalDav, entry, ifMatch: get.Etag, cancellationToken);
        EnsureHttpSuccess(put, "update event");
        return new EventWriteResult(eventId, AttendeeNotifications.ServerDependent);
    }

    public async Task DeleteEventAsync(
        string accountId, string calendarId, string eventId,
        CancellationToken cancellationToken = default)
    {
        var cfg = await ResolveConfigAsync(accountId);
        EnsureCalendar(cfg);

        var entry = EntryUri(cfg, DavServiceKind.CalDav);
        var target = await ResolveEventAsync(accountId, calendarId, eventId, cancellationToken);

        if (target.RecurrenceKey is not null)
        {
            // One occurrence: exclude it from the series rather than deleting the whole resource.
            var get = await _http.GetAsync(target.Href, cfg.Username, cfg.Password, cfg.Preset,
                DavServiceKind.CalDav, entry, cancellationToken);
            if (get.StatusCode == HttpStatusCode.NotFound)
                return;
            EnsureHttpSuccess(get, "get event for delete");

            var remaining = IcalMapper.ExcludeOccurrence(get.Body, target.RecurrenceKey);
            if (remaining is not null)
            {
                var put = await _http.PutAsync(
                    target.Href, cfg.Username, cfg.Password, remaining, "text/calendar; charset=utf-8",
                    cfg.Preset, DavServiceKind.CalDav, entry, ifMatch: get.Etag, cancellationToken);
                EnsureHttpSuccess(put, "delete occurrence");
                return;
            }
        }

        var del = await _http.DeleteAsync(target.Href, cfg.Username, cfg.Password, cfg.Preset,
            DavServiceKind.CalDav, entry, ifMatch: null, cancellationToken);
        if (del.StatusCode != HttpStatusCode.NotFound)
            EnsureHttpSuccess(del, "delete event");
    }

    public async Task RespondToEventAsync(
        string accountId,
        string calendarId,
        string eventId,
        string response,
        string? comment = null,
        CancellationToken cancellationToken = default)
    {
        var cfg = await ResolveConfigAsync(accountId);
        EnsureCalendar(cfg);

        var entry = EntryUri(cfg, DavServiceKind.CalDav);
        var target = await ResolveEventAsync(accountId, calendarId, eventId, cancellationToken);
        RejectSingleOccurrence(target, "Responding to");
        var get = await _http.GetAsync(target.Href, cfg.Username, cfg.Password, cfg.Preset,
            DavServiceKind.CalDav, entry, cancellationToken);
        EnsureHttpSuccess(get, "get event for respond");

        var updated = IcalMapper.ApplyPartStat(get.Body, cfg.Username, response);
        var put = await _http.PutAsync(
            target.Href, cfg.Username, cfg.Password, updated, "text/calendar; charset=utf-8",
            cfg.Preset, DavServiceKind.CalDav, entry, ifMatch: get.Etag, cancellationToken);
        EnsureHttpSuccess(put, "respond to event");
    }

    // ── Contacts ─────────────────────────────────────────────────────

    public async Task<IEnumerable<Contact>> GetContactsAsync(
        string accountId, int count = 50, CancellationToken cancellationToken = default)
    {
        var all = await QueryContactsAsync(accountId, query: null, cancellationToken);
        return all.Take(count);
    }

    public async Task<IEnumerable<Contact>> SearchContactsAsync(
        string accountId, string query, int count = 50, CancellationToken cancellationToken = default)
    {
        var all = await QueryContactsAsync(accountId, query, cancellationToken);
        return all.Take(count);
    }

    public async Task<Contact?> GetContactDetailsAsync(
        string accountId, string contactId, CancellationToken cancellationToken = default)
    {
        var cfg = await ResolveConfigAsync(accountId);
        EnsureContacts(cfg);

        var entry = EntryUri(cfg, DavServiceKind.CardDav);
        var (href, book) = await ResolveContactResourceAsync(cfg, contactId, cancellationToken);
        var get = await _http.GetAsync(href, cfg.Username, cfg.Password, cfg.Preset,
            DavServiceKind.CardDav, entry, cancellationToken);
        if (get.StatusCode == HttpStatusCode.NotFound)
            return null;
        EnsureHttpSuccess(get, "get contact");

        return VCardMapper.ToContact(get.Body, accountId, contactId, get.Etag, AddressBookName(book));
    }

    public Task<string> CreateContactAsync(
        string accountId,
        string displayName,
        string? givenName = null,
        string? surname = null,
        List<string>? emailAddresses = null,
        List<string>? phoneNumbers = null,
        string? jobTitle = null,
        string? companyName = null,
        string? notes = null,
        CancellationToken cancellationToken = default) =>
        CreateContactInAddressBookAsync(accountId, displayName, null, givenName, surname,
            emailAddresses, phoneNumbers, jobTitle, companyName, notes, cancellationToken);

    public async Task<string> CreateContactInAddressBookAsync(
        string accountId,
        string displayName,
        string? addressBookName,
        string? givenName = null,
        string? surname = null,
        List<string>? emailAddresses = null,
        List<string>? phoneNumbers = null,
        string? jobTitle = null,
        string? companyName = null,
        string? notes = null,
        CancellationToken cancellationToken = default)
    {
        var cfg = await ResolveConfigAsync(accountId);
        EnsureContacts(cfg);

        var book = await SelectAddressBookAsync(cfg, addressBookName, cancellationToken);
        var uid = Guid.NewGuid().ToString();
        var href = new Uri(book, $"{uid}.vcf");
        var vcard = VCardMapper.ToVCard(uid, displayName, givenName, surname,
            emailAddresses, phoneNumbers, jobTitle, companyName, notes);
        var entry = EntryUri(cfg, DavServiceKind.CardDav);

        var put = await _http.PutAsync(
            href, cfg.Username, cfg.Password, vcard, "text/vcard; charset=utf-8",
            cfg.Preset, DavServiceKind.CardDav, entry, ifMatch: null, cancellationToken);
        EnsureHttpSuccess(put, "create contact");

        return DavResourceId.Encode(href);
    }

    public async Task UpdateContactAsync(
        string accountId,
        string contactId,
        string? displayName = null,
        string? givenName = null,
        string? surname = null,
        List<string>? emailAddresses = null,
        List<string>? phoneNumbers = null,
        string? jobTitle = null,
        string? companyName = null,
        string? notes = null,
        string? etag = null,
        CancellationToken cancellationToken = default)
    {
        var cfg = await ResolveConfigAsync(accountId);
        EnsureContacts(cfg);

        var entry = EntryUri(cfg, DavServiceKind.CardDav);
        var href = await ValidateContactResourceAsync(cfg, contactId, cancellationToken);
        var get = await _http.GetAsync(href, cfg.Username, cfg.Password, cfg.Preset,
            DavServiceKind.CardDav, entry, cancellationToken);
        EnsureHttpSuccess(get, "get contact for update");

        var updated = VCardMapper.ApplyUpdates(get.Body, displayName, givenName, surname,
            emailAddresses, phoneNumbers, jobTitle, companyName, notes);
        var put = await _http.PutAsync(
            href, cfg.Username, cfg.Password, updated, "text/vcard; charset=utf-8",
            cfg.Preset, DavServiceKind.CardDav, entry, ifMatch: etag ?? get.Etag, cancellationToken);
        EnsureHttpSuccess(put, "update contact");
    }

    public async Task DeleteContactAsync(
        string accountId, string contactId, CancellationToken cancellationToken = default)
    {
        var cfg = await ResolveConfigAsync(accountId);
        EnsureContacts(cfg);

        var entry = EntryUri(cfg, DavServiceKind.CardDav);
        var href = await ValidateContactResourceAsync(cfg, contactId, cancellationToken);
        var del = await _http.DeleteAsync(href, cfg.Username, cfg.Password, cfg.Preset,
            DavServiceKind.CardDav, entry, ifMatch: null, cancellationToken);
        if (del.StatusCode != HttpStatusCode.NotFound)
            EnsureHttpSuccess(del, "delete contact");
    }

    private async Task<IReadOnlyList<Contact>> QueryContactsAsync(
        string accountId, string? query, CancellationToken cancellationToken)
    {
        var cfg = await ResolveConfigAsync(accountId);
        EnsureContacts(cfg);

        var books = await ListAddressbooksAsync(cfg, cancellationToken);
        var entry = EntryUri(cfg, DavServiceKind.CardDav);
        var contacts = new List<Contact>();

        var modeKey = $"{accountId}|{entry}";
        var mode = _addressbookFilterModes.GetValueOrDefault(modeKey);
        var usePropertyFilter = mode == AddressbookFilterMode.NeedsPropertyFilter;

        var results = new List<IReadOnlyList<DavPropResponse>>(books.Count);
        foreach (var book in books)
        {
            var report = await _http.ReportAsync(
                book.Href, cfg.Username, cfg.Password, DavXml.AddressbookQuery(usePropertyFilter),
                cfg.Preset, DavServiceKind.CardDav, entry, cancellationToken);
            EnsureHttpSuccess(report, usePropertyFilter ? "addressbook-query (property filter)" : "addressbook-query");
            results.Add(DavXml.ParseMultiStatus(report.Body, report.FinalUri));
        }

        if (mode == AddressbookFilterMode.Unknown)
        {
            if (results.Any(HasVCards))
            {
                // The server honours an empty filter; never probe this account again.
                _addressbookFilterModes[modeKey] = AddressbookFilterMode.EmptyFilterWorks;
            }
            else
            {
                // Every book came back empty. Either they really are empty, or this is a
                // server (e.g. Synology Contacts) that returns nothing for an empty filter.
                for (var i = 0; i < books.Count; i++)
                {
                    var retried = await TryPropertyFilteredQueryAsync(cfg, entry, books[i], cancellationToken);
                    if (retried is not null)
                        results[i] = retried;
                }

                var found = results.Sum(r => r.Count(item => !string.IsNullOrWhiteSpace(item.AddressData)));
                if (found > 0)
                {
                    _addressbookFilterModes[modeKey] = AddressbookFilterMode.NeedsPropertyFilter;
                    _logger.LogInformation(
                        "CardDAV server for account {AccountId} returned no vCards for an empty filter but {Count} with a property filter; using the property filter from now on",
                        accountId, found);
                }
                else
                {
                    _logger.LogDebug(
                        "CardDAV address books for account {AccountId} are empty with both filters", accountId);
                }
            }
        }

        // results[i] holds the query results for books[i].
        for (var i = 0; i < books.Count; i++)
        {
            var bookName = AddressBookName(books[i]);
            foreach (var item in results[i])
            {
                if (string.IsNullOrWhiteSpace(item.AddressData))
                    continue;

                var contactId = DavResourceId.Encode(item.Href);
                var contact = VCardMapper.ToContact(
                    item.AddressData, accountId, contactId, item.Etag, bookName);
                if (query is null || ContactMatches(contact, query))
                    contacts.Add(contact);
            }
        }

        return contacts;
    }

    private static bool HasVCards(IReadOnlyList<DavPropResponse> items) =>
        items.Any(item => !string.IsNullOrWhiteSpace(item.AddressData));

    /// <summary>
    /// Best-effort retry of one address book with an explicit property filter. Returns
    /// null (keep the original empty result) when the server rejects the filter or the
    /// request fails, so a server without this quirk never loses a working query.
    /// </summary>
    private async Task<IReadOnlyList<DavPropResponse>?> TryPropertyFilteredQueryAsync(
        DavAccountConfig cfg, Uri entry, DavPropResponse book, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _http.ReportAsync(
                book.Href, cfg.Username, cfg.Password, DavXml.AddressbookQuery(requirePropertyFilter: true),
                cfg.Preset, DavServiceKind.CardDav, entry, cancellationToken);
            if (!result.IsSuccess)
            {
                _logger.LogWarning(
                    "CardDAV property-filter retry for account {AccountId} failed with HTTP {Status}; keeping the empty result",
                    cfg.AccountId, (int)result.StatusCode);
                return null;
            }

            return DavXml.ParseMultiStatus(result.Body, result.FinalUri);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex,
                "CardDAV property-filter retry for account {AccountId} failed; keeping the empty result",
                cfg.AccountId);
            return null;
        }
    }

    private async Task<IReadOnlyList<DavPropResponse>> ListAddressbooksAsync(
        DavAccountConfig cfg, CancellationToken cancellationToken)
    {
        var home = await _discovery.GetAddressbookHomeAsync(cfg, cancellationToken);
        var entry = EntryUri(cfg, DavServiceKind.CardDav);
        var result = await _http.PropfindAsync(
            home, cfg.Username, cfg.Password, DavXml.PropfindAddressbookCollections(),
            cfg.Preset, DavServiceKind.CardDav, entry, depth: 1, cancellationToken);
        EnsureHttpSuccess(result, "list addressbooks");

        var books = DavXml.ParseMultiStatus(result.Body, result.FinalUri)
            .Where(r => r.IsAddressbook)
            .ToList();

        if (books.Count == 0)
            throw new InvalidOperationException(
                $"No address books found for account '{cfg.AccountId}'.");

        return books;
    }

    private async Task<Uri> SelectAddressBookAsync(
        DavAccountConfig cfg, string? addressBookName, CancellationToken cancellationToken)
    {
        var books = await ListAddressbooksAsync(cfg, cancellationToken);
        var name = addressBookName?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return books.FirstOrDefault(b => b.CanWrite)?.Href
                ?? throw new InvalidOperationException($"No writable address books found for account '{cfg.AccountId}'.");
        }

        var matches = books.Where(b => string.Equals(AddressBookName(b), name, StringComparison.OrdinalIgnoreCase)).ToList();
        // A read-only namesake (e.g. a shared "Contacts") shouldn't block the one writable match.
        var writable = matches.Where(b => b.CanWrite).ToList();
        if (writable.Count == 1)
            return writable[0].Href;
        if (writable.Count > 1)
            throw new ProviderOperationException(
                $"Address book name '{name}' matches {writable.Count} writable address books; rename one to make it unique.");

        var reason = matches.Count == 0 ? "was not found" : "is read-only";
        throw new ProviderOperationException(
            $"Address book '{name}' {reason}. {DescribeWritableBooks(books)}");
    }

    private static string DescribeWritableBooks(IEnumerable<DavPropResponse> books)
    {
        var names = books.Where(b => b.CanWrite)
            .Select(AddressBookName)
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(n => $"'{n}'")
            .ToList();
        return names.Count == 0
            ? "No writable address book has a name; omit addressBookName to use the default."
            : $"Writable address books: {string.Join(", ", names)}.";
    }

    /// <summary>
    /// An event ID resolved against the account's discovered calendars: the calendar object
    /// resource, the calendar it belongs to, and the occurrence when the ID names one.
    /// </summary>
    private sealed record EventTarget(Uri Href, string CalendarId, string? RecurrenceKey);

    private const string InvalidEventIdMessage =
        "The eventId is not a valid CalDAV event id; use an id from get_calendar_events.";

    /// <summary>
    /// Resolves an event ID, checking that it is a direct child of a discovered calendar. With a
    /// default-calendar alias (or no calendarId) the calendar is the one that holds the event,
    /// since the event-scoped tools pass "primary" when the caller names no calendar.
    /// </summary>
    private async Task<EventTarget> ResolveEventAsync(
        string accountId, string? calendarId, string eventId, CancellationToken cancellationToken)
    {
        var (resourceId, recurrenceKey) = DavEventId.Parse(eventId);
        if (recurrenceKey is not null && !IcalMapper.TryParseRecurrenceKey(recurrenceKey, out _, out _))
            throw new ProviderOperationException(InvalidEventIdMessage);

        Uri resource;
        try
        {
            resource = DavResourceId.Decode(resourceId);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            throw new ProviderOperationException(InvalidEventIdMessage, ex);
        }

        var calendars = await ListCalendarsAsync(accountId, cancellationToken);
        CalendarInfo calendar;
        if (CalendarIdAliases.IsDefaultCalendar(calendarId))
        {
            calendar = calendars.FirstOrDefault(c => IsCollectionChild(DavResourceId.Decode(c.Id), resource))
                ?? throw new ProviderOperationException(
                    "The event is not in any calendar in this account; use an id from get_calendar_events.");
        }
        else
        {
            calendar = calendars.FirstOrDefault(c => c.Id == calendarId)
                ?? throw new ProviderOperationException(CalendarIdAliases.UnknownCalendarMessage(calendarId));
            if (!IsCollectionChild(DavResourceId.Decode(calendar.Id), resource))
                throw new ProviderOperationException(
                    "The event is not in the selected calendar; omit calendarId or use the calendarId returned with the event.");
        }

        return new EventTarget(resource, calendar.Id, recurrenceKey);
    }

    /// <summary>
    /// Changing one occurrence needs an override VEVENT, which this provider doesn't write yet.
    /// Refuse rather than silently changing every occurrence of the series.
    /// </summary>
    private static void RejectSingleOccurrence(EventTarget target, string action)
    {
        if (target.RecurrenceKey is null)
            return;

        throw new ProviderOperationException(
            $"{action} a single occurrence of a recurring CalDAV event isn't supported. " +
            $"To apply it to every occurrence, use the series eventId \"{DavResourceId.Encode(target.Href)}\".");
    }

    // Servers often return <d:displayname/> in a 404 propstat for an unnamed collection,
    // which parses as "". Expose that as "no name" rather than an empty string.
    private static string? AddressBookName(DavPropResponse book) =>
        string.IsNullOrWhiteSpace(book.DisplayName) ? null : book.DisplayName.Trim();

    private async Task<Uri> ValidateContactResourceAsync(DavAccountConfig cfg, string contactId, CancellationToken cancellationToken) =>
        (await ResolveContactResourceAsync(cfg, contactId, cancellationToken)).Resource;

    private async Task<(Uri Resource, DavPropResponse Book)> ResolveContactResourceAsync(
        DavAccountConfig cfg, string contactId, CancellationToken cancellationToken)
    {
        Uri resource;
        try
        {
            resource = DavResourceId.Decode(contactId);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            throw new ProviderOperationException(
                "The contactId is not a valid CardDAV contact id; use an id from get_contacts.", ex);
        }

        var books = await ListAddressbooksAsync(cfg, cancellationToken);
        var book = books.FirstOrDefault(b => IsCollectionChild(b.Href, resource))
            ?? throw new ProviderOperationException("The contact is not in this account's address books.");
        return (resource, book);
    }

    private static bool IsCollectionChild(Uri collection, Uri resource)
    {
        // Compare normalized origins and a single resource segment. Reject encoded
        // separators/dot segments that a server could interpret differently.
        if (resource.Query.Length > 0 || resource.Fragment.Length > 0 || resource.UserInfo.Length > 0
            || collection.GetLeftPart(UriPartial.Authority) != resource.GetLeftPart(UriPartial.Authority)) return false;
        var prefix = collection.AbsolutePath.TrimEnd('/') + "/";
        if (!resource.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var segment = Uri.UnescapeDataString(resource.AbsolutePath[prefix.Length..]);
        return segment.Length > 0 && segment is not "." and not ".."
            && segment.IndexOfAny(['/', '\\', '%']) < 0;
    }

    private static bool ContactMatches(Contact contact, string query)
    {
        var q = query.Trim();
        if (q.Length == 0)
            return true;

        return Contains(contact.DisplayName)
            || Contains(contact.GivenName)
            || Contains(contact.Surname)
            || Contains(contact.CompanyName)
            || Contains(contact.JobTitle)
            || contact.EmailAddresses.Any(e => Contains(e.Address))
            || contact.PhoneNumbers.Any(p => Contains(p.Number));

        bool Contains(string? value) =>
            !string.IsNullOrEmpty(value)
            && value.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    // ── Email (unsupported) ──────────────────────────────────────────

    public Task<IEnumerable<EmailMessage>> GetEmailsAsync(
        string accountId, int count = 20, bool unreadOnly = false, string? folder = null,
        CancellationToken cancellationToken = default)
        => throw Unsupported("email");

    public Task<IEnumerable<EmailMessage>> SearchEmailsAsync(
        string accountId, string query, int count = 20,
        DateTime? fromDate = null, DateTime? toDate = null, string? folder = null,
        CancellationToken cancellationToken = default)
        => throw Unsupported("email");

    public Task<EmailMessage?> GetEmailDetailsAsync(
        string accountId, string emailId, CancellationToken cancellationToken = default)
        => throw Unsupported("email");

    public Task<EmailAttachmentContent?> GetEmailAttachmentContentAsync(
        string accountId, string emailId, string attachmentId,
        CancellationToken cancellationToken = default)
        => throw Unsupported("email");

    public Task<string> SendEmailAsync(
        string accountId, string to, string subject, string body,
        string bodyFormat = "html", List<string>? cc = null,
        IReadOnlyList<OutboundEmailAttachment>? attachments = null,
        string? textBody = null, string? htmlBody = null,
        CancellationToken cancellationToken = default)
        => throw Unsupported("email");

    public Task DeleteEmailAsync(
        string accountId, string emailId, CancellationToken cancellationToken = default)
        => throw Unsupported("email");

    public Task MarkEmailAsReadAsync(
        string accountId, string emailId, bool isRead, CancellationToken cancellationToken = default)
        => throw Unsupported("email");

    public Task<string?> MoveEmailAsync(
        string accountId, string emailId, string destinationFolder,
        CancellationToken cancellationToken = default)
        => throw Unsupported("email");

    // ── Helpers ──────────────────────────────────────────────────────

    private static void EnsureCalendar(DavAccountConfig cfg)
    {
        if (!cfg.EnableCalendar || string.IsNullOrWhiteSpace(cfg.CalDavUrl))
            throw new NotSupportedException(
                $"Account '{cfg.AccountId}' does not have CalDAV/calendar enabled.");
    }

    private static void EnsureContacts(DavAccountConfig cfg)
    {
        if (!cfg.EnableContacts || string.IsNullOrWhiteSpace(cfg.CardDavUrl))
            throw new NotSupportedException(
                $"Account '{cfg.AccountId}' does not have CardDAV/contacts enabled.");
    }

    private static void EnsureHttpSuccess(DavHttpResult result, string operation)
    {
        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(
                $"DAV {operation} failed with HTTP {(int)result.StatusCode} at '{result.FinalUri}'.");
        }
    }

    private static NotSupportedException Unsupported(string area) =>
        new($"DAV provider does not support {area}. Use an IMAP account for mail.");
}
