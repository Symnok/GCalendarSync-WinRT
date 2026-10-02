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
// The format of the account file, so a 16-character App Password does not have to be typed on a
// phone keyboard every time something is tested.
//
// Two lines, nothing else:
//
//     login: someone@gmail.com
//     password: abcdefghijklmnop
//
// Parsing only, with no reference to WinRT, so tests\ can exercise it on the desktop.
// CredentialsFileReader is the half that opens files.
//
// There is deliberately no "write" anywhere: putting an App Password into Documents, where every
// app holding the documentsLibrary capability can read it, would be a worse deal than typing it.
// The app's own copy lives in the Credential Locker - see CredentialStore.
using System;

namespace GCalendarSync.Core.Storage
{
    public sealed class CredentialsFileResult
    {
        public string Email    = string.Empty;
        public string Password = string.Empty;

        /// <summary>Why the file was not usable, ready to show to the user. Null when it was.</summary>
        public string Problem;

        /// <summary>Where this came from, for telling the user which file was used.</summary>
        public string Source = string.Empty;

        /// <summary>True when the file yielded both halves of an account.</summary>
        public bool IsComplete
        {
            get { return Email.Length > 0 && Password.Length > 0; }
        }
    }

    public static class CredentialsFile
    {
        /// <summary>
        /// What the app looks for in Documents on startup, before falling back to asking the user
        /// to pick a file. The extension matters: it has to be one Package.appxmanifest associates
        /// the app with, or the documentsLibrary capability grants nothing.
        /// </summary>
        public const string DefaultFileName = "caldav.cfg";

        /// <summary>
        /// Reads the two lines. Deliberately forgiving about what is easy to get wrong when typing a
        /// file for a phone: ':' or '=' as the separator, any spacing around it, any case for the
        /// key, blank lines, '#' or ';' comments, and CRLF or LF.
        /// </summary>
        public static CredentialsFileResult Parse(string text)
        {
            var result = new CredentialsFileResult();

            if (string.IsNullOrEmpty(text))
            {
                result.Problem = "the file is empty";
                return result;
            }

            foreach (var raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;

                // The first separator only. An email address contains neither ':' nor '=', but
                // splitting on the last would break the moment a value did.
                int colon = line.IndexOf(':');
                int equals = line.IndexOf('=');
                int split = colon < 0 ? equals
                          : equals < 0 ? colon
                          : Math.Min(colon, equals);
                if (split <= 0) continue;

                string key = line.Substring(0, split).Trim().ToLowerInvariant();
                string value = line.Substring(split + 1).Trim();
                if (value.Length == 0) continue;

                switch (key)
                {
                    case "login":
                    case "email":
                    case "user":
                    case "username":
                    case "account":
                        result.Email = value;
                        break;

                    case "password":
                    case "pass":
                    case "apppassword":
                    case "app password":
                        // Google shows an App Password as four groups of four. Copying it in that
                        // shape is the obvious thing to do, and is not what Google accepts.
                        result.Password = value.Replace(" ", "").Replace("\t", "");
                        break;
                }
            }

            result.Problem = Describe(result);
            return result;
        }

        /// <summary>
        /// What is wrong with what was read, or null. The two length/shape checks are worth making
        /// here rather than letting them come back as a 401 from Google two taps later - a password
        /// that is not 16 characters is almost always the account password by mistake.
        /// </summary>
        static string Describe(CredentialsFileResult result)
        {
            if (result.IsComplete)
            {
                if (result.Email.IndexOf('@') < 0)
                    return "the login line does not look like an email address";
                if (result.Password.Length != 16)
                    return "the password is " + result.Password.Length +
                           " characters; a Google App Password is 16";
                return null;
            }

            if (result.Email.Length == 0 && result.Password.Length == 0)
                return "no \"login:\" or \"password:\" line found";
            if (result.Email.Length == 0) return "no \"login:\" line found";
            return "no \"password:\" line found";
        }
    }
}
