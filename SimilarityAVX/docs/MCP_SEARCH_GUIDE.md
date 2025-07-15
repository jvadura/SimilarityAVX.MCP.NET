# MCP C# Semantic Search vs Traditional Tools Guide

This guide helps you choose between MCP semantic search and traditional search tools (grep/glob) based on extensive real-world testing with enterprise codebases.

> **New Features**: 
> - **Automatic monitoring and reindexing** to keep your search index synchronized with code changes in real-time (Windows)
> - **Periodic rescan** option for WSL users - automatically checks for changes every X minutes when file monitoring isn't available

## Quick Decision Matrix

| What You're Looking For | Best Tool | Why |
|------------------------|-----------|-----|
| Business features by concept | Semantic Search | Understands "rate limiting" → finds anti-flood code |
| ALL instances of something | Traditional Tools | Guarantees 100% completeness |
| UI components | Semantic Search | Excellent relevance scoring (0.70-0.83) |
| Security/auth code | Both | Semantic improved (0.57-0.67), but traditional finds all |
| Unknown terminology | Semantic Search | Maps concepts to implementation |
| Specific file patterns | Traditional Tools | Direct and fast with glob |
| Code relationships | Semantic Search | Better context understanding |
| Cross-cutting concerns | Traditional Tools | Finds all touchpoints |
| Domain-specific terms | Semantic Search | Superior performance with business terminology |

## 🎯 Quick Tool Selection (5-second rule)

**What are you looking for?**
- **A specific file/class name** → Use traditional tools (Glob/Grep)
- **How something works** → Use `search_project` or `batch_search`
- **Implementation details** → Use `search_with_context`
- **All instances of X** → Use traditional tools
- **Business logic/features** → Use semantic search

## MCP Semantic Search Strengths

### 🎯 Exceptional Performance (0.70+ scores with Qwen3)
- **Conceptual searches**: "email notifications" → finds SendGrid implementation
- **Entity/model searches**: "user entity" → finds correct data models
- **UI component discovery**: "validation form" → finds relevant Blazor components (0.70-0.83)
- **Business logic**: "payment processing" → understands domain concepts
- **Anti-patterns**: "anti-flood protection" → identifies rate limiting (0.73)
- **Multilingual**: Excellent performance with non-English domain terminology

### 💡 Key Advantages
1. **Natural language understanding**: Ask in plain English or other languages
2. **Relevance scoring**: Results ranked by semantic similarity (0.0-1.0)
3. **Context awareness**: Understands code purpose, not just keywords
4. **Typo tolerance**: Still finds results with minor spelling errors
5. **Cross-language search**: Can find concepts across programming languages

### 🛠️ Best Use Cases
```bash
# Finding features by description
mcp__csharp-search__search_project --project myproject --query "user authentication flow"

# Understanding business logic with context
mcp__csharp-search__search_with_context --project myproject --query "payment validation" --contextLines 15

# Exploring related concepts
mcp__csharp-search__batch_search --project myproject --queries "login,authentication,session"

# Authentication with filters (enhanced chunk types)
mcp__csharp-search__search_with_filters --project myproject --query "authentication" --chunkTypes "method-auth,class-auth"
```

## Traditional Tools Strengths

### 🎯 Essential For
- **Complete discovery**: Finding EVERY instance of a pattern
- **Known patterns**: When you know exact keywords/syntax
- **File organization**: Understanding project structure
- **Performance critical**: 5-10x faster (~50ms vs ~200ms)

### 💡 Key Advantages
1. **100% completeness**: Never misses matches
2. **Exact matching**: Precise pattern control with regex
3. **No indexing needed**: Works immediately
4. **Minimal resources**: Near-zero memory usage

### 🛠️ Best Use Cases
```bash
# Finding all authentication code
grep -r "authentication\|login\|security" --include="*.cs"

# Discovering file patterns
glob "**/*{Controller,Service,Repository}.cs"

# Complex pattern matching
grep -r "public.*async.*Task<.*>" --include="*.cs"
```

## Embedding Model Comparison: Qwen3-8B vs Voyage AI

### Score Distribution Comparison

| Search Type | Qwen3-8B Score | Voyage AI Score | Difference |
|------------|----------------|-----------------|------------|
| Anti-patterns | 0.73 | 0.63 | -0.10 |
| Auth middleware | 0.57-0.67 | 0.51-0.53 | -0.06 to -0.14 |
| UI components | 0.70-0.83 | 0.61-0.62 | -0.09 to -0.21 |
| Entity models | 0.70-0.78 | 0.56-0.59 | -0.14 to -0.19 |

### Recommended Score Thresholds

#### Qwen3-8B (vLLM) Thresholds
- **0.45+** - Minimum threshold for potentially relevant results
- **0.60+** - Good matches, usually relevant
- **0.70+** - Very good matches, high confidence
- **0.80+** - Excellent matches, exactly what you're looking for

#### Voyage AI (voyage-code-3) Thresholds
- **0.40+** - Minimum threshold for potentially relevant results
- **0.50+** - Good matches, usually relevant
- **0.55+** - Very good matches, high confidence
- **0.60+** - Excellent matches, exactly what you're looking for

**Quick conversion**: Voyage AI score + 0.15 ≈ Qwen3 score

### 📊 Score Interpretation Quick Reference

**When you see low scores (< 0.45 for Qwen3, < 0.40 for Voyage):**
- Tool provides hints automatically with 💡 note
- Try more specific queries
- Consider using batch_search with related terms
- Check if you're searching the right project

## Performance Characteristics

### MCP Semantic Search
- **Speed**: ~176ms first search → ~60ms subsequent (cache effect)
- **Memory**: 30-75 MB per project (very efficient)
- **Indexing time**: ~1 second per 100 files
- **CPU acceleration**: AVX-512 enabled (16x float32)

### Traditional Tools
- **Speed**: ~20-50ms per search
- **Memory**: Minimal
- **Results**: Binary (found/not found)
- **Setup**: Instant, no indexing

## Practical Guidelines

### ✅ Search Patterns That Work Well

**Effective queries (tested):**
- Specific features: "email validation in registration form" ✓
- Mixed concepts: "SAML authentication" ✓
- Technical concepts: "anti-flood protection" (finds rate limiting)
- Business logic: Domain-specific terminology often performs better

**Less effective queries:**
- Too generic: "validation" (many low-score results)
- Unrelated terms: "banana apple orange" (scores < 0.45)
- Wrong project name: Clear error message provided

### ✅ Use Semantic Search When

1. **Exploring unfamiliar codebases**
   - "How does error handling work?"
   - "Where is the caching implemented?"

2. **Finding business features**
   - "Customer notification system"
   - "Invoice generation logic"

3. **Understanding implementations**
   ```bash
   # Get surrounding context
   mcp__csharp-search__search_with_context --query "validation logic" --contextLines 15
   ```

4. **Discovering related code**
   ```bash
   # Find multiple related concepts
   mcp__csharp-search__batch_search --queries "SAML,OAuth,JWT,authentication"
   ```

### ✅ Use Traditional Tools When

1. **Needing complete coverage**
   - Security audits
   - Refactoring (find ALL usages)
   - Dependency analysis

2. **Working with known patterns**
   - Finding all async methods
   - Locating specific attributes
   - File naming conventions

3. **Performance matters**
   - Large codebases
   - Frequent searches
   - CI/CD pipelines

## Search Strategy Decision Tree

```
Need to understand a feature?
├─ YES → Start with batch_search (3-5 related concepts)
│   └─ Found interesting results? → search_with_context for details
└─ NO → Know exactly what you want?
    ├─ YES → Use search_project with specific query
    └─ NO → Traditional tools (grep/glob)
```

## Advanced Search Strategies

### 🔄 Hybrid Approach (Recommended)

```bash
# 1. Start with semantic to understand the feature
mcp__csharp-search__search_project --query "user registration"

# 2. Then use traditional to find all instances
grep -r "Register\|Registration" --include="*.cs"
```

### 🎯 Query Optimization Tips

1. **Be specific**: "email validation in registration" > "validation"
2. **Use domain terms**: "loan application" not just "application"
3. **Add context**: "background job for sending emails" > "job"
4. **Filter by enhanced types**: Use auth-specific chunk types for security code

### 🔗 Using Cross-References in Batch Search

The cross-reference section shows files appearing in multiple queries:
- High overlap = core functionality
- Example: If the same file appears for "anti-flood", "rate limiting", and "throttling"
- Use this to identify central components quickly

### 🚀 Effective Batch Search Combinations

**For Feature Exploration:**
- Related concepts: `"login,authentication,session"`
- Technical + business: `"PDF generation,document signing,digital signature"`
- Architecture patterns: `"repository,service,controller,dependency injection"`

## Core MCP Search Tools

**Primary search tools** ⭐:
- **`search_project`**: Basic semantic search with relevance scoring
- **`search_with_context`**: Extended context viewing (15-20 lines recommended)
- **`search_with_filters`**: Filter by file types and code structures
- **`batch_search`**: Multiple queries at once (3-5 queries optimal)

**Management tools**:
- **`index_project`**: Index or update projects (use `--force true` for auth detection)
- **`list_projects`**: Show all indexed projects
- **`get_project_stats`**: Memory and performance info
- **`clear_project_index`**: Remove project index

## Common Pitfalls to Avoid

### ❌ Semantic Search Limitations
- May miss some results (not 100% complete)
- Requires indexing (can be outdated without re-indexing)
- Higher resource usage
- Results vary by query phrasing and model

### ❌ Traditional Tools Limitations
- No understanding of intent
- Can't find conceptually similar code
- No relevance ranking
- Requires exact keyword knowledge

## When to Re-index

- After adding new features (>50 files changed)
- After major refactoring
- When switching between branches with different code structures
- To enable enhanced features: use `--force true` for auth detection

## Tips for Best Results

1. **Index with meaningful names**: Use descriptive project names for multi-project solutions
2. **Adjust threshold by model**: Qwen3 (0.45+), Voyage (0.40+)
3. **Use context**: `search_with_context` with 15-20 lines for understanding
4. **Batch similar queries**: More efficient than individual searches
5. **Verify critical code**: Always double-check auth/security with traditional tools

## Model Selection Guide

### Choose Qwen3-8B (vLLM) when:
- Running on-premises with GPU resources
- Need highest quality results with best score separation
- Want full control over your embedding infrastructure
- Score differentiation is critical for your use case

### Choose Voyage AI when:
- Prefer cloud-hosted solution
- Need smaller memory footprint (37% less)
- Want faster indexing times (19% faster)
- Don't have local GPU resources

### Performance Comparison

**Qwen3-8B (vLLM)**
- Vector dimensions: 4096
- Memory usage: ~13.2 KB per chunk
- Better score distribution (wider spread)

**Voyage AI (voyage-code-3)**
- Vector dimensions: 2048 (50% smaller)
- Memory usage: ~8.3 KB per chunk (37% less)
- Tighter score range (0.10-0.20 lower)

## Real-World Example Queries

```bash
# Business workflow understanding
mcp__csharp-search__search_project --project myproject --query "order processing workflow"

# Technical implementation with context
mcp__csharp-search__search_with_context --project myproject --query "caching strategy" --contextLines 20

# Authentication & security (use filters)
mcp__csharp-search__search_with_filters --project myproject --query "JWT validation" --chunkTypes "method-auth,class-auth"

# Batch exploration for architecture
mcp__csharp-search__batch_search --project myproject --queries "repository pattern,unit of work,dependency injection" --limitPerQuery 3
```

Remember: Neither tool is universally better - choose based on your specific need. When in doubt, try semantic search first for better developer experience, then verify with traditional tools if completeness is critical.