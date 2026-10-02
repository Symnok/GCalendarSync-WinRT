// GCalendarSync - a Google Contacts CardDAV sync client for Windows Phone 8.1
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
// An HttpMessageHandler that speaks HTTP/1.1 over GCalendarSync.Tls.TlsClient (OpenSSL),
// instead of the platform stack. This is the whole point of the native component: WP8.1's own
// TLS cannot reach www.google.com any more - its 2014 root store has no Google Trust Services
// root - so CardDavClient is handed this handler and every request goes through OpenSSL with
// our own trust bundle.
//
// Deliberate simplifications, each for a reason:
//
//   * One connection per request ("Connection: close"). Keep-alive would be worth having, but
//     connection-reuse bugs in a hand-written HTTP client are a bad trade; CardDavClient
//     instead keeps the number of requests down by batching with multiget REPORTs.
//   * No Accept-Encoding. Asking for gzip would mean shipping a decompressor; identity
//     responses cost a little bandwidth and remove a whole class of failure.
//   * Response bodies stream (see TlsBodyStream) rather than buffering. A multiget of cards
//     with photos runs to megabytes and this runs on 512 MB devices.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GCalendarSync.Tls;

namespace GCalendarSync.Core.Net
{
    // internal: nothing outside Core has any business constructing one. CardDavClient owns it.
    internal sealed class TlsHttpHandler : HttpMessageHandler
    {
        const int DefaultTimeoutMs = 30000;
        const int MaxRedirects = 5;

        readonly byte[] _trustBundle;

        /// <param name="trustBundlePem">
        /// Contents of google-cacert.pem (the Mozilla root bundle), read out of the app
        /// package. Native code never touches the filesystem itself.
        /// </param>
        public TlsHttpHandler(byte[] trustBundlePem)
        {
            if (trustBundlePem == null || trustBundlePem.Length == 0)
                throw new ArgumentException("a trust bundle is required", "trustBundlePem");
            _trustBundle = trustBundlePem;
        }

        /// <summary>Timeout applied to connect, and to each socket read/write.</summary>
        public int TimeoutMs { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Buffer the request body before leaving the async world - upload bodies are
            // images or voice clips, small enough that this is not a concern. (Sending video
            // is not supported; see the API doc.)
            byte[] body = null;
            if (request.Content != null)
                body = await request.Content.ReadAsByteArrayAsync().ConfigureAwait(false);

            return await Task.Run(
                () => SendBlocking(request, body, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }

        HttpResponseMessage SendBlocking(
            HttpRequestMessage request, byte[] body, CancellationToken ct)
        {
            var uri = request.RequestUri;
            var method = request.Method;
            int hops = 0;

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                // Only a TLS transport exists here, and sending contact data in the clear
                // would be worse than failing, so a plain-http URL is promoted rather than
                // followed as-is.
                if (string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase))
                    uri = new UriBuilder(uri) { Scheme = "https", Port = uri.IsDefaultPort ? 443 : uri.Port }.Uri;

                var client = new TlsClient();
                bool handedOff = false;
                try
                {
                    int accepted = client.SetTrustBundle(_trustBundle);
                    if (accepted <= 0)
                        throw new HttpRequestException("no CA certificates could be loaded from the trust bundle");

                    int timeout = TimeoutMs > 0 ? TimeoutMs : DefaultTimeoutMs;
                    int port = uri.IsDefaultPort ? 443 : uri.Port;

                    var status = client.Connect(uri.Host, port, timeout);
                    if (status != TlsStatus.Ok)
                        throw new HttpRequestException(DescribeFailure(status, uri.Host, client.LastError));

                    WriteRequest(client, request, method, uri, body);

                    var reader = new TlsLineReader(client);
                    var response = ReadResponseHead(reader, request);

                    // Redirects: Google answers some CardDAV paths with a 301 to the
                    // canonical collection URL.
                    var code = (int)response.StatusCode;
                    if ((code == 301 || code == 302 || code == 303 || code == 307 || code == 308)
                        && response.Headers.Location != null && hops < MaxRedirects)
                    {
                        hops++;
                        var next = response.Headers.Location.IsAbsoluteUri
                            ? response.Headers.Location
                            : new Uri(uri, response.Headers.Location);

                        // 303, and 301/302 on POST, degrade to GET without a body - the same
                        // rule every mainstream client follows.
                        if (code == 303 || ((code == 301 || code == 302) && method != HttpMethod.Get))
                        {
                            method = HttpMethod.Get;
                            body = null;
                        }
                        uri = next;
                        continue;   // client disposed in finally
                    }

                    // Hand the live connection to the body stream; it owns the TlsClient now.
                    AttachBody(response, reader, client);
                    handedOff = true;
                    return response;
                }
                finally
                {
                    if (!handedOff) client.Close();
                }
            }
        }

        static string DescribeFailure(TlsStatus status, string host, string detail)
        {
            string what;
            switch (status)
            {
                case TlsStatus.SocketError:         what = "could not reach " + host; break;
                case TlsStatus.HandshakeFailed:     what = "TLS handshake with " + host + " failed"; break;
                case TlsStatus.CertificateRejected: what = "certificate for " + host + " was rejected"; break;
                case TlsStatus.HostnameMismatch:    what = "certificate for " + host + " is for a different host"; break;
                case TlsStatus.NoTrustBundle:       what = "no trust bundle was installed"; break;
                default:                            what = "TLS failure talking to " + host; break;
            }
            return string.IsNullOrEmpty(detail) ? what : what + " (" + detail + ")";
        }

        // ---- request ---------------------------------------------------------------

        static void WriteRequest(
            TlsClient client, HttpRequestMessage request, HttpMethod method, Uri uri, byte[] body)
        {
            var sb = new StringBuilder();
            var target = uri.PathAndQuery;
            if (string.IsNullOrEmpty(target)) target = "/";

            sb.Append(method.Method).Append(' ').Append(target).Append(" HTTP/1.1\r\n");
            sb.Append("Host: ").Append(uri.Host);
            if (!uri.IsDefaultPort) sb.Append(':').Append(uri.Port);
            sb.Append("\r\n");

            foreach (var h in request.Headers)
            {
                if (string.Equals(h.Key, "Host", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var v in h.Value) sb.Append(h.Key).Append(": ").Append(v).Append("\r\n");
            }

            if (request.Content != null)
            {
                foreach (var h in request.Content.Headers)
                {
                    // HttpContent.Headers already carries a computed Content-Length, and the
                    // framing headers are ours to decide. Copying them here and then writing
                    // our own produces a request with TWO Content-Length headers, which is a
                    // protocol violation - OK answers 400 Bad Request. Only POSTs were
                    // affected, which is why GET-shaped calls appeared to work.
                    if (string.Equals(h.Key, "Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(h.Key, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) continue;

                    foreach (var v in h.Value)
                        sb.Append(h.Key).Append(": ").Append(v).Append("\r\n");
                }
            }

            sb.Append("Content-Length: ").Append(body == null ? 0 : body.Length).Append("\r\n");
            // Identity only - see the note at the top of the file.
            sb.Append("Accept-Encoding: identity\r\n");
            sb.Append("Connection: close\r\n");
            sb.Append("\r\n");

            var head = Encoding.UTF8.GetBytes(sb.ToString());
            if (client.Write(head) < 0)
                throw new HttpRequestException("failed to send request headers: " + client.LastError);

            if (body != null && body.Length > 0 && client.Write(body) < 0)
                throw new HttpRequestException("failed to send request body: " + client.LastError);
        }

        // ---- response --------------------------------------------------------------

        static HttpResponseMessage ReadResponseHead(TlsLineReader reader, HttpRequestMessage request)
        {
            var statusLine = reader.ReadLine();
            if (statusLine == null)
                throw new HttpRequestException("the server closed the connection without responding");

            // "HTTP/1.1 200 OK"
            var parts = statusLine.Split(new[] { ' ' }, 3);
            int code;
            if (parts.Length < 2 || !int.TryParse(parts[1], out code))
                throw new HttpRequestException("malformed status line: " + statusLine);

            var response = new HttpResponseMessage((HttpStatusCode)code);
            response.RequestMessage = request;
            if (parts.Length > 2) response.ReasonPhrase = parts[2];

            var contentHeaders = new List<KeyValuePair<string, string>>();
            string line;
            while (!string.IsNullOrEmpty(line = reader.ReadLine()))
            {
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                var name = line.Substring(0, colon).Trim();
                var value = line.Substring(colon + 1).Trim();

                // Content-* belong on the content, everything else on the response - but
                // Transfer-Encoding must reach AttachBody too, or chunked framing is never
                // decoded and the raw chunk sizes end up in the body. (That is what produced
                // "unparsable reply: 10f" - 0x10f is a chunk length, not a reply.)
                // TryAddWithoutValidation ACCEPTS Transfer-Encoding as a response header, so
                // the old else-if never saw it.
                bool isFraming = name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase)
                              || string.Equals(name, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase);

                if (!response.Headers.TryAddWithoutValidation(name, value) || isFraming)
                    contentHeaders.Add(new KeyValuePair<string, string>(name, value));
            }

            reader.PendingContentHeaders = contentHeaders;
            return response;
        }

        static void AttachBody(HttpResponseMessage response, TlsLineReader reader, TlsClient client)
        {
            bool chunked = false;
            long length = -1;

            foreach (var h in reader.PendingContentHeaders)
            {
                if (string.Equals(h.Key, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase) &&
                    h.Value.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0)
                    chunked = true;
                else if (string.Equals(h.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    long parsed;
                    if (long.TryParse(h.Value, out parsed)) length = parsed;
                }
            }

            var stream = new TlsBodyStream(reader, client, chunked, length);
            var content = new StreamContent(stream);

            foreach (var h in reader.PendingContentHeaders)
            {
                // Length and framing are the stream's business, not the caller's.
                if (string.Equals(h.Key, "Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(h.Key, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) continue;
                content.Headers.TryAddWithoutValidation(h.Key, h.Value);
            }

            response.Content = content;
        }
    }
}
