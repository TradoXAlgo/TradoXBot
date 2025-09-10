using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quartz;
using Quartz.Impl;
using TradoXBot.Jobs;
using TradoXBot.Services;

namespace TradoXBot
{
    class Program
    {
        [Obsolete]
        static async Task Main(string[] args)
        {

            // Build configuration
            IConfiguration configuration = new ConfigurationBuilder()
                .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
                .AddEnvironmentVariables()
                .Build();

            // Create host
            var builder = Host.CreateDefaultBuilder(args)
                .ConfigureServices((hostContext, services) =>
                {
                    var configuration = hostContext.Configuration;

                    services.AddSingleton<IConfiguration>(configuration);
                    services.AddLogging(builder =>
                    {
                        builder.AddConsole();
                        builder.AddConfiguration(configuration.GetSection("Logging"));
                    });

                    // Register services
                    services.AddSingleton<IConfiguration>(configuration);
                    services.AddSingleton<StoxKartClient>();
                    services.AddSingleton<ChartinkScraper>();
                    services.AddSingleton<HistoricalDataFetcher>();
                    services.AddSingleton<MongoDbService>();
                    services.AddSingleton<BacktestService>();
                    services.AddSingleton<TradingOperations>();
                    services.AddSingleton<ISchedulerFactory, StdSchedulerFactory>();

                    // Configure Quartz scheduler
                    services.AddQuartz(q =>
                    {
                        q.UseMicrosoftDependencyInjectionJobFactory();

                        // BuyJob (Swing, 3:25 PM IST)
                        var buyJobKey = new JobKey("BuyJob", "Trading");
                        q.AddJob<BuyJob>(opts => opts.WithIdentity(buyJobKey));
                        q.AddTrigger(opts => opts
                            .ForJob(buyJobKey)
                            .WithIdentity("BuyTrigger")
                            .WithDailyTimeIntervalSchedule(s => s
                                .StartingDailyAt(TimeOfDay.HourAndMinuteOfDay(15, 25))
                                .EndingDailyAt(TimeOfDay.HourAndMinuteOfDay(15, 25))
                                .InTimeZone(TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"))
                                .OnDaysOfTheWeek(DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
                                        DayOfWeek.Thursday, DayOfWeek.Friday)));

                        // ScalpingMonitorJob (every 5 minutes, 9:15 AM–2:30 PM IST)
                        var scalpingJobKey = new JobKey("ScalpingMonitorJob", "Trading");
                        q.AddJob<ScalpingBuyJob>(opts => opts.WithIdentity(scalpingJobKey));
                        q.AddTrigger(opts => opts
                            .ForJob(scalpingJobKey)
                            .WithIdentity("ScalpingBuyTrigger")
                            .WithDailyTimeIntervalSchedule(s => s
                                .StartingDailyAt(TimeOfDay.HourAndMinuteOfDay(9, 15))
                                .EndingDailyAt(TimeOfDay.HourAndMinuteOfDay(14, 30))
                                .InTimeZone(TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"))
                                .OnDaysOfTheWeek(DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
                                        DayOfWeek.Thursday, DayOfWeek.Friday)
                                .WithIntervalInMinutes(5)));


                        // ScalpingMonitorJob (every 5 minutes, 9:15 AM–2:30 PM IST)
                        var scalpingSellJobKey = new JobKey("ScalpingSellJob", "Trading");
                        q.AddJob<ScalpingSelJob>(opts => opts.WithIdentity(scalpingSellJobKey));
                        q.AddTrigger(opts => opts
                            .ForJob(scalpingSellJobKey)
                            .WithIdentity("ScalpingSellTrigger")
                            .WithDailyTimeIntervalSchedule(s => s
                                .StartingDailyAt(TimeOfDay.HourAndMinuteOfDay(9, 15))
                                .EndingDailyAt(TimeOfDay.HourAndMinuteOfDay(14, 30))
                                .InTimeZone(TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"))
                                .OnDaysOfTheWeek(DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
                                        DayOfWeek.Thursday, DayOfWeek.Friday)
                                .WithIntervalInMinutes(5)));


                        // SellJob (Swing, every 15 minutes, 9:15 AM–3:30 PM IST)
                        var sellJobKey = new JobKey("SellJob", "Trading");
                        q.AddJob<SellJob>(opts => opts.WithIdentity(sellJobKey));
                        q.AddTrigger(opts => opts
                            .ForJob(sellJobKey)
                            .WithIdentity("SellTriggerQuat")
                            .WithDailyTimeIntervalSchedule(s => s
                                .StartingDailyAt(TimeOfDay.HourAndMinuteOfDay(9, 15))
                                .EndingDailyAt(TimeOfDay.HourAndMinuteOfDay(15, 20))
                                .InTimeZone(TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"))
                                .OnDaysOfTheWeek(DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
                                        DayOfWeek.Thursday, DayOfWeek.Friday)
                                .WithIntervalInMinutes(15)));

                        q.AddTrigger(opts => opts
                                                    .ForJob(sellJobKey)
                                                    .WithIdentity("SellTriggerQuat")
                                                    .WithDailyTimeIntervalSchedule(s => s
                                                        .StartingDailyAt(TimeOfDay.HourAndMinuteOfDay(9, 15))
                                                        .EndingDailyAt(TimeOfDay.HourAndMinuteOfDay(15, 20))
                                                        .InTimeZone(TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"))
                                                        .OnDaysOfTheWeek(DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
                                                                DayOfWeek.Thursday, DayOfWeek.Friday)
                                                        .WithIntervalInMinutes(1)));
                        // After first hour: hourly
                        q.AddTrigger(opts => opts
                            .ForJob(sellJobKey)
                            .WithIdentity("SellTriggerHourly", "Trading")
                            .WithDailyTimeIntervalSchedule(s => s
                                .StartingDailyAt(TimeOfDay.HourAndMinuteOfDay(10, 15))
                                .EndingDailyAt(TimeOfDay.HourAndMinuteOfDay(15, 20))
                                .OnDaysOfTheWeek(DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
                                        DayOfWeek.Thursday, DayOfWeek.Friday)
                                .WithIntervalInHours(1)));

                        q.AddTrigger(opts => opts
                            .ForJob(sellJobKey)
                            .WithIdentity("SellTrigger24Hours", "Trading")
                            .WithDailyTimeIntervalSchedule(s =>
                                s.WithIntervalInHours(24)
                                 .OnEveryDay()
                                 .StartingDailyAt(TimeOfDay.HourAndMinuteOfDay(15, 15))
                                 .InTimeZone(TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"))));

                        // HourlyStatusJob (hourly, 9:15 AM–3:30 PM IST)
                        var statusJobKey = new JobKey("HourlyStatusJob", "Trading");
                        q.AddJob<HourlyStatusJob>(opts => opts.WithIdentity(statusJobKey));
                        q.AddTrigger(opts => opts
                            .ForJob(statusJobKey)
                            .WithIdentity("StatusTrigger")
                            .WithDailyTimeIntervalSchedule(s => s
                                .StartingDailyAt(TimeOfDay.HourAndMinuteOfDay(9, 15))
                                .EndingDailyAt(TimeOfDay.HourAndMinuteOfDay(15, 30))
                                .InTimeZone(TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"))
                                .OnDaysOfTheWeek(DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
                                        DayOfWeek.Thursday, DayOfWeek.Friday)
                                .WithIntervalInHours(1)));


                    });

                    // Add Quartz hosted service
                    services.AddQuartzHostedService(q => q.WaitForJobsToComplete = true);

                    // Add logging
                    services.AddLogging(logging =>
                    {
                        logging.AddConsole();
                        logging.SetMinimumLevel(LogLevel.Information);
                    });
                    //services.AddHostedService<TradoXBotHostedService>();
                });

            var host = builder.Build();

            // Check if running as a service or interactive mode
            if (args.Length > 0 && args[0].Equals("--service", StringComparison.OrdinalIgnoreCase))
            {
                await host.RunAsync();
            }
            else
            {
                //await host.RunAsync();
                //Console.WriteLine("Done!");
                var tradingOperations = host.Services.GetRequiredService<TradingOperations>();
                await tradingOperations.ShowMenuAsync();
            }
        }
    }
}


public sealed class TradoXBotHostedService : IHostedService, IHostedLifecycleService
{
    private readonly ILogger _logger;

    public TradoXBotHostedService(
        ILogger<TradoXBotHostedService> logger,
        IHostApplicationLifetime appLifetime)
    {
        _logger = logger;

        appLifetime.ApplicationStarted.Register(OnStarted);
        appLifetime.ApplicationStopping.Register(OnStopping);
        appLifetime.ApplicationStopped.Register(OnStopped);
    }

    Task IHostedLifecycleService.StartingAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("1. StartingAsync has been called.");

        return Task.CompletedTask;
    }

    Task IHostedService.StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("2. StartAsync has been called.");

        return Task.CompletedTask;
    }

    Task IHostedLifecycleService.StartedAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("3. StartedAsync has been called.");

        return Task.CompletedTask;
    }

    private void OnStarted()
    {
        _logger.LogInformation("4. OnStarted has been called.");
    }

    private void OnStopping()
    {
        _logger.LogInformation("5. OnStopping has been called.");
    }

    Task IHostedLifecycleService.StoppingAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("6. StoppingAsync has been called.");

        return Task.CompletedTask;
    }

    Task IHostedService.StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("7. StopAsync has been called.");

        return Task.CompletedTask;
    }

    Task IHostedLifecycleService.StoppedAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("8. StoppedAsync has been called.");

        return Task.CompletedTask;
    }

    private void OnStopped()
    {
        _logger.LogInformation("9. OnStopped has been called.");
    }
}
