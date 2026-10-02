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
// The periodic Google-to-phone download.
//
// All it does is call the same SyncEngine the app's sync button calls; there is deliberately no
// sync logic here. The result is written to settings so the main page can say when the last
// background run happened and what it did - a background task on a phone has no other way to
// report anything, and there is no notification to fall back on for a contacts sync.
//
// Uploading is never done here. It is manual by design, exactly as on Windows 10 Mobile.
using System;
using System.Threading;
using Windows.ApplicationModel.Background;
using GCalendarSync.Core.Storage;
using GCalendarSync.Core.Sync;

namespace GCalendarSync.BackgroundTasks
{
    public sealed class PeriodicSyncTask : IBackgroundTask
    {
        public void Run(IBackgroundTaskInstance taskInstance)
        {
            var deferral = taskInstance.GetDeferral();
            RunAsync(taskInstance, deferral);
        }

        // Separated from Run so the async void that a background task inevitably needs is not
        // also the interface method - an exception escaping Run would take the host down with it.
        async void RunAsync(IBackgroundTaskInstance taskInstance, BackgroundTaskDeferral deferral)
        {
            var cancellation = new CancellationTokenSource();
            taskInstance.Canceled += (sender, reason) => cancellation.Cancel();

            try
            {
                if (!AppSettings.AutoSyncEnabled) return;

                var credentials = CredentialStore.Load();
                if (credentials == null)
                {
                    AppSettings.LastAutoSyncError = "No saved account.";
                    return;
                }

                // The first sync has to be a manual one: it is the long download, it is the one
                // that can fail on a bad App Password, and the user should be watching when it
                // happens. Same rule as the W10M version.
                var state = await SyncState.LoadAsync();
                if (!state.HasSynced)
                {
                    AppSettings.LastAutoSyncError =
                        "Waiting for the first sync to be run from the app.";
                    return;
                }

                var outcome = await SyncEngine.DownloadAsync(
                    credentials.Email, credentials.Password, null, cancellation.Token);

                if (outcome.Success)
                {
                    AppSettings.LastAutoSync =
                        DateTime.Now.ToString("dd MMM HH:mm") + " - " + outcome.Message;
                    AppSettings.LastAutoSyncError = null;
                }
                else
                {
                    AppSettings.LastAutoSyncError =
                        DateTime.Now.ToString("dd MMM HH:mm") + " - " + outcome.Error;
                }
            }
            catch (Exception ex)
            {
                try { AppSettings.LastAutoSyncError = ex.Message; }
                catch (Exception) { }
            }
            finally
            {
                deferral.Complete();
            }
        }
    }
}
