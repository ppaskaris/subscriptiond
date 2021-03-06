﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using youtubed.Models;

namespace youtubed.Services
{
    public interface IChannelService
    {
        Task<ChannelModel> GetOrCreateChannelAsync(string url);
        Task<string> GetNextStaleIdOrDefaultAsync();
        Task<int> RemoveOrphanChannelsAsync();
    }
}
