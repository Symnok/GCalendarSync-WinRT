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
// Google -> phone, and only that direction. There is no upload: nothing on the phone is ever sent
// back, so nothing the user does in the phone's Calendar can reach Google, and an event this app
// wrote is replaced from Google on the next sync.
//
// The shape of a run:
//
//   1. discover the calendars, and classify them into the four the settings switch on
//   2. delete the phone calendars for any switch that is now off
//   3. for each calendar that is on:
//        a. compare the collection tag; skip the whole calendar when it has not changed
//        b. list the events in the window, and compare etags
//        c. fetch what changed, twenty-five at a time, and write them
//        d. delete what Google no longer has
//
// Step 3a is the one that makes a fifteen-minute timer reasonable: on a calendar where nothing
// has happened it costs one request and no parsing at all.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.Appointments;
using GCalendarSync.Core.Models;
using GCalendarSync.Core.Net;
using GCalendarSync.Core.Services;
using GCalendarSync.Core.Storage;

namespace GCalendarSync.Core.Sync
{
    public sealed class SyncOutcome
    {
        public bool Success;
        public bool WasFullSync;

        public int Written;        // events created or updated on the phone
        public int Removed;        // events deleted from the phone
        public int Failed;
        public int CalendarsSynced;
        public int CalendarsUnchanged;
        public int CalendarsRemoved;

        /// <summary>Events Google listed in the window, before etag comparison.</summary>
        public int Listed;
        public string FirstFailure = string.Empty;

        public string Message = string.Empty;
        public string Error = string.Empty;
        public bool NothingToDo;
    }

    public static class SyncEngine
    {
        const string BusyMessage = "Another sync is already running. Try again in a moment.";

        /// <summary>
        /// How many events may fail, with none succeeding, before a run gives up. Two full
        /// multiget batches: enough that a handful of odd events cannot trigger it, few enough
        /// that a calendar failing wholesale is reported in seconds rather than minutes.
        /// </summary>
        const int GiveUpAfter = 50;

        public static async Task<SyncOutcome> DownloadAsync(
            string email, string password, IProgress<string> progress, CancellationToken ct)
        {
            var outcome = new SyncOutcome();

            if (!AppSettings.TryBeginSync())
            {
                outcome.Error = BusyMessage;
                return outcome;
            }

            CalDavClient client = null;
            try
            {
                Report(progress, "Loading certificates...");
                var trustBundle = await TrustBundle.LoadAsync().ConfigureAwait(false);

                client = new CalDavClient(email, password, trustBundle);
                var state = await SyncState.LoadAsync().ConfigureAwait(false);
                var store = await PhoneCalendars.OpenStoreAsync().ConfigureAwait(false);

                outcome.WasFullSync = !state.HasSynced;

                Report(progress, "Asking Google which calendars there are...");
                var all = await client.DiscoverAsync(ct).ConfigureAwait(false);

                var wanted = CalendarKinds.Selected(
                    all,
                    AppSettings.SyncPersonal, AppSettings.SyncBirthdays,
                    AppSettings.SyncHolidays,
                    AppSettings.SyncOther);

                await RemoveUnwantedAsync(store, state, all, wanted, outcome).ConfigureAwait(false);

                if (wanted.Count == 0)
                {
                    outcome.Success = true;
                    outcome.Message = "No calendars are switched on. Choose some in settings.";
                    await state.SaveAsync().ConfigureAwait(false);
                    return outcome;
                }

                var now = DateTimeOffset.Now;
                var from = MonthStart(now, -AppSettings.WindowMonthsBack);
                var to = MonthStart(now, AppSettings.WindowMonthsForward + 1);

                foreach (var calendar in wanted)
                {
                    ct.ThrowIfCancellationRequested();
                    await SyncOneAsync(client, store, state, calendar, from, to,
                                       outcome, progress, ct).ConfigureAwait(false);
                }

                await state.SaveAsync().ConfigureAwait(false);

                outcome.Success = true;
                AppSettings.LastDownload = Timestamp();
                outcome.Message = Describe(outcome);
                return outcome;
            }
            catch (OperationCanceledException)
            {
                outcome.Error = "Sync cancelled.";
                return outcome;
            }
            catch (CalDavException ex)
            {
                outcome.Error = ex.Message;
                return outcome;
            }
            catch (Exception ex)
            {
                outcome.Error = ex.Message;
                return outcome;
            }
            finally
            {
                if (client != null) client.Dispose();
                AppSettings.EndSync();
            }
        }

        // ================================================================
        // One calendar
        // ================================================================
        static async Task SyncOneAsync(
            CalDavClient client, AppointmentStore store, SyncState state, CalendarInfo info,
            DateTimeOffset from, DateTimeOffset to,
            SyncOutcome outcome, IProgress<string> progress, CancellationToken ct)
        {
            string label = info.DisplayName.Length > 0 ? info.DisplayName : info.Kind.ToString();

            // The cheap question first: has anything in this calendar changed at all? On a
            // fifteen-minute timer, most runs end here having cost one request.
            string ctag = info.CTag.Length > 0
                ? info.CTag
                : await client.GetCTagAsync(info.Href, ct).ConfigureAwait(false);

            string knownCtag;
            if (ctag.Length > 0 && state.CTags.TryGetValue(info.Href, out knownCtag) &&
                knownCtag == ctag && state.HasSynced)
            {
                outcome.CalendarsUnchanged++;
                return;
            }

            Report(progress, "Checking " + label + "...");

            var serverEtags = await client.ListAsync(info.Href, from, to, ct).ConfigureAwait(false);
            outcome.Listed += serverEtags.Count;

            var phoneCalendar = await PhoneCalendars
                .OpenCalendarAsync(store, info).ConfigureAwait(false);
            state.Calendars[info.Href] = phoneCalendar.DisplayName;

            // What to fetch: anything whose etag we do not already have, or which has moved.
            var changed = new List<string>();
            foreach (var pair in serverEtags)
            {
                string known;
                if (!state.Etags.TryGetValue(pair.Key, out known) || known != pair.Value)
                    changed.Add(pair.Key);
            }

            // What Google no longer has in the window. Note this is about the window, not about
            // deletion: an event that has simply scrolled out of range is removed from the phone
            // too, which is the behaviour that keeps the phone calendar matching the window.
            var gone = new List<string>();
            foreach (var href in state.Etags.Keys)
                if (href.StartsWith(info.Href, StringComparison.Ordinal) &&
                    !serverEtags.ContainsKey(href))
                    gone.Add(href);

            if (changed.Count == 0 && gone.Count == 0)
            {
                if (ctag.Length > 0) state.CTags[info.Href] = ctag;
                outcome.CalendarsUnchanged++;
                return;
            }

            Report(progress, label + ": " + changed.Count + " changed, " + gone.Count + " removed.");

            bool complete = await FetchAndWriteAsync(
                client, store, state, info, phoneCalendar, changed, serverEtags,
                label, outcome, progress, ct).ConfigureAwait(false);

            foreach (var href in gone)
            {
                ct.ThrowIfCancellationRequested();
                await RemoveAsync(state, phoneCalendar, href, outcome).ConfigureAwait(false);
            }

            // Only record the collection tag when everything in it actually arrived. Recording it
            // after a partial run would make the next sync skip the calendar entirely and the
            // missing events would never be retried.
            if (complete && ctag.Length > 0) state.CTags[info.Href] = ctag;
            else state.CTags.Remove(info.Href);

            outcome.CalendarsSynced++;
        }

        static async Task<bool> FetchAndWriteAsync(
            CalDavClient client, AppointmentStore store, SyncState state, CalendarInfo info,
            AppointmentCalendar phoneCalendar, List<string> changed,
            Dictionary<string, string> serverEtags, string label,
            SyncOutcome outcome, IProgress<string> progress, CancellationToken ct)
        {

            for (int offset = 0; offset < changed.Count; offset += CalDavClient.MultigetBatch)
            {
                ct.ThrowIfCancellationRequested();

                int count = Math.Min(CalDavClient.MultigetBatch, changed.Count - offset);
                var batch = changed.GetRange(offset, count);

                var fetched = await client
                    .FetchBatchAsync(info.Href, batch, serverEtags, ct).ConfigureAwait(false);

                foreach (var resource in fetched)
                {
                    ct.ThrowIfCancellationRequested();
                    await WriteResourceAsync(store, state, phoneCalendar, resource, outcome)
                        .ConfigureAwait(false);
                }

                Report(progress, label + ": saved " + outcome.Written + " of " + changed.Count + "...");

                // Nothing has been written and the failures are piling up, so whatever is wrong is
                // wrong for the whole calendar. Another few hundred attempts would cost minutes
                // and say exactly what the first fifty already have.
                if (outcome.Written == 0 && outcome.Failed >= GiveUpAfter &&
                    offset + count < changed.Count)
                    return false;
            }
            return true;
        }

        /// <summary>
        /// One resource, which may hold a master event and an override per edited occurrence.
        ///
        /// Only the master is written. The phone's recurrence model has no way to express "this
        /// series, except that the one on the 12th moved to 2pm" - there is no exception list on
        /// an Appointment - so writing the overrides as well would put duplicate events on the
        /// phone for every edited occurrence. The series as Google defines it is the closer answer.
        /// </summary>
        static async Task WriteResourceAsync(
            AppointmentStore store, SyncState state, AppointmentCalendar phoneCalendar,
            FetchedEvent resource, SyncOutcome outcome)
        {
            VEvent master = null;
            foreach (var candidate in resource.Events)
                if (!candidate.IsOverride) { master = candidate; break; }
            if (master == null && resource.Events.Count > 0) master = resource.Events[0];

            if (master == null)
            {
                Failed(outcome, "the event could not be read");
                return;
            }

            if (string.IsNullOrEmpty(master.Uid))
            {
                Failed(outcome, "the event had no UID");
                return;
            }

            // A cancelled event is a deletion that still has a body.
            if (master.IsCancelled)
            {
                await RemoveAsync(state, phoneCalendar, resource.Href, outcome).ConfigureAwait(false);
                return;
            }


            string localId = state.LocalIdFor(master.Uid);
            if (localId == null)
                localId = await PhoneCalendars.FindLocalIdAsync(store, master.Uid).ConfigureAwait(false);

            try
            {
                await PhoneCalendars.WriteAsync(phoneCalendar, master, localId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // One unwriteable event must not abandon the rest of the calendar - but it must
                // not vanish without trace either.
                Failed(outcome, Describe(ex));
                return;
            }

            // Found again by UID, because SaveAppointmentAsync assigns the local id itself.
            string written = await PhoneCalendars
                .FindLocalIdAsync(store, master.Uid).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(written)) state.LocalIds[master.Uid] = written;

            state.Etags[resource.Href] = resource.Etag;
            outcome.Written++;
        }

        static async Task RemoveAsync(
            SyncState state, AppointmentCalendar phoneCalendar, string href, SyncOutcome outcome)
        {
            // Google names an event resource after its UID, so the path is enough to find what
            // was written for it - no second map to keep in step.
            string uid = UidFromHref(href);

            string localId = state.LocalIdFor(uid);
            if (localId != null &&
                await PhoneCalendars.DeleteAsync(phoneCalendar, localId).ConfigureAwait(false))
                outcome.Removed++;

            state.Forget(href, uid);
        }

        /// <summary>
        /// Google names an event resource "&lt;uid&gt;.ics", so the path yields the UID. Only a
        /// fallback: the UID map is authoritative while it has an entry.
        /// </summary>
        static string UidFromHref(string href)
        {
            if (string.IsNullOrEmpty(href)) return string.Empty;

            int slash = href.LastIndexOf('/');
            string name = slash >= 0 ? href.Substring(slash + 1) : href;
            if (name.EndsWith(".ics", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - 4);

            try { return Uri.UnescapeDataString(name); }
            catch (Exception) { return name; }
        }

        // ================================================================
        // Switches turned off
        // ================================================================
        /// <summary>
        /// Deletes the phone calendar for anything no longer selected. This is what makes a switch
        /// mean something: turning Holidays off removes the holidays from the phone rather than
        /// merely stopping them being refreshed.
        /// </summary>
        static async Task RemoveUnwantedAsync(
            AppointmentStore store, SyncState state,
            List<CalendarInfo> all, List<CalendarInfo> wanted, SyncOutcome outcome)
        {
            var keep = new HashSet<string>(StringComparer.Ordinal);
            foreach (var calendar in wanted) keep.Add(calendar.Href);

            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in state.Calendars)
                if (!keep.Contains(pair.Key)) names[pair.Key] = pair.Value;

            if (names.Count == 0) return;

            var onPhone = await PhoneCalendars.FindOwnAsync(store).ConfigureAwait(false);
            foreach (var pair in names)
            {
                foreach (var calendar in onPhone)
                {
                    if (calendar.DisplayName != pair.Value) continue;
                    await PhoneCalendars.DeleteCalendarAsync(calendar).ConfigureAwait(false);
                    outcome.CalendarsRemoved++;
                    break;
                }
                state.ForgetCalendar(pair.Key);
            }
        }

        // ================================================================
        // Helpers
        // ================================================================
        /// <summary>
        /// The first of a month, offset months from now. Whole months on purpose: a window
        /// anchored on today's date makes two runs a day apart disagree about what is in range,
        /// which on a phone shows up as events appearing and vanishing for no reason.
        /// </summary>
        public static DateTimeOffset MonthStart(DateTimeOffset now, int offsetMonths)
        {
            int index = now.Month - 1 + offsetMonths;
            int year = now.Year + (int)Math.Floor(index / 12.0);
            int month = ((index % 12) + 12) % 12 + 1;
            return new DateTimeOffset(new DateTime(year, month, 1, 0, 0, 0), now.Offset);
        }

        static bool Failed(SyncOutcome outcome, string reason)
        {
            outcome.Failed++;
            if (outcome.FirstFailure.Length == 0 && !string.IsNullOrEmpty(reason))
                outcome.FirstFailure = reason;
            return false;
        }

        static string Describe(Exception ex)
        {
            string message = (ex.Message ?? string.Empty).Trim();
            if (message.Length > 200) message = message.Substring(0, 200) + "...";
            string name = ex.GetType().Name;
            return message.Length == 0 ? name : name + ": " + message;
        }

        static string Describe(SyncOutcome outcome)
        {
            var parts = new List<string>();
            if (outcome.Written > 0) parts.Add(Events(outcome.Written) + " added or updated");
            if (outcome.Removed > 0) parts.Add(Events(outcome.Removed) + " removed");
            if (outcome.CalendarsRemoved > 0)
                parts.Add(outcome.CalendarsRemoved + " calendar(s) taken off the phone");

            if (parts.Count == 0)
            {
                string quiet = outcome.CalendarsUnchanged > 0
                    ? "Already up to date - nothing changed on Google."
                    : "Nothing to download in the window.";
                return quiet;
            }

            string message = string.Join(", ", parts.ToArray()) + ".";
            if (outcome.Failed > 0)
            {
                message += "\n" + Events(outcome.Failed) + " could not be written";
                if (outcome.FirstFailure.Length > 0) message += ": " + outcome.FirstFailure;
                message += ".";
            }
            return message;
        }

        static string Events(int count)
        {
            return count == 1 ? "1 event" : count + " events";
        }

        static void Report(IProgress<string> progress, string message)
        {
            if (progress != null) progress.Report(message);
        }

        static string Timestamp()
        {
            return DateTime.Now.ToString("dd MMM yyyy HH:mm");
        }
    }
}
