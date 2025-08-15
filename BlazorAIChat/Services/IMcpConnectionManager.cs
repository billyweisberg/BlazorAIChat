#pragma warning disable SKEXP0010, SKEXP0001, SKEXP0020, KMEXP00
using Microsoft.SemanticKernel;
using System.Threading;

namespace BlazorAIChat.Services
{
    public interface IMcpConnectionManager
    {
        /// <summary>
        /// Ensure that all configured MCP server plugins for the specified user are connected and attached to the kernel.
        /// </summary>
        Task EnsurePluginsForUserAsync(string userId, Kernel kernel, CancellationToken cancellationToken = default);

        /// <summary>
        /// Disconnect and dispose all MCP connections for a specific user.
        /// </summary>
        Task DisconnectUserAsync(string userId);

        /// <summary>
        /// Returns the last-known connection statuses for the user's effective MCP servers.
        /// This will attempt to establish connections if not already cached.
        /// </summary>
        Task<IReadOnlyList<McpServerStatus>> GetServerStatusesAsync(string userId, CancellationToken cancellationToken = default);
    }
}
