using HestonVolCalibrator.Models;
using HestonVolCalibrator.Calibration;

namespace HestonVolCalibrator.ConsoleTest
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.WriteLine("Testing Heston Greeks implementation...");
            
            // Setup test parameters
            var params1 = new HestonModelParams(1.0, 0.04, 0.3, -0.7, 0.04);
            double spot = 100.0;
            double strike = 100.0;
            double maturity = 1.0;
            double rate = 0.05;
            double dividendYield = 0.0;

            try
            {
                // Test each Greek calculation
                Console.WriteLine("Computing Greeks for Heston model...");
                
                var delta = HestonPricer.CallDelta(params1, spot, strike, maturity, rate, dividendYield);
                Console.WriteLine($"Call Delta: {delta:F6}");
                
                var gamma = HestonPricer.Gamma(params1, spot, strike, maturity, rate, dividendYield);
                Console.WriteLine($"Gamma: {gamma:F6}");
                
                var theta = HestonPricer.Theta(params1, spot, strike, maturity, rate, dividendYield);
                Console.WriteLine($"Theta: {theta:F6}");
                
                var rho = HestonPricer.Rho(params1, spot, strike, maturity, rate, dividendYield);
                Console.WriteLine($"Rho: {rho:F6}");
                
                var vega = HestonPricer.Vega(params1, spot, strike, maturity, rate, dividendYield);
                Console.WriteLine($"Vega: {vega:F6}");
                
                var putDelta = HestonPricer.PutDelta(params1, spot, strike, maturity, rate, dividendYield);
                Console.WriteLine($"Put Delta: {putDelta:F6}");
                
                Console.WriteLine("All Greeks computed successfully!");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during computation: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
            }
        }
    }
}