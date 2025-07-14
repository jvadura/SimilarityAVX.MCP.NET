using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace CSharpMcpServer.Core;

/// <summary>
/// Expands queries with common synonyms to improve search recall.
/// For example, "auth" expands to include "authentication", "authorization", "login", etc.
/// </summary>
public class QueryExpander
{
    private readonly Dictionary<string, HashSet<string>> _synonymGroups;
    private readonly bool _enabled;
    
    public QueryExpander(bool enabled = true)
    {
        _enabled = enabled;
        _synonymGroups = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        InitializeDefaultSynonyms();
    }
    
    private void InitializeDefaultSynonyms()
    {
        // Authentication/Security terms
        AddSynonymGroup("auth", "authentication", "authorization", "login", "signin", "credential", "identity");
        AddSynonymGroup("logout", "signout", "logoff");
        AddSynonymGroup("token", "jwt", "bearer", "access token", "refresh token");
        AddSynonymGroup("password", "pwd", "passphrase", "secret");
        AddSynonymGroup("user", "account", "member", "identity", "principal");
        AddSynonymGroup("permission", "privilege", "access", "role", "claim");
        AddSynonymGroup("security", "secure", "encryption", "cryptography");
        
        // Configuration terms
        AddSynonymGroup("config", "configuration", "settings", "options", "preferences", "setup");
        AddSynonymGroup("env", "environment", "environment variable");
        AddSynonymGroup("param", "parameter", "argument", "arg");
        
        // Initialization terms
        AddSynonymGroup("init", "initialize", "initialise", "setup", "startup", "bootstrap");
        AddSynonymGroup("create", "new", "instantiate", "construct");
        AddSynonymGroup("start", "begin", "launch", "run");
        AddSynonymGroup("stop", "end", "terminate", "shutdown", "dispose");
        
        // Data operations
        AddSynonymGroup("get", "fetch", "retrieve", "load", "read", "find", "query");
        AddSynonymGroup("save", "store", "persist", "write", "insert", "create");
        AddSynonymGroup("update", "modify", "change", "edit", "patch");
        AddSynonymGroup("delete", "remove", "destroy", "drop", "clear");
        AddSynonymGroup("search", "find", "lookup", "query", "filter");
        
        // API/Web terms
        AddSynonymGroup("api", "endpoint", "service", "rest", "web service");
        AddSynonymGroup("request", "call", "invoke", "http request");
        AddSynonymGroup("response", "result", "reply", "http response");
        AddSynonymGroup("controller", "endpoint", "route", "action");
        
        // Database terms
        AddSynonymGroup("db", "database", "data store", "repository");
        AddSynonymGroup("table", "entity", "model", "record");
        AddSynonymGroup("query", "sql", "linq", "select");
        AddSynonymGroup("connection", "connect", "connection string");
        
        // Error handling
        AddSynonymGroup("error", "exception", "fault", "issue", "problem");
        AddSynonymGroup("log", "logger", "logging", "trace", "debug");
        AddSynonymGroup("validate", "validation", "verify", "check");
        
        // Common programming terms
        AddSynonymGroup("async", "asynchronous", "await", "task");
        AddSynonymGroup("sync", "synchronous", "blocking");
        AddSynonymGroup("cache", "caching", "cached", "memory cache");
        AddSynonymGroup("queue", "message queue", "mq", "messaging");
        AddSynonymGroup("event", "event handler", "callback", "hook");
        AddSynonymGroup("interface", "contract", "abstraction");
        AddSynonymGroup("implement", "implementation", "concrete");
        AddSynonymGroup("test", "unit test", "testing", "spec");
        
        // File operations
        AddSynonymGroup("file", "document", "blob");
        AddSynonymGroup("upload", "file upload", "attachment");
        AddSynonymGroup("download", "export", "file download");
        AddSynonymGroup("path", "filepath", "directory", "folder");
    }
    
    private void AddSynonymGroup(params string[] terms)
    {
        var group = new HashSet<string>(terms, StringComparer.OrdinalIgnoreCase);
        foreach (var term in terms)
        {
            _synonymGroups[term] = group;
        }
    }
    
    public string ExpandQuery(string query)
    {
        if (!_enabled || string.IsNullOrWhiteSpace(query))
            return query;
        
        // Extract words from the query (handle multi-word terms)
        var words = ExtractWords(query);
        var expandedTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var expansions = new List<string>();
        
        // Add original query
        expandedTerms.Add(query);
        
        foreach (var word in words)
        {
            // Check if this word has synonyms
            if (_synonymGroups.TryGetValue(word, out var synonyms))
            {
                foreach (var synonym in synonyms)
                {
                    if (!expandedTerms.Contains(synonym))
                    {
                        expandedTerms.Add(synonym);
                        expansions.Add(synonym);
                    }
                }
            }
        }
        
        // Also check for multi-word matches in the query
        foreach (var kvp in _synonymGroups)
        {
            if (kvp.Key.Contains(' ') && query.IndexOf(kvp.Key, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                foreach (var synonym in kvp.Value)
                {
                    if (!expandedTerms.Contains(synonym))
                    {
                        expandedTerms.Add(synonym);
                        expansions.Add(synonym);
                    }
                }
            }
        }
        
        if (expansions.Any())
        {
            // Log expansion for debugging
            Console.Error.WriteLine($"[QueryExpander] Expanded '{query}' with: {string.Join(", ", expansions)}");
            
            // Combine original query with expansions
            // Format: "original query (expansion1 OR expansion2 OR expansion3)"
            return $"{query} ({string.Join(" OR ", expansions)})";
        }
        
        return query;
    }
    
    private List<string> ExtractWords(string query)
    {
        // Simple word extraction - could be enhanced with better tokenization
        var words = new List<string>();
        var matches = Regex.Matches(query, @"\b[\w]+\b");
        
        foreach (Match match in matches)
        {
            words.Add(match.Value);
        }
        
        return words;
    }
    
    public void AddCustomSynonyms(string term, params string[] synonyms)
    {
        var allTerms = new List<string> { term };
        allTerms.AddRange(synonyms);
        AddSynonymGroup(allTerms.ToArray());
    }
    
    public bool IsEnabled => _enabled;
    
    public IReadOnlyDictionary<string, HashSet<string>> GetSynonymGroups()
    {
        return _synonymGroups;
    }
}