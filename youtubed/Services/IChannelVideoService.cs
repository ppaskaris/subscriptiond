﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using youtubed.Models;

namespace youtubed.Services
{
    public interface IChannelVideoService
    {
        Task RefreshVideosAsync(StaleChannelModel channel);
    }
}
