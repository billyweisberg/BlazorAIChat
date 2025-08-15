#pragma warning disable SKEXP0010, SKEXP0001, SKEXP0020, KMEXP00
using BlazorAIChat;
using BlazorAIChat.Authentication;
using BlazorAIChat.Components;
using BlazorAIChat.Models;
using BlazorAIChat.Services;
using BlazorAIChat.Utils;
using BlazorAIChat.Infrastructure;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.SemanticKernel;
using ModelContextProtocol.Client;

var builder = WebApplication.CreateBuilder(args);

// Ensure only the desired appsettings are loaded per environment
// In Development: only appsettings.Development.json
// Otherwise: only appsettings.json
builder.Configuration.Sources.Clear();
if (builder.Environment.IsDevelopment())
{
    builder.Configuration.AddJsonFile("appsettings.Development.json", optional: false, reloadOnChange: true);
}
else
{
    builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
}

// Configure logging to default to the console
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Information);

builder.Services.Configure<AppSettings>(builder.Configuration);

// Register a default HttpClient for streaming/long-lived connections
builder.Services.AddHttpClient("defaultHttpClient");

//Register an HttpClient that has a retry policy handler. Used for Azure OpenAI calls.
builder.Services.AddHttpClient("retryHttpClient").AddPolicyHandler(RetryHelper.GetRetryPolicy());

builder.Services.AddDbContext<AIChatDBContext>();
builder.Services.AddScoped<UserService>();
builder.Services.AddScoped<ChatHistoryService>();
builder.Services.AddScoped<AIService>();
builder.Services.AddSingleton<AISearchService>();

// Add memory cache for MCP connection manager
builder.Services.AddMemoryCache();

// Register secure storage for MCP input secrets
builder.Services.AddSingleton<ISecureStorage, DpapiSecureStorage>();

// HttpContext accessor needed by circuit handler
builder.Services.AddHttpContextAccessor();

// Register new per-user MCP connection manager (scoped because it depends on AIChatDBContext)
builder.Services.AddScoped<IMcpConnectionManager, McpConnectionManager>();

// Register CircuitHandler to cleanup MCP connections when circuits close
builder.Services.AddSingleton<CircuitHandler, McpCircuitHandler>();

// Register the Kernel using DI, without pre-attaching MCP plugins (they will be attached per-user on-demand)
builder.Services.AddTransient<Kernel>(serviceProvider =>
{
    var appSettings = serviceProvider.GetRequiredService<IOptions<AppSettings>>().Value;
    var httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();
    var httpClient = httpClientFactory.CreateClient("retryHttpClient");

    var kernelBuilder = Kernel.CreateBuilder()
        .AddAzureOpenAIChatCompletion(
            appSettings.AzureOpenAIChatCompletion.DeploymentName,
            appSettings.AzureOpenAIChatCompletion.Endpoint,
            appSettings.AzureOpenAIChatCompletion.ApiKey,
            httpClient: httpClient)
        .AddAzureOpenAIEmbeddingGenerator(
            appSettings.AzureOpenAIEmbedding.DeploymentName,
            appSettings.AzureOpenAIChatCompletion.Endpoint,
            appSettings.AzureOpenAIChatCompletion.ApiKey,
            httpClient: httpClient);

    kernelBuilder.Services.AddLogging(services => services.AddConsole().SetMinimumLevel(LogLevel.Trace));
    return kernelBuilder.Build();
});

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddCircuitOptions(options => options.DetailedErrors = true);

builder.Services.AddCascadingAuthenticationState();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie();

builder.Services.AddAuthorizationCore();

builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = int.MaxValue;
});

var app = builder.Build();

//setup EF database and migrate to latest version
using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    var context = services.GetRequiredService<AIChatDBContext>();
    context.Database.Migrate();
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles();
app.UseAntiforgery();

app.UseAuthentication();

//Add easy auth middleware
app.UseMiddleware<EasyAuthMiddleware>();

// Add guest auth fallback after EasyAuth and before authorization
app.UseMiddleware<GuestAuthMiddleware>();

app.UseAuthorization();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

//Before we start the app, ensure that the KNN folder exists on the filesystem
if (!Directory.Exists("KNN"))
{
    Directory.CreateDirectory("KNN");
}
if (!Directory.Exists("SFS"))
{
    Directory.CreateDirectory("SFS");
}

app.Run();
