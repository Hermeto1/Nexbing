using Avalonia.Threading;
using Ryujinx.Ava.Systems.AppLibrary;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Ryujinx.Ava.Common
{
    /// <summary>
    /// [Nextendo] Pushes the local play history (every game ever played, with its play time and last
    /// played date) to the account. Used to be done ONLY when the Settings > Nextendo Network tab was
    /// opened, so a session's time never reached the account unless the user happened to visit that tab.
    /// Now it is also pushed on every way a game can end — the Stop action, closing the window (Alt+F4),
    /// and a caught crash — mirroring how the cloud-save sync is wired into those same paths.
    /// </summary>
    public static class NextendoHistorySync
    {
        /// <summary>
        /// Every game ever played, with the freshest play time available. The play time of the session
        /// that just ended is written to the title's metadata file by <c>AppHost.Dispose</c> right before
        /// the close hooks run, so we fold that file in (the in-memory <see cref="ApplicationData"/> may
        /// not have been refreshed yet). Reads are done only for titles that already have a metadata file,
        /// so a never-played title never gets a default file created for it.
        /// </summary>
        public static List<NextendoApi.HistoryItem> CollectLocalHistory()
        {
            List<NextendoApi.HistoryItem> list = [];

            var apps = RyujinxApp.MainWindow?.ViewModel?.Applications;
            if (apps == null)
            {
                return list;
            }

            foreach (ApplicationData app in apps.ToArray())
            {
                TimeSpan time = app.TimePlayed;
                DateTime? last = app.LastPlayed;

                if (MetadataExists(app.IdString))
                {
                    try
                    {
                        Gommon.Optional<ApplicationMetadata> meta = ApplicationLibrary.LoadAndSaveMetaData(app.IdString);
                        if (meta.HasValue)
                        {
                            if (meta.Value.TimePlayed > time)
                            {
                                time = meta.Value.TimePlayed;
                            }

                            if (meta.Value.LastPlayed.HasValue && (last == null || meta.Value.LastPlayed > last))
                            {
                                last = meta.Value.LastPlayed;
                            }
                        }
                    }
                    catch
                    {
                        // Fall back to the in-memory values.
                    }
                }

                if (last == null && time.TotalSeconds < 1)
                {
                    continue; // never played
                }

                list.Add(new NextendoApi.HistoryItem
                {
                    TitleId = app.IdString,
                    Name = app.Name,
                    IconBase64 = app.Icon is { Length: > 0 } ? Convert.ToBase64String(app.Icon) : "",
                    Seconds = (long)time.TotalSeconds,
                    LastPlayed = last?.ToUniversalTime().ToString("o") ?? "",
                });
            }

            return list;
        }

        /// <summary>
        /// Push the history to the account. No-op (with a log line) when the account is not linked or
        /// nothing has been played. Never throws — a failed push must never take down a game-close path.
        /// The collection is read on the UI thread (close hooks can fire from the emulation thread).
        /// </summary>
        public static async Task PushAsync(string reason)
        {
            if (!NextendoAccount.IsLinked)
            {
                Logger.Info?.Print(LogClass.Application, $"[Nextendo] history push skipped ({reason}): account not linked");
                return;
            }

            try
            {
                List<NextendoApi.HistoryItem> local = Dispatcher.UIThread.CheckAccess()
                    ? CollectLocalHistory()
                    : await Dispatcher.UIThread.InvokeAsync(CollectLocalHistory);

                if (local.Count == 0)
                {
                    Logger.Info?.Print(LogClass.Application, $"[Nextendo] history push skipped ({reason}): nothing played");
                    return;
                }

                await NextendoApi.SyncHistoryAsync(local);
                Logger.Info?.Print(LogClass.Application, $"[Nextendo] history pushed ({reason}): {local.Count} title(s)");
            }
            catch (Exception ex)
            {
                Logger.Warning?.Print(LogClass.Application, $"[Nextendo] history push failed ({reason}): {ex.Message}");
            }
        }

        private static bool MetadataExists(string titleId)
        {
            if (string.IsNullOrEmpty(titleId))
            {
                return false;
            }

            try
            {
                return File.Exists(Path.Combine(AppDataManager.GamesDirPath, titleId, "gui", "metadata.json"));
            }
            catch
            {
                return false;
            }
        }
    }
}
