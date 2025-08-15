using BlazorAIChat.Models;
using Microsoft.Extensions.Options;
using System.Security.Claims;

namespace BlazorAIChat.Authentication
{
    /// <summary>
    /// Fallback middleware that authenticates all visitors as a shared "guest" user
    /// when EasyAuth is not required and no authenticated principal exists.
    /// This enables shared MCP configuration for guest usage while keeping EasyAuth
    /// behavior unchanged when enabled.
    /// </summary>
    public class GuestAuthMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly IOptions<AppSettings> _appSettings;

        public GuestAuthMiddleware(RequestDelegate next, IOptions<AppSettings> appSettings)
        {
            _next = next;
            _appSettings = appSettings;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            // Only apply in scenarios where EasyAuth is not required and the user is not already authenticated
            var requireEasyAuth = _appSettings.Value.RequireEasyAuth;
            var isAuthenticated = context.User?.Identity?.IsAuthenticated == true;

            if (!requireEasyAuth && !isAuthenticated)
            {
                // Elevate guest to Admin when EasyAuth is disabled so admin pages are reachable for configuration
                var roleName = Enum.GetName(UserRoles.Admin)!;

                var claims = new List<Claim>
                {
                    new Claim(ClaimTypes.NameIdentifier, "guest"),
                    new Claim(ClaimTypes.Name, "Guest"),
                    new Claim(ClaimTypes.Role, roleName)
                };

                // Provide a stable principal so [Authorize] works and components get an authenticated identity
                var identity = new ClaimsIdentity(claims, authenticationType: "GuestAuth");
                context.User = new ClaimsPrincipal(identity);
            }

            await _next(context);
        }
    }
}
