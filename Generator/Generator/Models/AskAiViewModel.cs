using System.ComponentModel.DataAnnotations;

namespace Generator.Models
{
    public class AskAiViewModel
    {
        [Required]
        public string Prompt { get; set; } = string.Empty;

        public string? AiResponse { get; set; }

        public string? ErrorMessage { get; set; }
    }
}
