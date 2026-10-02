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
// The Gmail address and App Password, in the Windows Credential Locker rather than in
// LocalSettings: the locker is encrypted by the OS and tied to the device, and the background
// task can read it without the app running.
using System;
using Windows.Security.Credentials;

namespace GCalendarSync.Core.Storage
{
    public sealed class StoredCredentials
    {
        public string Email    = string.Empty;
        public string Password = string.Empty;
    }

    public static class CredentialStore
    {
        // Not renamed with everything else: this is the key the saved account is filed under, and
        // an upgrade that changed it would silently lose the account the old build stored.
        const string ResourceName = "GCalendarSync";

        public static void Save(string email, string appPassword)
        {
            Delete();
            var vault = new PasswordVault();
            vault.Add(new PasswordCredential(ResourceName, email, appPassword));
        }

        /// <summary>The saved account, or null when nothing has been saved.</summary>
        public static StoredCredentials Load()
        {
            try
            {
                var vault = new PasswordVault();
                var found = vault.FindAllByResource(ResourceName);
                if (found == null || found.Count == 0) return null;

                var credential = found[0];
                credential.RetrievePassword();   // the password is not populated until this runs
                return new StoredCredentials
                {
                    Email = credential.UserName,
                    Password = credential.Password
                };
            }
            catch (Exception)
            {
                // FindAllByResource throws rather than returning empty when the resource has
                // never been used.
                return null;
            }
        }

        public static bool HasCredentials()
        {
            return Load() != null;
        }

        public static void Delete()
        {
            try
            {
                var vault = new PasswordVault();
                var found = vault.FindAllByResource(ResourceName);
                if (found == null) return;
                foreach (var credential in found) vault.Remove(credential);
            }
            catch (Exception)
            {
                // Nothing saved; nothing to remove.
            }
        }
    }
}
