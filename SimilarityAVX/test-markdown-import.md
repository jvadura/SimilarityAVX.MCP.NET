# C# MCP Server Architecture

This document describes the architecture of the C# MCP server for semantic code search.

## Core Components

The server is built with several key components that work together to provide fast semantic search.

### CodeIndexer

The CodeIndexer is responsible for processing C# source files and creating searchable chunks. It uses Roslyn for accurate parsing and supports incremental updates.

Key features:
- Incremental indexing based on file hashes
- Support for C#, Razor, and C files
- Chunk creation based on code structure

### VectorMemoryStore

This component provides the in-memory vector search capability using SIMD instructions.

Features include:
- AVX-512 acceleration when available
- Brute force search for 100% accuracy
- Efficient memory management

#### SIMD Implementation

The SIMD implementation uses TensorPrimitives for hardware acceleration:
- Automatic detection of CPU capabilities
- Falls back to AVX2 or SSE if needed
- Parallel processing across all cores

## Memory System

The memory system provides persistent knowledge storage with semantic search.

### Memory Storage

SQLite-based storage with the following features:
- Integer IDs for easy reference
- Alias support for natural language references
- Parent-child relationships

### Memory Search

Semantic search across memories with:
- Vector similarity using embeddings
- Tag-based filtering
- Date range filtering
- Parent/child filtering

## MCP Protocol Integration

The server implements the Model Context Protocol (MCP) with multiple tools.

### Search Tools

- SearchProject: Basic semantic search
- SearchWithContext: Extended context windows
- SearchWithFilters: Advanced filtering options
- BatchSearch: Multiple queries at once

### Memory Tools

- AddMemory: Store new memories
- SearchMemories: Semantic memory search
- ImportMarkdownAsMemories: Import documentation
- GetMemoryTree: Visualize knowledge structure