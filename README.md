# Heston Volatility Calibrator

A .NET application for calibrating Heston stochastic volatility models using market data.

## Project Structure

- `HestonModel/` - Core Heston model implementation
- `HestonVolCalibrator/` - Calibration framework
- `HestonVolCalibrator.DataLoader/` - Data loading utilities
- `HestonVolCalibrator.Runner/` - Command-line runner
- `HestonVolCalibrator.Web/` - Web API service

## Features

- Heston model pricing engine
- Multiple calibration optimizers (BFGS, Nelder-Mead, Genetic)
- Volatility surface construction
- Market data loading from Yahoo Finance
- Web API for surface calibration and pricing

## Getting Started

1. Build the solution: `dotnet build`
2. Run the web service: `dotnet run --project HestonVolCalibrator.Web`
3. Run the command-line tool: `dotnet run --project HestonVolCalibrator.Runner`

## Calibration Process

The calibration process involves:
1. Loading market data (options prices, underlying prices, etc.)
2. Constructing volatility surfaces
3. Using optimization algorithms to fit Heston parameters
4. Validating the calibrated model against market data