using Kare.Abstractions;
using Kare.Cloud.Copilot;
using Kare.Core.Inference;
using Kare.Core.Options;
using Kare.Core.Routing;
using Kare.Inference.GenieX;
using Kare.Inference.OnnxGenAI;
using Kare.Service;
using Kare.Service.Api;
using Kare.Service.Cache;
using Kare.Service.Dashboard;
using Kare.Service.Logging;
using Kare.Service.Options;
using Kare.Service.Routing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateSlimBuilder(args);

var logDirectory = Environment.GetEnvironmentVariable("KARE_LOG_DIRECTORY");
if (!string.IsNullOrWhiteSpace(logDirectory))
{
    builder.Logging.AddProvider(new RotatingFileLoggerProvider(
        logDirectory,
        ReadPositiveLong("KARE_LOG_FILE_BYTES", 25L * 1024 * 1024),
        ReadPositiveLong("KARE_LOG_TOTAL_BYTES", 250L * 1024 * 1024)));
}

var externalConfig = Environment.GetEnvironmentVariable("KARE_CONFIG_FILE");
if (!string.IsNullOrWhiteSpace(externalConfig))
{
    if (!Path.IsPathRooted(externalConfig))
    {
        throw new InvalidOperationException("KARE_CONFIG_FILE must be an absolute path.");
    }

    builder.Configuration.AddJsonFile(externalConfig, optional: false, reloadOnChange: false);
}

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, DashboardJsonContext.Default);
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, OpenAiJsonContext.Default);
});

builder.Services
    .AddOptions<KareServiceOptions>()
    .Bind(builder.Configuration.GetSection(KareServiceOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<KareServiceOptions>, KareServiceOptionsValidator>();

builder.Services
    .AddOptions<InferenceLimits>()
    .Bind(builder.Configuration.GetSection(InferenceLimits.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<InferenceLimits>, InferenceLimitsValidator>();

builder.Services
    .AddOptions<RoutePolicyOptions>()
    .Bind(builder.Configuration.GetSection(RoutePolicyOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<RoutePolicyOptions>, RoutePolicyOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<RoutePolicyOptions>, RoutePolicySemanticValidator>();

builder.Services
    .AddOptions<ResponseCacheOptions>()
    .Bind(builder.Configuration.GetSection(ResponseCacheOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<ResponseCacheOptions>, ResponseCacheOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<ResponseCacheOptions>, ResponseCachePersistenceValidator>();

builder.Services
    .AddOptions<CopilotSdkOptions>()
    .Bind(builder.Configuration.GetSection(CopilotSdkOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<CopilotSdkOptions>, CopilotSdkOptionsValidator>();

builder.Services
    .AddOptions<OnnxGenAiOptions>()
    .Bind(builder.Configuration.GetSection(OnnxGenAiOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<OnnxGenAiOptions>, OnnxGenAiOptionsValidator>();

builder.Services
    .AddOptions<GenieXOptions>()
    .Bind(builder.Configuration.GetSection(GenieXOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<GenieXOptions>, GenieXOptionsValidator>();

builder.Services.AddSingleton<InferenceGate>();
builder.Services.AddSingleton<MetricsRouteRecorder>();
builder.Services.AddSingleton<SelectedBackend>();
builder.Services.AddSingleton<ResponseCache>();
builder.Services.AddSingleton<CascadeRouteContext>();
builder.Services.AddSingleton<IDashboardMetricsCollector, InMemoryMetricsCollector>();
builder.Services.AddSingleton<IModelEndpointProvider, ModelEndpointProvider>();
builder.Services.AddSingleton<DashboardKnowledgeService>();
builder.Services.AddSingleton<IDashboardKnowledgeService>(
    sp => sp.GetRequiredService<DashboardKnowledgeService>());
builder.Services.AddSingleton<IHostedService>(
    sp => sp.GetRequiredService<DashboardKnowledgeService>());
builder.Services.AddHttpClient(nameof(DashboardKnowledgeService), client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Kare/1.0");
})
.ConfigurePrimaryHttpMessageHandler(static () => new SocketsHttpHandler
{
    AllowAutoRedirect = false,
});
builder.Services.AddSingleton<IRouteRecorder>(sp =>
    new DashboardActivity(
        sp.GetRequiredService<MetricsRouteRecorder>(),
        sp.GetRequiredService<IDashboardMetricsCollector>(),
        sp.GetRequiredService<InferenceGate>(),
        sp.GetRequiredService<IOptions<InferenceLimits>>()));
builder.Services.AddSingleton<CopilotSdkBackend>();
builder.Services.AddSingleton<ICloudInferenceBackend>(sp => sp.GetRequiredService<CopilotSdkBackend>());
builder.Services.AddSingleton<ICloudModelCatalog>(sp => sp.GetRequiredService<CopilotSdkBackend>());

// The CPU backend is registered unconditionally and at the lowest priority so it stays
// the fallback. Accelerator backends are added above it once they probe successfully.
builder.Services.AddSingleton<ILocalInferenceBackend, OnnxGenAiBackend>();
builder.Services.AddSingleton<ILocalInferenceBackend, GenieXBackend>();
builder.Services.AddHttpClient(nameof(GenieXBackend));
builder.Services.AddSingleton<LocalBackendSelector>();

builder.Services.AddSingleton<IChatClient>(sp =>
{
    var selected = sp.GetRequiredService<SelectedBackend>();
    var gate = sp.GetRequiredService<InferenceGate>();
    var limits = sp.GetRequiredService<IOptions<InferenceLimits>>();

    // These bounds protect only the passive local SLM. Cloud routes must be able to
    // accept the larger prompts sent by Copilot CLI and other OpenAI clients.
    IChatClient boundedLocal = new BoundedChatClient(selected.Backend, gate, limits);
    boundedLocal = new ContextEnrichingChatClient(
        boundedLocal,
        sp.GetRequiredService<IDashboardKnowledgeService>());
    return new HydraFusionCascadeChatClient(
        boundedLocal,
        sp.GetRequiredService<ICloudInferenceBackend>(),
        sp.GetRequiredService<ICloudModelCatalog>(),
        sp.GetRequiredService<IRouteRecorder>(),
        sp.GetRequiredService<ResponseCache>(),
        sp.GetRequiredService<CascadeRouteContext>(),
        sp.GetRequiredService<IOptions<RoutePolicyOptions>>(),
        selected.Kind,
        selected.ModelId,
        sp.GetRequiredService<ILoggerFactory>());
});

builder.Services.Configure<KestrelServerOptions>(options =>
{
    var service = builder.Configuration
        .GetSection(KareServiceOptions.SectionName)
        .Get<KareServiceOptions>() ?? new KareServiceOptions();

    options.Limits.MaxRequestBodySize = service.MaxRequestBodyBytes;

    // A local model can be slow to produce the first token on a cold cache. The default
    // minimum response rate would abort those requests, so it is turned off deliberately.
    options.Limits.MinResponseDataRate = null;
});

var app = builder.Build();

var serviceOptions = app.Services.GetRequiredService<IOptions<KareServiceOptions>>().Value;
GuardBinding(app, serviceOptions);

app.UseMiddleware<NetworkAllowListMiddleware>();
app.UseMiddleware<ApiKeyMiddleware>();
app.MapGet("/health", () => Results.Ok("ok"));
app.MapDashboard();
app.MapOpenAiCompatibleApi();

// Backend selection runs before the listener opens. Kare should fail to start rather than
// accept a request it has no way to serve.
var selection = await app.Services.GetRequiredService<LocalBackendSelector>()
    .SelectAsync(app.Lifetime.ApplicationStopping);

app.Services.GetRequiredService<SelectedBackend>().Set(selection.Backend);

await app.RunAsync();

static long ReadPositiveLong(string key, long defaultValue)
{
    var value = Environment.GetEnvironmentVariable(key);
    if (string.IsNullOrWhiteSpace(value))
    {
        return defaultValue;
    }

    if (long.TryParse(value, out var parsed) && parsed > 0)
    {
        return parsed;
    }

    throw new InvalidOperationException($"{key} must be a positive integer.");
}

static void GuardBinding(WebApplication app, KareServiceOptions options)
{
    var configured = app.Configuration["ASPNETCORE_URLS"] ?? "http://127.0.0.1:5285";
    var addresses = app.Urls.Count > 0
        ? [.. app.Urls]
        : configured.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    foreach (var address in addresses)
    {
        if (Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.IsLoopback)
        {
            continue;
        }

        if (!options.AllowNonLoopbackBinding)
        {
            throw new InvalidOperationException(
                "Kare is configured to listen on a non-loopback address. Set Kare:Service:AllowNonLoopbackBinding " +
                "only after authentication, request limits, and an allow-list are in place.");
        }

        if (string.IsNullOrEmpty(options.ApiKey))
        {
            throw new InvalidOperationException(
                "A non-loopback listener requires Kare:Service:ApiKey. Kare will not serve prompts without authentication.");
        }

        if (options.AllowedNetworks.Count == 0)
        {
            throw new InvalidOperationException(
                "A non-loopback listener requires at least one Kare:Service:AllowedNetworks CIDR.");
        }
    }
}
