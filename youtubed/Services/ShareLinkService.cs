using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading.Tasks;
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

        public ShareLinkService(IShareLinkRepository shareLinkRepository)
        {
            _shareLinkRepository = shareLinkRepository;
        }

        public async Task<ShareLinkModel> CreateShareLinkAsync(Guid listId)
        {
            for (var attempt = 0; attempt < MaxCreateAttempts; attempt++)
            {
                var createdAt = DateTimeOffset.Now;
                var shareLink = new ShareLinkModel
                {
                    Password = CreatePassword(),
                    ListId = listId,
                    CreatedAt = createdAt,
                    ExpiresAfter = createdAt.Add(Constants.RandomlyBetween(
                        Constants.ShareLinkMaxAgeMin,
                        Constants.ShareLinkMaxAgeMax))
                };

                if (await _shareLinkRepository.TryCreateAsync(shareLink))
                {
                    return shareLink;
                }
            }

            throw new InvalidOperationException("Could not create a unique share link.");
        }

        public Task<IReadOnlyList<ShareLinkModel>> GetShareLinksAsync(Guid listId)
        {
            return _shareLinkRepository.GetByListAsync(listId);
        }

        public Task DeleteShareLinkInListAsync(Guid listId, string password)
        {
            return _shareLinkRepository.DeleteAsync(listId, password);
        }

        public Task DeleteShareLinksAsync(Guid listId)
        {
            return _shareLinkRepository.DeleteByListAsync(listId);
        }

        public Task<ConsumedShareLinkModel> ConsumeShareLinkAsync(string password)
        {
            return _shareLinkRepository.ConsumeAsync(password, DateTimeOffset.Now);
        }

        public Task<int> RemoveExpiredShareLinksAsync()
        {
            return _shareLinkRepository.RemoveExpiredAsync(
                DateTimeOffset.Now.Subtract(Constants.ShareLinkRetentionAfterExpiration));
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
