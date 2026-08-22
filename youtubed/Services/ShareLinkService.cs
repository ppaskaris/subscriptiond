using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading.Tasks;
using youtubed.Domain;
using youtubed.Models;
using youtubed.Persistence;

namespace youtubed.Services
{
    public class ShareLinkService : IShareLinkService
    {
        private const int PasswordWordCount = 4;
        private const int MaxCreateAttempts = 20;
        private static readonly Lazy<string[]> PasswordWords = new Lazy<string[]>(LoadPasswordWords);

        private readonly IShareLinkRepository _shareLinkRepository;
        private readonly IListRepository _listRepository;
        private readonly IAppClock _clock;

        public ShareLinkService(
            IShareLinkRepository shareLinkRepository,
            IListRepository listRepository,
            IAppClock clock)
        {
            _shareLinkRepository = shareLinkRepository
                ?? throw new ArgumentNullException(nameof(shareLinkRepository));
            _listRepository = listRepository
                ?? throw new ArgumentNullException(nameof(listRepository));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        public async Task<ShareLinkModel> CreateShareLinkAsync(Guid listId)
        {
            for (var attempt = 0; attempt < MaxCreateAttempts; attempt++)
            {
                var createdAt = _clock.UtcNow;
                var shareLink = new ShareLink
                {
                    Password = CreatePassword(),
                    ListId = listId,
                    CreatedAt = createdAt,
                    ExpiresAfter = createdAt.Add(_clock.RandomDelay(
                        Constants.ShareLinkMaxAgeMin,
                        Constants.ShareLinkMaxAgeMax))
                };

                if (await _shareLinkRepository.TryCreateAsync(shareLink))
                {
                    return ToModel(shareLink);
                }
            }

            throw new InvalidOperationException("Could not create a unique share link.");
        }

        public async Task<IReadOnlyList<ShareLinkModel>> GetShareLinksAsync(Guid listId)
        {
            return (await _shareLinkRepository.GetByListAsync(listId))
                .Select(ToModel)
                .ToArray();
        }

        public Task DeleteShareLinkInListAsync(Guid listId, string password)
        {
            return _shareLinkRepository.DeleteAsync(listId, password);
        }

        public Task DeleteShareLinksAsync(Guid listId)
        {
            return _shareLinkRepository.DeleteByListAsync(listId);
        }

        public async Task<ConsumedShareLinkModel> ConsumeShareLinkAsync(string password)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(password);
            var now = _clock.UtcNow;
            var shareLink = await _shareLinkRepository.GetAsync(password);
            if (shareLink == null
                || shareLink.UsedAt.HasValue
                || shareLink.ExpiresAfter <= now)
            {
                return null;
            }

            var list = await _listRepository.GetAsync(shareLink.ListId);
            if (list == null)
            {
                return null;
            }

            if (!await _shareLinkRepository.TryMarkUsedAsync(
                password,
                shareLink.ListId,
                now))
            {
                return null;
            }

            return new ConsumedShareLinkModel
            {
                ListId = list.Id,
                Token = list.Token.ToArray()
            };
        }

        private static ShareLinkModel ToModel(ShareLink shareLink)
        {
            return new ShareLinkModel
            {
                Password = shareLink.Password,
                ListId = shareLink.ListId,
                CreatedAt = shareLink.CreatedAt,
                ExpiresAfter = shareLink.ExpiresAfter,
                UsedAt = shareLink.UsedAt
            };
        }

        private static string CreatePassword()
        {
            var words = new string[PasswordWordCount];

            for (var index = 0; index < words.Length; index++)
            {
                words[index] = PasswordWords.Value[RandomNumberGenerator.GetInt32(PasswordWords.Value.Length)];
            }

            return string.Join("-", words);
        }

        private static string[] LoadPasswordWords()
        {
            var path = GetPasswordWordListPath();
            if (path == null)
            {
                throw new FileNotFoundException("Could not find share password word list.");
            }

            return File.ReadAllLines(path)
                .Where(word => !string.IsNullOrWhiteSpace(word))
                .ToArray();
        }

        private static string GetPasswordWordListPath()
        {
            var candidatePaths = new[]
            {
                Path.Combine(AppContext.BaseDirectory, "Resources", "SharePasswordWords.txt"),
                Path.GetFullPath(Path.Combine(
                    AppContext.BaseDirectory,
                    "..",
                    "..",
                    "..",
                    "..",
                    "youtubed",
                    "Resources",
                    "SharePasswordWords.txt"))
            };

            foreach (var candidatePath in candidatePaths)
            {
                if (File.Exists(candidatePath))
                {
                    return candidatePath;
                }
            }

            return null;
        }
    }
}
