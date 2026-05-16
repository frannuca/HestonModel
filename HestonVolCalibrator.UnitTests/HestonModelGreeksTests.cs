using HestonVolCalibrator.Models;
using HestonVolCalibrator.Calibration;

namespace HestonVolCalibrator.UnitTests;

[TestClass]
public class HestonModelGreeksTests
{
    [TestMethod]
    public void Test_Heston_Call_Delta_Computation()
    {
        // Setup test parameters
        var params1 = new HestonModelParams(1.0, 0.04, 0.3, -0.7, 0.04);
        double spot = 100.0;
        double strike = 100.0;
        double maturity = 1.0;
        double rate = 0.05;
        double dividendYield = 0.0;

        // Test that the delta calculation doesn't throw exceptions
        var delta = HestonPricer.CallDelta(params1, spot, strike, maturity, rate, dividendYield);
        Assert.IsNotNull(delta);
        Assert.IsFalse(double.IsNaN(delta));
        Assert.IsFalse(double.IsInfinity(delta));
    }

    [TestMethod]
    public void Test_Heston_Gamma_Computation()
    {
        // Setup test parameters
        var params1 = new HestonModelParams(1.0, 0.04, 0.3, -0.7, 0.04);
        double spot = 100.0;
        double strike = 100.0;
        double maturity = 1.0;
        double rate = 0.05;
        double dividendYield = 0.0;

        // Test that the gamma calculation doesn't throw exceptions
        var gamma = HestonPricer.Gamma(params1, spot, strike, maturity, rate, dividendYield);
        Assert.IsNotNull(gamma);
        Assert.IsFalse(double.IsNaN(gamma));
        Assert.IsFalse(double.IsInfinity(gamma));
    }

    [TestMethod]
    public void Test_Heston_Theta_Computation()
    {
        // Setup test parameters
        var params1 = new HestonModelParams(1.0, 0.04, 0.3, -0.7, 0.04);
        double spot = 100.0;
        double strike = 100.0;
        double maturity = 1.0;
        double rate = 0.05;
        double dividendYield = 0.0;

        // Test that the theta calculation doesn't throw exceptions
        var theta = HestonPricer.Theta(params1, spot, strike, maturity, rate, dividendYield);
        Assert.IsNotNull(theta);
        Assert.IsFalse(double.IsNaN(theta));
        Assert.IsFalse(double.IsInfinity(theta));
    }

    [TestMethod]
    public void Test_Heston_Rho_Computation()
    {
        // Setup test parameters
        var params1 = new HestonModelParams(1.0, 0.04, 0.3, -0.7, 0.04);
        double spot = 100.0;
        double strike = 100.0;
        double maturity = 1.0;
        double rate = 0.05;
        double dividendYield = 0.0;

        // Test that the rho calculation doesn't throw exceptions
        var rho = HestonPricer.Rho(params1, spot, strike, maturity, rate, dividendYield);
        Assert.IsNotNull(rho);
        Assert.IsFalse(double.IsNaN(rho));
        Assert.IsFalse(double.IsInfinity(rho));
    }

    [TestMethod]
    public void Test_Heston_Vega_Computation()
    {
        // Setup test parameters
        var params1 = new HestonModelParams(1.0, 0.04, 0.3, -0.7, 0.04);
        double spot = 100.0;
        double strike = 100.0;
        double maturity = 1.0;
        double rate = 0.05;
        double dividendYield = 0.0;

        // Test that the vega calculation doesn't throw exceptions
        var vega = HestonPricer.Vega(params1, spot, strike, maturity, rate, dividendYield);
        Assert.IsNotNull(vega);
        Assert.IsFalse(double.IsNaN(vega));
        Assert.IsFalse(double.IsInfinity(vega));
    }

    [TestMethod]
    public void Test_Heston_Put_Delta_Computation()
    {
        // Setup test parameters
        var params1 = new HestonModelParams(1.0, 0.04, 0.3, -0.7, 0.04);
        double spot = 100.0;
        double strike = 100.0;
        double maturity = 1.0;
        double rate = 0.05;
        double dividendYield = 0.0;

        // Test put delta calculation
        var putDelta = HestonPricer.PutDelta(params1, spot, strike, maturity, rate, dividendYield);
        Assert.IsNotNull(putDelta);
        Assert.IsFalse(double.IsNaN(putDelta));
        Assert.IsFalse(double.IsInfinity(putDelta));
    }

    [TestMethod]
    public void Test_Greeks_Are_Consistent()
    {
        // Test that CallDelta + PutDelta = 1.0 (delta hedging property)
        var params1 = new HestonModelParams(1.0, 0.04, 0.3, -0.7, 0.04);
        double spot = 100.0;
        double strike = 100.0;
        double maturity = 1.0;
        double rate = 0.05;
        double dividendYield = 0.0;

        var callDelta = HestonPricer.CallDelta(params1, spot, strike, maturity, rate, dividendYield);
        var putDelta = HestonPricer.PutDelta(params1, spot, strike, maturity, rate, dividendYield);

        // The sum should be approximately 1.0
        Assert.IsTrue(Math.Abs(callDelta + putDelta - 1.0) < 1e-10);
    }
}