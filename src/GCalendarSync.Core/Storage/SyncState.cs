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
// Everything the app has to remember between syncs:
//
//   CTags       calendar path -> collection tag, so a calendar nothing has changed in can be
//               skipped without listing its contents at all
//   Etags       event path    -> etag, so only what moved is downloaded
//   LocalIds    Google UID    -> the phone's own id for the appointment, so an edit on Google
//               replaces the appointment instead of duplicating it
//   Calendars   calendar path -> the phone calendar's display name, so a switch turned off in
//               Settings can delete exactly the right calendar
//
// Download only, so there is no fingerprint of what the phone holds and no cached original card:
// this app never sends anything back, and nothing it writes has to survive a round trip. That is
// most of what the contacts sibling's sync state exists for, and it is all absent here.
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace GCalendarSync.Core.Storage
{
    public sealed class SyncState
    {
        const string CTagsFile     = "ctags.tsv";
        const string EtagsFile     = "etags.tsv";
        const string LocalIdsFile  = "localids.tsv";
        const string CalendarsFile = "calendars.tsv";

        public Dictionary<string, string> CTags     = new Dictionary<string, string>(StringComparer.Ordinal);
        public Dictionary<string, string> Etags     = new Dictionary<string, string>(StringComparer.Ordinal);
        public Dictionary<string, string> LocalIds  = new Dictionary<string, string>(StringComparer.Ordinal);
        public Dictionary<string, string> Calendars = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>No etags means nothing has ever been downloaded, so the next run is a full one.</summary>
        public bool HasSynced { get { return Etags.Count > 0; } }

        public static async Task<SyncState> LoadAsync()
        {
            return new SyncState
            {
                CTags     = await LocalFiles.ReadMapAsync(CTagsFile).ConfigureAwait(false),
                Etags     = await LocalFiles.ReadMapAsync(EtagsFile).ConfigureAwait(false),
                LocalIds  = await LocalFiles.ReadMapAsync(LocalIdsFile).ConfigureAwait(false),
                Calendars = await LocalFiles.ReadMapAsync(CalendarsFile).ConfigureAwait(false)
            };
        }

        public async Task SaveAsync()
        {
            await LocalFiles.WriteMapAsync(CTagsFile, CTags).ConfigureAwait(false);
            await LocalFiles.WriteMapAsync(EtagsFile, Etags).ConfigureAwait(false);
            await LocalFiles.WriteMapAsync(LocalIdsFile, LocalIds).ConfigureAwait(false);
            await LocalFiles.WriteMapAsync(CalendarsFile, Calendars).ConfigureAwait(false);
        }

        public string LocalIdFor(string uid)
        {
            string id;
            if (string.IsNullOrEmpty(uid) || !LocalIds.TryGetValue(uid, out id)) return null;
            return id;
        }

        /// <summary>Drops everything remembered about one event.</summary>
        public void Forget(string href, string uid)
        {
            if (!string.IsNullOrEmpty(href)) Etags.Remove(href);
            if (!string.IsNullOrEmpty(uid)) LocalIds.Remove(uid);
        }

        /// <summary>
        /// Drops everything remembered about one calendar. Used when a switch is turned off: the
        /// phone calendar is deleted, and so must every record that would otherwise make the next
        /// sync believe its events are still on the phone.
        /// </summary>
        public void ForgetCalendar(string calendarHref)
        {
            if (string.IsNullOrEmpty(calendarHref)) return;

            CTags.Remove(calendarHref);
            Calendars.Remove(calendarHref);

            // An event's path lives under its calendar's path, which is what makes this possible
            // without recording the calendar against every single event.
            var doomed = new List<string>();
            foreach (var href in Etags.Keys)
                if (href.StartsWith(calendarHref, StringComparison.Ordinal)) doomed.Add(href);
            foreach (var href in doomed) Etags.Remove(href);
        }

        public static async Task ClearAsync()
        {
            await LocalFiles.DeleteAsync(CTagsFile).ConfigureAwait(false);
            await LocalFiles.DeleteAsync(EtagsFile).ConfigureAwait(false);
            await LocalFiles.DeleteAsync(LocalIdsFile).ConfigureAwait(false);
            await LocalFiles.DeleteAsync(CalendarsFile).ConfigureAwait(false);
        }
    }
}
