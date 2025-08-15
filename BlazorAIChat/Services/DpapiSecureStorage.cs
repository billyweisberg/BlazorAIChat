using System.Security.Cryptography;
using System.Text;

namespace BlazorAIChat.Services
{
    // Simple DPAPI-based protector for server-side usage. Replace with Azure Key Vault in production.
    internal sealed class DpapiSecureStorage : ISecureStorage
    {
        public string Protect(string plaintext)
        {
            if (plaintext == null) throw new ArgumentNullException(nameof(plaintext));
            var bytes = Encoding.UTF8.GetBytes(plaintext);
            var protectedBytes = ProtectedData.Protect(bytes, optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(protectedBytes);
        }

        public string? UnprotectOrNull(string? protectedValue)
        {
            if (string.IsNullOrWhiteSpace(protectedValue)) return null;
            try
            {
                var protectedBytes = Convert.FromBase64String(protectedValue);
                var bytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, scope: DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return null;
            }
        }
    }
}
