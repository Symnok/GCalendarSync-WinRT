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
// Small file helpers for the sync bookkeeping.
//
// The Windows 10 Mobile version keeps its etag, href and hash maps in LocalSettings, one
// setting per contact. That does not scale here: LocalSettings is registry-backed, and an
// address book of several hundred contacts turns into a couple of thousand settings that the
// background task has to walk every fifteen minutes. A tab-separated file per map is one read
// and one write, and can be looked at with the file system tools when something goes wrong.
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;

namespace GCalendarSync.Core.Storage
{
    internal static class LocalFiles
    {
        /// <summary>Reads a whole text file, or null when it does not exist.</summary>
        public static async Task<string> ReadTextAsync(string name)
        {
            try
            {
                // WP8.1 has no TryGetItemAsync - that arrived with Windows 10 - so a missing
                // file is a thrown FileNotFoundException and nothing else.
                var file = await ApplicationData.Current.LocalFolder
                    .GetFileAsync(name).AsTask().ConfigureAwait(false);
                return await FileIO.ReadTextAsync(file).AsTask().ConfigureAwait(false);
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static async Task WriteTextAsync(string name, string text)
        {
            var file = await ApplicationData.Current.LocalFolder
                .CreateFileAsync(name, CreationCollisionOption.ReplaceExisting)
                .AsTask().ConfigureAwait(false);
            await FileIO.WriteTextAsync(file, text ?? string.Empty).AsTask().ConfigureAwait(false);
        }

        public static async Task DeleteAsync(string name)
        {
            try
            {
                var file = await ApplicationData.Current.LocalFolder
                    .GetFileAsync(name).AsTask().ConfigureAwait(false);
                await file.DeleteAsync().AsTask().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Not there, which is the state we wanted.
            }
        }

        /// <summary>A key/value map from a tab-separated file. Missing file gives an empty map.</summary>
        public static async Task<Dictionary<string, string>> ReadMapAsync(string name)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            string text = await ReadTextAsync(name).ConfigureAwait(false);
            if (string.IsNullOrEmpty(text)) return map;

            foreach (var line in text.Split('\n'))
            {
                string row = line.TrimEnd('\r');
                if (row.Length == 0) continue;
                int tab = row.IndexOf('\t');
                if (tab <= 0) continue;
                map[row.Substring(0, tab)] = row.Substring(tab + 1);
            }
            return map;
        }

        public static Task WriteMapAsync(string name, Dictionary<string, string> map)
        {
            var sb = new StringBuilder();
            foreach (var kv in map)
            {
                // A tab or newline in a key or value would break the format. Neither can appear
                // in an href, an etag, a UID or a hash, so they are dropped rather than escaped.
                sb.Append(Clean(kv.Key)).Append('\t').Append(Clean(kv.Value)).Append('\n');
            }
            return WriteTextAsync(name, sb.ToString());
        }

        static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        }

        /// <summary>
        /// A file name that is safe on the phone's file system and unique for this key. The
        /// readable part is truncated, so a short hash of the whole key is appended to keep two
        /// long UIDs sharing a prefix from colliding.
        /// </summary>
        public static string SafeFileName(string key, string extension)
        {
            var sb = new StringBuilder();
            foreach (char ch in key ?? string.Empty)
            {
                if (sb.Length >= 40) break;
                sb.Append((char.IsLetterOrDigit(ch) || ch == '-' || ch == '_') ? ch : '_');
            }

            // FNV-1a, 32 bit: short, stable, and good enough to separate distinct keys.
            uint hash = 2166136261;
            foreach (char ch in key ?? string.Empty)
            {
                hash ^= ch;
                hash *= 16777619;
            }

            return sb.ToString() + "-" + hash.ToString("x8") + extension;
        }
    }
}
