using System;
using System.Diagnostics;
using System.Linq;
using System.Numerics.Tensors;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using CSharpMcpServer.Models;
using CSharpMcpServer.Storage;

namespace CSharpMcpServer.Utils;

public class BenchmarkRunner
{
    public static void RunBenchmark(int dimension = 2048, int vectorCount = 10000, int iterations = 100, int searchCount = 10)
    {
        Console.WriteLine("==================================================");
        Console.WriteLine("C# Semantic Search - AVX-512 Performance Benchmark");
        Console.WriteLine("==================================================");
        Console.WriteLine();
        Console.WriteLine($"Configuration:");
        Console.WriteLine($"  Dimension: {dimension}");
        Console.WriteLine($"  Vectors: {vectorCount:N0}");
        Console.WriteLine($"  Search iterations: {iterations}");
        Console.WriteLine($"  Searches per iteration: {searchCount}");
        Console.WriteLine($"  Memory size: {(vectorCount * dimension * 4 / 1024.0 / 1024.0):F1} MB");
        Console.WriteLine();
        
        // Check CPU capabilities
        Console.WriteLine("CPU Capabilities:");
        Console.WriteLine($"  AVX2: {Avx2.IsSupported}");
        Console.WriteLine($"  AVX-512F: {Avx512F.IsSupported}");
        Console.WriteLine($"  FMA: {Fma.IsSupported}");
        Console.WriteLine();
        
        // Generate random test data
        Console.Write("Generating random vectors... ");
        var vectors = GenerateRandomVectors(vectorCount, dimension);
        var queries = GenerateRandomVectors(searchCount, dimension);
        Console.WriteLine("Done!");
        
        // Create memory store with configurable thread count
        var useAllCores = vectorCount >= 20000; // Use all cores for larger datasets
        var threadCount = useAllCores ? Environment.ProcessorCount : Math.Max(1, Environment.ProcessorCount / 2);
        Console.WriteLine($"  Thread count: {threadCount} ({(useAllCores ? "all" : "half of")} {Environment.ProcessorCount} cores)");
        Console.WriteLine();
        var memoryStore = new VectorMemoryStore(dimension, VectorPrecision.Float32, threadCount);
        
        // Convert to VectorEntry format
        var entries = new VectorEntry[vectorCount];
        for (int i = 0; i < vectorCount; i++)
        {
            var vectorBytes = new byte[dimension * sizeof(float)];
            Buffer.BlockCopy(vectors[i], 0, vectorBytes, 0, vectorBytes.Length);
            
            entries[i] = new VectorEntry
            {
                Id = $"vec_{i}",
                FilePath = $"file_{i}.cs",
                StartLine = i * 10,
                EndLine = (i * 10) + 10,
                Content = $"Test vector {i}",
                EmbeddingBytes = vectorBytes,
                SourcePrecision = VectorPrecision.Float32,
                ChunkType = "benchmark",
                LastModified = DateTime.UtcNow
            };
        }
        
        // Add entries to memory store
        foreach (var entry in entries)
        {
            memoryStore.AddEntry(entry.Id, entry.FilePath, entry.StartLine, entry.EndLine,
                entry.Content, entry.EmbeddingBytes, entry.SourcePrecision, entry.ChunkType, entry.LastModified);
        }
        memoryStore.BuildIndex();
        Console.WriteLine($"Index built with {vectorCount:N0} vectors");
        Console.WriteLine();
        
        // Warmup and verify results match
        Console.Write("Warming up and verifying results... ");
        SearchResult[]? avx512Results = null;
        float[]? tensorResults = null;
        
        // Get results from AVX-512
        avx512Results = memoryStore.Search(queries[0], 10);
        
        // Get results from TensorPrimitives
        var tensorScores = new float[vectorCount];
        for (int i = 0; i < vectorCount; i++)
        {
            tensorScores[i] = TensorPrimitives.CosineSimilarity(queries[0], vectors[i]);
        }
        tensorResults = tensorScores.OrderByDescending(x => x).Take(10).ToArray();
        
        // Compare top 10 scores
        Console.WriteLine("\nTop 10 scores comparison:");
        Console.WriteLine("Rank | AVX-512  | TensorPrimitives | Diff");
        Console.WriteLine("-----|----------|------------------|-------");
        for (int i = 0; i < 10; i++)
        {
            var diff = Math.Abs(avx512Results[i].Score - tensorResults[i]);
            Console.WriteLine($"  {i+1,2} | {avx512Results[i].Score:F6} | {tensorResults[i]:F6}      | {diff:F6}");
        }
        
        var maxDiff = avx512Results.Select((r, i) => Math.Abs(r.Score - tensorResults[i])).Max();
        Console.WriteLine($"\nMax difference: {maxDiff:F8}");
        if (maxDiff > 0.0001f)
        {
            Console.WriteLine("WARNING: Results differ significantly!");
        }
        else
        {
            Console.WriteLine("Results match within tolerance!");
        }
        Console.WriteLine();
        
        // Benchmark our AVX-512 implementation
        Console.WriteLine("Running AVX-512 benchmark...");
        var avx512Times = new double[iterations];
        var sw = new Stopwatch();
        
        for (int iter = 0; iter < iterations; iter++)
        {
            sw.Restart();
            foreach (var query in queries)
            {
                _ = memoryStore.Search(query, 10);
            }
            sw.Stop();
            avx512Times[iter] = sw.Elapsed.TotalMilliseconds;
        }
        
        // Benchmark TensorPrimitives for comparison
        Console.WriteLine("Running TensorPrimitives benchmark...");
        var tensorTimes = new double[iterations];
        
        for (int iter = 0; iter < iterations; iter++)
        {
            sw.Restart();
            foreach (var query in queries)
            {
                var scores = new float[vectorCount];
                for (int i = 0; i < vectorCount; i++)
                {
                    scores[i] = TensorPrimitives.CosineSimilarity(query, vectors[i]);
                }
                // Find top 10
                var topIndices = scores
                    .Select((score, idx) => (score, idx))
                    .OrderByDescending(x => x.score)
                    .Take(10)
                    .ToArray();
            }
            sw.Stop();
            tensorTimes[iter] = sw.Elapsed.TotalMilliseconds;
        }
        
        // Calculate statistics
        var avx512Avg = avx512Times.Average();
        var avx512Min = avx512Times.Min();
        var avx512Max = avx512Times.Max();
        var avx512StdDev = CalculateStdDev(avx512Times, avx512Avg);
        
        var tensorAvg = tensorTimes.Average();
        var tensorMin = tensorTimes.Min();
        var tensorMax = tensorTimes.Max();
        var tensorStdDev = CalculateStdDev(tensorTimes, tensorAvg);
        
        // Calculate throughput
        var totalComparisons = (long)vectorCount * searchCount;
        var avx512Throughput = totalComparisons / (avx512Avg / 1000.0);
        var tensorThroughput = totalComparisons / (tensorAvg / 1000.0);
        
        // Calculate FLOPS
        // Each cosine similarity: 2*dim multiplies + 2*dim-1 adds + 2 sqrts + 1 divide
        var flopsPerComparison = dimension * 4L; // Simplified: 2 muls + 2 adds per dimension
        var totalFlops = flopsPerComparison * totalComparisons;
        var avx512Gflops = totalFlops / (avx512Avg / 1000.0) / 1e9;
        var tensorGflops = totalFlops / (tensorAvg / 1000.0) / 1e9;
        
        // Display results
        Console.WriteLine();
        Console.WriteLine("==================================================");
        Console.WriteLine("Benchmark Results");
        Console.WriteLine("==================================================");
        Console.WriteLine();
        Console.WriteLine($"Time per {searchCount} searches (ms):");
        Console.WriteLine($"  AVX-512:         {avx512Avg:F2} ± {avx512StdDev:F2} (min: {avx512Min:F2}, max: {avx512Max:F2})");
        Console.WriteLine($"  TensorPrimitives: {tensorAvg:F2} ± {tensorStdDev:F2} (min: {tensorMin:F2}, max: {tensorMax:F2})");
        Console.WriteLine();
        Console.WriteLine($"Average time per search (ms):");
        Console.WriteLine($"  AVX-512:         {avx512Avg / searchCount:F3}");
        Console.WriteLine($"  TensorPrimitives: {tensorAvg / searchCount:F3}");
        Console.WriteLine();
        Console.WriteLine($"Throughput (comparisons/sec):");
        Console.WriteLine($"  AVX-512:         {avx512Throughput:N0}");
        Console.WriteLine($"  TensorPrimitives: {tensorThroughput:N0}");
        Console.WriteLine();
        Console.WriteLine($"GFLOPS:");
        Console.WriteLine($"  AVX-512:         {avx512Gflops:F1}");
        Console.WriteLine($"  TensorPrimitives: {tensorGflops:F1}");
        Console.WriteLine();
        Console.WriteLine($"Performance ratio: {tensorAvg / avx512Avg:F2}x");
        Console.WriteLine($"  (AVX-512 is {((tensorAvg / avx512Avg - 1) * 100):F0}% faster)");
        Console.WriteLine();
        
        // Memory bandwidth estimation
        var bytesPerSearch = vectorCount * dimension * sizeof(float) * 2; // query + vectors
        var avx512Bandwidth = bytesPerSearch * searchCount / (avx512Avg / 1000.0) / 1e9;
        Console.WriteLine($"Estimated memory bandwidth:");
        Console.WriteLine($"  AVX-512: {avx512Bandwidth:F1} GB/s");
        Console.WriteLine();
    }
    
    private static float[][] GenerateRandomVectors(int count, int dimension)
    {
        var random = new Random(42); // Fixed seed for reproducibility
        var vectors = new float[count][];
        
        for (int i = 0; i < count; i++)
        {
            vectors[i] = new float[dimension];
            float norm = 0;
            
            // Generate random values
            for (int j = 0; j < dimension; j++)
            {
                vectors[i][j] = (float)(random.NextDouble() * 2 - 1); // -1 to 1
                norm += vectors[i][j] * vectors[i][j];
            }
            
            // Normalize to unit length
            norm = MathF.Sqrt(norm);
            for (int j = 0; j < dimension; j++)
            {
                vectors[i][j] /= norm;
            }
        }
        
        return vectors;
    }
    
    private static double CalculateStdDev(double[] values, double mean)
    {
        var sumSquares = values.Sum(v => (v - mean) * (v - mean));
        return Math.Sqrt(sumSquares / values.Length);
    }
}