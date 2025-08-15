using Azure.Security.KeyVault.Secrets;
using Microsoft.AspNetCore.DataProtection.Repositories;
using System.Xml.Linq;

namespace BlazorAIChat.Services
{
    /// <summary>
    /// ASP.NET Core Data Protection XML repository backed by Azure Key Vault Secrets.
    /// Each Data Protection key is stored as a separate secret version where the value is the key XML.
    /// </summary>
    internal sealed class KeyVaultXmlRepository : IXmlRepository
    {
        private readonly SecretClient _secretClient;
        private readonly string _secretNamePrefix;

        public KeyVaultXmlRepository(SecretClient secretClient, string secretNamePrefix = "blazoraichat-dp-")
        {
            _secretClient = secretClient ?? throw new ArgumentNullException(nameof(secretClient));
            _secretNamePrefix = string.IsNullOrWhiteSpace(secretNamePrefix) ? "blazoraichat-dp-" : secretNamePrefix;
        }

        public IReadOnlyCollection<XElement> GetAllElements()
        {
            var list = new List<XElement>();

            // List all secrets, filter by prefix, then read their latest values
            foreach (var props in _secretClient.GetPropertiesOfSecrets())
            {
                if (!props.Enabled.GetValueOrDefault(true)) continue;
                if (!props.Name.StartsWith(_secretNamePrefix, StringComparison.OrdinalIgnoreCase)) continue;

                try
                {
                    var secret = _secretClient.GetSecret(props.Name);
                    var value = secret?.Value?.Value;
                    if (string.IsNullOrWhiteSpace(value)) continue;

                    var element = XElement.Parse(value);
                    list.Add(element);
                }
                catch
                {
                    // Ignore malformed/unauthorized entries
                }
            }

            return list;
        }

        public void StoreElement(XElement element, string friendlyName)
        {
            if (element is null) throw new ArgumentNullException(nameof(element));
            if (string.IsNullOrWhiteSpace(friendlyName)) throw new ArgumentNullException(nameof(friendlyName));

            var name = _secretNamePrefix + friendlyName; // e.g., blazoraichat-dp-key-<guid>
            var value = element.ToString(SaveOptions.DisableFormatting);
            var secret = new KeyVaultSecret(name, value)
            {
                Properties = { ContentType = "application/xml" }
            };
            _secretClient.SetSecret(secret);
        }
    }
}
