using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quartz;
using TradoXBot.Services;

namespace TradoXBot;

class Program
{
    static async Task Main(string[] args)
    {
        var services = new ServiceCollection();
        ConfigureServices(services);
        var serviceProvider = services.BuildServiceProvider();
        var logger = serviceProvider.GetService<ILogger<Program>>();

        if (args.Length > 0 && args[0] == "--service")
        {
            await Host.CreateDefaultBuilder(args)
                .ConfigureServices((hostContext, s) =>
                {
                    ConfigureServices((ServiceCollection)s);
                    s.AddHostedService<TradingService>();
                })
                .RunConsoleAsync();
        }
        else
        {
            logger.LogInformation("Trading Bot Console App Started (Test Mode)");
            var tradingOperations = serviceProvider.GetService<TradingOperations>();

            while (true)
            {
                Console.WriteLine("\nTrading Bot Test Menu:");
                Console.WriteLine("1. Execute Swing Buy");
                Console.WriteLine("2. Execute Swing Sell");
                Console.WriteLine("3. Execute Scalping Monitor");
                Console.WriteLine("4. Execute Status Report");
                Console.WriteLine("5. Run Swing Backtest");
                Console.WriteLine("6. Run Scalping Backtest");
                Console.WriteLine("7. Exit");
                Console.Write("Select an option (1-7): ");

                var input = Console.ReadLine();
                try
                {
                    switch (input)
                    {
                        case "1":
                            await tradingOperations.ExecuteSwingBuyAsync();
                            Console.WriteLine("Swing Buy executed.");
                            break;
                        case "2":
                            await tradingOperations.ExecuteSwingSellAsync();
                            Console.WriteLine("Swing Sell executed.");
                            break;
                        case "3":
                            await tradingOperations.ExecuteScalpingMonitorAsync();
                            Console.WriteLine("Scalping Monitor executed.");
                            break;
                        case "4":
                            await tradingOperations.ExecuteStatusReportAsync();
                            Console.WriteLine("Status Report executed.");
                            break;
                        case "5":
                            Console.Write("Enter symbol for swing backtest (e.g., RELIANCE): ");
                            var swingSymbol = Console.ReadLine();
                            var (trades, pl, winRate) = await tradingOperations.ExecuteSwingBacktestAsync(swingSymbol);
                            Console.WriteLine($"Swing Backtest: Trades={trades}, Total P/L=₹{pl:F2}, Win Rate={winRate:P2}");
                            break;
                        case "6":
                            Console.Write("Enter symbol for scalping backtest (e.g., RELIANCE): ");
                            var scalpingSymbol = Console.ReadLine();
                            var (trades2, pl2, winRate2) = await tradingOperations.ExecuteScalpingBacktestAsync(scalpingSymbol);
                            Console.WriteLine($"Scalping Backtest: Trades={trades2}, Total P/L=₹{pl2:F2}, Win Rate={winRate2:P2}");
                            break;
                        case "7":
                            logger.LogInformation("Exiting Trading Bot Console App");
                            return;
                        default:
                            Console.WriteLine("Invalid option. Please select 1-7.");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError($"Error executing option {input}: {ex.Message}");
                    Console.WriteLine($"Error: {ex.Message}");
                }
            }
        }
    }

    public static void ConfigureServices(ServiceCollection services)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();

        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.AddConfiguration(configuration.GetSection("Logging"));
        });
        services.AddSingleton<StoxKartClient>();
        services.AddSingleton<ChartinkScraper>();
        services.AddSingleton<HistoricalDataFetcher>();
        services.AddSingleton<MongoDbService>();
        services.AddSingleton<Backtester>();
        services.AddSingleton<TradingOperations>();
        services.AddSingleton<ISchedulerFactory, Quartz.Impl.StdSchedulerFactory>();
    }
}