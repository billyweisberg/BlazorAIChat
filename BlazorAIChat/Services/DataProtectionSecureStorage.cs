using Microsoft.AspNetCore.DataProtection;

namespace BlazorAIChat.Services
{
    // Cross-platform secure storage using ASP.NET Core Data Protection
    internal sealed class DataProtectionSecureStorage : ISecureStorage
    {
        private readonly IDataProtector _protector;

        public DataProtectionSecureStorage(IDataProtectionProvider provider)
        {
            _protector = provider.CreateProtector("BlazorAIChat.ISecureStorage.v1");
        }

        public string Protect(string plaintext)
        {
            if (plaintext is null) throw new ArgumentNullException(nameof(plaintext));
            return _protector.Protect(plaintext);
        }

        public string? UnprotectOrNull(string? protectedValue)
        {
            if (string.IsNullOrWhiteSpace(protectedValue)) return null;
            try
            {
                return _protector.Unprotect(protectedValue);
            }
            catch
            {
                return null;
            }
        }
    }
}
