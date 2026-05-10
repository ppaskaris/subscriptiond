using System;
using System.Security.Cryptography;
using System.Threading.Tasks;
using youtubed.Models;
using youtubed.Persistence;

namespace youtubed.Services
{
    public class ListService : IListService
    {
        private readonly IListRepository _listRepository;
        private readonly IAppClock _clock;

        public ListService(IListRepository listRepository, IAppClock clock)
        {
            _listRepository = listRepository;
            _clock = clock;
        }

        public async Task<ListModel> CreateListAsync(string title)
        {
            var now = _clock.UtcNow;
            var list = new ListModel
            {
                Id = Guid.NewGuid(),
                Token = CreateToken(),
                Title = title,
                PlaybackRate = Constants.DefaultListPlaybackRate,
                ExpiredAfter = CreateExpiredAfter(now)
            };

            await _listRepository.CreateAsync(list);
            return list;
        }

        public Task<ListModel> GetListAsync(Guid id)
        {
            return _listRepository.GetAsync(id);
        }

        public Task<ListViewModel> GetListViewAsync(Guid id)
        {
            return GetListViewCoreAsync(id);
        }

        public Task AddChannelAsync(Guid listId, string channelId)
        {
            return _listRepository.AddChannelAsync(listId, channelId);
        }

        public Task RemoveChannelAsync(Guid listId, string channelId)
        {
            return _listRepository.RemoveChannelAsync(listId, channelId);
        }

        public Task UpdateListAsync(Guid id, string title, decimal playbackRate)
        {
            return _listRepository.UpdateAsync(id, title, playbackRate);
        }

        public Task DeleteListAsync(Guid id)
        {
            return _listRepository.DeleteAsync(id);
        }

        public Task<int> RemoveExpiredListsAsync()
        {
            return _listRepository.RemoveExpiredAsync(_clock.UtcNow);
        }

        private byte[] CreateToken()
        {
            var token = new byte[40];
            RandomNumberGenerator.Fill(token);
            return token;
        }

        private async Task<ListViewModel> GetListViewCoreAsync(Guid id)
        {
            var now = _clock.UtcNow;
            var view = await _listRepository.GetViewAsync(id, CreateExpiredAfter(now), now);
            if (view == null)
            {
                return null;
            }

            view.MaxAge = view.ExpiredAfter.Subtract(now);
            view.StaleRefreshAfter = _clock.RandomDelay(
                Constants.ChannelUpdateFrequencyMin,
                Constants.ChannelUpdateFrequencyMax);
            return view;
        }

        private DateTimeOffset CreateExpiredAfter(DateTimeOffset now)
        {
            return now.Add(_clock.RandomDelay(
                Constants.ListMaxAgeMin,
                Constants.ListMaxAgeMax));
        }
    }
}
