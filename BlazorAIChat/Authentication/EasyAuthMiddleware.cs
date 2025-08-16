using BlazorAIChat.Models;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

namespace BlazorAIChat.Authentication
{
    public class EasyAuthMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly IOptions<AppSettings> _appSettings;

        public EasyAuthMiddleware(RequestDelegate next, IOptions<AppSettings> appSettings)
        {
            _next = next;
            _appSettings = appSettings;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            // If EasyAuth is not required, do not attempt to create an EasyAuth principal
            if (!_appSettings.Value.RequireEasyAuth)
            {
                await _next(context);
                return;
            }

            ClaimsPrincipal? principal = null;

            // Preferred path: discrete Easy Auth headers (v2)
            if (context.Request.Headers.TryGetValue("X-MS-CLIENT-PRINCIPAL-ID", out var userId) &&
                !string.IsNullOrWhiteSpace(userId))
            {
                var claims = BuildClaimsFromDiscreteHeaders(context, userId!);
                principal = await EnrichWithDatabaseAsync(context, claims, userId!);
            }
            // Fallback: base64 encoded principal payload (v1/v2 compatible)
            else if (context.Request.Headers.TryGetValue("X-MS-CLIENT-PRINCIPAL", out var b64) &&
                     !string.IsNullOrWhiteSpace(b64))
            {
                var claims = BuildClaimsFromBase64Principal(b64!);
                var id = claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier)?.Value;
                if (!string.IsNullOrWhiteSpace(id))
                {
                    principal = await EnrichWithDatabaseAsync(context, claims, id!);
                }
            }

            if (principal is not null)
            {
                context.User = principal;
            }

            await _next(context);
        }

        private static List<Claim> BuildClaimsFromDiscreteHeaders(HttpContext context, string userId)
        {
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, userId)
            };

            // Name/UPN
            if (context.Request.Headers.TryGetValue("X-MS-CLIENT-PRINCIPAL-NAME", out var userName) &&
                !string.IsNullOrWhiteSpace(userName))
            {
                claims.Add(new Claim(ClaimTypes.Name, userName!));
                // If looks like an email, set email too for convenience
                if (userName!.ToString().Contains('@'))
                {
                    claims.Add(new Claim(ClaimTypes.Email, userName!));
                }
            }
            else
            {
                claims.Add(new Claim(ClaimTypes.Name, userId));
            }

            if (context.Request.Headers.TryGetValue("X-MS-CLIENT-PRINCIPAL-IDP", out var idp) &&
                !string.IsNullOrWhiteSpace(idp))
            {
                claims.Add(new Claim("idp", idp!));
            }

            return claims;
        }

        private static List<Claim> BuildClaimsFromBase64Principal(string base64)
        {
            var claims = new List<Claim>();
            try
            {
                var json = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var userId = root.TryGetProperty("userId", out var uid) ? uid.GetString() : null;
                var userDetails = root.TryGetProperty("userDetails", out var ud) ? ud.GetString() : null;
                var idp = root.TryGetProperty("identityProvider", out var p) ? p.GetString() : null;

                if (!string.IsNullOrWhiteSpace(userId))
                {
                    claims.Add(new Claim(ClaimTypes.NameIdentifier, userId!));
                }
                if (!string.IsNullOrWhiteSpace(userDetails))
                {
                    claims.Add(new Claim(ClaimTypes.Name, userDetails!));
                    if (userDetails!.Contains('@'))
                    {
                        claims.Add(new Claim(ClaimTypes.Email, userDetails!));
                    }
                }
                if (!string.IsNullOrWhiteSpace(idp))
                {
                    claims.Add(new Claim("idp", idp!));
                }

                // Additional claims provided by Easy Auth
                if (root.TryGetProperty("claims", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var c in arr.EnumerateArray())
                    {
                        if (c.TryGetProperty("typ", out var t) && c.TryGetProperty("val", out var v))
                        {
                            var type = t.GetString();
                            var val = v.GetString();
                            if (!string.IsNullOrWhiteSpace(type) && !string.IsNullOrWhiteSpace(val))
                            {
                                claims.Add(new Claim(type!, val!));
                            }
                        }
                    }
                }

                // Some providers set roles separately
                if (root.TryGetProperty("userRoles", out var rolesEl) && rolesEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var r in rolesEl.EnumerateArray())
                    {
                        var role = r.GetString();
                        if (!string.IsNullOrWhiteSpace(role))
                        {
                            claims.Add(new Claim(ClaimTypes.Role, role!));
                        }
                    }
                }
            }
            catch
            {
                // Ignore parse errors and continue anonymous; fallback middleware/authorize will handle
            }

            // Ensure a display name exists
            if (!claims.Any(c => c.Type == ClaimTypes.Name))
            {
                var id = claims.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier)?.Value;
                if (!string.IsNullOrWhiteSpace(id))
                {
                    claims.Add(new Claim(ClaimTypes.Name, id!));
                }
            }

            return claims;
        }

        private static async Task<ClaimsPrincipal> EnrichWithDatabaseAsync(HttpContext context, List<Claim> claims, string userId)
        {
            using var scope = context.RequestServices.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AIChatDBContext>();

            //get the user details from the database
            var dbUser = await dbContext.Users.FindAsync(userId);
            if (dbUser != null)
            {
                // Prefer stored display name when available
                var existingName = claims.FirstOrDefault(c => c.Type == ClaimTypes.Name);
                if (!string.IsNullOrEmpty(dbUser.Name))
                {
                    if (existingName != null) claims.Remove(existingName);
                    claims.Add(new Claim(ClaimTypes.Name, $"{dbUser.Name} ({existingName?.Value})"));
                }
                else if (existingName != null)
                {
                    dbUser.Name = existingName.Value;
                    await dbContext.SaveChangesAsync();
                }

                claims.Add(new Claim(ClaimTypes.Role, Enum.GetName(dbUser.Role)!));
                if (!string.IsNullOrEmpty(dbUser.Email))
                {
                    // Overwrite or add email from DB if present
                    var existingEmail = claims.FirstOrDefault(c => c.Type == ClaimTypes.Email);
                    if (existingEmail != null) claims.Remove(existingEmail);
                    claims.Add(new Claim(ClaimTypes.Email, dbUser.Email));
                }
                claims.Add(new Claim("dateRequested", dbUser.DateRequested.ToString()));
                if (dbUser.DateApproved != null)
                {
                    claims.Add(new Claim("dateApproved", dbUser.DateApproved.Value.ToString()));
                }
                if (!string.IsNullOrEmpty(dbUser.ApprovedBy))
                {
                    claims.Add(new Claim("approvedBy", dbUser.ApprovedBy));
                }
            }
            else
            {
                // Not yet in DB -> mark as Guest role until approved
                claims.Add(new Claim(ClaimTypes.Role, Enum.GetName(UserRoles.Guest)!));
            }

            var identity = new ClaimsIdentity(claims, authenticationType: "EasyAuth");
            return new ClaimsPrincipal(identity);
        }
    }
}
