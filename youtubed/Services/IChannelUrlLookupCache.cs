namespace youtubed.Services
{
    public interface IChannelUrlLookupCache
    {
        bool TryGetChannelId(string url, out string channelId);

        void Set(string url, string channelId);
    }
}
