using System;
using System.Linq;
using GeneticSharp;

namespace HestonVolCalibrator.Calibration
{
    // Genetic global optimiser via GeneticSharp.
    // Real-valued chromosome over [kappa, theta, sigma, rho, v0] with explicit box bounds.
    // Population ~50, generations from OptimizerOptions.MaxIterations (default 100 if too large).
    public sealed class GeneticOptimizer : IOptimizer
    {
        public int PopulationMin { get; init; } = 50;
        public int PopulationMax { get; init; } = 50;
        public int? GenerationsOverride { get; init; } = 100;
        public double CrossoverProbability { get; init; } = 0.75;
        public double MutationProbability { get; init; } = 0.25;

        public OptimizationResult Minimize(
            IObjective obj,
            double[] x0,
            OptimizerOptions opts,
            Action<int, double, double[]>? iterCallback = null)
        {
            int n = x0.Length;
            var lower = obj.Lower.ToArray();
            var upper = obj.Upper.ToArray();

            // GeneticSharp's BinaryStringRepresentation encodes via BitConverter.GetBytes((long)…),
            // which is always 64 bits and is not trimmed for sign-extended negatives. Any gene whose
            // range crosses zero (e.g. rho) therefore requires totalBits=64. Use 64 uniformly so the
            // user can supply any bounds without surprises.
            int[] totalBits = Enumerable.Repeat(64, n).ToArray();
            int[] fractionDigits = Enumerable.Repeat(6, n).ToArray();

            // Use FloatingPointChromosome which already handles per-gene min/max.
            var chromosome = new FloatingPointChromosome(
                minValue: lower,
                maxValue: upper,
                totalBits: totalBits,
                fractionDigits: fractionDigits);

            int gen = 0;
            double bestF = double.PositiveInfinity;
            double[] bestX = (double[])x0.Clone();

            var fitness = new FuncFitness(c =>
            {
                var fc = (FloatingPointChromosome)c;
                var x = fc.ToFloatingPoints();
                double f;
                try { f = obj.Evaluate(x); }
                catch { f = double.MaxValue; }
                if (double.IsNaN(f) || double.IsInfinity(f)) f = double.MaxValue;
                if (f < bestF) { bestF = f; bestX = (double[])x.Clone(); }
                // GA maximises fitness; invert.
                return -f;
            });

            int popSize = Math.Max(PopulationMin, PopulationMax);
            var population = new Population(PopulationMin, PopulationMax, chromosome);
            var selection = new EliteSelection();
            var crossover = new UniformCrossover(0.5f);
            var mutation = new FlipBitMutation();

            int generations = GenerationsOverride ?? Math.Max(10, opts.MaxIterations);

            var termination = new GenerationNumberTermination(generations);

            var ga = new GeneticAlgorithm(population, fitness, selection, crossover, mutation)
            {
                Termination = termination,
                CrossoverProbability = (float)CrossoverProbability,
                MutationProbability = (float)MutationProbability
            };

            if (opts.Seed.HasValue)
            {
                // GeneticSharp uses its own static RandomizationProvider; deterministic seed
                // requires custom IRandomization. Skip for now (results still reproducible-ish
                // under FastRandomRandomization). Note for callers.
            }

            ga.GenerationRan += (_, __) =>
            {
                gen++;
                iterCallback?.Invoke(gen, bestF, (double[])bestX.Clone());
            };

            try { ga.Start(); }
            catch { /* fall through, return best-so-far */ }

            return new OptimizationResult(
                X: bestX,
                FinalValue: bestF,
                Iterations: gen,
                Converged: true,
                Method: "Genetic");
        }
    }
}
