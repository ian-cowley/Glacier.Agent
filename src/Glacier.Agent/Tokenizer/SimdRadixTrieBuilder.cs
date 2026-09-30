namespace Glacier.Agent.Tokenizer;

using System;
using System.Collections.Generic;
using System.Runtime.Intrinsics;
using System.Text;

/// <summary>
/// Builder to construct an optimized <see cref="SimdRadixTrieTokenizer"/>.
/// </summary>
public sealed class SimdRadixTrieBuilder
{
    private sealed class TempNode
    {
        public int TokenId = -1;
        public readonly Dictionary<byte, TempNode> Children = new();
    }

    private readonly TempNode _root = new();
    private readonly Dictionary<string, int> _vocabMap = new(StringComparer.Ordinal);
    private readonly List<byte[]> _tokenBytesList = new();
    private int _nextId = 0;

    public SimdRadixTrieBuilder()
    {
        // Reserve byte fallback tokens 0..255
        for (int i = 0; i < 256; i++)
        {
            byte b = (byte)i;
            AddTokenInternal([b], i);
        }
        _nextId = 256;
    }

    /// <summary>
    /// Adds a token string to the vocabulary with auto-assigned ID.
    /// </summary>
    public int AddToken(string token)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(token);
        return AddToken(bytes);
    }

    /// <summary>
    /// Adds raw token bytes to the vocabulary with auto-assigned ID.
    /// </summary>
    public int AddToken(byte[] tokenBytes)
    {
        if (tokenBytes.Length == 1)
        {
            return tokenBytes[0]; // Byte fallback token
        }

        string key = Encoding.UTF8.GetString(tokenBytes);
        if (_vocabMap.TryGetValue(key, out int existingId))
        {
            return existingId;
        }

        int id = _nextId++;
        AddTokenInternal(tokenBytes, id);
        return id;
    }

    /// <summary>
    /// Adds a token with a specific token ID.
    /// </summary>
    public void AddToken(string token, int tokenId)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(token);
        AddToken(bytes, tokenId);
    }

    /// <summary>
    /// Adds raw token bytes with a specific token ID.
    /// </summary>
    public void AddToken(byte[] tokenBytes, int tokenId)
    {
        AddTokenInternal(tokenBytes, tokenId);
        if (tokenId >= _nextId)
        {
            _nextId = tokenId + 1;
        }
    }

    private void AddTokenInternal(byte[] bytes, int id)
    {
        while (_tokenBytesList.Count <= id)
        {
            _tokenBytesList.Add(Array.Empty<byte>());
        }
        _tokenBytesList[id] = bytes;
        _vocabMap[Encoding.UTF8.GetString(bytes)] = id;

        TempNode current = _root;
        for (int i = 0; i < bytes.Length; i++)
        {
            byte b = bytes[i];
            if (!current.Children.TryGetValue(b, out TempNode? next))
            {
                next = new TempNode();
                current.Children[b] = next;
            }
            current = next;
        }
        current.TokenId = id;
    }

    /// <summary>
    /// Populates standard vocabulary containing common English words, subwords, code keywords, and special tokens.
    /// </summary>
    public void AddDefaultVocabulary()
    {
        string[] commonTokens =
        [
            // Whitespace & control sequences
            " ", "  ", "   ", "    ", "        ", "\t", "\n", "\r\n", "\n\n",
            
            // Common punctuation & symbols
            ":", ";", ",", ".", "?", "!", "-", "_", "(", ")", "[", "]", "{", "}", "\"", "'", "`",
            "/", "\\", "|", "@", "#", "$", "%", "^", "&", "*", "+", "=", "<", ">", "~",
            "://", "://www.", "==", "!=", "<=", ">=", "=>", "->", "/*", "*/", "//",

            // Special LLM / Agent tokens
            "<|endoftext|>", "<|im_start|>", "<|im_end|>", "<|tool_call|>", "<|tool_result|>",
            "<|fim_prefix|>", "<|fim_middle|>", "<|fim_suffix|>", "<|context|>", "<|system|>", "<|user|>", "<|assistant|>",

            // Programming keywords (C#, Python, Rust, SQL, JS, TS)
            "public", "private", "protected", "internal", "static", "readonly", "volatile",
            "class", "struct", "interface", "record", "enum", "namespace", "using",
            "void", "int", "long", "float", "double", "bool", "string", "char", "byte", "object",
            "true", "false", "null", "undefined", "var", "let", "const",
            "if", "else", "switch", "case", "break", "continue", "return", "throw", "try", "catch", "finally",
            "for", "foreach", "while", "do", "in", "is", "as", "new", "this", "base", "typeof", "sizeof",
            "async", "await", "Task", "ValueTask", "Thread", "Span", "ReadOnlySpan", "Memory",
            "def", "import", "from", "lambda", "yield", "pass", "None", "self", "elif",
            "fn", "mut", "impl", "trait", "pub", "match", "loop", "unsafe", "ref", "out",
            "SELECT", "FROM", "WHERE", "JOIN", "INNER", "LEFT", "RIGHT", "GROUP", "ORDER", "BY",
            "INSERT", "INTO", "VALUES", "UPDATE", "SET", "DELETE", "AND", "OR", "NOT", "NULL",

            // Common English words (with and without space prefix)
            "the", " The", "the ", " of", "of ", " and", "and ", " to", "to ", " a", "a ", " in", "in ",
            " that", "that ", " is", "is ", " was", " was ", " for", "for ", " on", "on ", " with", "with ",
            " as", "as ", " by", "by ", " at", "at ", " an", "an ", " be", "be ", " this", "this ",
            " which", "which ", " or", "or ", " from", "from ", " but", "but ", " not", "not ", " are", "are ",
            " your", "your ", " all", "all ", " have", "have ", " new", "new ", " more", "more ", " we", "we ",
            " will", "will ", " home", " can", "can ", " us", " about", "about ", " if", "if ", " page",
            " my", "my ", " has", "has ", " search", " free", " our", "our ", " one", "one ", " other",
            " do", "do ", " no", "no ", " information", " time", "time ", " they", "they ", " site", " he",
            " up", "up ", " may", " what", "what ", " their", "their ", " news", " out", "out ", " use", "use ",
            " any", " there", "there ", " see", " only", " so", "so ", " his", " when", "when ", " contact",
            " here", "here ", " business", " who", "web", " also", " now", " help", " get", "get ", " view",
            " online", " first", " am", " been", " would", "would ", " how", "how ", " were", " me", " services",
            " some", "some ", " these", "these ", " click", " its", " like", "like ", " service", " than",
            " find", " price", " date", " back", " top", " people", " had", " list", " name", " just",
            " over", " state", " year", " day", " into", " email", " two", " health", " world", " next",
            " used", " go", " work", " last", " most", " products", " music", " buy", " data", " make",
            " them", " should", " product", " system", " post", " her", " city", " policy", " number",
            " such", " please", " available", " copyright", " support", " message", " after", " best",
            " software", " then", " good", " video", " well", " where", " info", " rights", " public",
            " books", " high", " school", " through", " each", " links", " she", " review", " years",
            " order", " very", " privacy", " book", " items", " company", " read", " group", " need",
            " many", " user", " said", " does", " set", " under", " general", " research", " university",
            " mail", " full", " map", " reviews", " program", " life", " know", " games", " way",
            " management", " part", " could", " great", " united", " hotel", " real", " item", " center",
            " must", " store", " travel", " comments", " made", " development", " report", " off",
            " member", " details", " line", " terms", " before", " hotels", " did", " send", " right",
            " type", " because", " local", " those", " using", " results", " office", " education",
            " national", " car", " design", " take", " posted", " internet", " address", " community",
            " within", " states", " area", " want", " phone", " shipping", " reserved", " subject",
            " between", " forum", " family", " long", " based", " code", " show", " even", " black",
            " check", " special", " prices", " website", " index", " being", " women", " much",
            " sign", " file", " link", " open", " today", " technology", " south", " case", " project",
            " same", " pages", " version", " section", " own", " found", " sports", " house",
            " related", " security", " both", " american", " photo", " game", " members", " power",
            " while", " care", " network", " down", " computer", " systems", " three", " total",
            " place", " end", " following", " download", " him", " without", " per", " access",
            " think", " north", " resources", " current", " posts", " big", " media", " law",
            " control", " water", " size", " art", " personal", " since", " including", " guide",
            " shop", " directory", " board", " location", " change", " white", " text", " small",
            " rating", " rate", " government", " children", " during", " return", " students",
            " shopping", " account", " times", " sites", " level", " digital", " profile", " previous",
            " form", " events", " love", " old", " main", " call", " hours", " image", " department",
            " title", " description", " insurance", " another", " why", " property", " still",
            " money", " quality", " every", " listing", " content", " country", " private", " little",
            " visit", " save", " tools", " low", " reply", " customer", " compare", " movies",
            " include", " college", " value", " article", " york", " man", " card", " jobs",
            " provide", " food", " source", " author", " different", " press", " learn", " sale",
            " around", " print", " course", " job", " process", " room", " stock", " training",
            " too", " credit", " point", " join", " science", " men", " categories", " advanced",
            " sales", " look", " english", " left", " team", " estate", " box", " conditions",
            " select", " windows", " photos", " thread", " week", " category", " note", " live",
            " large", " gallery", " table", " register", " however", " present"
        ];

        foreach (string token in commonTokens)
        {
            AddToken(token);
        }
    }

    /// <summary>
    /// Compiles the collected vocabulary into an immutable <see cref="SimdRadixTrieTokenizer"/>.
    /// </summary>
    public SimdRadixTrieTokenizer Build()
    {
        int[] rootTable = new int[256];
        Array.Fill(rootTable, -1);

        int[] byteFallback = new int[256];
        for (int i = 0; i < 256; i++)
        {
            byteFallback[i] = i;
        }

        // Flatten trie nodes via BFS
        var flatNodes = new List<TempNode>();
        var nodeToIdx = new Dictionary<TempNode, int>();

        // Root is not in flatNodes; root children are mapped directly in rootTable
        foreach (var (b, child) in _root.Children)
        {
            int idx = flatNodes.Count;
            flatNodes.Add(child);
            nodeToIdx[child] = idx;
            rootTable[b] = idx;
        }

        // BFS for remaining descendants
        int head = 0;
        while (head < flatNodes.Count)
        {
            TempNode node = flatNodes[head++];
            foreach (var (_, child) in node.Children)
            {
                if (!nodeToIdx.ContainsKey(child))
                {
                    int idx = flatNodes.Count;
                    flatNodes.Add(child);
                    nodeToIdx[child] = idx;
                }
            }
        }

        int nodeCount = flatNodes.Count;
        int[] nodeTokenIds = new int[nodeCount];
        Vector256<byte>[] nodeBranchVectors = new Vector256<byte>[nodeCount];
        byte[] nodeEdgeCounts = new byte[nodeCount];
        int[][] nodeChildIndices = new int[nodeCount][];
        byte[][] nodeEdgePrefixes = new byte[nodeCount][];

        Span<byte> branchBuffer = stackalloc byte[32];

        for (int i = 0; i < nodeCount; i++)
        {
            TempNode node = flatNodes[i];
            nodeTokenIds[i] = node.TokenId;
            nodeEdgePrefixes[i] = Array.Empty<byte>();

            int count = Math.Min(node.Children.Count, 32);
            nodeEdgeCounts[i] = (byte)count;

            branchBuffer.Clear();
            int[] childIndices = new int[count];

            int idx = 0;
            foreach (var (b, child) in node.Children)
            {
                if (idx < 32)
                {
                    branchBuffer[idx] = b;
                    childIndices[idx] = nodeToIdx[child];
                    idx++;
                }
            }

            nodeBranchVectors[i] = Vector256.Create<byte>(branchBuffer);
            nodeChildIndices[i] = childIndices;
        }

        byte[][] tokenToBytes = _tokenBytesList.ToArray();

        return new SimdRadixTrieTokenizer(
            rootTable,
            byteFallback,
            nodeTokenIds,
            nodeBranchVectors,
            nodeEdgeCounts,
            nodeChildIndices,
            nodeEdgePrefixes,
            tokenToBytes,
            _nextId);
    }
}
