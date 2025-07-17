using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CSharpMcpServer.Models;
using Microsoft.Data.Sqlite;

namespace CSharpMcpServer.Storage
{
    /// <summary>
    /// SQLite storage for memory entries, isolated per project
    /// </summary>
    public class MemoryStorage : IDisposable
    {
        private readonly string _projectName;
        private readonly string _dbPath;
        private SqliteConnection? _connection;
        private readonly JsonSerializerOptions _jsonOptions;
        
        public MemoryStorage(string projectName)
        {
            _projectName = projectName;
            
            // Create memories subfolder
            var baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "csharp-mcp-server",
                "memories"
            );
            Directory.CreateDirectory(baseDir);
            
            _dbPath = Path.Combine(baseDir, $"memory-{projectName}.db");
            
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false
            };
            
            InitializeDatabase();
        }
        
        private void InitializeDatabase()
        {
            _connection = new SqliteConnection($"Data Source={_dbPath}");
            _connection.Open();
            
            // Create memories table with graph structure support
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS memories (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    guid TEXT UNIQUE NOT NULL DEFAULT (lower(hex(randomblob(16)))),
                    project_name TEXT NOT NULL,
                    memory_name TEXT NOT NULL,
                    alias TEXT UNIQUE,
                    tags TEXT NOT NULL,
                    full_document_text TEXT NOT NULL,
                    timestamp TEXT NOT NULL,
                    parent_memory_id INTEGER,
                    child_memory_ids TEXT,
                    lines_count INTEGER,
                    size_kb REAL,
                    created_at TEXT DEFAULT CURRENT_TIMESTAMP,
                    updated_at TEXT DEFAULT CURRENT_TIMESTAMP,
                    FOREIGN KEY (parent_memory_id) REFERENCES memories(id)
                );
                
                CREATE INDEX IF NOT EXISTS idx_memories_project ON memories(project_name);
                CREATE INDEX IF NOT EXISTS idx_memories_timestamp ON memories(timestamp);
                CREATE INDEX IF NOT EXISTS idx_memories_parent ON memories(parent_memory_id);
                CREATE INDEX IF NOT EXISTS idx_memories_name ON memories(memory_name);
                CREATE INDEX IF NOT EXISTS idx_memories_alias ON memories(alias);
                
                -- Vectors table for embeddings
                CREATE TABLE IF NOT EXISTS memory_vectors (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    memory_id INTEGER NOT NULL,
                    project_name TEXT NOT NULL,
                    content TEXT NOT NULL,
                    embedding BLOB NOT NULL,
                    precision TEXT NOT NULL,
                    dimension INTEGER NOT NULL,
                    indexed_at TEXT NOT NULL,
                    FOREIGN KEY (memory_id) REFERENCES memories(id) ON DELETE CASCADE
                );
                
                CREATE INDEX IF NOT EXISTS idx_memory_vectors_memory ON memory_vectors(memory_id);
                CREATE INDEX IF NOT EXISTS idx_memory_vectors_project ON memory_vectors(project_name);
            ";
            cmd.ExecuteNonQuery();
        }
        
        public async Task<Memory> AddMemoryAsync(Memory memory)
        {
            if (_connection == null) throw new InvalidOperationException("Database not initialized");
            
            memory.ProjectName = _projectName; // Ensure project name matches
            
            // Auto-generate alias if not provided
            if (string.IsNullOrEmpty(memory.Alias))
            {
                memory.Alias = Utils.AliasGenerator.GenerateUniqueAlias(memory.MemoryName, alias => AliasExistsAsync(alias).Result);
            }
            
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO memories (
                    guid, project_name, memory_name, alias, tags, full_document_text, 
                    timestamp, parent_memory_id, child_memory_ids, lines_count, size_kb
                ) VALUES (
                    @guid, @project_name, @memory_name, @alias, @tags, @full_document_text,
                    @timestamp, @parent_memory_id, @child_memory_ids, @lines_count, @size_kb
                );
                SELECT last_insert_rowid();";
            
            cmd.Parameters.AddWithValue("@guid", memory.Guid);
            cmd.Parameters.AddWithValue("@project_name", memory.ProjectName);
            cmd.Parameters.AddWithValue("@memory_name", memory.MemoryName);
            cmd.Parameters.AddWithValue("@alias", (object?)memory.Alias ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@tags", JsonSerializer.Serialize(memory.Tags, _jsonOptions));
            cmd.Parameters.AddWithValue("@full_document_text", memory.FullDocumentText);
            cmd.Parameters.AddWithValue("@timestamp", memory.Timestamp.ToString("O"));
            cmd.Parameters.AddWithValue("@parent_memory_id", (object?)memory.ParentMemoryId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@child_memory_ids", JsonSerializer.Serialize(memory.ChildMemoryIds, _jsonOptions));
            cmd.Parameters.AddWithValue("@lines_count", memory.LinesCount);
            cmd.Parameters.AddWithValue("@size_kb", memory.SizeInKBytes);
            
            // Get the auto-generated ID
            var newId = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            memory.Id = newId;
            
            // Update parent's child list if applicable
            if (memory.ParentMemoryId.HasValue)
            {
                await UpdateParentChildListAsync(memory.ParentMemoryId.Value, memory.Id, add: true);
            }
            
            return memory;
        }
        
        public async Task<Memory?> GetMemoryAsync(int memoryId)
        {
            if (_connection == null) throw new InvalidOperationException("Database not initialized");
            
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT id, guid, project_name, memory_name, alias, tags, full_document_text,
                       timestamp, parent_memory_id, child_memory_ids
                FROM memories
                WHERE id = @id AND project_name = @project_name";
            
            cmd.Parameters.AddWithValue("@id", memoryId);
            cmd.Parameters.AddWithValue("@project_name", _projectName);
            
            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return ReadMemoryFromReader(reader);
            }
            
            return null;
        }

        public async Task<Memory?> GetMemoryByAliasAsync(string alias)
        {
            if (_connection == null) throw new InvalidOperationException("Database not initialized");
            
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT id, guid, project_name, memory_name, alias, tags, full_document_text,
                       timestamp, parent_memory_id, child_memory_ids
                FROM memories
                WHERE alias = @alias AND project_name = @project_name";
            
            cmd.Parameters.AddWithValue("@alias", alias);
            cmd.Parameters.AddWithValue("@project_name", _projectName);
            
            using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return ReadMemoryFromReader(reader);
            }
            
            return null;
        }

        public async Task<Memory?> GetMemoryByIdOrAliasAsync(string idOrAlias)
        {
            // Try to parse as integer ID first
            if (int.TryParse(idOrAlias, out var id))
            {
                return await GetMemoryAsync(id);
            }
            
            // Otherwise treat as alias
            return await GetMemoryByAliasAsync(idOrAlias);
        }

        public async Task<bool> AliasExistsAsync(string alias)
        {
            if (_connection == null) throw new InvalidOperationException("Database not initialized");
            
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT COUNT(*)
                FROM memories
                WHERE alias = @alias AND project_name = @project_name";
            
            cmd.Parameters.AddWithValue("@alias", alias);
            cmd.Parameters.AddWithValue("@project_name", _projectName);
            
            var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            return count > 0;
        }
        
        public async Task<bool> DeleteMemoryAsync(int memoryId)
        {
            if (_connection == null) throw new InvalidOperationException("Database not initialized");
            
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                DELETE FROM memories 
                WHERE id = @id AND project_name = @project_name";
            
            cmd.Parameters.AddWithValue("@id", memoryId);
            cmd.Parameters.AddWithValue("@project_name", _projectName);
            
            var rowsAffected = await cmd.ExecuteNonQueryAsync();
            return rowsAffected > 0;
        }
        
        public async Task<List<Memory>> GetAllMemoriesAsync()
        {
            if (_connection == null) throw new InvalidOperationException("Database not initialized");
            
            var memories = new List<Memory>();
            
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT id, guid, project_name, memory_name, alias, tags, full_document_text,
                       timestamp, parent_memory_id, child_memory_ids
                FROM memories
                WHERE project_name = @project_name
                ORDER BY timestamp DESC";
            
            cmd.Parameters.AddWithValue("@project_name", _projectName);
            
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                memories.Add(ReadMemoryFromReader(reader));
            }
            
            return memories;
        }
        
        public async Task<List<Memory>> GetChildMemoriesAsync(int parentMemoryId, int limit = 10)
        {
            if (_connection == null) throw new InvalidOperationException("Database not initialized");
            
            var memories = new List<Memory>();
            
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT id, guid, project_name, memory_name, alias, tags, full_document_text,
                       timestamp, parent_memory_id, child_memory_ids
                FROM memories
                WHERE parent_memory_id = @parent_id AND project_name = @project_name
                ORDER BY timestamp DESC
                LIMIT @limit";
            
            cmd.Parameters.AddWithValue("@parent_id", parentMemoryId);
            cmd.Parameters.AddWithValue("@project_name", _projectName);
            cmd.Parameters.AddWithValue("@limit", limit);
            
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                memories.Add(ReadMemoryFromReader(reader));
            }
            
            return memories;
        }
        
        // Vector storage methods
        public async Task StoreVectorAsync(MemoryVectorEntry vector)
        {
            if (_connection == null) throw new InvalidOperationException("Database not initialized");
            
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                INSERT OR REPLACE INTO memory_vectors (
                    memory_id, project_name, content, embedding, 
                    precision, dimension, indexed_at
                ) VALUES (
                    @memory_id, @project_name, @content, @embedding,
                    @precision, @dimension, @indexed_at
                );
                SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("@memory_id", vector.MemoryId);
            cmd.Parameters.AddWithValue("@project_name", vector.ProjectName);
            cmd.Parameters.AddWithValue("@content", vector.Content);
            
            // Store embedding as binary
            byte[] embeddingBytes;
            if (vector.Precision == VectorPrecision.Float32)
            {
                embeddingBytes = new byte[vector.Embedding.Length * sizeof(float)];
                Buffer.BlockCopy(vector.Embedding, 0, embeddingBytes, 0, embeddingBytes.Length);
            }
            else
            {
                embeddingBytes = new byte[vector.EmbeddingHalf.Length * sizeof(ushort)];
                Buffer.BlockCopy(vector.EmbeddingHalf, 0, embeddingBytes, 0, embeddingBytes.Length);
            }
            
            cmd.Parameters.AddWithValue("@embedding", embeddingBytes);
            cmd.Parameters.AddWithValue("@precision", vector.Precision.ToString());
            cmd.Parameters.AddWithValue("@dimension", vector.Precision == VectorPrecision.Float32 ? 
                vector.Embedding.Length : vector.EmbeddingHalf.Length);
            cmd.Parameters.AddWithValue("@indexed_at", vector.IndexedAt.ToString("O"));
            
            // Get the auto-generated ID
            var newId = Convert.ToInt32(await cmd.ExecuteScalarAsync());
            vector.Id = newId;
        }
        
        public async Task<List<MemoryVectorEntry>> GetAllVectorsAsync()
        {
            if (_connection == null) throw new InvalidOperationException("Database not initialized");
            
            var vectors = new List<MemoryVectorEntry>();
            
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                SELECT id, memory_id, project_name, content, embedding, 
                       precision, dimension, indexed_at
                FROM memory_vectors
                WHERE project_name = @project_name";
            
            cmd.Parameters.AddWithValue("@project_name", _projectName);
            
            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var vector = new MemoryVectorEntry
                {
                    Id = reader.GetInt32(0),
                    MemoryId = reader.GetInt32(1),
                    ProjectName = reader.GetString(2),
                    Content = reader.GetString(3),
                    Precision = Enum.Parse<VectorPrecision>(reader.GetString(5)),
                    IndexedAt = DateTime.Parse(reader.GetString(7))
                };
                
                var embeddingBytes = (byte[])reader["embedding"];
                var dimension = reader.GetInt32(6);
                
                if (vector.Precision == VectorPrecision.Float32)
                {
                    vector.Embedding = new float[dimension];
                    Buffer.BlockCopy(embeddingBytes, 0, vector.Embedding, 0, embeddingBytes.Length);
                }
                else
                {
                    vector.EmbeddingHalf = new Half[dimension];
                    Buffer.BlockCopy(embeddingBytes, 0, vector.EmbeddingHalf, 0, embeddingBytes.Length);
                }
                
                vectors.Add(vector);
            }
            
            return vectors;
        }
        
        public async Task DeleteVectorAsync(int memoryId)
        {
            if (_connection == null) throw new InvalidOperationException("Database not initialized");
            
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                DELETE FROM memory_vectors 
                WHERE memory_id = @memory_id AND project_name = @project_name";
            
            cmd.Parameters.AddWithValue("@memory_id", memoryId);
            cmd.Parameters.AddWithValue("@project_name", _projectName);
            
            await cmd.ExecuteNonQueryAsync();
        }
        
        private async Task UpdateParentChildListAsync(int parentId, int childId, bool add)
        {
            if (_connection == null) throw new InvalidOperationException("Database not initialized");
            
            var parent = await GetMemoryAsync(parentId);
            if (parent == null) return;
            
            if (add && !parent.ChildMemoryIds.Contains(childId))
            {
                parent.ChildMemoryIds.Add(childId);
            }
            else if (!add)
            {
                parent.ChildMemoryIds.Remove(childId);
            }
            
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                UPDATE memories 
                SET child_memory_ids = @child_ids
                WHERE id = @id";
            
            cmd.Parameters.AddWithValue("@child_ids", JsonSerializer.Serialize(parent.ChildMemoryIds, _jsonOptions));
            cmd.Parameters.AddWithValue("@id", parentId);
            
            await cmd.ExecuteNonQueryAsync();
        }
        
        private Memory ReadMemoryFromReader(IDataReader reader)
        {
            return new Memory
            {
                Id = reader.GetInt32(0),
                Guid = reader.GetString(1),
                ProjectName = reader.GetString(2),
                MemoryName = reader.GetString(3),
                Alias = reader.IsDBNull(4) ? null : reader.GetString(4),
                Tags = JsonSerializer.Deserialize<List<string>>(reader.GetString(5), _jsonOptions) ?? new(),
                FullDocumentText = reader.GetString(6),
                Timestamp = DateTime.Parse(reader.GetString(7)),
                ParentMemoryId = reader.IsDBNull(8) ? null : reader.GetInt32(8),
                ChildMemoryIds = JsonSerializer.Deserialize<List<int>>(reader.GetString(9), _jsonOptions) ?? new()
            };
        }
        
        public void Dispose()
        {
            _connection?.Dispose();
        }
    }
}