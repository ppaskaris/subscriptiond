using System;
using System.Collections.Generic;
using System.Linq;

namespace youtubed.Services
{
    public sealed class ChannelRefreshPipelineResult
    {
        public IReadOnlyList<ChannelRefreshOutcome> Outcomes { get; set; } =
            Array.Empty<ChannelRefreshOutcome>();
        public int SelectedChannelCount { get; set; }
        public int MetadataCallCount { get; set; }
        public int PlaylistCallCount { get; set; }
        public int DurationCallCount { get; set; }
        public int RefreshedChannelCount { get; set; }
        public int UnavailableChannelCount { get; set; }
        public bool CanceledBeforeStartingYoutubeCall { get; set; }
        public bool CanceledDuringYoutubeWork { get; set; }

        public IReadOnlyList<string> RetryChannelIds => Outcomes
            .Where(outcome => outcome.Disposition == ChannelRefreshDisposition.RetryTransient)
            .Select(outcome => outcome.ChannelId)
            .ToList();
    }
}
