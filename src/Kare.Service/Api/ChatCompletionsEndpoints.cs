using System.Text;
using System.Text.Json;
using Kare.Abstractions;
using Kare.Core;
using Kare.Service.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kare.Service.Api;

/// <summary>
/// The OpenAI compatible surface. This exists because Copilot CLI BYOK points at an
/// OpenAI compatible base URL, and that is the documented way to route Copilot CLI model
/// calls through Kare. Streaming and tool calling are requirements of that route, not extras.
/// </summary>
public static class ChatCompletionsEndpoints
{
    private static readonly byte[] DoneEvent = "data: [DONE]\n\n"u8.ToArray();

    /// <summary>Maps the OpenAI compatible endpoints.</summary>
    public static IEndpointRouteBuilder MapOpenAiCompatibleApi(this IEndpointRouteBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.MapPost("/v1/chat/completions", HandleChatCompletionAsync);
        builder.MapGet("/v1/models", HandleListModels);

        return builder;
    }

    private static IResult HandleListModels(IOptions<KareServiceOptions> options)
    {
        var list = new ModelListResponse
        {
            Data =
            [
                new ModelDescription
                {
                    Id = options.Value.ModelId,
                    Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                },
            ],
        };

        return Results.Json(list, OpenAiJsonContext.Default.ModelListResponse);
    }

    private static async Task HandleChatCompletionAsync(
        HttpContext context,
        IChatClient chatClient,
        IRouteSelector routeSelector,
        IOptions<KareServiceOptions> serviceOptions,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("Kare.Service.ChatCompletions");
        var modelId = serviceOptions.Value.ModelId;

        ChatCompletionRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync(
                context.Request.Body,
                OpenAiJsonContext.Default.ChatCompletionRequest,
                context.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, ex.Message, "invalid_json").ConfigureAwait(false);
            return;
        }

        if (request is null)
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "Request body is empty.", "empty_body").ConfigureAwait(false);
            return;
        }

        List<ChatMessage> messages;
        ChatOptions chatOptions;
        try
        {
            messages = OpenAiTranslator.ToChatMessages(request.Messages);
            chatOptions = OpenAiTranslator.ToChatOptions(request, modelId);
        }
        catch (InvalidRequestException ex)
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, ex.Message, ex.Code).ConfigureAwait(false);
            return;
        }

        var decision = await routeSelector
            .SelectAsync(messages, chatOptions, context.RequestAborted)
            .ConfigureAwait(false);

        logger.LogInformation(
            "Serving chat completion on route {Route} backend {Backend} billable {Billable} stream {Stream}.",
            decision.Route,
            decision.Backend,
            decision.IsBillable,
            request.Stream);

        var responseId = "chatcmpl-" + Guid.NewGuid().ToString("N");

        try
        {
            if (request.Stream)
            {
                await StreamAsync(context, chatClient, messages, chatOptions, responseId, modelId, decision)
                    .ConfigureAwait(false);
            }
            else
            {
                await CompleteAsync(context, chatClient, messages, chatOptions, responseId, modelId, decision)
                    .ConfigureAwait(false);
            }
        }
        catch (PromptTooLargeException ex)
        {
            await WriteErrorAsync(context, StatusCodes.Status413PayloadTooLarge, ex.Message, "prompt_too_large").ConfigureAwait(false);
        }
        catch (InferenceCapacityException ex)
        {
            context.Response.Headers.RetryAfter = "5";
            await WriteErrorAsync(context, StatusCodes.Status429TooManyRequests, ex.Message, "capacity_exhausted").ConfigureAwait(false);
        }
        catch (NoBackendAvailableException ex)
        {
            await WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable, ex.Message, "no_backend").ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            logger.LogInformation("Client cancelled the request on route {Route}.", decision.Route);
        }
    }

    private static async Task CompleteAsync(
        HttpContext context,
        IChatClient chatClient,
        List<ChatMessage> messages,
        ChatOptions chatOptions,
        string responseId,
        string modelId,
        RouteDecision decision)
    {
        var response = await chatClient
            .GetResponseAsync(messages, chatOptions, context.RequestAborted)
            .ConfigureAwait(false);

        var payload = OpenAiTranslator.ToCompletionResponse(response, responseId, modelId, decision);

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";

        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            payload,
            OpenAiJsonContext.Default.ChatCompletionResponse,
            context.RequestAborted).ConfigureAwait(false);
    }

    private static async Task StreamAsync(
        HttpContext context,
        IChatClient chatClient,
        List<ChatMessage> messages,
        ChatOptions chatOptions,
        string responseId,
        string modelId,
        RouteDecision decision)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        // Buffering a token stream defeats the point of streaming, and Copilot CLI reads
        // these events as they arrive.
        context.Response.Headers.Append("X-Accel-Buffering", "no");

        var isFirst = true;
        var toolCallIndex = 0;
        string? finishReason = null;
        ChatCompletionUsage? usage = null;

        var stream = chatClient.GetStreamingResponseAsync(messages, chatOptions, context.RequestAborted);
        await foreach (var update in stream.ConfigureAwait(false))
        {
            var chunk = OpenAiTranslator.ToChunk(update, responseId, modelId, isFirst, ref toolCallIndex);
            if (chunk is null)
            {
                continue;
            }

            usage ??= chunk.Usage;
            chunk.Usage = null;

            foreach (var choice in chunk.Choices)
            {
                finishReason ??= choice.FinishReason;
                choice.FinishReason = null;
            }

            var hasPayload = chunk.Choices.Any(static choice =>
                choice.Delta is
                {
                    Role: not null,
                } or
                {
                    Content: not null,
                } or
                {
                    ToolCalls.Count: > 0,
                });

            if (!hasPayload && !isFirst)
            {
                continue;
            }

            isFirst = false;
            await WriteEventAsync(context, chunk).ConfigureAwait(false);
        }

        // Terminal metadata goes out after all text and tool deltas. Some providers report
        // finish_reason before their tool-call update, which strict OpenAI clients reject.
        // Holding it here normalizes the order without changing generated content.
        var trailer = new ChatCompletionChunk
        {
            Id = responseId,
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Model = modelId,
            Choices =
            [
                new ChatCompletionChunkChoice
                {
                    Index = 0,
                    Delta = new ChatCompletionDelta(),
                    FinishReason = finishReason ?? "stop",
                },
            ],
            Usage = usage,
            KareRoute = OpenAiTranslator.ToRouteEnvelope(decision),
        };

        await WriteEventAsync(context, trailer).ConfigureAwait(false);
        await context.Response.Body.WriteAsync(DoneEvent, context.RequestAborted).ConfigureAwait(false);
        await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
    }

    private static async Task WriteEventAsync(HttpContext context, ChatCompletionChunk chunk)
    {
        var json = JsonSerializer.Serialize(chunk, OpenAiJsonContext.Default.ChatCompletionChunk);
        var payload = Encoding.UTF8.GetBytes("data: " + json + "\n\n");

        await context.Response.Body.WriteAsync(payload, context.RequestAborted).ConfigureAwait(false);
        await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
    }

    private static async Task WriteErrorAsync(HttpContext context, int statusCode, string message, string code)
    {
        if (context.Response.HasStarted)
        {
            // The response is already on the wire. Aborting is the only honest option left,
            // because a partial stream must not be followed by a success shaped body.
            context.Abort();
            return;
        }

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";

        var payload = new ErrorResponse
        {
            Error = new ErrorBody
            {
                Message = message,
                Code = code,
                Type = statusCode >= 500 ? "server_error" : "invalid_request_error",
            },
        };

        await JsonSerializer.SerializeAsync(
            context.Response.Body,
            payload,
            OpenAiJsonContext.Default.ErrorResponse,
            context.RequestAborted).ConfigureAwait(false);
    }
}
