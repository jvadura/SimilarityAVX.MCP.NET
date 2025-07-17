using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics.Tensors;
using System.Threading;
using System.Threading.Tasks;
using CSharpMcpServer.Models;

namespace CSharpMcpServer.Storage
{
    /// <summary>
    /// In-memory vector store for memory semantic search using TensorPrimitives
    /// </summary>
    public class MemoryVectorStore
    {
        private readonly List<MemoryVectorEntry> _vectors = new();
        private readonly ConcurrentDictionary<int, int> _memoryIdToIndex = new();
        private readonly HashSet<int> _deletedIndices = new();
        private readonly object _writeLock = new();
        
        // Pre-allocated arrays for all vectors (columnar storage for efficiency)
        private float[]? _allVectorsFloat32;
        private int _dimension;
        private int _capacity;
        private int _activeCount;
        private readonly int _maxDegreeOfParallelism;
        
        // Debugging and process tracking
        private static readonly int _ownerProcessId = Process.GetCurrentProcess().Id;
        private static readonly string _machineName = Environment.MachineName;
        
        public int VectorCount => _activeCount;
        
        public MemoryVectorStore()
        {
            _maxDegreeOfParallelism = Environment.ProcessorCount;
            Console.WriteLine($"[MemoryVectorStore] Initialized with TensorPrimitives (auto-optimized SIMD), parallelism: {_maxDegreeOfParallelism}");
        }
        
        /// <summary>
        /// Load all vectors from storage into memory for fast search
        /// </summary>
        public void LoadVectors(List<MemoryVectorEntry> vectors)
        {
            if (vectors.Count == 0) return;
            
            lock (_writeLock)
            {
                _vectors.Clear();
                _memoryIdToIndex.Clear();
                _deletedIndices.Clear();
                
                // Determine dimension from first vector (always use Float32 for simplicity)
                var firstVector = vectors[0];
                _dimension = firstVector.Embedding.Length;
                
                // Pre-allocate columnar storage with some headroom
                _capacity = (int)(vectors.Count * 1.5);
                _allVectorsFloat32 = new float[_capacity * _dimension];
                _activeCount = vectors.Count;
                
                // Pre-allocate list to avoid race conditions
                _vectors.Clear();
                _vectors.Capacity = vectors.Count;
                for (int i = 0; i < vectors.Count; i++)
                {
                    _vectors.Add(null!); // Pre-allocate slots
                }
                
                // Load vectors and build index in parallel (now thread-safe)
                Parallel.For(0, vectors.Count, new ParallelOptions { MaxDegreeOfParallelism = _maxDegreeOfParallelism }, i =>
                {
                    var vector = vectors[i];
                    _vectors[i] = vector; // Safe: direct assignment to pre-allocated slot
                    _memoryIdToIndex[vector.MemoryId] = i;
                    
                    // Copy to columnar storage
                    Array.Copy(vector.Embedding, 0, _allVectorsFloat32!, i * _dimension, _dimension);
                });
                
                // Validate index consistency
                for (int i = 0; i < vectors.Count; i++)
                {
                    var vector = _vectors[i];
                    if (!_memoryIdToIndex.TryGetValue(vector.MemoryId, out int mappedIndex) || mappedIndex != i)
                    {
                        throw new InvalidOperationException($"Index corruption detected: Memory ID {vector.MemoryId} at position {i} mapped to {mappedIndex}");
                    }
                }
                
                Console.WriteLine($"[MemoryVectorStore] Loaded {vectors.Count} vectors, dimension: {_dimension}, capacity: {_capacity}");
            }
        }
        
        /// <summary>
        /// Add a new vector to the store
        /// </summary>
        public void AddVector(MemoryVectorEntry vector)
        {
            lock (_writeLock)
            {
                // Dimension validation
                if (_dimension > 0 && vector.Embedding.Length != _dimension)
                {
                    throw new InvalidOperationException($"Dimension mismatch: Expected {_dimension}, got {vector.Embedding.Length} for memory {vector.MemoryId}");
                }
                
                int newIndex;
                
                // Try to reuse a deleted slot first
                if (_deletedIndices.Count > 0)
                {
                    newIndex = _deletedIndices.First();
                    _deletedIndices.Remove(newIndex);
                    _vectors[newIndex] = vector;
                }
                else
                {
                    newIndex = _vectors.Count;
                    
                    // CRITICAL FIX: Ensure capacity BEFORE adding to _vectors list
                    // This prevents _vectors.Count from being ahead of actual array capacity
                    EnsureCapacity(newIndex + 1);
                    
                    // Now safe to add the vector
                    _vectors.Add(vector);
                }
                
                _memoryIdToIndex[vector.MemoryId] = newIndex;
                _activeCount++;
                
                // Copy to columnar storage
                Array.Copy(vector.Embedding, 0, _allVectorsFloat32!, newIndex * _dimension, _dimension);
            }
        }
        
        /// <summary>
        /// Update an existing vector
        /// </summary>
        public void UpdateVector(int memoryId, float[] newEmbedding)
        {
            lock (_writeLock)
            {
                if (_memoryIdToIndex.TryGetValue(memoryId, out var index))
                {
                    // Update the vector in list
                    _vectors[index].Embedding = newEmbedding;
                    
                    // Update columnar storage
                    Array.Copy(newEmbedding, 0, _allVectorsFloat32!, index * _dimension, _dimension);
                }
            }
        }
        
        /// <summary>
        /// Remove a vector by memory ID
        /// </summary>
        public void RemoveVector(int memoryId)
        {
            lock (_writeLock)
            {
                if (!_memoryIdToIndex.TryRemove(memoryId, out var index))
                    return;
                
                // Mark as deleted (lazy deletion for performance)
                _vectors[index] = null!;
                _deletedIndices.Add(index);
                _activeCount--;
                
                // Consider compaction if too many deleted entries
                if (_deletedIndices.Count > _activeCount * 0.5 && _activeCount > 100)
                {
                    CompactVectors();
                }
            }
        }
        
        /// <summary>
        /// Search for top K most similar vectors using cosine similarity
        /// </summary>
        public List<(MemoryVectorEntry vector, float score)> Search(float[] queryVector, int topK = 3)
        {
            if (_activeCount == 0 || queryVector.Length != _dimension || _allVectorsFloat32 == null)
                return new List<(MemoryVectorEntry, float)>();
            
            var scores = new float[_vectors.Count];
            
            // Parallel similarity calculations with TensorPrimitives
            Parallel.For(0, _vectors.Count, new ParallelOptions { MaxDegreeOfParallelism = _maxDegreeOfParallelism }, i =>
            {
                if (_deletedIndices.Contains(i) || _vectors[i] == null)
                {
                    scores[i] = float.MinValue;
                    return;
                }
                
                var vectorSpan = _allVectorsFloat32.AsSpan(i * _dimension, _dimension);
                scores[i] = TensorPrimitives.CosineSimilarity(queryVector, vectorSpan);
            });
            
            // Optimized top-K selection for small K
            if (topK <= 20)
            {
                return GetTopResultsLinearScan(scores, topK);
            }
            else
            {
                // Full sort for larger K
                var results = new List<(MemoryVectorEntry vector, float score)>();
                var indices = Enumerable.Range(0, _vectors.Count)
                    .Where(i => !_deletedIndices.Contains(i) && _vectors[i] != null)
                    .OrderByDescending(i => scores[i])
                    .Take(topK)
                    .ToList();
                
                foreach (var idx in indices)
                {
                    results.Add((_vectors[idx], scores[idx]));
                }
                
                return results;
            }
        }
        
        
        private List<(MemoryVectorEntry vector, float score)> GetTopResultsLinearScan(float[] scores, int topK)
        {
            var results = new List<(int index, float score)>(topK);
            
            for (int i = 0; i < scores.Length; i++)
            {
                if (_deletedIndices.Contains(i) || _vectors[i] == null || scores[i] == float.MinValue)
                    continue;
                
                if (results.Count < topK)
                {
                    results.Add((i, scores[i]));
                    if (results.Count == topK)
                    {
                        results.Sort((a, b) => a.score.CompareTo(b.score));
                    }
                }
                else if (scores[i] > results[0].score)
                {
                    results[0] = (i, scores[i]);
                    
                    // Bubble up to maintain sorted order
                    for (int j = 0; j < results.Count - 1 && results[j].score > results[j + 1].score; j++)
                    {
                        (results[j], results[j + 1]) = (results[j + 1], results[j]);
                    }
                }
            }
            
            // Convert to final format in descending order
            var finalResults = new List<(MemoryVectorEntry vector, float score)>(results.Count);
            for (int i = results.Count - 1; i >= 0; i--)
            {
                finalResults.Add((_vectors[results[i].index], results[i].score));
            }
            
            return finalResults;
        }
        
        private void EnsureCapacity(int requiredCapacity)
        {
            if (_capacity >= requiredCapacity) return;
            
            // Validate state consistency before capacity expansion
            int sourceArrayLength = _allVectorsFloat32?.Length ?? 0;
            int expectedCopyLength = _vectors.Count * _dimension;
            
            // Critical validation: ensure we don't exceed source array bounds
            if (_allVectorsFloat32 != null && sourceArrayLength < expectedCopyLength)
            {
                Console.WriteLine($"🚨 INTERNAL ERROR: Array copy bounds violation prevented");
                Console.WriteLine($"   This should not happen with the fixed AddVector logic");
                Console.WriteLine($"   Source: {sourceArrayLength}, Expected: {expectedCopyLength}");
                throw new InvalidOperationException($"Internal state corruption: {sourceArrayLength} < {expectedCopyLength}");
            }
            
            // Grow by 50% (more conservative than doubling)
            _capacity = Math.Max(requiredCapacity, (int)(_capacity * 1.5));
            var newArray = new float[_capacity * _dimension];
            
            if (_allVectorsFloat32 != null)
            {
                // Safe copy: we've already validated bounds above
                Array.Copy(_allVectorsFloat32, newArray, expectedCopyLength);
            }
            
            _allVectorsFloat32 = newArray;
        }
        
        private void CompactVectors()
        {
            var newVectors = new List<MemoryVectorEntry>(_activeCount);
            var newMemoryIdToIndex = new ConcurrentDictionary<int, int>();
            var newAllVectorsFloat32 = new float[_activeCount * _dimension];
            
            int newIndex = 0;
            for (int i = 0; i < _vectors.Count; i++)
            {
                if (!_deletedIndices.Contains(i) && _vectors[i] != null)
                {
                    newVectors.Add(_vectors[i]);
                    newMemoryIdToIndex[_vectors[i].MemoryId] = newIndex;
                    
                    // Copy vector data
                    Array.Copy(_allVectorsFloat32!, i * _dimension, newAllVectorsFloat32, newIndex * _dimension, _dimension);
                    newIndex++;
                }
            }
            
            _vectors.Clear();
            _vectors.AddRange(newVectors);
            _memoryIdToIndex.Clear();
            foreach (var kvp in newMemoryIdToIndex)
            {
                _memoryIdToIndex[kvp.Key] = kvp.Value;
            }
            _deletedIndices.Clear();
            _allVectorsFloat32 = newAllVectorsFloat32;
            _capacity = _activeCount;
            
            Console.WriteLine($"[MemoryVectorStore] Compacted vectors: {_activeCount} active entries");
        }
        
        /// <summary>
        /// Get memory usage statistics
        /// </summary>
        public MemorySystemStats GetMemoryStats()
        {
            long vectorMemory = 0;
            
            if (_allVectorsFloat32 != null)
            {
                vectorMemory = _allVectorsFloat32.Length * sizeof(float);
            }
            
            return new MemorySystemStats
            {
                TotalMemoryMB = vectorMemory / (1024.0 * 1024.0),
                VectorCount = _activeCount,
                DimensionSize = _dimension,
                Precision = "Float32",
                SearchMethod = "TensorPrimitives (Parallel)"
            };
        }
        
    }
}