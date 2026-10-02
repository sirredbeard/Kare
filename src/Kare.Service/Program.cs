using Kare.Abstractions;
using Kare.Core.Inference;
using Kare.Core.Options;
using Kare.Core.Routing;
using Kare.Inference.OnnxGenAI;
using Kare.Service;
using Kare.Service.Api;
using Kare.Service.Options;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, OpenAiJsonContext.Default));

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
    .AddOptions<OnnxGenAiOptions>()
    .Bind(builder.Configuration.GetSection(OnnxGenAiOptions.SectionName))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<OnnxGenAiOptions>, OnnxGenAiOptionsValidator>();

builder.Services.AddSingleton<InferenceGate>();
builder.Services.AddSingleton<IRouteRecorder, MetricsRouteRecorder>();
builder.Services.AddSingleton<SelectedBackend>();

// The CPU backend is registered unconditionally and at the lowest priority so it stays
// the fallback. Accelerator backends are added above it once they probe successfully.
builder.Services.AddSingleton<ILocalInferenceBackend, OnnxGenAiBackend>();
builder.Services.AddSingleton<LocalBackendSelector>();

builder.Services.AddSingleton<IRouteSelector>(sp =>
{
    var selected = sp.GetRequiredService<SelectedBackend>();
    return new LocalOnlyRouteSelector(selected.Kind, selected.ModelId);
});

builder.Services.AddSingleton<IChatClient>(sp =>
{
    var selected = sp.GetRequiredService<SelectedBackend>();
    var gate = sp.GetRequiredService<InferenceGate>();
    var limits = sp.GetRequiredService<IOptions<InferenceLimits>>();
    var recorder = sp.GetRequiredService<IRouteRecorder>();
    var selector = sp.GetRequiredService<IRouteSelector>();
    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();

    // Bounds sit closest to the model and recording wraps the whole call, so a request
    // rejected by a bound costs nothing and a served request is always counted.
    var task = selector.SelectAsync([], null, CancellationToken.None);
    var decision = task.IsCompletedSuccessfully
        ? task.Result
        : task.AsTask().GetAwaiter().GetResult();

    IChatClient bounded = new BoundedChatClient(selected.Backend, gate, limits);
    return new RouteRecordingChatClient(
        bounded,
        decision,
        recorder,
        loggerFactory.CreateLogger<RouteRecordingChatClient>());
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

app.UseMiddleware<ApiKeyMiddleware>();
app.MapGet("/health", () => Results.Ok("ok"));
app.MapOpenAiCompatibleApi();

// Backend selection runs before the listener opens. Kare should fail to start rather than
// accept a request it has no way to serve.
var selection = await app.Services.GetRequiredService<LocalBackendSelector>()
    .SelectAsync(app.Lifetime.ApplicationStopping);

app.Services.GetRequiredService<SelectedBackend>().Set(selection.Backend);

await app.RunAsync();

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
    }
}
