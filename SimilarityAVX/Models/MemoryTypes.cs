using System;
using System.Collections.Generic;

namespace CSharpMcpServer.Models
{
    /// <summary>
    /// Represents a memory entry in the system
    /// </summary>
    public class Memory
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string ProjectName { get; set; } = string.Empty;
        public string MemoryName { get; set; } = string.Empty;
        public List<string> Tags { get; set; } = new();
        public string FullDocumentText { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        
        // Graph structure foundation (Phase 2)
        public string? ParentMemoryId { get; set; }
        public List<string> ChildMemoryIds { get; set; } = new();
        
        // Metadata for LLM
        public int LinesCount => string.IsNullOrEmpty(FullDocumentText) ? 0 : FullDocumentText.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Length;
        public double SizeInKBytes => string.IsNullOrEmpty(FullDocumentText) ? 0 : System.Text.Encoding.UTF8.GetByteCount(FullDocumentText) / 1024.0;
        
        // For display
        public string AgeDisplay
        {
            get
            {
                var age = DateTime.UtcNow - Timestamp;
                if (age.TotalDays < 1)
                    return "Stored today";
                else if (age.TotalDays < 2)
                    return "Stored 1 day ago";
                else
                    return $"Stored {(int)age.TotalDays} days ago";
            }
        }
    }
    
    /// <summary>
    /// Represents a memory search result with similarity score
    /// </summary>
    public class MemorySearchResult
    {
        public Memory Memory { get; set; } = new();
        public float Score { get; set; }
        public string SnippetText { get; set; } = string.Empty;
        public int SnippetLineCount { get; set; }
    }
    
    /// <summary>
    /// Represents a memory entry stored in the vector database
    /// </summary>
    public class MemoryVectorEntry
    {
        public string Id { get; set; } = string.Empty;
        public string MemoryId { get; set; } = string.Empty;
        public string ProjectName { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public float[] Embedding { get; set; } = Array.Empty<float>();
        public Half[] EmbeddingHalf { get; set; } = Array.Empty<Half>();
        public VectorPrecision Precision { get; set; }
        public DateTime IndexedAt { get; set; } = DateTime.UtcNow;
    }
    
    /// <summary>
    /// Configuration for memory search operations
    /// </summary>
    public class MemorySearchConfig
    {
        public int TopK { get; set; } = 3;
        public int SnippetLineCount { get; set; } = 10;
        public bool IncludeMetadata { get; set; } = true;
        public bool IncludeGraphRelations { get; set; } = false;
    }
    
    /// <summary>
    /// Memory system statistics
    /// </summary>
    public class MemorySystemStats
    {
        public double TotalMemoryMB { get; set; }
        public int VectorCount { get; set; }
        public int DimensionSize { get; set; }
        public string Precision { get; set; } = "Float32";
        public string SearchMethod { get; set; } = "TensorPrimitives";
    }
}