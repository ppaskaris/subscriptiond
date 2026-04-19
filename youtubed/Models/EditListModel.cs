using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;

namespace youtubed.Models
{
    public class EditListModel : IValidatableObject
    {
        [Required, StringLength(100)]
        public string Title { get; set; }

        [Display(Name = "Playback speed")]
        public decimal PlaybackRate { get; set; } = Constants.DefaultListPlaybackRate;

        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if (!Constants.IsSupportedPlaybackRate(PlaybackRate))
            {
                yield return new ValidationResult(
                    "Choose a supported playback speed.",
                    new[] { nameof(PlaybackRate) });
            }
        }
    }
}
