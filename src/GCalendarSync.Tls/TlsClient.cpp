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
// TLS over the ported OpenSSL 1.0.2r (see lib\openssl\PROVENANCE.md).
#include "pch.h"
#include "TlsClient.h"

#include <openssl/ssl.h>
#include <openssl/err.h>
#include <openssl/x509v3.h>
#include <openssl/pem.h>

#include <string>
#include <vector>

using namespace Platform;
using namespace GCalendarSync::Tls;

namespace {

// OpenSSL and Winsock global init, once per process.
INIT_ONCE g_once = INIT_ONCE_STATIC_INIT;
BOOL CALLBACK InitOpenSsl(PINIT_ONCE, PVOID, PVOID*)
{
    // Winsock must be started before ANY socket call, getaddrinfo included. Omitting this
    // makes every call fail with WSANOTINITIALISED, which surfaces as "DNS lookup failed"
    // and looks convincingly like a network problem. It is not.
    WSADATA wsa;
    WSAStartup(MAKEWORD(2, 2), &wsa);

    SSL_library_init();
    SSL_load_error_strings();
    OpenSSL_add_all_algorithms();
    return TRUE;
}

// A Winsock error as a sentence. The number alone sends you looking it up, and the two that
// matter here mean opposite things: 11001 is the phone failing to resolve a name, which is a
// network problem and nothing to do with this app, while 10093 would mean Winsock was never
// started, which would be a bug in it.
const wchar_t* WsaMeaning(int code)
{
    switch (code) {
        case 11001: return L"host not found - the phone could not resolve the name. "
                           L"No DNS: check Wi-Fi or mobile data is actually up";
        case 11002: return L"DNS server returned a temporary failure - try again";
        case 11004: return L"the name resolved to no address of a usable kind";
        case 10093: return L"Winsock was never started - a bug in this app, not your network";
        case 10013: return L"access denied - the app is missing the internetClient capability";
        case 10060: return L"timed out - a route exists but nothing answered";
        case 10061: return L"connection refused";
        case 10050:
        case 10051: return L"the network is down or unreachable";
        default:    return L"";
    }
}

String^ Widen(const std::string& s)
{
    if (s.empty()) return L"";
    int n = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), nullptr, 0);
    std::wstring w(n, L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s.c_str(), (int)s.size(), &w[0], n);
    return ref new String(w.c_str(), (unsigned)w.size());
}

std::string Narrow(String^ s)
{
    if (s == nullptr || s->IsEmpty()) return std::string();
    int n = WideCharToMultiByte(CP_UTF8, 0, s->Data(), (int)s->Length(), nullptr, 0, nullptr, nullptr);
    std::string a(n, '\0');
    WideCharToMultiByte(CP_UTF8, 0, s->Data(), (int)s->Length(), &a[0], n, nullptr, nullptr);
    return a;
}

// Drain OpenSSL's error queue into one line.
std::string PopOpenSslErrors()
{
    std::string out;
    unsigned long e;
    char buf[256];
    while ((e = ERR_get_error()) != 0) {
        ERR_error_string_n(e, buf, sizeof(buf));
        if (!out.empty()) out += "; ";
        out += buf;
    }
    return out;
}

} // anonymous namespace

TlsClient::TlsClient()
    : _ctx(nullptr), _ssl(nullptr), _socket((uintptr_t)INVALID_SOCKET),
      _haveBundle(false), _lastError(L""), _cipher(L""), _protocol(L"")
{
    InitOnceExecuteOnce(&g_once, InitOpenSsl, nullptr, nullptr);

    // SSLv23_client_method negotiates the best available, then we switch the obsolete
    // protocols off. 1.0.2 tops out at TLS 1.2, which every endpoint we target supports.
    const SSL_METHOD* method = SSLv23_client_method();
    SSL_CTX* ctx = SSL_CTX_new(method);
    if (ctx != nullptr) {
        SSL_CTX_set_options(ctx,
            SSL_OP_NO_SSLv2 | SSL_OP_NO_SSLv3 | SSL_OP_NO_TLSv1 | SSL_OP_NO_TLSv1_1 |
            SSL_OP_NO_COMPRESSION);
        // Forward secrecy first; these are what www.google.com actually negotiates.
        // No RC4, no 3DES, no export suites.
        SSL_CTX_set_cipher_list(ctx,
            "ECDHE-ECDSA-AES128-GCM-SHA256:ECDHE-RSA-AES128-GCM-SHA256:"
            "ECDHE-ECDSA-AES256-GCM-SHA384:ECDHE-RSA-AES256-GCM-SHA384:"
            "ECDHE-ECDSA-AES128-SHA256:ECDHE-RSA-AES128-SHA256:"
            "AES128-GCM-SHA256:AES256-GCM-SHA384");
        SSL_CTX_set_verify(ctx, SSL_VERIFY_PEER, nullptr);
        SSL_CTX_set_verify_depth(ctx, 10);
    }
    _ctx = ctx;
}

TlsClient::~TlsClient()
{
    Cleanup();
    if (_ctx != nullptr) { SSL_CTX_free((SSL_CTX*)_ctx); _ctx = nullptr; }
}

void TlsClient::Fail(String^ stage)
{
    // Platform::String^ has no operator+, so messages are assembled as std::wstring.
    std::string detail = PopOpenSslErrors();
    if (detail.empty()) { _lastError = stage; return; }

    std::wstring w(stage->Data());
    w += L": ";
    String^ widened = Widen(detail);
    w += widened->Data();
    _lastError = ref new String(w.c_str(), (unsigned)w.size());
}

void TlsClient::Cleanup()
{
    if (_ssl != nullptr) {
        SSL_shutdown((SSL*)_ssl);
        SSL_free((SSL*)_ssl);
        _ssl = nullptr;
    }
    if (_socket != (uintptr_t)INVALID_SOCKET) {
        closesocket((SOCKET)_socket);
        _socket = (uintptr_t)INVALID_SOCKET;
    }
}

int TlsClient::SetTrustBundle(const Array<uint8>^ pem)
{
    if (_ctx == nullptr || pem == nullptr || pem->Length == 0) return 0;

    X509_STORE* store = SSL_CTX_get_cert_store((SSL_CTX*)_ctx);
    if (store == nullptr) return 0;

    BIO* bio = BIO_new_mem_buf((void*)pem->Data, (int)pem->Length);
    if (bio == nullptr) return 0;

    int added = 0;
    X509* cert = nullptr;
    while ((cert = PEM_read_bio_X509(bio, nullptr, nullptr, nullptr)) != nullptr) {
        if (X509_STORE_add_cert(store, cert) == 1) added++;
        X509_free(cert);
    }
    // PEM_read_bio_X509 ends by pushing a "no start line" error; that is the normal
    // end-of-input signal here, not a failure.
    ERR_clear_error();
    BIO_free(bio);

    _haveBundle = added > 0;
    return added;
}

TlsStatus TlsClient::Connect(String^ host, int port, int timeoutMs)
{
    Cleanup();
    _lastError = L"";
    _cipher = L"";
    _protocol = L"";

    if (_ctx == nullptr) {
        _lastError = L"SSL_CTX not created";
        return TlsStatus::HandshakeFailed;
    }
    if (!_haveBundle) {
        _lastError = L"no trust bundle installed";
        return TlsStatus::NoTrustBundle;
    }

    std::string hostA = Narrow(host);
    char portA[16];
    sprintf_s(portA, "%d", port);

    addrinfo hints = {};
    hints.ai_family = AF_UNSPEC;
    hints.ai_socktype = SOCK_STREAM;
    hints.ai_protocol = IPPROTO_TCP;

    addrinfo* res = nullptr;
    int gai = getaddrinfo(hostA.c_str(), portA, &hints, &res);
    if (gai != 0 || res == nullptr) {
        // Report the actual code. WSANOTINITIALISED (10093) here means Winsock was never
        // started; WSAHOST_NOT_FOUND (11001) is a genuine name-resolution failure. They are
        // very different problems and must not look alike.
        int wsa = WSAGetLastError();
        int code = (gai != 0) ? gai : wsa;
        wchar_t detail[256];
        swprintf_s(detail, L"DNS lookup failed (getaddrinfo=%d, WSAGetLastError=%d)%s%s",
                   gai, wsa,
                   WsaMeaning(code)[0] ? L": " : L"", WsaMeaning(code));
        _lastError = ref new String(detail);
        return TlsStatus::SocketError;
    }

    SOCKET s = INVALID_SOCKET;
    for (addrinfo* ai = res; ai != nullptr; ai = ai->ai_next) {
        s = socket(ai->ai_family, ai->ai_socktype, ai->ai_protocol);
        if (s == INVALID_SOCKET) continue;
        if (connect(s, ai->ai_addr, (int)ai->ai_addrlen) == 0) break;
        closesocket(s);
        s = INVALID_SOCKET;
    }
    freeaddrinfo(res);

    if (s == INVALID_SOCKET) {
        int wsa = WSAGetLastError();
        wchar_t detail[256];
        swprintf_s(detail, L"TCP connect failed (WSAGetLastError=%d)%s%s", wsa,
                   WsaMeaning(wsa)[0] ? L": " : L"", WsaMeaning(wsa));
        _lastError = ref new String(detail);
        return TlsStatus::SocketError;
    }

    if (timeoutMs > 0) {
        DWORD t = (DWORD)timeoutMs;
        setsockopt(s, SOL_SOCKET, SO_RCVTIMEO, (const char*)&t, sizeof(t));
        setsockopt(s, SOL_SOCKET, SO_SNDTIMEO, (const char*)&t, sizeof(t));
    }
    _socket = (uintptr_t)s;

    SSL* ssl = SSL_new((SSL_CTX*)_ctx);
    if (ssl == nullptr) {
        Fail(L"SSL_new");
        Cleanup();
        return TlsStatus::HandshakeFailed;
    }
    _ssl = ssl;

    // SNI - www.google.com is virtual-hosted; without this the wrong certificate comes back.
    SSL_set_tlsext_host_name(ssl, hostA.c_str());

    // Hostname verification, an OpenSSL 1.0.2 feature. Without it, a certificate that is
    // valid for some *other* host would still chain to a trusted root and be accepted.
    X509_VERIFY_PARAM* param = SSL_get0_param(ssl);
    X509_VERIFY_PARAM_set_hostflags(param, X509_CHECK_FLAG_NO_PARTIAL_WILDCARDS);
    if (X509_VERIFY_PARAM_set1_host(param, hostA.c_str(), 0) != 1) {
        _lastError = L"could not set expected hostname";
        Cleanup();
        return TlsStatus::HandshakeFailed;
    }

    SSL_set_fd(ssl, (int)s);

    if (SSL_connect(ssl) != 1) {
        long vr = SSL_get_verify_result(ssl);
        Fail(L"handshake");
        TlsStatus st = TlsStatus::HandshakeFailed;
        if (vr != X509_V_OK) {
            String^ reason = Widen(X509_verify_cert_error_string(vr) ? X509_verify_cert_error_string(vr) : "unknown");
            std::wstring w(_lastError->Data());
            w += L" (verify: ";
            w += reason->Data();
            w += L")";
            _lastError = ref new String(w.c_str(), (unsigned)w.size());
            st = (vr == X509_V_ERR_HOSTNAME_MISMATCH) ? TlsStatus::HostnameMismatch
                                                      : TlsStatus::CertificateRejected;
        }
        Cleanup();
        return st;
    }

    long vr = SSL_get_verify_result(ssl);
    if (vr != X509_V_OK) {
        const char* reason = X509_verify_cert_error_string(vr);
        std::wstring w(L"certificate rejected: ");
        String^ r = Widen(reason ? reason : "unknown");
        w += r->Data();
        _lastError = ref new String(w.c_str(), (unsigned)w.size());
        Cleanup();
        return TlsStatus::CertificateRejected;
    }

    const char* c = SSL_get_cipher(ssl);
    const char* v = SSL_get_version(ssl);
    _cipher = Widen(c ? c : "");
    _protocol = Widen(v ? v : "");
    return TlsStatus::Ok;
}

int TlsClient::Write(const Array<uint8>^ data)
{
    if (_ssl == nullptr || data == nullptr) return -1;
    if (data->Length == 0) return 0;

    int total = 0;
    while (total < (int)data->Length) {
        int n = SSL_write((SSL*)_ssl, data->Data + total, (int)data->Length - total);
        if (n <= 0) {
            Fail(L"write");
            return -1;
        }
        total += n;
    }
    return total;
}

Array<uint8>^ TlsClient::Read(int maxBytes)
{
    if (_ssl == nullptr || maxBytes <= 0) return ref new Array<uint8>(0);

    std::vector<uint8> buf((size_t)maxBytes);
    int n = SSL_read((SSL*)_ssl, buf.data(), maxBytes);
    if (n <= 0) {
        int err = SSL_get_error((SSL*)_ssl, n);
        // A clean close, or the peer simply stopping, is not an error to the caller.
        if (err != SSL_ERROR_ZERO_RETURN && err != SSL_ERROR_SYSCALL) Fail(L"read");
        return ref new Array<uint8>(0);
    }
    auto out = ref new Array<uint8>(n);
    memcpy(out->Data, buf.data(), (size_t)n);
    return out;
}

void TlsClient::Close()
{
    Cleanup();
}
