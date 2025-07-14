using System;
using System.Collections.Generic;
using System.Linq;

namespace CSharpMcpServer.Models;

public record CodeChunk(
    string Id,
    string Content,
    string FilePath,
    int StartLine,
    int EndLine,
    string ChunkType)
{
    // Additional metadata for ranking
    public DateTime LastModified { get; init; } = DateTime.UtcNow;
    public double FileImportance { get; init; } = 1.0;
}

public record SearchResult(
    string FilePath,
    int StartLine,
    int EndLine,
    string Content,
    double Score,
    string ChunkType);

public record IndexStats(
    int FilesProcessed,
    int ChunksCreated,
    int FilesSkipped,
    TimeSpan Duration,
    FileChanges? Changes = null);

public record FileChanges(
    List<string> Added,
    List<string> Modified,
    List<string> Removed)
{
    public bool HasChanges => Added.Any() || Modified.Any() || Removed.Any();
}

public class IndexProgress
{
    public string Phase { get; set; } = "";
    public int Current { get; set; }
    public int Total { get; set; }
    public double Percentage => Total > 0 ? (double)Current / Total * 100 : 0;
}

public enum VectorPrecision
{
    Float32,
    Half
}

public class VectorStorageMetadata
{
    public int Dimension { get; set; }
    public VectorPrecision Precision { get; set; }
    public bool SupportsAvx512 { get; set; }
    public bool SupportsAvx2 { get; set; }
    public string CpuCapabilities { get; set; } = "";
}

public class MemoryStats
{
    public int ChunkCount { get; set; }
    public int FileCount { get; set; }
    public double MemoryUsageMB { get; set; }
    public double VectorsMemoryMB { get; set; }
    public double MetadataMemoryMB { get; set; }
    public VectorPrecision Precision { get; set; }
}

public class IndexStatistics
{
    public int ChunkCount { get; set; }
    public int FileCount { get; set; }
    public double MemoryUsageMB { get; set; }
    public int VectorDimension { get; set; }
    public VectorPrecision Precision { get; set; }
    public string SearchMethod { get; set; } = "";
}