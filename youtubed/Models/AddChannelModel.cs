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
        [Required]
        public Guid? Id { get; set; }

        [Required]
        public string Token { get; set; }

        [Required, Url]
        [RegularExpression(Constants.YoutubePattern)]
        [Display(Name = "Channel URL")]
        public string Url { get; set; }
    }
}
