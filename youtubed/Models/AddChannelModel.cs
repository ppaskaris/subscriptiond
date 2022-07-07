using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;

namespace youtubed.Models
{
    public class AddChannelModel
    {
        [Required, Url]
        [RegularExpression(Constants.YoutubeChannelPattern + "|" + Constants.YoutubeVideoPattern)]
        [Display(Name = "Channel or Video URL")]
        public string Url { get; set; }
    }
}
