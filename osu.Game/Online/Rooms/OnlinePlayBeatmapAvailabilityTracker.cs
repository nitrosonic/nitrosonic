// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Logging;
using osu.Framework.Threading;
using osu.Game.Beatmaps;
using osu.Game.Database;
using osu.Game.Online.API.Requests.Responses;
using Realms;

namespace osu.Game.Online.Rooms
{
    /// <summary>
    /// Represent a checksum-verifying beatmap availability tracker usable for online play screens.
    ///
    /// This differs from a regular download tracking composite as this accounts for the
    /// databased beatmap set's checksum, to disallow from playing with an altered version of the beatmap.
    /// </summary>
    public sealed class OnlinePlayBeatmapAvailabilityTracker : CompositeDrawable
    {
        public readonly IBindable<PlaylistItem> SelectedItem = new Bindable<PlaylistItem>();

        // Required to allow child components to update. Can potentially be replaced with a `CompositeComponent` class if or when we make one.
        protected override bool RequiresChildrenUpdate => true;

        [Resolved]
        private RealmAccess realm { get; set; } = null!;

        [Resolved]
        private BeatmapLookupCache beatmapLookupCache { get; set; } = null!;

        /// <summary>
        /// The availability state of the currently selected playlist item.
        /// </summary>
        public IBindable<BeatmapAvailability> Availability => availability;

        private readonly Bindable<BeatmapAvailability> availability = new Bindable<BeatmapAvailability>(BeatmapAvailability.NotDownloaded());

        private ScheduledDelegate progressUpdate;

        private BeatmapDownloadTracker downloadTracker;

        private IDisposable realmSubscription;

        protected override void LoadComplete()
        {
            base.LoadComplete();

            SelectedItem.BindValueChanged(item =>
            {
                // the underlying playlist is regularly cleared for maintenance purposes (things which probably need to be fixed eventually).
                // to avoid exposing a state change when there may actually be none, ignore all nulls for now.
                if (item.NewValue == null)
                    return;

                downloadTracker?.RemoveAndDisposeImmediately();

                beatmapLookupCache.GetBeatmapAsync(item.NewValue.Beatmap.OnlineID).ContinueWith(task => Schedule(() =>
                {
                    var beatmap = task.GetResultSafely();

                    if (SelectedItem.Value?.Beatmap.OnlineID == beatmap.OnlineID)
                        beginTracking(beatmap);
                }), TaskContinuationOptions.OnlyOnRanToCompletion);
            }, true);
        }

        private void beginTracking(APIBeatmap beatmap)
        {
            Debug.Assert(beatmap.BeatmapSet != null);

            downloadTracker = new BeatmapDownloadTracker(beatmap.BeatmapSet);

            AddInternal(downloadTracker);

            downloadTracker.State.BindValueChanged(_ => Scheduler.AddOnce(updateAvailability, beatmap), true);
            downloadTracker.Progress.BindValueChanged(_ =>
            {
                if (downloadTracker.State.Value != DownloadState.Downloading)
                    return;

                // incoming progress changes are going to be at a very high rate.
                // we don't want to flood the network with this, so rate limit how often we send progress updates.
                if (progressUpdate?.Completed != false)
                    progressUpdate = Scheduler.AddDelayed(updateAvailability, beatmap, progressUpdate == null ? 0 : 500);
            }, true);

            // handles changes to hash that didn't occur from the import process (ie. a user editing the beatmap in the editor, somehow).
            realmSubscription?.Dispose();
            realmSubscription = realm.RegisterForNotifications(r => QueryBeatmapForOnlinePlay(r, SelectedItem.Value.Beatmap), (items, changes, ___) =>
            {
                if (changes == null)
                    return;

                Scheduler.AddOnce(updateAvailability, beatmap);
            });
        }

        private void updateAvailability(APIBeatmap beatmap)
        {
            if (downloadTracker == null)
                return;

            switch (downloadTracker.State.Value)
            {
                case DownloadState.NotDownloaded:
                    availability.Value = BeatmapAvailability.NotDownloaded();
                    break;

                case DownloadState.Downloading:
                    availability.Value = BeatmapAvailability.Downloading((float)downloadTracker.Progress.Value);
                    break;

                case DownloadState.Importing:
                    availability.Value = BeatmapAvailability.Importing();
                    break;

                case DownloadState.LocallyAvailable:
                    bool available = QueryBeatmapForOnlinePlay(realm.Realm, beatmap).Any();

                    availability.Value = available ? BeatmapAvailability.LocallyAvailable() : BeatmapAvailability.NotDownloaded();

                    // only display a message to the user if a download seems to have just completed.
                    if (!available && downloadTracker.Progress.Value == 1)
                        Logger.Log("The imported beatmap set does not match the online version.", LoggingTarget.Runtime, LogLevel.Important);

                    break;

                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        /// <summary>
        /// Performs a query for a local <see cref="BeatmapInfo"/> matching a requested one for the purpose of online play.
        /// </summary>
        /// <param name="realm">The realm to query from.</param>
        /// <param name="beatmap">The requested beatmap.</param>
        /// <returns>A beatmap query.</returns>
        public static IQueryable<BeatmapInfo> QueryBeatmapForOnlinePlay(Realm realm, IBeatmapInfo beatmap)
        {
            return realm.All<BeatmapInfo>().Filter("OnlineID == $0 && MD5Hash == $1 && BeatmapSet.DeletePending == false", beatmap.OnlineID, beatmap.MD5Hash);
        }

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);

            realmSubscription?.Dispose();
        }
    }
}
