using System;
using System.Collections.Generic;
using System.Linq;

namespace youtubed.Models
{
    public class ShareListViewModel
    {
        public Guid ListId { get; set; }
        public string Token { get; set; }
        public string Title { get; set; }
        public bool ShareCreationEnabled { get; set; } = true;
        public IEnumerable<ShareLinkListItemViewModel> ShareLinks { get; set; } = Enumerable.Empty<ShareLinkListItemViewModel>();

        public bool HasLinks => ShareLinks.Any();
    }
}
