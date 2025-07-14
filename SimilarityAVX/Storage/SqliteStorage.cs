using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Dapper;
using CSharpMcpServer.Models;
using System.Runtime.CompilerServices;

namespace CSharpMcpServer.Storage;

public class SqliteStorage
{
    private readonly string _dbPath;
    
    public SqliteStorage(string? projectName = null)
    {
        // If project name is provided, create project-specific database
        var dbFileName = string.IsNullOrEmpty(projectName) 
            ? "codesearch.db" 
            : $"codesearch-{SanitizeProjectName(projectName)}.db";
            
        _dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "csharp-mcp-server",
            dbFileName
        );
        
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath)!);
        InitializeDatabase();
        
        Console.Error.WriteLine($"[SqliteStorage] Using database: {dbFileName}");
    }
    
    private static string SanitizeProjectName(string projectName)
    {
        // Replace invalid filename characters with underscore
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = projectName;
        foreach (var c in invalid)
        {
            sanitized = sanitized.Replace(c, '_');
        }
        return sanitized.ToLowerInvariant();
    }
    
    private void InitializeDatabase()
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        
        conn.Execute(@"
            CREATE TABLE IF NOT EXISTS chunks (
                id TEXT PRIMARY KEY,
                file_path TEXT NOT NULL,
                start_line INTEGER,
                end_line INTEGER,
                content TEXT,
                chunk_type TEXT,
                embedding BLOB,
                precision INTEGER DEFAULT 0,
                indexed_at TEXT DEFAULT CURRENT_TIMESTAMP
            );
            
            CREATE INDEX IF NOT EXISTS idx_file_path ON chunks(file_path);
            CREATE INDEX IF NOT EXISTS idx_chunk_type ON chunks(chunk_type);
            
            CREATE TABLE IF NOT EXISTS metadata (
                key TEXT PRIMARY KEY,
                value TEXT
            );
        ");
    }
    
    public void SaveChunks(IEnumerable<(string id, string path, int start, int end, string content, byte[] embedding, VectorPrecision precision, string chunkType)> chunks)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        
        using var transaction = conn.BeginTransaction();
        
        foreach (var chunk in chunks)
        {
            conn.Execute(@"
                INSERT OR REPLACE INTO chunks 
                (id, file_path, start_line, end_line, content, chunk_type, embedding, precision)
                VALUES (@id, @path, @start, @end, @content, @type, @embedding, @precision)",
                new
                {
                    id = chunk.id,
                    path = chunk.path,
                    start = chunk.start,
                    end = chunk.end,
                    content = chunk.content,
                    type = chunk.chunkType,
                    embedding = chunk.embedding,
                    precision = (int)chunk.precision
                }, transaction);
        }
        
        transaction.Commit();
    }
    
    public List<ChunkRecord> GetChunksByIds(IEnumerable<string> ids)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        var idsList = ids.ToList();
        if (!idsList.Any()) return new List<ChunkRecord>();
        
        return conn.Query<ChunkRecord>(
            "SELECT * FROM chunks WHERE id IN @ids",
            new { ids = idsList }
        ).ToList();
    }
    
    public VectorMemoryStore LoadToMemory(int dimension, VectorPrecision precision, int maxDegreeOfParallelism = 16)
    {
        var store = new VectorMemoryStore(dimension, precision, maxDegreeOfParallelism);
        
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        var chunks = conn.Query<ChunkRecord>(@"
            SELECT id, file_path, start_line, end_line, content, chunk_type, embedding, precision
            FROM chunks
        ");
        
        foreach (var chunk in chunks)
        {
            store.AddEntry(chunk.id, chunk.file_path, chunk.start_line, 
                          chunk.end_line, chunk.content, chunk.embedding, 
                          (VectorPrecision)chunk.precision, chunk.chunk_type);
        }
        
        store.BuildIndex();
        return store;
    }
    
    public void SaveMetadata(string key, string value)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Execute(@"
            INSERT OR REPLACE INTO metadata (key, value) 
            VALUES (@key, @value)",
            new { key, value });
    }
    
    public string? GetMetadata(string key)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        return conn.QuerySingleOrDefault<string>(
            "SELECT value FROM metadata WHERE key = @key",
            new { key });
    }
    
    public List<string> DeleteByPath(string filePath)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        
        // Get IDs of chunks to be deleted
        var deletedIds = conn.Query<string>(
            "SELECT id FROM chunks WHERE file_path = @path", 
            new { path = filePath }
        ).ToList();
        
        // Delete the chunks
        conn.Execute("DELETE FROM chunks WHERE file_path = @path", new { path = filePath });
        
        return deletedIds;
    }
    
    public void Clear()
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Execute("DELETE FROM chunks");
        conn.Execute("DELETE FROM metadata");
        conn.Execute("VACUUM");
    }
    
    private string DetermineChunkType(string content)
    {
        if (content.Contains("namespace ")) return "namespace";
        if (content.Contains("class ")) return "class";
        if (content.Contains("interface ")) return "interface";
        if (content.Contains("public ") || content.Contains("private ") || 
            content.Contains("protected ") || content.Contains("internal "))
            return "method";
        return "other";
    }
    
    public class ChunkRecord
    {
        public string id { get; set; } = "";
        public string file_path { get; set; } = "";
        public int start_line { get; set; }
        public int end_line { get; set; }
        public string content { get; set; } = "";
        public string chunk_type { get; set; } = "other";
        public byte[] embedding { get; set; } = Array.Empty<byte>();
        public int precision { get; set; }
    }
}