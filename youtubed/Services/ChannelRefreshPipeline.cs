using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using youtubed.Domain;
using youtubed.Persistence;

namespace youtubed.Services
{
    public sealed class ChannelRefreshPipeline : IChannelRefreshPipeline
    {
        private readonly IChannelRepository _channelRepository;
        private readonly IYoutubeService _youtubeService;
        private readonly IListProjectionRepository _projectionRepository;
        private readonly IAppClock _clock;
        private readonly IYoutubeCallDelay _youtubeCallDelay;

        public ChannelRefreshPipeline(
            IChannelRepository channelRepository,
            IYoutubeService youtubeService,
            IListProjectionRepository projectionRepository,
            IAppClock clock,
            IYoutubeCallDelay youtubeCallDelay)
        {
            _channelRepository = channelRepository;
            _youtubeService = youtubeService;
            _projectionRepository = projectionRepository;
            _clock = clock;
            _youtubeCallDelay = youtubeCallDelay;
        }

        public async Task<ChannelRefreshPipelineResult> RefreshStaleChannelsAsync(CancellationToken cancellationToken)
        {
            var now = _clock.UtcNow;
            var lookahead = await _channelRepository.GetStaleLookaheadAsync(
                now,
                Constants.ChannelRefreshLookaheadCount,
                cancellationToken);

            var selectedIds = lookahead
                .Take(Constants.ChannelRefreshBatchSize)
                .Select(channel => channel.Id)
                .ToList();
            var channels = await _channelRepository.GetBatchAsync(selectedIds, cancellationToken);
            var result = new ChannelRefreshPipelineResult
            {
                StaleLookaheadCount = lookahead.Count,
                SelectedChannelCount = channels.Count
            };

            if (channels.Count == 0)
            {
                result.NextChannelRefreshAt =
                    await _channelRepository.GetNextActiveSubscribedRefreshAtAsync(cancellationToken);
                return result;
            }

            var refreshResults = await ProcessBatchAsync(channels, result, cancellationToken);
            if (refreshResults.Count == 0)
            {
                result.NextChannelRefreshAt =
                    await _channelRepository.GetNextActiveSubscribedRefreshAtAsync(CancellationToken.None);
                return result;
            }

            await _channelRepository.SaveRefreshResultsAsync(refreshResults, CancellationToken.None);
            result.ProjectionUpdateAttemptCount = 1;
            await _projectionRepository.UpdateProjectedChannelsAsync(
                refreshResults.Select(value => value.Channel).ToList(),
                CancellationToken.None);
            result.ProjectionUpdateSuccessCount = 1;

            result.RefreshedChannelCount = refreshResults.Count(value => value.VideosRefreshed);
            result.UnavailableChannelCount = refreshResults.Count(value => value.Channel.Status == ChannelStatus.Unavailable);
            result.NextChannelRefreshAt =
                await _channelRepository.GetNextActiveSubscribedRefreshAtAsync(CancellationToken.None);
            return result;
        }

        private async Task<IReadOnlyList<ChannelRefreshResult>> ProcessBatchAsync(
            IReadOnlyList<Channel> channels,
            ChannelRefreshPipelineResult result,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                result.CanceledBeforeStartingYoutubeCall = true;
                return Array.Empty<ChannelRefreshResult>();
            }

            IReadOnlyDictionary<string, YoutubeChannel> metadataById;
            try
            {
                metadataById = await _youtubeService.GetChannelsByIdAsync(
                    channels.Select(channel => channel.Id).ToList(),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                result.CanceledDuringYoutubeWork = true;
                return Array.Empty<ChannelRefreshResult>();
            }

            result.MetadataCallCount++;

            var refreshResultsByChannelId = new Dictionary<string, ChannelRefreshResult>(StringComparer.Ordinal);
            var channelsReadyForPlaylist = new List<Channel>();
            foreach (var channel in channels)
            {
                if (!metadataById.TryGetValue(channel.Id, out var metadata))
                {
                    if (string.IsNullOrWhiteSpace(channel.PlaylistId))
                    {
                        MarkUnavailable(channel);
                        refreshResultsByChannelId[channel.Id] = new ChannelRefreshResult
                        {
                            Channel = channel,
                            VideosRefreshed = false
                        };
                    }
                    else
                    {
                        channelsReadyForPlaylist.Add(channel);
                    }

                    continue;
                }

                ApplyMetadata(channel, metadata);
                refreshResultsByChannelId[channel.Id] = new ChannelRefreshResult
                {
                    Channel = channel,
                    VideosRefreshed = false
                };

                if (!string.IsNullOrWhiteSpace(channel.PlaylistId))
                {
                    channelsReadyForPlaylist.Add(channel);
                }
                else
                {
                    channel.StaleAfter = _clock.UtcNowAfterRandomDelay(
                        Constants.ChannelMaxAgeMin,
                        Constants.ChannelMaxAgeMax);
                }
            }

            var earliestPublishedAt = _clock.UtcNow.Subtract(Constants.VideoMaxAge);
            var playlistVideosByChannelId = new Dictionary<string, IReadOnlyList<YoutubeVideo>>(StringComparer.Ordinal);
            foreach (var channel in channelsReadyForPlaylist)
            {
                var playlistVideos = await FetchPlaylistVideosAsync(
                    channel,
                    earliestPublishedAt,
                    result,
                    cancellationToken);
                if (playlistVideos == null)
                {
                    break;
                }

                playlistVideosByChannelId[channel.Id] = playlistVideos;
            }

            var videoIds = playlistVideosByChannelId.Values
                .SelectMany(videos => videos)
                .Select(video => video.Id)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            IReadOnlyDictionary<string, TimeSpan> durationsById =
                new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
            var durationFetchCanceled = false;
            if (videoIds.Count > 0)
            {
                var fetchedDurationsById = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
                foreach (var chunk in videoIds.Chunk(50))
                {
                    if (!await CanStartNextYoutubeCallAsync(result, cancellationToken))
                    {
                        durationFetchCanceled = true;
                        break;
                    }

                    IReadOnlyDictionary<string, TimeSpan> chunkDurations;
                    try
                    {
                        chunkDurations = await _youtubeService.GetVideoDurationsByIdAsync(chunk, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        result.CanceledDuringYoutubeWork = true;
                        durationFetchCanceled = true;
                        break;
                    }

                    foreach (var duration in chunkDurations)
                    {
                        fetchedDurationsById[duration.Key] = duration.Value;
                    }

                    result.DurationCallCount++;
                }

                durationsById = fetchedDurationsById;
            }

            foreach (var channel in channelsReadyForPlaylist)
            {
                if (!playlistVideosByChannelId.TryGetValue(channel.Id, out var playlistVideos))
                {
                    continue;
                }

                var requiredVideoIds = playlistVideos
                    .Select(video => video.Id)
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                if (durationFetchCanceled && requiredVideoIds.Any(id => !durationsById.ContainsKey(id)))
                {
                    continue;
                }

                channel.Videos = playlistVideos
                    .Where(video => durationsById.ContainsKey(video.Id))
                    .Select(video => new ChannelVideo
                    {
                        ChannelId = channel.Id,
                        VideoId = video.Id,
                        Title = video.Title,
                        Duration = durationsById[video.Id],
                        PublishedAt = video.PublishedAt,
                        ThumbnailUrl = video.Thumbnail
                    })
                    .ToList();
                channel.StaleAfter = _clock.UtcNowAfterRandomDelay(
                    Constants.ChannelMaxAgeMin,
                    Constants.ChannelMaxAgeMax);
                if (!refreshResultsByChannelId.TryGetValue(channel.Id, out var refreshResult))
                {
                    refreshResult = new ChannelRefreshResult
                    {
                        Channel = channel
                    };
                    refreshResultsByChannelId[channel.Id] = refreshResult;
                }

                refreshResult.VideosRefreshed = true;
                refreshResult.EarliestPublishedAt = earliestPublishedAt;
            }

            return refreshResultsByChannelId.Values.ToList();
        }

        private async Task<IReadOnlyList<YoutubeVideo>> FetchPlaylistVideosAsync(
            Channel channel,
            DateTimeOffset earliestPublishedAt,
            ChannelRefreshPipelineResult result,
            CancellationToken cancellationToken)
        {
            var videos = new List<YoutubeVideo>();
            string pageToken = null;
            do
            {
                if (!await CanStartNextYoutubeCallAsync(result, cancellationToken))
                {
                    return null;
                }

                YoutubePlaylistVideoPage page;
                try
                {
                    page = await _youtubeService.GetPlaylistVideoPageAsync(
                        channel.PlaylistId,
                        earliestPublishedAt,
                        pageToken,
                        cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    result.CanceledDuringYoutubeWork = true;
                    return null;
                }

                result.PlaylistCallCount++;

                videos.AddRange(page.Videos.Where(video => video.ChannelId == channel.Id));
                pageToken = page.NextPageToken;
            } while (pageToken != null);

            return videos;
        }

        private async Task<bool> CanStartNextYoutubeCallAsync(
            ChannelRefreshPipelineResult result,
            CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                result.CanceledDuringYoutubeWork = true;
                return false;
            }

            try
            {
                await _youtubeCallDelay.DelayAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                result.CanceledDuringYoutubeWork = true;
                return false;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                result.CanceledDuringYoutubeWork = true;
                return false;
            }

            return true;
        }

        private void ApplyMetadata(Channel channel, YoutubeChannel metadata)
        {
            channel.Url = string.Format(Constants.YoutubeChannelUrl, metadata.Id);
            channel.Title = metadata.Title;
            channel.Thumbnail = metadata.Thumbnail;
            channel.PlaylistId = metadata.PlaylistId ?? string.Empty;
            channel.Status = ChannelStatus.Active;
            channel.StatusReason = ChannelStatusReason.None;
            channel.StatusUpdatedAt = null;
        }

        private void MarkUnavailable(Channel channel)
        {
            var now = _clock.UtcNow;
            channel.Status = ChannelStatus.Unavailable;
            channel.StatusReason = ChannelStatusReason.NotFound;
            channel.StatusUpdatedAt = now;
            channel.StaleAfter = now.Add(Constants.ChannelUnavailableStaleDelay);
        }
    }
}
