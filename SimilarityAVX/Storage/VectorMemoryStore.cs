using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using CSharpMcpServer.Models;
using System.Numerics.Tensors;

namespace CSharpMcpServer.Storage;

public class VectorMemoryStore
{
    protected readonly List<VectorEntry> _entries = new();
    protected readonly int _dimension;
    protected readonly VectorPrecision _precision;
    protected readonly int _maxDegreeOfParallelism;
    
    // Storage for different precision types
    protected float[]? _allVectorsFloat32;
    protected Half[]? _allVectorsHalf;
    protected int _vectorCount;
    
    // CPU capabilities
    private readonly bool _supportsAvx512;
    private readonly bool _supportsAvx2;
    private readonly bool _supportsFma;
    private readonly string _searchMethod;
    
    public VectorMemoryStore(int dimension, VectorPrecision precision, int maxDegreeOfParallelism = 16)
    {
        _dimension = dimension;
        _precision = precision;
        _maxDegreeOfParallelism = maxDegreeOfParallelism;
        
        // Detect CPU capabilities
        _supportsAvx512 = Avx512F.IsSupported && Avx512F.VL.IsSupported;
        _supportsAvx2 = Avx2.IsSupported;
        _supportsFma = Fma.IsSupported;
        
        // Determine search method
        if (_precision == VectorPrecision.Half)
        {
            _searchMethod = "TensorPrimitives (Half)";
        }
        else
        {
            // .NET 9 TensorPrimitives now uses AVX-512 when available
            _searchMethod = _supportsAvx512 ? "TensorPrimitives (AVX-512)" : "TensorPrimitives (AVX2/SSE)";
        }
        
        Console.Error.WriteLine($"[VectorStore] Initialized with {_searchMethod}, dimension={dimension}");
    }
    
    public void AddEntry(string id, string filePath, int startLine, int endLine, 
                        string content, byte[] embeddingBytes, VectorPrecision sourcePrecision,
                        string? chunkType = null, DateTime? lastModified = null)
    {
        var entry = new VectorEntry
        {
            Id = id,
            FilePath = filePath,
            StartLine = startLine,
            EndLine = endLine,
            Content = content,
            EmbeddingBytes = embeddingBytes,
            SourcePrecision = sourcePrecision,
            ChunkType = chunkType ?? "unknown"
        };
        
        // Get file last modified date if not provided
        if (lastModified.HasValue)
        {
            entry.LastModified = lastModified.Value;
        }
        else if (File.Exists(filePath))
        {
            entry.LastModified = File.GetLastWriteTimeUtc(filePath);
        }
        
        _entries.Add(entry);
        
        // Mark for rebuild
        _allVectorsFloat32 = null;
        _allVectorsHalf = null;
    }
    
    public void BuildIndex()
    {
        _vectorCount = _entries.Count;
        if (_vectorCount == 0) return;
        
        if (_precision == VectorPrecision.Half)
        {
            BuildHalfIndex();
        }
        else
        {
            BuildFloat32Index();
        }
        
        var stats = GetStats();
        Console.Error.WriteLine($"[VectorStore] Index built: {_vectorCount} vectors, {stats.VectorsMemoryMB:F1}MB vectors, {stats.MemoryUsageMB:F1}MB total");
    }
    
    private void BuildFloat32Index()
    {
        _allVectorsFloat32 = new float[_vectorCount * _dimension];
        
        // Convert all vectors to float32 and flatten
        Parallel.For(0, _vectorCount, new ParallelOptions { MaxDegreeOfParallelism = _maxDegreeOfParallelism }, i =>
        {
            var entry = _entries[i];
            var offset = i * _dimension;
            
            if (entry.SourcePrecision == VectorPrecision.Float32)
            {
                // Direct copy from float32
                Buffer.BlockCopy(entry.EmbeddingBytes, 0, _allVectorsFloat32, offset * sizeof(float), entry.EmbeddingBytes.Length);
            }
            else
            {
                // Convert from Half to float32
                var halfSpan = MemoryMarshal.Cast<byte, Half>(entry.EmbeddingBytes);
                for (int j = 0; j < _dimension; j++)
                {
                    _allVectorsFloat32[offset + j] = (float)halfSpan[j];
                }
            }
        });
    }
    
    private void BuildHalfIndex()
    {
        _allVectorsHalf = new Half[_vectorCount * _dimension];
        
        // Convert all vectors to Half and flatten
        Parallel.For(0, _vectorCount, new ParallelOptions { MaxDegreeOfParallelism = _maxDegreeOfParallelism }, i =>
        {
            var entry = _entries[i];
            var offset = i * _dimension;
            
            if (entry.SourcePrecision == VectorPrecision.Half)
            {
                // Direct copy from Half bytes
                var halfSpan = MemoryMarshal.Cast<byte, Half>(entry.EmbeddingBytes);
                
                
                halfSpan.CopyTo(_allVectorsHalf.AsSpan(offset, _dimension));
            }
            else
            {
                // Convert from float32 to Half
                var floatSpan = MemoryMarshal.Cast<byte, float>(entry.EmbeddingBytes);
                for (int j = 0; j < _dimension; j++)
                {
                    _allVectorsHalf[offset + j] = (Half)floatSpan[j];
                }
            }
        });
    }
    
    public virtual SearchResult[] Search(float[] queryVector, int topK = 5)
    {
        if (_vectorCount == 0) 
            return Array.Empty<SearchResult>();
        
        var scores = new float[_vectorCount];
        
        if (_precision == VectorPrecision.Half)
        {
            SearchWithHalf(queryVector, scores);
        }
        else
        {
            // Use TensorPrimitives as default for Float32 (fastest with .NET 9)
            SearchWithTensorPrimitives(queryVector, scores);
        }
        
        // Get top K results
        return GetTopResults(scores, topK);
    }
    
    protected void SearchWithTensorPrimitives(float[] queryVector, float[] scores)
    {
        // Use TensorPrimitives for Float32 - now with AVX-512 support in .NET 9
        Parallel.For(0, _vectorCount, new ParallelOptions { MaxDegreeOfParallelism = _maxDegreeOfParallelism }, vectorIndex =>
        {
            var offset = vectorIndex * _dimension;
            var vectorSpan = new ReadOnlySpan<float>(_allVectorsFloat32, offset, _dimension);
            
            // TensorPrimitives.CosineSimilarity now uses AVX-512 when available
            scores[vectorIndex] = TensorPrimitives.CosineSimilarity(queryVector, vectorSpan);
        });
    }
    
    protected void SearchWithHalf(float[] queryVector, float[] scores)
    {
        // Convert query to Half
        var queryHalf = new Half[_dimension];
        for (int i = 0; i < _dimension; i++)
        {
            queryHalf[i] = (Half)queryVector[i];
        }
        
        // Use TensorPrimitives for hardware-accelerated operations
        Parallel.For(0, _vectorCount, new ParallelOptions { MaxDegreeOfParallelism = _maxDegreeOfParallelism }, vectorIndex =>
        {
            var offset = vectorIndex * _dimension;
            var vectorSpan = new ReadOnlySpan<Half>(_allVectorsHalf, offset, _dimension);
            
            // TensorPrimitives.CosineSimilarity is hardware-accelerated
            scores[vectorIndex] = (float)TensorPrimitives.CosineSimilarity(new ReadOnlySpan<Half>(queryHalf), vectorSpan);
        });
    }
    
    protected void SearchWithAvx2(float[] queryVector, float[] scores)
    {
        // Pre-compute query norm
        float queryNorm = 0;
        for (int i = 0; i < _dimension; i++)
            queryNorm += queryVector[i] * queryVector[i];
        queryNorm = MathF.Sqrt(queryNorm);
        
        if (queryNorm == 0)
        {
            Array.Fill(scores, 0);
            return;
        }
        
        // Search using AVX2
        Parallel.For(0, _vectorCount, new ParallelOptions { MaxDegreeOfParallelism = _maxDegreeOfParallelism }, vectorIndex =>
        {
            int offset = vectorIndex * _dimension;
            float dotProduct = 0;
            float vectorNorm = 0;
            
            int i = 0;
            
            // Process 8 floats at a time with AVX2
            unsafe
            {
                fixed (float* pQuery = queryVector)
                fixed (float* pVector = &_allVectorsFloat32![offset])
                {
                    for (; i <= _dimension - 8; i += 8)
                    {
                        var q = Avx.LoadVector256(pQuery + i);
                        var v = Avx.LoadVector256(pVector + i);
                        
                        if (_supportsFma)
                        {
                            var prod = Fma.MultiplyAdd(q, v, Vector256<float>.Zero);
                            dotProduct += Sum256(prod);
                        }
                        else
                        {
                            var prod = Avx.Multiply(q, v);
                            dotProduct += Sum256(prod);
                        }
                        
                        var vsq = Avx.Multiply(v, v);
                        vectorNorm += Sum256(vsq);
                    }
                }
            }
            
            // Handle remaining elements
            for (; i < _dimension; i++)
            {
                float stored = _allVectorsFloat32![offset + i];
                dotProduct += queryVector[i] * stored;
                vectorNorm += stored * stored;
            }
            
            vectorNorm = MathF.Sqrt(vectorNorm);
            scores[vectorIndex] = vectorNorm > 0 ? dotProduct / (queryNorm * vectorNorm) : 0;
        });
    }
    
    protected void SearchWithAvx512(float[] queryVector, float[] scores)
    {
        // Pre-compute query norm using AVX-512
        float queryNorm = ComputeNormAvx512(queryVector);
        
        if (queryNorm == 0)
        {
            Array.Fill(scores, 0);
            return;
        }
        
        // Process each vector using AVX-512
        Parallel.For(0, _vectorCount, new ParallelOptions { MaxDegreeOfParallelism = _maxDegreeOfParallelism }, vectorIndex =>
        {
            unsafe
            {
                fixed (float* pQuery = queryVector)
                fixed (float* pVectors = _allVectorsFloat32)
                {
                    var pVector = pVectors + (vectorIndex * _dimension);
                    var dotProductSum = Vector512<float>.Zero;
                    var vectorNormSum = Vector512<float>.Zero;
                    
                    int i = 0;
                    // Process 16 floats at a time with AVX-512
                    for (; i <= _dimension - 16; i += 16)
                    {
                        var q = Avx512F.LoadVector512(pQuery + i);
                        var v = Avx512F.LoadVector512(pVector + i);
                        
                        // Fused multiply-add for dot product
                        dotProductSum = Avx512F.FusedMultiplyAdd(q, v, dotProductSum);
                        
                        // Vector norm
                        vectorNormSum = Avx512F.FusedMultiplyAdd(v, v, vectorNormSum);
                    }
                    
                    // Horizontal sum for AVX-512 vectors
                    float dotProduct = HorizontalSum512(dotProductSum);
                    float vectorNorm = HorizontalSum512(vectorNormSum);
                    
                    // Handle remaining elements with AVX2 (8 at a time)
                    for (; i <= _dimension - 8; i += 8)
                    {
                        var q = Avx.LoadVector256(pQuery + i);
                        var v = Avx.LoadVector256(pVector + i);
                        
                        var prod = Avx.Multiply(q, v);
                        dotProduct += Sum256(prod);
                        
                        var vsq = Avx.Multiply(v, v);
                        vectorNorm += Sum256(vsq);
                    }
                    
                    // Scalar remainder
                    for (; i < _dimension; i++)
                    {
                        dotProduct += pQuery[i] * pVector[i];
                        vectorNorm += pVector[i] * pVector[i];
                    }
                    
                    vectorNorm = MathF.Sqrt(vectorNorm);
                    scores[vectorIndex] = vectorNorm > 0 ? dotProduct / (queryNorm * vectorNorm) : 0;
                }
            }
        });
    }
    
    protected void SearchWithVector(float[] queryVector, float[] scores)
    {
        // Pre-compute query norm
        float queryNorm = 0;
        for (int i = 0; i < _dimension; i++)
            queryNorm += queryVector[i] * queryVector[i];
        queryNorm = MathF.Sqrt(queryNorm);
        
        if (queryNorm == 0)
        {
            Array.Fill(scores, 0);
            return;
        }
        
        int simdWidth = Vector<float>.Count;
        
        Parallel.For(0, _vectorCount, new ParallelOptions { MaxDegreeOfParallelism = _maxDegreeOfParallelism }, vectorIndex =>
        {
            int offset = vectorIndex * _dimension;
            float dotProduct = 0;
            float vectorNorm = 0;
            
            // SIMD loop
            int i = 0;
            for (; i <= _dimension - simdWidth; i += simdWidth)
            {
                var queryVec = new Vector<float>(queryVector, i);
                var storedVec = new Vector<float>(_allVectorsFloat32!, offset + i);
                
                dotProduct += Vector.Dot(queryVec, storedVec);
                vectorNorm += Vector.Dot(storedVec, storedVec);
            }
            
            // Handle remaining elements
            for (; i < _dimension; i++)
            {
                float stored = _allVectorsFloat32![offset + i];
                dotProduct += queryVector[i] * stored;
                vectorNorm += stored * stored;
            }
            
            vectorNorm = MathF.Sqrt(vectorNorm);
            scores[vectorIndex] = vectorNorm > 0 ? dotProduct / (queryNorm * vectorNorm) : 0;
        });
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Sum256(Vector256<float> vector)
    {
        var upper = Avx.ExtractVector128(vector, 1);
        var lower = vector.GetLower();
        var sum128 = Sse.Add(upper, lower);
        
        // Horizontal add
        var shuf = Sse.Shuffle(sum128, sum128, 0x1B);
        var sums = Sse.Add(sum128, shuf);
        shuf = Sse.Shuffle(sums, sums, 0x01);
        sums = Sse.Add(sums, shuf);
        
        return sums.GetElement(0);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private float ComputeNormAvx512(float[] vector)
    {
        unsafe
        {
            fixed (float* pVector = vector)
            {
                var normSum = Vector512<float>.Zero;
                int i = 0;
                
                // Process 16 floats at a time
                for (; i <= _dimension - 16; i += 16)
                {
                    var v = Avx512F.LoadVector512(pVector + i);
                    normSum = Avx512F.FusedMultiplyAdd(v, v, normSum);
                }
                
                float norm = HorizontalSum512(normSum);
                
                // Handle remaining elements
                for (; i < _dimension; i++)
                {
                    norm += vector[i] * vector[i];
                }
                
                return MathF.Sqrt(norm);
            }
        }
    }
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float HorizontalSum512(Vector512<float> vector)
    {
        // Extract upper and lower 256-bit halves
        var lower = vector.GetLower();
        var upper = vector.GetUpper();
        var sum256 = Avx.Add(lower, upper);
        
        // Use existing Sum256 method for final reduction
        return Sum256(sum256);
    }
    
    protected SearchResult[] GetTopResults(float[] scores, int topK)
    {
        // For small K (typical case), use simple K-pass algorithm
        // This is O(n*k) which is optimal for small k (k < 20)
        if (topK <= 20)
        {
            return GetTopResultsLinearScan(scores, topK);
        }
        
        // For larger K, use sorting approach
        return GetTopResultsSimple(scores, topK);
    }
    
    private SearchResult[] GetTopResultsLinearScan(float[] scores, int topK)
    {
        // Pre-calculate combined scores
        var combinedScores = new float[_vectorCount];
        for (int i = 0; i < _vectorCount; i++)
        {
            var entry = _entries[i];
            var semanticScore = scores[i];
            
            // Calculate recency score (files modified within last 7 days get boost)
            var daysSinceModified = (DateTime.UtcNow - entry.LastModified).TotalDays;
            var recencyScore = daysSinceModified <= 7 ? 1.1f : 
                              daysSinceModified <= 30 ? 1.05f : 
                              daysSinceModified <= 90 ? 1.0f : 0.95f;
            
            // Calculate file importance based on path, type, and content
            var fileImportance = CalculateFileImportance(entry.FilePath, entry.ChunkType, entry.Content);
            
            // Combine scores: 70% semantic, 20% importance, 10% recency
            combinedScores[i] = (semanticScore * 0.7f) + 
                               (semanticScore * fileImportance * 0.2f) + 
                               (semanticScore * recencyScore * 0.1f);
        }
        
        // Find top K using K linear scans
        var topIndices = new int[Math.Min(topK, _vectorCount)];
        var used = new bool[_vectorCount];
        
        for (int k = 0; k < topIndices.Length; k++)
        {
            int bestIndex = -1;
            float bestScore = float.MinValue;
            
            // Find the next best score
            for (int i = 0; i < _vectorCount; i++)
            {
                if (!used[i] && combinedScores[i] > bestScore)
                {
                    bestScore = combinedScores[i];
                    bestIndex = i;
                }
            }
            
            topIndices[k] = bestIndex;
            used[bestIndex] = true;
        }
        
        // Convert to SearchResults
        var results = new SearchResult[topIndices.Length];
        for (int i = 0; i < topIndices.Length; i++)
        {
            var idx = topIndices[i];
            results[i] = new SearchResult(
                _entries[idx].FilePath,
                _entries[idx].StartLine,
                _entries[idx].EndLine,
                _entries[idx].Content,
                scores[idx], // Return original semantic score for transparency
                _entries[idx].ChunkType
            );
        }
        
        return results;
    }
    
    private SearchResult[] GetTopResultsSimple(float[] scores, int topK)
    {
        var results = new List<(int index, float score, float combinedScore)>(_vectorCount);
        
        // Calculate combined scores with semantic similarity, file importance, and recency
        for (int i = 0; i < _vectorCount; i++)
        {
            var entry = _entries[i];
            var semanticScore = scores[i];
            
            // Calculate recency score (files modified within last 7 days get boost)
            var daysSinceModified = (DateTime.UtcNow - entry.LastModified).TotalDays;
            var recencyScore = daysSinceModified <= 7 ? 1.1f : 
                              daysSinceModified <= 30 ? 1.05f : 
                              daysSinceModified <= 90 ? 1.0f : 0.95f;
            
            // Calculate file importance based on path, type, and content
            var fileImportance = CalculateFileImportance(entry.FilePath, entry.ChunkType, entry.Content);
            
            // Combine scores: 70% semantic, 20% importance, 10% recency
            var combinedScore = (semanticScore * 0.7f) + 
                               (semanticScore * fileImportance * 0.2f) + 
                               (semanticScore * recencyScore * 0.1f);
            
            results.Add((i, semanticScore, combinedScore));
        }
        
        return results
            .OrderByDescending(x => x.combinedScore)
            .Take(Math.Min(topK, _vectorCount))
            .Select(x => new SearchResult(
                _entries[x.index].FilePath,
                _entries[x.index].StartLine,
                _entries[x.index].EndLine,
                _entries[x.index].Content,
                x.score, // Return original semantic score for transparency
                _entries[x.index].ChunkType
            ))
            .ToArray();
    }
    
    private float CalculateFileImportance(string filePath, string chunkType, string content)
    {
        var fileName = Path.GetFileName(filePath).ToLowerInvariant();
        var directory = Path.GetDirectoryName(filePath)?.ToLowerInvariant() ?? "";
        content = content.ToLowerInvariant();
        
        // Authentication and security patterns get highest boost
        if (ContainsAuthenticationPatterns(fileName, directory, content))
            return 1.5f;
        
        // Configuration and startup files
        if (fileName.Contains("program.cs") || fileName.Contains("startup.cs"))
            return 1.4f;
        if (fileName.Contains("appsettings") || fileName.Contains("config"))
            return 1.3f;
        
        // Controllers and API endpoints
        if (directory.Contains("controllers") || fileName.Contains("controller"))
            return 1.2f;
        
        // Services and business logic
        if (directory.Contains("services") || directory.Contains("handlers"))
            return 1.15f;
        
        // Models and data structures
        if (directory.Contains("models") || directory.Contains("entities"))
            return 1.1f;
        
        // Blazor components
        if (fileName.EndsWith(".razor") || directory.Contains("components"))
            return 1.1f;
        
        // Lower importance for tests and generated code
        if (directory.Contains("test") || directory.Contains("spec"))
            return 0.8f;
        if (fileName.EndsWith(".generated.cs") || fileName.Contains(".designer.cs"))
            return 0.7f;
        
        // Chunk type importance with authentication awareness
        return chunkType switch
        {
            // Authentication-enhanced types get highest boost
            "class-auth" or "method-auth" or "interface-auth" or "property-auth" => 1.5f,
            "class-security" or "method-security" => 1.4f,
            "class-config" or "method-config" => 1.3f,
            "class-controller" => 1.2f,
            "class-service" => 1.15f,
            
            // Standard types
            "class" when content.Contains("controller") => 1.2f,
            "class" => 1.1f,
            "interface" => 1.1f,
            "method" when ContainsAuthMethodPatterns(content) => 1.3f,
            "method" => 1.05f,
            "property" => 1.0f,
            "namespace" => 1.0f,
            "sliding_window" => 0.9f,
            "generated" => 0.8f,
            _ when chunkType.EndsWith("-auth") => 1.4f,
            _ when chunkType.EndsWith("-security") => 1.3f,
            _ when chunkType.EndsWith("-config") => 1.2f,
            _ => 1.0f
        };
    }
    
    private bool ContainsAuthenticationPatterns(string fileName, string directory, string content)
    {
        // File/directory patterns
        if (fileName.Contains("auth") || fileName.Contains("login") || fileName.Contains("security"))
            return true;
        if (directory.Contains("identity") || directory.Contains("auth") || directory.Contains("security"))
            return true;
        if (directory.Contains("areas") && directory.Contains("identity"))
            return true;
            
        // Content patterns for authentication
        var authPatterns = new[] { 
            "authenticate", "authorize", "login", "logout", "signin", "signout",
            "saml", "oauth", "jwt", "bearer", "claims", "identity", "principal",
            "certificate", "token", "session", "cookie", "credential"
        };
        
        return authPatterns.Any(pattern => content.Contains(pattern));
    }
    
    private bool ContainsAuthMethodPatterns(string content)
    {
        var authMethodPatterns = new[] {
            "authenticate", "authorize", "validatetoken", "signin", "signout",
            "login", "logout", "createtoken", "validatecredentials"
        };
        
        return authMethodPatterns.Any(pattern => content.Contains(pattern));
    }
    
    public void Clear()
    {
        _entries.Clear();
        _allVectorsFloat32 = null;
        _allVectorsHalf = null;
        _vectorCount = 0;
    }
    
    public void AppendVectors(IEnumerable<VectorEntry> newEntries)
    {
        var entriesToAdd = newEntries.ToList();
        if (!entriesToAdd.Any()) return;
        
        // Add new entries
        _entries.AddRange(entriesToAdd);
        
        // Rebuild index with new vectors
        BuildIndex();
        
        Console.Error.WriteLine($"[VectorStore] Appended {entriesToAdd.Count} vectors, total now: {_entries.Count}");
    }
    
    public void RemoveVectors(IEnumerable<string> idsToRemove)
    {
        var idsSet = new HashSet<string>(idsToRemove);
        if (!idsSet.Any()) return;
        
        var countBefore = _entries.Count;
        _entries.RemoveAll(e => idsSet.Contains(e.Id));
        var removed = countBefore - _entries.Count;
        
        if (removed > 0)
        {
            // Rebuild index after removal
            BuildIndex();
            Console.Error.WriteLine($"[VectorStore] Removed {removed} vectors, {_entries.Count} remaining");
        }
    }
    
    public void UpdateVectors(IEnumerable<VectorEntry> updatedEntries)
    {
        var updates = updatedEntries.ToList();
        if (!updates.Any()) return;
        
        // Create lookup for faster updates
        var updateLookup = updates.ToDictionary(e => e.Id);
        
        // Update existing entries
        int updated = 0;
        for (int i = 0; i < _entries.Count; i++)
        {
            if (updateLookup.TryGetValue(_entries[i].Id, out var newEntry))
            {
                _entries[i] = newEntry;
                updated++;
            }
        }
        
        if (updated > 0)
        {
            // Rebuild index after updates
            BuildIndex();
            Console.Error.WriteLine($"[VectorStore] Updated {updated} vectors");
        }
    }
    
    public void RemoveVectorsByPath(string filePath)
    {
        var countBefore = _entries.Count;
        _entries.RemoveAll(e => e.FilePath == filePath);
        var removed = countBefore - _entries.Count;
        
        if (removed > 0)
        {
            // Rebuild index after removal
            BuildIndex();
            Console.Error.WriteLine($"[VectorStore] Removed {removed} vectors from {filePath}");
        }
    }
    
    public MemoryStats GetStats()
    {
        double vectorsMemoryMB = 0;
        
        if (_precision == VectorPrecision.Half)
        {
            vectorsMemoryMB = _allVectorsHalf != null 
                ? (_allVectorsHalf.Length * sizeof(ushort)) / (1024.0 * 1024.0)
                : 0;
        }
        else
        {
            vectorsMemoryMB = _allVectorsFloat32 != null 
                ? (_allVectorsFloat32.Length * sizeof(float)) / (1024.0 * 1024.0)
                : 0;
        }
        
        var metadataMemoryMB = _entries.Count * 512 / (1024.0 * 1024.0);
        var uniqueFiles = _entries.Select(e => e.FilePath).Distinct().Count();
        
        return new MemoryStats
        {
            ChunkCount = _entries.Count,
            FileCount = uniqueFiles,
            VectorsMemoryMB = vectorsMemoryMB,
            MetadataMemoryMB = metadataMemoryMB,
            MemoryUsageMB = vectorsMemoryMB + metadataMemoryMB,
            Precision = _precision
        };
    }
    
    public VectorStorageMetadata GetMetadata()
    {
        return new VectorStorageMetadata
        {
            Dimension = _dimension,
            Precision = _precision,
            SupportsAvx512 = _supportsAvx512,
            SupportsAvx2 = _supportsAvx2,
            CpuCapabilities = _searchMethod
        };
    }
    
    public IEnumerable<VectorEntry> GetAllEntries() => _entries;
}

public class VectorEntry
{
    public required string Id { get; set; }
    public required string FilePath { get; set; }
    public required int StartLine { get; set; }
    public required int EndLine { get; set; }
    public required string Content { get; set; }
    public required byte[] EmbeddingBytes { get; set; }
    public required VectorPrecision SourcePrecision { get; set; }
    public DateTime LastModified { get; set; } = DateTime.UtcNow;
    public string ChunkType { get; set; } = "unknown";
}