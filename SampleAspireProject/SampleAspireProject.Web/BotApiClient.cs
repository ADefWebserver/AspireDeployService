namespace SampleAspireProject.Web;

public class BotApiClient(HttpClient httpClient)
{
    public async Task<BotReply> AskAsync(string message, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync("/bot", new BotRequest(message), cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<BotReply>(cancellationToken) ?? new BotReply("(no reply)", DateTimeOffset.UtcNow);
    }
}

public record BotRequest(string Message);

public record BotReply(string Reply, DateTimeOffset At);
