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
// The line-level details of iCalendar (RFC 5545) text.
//
// The content line grammar is the same one vCard uses - RFC 5545 and RFC 6350 share it - so
// folding, escaping and the name;PARAM=value:value shape are identical to the vCard half of
// GContactsSync, and this file is deliberately its twin. What differs is above this level:
// an iCalendar body is a tree of components (VCALENDAR contains VEVENT contains VALARM), where
// a vCard is a flat list. That tree is ICalParser's problem; this file only sees lines.
using System;
using System.Collections.Generic;
using System.Text;

namespace GCalendarSync.Core.Services
{
    internal static class ICalText
    {
        /// <summary>
        /// Splits a body into logical lines, undoing RFC 5545 folding: a CRLF followed by a space
        /// or a tab continues the previous line. Google folds at 75 octets, so a description of
        /// any length arrives folded and nothing works without this.
        /// </summary>
        public static List<string> Unfold(string text)
        {
            var lines = new List<string>();
            if (string.IsNullOrEmpty(text)) return lines;

            var current = new StringBuilder();
            bool have = false;

            foreach (var raw in text.Split('\n'))
            {
                var line = raw.EndsWith("\r", StringComparison.Ordinal)
                    ? raw.Substring(0, raw.Length - 1)
                    : raw;

                if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
                {
                    // The folding whitespace is not part of the value.
                    if (have) current.Append(line.Substring(1));
                    continue;
                }

                if (have) lines.Add(current.ToString());
                current.Length = 0;
                current.Append(line);
                have = true;
            }
            if (have) lines.Add(current.ToString());

            for (int i = lines.Count - 1; i >= 0; i--)
                if (lines[i].Trim().Length == 0) lines.RemoveAt(i);

            return lines;
        }

        /// <summary>
        /// The property name of a logical line, upper-cased and without parameters.
        /// "DTSTART;TZID=Europe/Kyiv:20261005T090000" gives "DTSTART".
        /// </summary>
        public static string PropertyName(string line)
        {
            int colon = IndexOfValueColon(line);
            if (colon <= 0) return string.Empty;

            string head = line.Substring(0, colon);
            int semi = head.IndexOf(';');
            if (semi >= 0) head = head.Substring(0, semi);

            return head.Trim().ToUpperInvariant();
        }

        /// <summary>
        /// The whole head of a line - name and parameters - with its case intact.
        ///
        /// Deliberately NOT upper-cased, though the vCard twin of this method is. A parameter NAME
        /// is case-insensitive but a parameter VALUE is not, and the one that matters here is TZID:
        /// upper-casing the head turns "Europe/Kyiv" into "EUROPE/KYIV", which is not the name of
        /// any time zone. Param() does the case-insensitive matching instead.
        /// </summary>
        public static string Head(string line)
        {
            int colon = IndexOfValueColon(line);
            return colon > 0 ? line.Substring(0, colon) : line;
        }

        /// <summary>Everything after the value colon, untouched.</summary>
        public static string RawValue(string line)
        {
            int colon = IndexOfValueColon(line);
            return colon < 0 ? string.Empty : line.Substring(colon + 1);
        }

        /// <summary>
        /// Where the value starts.
        ///
        /// Not simply IndexOf(':'), because a parameter value may be quoted and a quoted parameter
        /// value may contain a colon - which is not theoretical here: Google writes
        /// ATTENDEE;MEMBER="mailto:list@example.com":mailto:someone@example.com, and splitting on
        /// the first colon tears that line in half.
        /// </summary>
        static int IndexOfValueColon(string line)
        {
            bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"') quoted = !quoted;
                else if (c == ':' && !quoted) return i;
            }
            return -1;
        }

        /// <summary>
        /// The value of one parameter, or an empty string. The name is matched without regard to
        /// case, as RFC 5545 requires; the value is returned exactly as written, because TZID
        /// values are case-sensitive zone names. Surrounding quotes are removed - the RFC allows
        /// them, and Google uses them for names containing a slash.
        /// </summary>
        public static string Param(string head, string name)
        {
            foreach (var segment in SplitParams(head))
            {
                int eq = segment.IndexOf('=');
                if (eq < 0) continue;
                if (eq == name.Length &&
                    string.Compare(segment, 0, name, 0, eq, StringComparison.OrdinalIgnoreCase) == 0)
                    return segment.Substring(eq + 1).Trim().Trim('"');
            }
            return string.Empty;
        }

        /// <summary>The parameter segments of a head, without the property name.</summary>
        static List<string> SplitParams(string head)
        {
            var parts = new List<string>();
            var current = new StringBuilder();
            bool quoted = false;

            foreach (char c in head)
            {
                if (c == '"') { quoted = !quoted; current.Append(c); continue; }
                if (c == ';' && !quoted) { parts.Add(current.ToString().Trim()); current.Length = 0; continue; }
                current.Append(c);
            }
            parts.Add(current.ToString().Trim());

            if (parts.Count > 0) parts.RemoveAt(0);   // the property name
            return parts;
        }

        /// <summary>
        /// Splits a structured value on unescaped semicolons. "\;" is a literal semicolon and must
        /// not split - EXDATE and RRULE both carry values where this matters.
        /// </summary>
        public static string[] SplitComponents(string value)
        {
            var parts = new List<string>();
            var current = new StringBuilder();

            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] == '\\' && i + 1 < value.Length)
                {
                    current.Append(value[i]).Append(value[i + 1]);
                    i++;
                    continue;
                }
                if (value[i] == ';') { parts.Add(current.ToString()); current.Length = 0; continue; }
                current.Append(value[i]);
            }
            parts.Add(current.ToString());
            return parts.ToArray();
        }

        /// <summary>Undoes iCalendar value escaping: \n \N \, \; \\ .</summary>
        public static string Unescape(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? string.Empty;

            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != '\\' || i + 1 >= s.Length) { sb.Append(s[i]); continue; }

                char next = s[++i];
                switch (next)
                {
                    case 'n':
                    case 'N': sb.Append('\n'); break;
                    case ',': sb.Append(','); break;
                    case ';': sb.Append(';'); break;
                    case '\\': sb.Append('\\'); break;
                    default: sb.Append(next); break;   // unknown escape: keep the character
                }
            }
            return sb.ToString();
        }
    }
}
