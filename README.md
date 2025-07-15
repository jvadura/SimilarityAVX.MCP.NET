# C# Semantic Code Search MCP Server

![.NET 9.0](https://img.shields.io/badge/.NET-9.0-purple)
![MCP](https://img.shields.io/badge/MCP-Compatible-blue)
![License](https://img.shields.io/badge/License-MIT-green)

⚡ A high-performance semantic code search tool for C# projects using the Model Context Protocol (MCP). Built with .NET 9 and optimized with AVX-512 SIMD acceleration.

🎯 **Search code by meaning, not text** - Find implementations using natural language descriptions instead of exact string matching.

## 🪟 Windows Host Deployment Recommended

**Important**: This tool should run on your Windows host machine, not in WSL. Claude Code instances running in WSL can connect to the Windows-hosted server.

Why? File monitoring (FileSystemWatcher) doesn't work reliably in WSL when watching Windows filesystems. Running on Windows ensures real-time index updates work correctly.

> **WSL Users**: If you must run in WSL, disable file monitoring (`"enableFileWatching": false`) and enable periodic rescan (`"enablePeriodicRescan": true`) to check for changes every 30 minutes.

## Why Choose This?

Super simple setup - just select your embedding provider and run! The server runs as a background service on Windows, accessible from any Claude Code instance.

| Feature | Semantic Search | Traditional grep |
|---------|----------------|------------------|
| Natural language queries | ✅ Yes | ❌ No |
| Typo tolerance | ✅ Yes | ❌ No |
| Relevance scoring | ✅ Yes | ❌ No |
| Conceptual matching | ✅ Yes | ❌ No |
| 100% completeness | ❌ No | ✅ Yes |
| Setup required | ✅ Indexing | ❌ None |
| Speed | ~50-200ms | ~20-50ms |

**Tested embedding models:**
- **vLLM Qwen3-8B** (recommended) - Best overall performance, open weights
- **VoyageAI voyage-code-3** - Excellent cloud option when local models aren't viable
- **Snowflake Arctic Embed2** - Good baseline option via Ollama

Smaller models often compromise too much on quality. For production use, we recommend 8B+ parameter models.

## What It Does

Enables natural language search over C# codebases. Instead of exact string matching, find code by describing what it does:
- "email notifications" → finds SendGrid implementation
- "rate limiting" → finds anti-flood protection code
- "user authentication" → finds login controllers and auth services

## Features

- **Semantic search** with relevance scoring (0.0-1.0)
- **Multi-project support** - index multiple C# projects separately
- **Smart incremental updates** - only re-indexes changed files
- **Extended context viewing** - see surrounding code lines
- **Code structure filtering** - search by class, method, interface, etc.
- **Fast performance** - ~50ms average search time on 25k vectors
- **Hardware acceleration** - Uses .NET 9 TensorPrimitives with AVX-512 support

## Quick Start

### Prerequisites
- .NET 9.0 SDK
- Embedding API (OpenAI, Ollama, or compatible)
- Claude Code for MCP SSE (http) integration

### Installation

1. Clone and build:
```bash
git clone https://github.com/jvadura/SimilarityAVX.MCP.NET
cd SimilarityAVX.MCP.NET/SimilarityAVX
dotnet build -c Release
```

2. Configure embedding API in `config.json`:
```json
{
  "embedding": {
    "provider": "VoyageAI",  // or "OpenAI" for OpenAI-compatible APIs
    "apiUrl": "https://api.voyageai.com/v1/",
    "apiKey": "",  // Set via EMBEDDING_API_KEY env var
    "model": "voyage-code-3",
    "dimension": 2048,
    "precision": "Float32",
    "batchSize": 50,
    "maxRetries": 6,
    "retryDelayMs": 1000
  },
  "security": {
    "allowedDirectories": ["E:\\"],     // Whitelist directories for indexing
    "enablePathValidation": true        // Enforce directory restrictions
  },
  "monitoring": {
    "enableAutoReindex": true,          // Auto-sync with code changes
    "verifyOnStartup": true,            // Check for changes on startup
    "debounceDelaySeconds": 60,         // Wait after last file change
    "enableFileWatching": true,         // Real-time monitoring (Windows only)
    "enablePeriodicRescan": false,      // Enable for WSL users
    "periodicRescanMinutes": 30         // How often to check for changes
  }
}
```

**Supported embedding providers:**
- **[VoyageAI](https://www.voyageai.com/)** - Optimized for code search (voyage-code-3)
- **[Ollama](https://ollama.ai/)** - For local models (use provider: "OpenAI")
- **[OpenAI](https://openai.com/)** - text-embedding-3-small/large
- **Any OpenAI-compatible API** - vLLM, TEI, etc.

3. Set your API key and start the server:
```bash
export EMBEDDING_API_KEY="your-api-key"
dotnet run
```
Server will start on `http://0.0.0.0:5001`

4. Add to Claude Code:
```bash
claude mcp add csharp-search --transport sse http://localhost:5001/sse
```

**For remote access** (if running on different machine):
```bash
claude mcp add csharp-search --transport sse http://YOUR_IP:5001/sse
```

## Available Tools

**Search Tools:**
- `SearchProject` - Basic semantic search with relevance scoring ⭐
- `SearchWithContext` - Extended context viewing (15-20 lines recommended) ⭐
- `SearchWithFilters` - Filter by file types and code structures ⭐
- `BatchSearch` - Multiple queries at once (3-5 optimal) ⭐
- `GetFilterHelp` - Comprehensive help for search filters

**Management Tools:**
- `IndexProject` - Index or update projects (use `--force true` for reindexing)
- `ListProjects` - Show all indexed projects
- `GetProjectStats` - Memory and performance info
- `ClearProjectIndex` - Remove project index

## Performance

### Real-World Performance
Tested on a 761-file enterprise Blazor application:
- **Index size**: 73.6 MB for 5,575 code chunks
- **Search time**: ~176ms first search (with embeddings), ~60ms subsequent searches
- **Memory usage**: Efficient (1MB per ~220 chunks)
- **Hardware acceleration**: AVX-512 SIMD support for maximum speed

### Benchmark Results
Synthetic benchmark comparing custom AVX-512 vs .NET 9 TensorPrimitives (25k vectors, 4096 dimensions):

| Implementation | Time per Search | Throughput | GFLOPS | Notes |
|----------------|-----------------|------------|--------|-------|
| AVX-512 (Custom) | 13.7ms | 1.82M/sec | 29.9 | Lower variance, predictable |
| TensorPrimitives | 10.6ms | 2.35M/sec | 38.5 | .NET 9 with AVX-512 support |

**Key findings:**
- Both implementations achieve perfect numerical accuracy (identical cosine similarity scores)
- TensorPrimitives is ~22% faster in synthetic benchmarks with .NET 9's AVX-512 optimizations
- Custom implementation offers more predictable latency (lower variance)
- **Update**: Now using TensorPrimitives by default for best performance

**To run benchmarks:**
```bash
dotnet run -c Release -- bench [dimension] [vectors] [iterations] [searches]
# Example: dotnet run -c Release -- bench 4096 25000 50 5
```

## When to Use This

**Excellent for:**
- Finding code by concept rather than exact text
- Understanding unfamiliar codebases  
- UI component searches (Qwen3: 0.70-0.83, Voyage: 0.61-0.62)
- Business logic discovery
- Czech domain terminology (superior performance)

**Use traditional tools for:**
- Finding ALL instances (100% completeness)
- Cross-cutting concerns
- Known exact patterns
- Security audits requiring exhaustive search

## Configuration

### Security Settings

The server includes built-in security features:
- **Path validation** - Projects are restricted to whitelisted directories (default: `E:\`)
- **Directory traversal protection** - Project names are sanitized to prevent path escaping
- **Configurable whitelist** - Add allowed paths via `security.allowedDirectories` in config.json

### Automatic Monitoring

The server automatically keeps your search index synchronized:
- **Startup verification** - Checks all projects for changes when the server starts
- **Real-time monitoring** - Watches for code changes and reindexes automatically (Windows only)
- **Periodic rescan** - Optional scheduled rescan for WSL users (every 30 minutes by default)
- **Smart debouncing** - Waits 60 seconds after last change to avoid excessive reindexing
- **Configurable** - Control monitoring behavior via `monitoring` section in config.json

### Score Thresholds by Embedding Model

**Qwen3-8B (vLLM):**
- 0.45+ - Comprehensive results (recommended)
- 0.60+ - More focused results
- 0.70+ - High confidence only

**Voyage AI (voyage-code-3):**
- 0.40+ - Comprehensive results (recommended)
- 0.50+ - More focused results  
- 0.60+ - High confidence only

Note: Voyage AI scores run ~0.10-0.20 points lower than Qwen3 with similar relevance.

## Configuration Examples

See `config/examples/` for provider-specific configurations:
- `config-voyageai.json` - VoyageAI setup
- `config-vllm.json` - vLLM with Qwen3-8B
- `config_snowflake.json` - Ollama with Snowflake Arctic
- `config-ollama.json` - Generic Ollama setup

## Limitations

- Requires indexing before searching
- Results are probabilistic, not exhaustive  
- Score thresholds vary by embedding model
- Best combined with traditional search tools

## Contributing

Contributions welcome! Please open an issue or submit a pull request.

## License

MIT License - see LICENSE file for details.

## Acknowledgments

- [Model Context Protocol](https://github.com/anthropics/mcp) by Anthropic
- [VoyageAI](https://www.voyageai.com/) for excellent code embeddings
- [Roslyn](https://github.com/dotnet/roslyn) for C# syntax analysis
- Community testers for invaluable feedback

---

*Built with ❤️ using Claude AI*