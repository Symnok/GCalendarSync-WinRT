// GCalendarSync - tests for the platform-neutral half of the sync engine.
// Copyright (C) 2026 - GPL-3.0-or-later, see LICENSE.
//
// Compiled from the REAL Core sources by run-tests.bat, so these cannot drift from what ships.
// What needs a phone is listed in docs\TESTING.md.
using System;
using System.Collections.Generic;
using GCalendarSync.Core.Models;
using GCalendarSync.Core.Services;

static class ICalTests
{
    static int _failed;

    static int Main()
    {
        FoldingIsUndone();
        QuotedParameterWithAColonSurvives();
        EscapingIsUndone();
        AllDayEventIsRecognised();
        UtcTimeIsExact();
        FloatingTimeIsFlaggedApproximate();
        TimeZoneIsResolvedFromVTimezone();
        AlarmDescriptionDoesNotBecomeTheEventDescription();
        ReminderIsReadFromTheAlarm();
        OverridesAreSeparateEvents();
        DurationBecomesAnEnd();
        MissingEndGetsOne();
        RecurrenceIsParsed();
        UnsupportedRecurrenceIsRefused();
        CancelledAndTranspAreRead();
        CalendarsAreClassified();
        SelectionFollowsTheSwitches();
        RecurrenceMapsToThePhoneModel();
        UnmappableRulesBecomeSingleOccurrences();
        ScalarsAreMapped();

        Console.WriteLine();
        Console.WriteLine(_failed == 0 ? "ALL TESTS PASSED" : _failed + " CHECK(S) FAILED");
        return _failed == 0 ? 0 : 1;
    }

    // ----------------------------------------------------------------------------------------
    // text layer
    // ----------------------------------------------------------------------------------------
    static void FoldingIsUndone()
    {
        var e = One(Event("UID:a", "SUMMARY:A very long summ\r\n ary folded over two lines",
                          "DTSTART;VALUE=DATE:20261005"));
        Check("folded summary rejoins", "A very long summary folded over two lines", e.Subject);
    }

    static void QuotedParameterWithAColonSurvives()
    {
        // Splitting on the first colon tears this line in half and loses the value. Google writes
        // exactly this shape for a meeting with a mailing list on it.
        string line = "ATTENDEE;MEMBER=\"mailto:list@example.com\":mailto:someone@example.com";
        Check("property name past a quoted colon", "ATTENDEE", ICalText.PropertyName(line));
        Check("value past a quoted colon", "mailto:someone@example.com", ICalText.RawValue(line));
        Check("parameter past a quoted colon", "mailto:list@example.com",
              ICalText.Param(ICalText.Head(line), "MEMBER").ToLowerInvariant());
    }

    static void EscapingIsUndone()
    {
        var e = One(Event("UID:a", "DTSTART;VALUE=DATE:20261005",
                          @"DESCRIPTION:line one\nline two\, with a comma\; and a semicolon"));
        Check("escapes undone", "line one\nline two, with a comma; and a semicolon", e.Details);
    }

    // ----------------------------------------------------------------------------------------
    // time
    // ----------------------------------------------------------------------------------------
    static void AllDayEventIsRecognised()
    {
        var e = One(Event("UID:a", "SUMMARY:Flag Day",
                          "DTSTART;VALUE=DATE:20210614", "DTEND;VALUE=DATE:20210615"));
        Check("all day", true, e.IsAllDay);
        Check("starts at midnight", 0, e.Start.Hour);
        Check("one day long", 1.0, (e.End - e.Start).TotalDays);
        Check("not flagged approximate", false, e.TimeIsApproximate);
    }

    static void UtcTimeIsExact()
    {
        var e = One(Event("UID:a", "DTSTART:20261005T090000Z", "DTEND:20261005T100000Z"));
        Check("utc offset is zero", TimeSpan.Zero, e.Start.Offset);
        Check("utc hour kept", 9, e.Start.Hour);
        Check("exact, not approximate", false, e.TimeIsApproximate);
        Check("one hour long", 1.0, (e.End - e.Start).TotalHours);
    }

    static void FloatingTimeIsFlaggedApproximate()
    {
        // A TZID with no VTIMEZONE to resolve it: readable only as the phone's own local time,
        // and the flag is the only evidence left that it might be an hour out.
        var e = One(Event("UID:a", "DTSTART;TZID=Europe/Kyiv:20261005T090000"));
        Check("hour kept as written", 9, e.Start.Hour);
        Check("zone recorded", "Europe/Kyiv", e.TimeZoneId);
        Check("flagged approximate", true, e.TimeIsApproximate);
    }

    static void TimeZoneIsResolvedFromVTimezone()
    {
        string body =
            "BEGIN:VCALENDAR\r\n" +
            "BEGIN:VTIMEZONE\r\nTZID:Europe/Kyiv\r\n" +
            "BEGIN:STANDARD\r\nTZOFFSETFROM:+0300\r\nTZOFFSETTO:+0200\r\n" +
            "DTSTART:19701025T040000\r\nEND:STANDARD\r\nEND:VTIMEZONE\r\n" +
            "BEGIN:VEVENT\r\nUID:a\r\nDTSTART;TZID=Europe/Kyiv:20261005T090000\r\n" +
            "END:VEVENT\r\nEND:VCALENDAR\r\n";

        var e = ICalParser.ParseEvents(body)[0];
        Check("offset from VTIMEZONE", TimeSpan.FromHours(2), e.Start.Offset);
        Check("resolved, so not approximate", false, e.TimeIsApproximate);
    }

    // ----------------------------------------------------------------------------------------
    // the component tree
    // ----------------------------------------------------------------------------------------
    static void AlarmDescriptionDoesNotBecomeTheEventDescription()
    {
        // The bug this exists to prevent. A VALARM has its own DESCRIPTION, and reading the body
        // as a flat list of lines - which is what the vCard parser does, correctly, for its own
        // format - puts the alarm's text into the event's Details on every event with a reminder.
        var e = One(Event(
            "UID:a", "SUMMARY:Standup", "DESCRIPTION:the real description",
            "DTSTART:20261005T090000Z",
            "BEGIN:VALARM", "ACTION:DISPLAY", "DESCRIPTION:This is an event reminder",
            "TRIGGER:-PT10M", "END:VALARM"));

        Check("event description kept", "the real description", e.Details);
        Check("event summary kept", "Standup", e.Subject);
    }

    static void ReminderIsReadFromTheAlarm()
    {
        var e = One(Event("UID:a", "DTSTART:20261005T090000Z",
                          "BEGIN:VALARM", "TRIGGER:-PT30M", "END:VALARM"));
        Check("reminder minutes", 30, e.ReminderMinutes);

        var hours = One(Event("UID:b", "DTSTART:20261005T090000Z",
                              "BEGIN:VALARM", "TRIGGER:-P1DT2H", "END:VALARM"));
        Check("day-and-hours trigger", 26 * 60, hours.ReminderMinutes);

        // A trigger AFTER the start is not a reminder and must not become one.
        var after = One(Event("UID:c", "DTSTART:20261005T090000Z",
                              "BEGIN:VALARM", "TRIGGER:PT15M", "END:VALARM"));
        Check("trigger after the start is ignored", null, after.ReminderMinutes);
    }

    static void OverridesAreSeparateEvents()
    {
        string body =
            "BEGIN:VCALENDAR\r\n" +
            "BEGIN:VEVENT\r\nUID:series\r\nSUMMARY:Weekly\r\nDTSTART:20261005T090000Z\r\n" +
            "RRULE:FREQ=WEEKLY;BYDAY=MO\r\nEND:VEVENT\r\n" +
            "BEGIN:VEVENT\r\nUID:series\r\nSUMMARY:Weekly (moved)\r\n" +
            "RECURRENCE-ID:20261012T090000Z\r\nDTSTART:20261012T140000Z\r\nEND:VEVENT\r\n" +
            "END:VCALENDAR\r\n";

        var events = ICalParser.ParseEvents(body);
        Check("master and override both parsed", 2, events.Count);
        Check("master is not an override", false, events[0].IsOverride);
        Check("override is one", true, events[1].IsOverride);
        Check("override keeps the uid", "series", events[1].Uid);
        Check("master keeps its rule", "WEEKLY", events[0].Recurrence.Frequency);
    }

    // ----------------------------------------------------------------------------------------
    // duration and recurrence
    // ----------------------------------------------------------------------------------------
    static void DurationBecomesAnEnd()
    {
        var e = One(Event("UID:a", "DTSTART:20261005T090000Z", "DURATION:PT45M"));
        Check("duration applied", 45.0, (e.End - e.Start).TotalMinutes);
    }

    static void MissingEndGetsOne()
    {
        var timed = One(Event("UID:a", "DTSTART:20261005T090000Z"));
        Check("timed event gets an hour", 1.0, (timed.End - timed.Start).TotalHours);

        var allDay = One(Event("UID:b", "DTSTART;VALUE=DATE:20261005"));
        Check("all-day event gets a day", 1.0, (allDay.End - allDay.Start).TotalDays);

        // An end at or before the start renders as a glitch on the phone.
        var backwards = One(Event("UID:c", "DTSTART:20261005T090000Z", "DTEND:20261005T080000Z"));
        Check("backwards event is corrected", true, backwards.End > backwards.Start);
    }

    static void RecurrenceIsParsed()
    {
        var e = One(Event("UID:a", "DTSTART:20261005T090000Z",
                          "RRULE:FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE;COUNT=10"));
        Check("frequency", "WEEKLY", e.Recurrence.Frequency);
        Check("interval", 2, e.Recurrence.Interval);
        Check("count", 10, e.Recurrence.Count);
        Check("two days", 2, e.Recurrence.ByDay.Count);
        Check("supported", false, e.Recurrence.IsUnsupported);

        var until = One(Event("UID:b", "DTSTART:20261005T090000Z",
                              "RRULE:FREQ=DAILY;UNTIL=20261231T000000Z"));
        Check("until read", 2026, until.Recurrence.Until.Value.Year);
    }

    static void UnsupportedRecurrenceIsRefused()
    {
        // BYSETPOS cannot be expressed by the phone. Writing a rule that ignores it would repeat
        // the event on the wrong days, which is worse than not repeating it at all.
        var e = One(Event("UID:a", "DTSTART:20261005T090000Z",
                          "RRULE:FREQ=MONTHLY;BYDAY=MO;BYSETPOS=-1"));
        Check("refused rather than approximated", true, e.Recurrence.IsUnsupported);
    }

    static void CancelledAndTranspAreRead()
    {
        var e = One(Event("UID:a", "DTSTART:20261005T090000Z",
                          "STATUS:CANCELLED", "TRANSP:TRANSPARENT", "CLASS:PRIVATE"));
        Check("cancelled", true, e.IsCancelled);
        Check("free", true, e.IsFree);
        Check("private", true, e.IsPrivate);
    }

    // ----------------------------------------------------------------------------------------
    // the four switches
    // ----------------------------------------------------------------------------------------
    static void CalendarsAreClassified()
    {
        // Exactly the listing a live account returned.
        const string me = "ukrvoipcalls@gmail.com";
        Check("personal by href", CalendarKind.Personal,
            CalendarKinds.Classify("/calendar/dav/ukrvoipcalls%40gmail.com/events/",
                                   "ukrvoipcalls@gmail.com", me));
        Check("holidays by @virtual principal", CalendarKind.Holidays,
            CalendarKinds.Classify("/calendar/dav/cln2stbjc4hm%40virtual/events/",
                                   "Holidays in United States", me));
        Check("holidays by name alone", CalendarKind.Holidays,
            CalendarKinds.Classify("/calendar/dav/something/events/", "Holidays in Ukraine", me));
        Check("birthdays by name", CalendarKind.Birthdays,
            CalendarKinds.Classify("/calendar/dav/x/events/", "Birthdays", me));
        Check("birthdays by google contacts id", CalendarKind.Birthdays,
            CalendarKinds.Classify("/calendar/dav/%23contacts%40group.v.calendar.google.com/events/",
                                   "Contacts", me));
        Check("anything else is other", CalendarKind.Other,
            CalendarKinds.Classify("/calendar/dav/team%40example.com/events/", "Team", me));

        // A renamed personal calendar is still personal: the href decides, not the label.
        Check("renamed personal calendar", CalendarKind.Personal,
            CalendarKinds.Classify("/calendar/dav/ukrvoipcalls%40gmail.com/events/", "My stuff", me));
    }

    static void SelectionFollowsTheSwitches()
    {
        var all = new List<CalendarInfo>
        {
            new CalendarInfo { DisplayName = "me",       Kind = CalendarKind.Personal },
            new CalendarInfo { DisplayName = "bdays",    Kind = CalendarKind.Birthdays },
            new CalendarInfo { DisplayName = "holidays", Kind = CalendarKind.Holidays },
            new CalendarInfo { DisplayName = "team",     Kind = CalendarKind.Other },
        };

        // The defaults: personal on, the other three off.
        var chosen = CalendarKinds.Selected(all, true, false, false, false);
        Check("defaults select one calendar", 1, chosen.Count);
        Check("and it is the personal one", CalendarKind.Personal, chosen[0].Kind);

        Check("all four on", 4, CalendarKinds.Selected(all, true, true, true, true).Count);
        Check("all four off", 0, CalendarKinds.Selected(all, false, false, false, false).Count);

        // The birthday calendar answers to its own switch and to nothing else. The half that
        // matters is the second check: with Birthdays off but Other on it must stay out, which
        // is the whole reason it has a kind of its own rather than falling into Other.
        var birthdaysOnly = CalendarKinds.Selected(all, false, true, false, false);
        Check("birthdays on selects just it", 1, birthdaysOnly.Count);
        Check("and it is the birthdays one", CalendarKind.Birthdays, birthdaysOnly[0].Kind);

        foreach (var calendar in CalendarKinds.Selected(all, true, false, true, true))
            Check("birthdays off is never dragged in by another switch",
                  false, calendar.Kind == CalendarKind.Birthdays);

        // "Other" is one switch for every other calendar, however many there are.
        all.Add(new CalendarInfo { DisplayName = "second team", Kind = CalendarKind.Other });
        Check("other covers them all", 2,
            CalendarKinds.Selected(all, false, false, false, true).Count);
    }

    // ----------------------------------------------------------------------------------------
    // recurrence -> the phone's model
    // ----------------------------------------------------------------------------------------
    static void RecurrenceMapsToThePhoneModel()
    {
        var daily = Map("DTSTART:20261005T090000Z", "RRULE:FREQ=DAILY;INTERVAL=3");
        Check("daily unit", PhoneRecurrenceUnit.Daily, daily.Unit);
        Check("daily interval", 3, daily.Interval);

        var weekly = Map("DTSTART:20261005T090000Z", "RRULE:FREQ=WEEKLY;BYDAY=MO,WE,FR");
        Check("weekly unit", PhoneRecurrenceUnit.Weekly, weekly.Unit);
        Check("three weekdays as flags",
              PhoneDaysOfWeek.Monday | PhoneDaysOfWeek.Wednesday | PhoneDaysOfWeek.Friday,
              weekly.DaysOfWeek);

        // A weekly rule with no BYDAY repeats on whatever day it starts.
        var implied = Map("DTSTART:20261005T090000Z", "RRULE:FREQ=WEEKLY");
        Check("weekday taken from the start", PhoneDaysOfWeek.Monday, implied.DaysOfWeek);

        var monthlyDate = Map("DTSTART:20261014T090000Z", "RRULE:FREQ=MONTHLY;BYMONTHDAY=14");
        Check("monthly on a date", PhoneRecurrenceUnit.Monthly, monthlyDate.Unit);
        Check("the date", 14, monthlyDate.Day);

        var monthlyDay = Map("DTSTART:20261012T090000Z", "RRULE:FREQ=MONTHLY;BYDAY=2MO");
        Check("monthly on a weekday", PhoneRecurrenceUnit.MonthlyOnDay, monthlyDay.Unit);
        Check("which weekday", PhoneDaysOfWeek.Monday, monthlyDay.DaysOfWeek);
        Check("which week", PhoneWeekOfMonth.Second, monthlyDay.WeekOfMonth);

        var last = Map("DTSTART:20261030T090000Z", "RRULE:FREQ=MONTHLY;BYDAY=-1FR");
        Check("negative ordinal is the last week", PhoneWeekOfMonth.Last, last.WeekOfMonth);
        Check("and the right day", PhoneDaysOfWeek.Friday, last.DaysOfWeek);

        var yearly = Map("DTSTART:20261225T090000Z", "RRULE:FREQ=YEARLY");
        Check("yearly unit", PhoneRecurrenceUnit.Yearly, yearly.Unit);
        Check("month from the start", 12, yearly.Month);
        Check("day from the start", 25, yearly.Day);

        // COUNT and UNTIL are different fields on the phone, and only one may be set.
        var counted = Map("DTSTART:20261005T090000Z", "RRULE:FREQ=DAILY;COUNT=5");
        Check("occurrences", (uint)5, counted.Occurrences);
        Check("and no until", false, counted.Until.HasValue);

        var ends = Map("DTSTART:20261005T090000Z", "RRULE:FREQ=DAILY;UNTIL=20261231T000000Z");
        Check("until", 2026, ends.Until.Value.Year);
        Check("and no occurrences", (uint)0, ends.Occurrences);
    }

    static void UnmappableRulesBecomeSingleOccurrences()
    {
        // Each of these would repeat on the WRONG days if the qualifier were dropped.
        Check("BYSETPOS is refused", null,
              Map("DTSTART:20261005T090000Z", "RRULE:FREQ=MONTHLY;BYDAY=MO;BYSETPOS=-1"));
        Check("two weekdays in a month is refused", null,
              Map("DTSTART:20261005T090000Z", "RRULE:FREQ=MONTHLY;BYDAY=1MO,3TH"));
        Check("an unordered monthly weekday is refused", null,
              Map("DTSTART:20261005T090000Z", "RRULE:FREQ=MONTHLY;BYDAY=MO"));
        Check("an unknown frequency is refused", null,
              Map("DTSTART:20261005T090000Z", "RRULE:FREQ=HOURLY"));

        // And an event with no rule at all simply has none.
        Check("no rule, no recurrence", null,
              AppointmentMapping.ToPhoneRecurrence(One(Event("UID:a", "DTSTART:20261005T090000Z"))));
    }

    static void ScalarsAreMapped()
    {
        var free = One(Event("UID:a", "DTSTART:20261005T090000Z", "TRANSP:TRANSPARENT"));
        Check("transparent is free", PhoneBusyStatus.Free, AppointmentMapping.BusyStatusFor(free));

        var busy = One(Event("UID:b", "DTSTART:20261005T090000Z"));
        Check("otherwise busy", PhoneBusyStatus.Busy, AppointmentMapping.BusyStatusFor(busy));

        var secret = One(Event("UID:c", "DTSTART:20261005T090000Z", "CLASS:PRIVATE"));
        Check("private", PhoneSensitivity.Private, AppointmentMapping.SensitivityFor(secret));

        // An untitled event is not unusual, and an empty agenda row is useless.
        var untitled = One(Event("UID:d", "DTSTART:20261005T090000Z", "LOCATION:Room 3"));
        Check("falls back to the location", "Room 3", AppointmentMapping.SubjectFor(untitled));
        var nothing = One(Event("UID:e", "DTSTART:20261005T090000Z"));
        Check("or says so", "(no title)", AppointmentMapping.SubjectFor(nothing));

        // A time that could not be resolved exactly must say so where the user will see it.
        var vague = One(Event("UID:f", "DTSTART;TZID=Europe/Kyiv:20261005T090000",
                              "DESCRIPTION:the agenda"));
        Check("the note is added", true,
              AppointmentMapping.DetailsFor(vague).IndexOf("could not be resolved") >= 0);
        Check("and the description survives", true,
              AppointmentMapping.DetailsFor(vague).StartsWith("the agenda"));

        var exact = One(Event("UID:g", "DTSTART:20261005T090000Z", "DESCRIPTION:the agenda"));
        Check("an exact time gets no note", "the agenda", AppointmentMapping.DetailsFor(exact));

        Check("duration", 1.0, AppointmentMapping.DurationFor(exact).TotalHours);
    }

    static PhoneRecurrence Map(params string[] lines)
    {
        var all = new List<string> { "UID:a" };
        all.AddRange(lines);
        return AppointmentMapping.ToPhoneRecurrence(One(Event(all.ToArray())));
    }

    // ----------------------------------------------------------------------------------------
    // helpers
    // ----------------------------------------------------------------------------------------
    static string Event(params string[] lines)
    {
        return "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VEVENT\r\n"
             + string.Join("\r\n", lines)
             + "\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
    }

    static VEvent One(string body)
    {
        var events = ICalParser.ParseEvents(body);
        if (events.Count == 0) { Fail("parse", "one event", "none"); return new VEvent(); }
        return events[0];
    }

    static VEvent Ev(string subject, bool allDay, string frequency, params string[] categories)
    {
        var e = new VEvent { Subject = subject, IsAllDay = allDay };
        if (frequency != null) e.Recurrence = new EventRecurrence { Frequency = frequency };
        e.Categories.AddRange(categories);
        return e;
    }

    static void Check(string what, object expected, object actual)
    {
        bool ok = expected == null ? actual == null : expected.Equals(actual);
        if (ok) Console.WriteLine("  ok    " + what);
        else Fail(what, expected, actual);
    }

    static void Fail(string what, object expected, object actual)
    {
        _failed++;
        Console.WriteLine("  FAIL  " + what + ": expected <" + (expected ?? "null") +
                          "> but got <" + (actual ?? "null") + ">");
    }
}
