using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Quartz;
using Telegram.Bot;
using TradoXBot.Services;

namespace TradoXBot.Jobs;

public class AuthJob : IJob
{
    private readonly ILogger<SellJob> _logger;
    private readonly StoxKartClient _stoxKartClient;
    private readonly TelegramBotClient _telegramBot;

    private readonly string? _chatId;
    public AuthJob(IConfiguration configuration, ILogger<SellJob> logger, StoxKartClient stoxKartClient)
    {
        _logger = logger;
        _stoxKartClient = stoxKartClient;
        _chatId = configuration["Telegram:ChatId"];
        _telegramBot = new TelegramBotClient(configuration["Telegram:ApiKey"] ?? throw new ArgumentNullException(nameof(configuration)));
        // Initialize Polly retry policy
    }

    public async Task Execute(IJobExecutionContext context)
    {
        var istTimeZone = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
        var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, istTimeZone);
        Console.WriteLine(now);
        var status = await _stoxKartClient.AuthenticateAsync();

        if (status == false)
        {
            await _telegramBot.SendMessage(_chatId, "Token Authentication failed!");
        }
    }
}