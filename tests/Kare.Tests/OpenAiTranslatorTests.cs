using System.Text.Json;
using Kare.Abstractions;
using Kare.Service.Api;
using Microsoft.Extensions.AI;
using Xunit;

namespace Kare.Tests;

public sealed class OpenAiTranslatorTests
{
    private static ChatCompletionRequest Parse(string json) =>
        JsonSerializer.Deserialize(json, OpenAiJsonContext.Default.ChatCompletionRequest)!;

    [Fact]
    public void ReadsStringContent()
    {
        var request = Parse("""{"messages":[{"role":"user","content":"hello"}]}""");

        Assert.Equal("hello", request.Messages[0].Content);
    }

    [Fact]
    public void ReadsArrayContentParts()
    {
        // Copilot CLI sends the array form, so this is the shape that actually matters.
        var request = Parse(
            """{"messages":[{"role":"user","content":[{"type":"text","text":"ab"},{"type":"text","text":"cd"}]}]}""");

        Assert.Equal("abcd", request.Messages[0].Content);
    }

    [Fact]
    public void RejectsNonTextContentPartsInsteadOfDroppingThem()
    {
        var json =
            """{"messages":[{"role":"user","content":[{"type":"image_url","image_url":{"url":"x"}}]}]}""";

        Assert.Throws<JsonException>(() => Parse(json));
    }

    [Fact]
    public void ReadsStopAsStringOrArray()
    {
        Assert.Equal(["x"], Parse("""{"messages":[],"stop":"x"}""").Stop);
        Assert.Equal(["x", "y"], Parse("""{"messages":[],"stop":["x","y"]}""").Stop);
        Assert.Null(Parse("""{"messages":[]}""").Stop);
    }

    [Fact]
    public void MapsRolesToChatRoles()
    {
        var request = Parse(
            """
            {"messages":[
              {"role":"system","content":"s"},
              {"role":"developer","content":"d"},
              {"role":"user","content":"u"},
              {"role":"assistant","content":"a"}
            ]}
            """);

        var messages = OpenAiTranslator.ToChatMessages(request.Messages);

        Assert.Equal(ChatRole.System, messages[0].Role);
        Assert.Equal(ChatRole.System, messages[1].Role);
        Assert.Equal(ChatRole.User, messages[2].Role);
        Assert.Equal(ChatRole.Assistant, messages[3].Role);
    }

    [Fact]
    public void RejectsUnknownRole()
    {
        var request = Parse("""{"messages":[{"role":"function","content":"x"}]}""");

        var error = Assert.Throws<InvalidRequestException>(
            () => OpenAiTranslator.ToChatMessages(request.Messages));

        Assert.Equal("unsupported_role", error.Code);
    }

    [Fact]
    public void RoundTripsAToolCallAndItsResult()
    {
        var request = Parse(
            """
            {"messages":[
              {"role":"assistant","content":null,"tool_calls":[
                {"id":"call_1","type":"function","function":{"name":"read_file","arguments":"{\"path\":\"a.cs\",\"lines\":10}"}}]},
              {"role":"tool","tool_call_id":"call_1","content":"file body"}
            ]}
            """);

        var messages = OpenAiTranslator.ToChatMessages(request.Messages);

        var call = Assert.IsType<FunctionCallContent>(Assert.Single(messages[0].Contents));
        Assert.Equal("call_1", call.CallId);
        Assert.Equal("read_file", call.Name);
        Assert.Equal("a.cs", ((JsonElement)call.Arguments!["path"]!).GetString());
        Assert.Equal(10, ((JsonElement)call.Arguments["lines"]!).GetInt32());

        var result = Assert.IsType<FunctionResultContent>(Assert.Single(messages[1].Contents));
        Assert.Equal("call_1", result.CallId);
    }

    [Fact]
    public void RejectsToolMessageWithoutCallId()
    {
        var request = Parse("""{"messages":[{"role":"tool","content":"x"}]}""");

        var error = Assert.Throws<InvalidRequestException>(
            () => OpenAiTranslator.ToChatMessages(request.Messages));

        Assert.Equal("missing_tool_call_id", error.Code);
    }

    [Fact]
    public void DeclaresToolsWithoutExecutingThem()
    {
        var request = Parse(
            """
            {"messages":[{"role":"user","content":"x"}],
             "tools":[{"type":"function","function":{
               "name":"run_tests","description":"Runs tests",
               "parameters":{"type":"object","properties":{"filter":{"type":"string"}}}}}],
             "tool_choice":"auto"}
            """);

        var options = OpenAiTranslator.ToChatOptions(request, "kare-local");

        var tool = Assert.Single(options.Tools!);
        var declaration = Assert.IsAssignableFrom<AIFunctionDeclaration>(tool);
        Assert.Equal("run_tests", declaration.Name);
        Assert.Equal("Runs tests", declaration.Description);
        Assert.Equal(JsonValueKind.Object, declaration.JsonSchema.ValueKind);

        // A declaration is not an invocable function. Kare must never run a caller's tool.
        Assert.IsNotType<AIFunction>(tool, exactMatch: false);
        Assert.Equal(ChatToolMode.Auto, options.ToolMode);
    }

    [Fact]
    public void MapsNamedToolChoice()
    {
        var request = Parse(
            """{"messages":[],"tool_choice":{"type":"function","function":{"name":"run_tests"}}}""");

        var options = OpenAiTranslator.ToChatOptions(request, "kare-local");

        var required = Assert.IsType<RequiredChatToolMode>(options.ToolMode);
        Assert.Equal("run_tests", required.RequiredFunctionName);
    }

    [Fact]
    public void PrefersMaxCompletionTokensOverMaxTokens()
    {
        var request = Parse("""{"messages":[],"max_tokens":100,"max_completion_tokens":50}""");

        Assert.Equal(50, OpenAiTranslator.ToChatOptions(request, "kare-local").MaxOutputTokens);
    }

    [Fact]
    public void ReportsToolCallsAsTheFinishReason()
    {
        var response = new ChatResponse(
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("c1", "run_tests", null)]))
        {
            FinishReason = ChatFinishReason.Stop,
        };

        var decision = new RouteDecision(
            KareRoute.LocalSlm, "test", "kare-local", BackendKind.OnnxGenAiCpu, IsBillable: false);

        var payload = OpenAiTranslator.ToCompletionResponse(response, "id", "kare-local", decision);

        Assert.Equal("tool_calls", payload.Choices[0].FinishReason);
        Assert.Equal("c1", payload.Choices[0].Message!.ToolCalls![0].Id);
        Assert.Equal("{}", payload.Choices[0].Message!.ToolCalls![0].Function!.Arguments);
    }

    [Fact]
    public void EveryResponseDisclosesItsRoute()
    {
        var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, "hi"));
        var decision = new RouteDecision(
            KareRoute.LocalSlm,
            "local only",
            "kare-local",
            BackendKind.OnnxGenAiCpu,
            IsBillable: false,
            FellBackFrom: KareRoute.CopilotLight);

        var payload = OpenAiTranslator.ToCompletionResponse(response, "id", "kare-local", decision);

        Assert.Equal("LocalSlm", payload.KareRoute!.Route);
        Assert.Equal("OnnxGenAiCpu", payload.KareRoute.Backend);
        Assert.False(payload.KareRoute.Billable);
        Assert.Equal("CopilotLight", payload.KareRoute.FellBackFrom);
    }

    [Fact]
    public void FirstChunkCarriesTheAssistantRoleAndEmptyChunksAreDropped()
    {
        var index = 0;
        var first = OpenAiTranslator.ToChunk(
            new ChatResponseUpdate(ChatRole.Assistant, "he"), "id", "m", isFirst: true, ref index);
        var second = OpenAiTranslator.ToChunk(
            new ChatResponseUpdate(ChatRole.Assistant, "llo"), "id", "m", isFirst: false, ref index);
        var empty = OpenAiTranslator.ToChunk(
            new ChatResponseUpdate(ChatRole.Assistant, string.Empty), "id", "m", isFirst: false, ref index);

        Assert.Equal("assistant", first!.Choices[0].Delta!.Role);
        Assert.Equal("he", first.Choices[0].Delta!.Content);
        Assert.Null(second!.Choices[0].Delta!.Role);
        Assert.Equal("llo", second.Choices[0].Delta!.Content);
        Assert.Null(empty);
    }

    [Fact]
    public void StreamedUsageIsForwardedWhenTheBackendReportsIt()
    {
        var index = 0;
        var update = new ChatResponseUpdate(ChatRole.Assistant,
            [new UsageContent(new UsageDetails { InputTokenCount = 12, OutputTokenCount = 34 })]);

        var chunk = OpenAiTranslator.ToChunk(update, "id", "m", isFirst: false, ref index);

        Assert.Equal(12, chunk!.Usage!.PromptTokens);
        Assert.Equal(34, chunk.Usage.CompletionTokens);
        Assert.Equal(46, chunk.Usage.TotalTokens);
    }

    [Fact]
    public void ToolArgumentsSurviveTheRoundTrip()
    {
        const string Arguments = """{"path":"a.cs","depth":3,"deep":{"nested":[1,2]},"flag":true}""";

        var parsed = ToolArgumentJson.Parse(Arguments);
        var serialized = ToolArgumentJson.Serialize(parsed);

        using var original = JsonDocument.Parse(Arguments);
        using var round = JsonDocument.Parse(serialized);

        Assert.Equal(
            original.RootElement.GetProperty("deep").GetRawText(),
            round.RootElement.GetProperty("deep").GetRawText());
        Assert.Equal(3, round.RootElement.GetProperty("depth").GetInt32());
        Assert.True(round.RootElement.GetProperty("flag").GetBoolean());
    }

    [Fact]
    public void RefusesToSerializeToolArgumentsItCannotRepresent()
    {
        var arguments = new Dictionary<string, object?> { ["when"] = DateTime.UnixEpoch };

        Assert.Throws<NotSupportedException>(() => ToolArgumentJson.Serialize(arguments));
    }
}
