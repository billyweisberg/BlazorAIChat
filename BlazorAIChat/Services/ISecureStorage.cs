namespace BlazorAIChat.Services
{
    public interface ISecureStorage
    {
        string Protect(string plaintext);
        string? UnprotectOrNull(string? protectedValue);
    }
}
