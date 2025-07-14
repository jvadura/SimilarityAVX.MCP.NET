# MCP C# Semantic Search vs Traditional Tools Guide

This guide helps you choose between MCP semantic search and traditional search tools (grep/glob) based on extensive real-world testing with enterprise codebases.

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
| Czech business terms | Semantic Search | Superior performance with domain terminology |

## MCP Semantic Search Strengths

### 🎯 Exceptional Performance (0.70+ scores)
- **Conceptual searches**: "email notifications" → finds SendGrid implementation
- **Entity/model searches**: "user entity" → finds correct data models
- **UI component discovery**: "validation form" → finds relevant Blazor components (0.70-0.83)
- **Business logic**: "payment processing" → understands domain concepts
- **Anti-patterns**: "anti-flood protection" → identifies rate limiting (0.73)
- **Czech terminology**: "čerpání finančních prostředků" → financial disbursement (0.75)

### 💡 Key Advantages
1. **Natural language understanding**: Ask in plain English or Czech
2. **Relevance scoring**: Results ranked by semantic similarity (0.0-1.0)
3. **Context awareness**: Understands code purpose, not just keywords
4. **Typo tolerance**: Still finds results with minor spelling errors
5. **Multilingual support**: Excellent Czech domain terminology understanding

### 🛠️ Best Use Cases
```bash
# Finding features by description
mcp__csharp-search__search_project --project myproject --query "user authentication flow"

# Understanding business logic with context
mcp__csharp-search__search_with_context --project myproject --query "payment validation" --contextLines 15

# Exploring related concepts
mcp__csharp-search__batch_search --project myproject --queries "login,authentication,session"

# Czech business logic (excellent performance)
mcp__csharp-search__search_project --project myproject --query "čerpání finančních prostředků úrokové sazby"

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

## Performance Characteristics

### MCP Semantic Search (Production Results)
- **Speed**: ~176ms first search → ~60ms subsequent (cache effect)
- **Memory**: 30-75 MB per project (very efficient)
- **Scoring range**: 0.30-0.83 (typical 0.45-0.70)
- **Recommended threshold**: 0.45 (comprehensive results)
- **Indexing time**: ~1 second per 100 files
- **Vector dimensions**: 2048-4096 (configurable)
- **CPU acceleration**: AVX-512 enabled (16x float32)

### Traditional Tools
- **Speed**: ~20-50ms per search
- **Memory**: Minimal
- **Results**: Binary (found/not found)
- **Setup**: Instant, no indexing

## Multilingual Search Patterns (Czech/English)

### 🏆 Czech Advantages (Real Production Data)
- **Domain terminology**: "čerpání finančních prostředků" (0.75) vs "financial disbursement" (0.70)
- **Government workflows**: "stavy žádostí workflow" (0.76) vs "application states" (0.61)
- **Technical validation**: "validace bankovního účtu" (0.84) vs "bank account validation" (0.78)
- **Permissions**: "oprávnění" (0.77-0.79) - excellent match

### 📋 Best Practice
- **Use Czech for**: Government processes, financial terms, business workflows
- **Use English for**: Technical frameworks, architecture patterns, general programming
- **Mixed queries**: "žádosti API endpoints" often work well

## Score Interpretation Guide

Based on extensive production testing:

- **0.80-1.00**: Exceptional matches (rare, highly specific)
- **0.70-0.79**: Excellent matches, exactly what you're looking for
- **0.60-0.69**: Very good matches, usually relevant
- **0.45-0.59**: Good matches, often relevant depending on context
- **0.40-0.44**: Moderate matches, may be tangentially related
- **0.30-0.39**: Weak matches, semantic similarity but likely not useful
- **<0.30**: Poor matches (embedding models always return something)

**⚠️ Important**: Embedding models find *most similar* content even for nonexistent concepts - use score thresholds to filter!

## Practical Guidelines

### ✅ Use Semantic Search When

1. **Exploring unfamiliar codebases**
   - "How does error handling work?"
   - "Where is the caching implemented?"

2. **Finding business features**
   - "Customer notification system"
   - "Invoice generation logic"
   - "plná moc" (power of attorney) - excellent Czech match

3. **Understanding implementations**
   ```bash
   # Get surrounding context
   mcp__csharp-search__search_with_context --query "validation logic" --contextLines 15
   ```

4. **Discovering related code**
   ```bash
   # Find multiple related concepts
   mcp__csharp-search__batch_search --queries "SAML,BankID,OIDC,authentication"
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
4. **Mix languages strategically**: "žádosti API endpoints" for Czech business + English tech
5. **Filter by enhanced types**: Use auth-specific chunk types for security code

### 🚀 Effective Batch Search Combinations

**For Feature Exploration:**
- Related concepts: `"login,authentication,session"`
- Power of attorney: `"plná moc,plne moci,oprávnění,zmocnění"`
- Technical + business: `"PDF generation,document signing,digital signature"`

**Cross-Reference Insights:**
- Files appearing in multiple batch queries often contain core functionality
- Use cross-reference section to identify central components

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

## When to Re-index

- After adding new features (>50 files changed)
- After major refactoring
- When switching between branches with different code structures
- To enable enhanced features: use `--force true` for auth detection

## Tips for Best Results

1. **Index with meaningful names**: Use descriptive project names for multi-project solutions
2. **Adjust threshold**: Use 0.45+ for good matches (not 0.70)
3. **Use context**: `search_with_context` with 15-20 lines for understanding
4. **Batch similar queries**: More efficient than individual searches
5. **Leverage Czech for business**: Better results for domain-specific terms
6. **Verify critical code**: Always double-check auth/security with traditional tools

## Real-World Examples

```bash
# Czech business workflow (excellent performance)
mcp__csharp-search__search_project --project klientskyportal --query "stavy žádostí workflow podání"

# Technical implementation with context
mcp__csharp-search__search_with_context --project klientskyportal --query "PDF document signing" --contextLines 15

# Authentication & security (use filters)
mcp__csharp-search__search_with_filters --project klientskyportal --query "authentication middleware" --chunkTypes "method-auth,class-auth"

# Batch exploration for related concepts
mcp__csharp-search__batch_search --project klientskyportal --queries "SAML,BankID,OIDC,authentication" --limitPerQuery 3
```

Remember: Neither tool is universally better - choose based on your specific need. When in doubt, try semantic search first for better developer experience, then verify with traditional tools if completeness is critical.