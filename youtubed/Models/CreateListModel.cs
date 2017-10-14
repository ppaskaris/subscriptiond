using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;

namespace youtubed.Models
{
    public class CreateListModel
    {
        [Required, StringLength(100)]
        public string Title { get; set; } = "My List";
    }
}
