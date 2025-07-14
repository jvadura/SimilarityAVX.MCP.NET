# MCP Search Guide - Score Distributions and Recommendations

## Embedding Model Comparison: Qwen3-8B vs Voyage AI

This guide documents score distributions and best practices for both supported embedding models, based on extensive testing with the KlientskyPortal project (761 files, 5,575 chunks).

## Score Distribution Comparison

### Test Results Summary

| Search Query | Qwen3-8B Score | Voyage AI Score | Difference |
|--------------|----------------|-----------------|------------|
| Anti-flood protection | 0.73 | 0.63 | -0.10 |
| Authentication middleware | 0.57-0.67 | 0.51-0.53 | -0.06 to -0.14 |
| Validation form Blazor | 0.70-0.83 | 0.61-0.62 | -0.09 to -0.21 |
| User entity model | 0.70-0.78 | 0.56-0.59 | -0.14 to -0.19 |
| SAML (batch search) | - | 0.54-0.60 | - |
| BankID (batch search) | - | 0.59-0.64 | - |

## Key Findings

1. **Voyage AI scores are consistently lower** than Qwen3-8B scores by approximately 0.10-0.20 points
2. **Score compression**: Voyage AI has a tighter score range (0.51-0.64 observed) compared to Qwen3 (0.57-0.83)
3. **Relative rankings preserved**: The best matches with Qwen3 are still the best matches with Voyage AI
4. **Authentication detection working**: Enhanced chunk types (method-auth, class-auth) are properly detected

## Recommended Score Thresholds by Model

### Qwen3-8B (vLLM) Thresholds
- **0.45+** - Minimum threshold for potentially relevant results
- **0.60+** - Good matches, usually relevant
- **0.70+** - Very good matches, high confidence
- **0.80+** - Excellent matches, exactly what you're looking for
- **0.85+** - Exceptional matches

### Voyage AI (voyage-code-3) Thresholds
- **0.40+** - Minimum threshold for potentially relevant results
- **0.50+** - Good matches, usually relevant
- **0.55+** - Very good matches, high confidence
- **0.60+** - Excellent matches, exactly what you're looking for
- **0.65+** - Exceptional matches (rare with Voyage AI)

### Score Interpretation Guides

**Qwen3-8B Scores:**
- **0.80-1.00**: Exceptional matches, highly specific
- **0.70-0.79**: Excellent matches, exactly what you're looking for
- **0.60-0.69**: Very good matches, usually relevant
- **0.45-0.59**: Good matches, often relevant depending on context
- **0.40-0.44**: Moderate matches, may be tangentially related
- **<0.40**: Poor matches

**Voyage AI Scores:**
- **0.60-1.00**: Exceptional matches (rare, highly specific)
- **0.55-0.59**: Excellent matches, exactly what you're looking for
- **0.50-0.54**: Very good matches, usually relevant
- **0.45-0.49**: Good matches, often relevant depending on context
- **0.40-0.44**: Moderate matches, may be tangentially related
- **<0.40**: Poor matches

## Usage Recommendations

### 1. Adjust Your Expectations
When switching from Qwen3 to Voyage AI:
- Expect scores to be 0.10-0.20 points lower
- A score of 0.55 with Voyage AI ≈ 0.70 with Qwen3
- A score of 0.50 with Voyage AI ≈ 0.60 with Qwen3

### 2. Search Strategy Remains the Same
The search patterns and strategies documented for Qwen3 still apply:
- Start with semantic search for concepts
- Use context for understanding
- Batch related searches
- Filter by code structure when needed

### 3. When to Use Traditional Tools
The same guidelines apply regardless of embedding model:
- Finding ALL instances (use grep)
- Cross-cutting concerns
- When you need 100% completeness

## Performance Comparison

### Qwen3-8B (vLLM)
- **Vector dimensions**: 4096
- **Memory usage**: 73.6 MB for 5,575 chunks
- **Indexing time**: ~143s for 5,575 chunks
- **Search performance**: ~176ms first search, ~60ms subsequent
- **Acceleration**: TensorPrimitives with AVX-512

### Voyage AI (voyage-code-3)
- **Vector dimensions**: 2048 (50% smaller)
- **Memory usage**: 46.3 MB for 5,575 chunks (37% less)
- **Indexing time**: 116.2s for 5,575 chunks (19% faster)
- **Search performance**: Sub-200ms (similar)
- **Acceleration**: TensorPrimitives with AVX-512

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

### Key Differences Summary
- **Score offset**: Voyage AI runs ~0.10-0.20 points lower
- **Memory efficiency**: Voyage AI uses 37% less memory
- **Score distribution**: Qwen3 has wider spread (better differentiation)
- **Infrastructure**: Qwen3 requires local GPU, Voyage AI is cloud-based

**Quick conversion**: Voyage AI score + 0.15 ≈ Qwen3 score