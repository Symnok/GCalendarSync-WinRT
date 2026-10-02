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
// iCalendar -> VEvent. Only what Google actually sends is handled.
//
// The one thing that makes this harder than the vCard parser is that an iCalendar body is a tree.
// A VCALENDAR holds VEVENTs; a VEVENT holds VALARMs; and a VALARM has its own DESCRIPTION and its
// own TRIGGER. Reading the body as a flat list of lines - which is exactly what the vCard parser
// does, correctly, for its own format - puts the alarm's description in the event's Details on
// every event that has a reminder. So this tracks component depth and only ever reads properties
// belonging to the component it is currently in.
using System;
using System.Collections.Generic;
using System.Globalization;
using GCalendarSync.Core.Models;

namespace GCalendarSync.Core.Services
{
    public static class ICalParser
    {
        /// <summary>
        /// Every VEVENT in a body, masters and overrides alike, in the order they appear.
        ///
        /// One resource usually holds one event, but a repeating event whose occurrences have been
        /// edited arrives as a master VEVENT followed by one VEVENT per edited occurrence, all
        /// sharing a UID and distinguished by RECURRENCE-ID.
        /// </summary>
        public static List<VEvent> ParseEvents(string body)
        {
            var events = new List<VEvent>();
            if (string.IsNullOrEmpty(body)) return events;

            var lines = ICalText.Unfold(body);

            // Resolving a TZID to an offset needs the VTIMEZONE components, which may appear after
            // the events that use them, so they are collected in a first pass.
            var zones = ReadTimeZones(lines);

            VEvent current = null;
            string pendingStartZone = null;
            var stack = new List<string>();

            foreach (var line in lines)
            {
                string name = ICalText.PropertyName(line);
                if (name.Length == 0) continue;

                string value = ICalText.RawValue(line);

                if (name == "BEGIN")
                {
                    string component = value.Trim().ToUpperInvariant();
                    stack.Add(component);
                    if (component == "VEVENT" && Depth(stack, "VEVENT") == 1)
                    {
                        current = new VEvent();
                        pendingStartZone = null;
                    }
                    continue;
                }

                if (name == "END")
                {
                    string component = value.Trim().ToUpperInvariant();
                    if (component == "VEVENT" && current != null)
                    {
                        Finish(current, pendingStartZone);
                        events.Add(current);
                        current = null;
                    }
                    if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                    continue;
                }

                if (current == null) continue;

                string inside = stack.Count > 0 ? stack[stack.Count - 1] : string.Empty;

                if (inside == "VALARM")
                {
                    // The ONLY thing worth taking from an alarm is when it fires. Its DESCRIPTION,
                    // SUMMARY and ATTENDEE belong to the alarm, not to the event.
                    if (name == "TRIGGER" && !current.ReminderMinutes.HasValue)
                        current.ReminderMinutes = ParseTrigger(value);
                    continue;
                }

                if (inside != "VEVENT") continue;

                string head = ICalText.Head(line);

                switch (name)
                {
                    case "UID":
                        current.Uid = ICalText.Unescape(value).Trim();
                        break;

                    case "SUMMARY":
                        current.Subject = ICalText.Unescape(value).Trim();
                        break;

                    case "DESCRIPTION":
                        current.Details = ICalText.Unescape(value);
                        break;

                    case "LOCATION":
                        current.Location = ICalText.Unescape(value).Trim();
                        break;

                    case "URL":
                        current.Url = ICalText.Unescape(value).Trim();
                        break;

                    case "DTSTART":
                    {
                        bool allDay, approximate;
                        string zone = ICalText.Param(head, "TZID");
                        current.Start = ParseDateTime(value, head, zone, zones,
                                                      out allDay, out approximate);
                        current.IsAllDay = allDay;
                        current.TimeZoneId = zone;
                        current.TimeIsApproximate = approximate;
                        pendingStartZone = zone;
                        break;
                    }

                    case "DTEND":
                    {
                        bool allDay, approximate;
                        string zone = ICalText.Param(head, "TZID");
                        current.End = ParseDateTime(value, head, zone, zones,
                                                    out allDay, out approximate);
                        break;
                    }

                    case "DURATION":
                        // Recorded by leaving End unset; Finish turns it into an End.
                        current.End = default(DateTimeOffset);
                        _lastDuration = ParseDuration(value);
                        break;

                    case "RRULE":
                        current.Recurrence = ParseRecurrence(value, zones);
                        break;

                    case "RECURRENCE-ID":
                    {
                        bool allDay, approximate;
                        string zone = ICalText.Param(head, "TZID");
                        current.RecurrenceId = ParseDateTime(value, head, zone, zones,
                                                             out allDay, out approximate);
                        break;
                    }

                    case "STATUS":
                        current.IsCancelled =
                            value.Trim().ToUpperInvariant() == "CANCELLED";
                        break;

                    case "TRANSP":
                        current.IsFree = value.Trim().ToUpperInvariant() == "TRANSPARENT";
                        break;

                    case "CLASS":
                    {
                        string klass = value.Trim().ToUpperInvariant();
                        current.IsPrivate = klass == "PRIVATE" || klass == "CONFIDENTIAL";
                        break;
                    }

                    case "CATEGORIES":
                        foreach (var part in ICalText.Unescape(value).Split(','))
                            if (part.Trim().Length > 0)
                                current.Categories.Add(part.Trim().ToUpperInvariant());
                        break;
                }
            }

            return events;
        }

        // DURATION is rare enough from Google that threading it through every call would cost more
        // clarity than it buys; it is consumed by the Finish immediately after it is read.
        [ThreadStatic]
        static TimeSpan _lastDuration;

        static int Depth(List<string> stack, string component)
        {
            int depth = 0;
            foreach (var entry in stack)
                if (entry == component) depth++;
            return depth;
        }

        static void Finish(VEvent e, string startZone)
        {
            if (e.End == default(DateTimeOffset))
            {
                e.End = _lastDuration > TimeSpan.Zero
                    ? e.Start + _lastDuration
                    : e.Start + (e.IsAllDay ? TimeSpan.FromDays(1) : TimeSpan.FromHours(1));
            }
            _lastDuration = TimeSpan.Zero;

            // An end at or before the start is not a usable appointment; the phone shows a
            // negative-length event as a glitch rather than refusing it.
            if (e.End <= e.Start)
                e.End = e.Start + (e.IsAllDay ? TimeSpan.FromDays(1) : TimeSpan.FromMinutes(30));

            if (string.IsNullOrEmpty(e.TimeZoneId) && !string.IsNullOrEmpty(startZone))
                e.TimeZoneId = startZone;
        }

        // ------------------------------------------------------------------------------------
        // Dates
        // ------------------------------------------------------------------------------------
        /// <summary>
        /// The three forms a date-time takes, and what this app does with each:
        ///
        ///   20261005          VALUE=DATE, an all-day event. Local midnight, AllDay set.
        ///   20261005T090000Z  UTC. Exact, and the only form that always is.
        ///   20261005T090000   floating, or local to a TZID. Resolved from VTIMEZONE when the body
        ///                     carries one, otherwise read as the phone's own local time and
        ///                     flagged approximate.
        /// </summary>
        static DateTimeOffset ParseDateTime(
            string value, string head, string zoneId,
            Dictionary<string, TimeSpan> zones, out bool allDay, out bool approximate)
        {
            allDay = false;
            approximate = false;

            string text = (value ?? string.Empty).Trim();
            if (text.Length == 0) return DateTimeOffset.MinValue;

            bool declaredDate = string.Equals(ICalText.Param(head, "VALUE"), "DATE",
                                             StringComparison.OrdinalIgnoreCase);

            if (declaredDate || text.Length == 8)
            {
                allDay = true;
                int y, m, d;
                if (!TryDateParts(text, out y, out m, out d)) return DateTimeOffset.MinValue;
                try
                {
                    var midnight = new DateTime(y, m, d, 0, 0, 0, DateTimeKind.Unspecified);
                    return new DateTimeOffset(midnight, TimeZoneInfo.Local.GetUtcOffset(midnight));
                }
                catch (Exception) { return DateTimeOffset.MinValue; }
            }

            if (text.Length < 15) return DateTimeOffset.MinValue;

            int year, month, day, hour, minute, second;
            if (!TryDateParts(text, out year, out month, out day)) return DateTimeOffset.MinValue;
            if (!int.TryParse(text.Substring(9, 2), out hour)) return DateTimeOffset.MinValue;
            if (!int.TryParse(text.Substring(11, 2), out minute)) return DateTimeOffset.MinValue;
            if (!int.TryParse(text.Substring(13, 2), out second)) return DateTimeOffset.MinValue;

            DateTime naive;
            try { naive = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified); }
            catch (Exception) { return DateTimeOffset.MinValue; }

            if (text.EndsWith("Z", StringComparison.OrdinalIgnoreCase))
                return new DateTimeOffset(naive, TimeSpan.Zero);

            TimeSpan offset;
            if (!string.IsNullOrEmpty(zoneId) && zones != null && zones.TryGetValue(zoneId, out offset))
                return new DateTimeOffset(naive, offset);

            // No VTIMEZONE to resolve it with. Reading it as the phone's own local time is right
            // whenever the calendar and the phone are in the same zone, which is the ordinary
            // case, and wrong by the difference when they are not - hence the flag.
            approximate = !string.IsNullOrEmpty(zoneId);
            return new DateTimeOffset(naive, TimeZoneInfo.Local.GetUtcOffset(naive));
        }

        static bool TryDateParts(string text, out int year, out int month, out int day)
        {
            year = month = day = 0;
            if (text.Length < 8) return false;
            return int.TryParse(text.Substring(0, 4), out year)
                && int.TryParse(text.Substring(4, 2), out month)
                && int.TryParse(text.Substring(6, 2), out day);
        }

        /// <summary>
        /// TZID -> offset, from the VTIMEZONE components in the body.
        ///
        /// Only the STANDARD and DAYLIGHT offsets are read, and the one whose observance starts
        /// later in the year wins for summer dates - a real VTIMEZONE states its transitions as
        /// recurrence rules, and expanding those is a disproportionate amount of machinery for a
        /// phone. The error this leaves is at most one hour, and only for events in a zone the
        /// phone is not itself in; AppointmentMapping records it as TimeIsApproximate.
        /// </summary>
        static Dictionary<string, TimeSpan> ReadTimeZones(List<string> lines)
        {
            var zones = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);

            string tzid = null;
            bool inZone = false, inObservance = false;
            TimeSpan standard = TimeSpan.Zero;
            bool haveStandard = false;

            foreach (var line in lines)
            {
                string name = ICalText.PropertyName(line);
                if (name.Length == 0) continue;
                string value = ICalText.RawValue(line).Trim();

                if (name == "BEGIN")
                {
                    string component = value.ToUpperInvariant();
                    if (component == "VTIMEZONE") { inZone = true; tzid = null; haveStandard = false; }
                    else if (inZone && (component == "STANDARD" || component == "DAYLIGHT"))
                        inObservance = true;
                    continue;
                }

                if (name == "END")
                {
                    string component = value.ToUpperInvariant();
                    if (component == "VTIMEZONE")
                    {
                        if (inZone && tzid != null && haveStandard) zones[tzid] = standard;
                        inZone = false;
                        tzid = null;
                    }
                    else if (component == "STANDARD" || component == "DAYLIGHT")
                        inObservance = false;
                    continue;
                }

                if (!inZone) continue;

                if (name == "TZID" && !inObservance) tzid = value;

                // Prefer STANDARD: an event's own date decides whether summer time applies, and
                // guessing wrong on the standard offset is the smaller error.
                if (name == "TZOFFSETTO" && inObservance && !haveStandard)
                {
                    TimeSpan parsed;
                    if (TryParseUtcOffset(value, out parsed)) { standard = parsed; haveStandard = true; }
                }
            }

            return zones;
        }

        /// <summary>"+0300", "-0530" or "+030000".</summary>
        static bool TryParseUtcOffset(string text, out TimeSpan offset)
        {
            offset = TimeSpan.Zero;
            if (string.IsNullOrEmpty(text) || text.Length < 5) return false;

            int sign = text[0] == '-' ? -1 : 1;
            int hours, minutes, seconds = 0;
            if (!int.TryParse(text.Substring(1, 2), out hours)) return false;
            if (!int.TryParse(text.Substring(3, 2), out minutes)) return false;
            if (text.Length >= 7) int.TryParse(text.Substring(5, 2), out seconds);

            offset = new TimeSpan(sign * hours, sign * minutes, sign * seconds);
            return true;
        }

        // ------------------------------------------------------------------------------------
        // Recurrence and alarms
        // ------------------------------------------------------------------------------------
        static EventRecurrence ParseRecurrence(string value, Dictionary<string, TimeSpan> zones)
        {
            var rule = new EventRecurrence();

            foreach (var part in value.Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;

                string key = part.Substring(0, eq).Trim().ToUpperInvariant();
                string val = part.Substring(eq + 1).Trim();

                switch (key)
                {
                    case "FREQ":
                        rule.Frequency = val.ToUpperInvariant();
                        break;
                    case "INTERVAL":
                    {
                        int interval;
                        if (int.TryParse(val, out interval) && interval > 0) rule.Interval = interval;
                        break;
                    }
                    case "COUNT":
                    {
                        int count;
                        if (int.TryParse(val, out count)) rule.Count = count;
                        break;
                    }
                    case "UNTIL":
                    {
                        bool allDay, approximate;
                        var until = ParseDateTime(val, string.Empty, string.Empty, zones,
                                                  out allDay, out approximate);
                        if (until != DateTimeOffset.MinValue) rule.Until = until;
                        break;
                    }
                    case "BYDAY":
                        foreach (var day in val.Split(','))
                            if (day.Trim().Length > 0) rule.ByDay.Add(day.Trim().ToUpperInvariant());
                        break;
                    case "BYMONTHDAY":
                    {
                        int monthDay;
                        if (int.TryParse(val.Split(',')[0], out monthDay)) rule.ByMonthDay = monthDay;
                        break;
                    }
                    case "BYMONTH":
                    {
                        int month;
                        if (int.TryParse(val.Split(',')[0], out month)) rule.ByMonth = month;
                        break;
                    }
                    case "BYSETPOS":
                    case "BYWEEKNO":
                    case "BYYEARDAY":
                        // The phone's recurrence model cannot express these. Saying so is what
                        // keeps AppointmentMapping from writing a rule that repeats on the wrong
                        // days - a wrong repeat is worse than a single occurrence.
                        rule.IsUnsupported = true;
                        break;
                }
            }

            if (rule.Frequency.Length == 0) rule.IsUnsupported = true;
            return rule;
        }

        /// <summary>
        /// A VALARM TRIGGER as minutes before the start. Only the relative form is used: an
        /// absolute trigger (VALUE=DATE-TIME) is a fixed moment, which the phone's reminder - an
        /// offset from the event - cannot express.
        /// </summary>
        static int? ParseTrigger(string value)
        {
            string text = (value ?? string.Empty).Trim().ToUpperInvariant();
            if (text.Length < 2 || text.IndexOf('P') < 0) return null;

            bool before = text.StartsWith("-", StringComparison.Ordinal);
            var span = ParseDuration(text.TrimStart('-', '+'));
            if (span == TimeSpan.Zero) return 0;
            if (!before) return null;   // a reminder after the event is not a reminder

            return (int)Math.Round(span.TotalMinutes);
        }

        /// <summary>An ISO 8601 duration: P15DT5H0M20S, PT30M, -PT10M.</summary>
        static TimeSpan ParseDuration(string value)
        {
            string text = (value ?? string.Empty).Trim().ToUpperInvariant().TrimStart('-', '+');
            if (!text.StartsWith("P", StringComparison.Ordinal)) return TimeSpan.Zero;

            int days = 0, hours = 0, minutes = 0, seconds = 0, weeks = 0;
            bool inTime = false;
            var number = string.Empty;

            for (int i = 1; i < text.Length; i++)
            {
                char c = text[i];
                if (c == 'T') { inTime = true; number = string.Empty; continue; }
                if (char.IsDigit(c)) { number += c; continue; }

                int amount;
                if (!int.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out amount))
                    amount = 0;
                number = string.Empty;

                switch (c)
                {
                    case 'W': weeks = amount; break;
                    case 'D': days = amount; break;
                    case 'H': hours = amount; break;
                    case 'M': if (inTime) minutes = amount; break;
                    case 'S': seconds = amount; break;
                }
            }

            return new TimeSpan(weeks * 7 + days, hours, minutes, seconds);
        }
    }
}
