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

        public ListService(IListRepository listRepository)
        {
            _listRepository = listRepository;
        }

        public async Task<ListModel> CreateListAsync(string title)
        {
            var list = new ListModel
            {
                Id = Guid.NewGuid(),
                Token = CreateToken(),
                Title = title,
                ExpiredAfter = CreateExpiredAfter()
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
            return _listRepository.GetViewAsync(id, CreateExpiredAfter(), DateTimeOffset.Now);
        }

        public Task AddChannelAsync(Guid listId, string channelId)
        {
            return _listRepository.AddChannelAsync(listId, channelId);
        }

        public Task RemoveChannelAsync(Guid listId, string channelId)
        {
            return _listRepository.RemoveChannelAsync(listId, channelId);
        }

        public Task RenameListAsync(Guid id, string title)
        {
            return _listRepository.RenameAsync(id, title);
        }

        public Task DeleteListAsync(Guid id)
        {
            return _listRepository.DeleteAsync(id);
        }

        public Task<int> RemoveExpiredListsAsync()
        {
            return _listRepository.RemoveExpiredAsync(DateTimeOffset.Now);
        }

        private byte[] CreateToken()
        {
            var token = new byte[40];
            RandomNumberGenerator.Fill(token);
            return token;
        }

        private static DateTimeOffset CreateExpiredAfter()
        {
            var maxAge = Constants.RandomlyBetween(
                Constants.ListMaxAgeMin,
                Constants.ListMaxAgeMax);
            return DateTimeOffset.Now.Add(maxAge);
        }
    }
}
