﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace youtubed.Services
{
    public interface IChannelVideoService
    {
        Task RefreshVideosAsync(string channelId);
    }
}
