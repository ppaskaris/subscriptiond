using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;

namespace youtubed.Models
{
    public class RemoveChannelModel
    {
        [Required]
        public string ChannelId { get; set; }
    }
}
