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
// Getting the account file off the phone. The format itself is CredentialsFile's business.
//
// WP8.1 has no general filesystem access: an app sees its own folders, plus the specific known
// libraries it declares a capability for, plus whatever the user hands it through a picker. So
// there are two ways in, and the app tries them in that order - Documents needs no interaction when
// the file is where it is expected, and the picker covers everywhere else.
using System;
using System.Threading.Tasks;
using Windows.Storage;

namespace GCalendarSync.Core.Storage
{
    public static class CredentialsFileReader
    {
        /// <summary>
        /// Reads Documents\caldav.cfg if it is there. Returns null when there is nothing
        /// to read, so the caller can move on to asking the user to pick a file.
        /// </summary>
        public static async Task<CredentialsFileResult> TryLoadFromDocumentsAsync()
        {
            return await TryLoadNamedAsync(CredentialsFile.DefaultFileName).ConfigureAwait(false);
        }

        /// <summary>
        /// One named file from Documents, or null when it is not there. Never throws: not having the
        /// file is the ordinary case, and a refused library is a configuration problem rather than a
        /// crash - the picker still works either way.
        /// </summary>
        static async Task<CredentialsFileResult> TryLoadNamedAsync(string name)
        {
            try
            {
                var file = await KnownFolders.DocumentsLibrary
                    .GetFileAsync(name).AsTask().ConfigureAwait(false);

                var result = await LoadAsync(file).ConfigureAwait(false);
                result.Source = "Documents\\" + name;
                return result;
            }
            catch (System.IO.FileNotFoundException)
            {
                return null;   // the ordinary case
            }
            catch (UnauthorizedAccessException)
            {
                return null;   // library refused
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Reads and parses one file, normally the one the user picked.</summary>
        public static async Task<CredentialsFileResult> LoadAsync(StorageFile file)
        {
            if (file == null)
                return new CredentialsFileResult { Problem = "no file was chosen" };

            try
            {
                string text = await FileIO.ReadTextAsync(file).AsTask().ConfigureAwait(false);

                var result = CredentialsFile.Parse(text);
                result.Source = file.Name;
                return result;
            }
            catch (Exception ex)
            {
                return new CredentialsFileResult
                {
                    Problem = "could not read " + file.Name + ": " + ex.Message
                };
            }
        }
    }
}
