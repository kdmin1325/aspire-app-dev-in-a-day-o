using Aliencube.YouTubeSubtitlesExtractor;
using Aliencube.YouTubeSubtitlesExtractor.Abstractions;
using Aliencube.YouTubeSubtitlesExtractor.Models;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.AspNetCore.Mvc;
using OpenAI.Chat;

var builder = WebApplication.CreateBuilder(args);

// 서비스 기본 설정 추가
builder.AddServiceDefaults();

// HTTP 클라이언트 및 OpenAI 클라이언트 설정
builder.Services.AddHttpClient<IYouTubeVideo, YouTubeVideo>();
builder.Services.AddScoped<AzureOpenAIClient>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var endpoint = new Uri(config["OpenAI:Endpoint"]);
    var credential = new AzureKeyCredential(config["OpenAI:ApiKey"]);
    var client = new AzureOpenAIClient(endpoint, credential);
    return client;
});

builder.Services.AddScoped<YouTubeSummariserService>();

// Swagger/OpenAPI 설정
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.MapDefaultEndpoints();

// 개발 환경에서 Swagger 사용 설정
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

if (app.Environment.IsDevelopment())
{
    app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();
}

// 날씨 예보 API 설정
var summaries = new[]
{
    "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
};

app.MapGet("/weatherforecast", () =>
{
    var forecast = Enumerable.Range(1, 5).Select(index =>
        new WeatherForecast
        (
            DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
            Random.Shared.Next(-20, 55),
            summaries[Random.Shared.Next(summaries.Length)]
        ))
        .ToArray();
    return forecast;
})
.WithName("GetWeatherForecast")
.WithOpenApi();

// YouTube 영상 요약 API 설정
app.MapPost("/summarise", async ([FromBody] SummaryRequest req, YouTubeSummariserService service) =>
{
    if (req == null)
    {
        return Results.BadRequest("Request cannot be null");
    }

    var summary = await service.SummariseAsync(req);
    return summary;
})
.WithName("GetSummary")
.WithOpenApi();

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}

record SummaryRequest(string? YouTubeLinkUrl, string VideoLanguageCode, string? SummaryLanguageCode);

internal class YouTubeSummariserService
{
    private readonly IYouTubeVideo _youtube;
    private readonly AzureOpenAIClient _openai;
    private readonly IConfiguration _config;

    public YouTubeSummariserService(IYouTubeVideo youtube, AzureOpenAIClient openai, IConfiguration config)
    {
        _youtube = youtube ?? throw new ArgumentNullException(nameof(youtube));
        _openai = openai ?? throw new ArgumentNullException(nameof(openai));
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public async Task<string> SummariseAsync(SummaryRequest req)
    {
        if (req == null)
        {
            throw new ArgumentNullException(nameof(req), "Request cannot be null");
        }

        if (string.IsNullOrWhiteSpace(req.YouTubeLinkUrl))
        {
            throw new ArgumentException("YouTubeLinkUrl cannot be null or empty", nameof(req.YouTubeLinkUrl));
        }

        if (string.IsNullOrWhiteSpace(req.VideoLanguageCode))
        {
            throw new ArgumentException("VideoLanguageCode cannot be null or empty", nameof(req.VideoLanguageCode));
        }

        if (string.IsNullOrWhiteSpace(req.SummaryLanguageCode))
        {
            throw new ArgumentException("SummaryLanguageCode cannot be null or empty", nameof(req.SummaryLanguageCode));
        }

        // 자막 추출
        Subtitle subtitle = await _youtube.ExtractSubtitleAsync(req.YouTubeLinkUrl, req.VideoLanguageCode).ConfigureAwait(false);

        if (subtitle == null || subtitle.Content == null || !subtitle.Content.Any())
        {
            throw new InvalidOperationException("Subtitle content is empty or null.");
        }

        // 자막 내용 생성
        string caption = string.Join("\n", subtitle.Content.Select(p => p.Text));

        // OpenAI 클라이언트
        var chat = _openai.GetChatClient(_config["OpenAI:DeploymentName"]);

        var messages = new List<ChatMessage>()
        {
            new SystemChatMessage(_config["Prompt:System"]),
            new SystemChatMessage($"Here's the transcript. Summarise it in 5 bullet point items in the given language code of \"{req.SummaryLanguageCode}\"."),
            new UserChatMessage(caption),
        };

        var options = new ChatCompletionOptions
        {
            MaxTokens = int.TryParse(_config["Prompt:MaxTokens"], out var maxTokens) ? maxTokens : 3000,
            Temperature = float.TryParse(_config["Prompt:Temperature"], out var temperature) ? temperature : 0.7f,
        };

        var response = await chat.CompleteChatAsync(messages, options).ConfigureAwait(false);

        if (response == null || response.Value.Content == null || !response.Value.Content.Any())
        {
            throw new InvalidOperationException("Chat response is empty or null.");
        }

        var summary = response.Value.Content[0].Text;
        return summary;
    }
}
