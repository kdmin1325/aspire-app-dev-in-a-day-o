using Aliencube.YouTubeSubtitlesExtractor;
using Aliencube.YouTubeSubtitlesExtractor.Abstractions;
using Aliencube.YouTubeSubtitlesExtractor.Models;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenAI.Chat;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

var builder = WebApplication.CreateBuilder(args);

// 서비스 기본 설정 추가
builder.AddServiceDefaults();

// HTTP 클라이언트 및 OpenAI 클라이언트 설정
builder.Services.AddHttpClient<IYouTubeVideo, YouTubeVideo>();
builder.Services.AddScoped<AzureOpenAIClient>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var endpoint = new Uri(config["OpenAI:Endpoint"] ?? throw new ArgumentNullException("OpenAI:Endpoint"));
    var credential = new AzureKeyCredential(config["OpenAI:ApiKey"] ?? throw new ArgumentNullException("OpenAI:ApiKey"));
    var client = new AzureOpenAIClient(endpoint, credential);
    return client;
});

builder.Services.AddScoped<YouTubeSummariserService>();

// Swagger/OpenAPI 설정
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.UseHttpsRedirection();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();

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

app.MapPost("/summarise", async (HttpContext httpContext) =>
{
    var service = httpContext.RequestServices.GetRequiredService<YouTubeSummariserService>();
    var req = await httpContext.Request.ReadFromJsonAsync<SummaryRequest>();

    if (req == null)
    {
        return Results.BadRequest("Request cannot be null");
    }

    var summary = await service.SummariseAsync(req);
    return Results.Ok(summary);
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

        Subtitle subtitle;
        try
        {
            subtitle = await _youtube.ExtractSubtitleAsync(req.YouTubeLinkUrl, req.VideoLanguageCode).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to extract subtitles.", ex);
        }

        if (subtitle == null || subtitle.Content == null || !subtitle.Content.Any())
        {
            throw new InvalidOperationException("Subtitle content is empty or null.");
        }

        string caption = string.Join("\n", subtitle.Content.Select(p => p.Text));

        var chat = _openai.GetChatClient(_config["OpenAI:DeploymentName"]);

        if (chat == null)
        {
            throw new InvalidOperationException("Failed to create ChatClient.");
        }

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

        if (response == null || response.Value == null || response.Value.Content == null || !response.Value.Content.Any())
        {
            throw new InvalidOperationException("Chat response is empty or null.");
        }

        var summary = response.Value.Content[0].Text;
        return summary;
    }
}
