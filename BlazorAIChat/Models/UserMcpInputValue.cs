using System.ComponentModel.DataAnnotations;

namespace BlazorAIChat.Models
{
    public class UserMcpInputValue
    {
        [Key]
        public Guid Id { get; set; } = Guid.NewGuid();
        [Required]
        public string UserId { get; set; } = string.Empty;
        [Required]
        public string InputId { get; set; } = string.Empty; // matches McpInput.Id
        [Required]
        public string ProtectedValue { get; set; } = string.Empty; // encrypted/protected
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
