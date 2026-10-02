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
// A minimal TLS client exposed to the managed head as a Windows Runtime type. It exists
// because a WP8.1 C# app cannot P/Invoke a plain DLL: reaching OpenSSL requires a WinRT
// component.
//
// Why OpenSSL at all, when WP8.1 has its own TLS: the device root store was frozen in 2014
// and does not contain the Google Trust Services roots (GTS Root R1..R4) that
// www.google.com chains to today, so the platform stack cannot validate the CardDAV
// endpoint. Shipping our own trust bundle sidesteps a root store nobody can update.
//
// The surface is deliberately a *stream*, not an HTTP client: connect / write / read / close.
// HTTP/1.1 is built on top of this in C#, where header parsing, chunked transfer-decoding
// and redirects are far less painful to get right than in C++ we cannot easily debug on
// the device.
#pragma once

namespace GCalendarSync {
namespace Tls {

/// <summary>Result of a Connect attempt. Values are stable; the managed side switches on them.</summary>
public enum class TlsStatus
{
    Ok                  = 0,
    SocketError         = 1,   // DNS or TCP connect failed
    HandshakeFailed     = 2,   // TLS negotiation failed
    CertificateRejected = 3,   // chain did not verify against the bundle
    HostnameMismatch    = 4,   // chain verified but the name does not match
    NotConnected        = 5,
    NoTrustBundle       = 6,   // SetTrustBundle was never called
};

public ref class TlsClient sealed
{
public:
    TlsClient();
    virtual ~TlsClient();

    /// <summary>
    /// Install the PEM trust anchors. The app reads google-cacert.pem out of its own package
    /// and hands the bytes over, so nothing here touches the filesystem - an app container
    /// makes native file access awkward, and the bundle is a package resource anyway.
    /// Must be called before Connect.
    /// </summary>
    /// <returns>number of certificates accepted</returns>
    int SetTrustBundle(const Platform::Array<uint8>^ pem);

    /// <summary>TCP connect + TLS 1.2 handshake with SNI and hostname verification.</summary>
    TlsStatus Connect(Platform::String^ host, int port, int timeoutMs);

    /// <summary>Writes all of <paramref name="data"/>. Returns bytes written, or -1.</summary>
    int Write(const Platform::Array<uint8>^ data);

    /// <summary>Reads up to maxBytes. A zero-length result means the peer closed cleanly.</summary>
    Platform::Array<uint8>^ Read(int maxBytes);

    void Close();

    /// <summary>Human-readable detail for the last failure; empty when fine.</summary>
    property Platform::String^ LastError { Platform::String^ get() { return _lastError; } }
    property Platform::String^ Cipher     { Platform::String^ get() { return _cipher; } }
    property Platform::String^ Protocol   { Platform::String^ get() { return _protocol; } }
    property bool Connected               { bool get() { return _ssl != nullptr; } }

private:
    void  Fail(Platform::String^ stage);
    void  Cleanup();

    void* _ctx;            // SSL_CTX*
    void* _ssl;            // SSL*
    uintptr_t _socket;     // SOCKET
    bool  _haveBundle;

    Platform::String^ _lastError;
    Platform::String^ _cipher;
    Platform::String^ _protocol;
};

}} // namespace
