namespace TradoXBot.Models;

public class ScannerStock
{
    public string? Id { get; set; }
    public DateTime ScanDate { get; set; }
    public int Sr { get; set; }
    public string? Name { get; set; }
    public string? Symbol { get; set; }
    public decimal Close { get; set; }
    public decimal PercentChange { get; set; }
    public long Volume { get; set; }
}