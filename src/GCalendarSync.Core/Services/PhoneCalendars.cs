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
// The phone half of the sync: VEvent -> an Appointment in a calendar this app owns.
//
// This is a far better position than the contacts sibling is in. WP8.1 lets an app CREATE ITS OWN
// AppointmentCalendar, so everything written here lives in a calendar of this app's own, which the
// phone's Calendar shows alongside the others and which can be removed whole. Nothing this app
// does can touch an appointment the user made themselves, which is the thing the Symbian contacts
// port had to work hard for and never got completely.
//
// And an Appointment has no fixed slots: no equivalent of StoredContact's ten phone numbers, so
// none of the "what would not fit" machinery exists here.
//
// One calendar per Google calendar, not one for everything, so turning a switch off in Settings
// can delete exactly that calendar and leave the rest alone.
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.ApplicationModel.Appointments;
using GCalendarSync.Core.Models;

namespace GCalendarSync.Core.Services
{
    public static class PhoneCalendars
    {
        /// <summary>
        /// Prefixed so this app's calendars are recognisable among the phone's own, and so
        /// FindOwnAsync can find them again after an upgrade without storing anything.
        /// </summary>
        const string NamePrefix = "Google: ";

        public static async Task<AppointmentStore> OpenStoreAsync()
        {
            // AppCalendarsReadWrite, not AllCalendarsReadOnly: this app only ever needs the
            // calendars it created itself, and asking for less is the whole reason it cannot
            // damage anything else.
            var store = await AppointmentManager
                .RequestStoreAsync(AppointmentStoreAccessType.AppCalendarsReadWrite)
                .AsTask().ConfigureAwait(false);

            if (store == null)
                throw new InvalidOperationException(
                    "The calendar store could not be opened. Check that this app is allowed " +
                    "access to the calendar in the phone's settings.");
            return store;
        }

        /// <summary>Every calendar this app has created, by display name.</summary>
        public static async Task<List<AppointmentCalendar>> FindOwnAsync(AppointmentStore store)
        {
            var all = await store.FindAppointmentCalendarsAsync(
                FindAppointmentCalendarsOptions.IncludeHidden).AsTask().ConfigureAwait(false);

            var mine = new List<AppointmentCalendar>();
            foreach (var calendar in all) mine.Add(calendar);
            return mine;
        }

        /// <summary>
        /// The phone calendar standing in for one Google calendar, created on first use.
        /// </summary>
        public static async Task<AppointmentCalendar> OpenCalendarAsync(
            AppointmentStore store, CalendarInfo info)
        {
            string name = NamePrefix + DisplayNameFor(info);

            foreach (var existing in await FindOwnAsync(store).ConfigureAwait(false))
                if (existing.DisplayName == name) return existing;

            var calendar = await store.CreateAppointmentCalendarAsync(name)
                .AsTask().ConfigureAwait(false);

            // Let the phone's own Calendar show these, and let other apps read them. They came
            // from Google, where they are already readable by everything the account allows.
            calendar.OtherAppReadAccess = AppointmentCalendarOtherAppReadAccess.Full;
            calendar.OtherAppWriteAccess = AppointmentCalendarOtherAppWriteAccess.None;
            calendar.SummaryCardView = AppointmentSummaryCardView.System;
            await calendar.SaveAsync().AsTask().ConfigureAwait(false);

            return calendar;
        }

        static string DisplayNameFor(CalendarInfo info)
        {
            string name = (info.DisplayName ?? string.Empty).Trim();
            if (name.Length > 0) return name;
            return info.Kind.ToString();
        }

        /// <summary>Removes a calendar and everything in it - what a switch turned off means.</summary>
        public static async Task DeleteCalendarAsync(AppointmentCalendar calendar)
        {
            try { await calendar.DeleteAsync().AsTask().ConfigureAwait(false); }
            catch (Exception) { /* already gone is the outcome we wanted */ }
        }

        // ================================================================
        // Google -> phone
        // ================================================================
        /// <summary>
        /// Writes one event. Matching is by RoamingId, which carries the iCalendar UID, so an
        /// event edited on Google updates rather than duplicating.
        /// </summary>
        public static async Task WriteAsync(AppointmentCalendar calendar, VEvent e, string localId)
        {
            var appointment = new Appointment();

            appointment.RoamingId = e.Uid ?? string.Empty;
            appointment.Subject = Trim(AppointmentMapping.SubjectFor(e), 255);
            appointment.Details = Trim(AppointmentMapping.DetailsFor(e), 4000);
            appointment.Location = Trim(e.Location, 255);
            appointment.StartTime = e.Start;
            appointment.Duration = AppointmentMapping.DurationFor(e);
            appointment.AllDay = e.IsAllDay;
            appointment.BusyStatus = (AppointmentBusyStatus)(int)AppointmentMapping.BusyStatusFor(e);
            appointment.Sensitivity = (AppointmentSensitivity)(int)AppointmentMapping.SensitivityFor(e);

            if (!string.IsNullOrEmpty(e.Url))
            {
                // A malformed URL must not cost us the event.
                try { appointment.Uri = new Uri(e.Url); }
                catch (Exception) { }
            }

            if (e.ReminderMinutes.HasValue && e.ReminderMinutes.Value >= 0)
                appointment.Reminder = TimeSpan.FromMinutes(e.ReminderMinutes.Value);

            var repeat = AppointmentMapping.ToPhoneRecurrence(e);
            if (repeat != null) appointment.Recurrence = ToWinRt(repeat);

            if (!string.IsNullOrEmpty(localId))
            {
                // Replacing in place keeps the phone's own identity for the appointment, so a
                // reminder already scheduled against it is not lost on every sync.
                try
                {
                    await calendar.DeleteAppointmentAsync(localId).AsTask().ConfigureAwait(false);
                }
                catch (Exception) { }
            }

            await calendar.SaveAppointmentAsync(appointment).AsTask().ConfigureAwait(false);
        }

        public static async Task<bool> DeleteAsync(AppointmentCalendar calendar, string localId)
        {
            if (string.IsNullOrEmpty(localId)) return false;
            try
            {
                await calendar.DeleteAppointmentAsync(localId).AsTask().ConfigureAwait(false);
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// The phone's own id for an appointment this app wrote, found by the Google UID it was
        /// tagged with. Used when the stored map has been lost - after a reinstall, say.
        /// </summary>
        public static async Task<string> FindLocalIdAsync(AppointmentStore store, string uid)
        {
            if (string.IsNullOrEmpty(uid)) return null;
            try
            {
                var ids = await store.FindLocalIdsFromRoamingIdAsync(uid)
                    .AsTask().ConfigureAwait(false);
                foreach (var id in ids) return id;
            }
            catch (Exception) { }
            return null;
        }

        // ================================================================
        // The one type that has to be translated rather than cast
        // ================================================================
        /// <summary>
        /// AppointmentMapping's enums mirror the WinRT ones value for value, so each is a cast;
        /// the recurrence itself is an object and has to be built.
        /// </summary>
        static AppointmentRecurrence ToWinRt(PhoneRecurrence r)
        {
            var recurrence = new AppointmentRecurrence
            {
                Unit = (AppointmentRecurrenceUnit)(int)r.Unit,
                Interval = (uint)Math.Max(1, r.Interval),
                DaysOfWeek = (AppointmentDaysOfWeek)(int)r.DaysOfWeek,
                WeekOfMonth = (AppointmentWeekOfMonth)(int)r.WeekOfMonth
            };

            if (r.Day > 0) recurrence.Day = (uint)r.Day;
            if (r.Month > 0) recurrence.Month = (uint)r.Month;

            // Occurrences and Until are alternatives; setting both is rejected.
            if (r.Occurrences > 0) recurrence.Occurrences = r.Occurrences;
            else if (r.Until.HasValue) recurrence.Until = r.Until.Value;

            return recurrence;
        }

        static string Trim(string value, int max)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return value.Length <= max ? value : value.Substring(0, max);
        }
    }
}
