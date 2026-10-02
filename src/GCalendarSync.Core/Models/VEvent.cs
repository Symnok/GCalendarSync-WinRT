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
// One VEVENT, in the shape the phone needs it rather than the shape iCalendar states it.
//
// Three decisions are baked into this type, all of them about time:
//
//   * Start and End are DateTimeOffset, because that is what a WP8.1 Appointment takes. The
//     conversion from iCalendar's three date forms happens in the parser, once, rather than
//     being rediscovered at every use.
//   * IsAllDay is kept separately rather than inferred from the time being midnight. A meeting
//     genuinely at 00:00 is not an all-day event, and the phone renders the two very differently.
//   * TimeZoneId is recorded even though the parser has already resolved the offset, because
//     when it could NOT resolve one, that is the only evidence left of what was meant.
using System;
using System.Collections.Generic;

namespace GCalendarSync.Core.Models
{
    /// <summary>How an event repeats, in the subset WP8.1's AppointmentRecurrence can express.</summary>
    public sealed class EventRecurrence
    {
        public string Frequency = string.Empty;   // DAILY / WEEKLY / MONTHLY / YEARLY
        public int Interval = 1;
        public int Count;                         // 0 when not limited by a count
        public DateTimeOffset? Until;
        public List<string> ByDay = new List<string>();     // MO, TU, ... and 2MO, -1FR
        public int ByMonthDay;
        public int ByMonth;

        /// <summary>
        /// True when this is a shape the phone cannot hold, so the event is written as a single
        /// occurrence rather than silently repeating on the wrong days.
        /// </summary>
        public bool IsUnsupported;
    }

    public sealed class VEvent
    {
        /// <summary>The iCalendar UID. Google's is stable across edits, and it is what the phone
        /// contact for this event is matched by on every later sync.</summary>
        public string Uid = string.Empty;

        /// <summary>Path on the server, and the ETag that was current when this was fetched.</summary>
        public string Href = string.Empty;
        public string Etag = string.Empty;

        public string Subject = string.Empty;
        public string Details = string.Empty;
        public string Location = string.Empty;
        public string Url = string.Empty;

        public DateTimeOffset Start;
        public DateTimeOffset End;
        public bool IsAllDay;

        /// <summary>The TZID the event named, empty for UTC and for floating times.</summary>
        public string TimeZoneId = string.Empty;

        /// <summary>True when the start was a floating or TZID time that had to be read as the
        /// phone's own local time. The only kind of event that can land on the wrong hour.</summary>
        public bool TimeIsApproximate;

        public EventRecurrence Recurrence;

        /// <summary>Minutes before the start, or null when the event carries no alarm.</summary>
        public int? ReminderMinutes;

        /// <summary>STATUS:CANCELLED. Downloaded so the phone copy can be removed.</summary>
        public bool IsCancelled;

        /// <summary>TRANSP:TRANSPARENT - shown as free rather than busy.</summary>
        public bool IsFree;

        /// <summary>CLASS:PRIVATE or CONFIDENTIAL.</summary>
        public bool IsPrivate;

        /// <summary>CATEGORIES, upper-cased, used by the birthday test.</summary>
        public List<string> Categories = new List<string>();

        /// <summary>
        /// Set on an override: RECURRENCE-ID names the one occurrence of a repeating event that
        /// this VEVENT replaces. Google sends the master and its overrides in one resource.
        /// </summary>
        public DateTimeOffset? RecurrenceId;

        public bool IsOverride { get { return RecurrenceId.HasValue; } }

        /// <summary>Everything needed to tell whether the phone copy is still current.</summary>
        public override string ToString()
        {
            return Uid + " " + Start.ToString("u") + " " + Subject;
        }
    }
}
