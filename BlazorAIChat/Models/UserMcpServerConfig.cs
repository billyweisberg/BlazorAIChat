using System.ComponentModel.DataAnnotations;

namespace BlazorAIChat.Models
{
    public class UserMcpServerConfig
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();
        [Required]
        public string UserId { get; set; } = string.Empty;
        [Required]
        public string Name { get; set; } = string.Empty; // server key/alias
        public string Type { get; set; } = string.Empty; // stdio or sse
        public string? Command { get; set; }
        public string? ArgsJson { get; set; } // JSON array of strings
        public string? EnvJson { get; set; } // JSON object of string:string
        public string? Url { get; set; }
        public string? HeadersJson { get; set; } // JSON object of string:string
        public bool Enabled { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
