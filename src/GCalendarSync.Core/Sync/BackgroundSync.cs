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
// Registration of the periodic Google-to-phone download.
//
// Only the download runs on a timer. Uploading is manual by design - the same decision the
// Windows 10 Mobile version makes - because a background run cannot ask the user anything, and a
// phone-side edit that turns out to be unwanted should not reach Google unattended.
//
// Windows Phone will not accept a TimeTrigger under fifteen minutes, and schedules it within
// another fifteen of the interval elapsing, so the setting is a floor and not a promise.
using System;
using System.Threading.Tasks;
using Windows.ApplicationModel.Background;
using GCalendarSync.Core.Storage;

namespace GCalendarSync.Core.Sync
{
    public static class BackgroundSync
    {
        public const string TaskName = "GCalendarSyncDownloadTask";

        /// <summary>
        /// The runtime class the background host activates. Must match the EntryPoint in
        /// Package.appxmanifest and the class in the BackgroundTasks component exactly - the
        /// failure is silent otherwise, and shows only as a task that never runs.
        /// </summary>
        public const string TaskEntryPoint = "GCalendarSync.BackgroundTasks.PeriodicSyncTask";

        const string RegisteredIntervalKey = "RegisteredIntervalMinutes";

        public static bool IsRegistered
        {
            get { return Find() != null; }
        }

        static IBackgroundTaskRegistration Find()
        {
            foreach (var entry in BackgroundTaskRegistration.AllTasks)
                if (entry.Value.Name == TaskName) return entry.Value;
            return null;
        }

        /// <summary>
        /// Makes the registration match the current settings. Returns null on success, or a
        /// sentence explaining why the task cannot run.
        /// </summary>
        public static async Task<string> ApplyAsync()
        {
            if (!AppSettings.AutoSyncEnabled)
            {
                Unregister();
                return null;
            }

            uint minutes = AppSettings.AutoSyncIntervalMinutes;

            var existing = Find();
            if (existing != null)
            {
                // A trigger's interval cannot be read back off a registration, so the interval
                // that was registered is recorded alongside it. Same interval, nothing to do -
                // re-registering would reset the timer on every visit to the settings page.
                object registered = Windows.Storage.ApplicationData.Current
                    .LocalSettings.Values[RegisteredIntervalKey];
                if (registered is uint && (uint)registered == minutes) return null;

                existing.Unregister(true);
            }

            var access = await BackgroundExecutionManager.RequestAccessAsync()
                .AsTask().ConfigureAwait(false);

            if (access == BackgroundAccessStatus.Denied ||
                access == BackgroundAccessStatus.Unspecified)
                return "Windows Phone is not allowing this app to run in the background. " +
                       "Automatic sync is off; use the sync button instead.";

            try
            {
                var builder = new BackgroundTaskBuilder
                {
                    Name = TaskName,
                    TaskEntryPoint = TaskEntryPoint,
                    IsNetworkRequested = true
                };
                builder.SetTrigger(new TimeTrigger(minutes, false));
                builder.AddCondition(new SystemCondition(SystemConditionType.InternetAvailable));
                builder.Register();

                Windows.Storage.ApplicationData.Current
                    .LocalSettings.Values[RegisteredIntervalKey] = minutes;
                return null;
            }
            catch (Exception ex)
            {
                return "Automatic sync could not be scheduled: " + ex.Message;
            }
        }

        public static void Unregister()
        {
            var existing = Find();
            if (existing != null) existing.Unregister(true);
            Windows.Storage.ApplicationData.Current
                .LocalSettings.Values.Remove(RegisteredIntervalKey);
        }
    }
}
