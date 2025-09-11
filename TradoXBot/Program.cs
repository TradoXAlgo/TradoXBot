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

                    Console.WriteLine("Job Started");
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

                        var authJobKey = new JobKey("AuthJob", "Trading");
                        q.AddJob<AuthJob>(opts => opts.WithIdentity(authJobKey));
                        q.AddTrigger(opts => opts
                            .ForJob(authJobKey)
                            .WithIdentity("AuthTrigger"));

                        // BuyJob (Swing, 3:25 PM IST)
                        var buyJobKey = new JobKey("BuyJob", "Trading");
                        q.AddJob<BuyJob>(opts => opts.WithIdentity(buyJobKey));
                        q.AddTrigger(opts => opts
                            .ForJob(buyJobKey)
                            .WithIdentity("BuyTrigger")
                            .WithDailyTimeIntervalSchedule(s => s
                                .StartingDailyAt(TimeOfDay.HourAndMinuteOfDay(15, 25))
                                .EndingDailyAt(TimeOfDay.HourAndMinuteOfDay(15, 30))
                                .InTimeZone(TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"))
                                .OnDaysOfTheWeek(DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
                                        DayOfWeek.Thursday, DayOfWeek.Friday)));

                        //ScalpingMonitorJob (every 5 minutes, 9:15 AM–2:30 PM IST)
                        var scalpingJobKey = new JobKey("ScalpingMonitorJob", "Trading");
                        q.AddJob<ScalpingBuyJob>(opts => opts.WithIdentity(scalpingJobKey));
                        q.AddTrigger(opts => opts
                            .ForJob(scalpingJobKey)
                            .WithIdentity("ScalpingBuyTrigger")
                            .WithDailyTimeIntervalSchedule(s => s
                                .StartingDailyAt(TimeOfDay.HourAndMinuteOfDay(9, 10))
                                .EndingDailyAt(TimeOfDay.HourAndMinuteOfDay(14, 30))
                                .InTimeZone(TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"))
                                .OnDaysOfTheWeek(DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
                                        DayOfWeek.Thursday, DayOfWeek.Friday)
                                .WithIntervalInMinutes(5)));


                        // // ScalpingMonitorJob (every 5 minutes, 9:15 AM–2:30 PM IST)
                        var scalpingSellJobKey = new JobKey("ScalpingSellJob", "Trading");
                        q.AddJob<ScalpingSelJob>(opts => opts.WithIdentity(scalpingSellJobKey));
                        q.AddTrigger(opts => opts
                            .ForJob(scalpingSellJobKey)
                            .WithIdentity("ScalpingSellTrigger")
                            .WithDailyTimeIntervalSchedule(s => s
                                .StartingDailyAt(TimeOfDay.HourAndMinuteOfDay(9, 10))
                                .EndingDailyAt(TimeOfDay.HourAndMinuteOfDay(15, 30))
                                .InTimeZone(TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"))
                                .OnDaysOfTheWeek(DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
                                        DayOfWeek.Thursday, DayOfWeek.Friday)
                                .WithIntervalInMinutes(6)));


                        // SellJob (Swing, every 15 minutes, 9:15 AM–3:30 PM IST)
                        var sellJobKey = new JobKey("SellJob", "Trading");
                        q.AddJob<SellJob>(opts => opts.WithIdentity(sellJobKey));
                        q.AddTrigger(opts => opts
                            .ForJob(sellJobKey)
                            .WithIdentity("SellTriggerQuat")
                            .WithDailyTimeIntervalSchedule(s => s
                                .StartingDailyAt(TimeOfDay.HourAndMinuteOfDay(9, 0))
                                .EndingDailyAt(TimeOfDay.HourAndMinuteOfDay(10, 15))
                                .InTimeZone(TimeZoneInfo.FindSystemTimeZoneById("India Standard Time"))
                                .OnDaysOfTheWeek(DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday,
                                        DayOfWeek.Thursday, DayOfWeek.Friday)
                                .WithIntervalInMinutes(5)));

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

            using var host = builder.Build();
            // Check if running as a service or interactive mode
            if (args.Length > 0 && args[0].Equals("--service", StringComparison.OrdinalIgnoreCase))
            {
                await host.RunAsync();
            }
            else
            {
                var cts = new CancellationTokenSource();
                var tradingOperations = host.Services.GetRequiredService<TradingOperations>();
                TimeZoneInfo istZone = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
                TimeSpan startTime = new(9, 10, 0); // 9:15 AM
                TimeSpan endTime = new(15, 30, 0); // 15:30 pm 
                var istTimeZone = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
                var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, istTimeZone);
                TimeSpan currentTime = now.TimeOfDay;
                bool isWithinTime = currentTime >= startTime && currentTime <= endTime && IsTradingDay(now); 
                if (!isWithinTime)
                {
                    await tradingOperations.ShowMenuAsync();
                }
                else
                {
                    var tasks = new[]
                    {
                     tradingOperations.RunMethodAtIntervalAsync(async()=> await tradingOperations.ExecuteTokenAuthAsync(), TimeSpan.FromMinutes(5), "AuthJob", cts.Token, istZone),
                     tradingOperations.RunMethodAtIntervalAsync(async()=> await tradingOperations.ExecuteSwingBuyAsync(), TimeSpan.FromMinutes(5), "BuyJob", cts.Token, istZone),
                     tradingOperations.RunMethodAtIntervalAsync(async()=> await tradingOperations.ExecuteSwingSellAsync(), TimeSpan.FromMinutes(5), "5Mnt", cts.Token,istZone),
                     tradingOperations.RunMethodAtIntervalAsync(async()=> await tradingOperations.ExecuteSwingSellAsync(), TimeSpan.FromMinutes(340), "endDay", cts.Token, istZone),
                     tradingOperations.RunMethodAtIntervalAsync(async()=> await tradingOperations.ExecuteScalpingBuyAsync(), TimeSpan.FromMinutes(5), "scalpingBuy", cts.Token, istZone),
                     tradingOperations.RunMethodAtIntervalAsync(async()=> await tradingOperations.ExecuteScalpingSellAsync(), TimeSpan.FromMinutes(6), "scalpingsell", cts.Token, istZone),
                     tradingOperations.RunMethodAtIntervalAsync(async()=> await tradingOperations.ExecuteHouralyStatusAsync(), TimeSpan.FromMinutes(59), "shoyralyStatus", cts.Token, istZone),
                 };
                    await Task.WhenAll(tasks);
                    //cts.Cancel();
                    // Keep the program running 
                    //await host.RunAsync();
                }

                Console.ReadLine();
            }
        }

        private static bool IsTradingDay(DateTime date)
        {
            return date.DayOfWeek != DayOfWeek.Saturday && date.DayOfWeek != DayOfWeek.Sunday && !Holidays.Contains(date.Date);
        }

        private static readonly List<DateTime> Holidays =
        [
            new DateTime(2025, 2, 26), // Mahashivratri
        new DateTime(2025, 3, 14), // Holi
        new DateTime(2025, 3, 31), // Eid-Ul-Fitr
        new DateTime(2025, 4, 10), // Shri Mahavir Jayanti
        new DateTime(2025, 4, 14), // Dr. Baba Saheb Ambedkar Jayanti
        new DateTime(2025, 4, 18), // Good Friday
        new DateTime(2025, 5, 1), // Maharashtra Day
        new DateTime(2025, 8, 15), // Independence Day
        new DateTime(2025, 8, 27), // Ganesh Chaturthi
        new DateTime(2025, 10, 2), // Mahatma Gandhi Jayanti/Dussehra
        new DateTime(2025, 10, 21), // Diwali Laxmi Pujan
        new DateTime(2025, 10, 22), // Diwali-Balipratipada
        new DateTime(2025, 11, 5), // Prakash Gurpurb Sri Guru Nanak Dev
        new DateTime(2025, 12, 25) // Christmas
        ];
    }

}
