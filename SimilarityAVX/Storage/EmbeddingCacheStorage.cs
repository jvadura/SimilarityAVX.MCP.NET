using System;
using System.Data;
using Microsoft.Data.Sqlite;
using System.IO;
using System.Threading.Tasks;

namespace CSharpMcpServer.Storage;

/// <summary>
/// SQLite storage for embedding cache. Lean, fast, and simple.
/// </summary>
public class EmbeddingCacheStorage : IDisposable
{
    private readonly string _dbPath;
    private readonly string _connectionString;
    private bool _disposed;

    public EmbeddingCacheStorage(string? dbPath = null)
    {
        _dbPath = dbPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "csharp-mcp-server",
            "embedding_cache.db"
        );

        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _connectionString = $"Data Source={_dbPath};Mode=ReadWriteCreate;Cache=Shared;";
        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        
        // Simple, focused schema - no over-engineering
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS embedding_cache (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                content_hash TEXT NOT NULL,
                embedding_type TEXT NOT NULL,
                model TEXT NOT NULL,
                embedding BLOB NOT NULL,
                created_at INTEGER NOT NULL,
                last_accessed INTEGER NOT NULL,
                access_count INTEGER DEFAULT 1,
                project_name TEXT,
                UNIQUE(content_hash, embedding_type, model, project_name)
            );

            CREATE INDEX IF NOT EXISTS idx_cache_lookup 
                ON embedding_cache(content_hash, embedding_type, model, project_name);
            
            CREATE INDEX IF NOT EXISTS idx_cache_lru 
                ON embedding_cache(last_accessed);
        ";
        cmd.ExecuteNonQuery();
    }

    public async Task<byte[]?> GetEmbeddingAsync(string contentHash, string embeddingType, string model, string? projectName)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            UPDATE embedding_cache 
            SET last_accessed = @now, access_count = access_count + 1
            WHERE content_hash = @hash 
                AND embedding_type = @type 
                AND model = @model 
                AND (project_name = @project OR (project_name IS NULL AND @project IS NULL))
            RETURNING embedding;
        ";
        
        cmd.Parameters.AddWithValue("@hash", contentHash);
        cmd.Parameters.AddWithValue("@type", embeddingType);
        cmd.Parameters.AddWithValue("@model", model);
        cmd.Parameters.AddWithValue("@project", projectName ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        var result = await cmd.ExecuteScalarAsync();
        return result as byte[];
    }

    public async Task StoreEmbeddingAsync(string contentHash, byte[] embedding, string embeddingType, string model, string? projectName)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            INSERT OR REPLACE INTO embedding_cache 
                (content_hash, embedding_type, model, embedding, created_at, last_accessed, access_count, project_name)
            VALUES 
                (@hash, @type, @model, @embedding, @now, @now, 1, @project)
        ";

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        cmd.Parameters.AddWithValue("@hash", contentHash);
        cmd.Parameters.AddWithValue("@type", embeddingType);
        cmd.Parameters.AddWithValue("@model", model);
        cmd.Parameters.AddWithValue("@embedding", embedding);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.Parameters.AddWithValue("@project", projectName ?? (object)DBNull.Value);

        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<int> GetCacheSizeAsync()
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM embedding_cache";
        
        var result = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(result);
    }

    public async Task<long> GetCacheSizeBytesAsync()
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT SUM(LENGTH(embedding)) FROM embedding_cache";
        
        var result = await cmd.ExecuteScalarAsync();
        return result == DBNull.Value ? 0 : Convert.ToInt64(result);
    }

    public async Task<int> ClearOldEntriesAsync(int daysOld)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            DELETE FROM embedding_cache 
            WHERE last_accessed < @cutoff
        ";
        
        var cutoff = DateTimeOffset.UtcNow.AddDays(-daysOld).ToUnixTimeSeconds();
        cmd.Parameters.AddWithValue("@cutoff", cutoff);

        return await cmd.ExecuteNonQueryAsync();
    }

    public async Task ClearProjectCacheAsync(string projectName)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM embedding_cache WHERE project_name = @project";
        cmd.Parameters.AddWithValue("@project", projectName);

        await cmd.ExecuteNonQueryAsync();
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            // SQLite handles connection pooling internally
            _disposed = true;
        }
    }
}