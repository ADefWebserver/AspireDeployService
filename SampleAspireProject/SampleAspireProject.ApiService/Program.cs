var builder = WebApplication.CreateBuilder(args);

// Add service defaults & Aspire client integrations.
builder.AddServiceDefaults();

// Add services to the container.
builder.Services.AddProblemDetails();

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

string[] summaries = ["Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"];

app.MapGet("/", () => "API service is running. Navigate to /weatherforecast to see sample data.");

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
.WithName("GetWeatherForecast");

// A tiny rule-based bot so the web front end has something to talk to.
app.MapPost("/bot", (BotRequest request) =>
{
    var text = (request.Message ?? "").Trim();
    var lower = text.ToLowerInvariant();
    var reply = lower switch
    {
        "" => "Say something and I will answer.",
        _ when lower.Contains("hello") || lower.Contains("hi") => "Hello! I am the sample API bot running in a container.",
        _ when lower.Contains("time") => $"The server time is {DateTimeOffset.UtcNow:u}.",
        _ when lower.Contains("weather") => $"It is {summaries[Random.Shared.Next(summaries.Length)].ToLowerInvariant()} here, {Random.Shared.Next(-5, 35)} °C.",
        _ when lower.Contains("where") => $"I am running on host {Environment.MachineName}.",
        _ => $"You said \"{text}\". Try asking about the time, the weather, or where I run."
    };
    return new BotReply(reply, DateTimeOffset.UtcNow);
})
.WithName("Bot");

app.MapDefaultEndpoints();

app.Run();

record BotRequest(string? Message);

record BotReply(string Reply, DateTimeOffset At);

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
