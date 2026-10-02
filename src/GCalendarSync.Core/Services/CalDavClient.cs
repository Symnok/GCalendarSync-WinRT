// GCalendarSync - a Google Calendar CalDAV sync client for Windows Phone 8.1
// (and Windows 10 Mobile, which runs the same package).
// Copyright (C) 2026
//
// This program is free software; you can redistribute it and/or modify it under
// the terms of the GNU General Public License as published by the Free Software
// Foundation; either version 2 of the License, or (at your option) any later
// version.
//
// This program is distributed in the hope that it will be useful, but WITHOUT
// ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS
// FOR A PARTICULAR PURPOSE. See the GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License along with
// this program; see the LICENSE file. If not, see <https://www.gnu.org/licenses/>.
//
// Google Calendar over CalDAV, with a Google App Password as Basic auth.
//
// The endpoint is NOT the one Google's own documentation points at. Probed against a live account:
//
//   https://www.google.com/calendar/dav/<email>/          401 + WWW-Authenticate: basic   <- this one
//   https://apidata.googleusercontent.com/caldav/v2/...   401 with NO auth scheme offered
//
// The v2 host wants an OAuth bearer token and will never accept an App Password: it does not
// reject the password, it never asks for one. v1 is deprecated but serving, and it is the only
// endpoint this app can use at all. If Google ever redirects v1 to v2 the symptom will be a 401
// that looks exactly like a wrong password, which is why the redirect is reported by host.
//
// Every request goes through TlsHttpHandler (OpenSSL), because WP8.1's own TLS cannot validate
// www.google.com any more. That handler opens one connection per request, so request count
// matters: a calendar-query returns the whole window's etags in one, and calendar-multiget
// fetches twenty-five events in one, rather than a GET each.
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using GCalendarSync.Core.Models;
using GCalendarSync.Core.Net;

namespace GCalendarSync.Core.Services
{
    /// <summary>One event resource as the server has it.</summary>
    public sealed class FetchedEvent
    {
        public string Href = string.Empty;
        public string Etag = string.Empty;
        public string CalendarHref = string.Empty;
        /// <summary>Every VEVENT in the resource: a master, plus an override per edited occurrence.</summary>
        public List<VEvent> Events = new List<VEvent>();
    }

    public sealed class CalDavClient : IDisposable
    {
        const string Origin = "https://www.google.com";
        const string BasePath = "/calendar/dav/{0}/";

        /// <summary>
        /// Events per calendar-multiget. Twenty-five is what the CardDAV sibling uses: the whole
        /// response is buffered as a string, and a description-heavy event runs to kilobytes.
        /// </summary>
        public const int MultigetBatch = 25;

        static readonly XNamespace Dav = "DAV:";
        static readonly XNamespace CalDav = "urn:ietf:params:xml:ns:caldav";
        /// <summary>Not an IETF namespace: getctag is a CalendarServer extension, and it is
        /// supported far more widely than the RFC 6578 sync-token that replaced it.</summary>
        static readonly XNamespace CalServer = "http://calendarserver.org/ns/";

        readonly HttpClient _http;
        readonly string _basePath;
        readonly string _email;

        bool _multigetWorks = true;
        int _multigetDepth = 1;

        public CalDavClient(string gmailAddress, string appPassword, byte[] trustBundlePem)
        {
            _email = gmailAddress;
            _basePath = string.Format(BasePath, Uri.EscapeDataString(gmailAddress));

            var handler = new TlsHttpHandler(trustBundlePem);
            handler.TimeoutMs = 60000;

            _http = new HttpClient(handler);
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(gmailAddress + ":" + appPassword)));
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("GCalendarSync/1.0");
            _http.Timeout = TimeSpan.FromMinutes(5);
        }

        // ================================================================
        // DISCOVERY - which calendars exist
        // ================================================================
        /// <summary>
        /// Every calendar the account can see, classified into the four the settings switch on.
        ///
        /// The home set is asked for rather than assumed, because a subscribed calendar - a
        /// holiday one, say - lives under a DIFFERENT principal ("&lt;opaque&gt;@virtual"), not under
        /// this account. Building "&lt;email&gt;/events/" by hand finds only the personal calendar and
        /// silently misses every other one.
        /// </summary>
        public async Task<List<CalendarInfo>> DiscoverAsync(CancellationToken ct)
        {
            string home = await FindHomeSetAsync(ct).ConfigureAwait(false);

            const string body =
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                "<d:propfind xmlns:d=\"DAV:\" xmlns:cs=\"http://calendarserver.org/ns/\">" +
                  "<d:prop><d:displayname/><d:resourcetype/><cs:getctag/></d:prop>" +
                "</d:propfind>";

            var response = await SendAsync(
                Request("PROPFIND", home, body, "1"), ct).ConfigureAwait(false);
            EnsureSuccess(response, "listing calendars");

            string xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            var calendars = new List<CalendarInfo>();
            foreach (var entry in ReadMultistatus(xml))
            {
                if (entry.Href.Length == 0) continue;
                if (!entry.IsCalendar) continue;       // inbox, outbox and the principal are not

                var info = new CalendarInfo
                {
                    Href = entry.Href,
                    DisplayName = entry.DisplayName,
                    CTag = entry.CTag
                };
                info.Kind = CalendarKinds.Classify(info.Href, info.DisplayName, _email);
                calendars.Add(info);
            }
            return calendars;
        }

        /// <summary>
        /// The calendar-home-set, or the account's own path when the server does not advertise
        /// one. v1 does advertise it, which was not a given for an endpoint older than the RFC.
        /// </summary>
        async Task<string> FindHomeSetAsync(CancellationToken ct)
        {
            const string body =
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                "<d:propfind xmlns:d=\"DAV:\" xmlns:c=\"urn:ietf:params:xml:ns:caldav\">" +
                  "<d:prop><d:current-user-principal/><c:calendar-home-set/></d:prop>" +
                "</d:propfind>";

            try
            {
                var response = await SendAsync(
                    Request("PROPFIND", _basePath + "user/", body, "0"), ct).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    string xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    foreach (var entry in ReadMultistatus(xml))
                        if (entry.HomeSet.Length > 0) return entry.HomeSet;
                }
                else if ((int)response.StatusCode == 401)
                {
                    // Worth failing here rather than carrying on to a listing that would fail the
                    // same way with a less useful message.
                    EnsureSuccess(response, "signing in");
                }
            }
            catch (CalDavException) { throw; }
            catch (Exception) { /* fall through to the account's own path */ }

            return _basePath;
        }

        // ================================================================
        // LIST - what is in a calendar, within the window
        // ================================================================
        /// <summary>
        /// Path -> ETag for every event overlapping the window.
        ///
        /// A time-range filter rather than a plain listing, because a calendar holds years of
        /// history: on a real account the window cut several hundred events down to the tens that
        /// a phone has any use for.
        /// </summary>
        public async Task<Dictionary<string, string>> ListAsync(
            string calendarHref, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
        {
            string body =
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                "<C:calendar-query xmlns:D=\"DAV:\" xmlns:C=\"urn:ietf:params:xml:ns:caldav\">" +
                  "<D:prop><D:getetag/></D:prop>" +
                  "<C:filter><C:comp-filter name=\"VCALENDAR\"><C:comp-filter name=\"VEVENT\">" +
                    "<C:time-range start=\"" + Stamp(from) + "\" end=\"" + Stamp(to) + "\"/>" +
                  "</C:comp-filter></C:comp-filter></C:filter>" +
                "</C:calendar-query>";

            var response = await SendAsync(
                Request("REPORT", calendarHref, body, "1"), ct).ConfigureAwait(false);
            EnsureSuccess(response, "listing events");

            string xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            var etags = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in ReadMultistatus(xml))
            {
                if (entry.Href.Length == 0 || entry.Href.EndsWith("/", StringComparison.Ordinal)) continue;
                if (entry.Etag.Length == 0) continue;
                etags[entry.Href] = entry.Etag;
            }
            return etags;
        }

        /// <summary>The collection tag, when the server has one: one request to answer
        /// "has anything in this calendar changed?". Empty when unsupported.</summary>
        public async Task<string> GetCTagAsync(string calendarHref, CancellationToken ct)
        {
            const string body =
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                "<d:propfind xmlns:d=\"DAV:\" xmlns:cs=\"http://calendarserver.org/ns/\">" +
                  "<d:prop><cs:getctag/></d:prop>" +
                "</d:propfind>";
            try
            {
                var response = await SendAsync(
                    Request("PROPFIND", calendarHref, body, "0"), ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return string.Empty;

                string xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                foreach (var entry in ReadMultistatus(xml))
                    if (entry.CTag.Length > 0) return entry.CTag;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { }
            return string.Empty;
        }

        // ================================================================
        // FETCH
        // ================================================================
        /// <summary>
        /// One batch of events. A calendar-multiget, falling back to a GET each if the server
        /// refuses the REPORT.
        /// </summary>
        public async Task<List<FetchedEvent>> FetchBatchAsync(
            string calendarHref, List<string> hrefs, Dictionary<string, string> etags,
            CancellationToken ct)
        {
            var result = new List<FetchedEvent>();
            if (hrefs == null || hrefs.Count == 0) return result;

            ct.ThrowIfCancellationRequested();

            List<FetchedEvent> fetched = null;
            if (_multigetWorks)
            {
                fetched = await MultigetAsync(calendarHref, hrefs, ct).ConfigureAwait(false);
                if (fetched == null || fetched.Count == 0)
                {
                    _multigetWorks = false;
                    fetched = null;
                }
            }
            if (fetched == null)
                fetched = await GetEachAsync(hrefs, ct).ConfigureAwait(false);

            foreach (var item in fetched)
            {
                if (item.Etag.Length == 0 && etags != null)
                {
                    string etag;
                    if (etags.TryGetValue(item.Href, out etag)) item.Etag = etag;
                }
                item.CalendarHref = calendarHref;
                foreach (var e in item.Events) { e.Href = item.Href; e.Etag = item.Etag; }
                result.Add(item);
            }
            return result;
        }

        async Task<List<FetchedEvent>> MultigetAsync(
            string calendarHref, List<string> hrefs, CancellationToken ct)
        {
            var body = new StringBuilder();
            body.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            body.Append("<C:calendar-multiget xmlns:D=\"DAV:\" ")
                .Append("xmlns:C=\"urn:ietf:params:xml:ns:caldav\">");
            body.Append("<D:prop><D:getetag/><C:calendar-data/></D:prop>");
            foreach (var href in hrefs)
                body.Append("<D:href>").Append(XmlEscape(href)).Append("</D:href>");
            body.Append("</C:calendar-multiget>");

            // RFC 4791 and RFC 6352 differ on the Depth a multiget takes, and servers differ from
            // both, so the one that works is found once and then remembered.
            for (int attempt = 0; attempt < 2; attempt++)
            {
                HttpResponseMessage response;
                try
                {
                    response = await SendAsync(
                        Request("REPORT", calendarHref, body.ToString(), _multigetDepth.ToString()),
                        ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { return null; }

                if (!response.IsSuccessStatusCode)
                {
                    if (attempt == 0) { _multigetDepth = _multigetDepth == 1 ? 0 : 1; continue; }
                    return null;
                }

                string xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                var events = new List<FetchedEvent>();
                foreach (var entry in ReadMultistatus(xml))
                {
                    if (entry.CalendarData.Length == 0) continue;
                    var parsed = ICalParser.ParseEvents(entry.CalendarData);
                    if (parsed.Count == 0) continue;

                    events.Add(new FetchedEvent
                    {
                        Href = entry.Href,
                        Etag = entry.Etag,
                        Events = parsed
                    });
                }
                return events;
            }
            return null;
        }

        async Task<List<FetchedEvent>> GetEachAsync(List<string> hrefs, CancellationToken ct)
        {
            var events = new List<FetchedEvent>();
            foreach (var href in hrefs)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, Url(href));
                    request.Headers.Accept.ParseAdd("text/calendar");
                    var response = await SendAsync(request, ct).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode) continue;

                    string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var parsed = ICalParser.ParseEvents(text);
                    if (parsed.Count == 0) continue;

                    events.Add(new FetchedEvent
                    {
                        Href = href,
                        Etag = response.Headers.ETag != null
                             ? response.Headers.ETag.Tag.Trim('"') : string.Empty,
                        Events = parsed
                    });
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { }
            }
            return events;
        }

        // ================================================================
        // Plumbing
        // ================================================================
        HttpRequestMessage Request(string method, string path, string body, string depth)
        {
            var request = new HttpRequestMessage(new HttpMethod(method), Url(path));
            request.Content = new StringContent(body, Encoding.UTF8, "text/xml");
            request.Content.Headers.ContentType =
                new MediaTypeHeaderValue("text/xml") { CharSet = "utf-8" };
            request.Headers.Add("Depth", depth);
            return request;
        }

        /// <summary>
        /// An absolute URL from a path the server gave us.
        ///
        /// The path is used EXACTLY as it arrived. Every href in a multistatus is already
        /// percent-encoded - the personal calendar comes back as "someone%40gmail.com" - and
        /// encoding it a second time turns "%40" into "%2540", which 404s. That is not a
        /// hypothetical: it cost the Symbian port of GContactsSync a day of debugging.
        /// </summary>
        Uri Url(string path)
        {
            if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return new Uri(path);
            return new Uri(Origin + path);
        }

        static string Stamp(DateTimeOffset when)
        {
            return when.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'");
        }

        async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            try
            {
                return await _http.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                throw new CalDavException("Network error: " + ex.Message, ex);
            }
        }

        static void EnsureSuccess(HttpResponseMessage response, string what)
        {
            if (response.IsSuccessStatusCode) return;   // 207 Multi-Status is already a 2xx

            int code = (int)response.StatusCode;
            if (code == 401)
                throw new CalDavException(
                    "Google rejected the sign-in (401).\n\n" +
                    "Use a 16-character Google App Password, not your account password. " +
                    "Create one at myaccount.google.com/apppasswords - it needs 2-Step " +
                    "Verification to be switched on.");
            if (code == 403)
                throw new CalDavException(
                    "Google refused access (403).\n\n" +
                    "Check that the account allows CalDAV and that the App Password has not " +
                    "been revoked.");
            if (code == 404)
                throw new CalDavException(
                    "The calendar was not found (404).\n\n" +
                    "Check the Gmail address is spelt correctly.");

            throw new CalDavException("Google returned " + code + " " + response.ReasonPhrase +
                                      " while " + what + ".");
        }

        sealed class MultistatusEntry
        {
            public string Href = string.Empty;
            public string Etag = string.Empty;
            public string DisplayName = string.Empty;
            public string CTag = string.Empty;
            public string HomeSet = string.Empty;
            public string CalendarData = string.Empty;
            public bool IsCalendar;
        }

        /// <summary>
        /// Every response in a DAV multistatus. A real XML parser rather than string scanning:
        /// calendar-data arrives as escaped element text, and getting the entity decoding wrong
        /// corrupts events.
        /// </summary>
        static IEnumerable<MultistatusEntry> ReadMultistatus(string xml)
        {
            var entries = new List<MultistatusEntry>();
            if (string.IsNullOrEmpty(xml)) return entries;

            XDocument document;
            try
            {
                document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            }
            catch (Exception ex)
            {
                throw new CalDavException(
                    "Google's reply could not be understood (" + ex.Message + ").", ex);
            }
            if (document.Root == null) return entries;

            foreach (var response in document.Root.Elements(Dav + "response"))
            {
                var entry = new MultistatusEntry();

                var href = response.Element(Dav + "href");
                if (href != null) entry.Href = href.Value.Trim();

                foreach (var propstat in response.Elements(Dav + "propstat"))
                {
                    // A propstat can report a property as not found; only the 200 ones are values.
                    var status = propstat.Element(Dav + "status");
                    if (status != null && status.Value.IndexOf(" 200 ", StringComparison.Ordinal) < 0)
                        continue;

                    var prop = propstat.Element(Dav + "prop");
                    if (prop == null) continue;

                    var etag = prop.Element(Dav + "getetag");
                    if (etag != null) entry.Etag = etag.Value.Trim().Trim('"');

                    var name = prop.Element(Dav + "displayname");
                    if (name != null) entry.DisplayName = name.Value.Trim();

                    var ctag = prop.Element(CalServer + "getctag");
                    if (ctag != null) entry.CTag = ctag.Value.Trim();

                    var data = prop.Element(CalDav + "calendar-data");
                    if (data != null) entry.CalendarData = data.Value;

                    var home = prop.Element(CalDav + "calendar-home-set");
                    if (home != null)
                    {
                        var target = home.Element(Dav + "href");
                        if (target != null) entry.HomeSet = target.Value.Trim();
                    }

                    var kinds = prop.Element(Dav + "resourcetype");
                    if (kinds != null && kinds.Element(CalDav + "calendar") != null)
                        entry.IsCalendar = true;
                }

                entries.Add(entry);
            }
            return entries;
        }

        static string XmlEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }

        public void Dispose()
        {
            _http.Dispose();
        }
    }

    /// <summary>A failure with a message meant to be shown to the user as it is.</summary>
    public sealed class CalDavException : Exception
    {
        public CalDavException(string message) : base(message) { }
        public CalDavException(string message, Exception inner) : base(message, inner) { }
    }
}
