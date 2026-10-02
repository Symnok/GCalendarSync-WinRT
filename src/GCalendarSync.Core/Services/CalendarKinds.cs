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
// Which of the four switches in Settings a calendar answers to.
//
// The switches are Personal, Holidays and Other, and the only information available to decide
// between them is what the calendar-home-set listing gives: a href and a display name.
// Observed against a live account, the listing looks like this:
//
//   /calendar/dav/someone%40gmail.com/events/                      displayname "someone@gmail.com"
//   /calendar/dav/<opaque>%40virtual/events/                       displayname "Holidays in United States"
//   /calendar/dav/someone%40gmail.com/inbox/   and  .../outbox/    scheduling, not calendars
//   /calendar/dav/someone%40gmail.com/user                         the principal, not a calendar
//
// Two consequences worth knowing before reading the code:
//
//   * A subscribed calendar lives under a DIFFERENT principal - "<opaque>@virtual" - not under the
//     account. Anything that builds "<email>/events/" by hand finds only the personal calendar and
//     silently misses every other one.
//
//   * There is a fourth kind, Birthdays, with no switch: it is never synced. Google's Birthdays
//     is not a calendar anyone keeps - it is a view of the contacts, generated from their
//     birthday fields - and Google does not expose it over CalDAV anyway. It is still classified,
//     and that is the point: without a kind of its own it would fall into Other and be dragged in
//     by the Other switch.
using System;
using System.Collections.Generic;
using GCalendarSync.Core.Models;

namespace GCalendarSync.Core.Services
{
    public enum CalendarKind
    {
        /// <summary>The account's own calendar.</summary>
        Personal,
        Birthdays,
        Holidays,
        /// <summary>Anything else: a second calendar, or one shared by someone else.</summary>
        Other
    }

    /// <summary>One calendar from the home-set listing.</summary>
    public sealed class CalendarInfo
    {
        /// <summary>Path on the server, exactly as the server wrote it - already percent-encoded,
        /// and never to be encoded again.</summary>
        public string Href = string.Empty;
        public string DisplayName = string.Empty;
        public CalendarKind Kind;

        /// <summary>CS:getctag, when the server offers one: changes without listing the contents.</summary>
        public string CTag = string.Empty;

        public override string ToString()
        {
            return Kind + ": " + DisplayName;
        }
    }

    public static class CalendarKinds
    {
        /// <summary>
        /// Which switch a calendar belongs to.
        /// </summary>
        /// <param name="accountEmail">
        /// Used to recognise the account's own calendar. Google names the personal calendar after
        /// the address, and puts it under the address's own principal.
        /// </param>
        public static CalendarKind Classify(string href, string displayName, string accountEmail)
        {
            string path = Decode(href ?? string.Empty);
            string name = (displayName ?? string.Empty).Trim();
            string lower = name.ToLowerInvariant();

            // Birthdays first: Google's birthday calendar, if it is ever exposed, is named after
            // the contacts group rather than after the account, so the account test below would
            // not claim it - but the holiday test might.
            if (path.IndexOf("#contacts@", StringComparison.OrdinalIgnoreCase) >= 0 ||
                path.IndexOf("contacts@group.v.calendar.google.com", StringComparison.OrdinalIgnoreCase) >= 0 ||
                IsBirthdayName(lower))
                return CalendarKind.Birthdays;

            // A holiday calendar is a subscription: a different principal, ending "@virtual", and
            // named for the country. The name test is there because "@virtual" is Google's own
            // arrangement rather than anything CalDAV promises.
            if (path.IndexOf("@virtual", StringComparison.OrdinalIgnoreCase) >= 0 ||
                path.IndexOf("holiday@group.v.calendar.google.com", StringComparison.OrdinalIgnoreCase) >= 0 ||
                lower.StartsWith("holidays", StringComparison.Ordinal) ||
                lower.IndexOf("holidays in", StringComparison.Ordinal) >= 0)
                return CalendarKind.Holidays;

            if (!string.IsNullOrEmpty(accountEmail))
            {
                string account = accountEmail.Trim().ToLowerInvariant();

                // Both tests, because the two have been seen to disagree: the href is reliable,
                // and the display name is what a user renaming their calendar changes.
                if (path.ToLowerInvariant().IndexOf("/" + account + "/", StringComparison.Ordinal) >= 0)
                    return CalendarKind.Personal;
                if (lower == account)
                    return CalendarKind.Personal;
            }

            return CalendarKind.Other;
        }

        static bool IsBirthdayName(string lower)
        {
            foreach (var word in BirthdayWords)
                if (lower.IndexOf(word, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        /// <summary>
        /// Words that mark a birthday calendar, in the languages this app is likely to meet.
        /// Used only to NAME a calendar, never to judge an individual event: guessing that one
        /// event among thousands is a birthday, and hiding it, is not a guess worth making.
        /// </summary>
        public static readonly string[] BirthdayWords =
        {
            "birthday", "geburtstag", "anniversaire", "cumplea",
            "день народження",   // ukr
            "день рождения",               // rus
            "urodziny"
        };

        /// <summary>
        /// The calendars the current settings say to sync, out of everything the server listed.
        /// </summary>
        public static List<CalendarInfo> Selected(
            IEnumerable<CalendarInfo> all,
            bool personal, bool birthdays, bool holidays, bool other)
        {
            var chosen = new List<CalendarInfo>();
            foreach (var calendar in all)
            {
                bool wanted;
                switch (calendar.Kind)
                {
                    case CalendarKind.Personal: wanted = personal; break;
                    case CalendarKind.Holidays: wanted = holidays; break;

                    // Its own switch, off by default, and off means the calendar is never even
                    // opened. Having a kind of its own is what makes that possible: without one
                    // it would fall into Other and be dragged in by the Other switch.
                    case CalendarKind.Birthdays: wanted = birthdays; break;

                    default: wanted = other; break;
                }
                if (wanted) chosen.Add(calendar);
            }
            return chosen;
        }

        /// <summary>
        /// A percent-decoded path, for matching only. The href itself is never rewritten: it goes
        /// back to the server exactly as it arrived, because re-encoding an encoded href turns
        /// "%40" into "%2540" and the request 404s. That mistake cost the Symbian port of
        /// GContactsSync a day of debugging.
        /// </summary>
        static string Decode(string href)
        {
            try { return Uri.UnescapeDataString(href); }
            catch (Exception) { return href; }
        }
    }
}
