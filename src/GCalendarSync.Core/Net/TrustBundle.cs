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
// Reads google-cacert.pem out of the app package. The native TLS component is handed the bytes
// rather than a path: an app container makes native filesystem access awkward, and the bundle is
// a package resource, so there is nothing to gain from letting C++ open it.
using System;
using System.Threading.Tasks;
using Windows.Security.Cryptography;
using Windows.Storage;

namespace GCalendarSync.Core.Net
{
    public static class TrustBundle
    {
        const string PackagePath = "ms-appx:///Assets/google-cacert.pem";

        static byte[] _cached;

        /// <summary>
        /// The Mozilla root bundle, which is where the Google Trust Services roots that
        /// www.google.com chains to come from. Cached after the first read - it is a package
        /// resource and cannot change while the app runs.
        /// </summary>
        public static async Task<byte[]> LoadAsync()
        {
            if (_cached != null) return _cached;

            var file = await StorageFile.GetFileFromApplicationUriAsync(new Uri(PackagePath));
            var buffer = await FileIO.ReadBufferAsync(file);

            byte[] bytes;
            CryptographicBuffer.CopyToByteArray(buffer, out bytes);
            _cached = bytes;
            return bytes;
        }
    }
}
