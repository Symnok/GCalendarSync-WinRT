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
// The two pieces TlsHttpHandler needs to turn a TLS byte stream into HTTP/1.1: a buffered
// reader that can pull a header line without over-reading into the body, and a Stream that
// applies the response framing (chunked / Content-Length / read-until-close).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using GCalendarSync.Tls;

namespace GCalendarSync.Core.Net
{
    /// <summary>
    /// Buffered reader over TlsClient. Headers are read line by line, but a single TLS record
    /// usually carries the head *and* the start of the body - so whatever is read past the
    /// blank line stays buffered and is handed on to the body stream.
    /// </summary>
    sealed class TlsLineReader
    {
        const int BufferSize = 8192;

        readonly TlsClient _client;
        byte[] _buffer = new byte[BufferSize];
        int _start;
        int _end;
        bool _eof;

        public TlsLineReader(TlsClient client)
        {
            _client = client;
        }

        /// <summary>Content-* headers parked between reading the head and building the body.</summary>
        public List<KeyValuePair<string, string>> PendingContentHeaders { get; set; }

        int Buffered { get { return _end - _start; } }

        bool Fill()
        {
            if (_eof) return false;

            var chunk = _client.Read(BufferSize);
            if (chunk == null || chunk.Length == 0) { _eof = true; return false; }

            // Compact or grow so the new bytes fit after what is still unread.
            if (_end + chunk.Length > _buffer.Length)
            {
                if (Buffered + chunk.Length > _buffer.Length)
                {
                    var bigger = new byte[Math.Max(_buffer.Length * 2, Buffered + chunk.Length)];
                    Buffer.BlockCopy(_buffer, _start, bigger, 0, Buffered);
                    _buffer = bigger;
                }
                else
                {
                    Buffer.BlockCopy(_buffer, _start, _buffer, 0, Buffered);
                }
                _end = Buffered;
                _start = 0;
            }

            Buffer.BlockCopy(chunk, 0, _buffer, _end, chunk.Length);
            _end += chunk.Length;
            return true;
        }

        /// <summary>One CRLF-terminated line without its terminator; null at end of stream.</summary>
        public string ReadLine()
        {
            int scanned = 0;
            while (true)
            {
                for (int i = _start + scanned; i < _end; i++)
                {
                    if (_buffer[i] == (byte)'\n')
                    {
                        int end = i;
                        if (end > _start && _buffer[end - 1] == (byte)'\r') end--;
                        var line = Encoding.UTF8.GetString(_buffer, _start, end - _start);
                        _start = i + 1;
                        return line;
                    }
                }
                scanned = Buffered;
                if (!Fill())
                    return Buffered > 0 ? DrainRemaining() : null;
            }
        }

        string DrainRemaining()
        {
            var line = Encoding.UTF8.GetString(_buffer, _start, Buffered);
            _start = _end;
            return line;
        }

        /// <summary>Reads up to count bytes, preferring what is already buffered. 0 means EOF.</summary>
        public int Read(byte[] destination, int offset, int count)
        {
            if (count <= 0) return 0;

            if (Buffered == 0 && !Fill()) return 0;

            int n = Math.Min(count, Buffered);
            Buffer.BlockCopy(_buffer, _start, destination, offset, n);
            _start += n;
            return n;
        }
    }

    /// <summary>
    /// Read-only stream over the response body. Owns the TlsClient: disposing it closes the
    /// connection, which is what HttpClient does when the caller is done with the content.
    /// Bodies are never buffered whole - a received video would not fit in memory on a
    /// 512 MB device.
    /// </summary>
    sealed class TlsBodyStream : Stream
    {
        readonly TlsLineReader _reader;
        readonly TlsClient _client;
        readonly bool _chunked;
        readonly long _length;      // -1 when unknown (read until close)

        long _consumed;
        long _chunkRemaining;
        bool _finished;
        bool _disposed;

        public TlsBodyStream(TlsLineReader reader, TlsClient client, bool chunked, long length)
        {
            _reader = reader;
            _client = client;
            _chunked = chunked;
            _length = length;
        }

        public override bool CanRead { get { return !_disposed; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return false; } }

        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position
        {
            get { return _consumed; }
            set { throw new NotSupportedException(); }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_disposed) throw new ObjectDisposedException("TlsBodyStream");
            if (buffer == null) throw new ArgumentNullException("buffer");
            if (count <= 0 || _finished) return 0;

            return _chunked ? ReadChunked(buffer, offset, count)
                            : ReadPlain(buffer, offset, count);
        }

        int ReadPlain(byte[] buffer, int offset, int count)
        {
            if (_length >= 0)
            {
                long left = _length - _consumed;
                if (left <= 0) { _finished = true; return 0; }
                if (count > left) count = (int)left;
            }

            int n = _reader.Read(buffer, offset, count);
            if (n <= 0) { _finished = true; return 0; }

            _consumed += n;
            if (_length >= 0 && _consumed >= _length) _finished = true;
            return n;
        }

        int ReadChunked(byte[] buffer, int offset, int count)
        {
            if (_chunkRemaining == 0 && !NextChunk()) return 0;

            if (count > _chunkRemaining) count = (int)_chunkRemaining;
            int n = _reader.Read(buffer, offset, count);
            if (n <= 0) { _finished = true; return 0; }

            _chunkRemaining -= n;
            _consumed += n;

            // Each chunk is followed by its own CRLF.
            if (_chunkRemaining == 0) _reader.ReadLine();
            return n;
        }

        bool NextChunk()
        {
            var line = _reader.ReadLine();
            if (line == null) { _finished = true; return false; }

            // "1a" or "1a; some-extension"
            int semi = line.IndexOf(';');
            if (semi >= 0) line = line.Substring(0, semi);
            line = line.Trim();

            long size;
            if (line.Length == 0 ||
                !long.TryParse(line, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out size))
            {
                _finished = true;
                return false;
            }

            if (size == 0)
            {
                // Trailers, then the terminating blank line.
                string trailer;
                while (!string.IsNullOrEmpty(trailer = _reader.ReadLine())) { }
                _finished = true;
                return false;
            }

            _chunkRemaining = size;
            return true;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                if (disposing && _client != null) _client.Close();
            }
            base.Dispose(disposing);
        }
    }
}
