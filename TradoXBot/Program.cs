using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quartz;
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
                    // Register services
                    services.AddSingleton<IConfiguration>(configuration);
                    services.AddSingleton<StoxKartClient>();
                    services.AddSingleton<ChartinkScraper>();
                    services.AddSingleton<HistoricalDataFetcher>();
                    services.AddSingleton<MongoDbService>();
                    services.AddSingleton<BacktestService>();
                    services.AddSingleton<TradingOperations>();

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
                                .EndingDailyAt(TimeOfDay.HourAndMinuteOfDay(15, 30))
                                .InTimeZone(TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"))
                                .OnDaysOfTheWeek(DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
                                        DayOfWeek.Thursday, DayOfWeek.Friday)
                                .WithIntervalInMinutes(15)));

                        // After first hour: hourly
                        q.AddTrigger(opts => opts
                            .ForJob(sellJobKey)
                            .WithIdentity("SellTriggerHourly")
                            .WithDailyTimeIntervalSchedule(s => s
                                .StartingDailyAt(TimeOfDay.HourAndMinuteOfDay(11, 15))
                                .EndingDailyAt(TimeOfDay.HourAndMinuteOfDay(15, 15))
                                .OnDaysOfTheWeek(DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
                                        DayOfWeek.Thursday, DayOfWeek.Friday)
                                .WithIntervalInHours(1)));

                        q.AddJob<SellJob>(opts => opts.WithIdentity(sellJobKey));
                        q.AddTrigger(opts => opts
                            .ForJob(sellJobKey)
                            .WithIdentity("SellTrigger", "Trading")
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
                var tradingOperations = host.Services.GetRequiredService<TradingOperations>();
                await tradingOperations.ShowMenuAsync();
            }
        }
    }
}
