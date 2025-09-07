using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quartz;
using Quartz.Impl;
using TradoXBot.Jobs;

namespace TradoXBot.Services;

public class TradingService : IHostedService
{
    private readonly ISchedulerFactory _schedulerFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<TradingService> _logger;
    private IScheduler? _scheduler;

    public TradingService(ISchedulerFactory schedulerFactory, IConfiguration configuration, ILogger<TradingService> logger)
    {
        _schedulerFactory = schedulerFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting Trading Service...");
        _scheduler = await _schedulerFactory.GetScheduler();

        // Schedule BuyJob (3:25 PM IST daily)
        IJobDetail buyJob = JobBuilder.Create<BuyJob>()
            .WithIdentity("BuyJob", "Trading")
            .Build();
        ITrigger buyTrigger = TriggerBuilder.Create()
            .WithIdentity("BuyTrigger", "Trading")
            .WithDailyTimeIntervalSchedule(s => s
                .StartingDailyAt(TimeOfDay.HourAndMinuteOfDay(15, 25))
                .InTimeZone(TimeZoneInfo.FindSystemTimeZoneById("India Standard Time")))
            .Build();
        await _scheduler.ScheduleJob(buyJob, buyTrigger, cancellationToken);

        // Schedule SellJob (3:15 PM IST daily)
        IJobDetail sellJob = JobBuilder.Create<SellJob>()
            .WithIdentity("SellJob", "Trading")
            .Build();
        ITrigger sellTrigger = TriggerBuilder.Create()
            .WithIdentity("SellTrigger", "Trading")
            .WithDailyTimeIntervalSchedule(s => s
                .StartingDailyAt(TimeOfDay.HourAndMinuteOfDay(15, 15))
                .InTimeZone(TimeZoneInfo.FindSystemTimeZoneById("India Standard Time")))
            .Build();
        await _scheduler.ScheduleJob(sellJob, sellTrigger, cancellationToken);

        // Schedule ScalpingMonitorJob (every 5 min, 9:15 AM - 2:30 PM IST)
        IJobDetail scalpingJob = JobBuilder.Create<ScalpingMonitorJob>()
            .WithIdentity("ScalpingMonitorJob", "Trading")
            .Build();
        ITrigger scalpingTrigger = TriggerBuilder.Create()
            .WithIdentity("ScalpingTrigger", "Trading")
            .WithSchedule(SimpleScheduleBuilder.Create()
                .WithIntervalInMinutes(5)
                .RepeatForever())
            .StartAt(DateTime.Today.Add(new TimeSpan(9, 15, 0)))
            .EndAt(DateTime.Today.Add(new TimeSpan(14, 30, 0)))
            .WithDailyTimeIntervalSchedule(s => s
                .OnEveryDay()
                .StartingDailyAt(TimeOfDay.HourAndMinuteOfDay(9, 15))
                .EndingDailyAt(TimeOfDay.HourAndMinuteOfDay(14, 30))
                .InTimeZone(TimeZoneInfo.FindSystemTimeZoneById("India Standard Time")))
            .Build();
        await _scheduler.ScheduleJob(scalpingJob, scalpingTrigger, cancellationToken);

        // Schedule StatusJob (hourly during market hours)
        IJobDetail statusJob = JobBuilder.Create<StatusJob>()
            .WithIdentity("StatusJob", "Trading")
            .Build();
        ITrigger statusTrigger = TriggerBuilder.Create()
            .WithIdentity("StatusTrigger", "Trading")
            .WithDailyTimeIntervalSchedule(s => s
                .WithIntervalInHours(1)
                .OnEveryDay()
                .StartingDailyAt(TimeOfDay.HourAndMinuteOfDay(9, 0))
                .EndingDailyAt(TimeOfDay.HourAndMinuteOfDay(15, 30))
                .InTimeZone(TimeZoneInfo.FindSystemTimeZoneById("India Standard Time")))
            .Build();
        await _scheduler.ScheduleJob(statusJob, statusTrigger, cancellationToken);

        await _scheduler.Start(cancellationToken);
        _logger.LogInformation("Trading Service started with scheduled jobs.");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_scheduler != null)
        {
            await _scheduler.Shutdown(cancellationToken);
            _logger.LogInformation("Trading Service stopped.");
        }
    }
}