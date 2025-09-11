using HtmlAgilityPack;
using TradoXBot.Models;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;

namespace TradoXBot.Services;

public class ChartinkScraper
{
    private readonly HttpClient _httpClient;
    private const string ScannerUrl = "https://chartink.com/screener/under-valued-stocks-pro";
    private const string ScalpingScannerUrl = "https://chartink.com/screener/tradox-scalping-scanner";

    public ChartinkScraper()
    {
        _httpClient = new HttpClient();
        //_httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
    }

    public async Task<List<ScannerStock>> GetStocksAsync()
    {
        // Headless Chrome setup
        var chromeOptions = new ChromeOptions();
        chromeOptions.AddArgument("--headless");
        IWebDriver driver = new ChromeDriver(chromeOptions);

        await driver.Navigate().GoToUrlAsync(ScannerUrl);
        await Task.Delay(2500);
        // Wait for the table to load
        try
        {
            // Wait for table to load (max 20 sec)
            var wait = new WebDriverWait(driver, TimeSpan.FromSeconds(30));
            await Task.Run(() => wait.Until(d => d.FindElement(By.XPath("//table[contains(@class, 'w-full')]"))));

            // Get HTML after JS rendering
            var html = driver.PageSource;
            var htmlDoc = new HtmlDocument();
            htmlDoc.LoadHtml(html);

            // Parse table rows
            var stocks = new List<ScannerStock>();
            var tables = htmlDoc.DocumentNode.SelectNodes("//table[contains(@class, 'w-full')]");

            if (tables != null)
            {
                foreach (var table in tables)
                {
                    var th = table.SelectNodes(".//thead//tr//th");
                    for (int i = 0; i < th.Count; i++)
                    {
                        if (th[i] == null) continue;
                        if (th[i].InnerText.Trim() == "Stock Name")
                        {
                            var rows = tables[i].SelectNodes(".//tbody//tr");
                            if (rows != null)
                            {
                                // Skip header row
                                for (int j = 0; j < rows.Count; j++)
                                {
                                    var cols = rows[j].SelectNodes(".//td");
                                    if (cols != null && cols.Count >= 0)
                                    {
                                        stocks.Add(new ScannerStock
                                        {
                                            ScanDate = DateTime.Now,
                                            Sr = int.Parse(cols[0].InnerText.Trim()),
                                            Name = cols[1].InnerText.Trim(),
                                            Symbol = cols[2].InnerText.Trim(),
                                            Close = decimal.Parse(cols[5].InnerText.Trim()),
                                            PercentChange = decimal.Parse(cols[4].InnerText.Trim().Replace("%", "")),
                                            Volume = long.Parse(cols[6].InnerText.Trim().Replace(",", ""))
                                        });
                                    }
                                }
                            }

                        }
                    }

                }
            }

            return stocks;
        }
        finally
        {
            driver.Quit();
        }
    }

    public async Task<List<ScannerStock>> GetScalpingStocksAsync()
    {
        // Headless Chrome setup
        var chromeOptions = new ChromeOptions();
        chromeOptions.AddArgument("--headless");
        IWebDriver driver = new ChromeDriver(chromeOptions);

        await driver.Navigate().GoToUrlAsync(ScalpingScannerUrl);
        await Task.Delay(2500);
        // Wait for the table to load
        try
        {
            // Wait for table to load (max 20 sec)
            var wait = new WebDriverWait(driver, TimeSpan.FromSeconds(30));
            await Task.Run(() => wait.Until(d => d.FindElement(By.XPath("//table[contains(@class, 'w-full')]"))));

            // Get HTML after JS rendering
            var html = driver.PageSource;
            var htmlDoc = new HtmlDocument();
            htmlDoc.LoadHtml(html);

            // Parse table rows
            var stocks = new List<ScannerStock>();
            var tables = htmlDoc.DocumentNode.SelectNodes("//table[contains(@class, 'w-full')]");

            if (tables != null)
            {
                foreach (var table in tables)
                {
                    var th = table.SelectNodes(".//thead//tr//th");
                    for (int i = 0; i < th.Count; i++)
                    {
                        if (th[i] == null) continue;
                        if (th[i].InnerText.Trim() == "Stock Name")
                        {
                            var rows = tables[i].SelectNodes(".//tbody//tr");
                            if (rows != null)
                            {
                                // Skip header row
                                for (int j = 0; j < rows.Count; j++)
                                {
                                    var cols = rows[j].SelectNodes(".//td");
                                    if (cols != null && cols.Count >= 0)
                                    {
                                        stocks.Add(new ScannerStock
                                        {
                                            ScanDate = DateTime.Now,
                                            Sr = int.Parse(cols[0].InnerText.Trim()),
                                            Name = cols[1].InnerText.Trim(),
                                            Symbol = cols[2].InnerText.Trim(),
                                            Close = decimal.Parse(cols[5].InnerText.Trim()),
                                            PercentChange = decimal.Parse(cols[4].InnerText.Trim().Replace("%", "")),
                                            Volume = long.Parse(cols[6].InnerText.Trim().Replace(",", ""))
                                        });
                                    }
                                }
                            }

                        }
                    }

                }
            }

            return stocks;
        }
        finally
        {
            driver.Quit();
        }
    }
}
