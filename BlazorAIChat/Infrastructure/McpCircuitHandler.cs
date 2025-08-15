using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Server.Circuits;
using BlazorAIChat.Services;
using Microsoft.AspNetCore.Http;
using System.Security.Claims;

namespace BlazorAIChat.Infrastructure
{
    public class McpCircuitHandler : CircuitHandler
    {
        private readonly IServiceProvider _services;
        private readonly ILogger<McpCircuitHandler> _logger;

        public McpCircuitHandler(IServiceProvider services, ILogger<McpCircuitHandler> logger)
        {
            _services = services;
            _logger = logger;
        }

        public override async Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
        {
            try
            {
                using var scope = _services.CreateScope();
                var httpContextAccessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
                var manager = scope.ServiceProvider.GetRequiredService<IMcpConnectionManager>();
                var userId = httpContextAccessor.HttpContext?.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (!string.IsNullOrEmpty(userId))
                {
                    await manager.DisconnectUserAsync(userId);
                    _logger.LogInformation("MCP connections disconnected for user {UserId} on circuit close", userId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error during MCP disconnect on circuit close");
            }
        }
    }
}
