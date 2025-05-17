using Azure.AI.OpenAI;
using Azure;

namespace DiagnosisApi.Services;

public class AzureOpenAIService
{
    private readonly OpenAIClient _client;
    private readonly string _deployment;

    public AzureOpenAIService()
    {
        var endpoint = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT") ?? "";
        var key = Environment.GetEnvironmentVariable("AZURE_OPENAI_KEY") ?? "";
        _deployment = Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT") ?? "gpt-4";
        if (string.IsNullOrEmpty(endpoint) || string.IsNullOrEmpty(key))
        {
            throw new InvalidOperationException("Azure OpenAI environment variables not set");
        }
        _client = new OpenAIClient(new Uri(endpoint), new AzureKeyCredential(key));
    }

    public async Task<string> GetChatCompletionAsync(
        IEnumerable<ChatMessage> messages,
        float temperature = 0.2f,
        IEnumerable<ChatCompletionsToolDefinition>? tools = null,
        string? functionName = null)
    {
        var options = new ChatCompletionsOptions();
        foreach (var m in messages)
        {
            options.Messages.Add(m);
        }
        options.Temperature = temperature;
        if (tools != null)
        {
            foreach (var t in tools)
            {
                options.Tools.Add(t);
            }
            if (!string.IsNullOrEmpty(functionName))
            {
                options.ToolChoice = ChatCompletionsToolChoice.FromFunctionName(functionName);
            }
        }
        var resp = await _client.GetChatCompletionsAsync(_deployment, options);
        var msg = resp.Value.Choices[0].Message;
        return msg.ToolCalls.Count > 0 ? msg.ToolCalls[0].Function.Arguments : msg.Content;
    }
}
