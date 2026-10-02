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
// VEvent -> the values a WP8.1 Appointment takes, decided here rather than in PhoneCalendars.
//
// Same arrangement as ContactMapping in GContactsSync, and for the same reason: everything here
// is plain C# over plain types, so tests\ compiles it straight from this file and exercises the
// rules on a desktop. PhoneCalendars holds only the parts that genuinely need the platform -
// opening the store, creating the calendar, saving an Appointment.
//
// The enums below MIRROR Windows.ApplicationModel.Appointments, values included, so the WinRT
// side is a cast rather than a translation. They are not invented: they were read out of the SDK
// metadata, and they can be checked again with
//
//   ildasm /text /item:Windows.ApplicationModel.Appointments.AppointmentRecurrenceUnit
//          "%ProgramFiles(x86)%\Windows Phone Kits\8.1\References\CommonConfiguration\Neutral\Windows.winmd"
//
// Guessing a WinRT name or value from memory is how GContactsSync shipped a "HomeTelephone"
// property that does not exist and silently refused every contact with a home number.
using System;
using System.Collections.Generic;
using GCalendarSync.Core.Models;

namespace GCalendarSync.Core.Services
{
    public enum PhoneRecurrenceUnit
    {
        Daily = 0,
        Weekly = 1,
        /// <summary>On a day of the month: "the 14th".</summary>
        Monthly = 2,
        /// <summary>On a weekday of the month: "the second Monday".</summary>
        MonthlyOnDay = 3,
        Yearly = 4,
        YearlyOnDay = 5
    }

    [Flags]
    public enum PhoneDaysOfWeek
    {
        None = 0,
        Sunday = 1,
        Monday = 2,
        Tuesday = 4,
        Wednesday = 8,
        Thursday = 16,
        Friday = 32,
        Saturday = 64
    }

    public enum PhoneWeekOfMonth
    {
        First = 0, Second = 1, Third = 2, Fourth = 3, Last = 4
    }

    public enum PhoneBusyStatus { Busy = 0, Tentative = 1, Free = 2, OutOfOffice = 3 }

    public enum PhoneSensitivity { Public = 0, Private = 1 }

    /// <summary>A repeat rule in the only shape the phone can hold.</summary>
    public sealed class PhoneRecurrence
    {
        public PhoneRecurrenceUnit Unit;
        public int Interval = 1;
        public PhoneDaysOfWeek DaysOfWeek;
        public PhoneWeekOfMonth WeekOfMonth;
        public int Day;      // day of the month, 1-31
        public int Month;    // 1-12
        public uint Occurrences;
        public DateTimeOffset? Until;
    }

    public static class AppointmentMapping
    {
        /// <summary>
        /// The repeat rule for an event, or null when it has none or the phone cannot hold it.
        ///
        /// Null for an unsupported rule is deliberate and is the whole point of this method. The
        /// phone's model covers "every N days/weeks/months/years", optionally on named weekdays or
        /// on a numbered weekday of the month. It cannot express BYSETPOS, BYWEEKNO or BYYEARDAY.
        /// Writing those rules without their qualifier would repeat the event on the WRONG days -
        /// a meeting appearing every Monday instead of the last Monday - and a wrong repeat is a
        /// worse answer than a single occurrence the user can see is single.
        /// </summary>
        public static PhoneRecurrence ToPhoneRecurrence(VEvent e)
        {
            if (e == null || e.Recurrence == null) return null;

            var rule = e.Recurrence;
            if (rule.IsUnsupported) return null;

            var result = new PhoneRecurrence { Interval = Math.Max(1, rule.Interval) };

            if (rule.Count > 0) result.Occurrences = (uint)rule.Count;
            else if (rule.Until.HasValue) result.Until = rule.Until;

            switch (rule.Frequency)
            {
                case "DAILY":
                    result.Unit = PhoneRecurrenceUnit.Daily;
                    return result;

                case "WEEKLY":
                {
                    result.Unit = PhoneRecurrenceUnit.Weekly;
                    result.DaysOfWeek = rule.ByDay.Count > 0
                        ? DaysFrom(rule.ByDay)
                        : DayOf(e.Start.DayOfWeek);

                    // A weekly rule with no day at all would repeat on nothing.
                    if (result.DaysOfWeek == PhoneDaysOfWeek.None) return null;
                    return result;
                }

                case "MONTHLY":
                    return Monthly(e, rule, result, PhoneRecurrenceUnit.Monthly,
                                   PhoneRecurrenceUnit.MonthlyOnDay);

                case "YEARLY":
                {
                    var mapped = Monthly(e, rule, result, PhoneRecurrenceUnit.Yearly,
                                         PhoneRecurrenceUnit.YearlyOnDay);
                    if (mapped == null) return null;
                    mapped.Month = rule.ByMonth > 0 ? rule.ByMonth : e.Start.Month;
                    return mapped;
                }

                default:
                    return null;
            }
        }

        /// <summary>
        /// The month-shaped half of MONTHLY and YEARLY, which differ only in whether a month
        /// number is also needed.
        /// </summary>
        static PhoneRecurrence Monthly(
            VEvent e, EventRecurrence rule, PhoneRecurrence result,
            PhoneRecurrenceUnit onDate, PhoneRecurrenceUnit onWeekday)
        {
            if (rule.ByDay.Count > 0)
            {
                // "the second Monday", "the last Friday". One weekday only: the phone has a single
                // WeekOfMonth, so "the first Monday AND the third Thursday" has no representation.
                if (rule.ByDay.Count != 1) return null;

                int ordinal;
                PhoneDaysOfWeek day;
                if (!TryParseOrdinalDay(rule.ByDay[0], out ordinal, out day)) return null;
                if (ordinal == 0) return null;   // "every Monday of the month" is not a thing here

                result.Unit = onWeekday;
                result.DaysOfWeek = day;
                result.WeekOfMonth = WeekFrom(ordinal);
                return result;
            }

            result.Unit = onDate;
            result.Day = rule.ByMonthDay > 0 ? rule.ByMonthDay : e.Start.Day;

            // A rule on the 29th, 30th or 31st does not occur in every month. The phone decides
            // for itself what to do; recording the day as written keeps the common months right.
            if (result.Day < 1 || result.Day > 31) return null;
            return result;
        }

        /// <summary>"MO" -> (0, Monday); "2MO" -> (2, Monday); "-1FR" -> (-1, Friday).</summary>
        public static bool TryParseOrdinalDay(string value, out int ordinal, out PhoneDaysOfWeek day)
        {
            ordinal = 0;
            day = PhoneDaysOfWeek.None;
            if (string.IsNullOrEmpty(value)) return false;

            string text = value.Trim().ToUpperInvariant();
            int split = text.Length - 2;
            if (split < 0) return false;

            string code = text.Substring(split);
            string number = text.Substring(0, split);

            day = DayFromCode(code);
            if (day == PhoneDaysOfWeek.None) return false;

            if (number.Length == 0) { ordinal = 0; return true; }
            return int.TryParse(number, out ordinal);
        }

        static PhoneWeekOfMonth WeekFrom(int ordinal)
        {
            if (ordinal < 0) return PhoneWeekOfMonth.Last;   // -1FR, and -2 has nowhere better
            switch (ordinal)
            {
                case 1: return PhoneWeekOfMonth.First;
                case 2: return PhoneWeekOfMonth.Second;
                case 3: return PhoneWeekOfMonth.Third;
                case 4: return PhoneWeekOfMonth.Fourth;
                default: return PhoneWeekOfMonth.Last;
            }
        }

        public static PhoneDaysOfWeek DaysFrom(IEnumerable<string> byDay)
        {
            var days = PhoneDaysOfWeek.None;
            foreach (var entry in byDay)
            {
                int ordinal;
                PhoneDaysOfWeek day;
                if (TryParseOrdinalDay(entry, out ordinal, out day)) days |= day;
            }
            return days;
        }

        static PhoneDaysOfWeek DayFromCode(string code)
        {
            switch (code)
            {
                case "SU": return PhoneDaysOfWeek.Sunday;
                case "MO": return PhoneDaysOfWeek.Monday;
                case "TU": return PhoneDaysOfWeek.Tuesday;
                case "WE": return PhoneDaysOfWeek.Wednesday;
                case "TH": return PhoneDaysOfWeek.Thursday;
                case "FR": return PhoneDaysOfWeek.Friday;
                case "SA": return PhoneDaysOfWeek.Saturday;
                default: return PhoneDaysOfWeek.None;
            }
        }

        public static PhoneDaysOfWeek DayOf(DayOfWeek day)
        {
            switch (day)
            {
                case DayOfWeek.Sunday: return PhoneDaysOfWeek.Sunday;
                case DayOfWeek.Monday: return PhoneDaysOfWeek.Monday;
                case DayOfWeek.Tuesday: return PhoneDaysOfWeek.Tuesday;
                case DayOfWeek.Wednesday: return PhoneDaysOfWeek.Wednesday;
                case DayOfWeek.Thursday: return PhoneDaysOfWeek.Thursday;
                case DayOfWeek.Friday: return PhoneDaysOfWeek.Friday;
                default: return PhoneDaysOfWeek.Saturday;
            }
        }

        // ------------------------------------------------------------------------------------
        // The scalars
        // ------------------------------------------------------------------------------------
        /// <summary>
        /// What the calendar should show. An event with no SUMMARY is not unusual - Google lets
        /// one be created with only a time - and an untitled row on the phone's agenda is far less
        /// use than the word the phone's own calendar uses for it.
        /// </summary>
        public static string SubjectFor(VEvent e)
        {
            if (e == null) return string.Empty;
            string subject = (e.Subject ?? string.Empty).Trim();
            if (subject.Length > 0) return subject;
            return (e.Location ?? string.Empty).Trim().Length > 0 ? e.Location.Trim() : "(no title)";
        }

        public static PhoneBusyStatus BusyStatusFor(VEvent e)
        {
            if (e == null) return PhoneBusyStatus.Busy;
            return e.IsFree ? PhoneBusyStatus.Free : PhoneBusyStatus.Busy;
        }

        public static PhoneSensitivity SensitivityFor(VEvent e)
        {
            if (e == null) return PhoneSensitivity.Public;
            return e.IsPrivate ? PhoneSensitivity.Private : PhoneSensitivity.Public;
        }

        /// <summary>
        /// The details to show, with a note when the start time could not be resolved exactly.
        /// Silently putting an event on the phone at a time that may be an hour out is worse than
        /// saying so where the user will see it.
        /// </summary>
        public static string DetailsFor(VEvent e)
        {
            if (e == null) return string.Empty;
            string details = e.Details ?? string.Empty;

            if (!e.TimeIsApproximate || e.IsAllDay) return details;

            string note = "(Time zone " + e.TimeZoneId + " could not be resolved exactly; " +
                          "this time is shown as if it were local.)";
            return details.Length > 0 ? details + "\n\n" + note : note;
        }

        /// <summary>
        /// The duration the phone stores. WP8.1 holds a start and a length, not an end.
        /// </summary>
        public static TimeSpan DurationFor(VEvent e)
        {
            if (e == null) return TimeSpan.FromHours(1);
            var span = e.End - e.Start;
            if (span <= TimeSpan.Zero) return e.IsAllDay ? TimeSpan.FromDays(1) : TimeSpan.FromHours(1);
            return span;
        }
    }
}
