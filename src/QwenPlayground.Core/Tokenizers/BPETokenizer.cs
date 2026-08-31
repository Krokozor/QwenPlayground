using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
// ⚠️ WIP / долгосрочный проект: локальный BPE-токенизатор.
// Выведен из активного использования 2026-08-20: аппроксимация токенов на нём
// расходилась с токенизатором сервера (сотни токенов), точное количество теперь
// всегда запрашивается у сервера (/tokenize, /api/extra/tokencount). Код сохраняется
// как задел: требует доработки (полный merge-проход, нормализация, спецтокены),
// чтобы когда-нибудь стать точным локальным счётчиком. См. refactoring.md, backlog.
using System.Text.RegularExpressions;

namespace QwenPlayground.Core.Tokenizers
{
    public class BPETokenizer
    {
        private Dictionary<string, int> _vocab;
        private Dictionary<string, Dictionary<string, int>> _mergeRanks;
        private Regex _preTokenizerRegex;
        private HashSet<string> _specialTokens;
        private Dictionary<string, int> _specialTokenIds;
        private Regex? _specialTokenRegex;
        private Dictionary<string, int> _cache;
        private const int MAX_CACHE_SIZE = 10000;

        // Normalizer
        private string? _normalizePattern;
        private string? _normalizeReplacement;
        private bool _useNfc;
        // Pre-tokenizer
        private string? _splitPattern;
        private bool _useByteLevel;

        private static readonly Dictionary<int, int> BytesToUnicodeMap = BuildBytesToUnicodeMap();
        private static readonly Dictionary<char, int> UnicodeToBytesMap = BuildUnicodeToBytesMap();

        /// <summary>
        /// GPT-2 style bytes-to-unicode mapping used by ByteLevel pre-tokenizer.
        /// printable ASCII and Latin-1 ranges map to themselves, the rest map to U+0100+.
        /// </summary>
        private static Dictionary<int, int> BuildBytesToUnicodeMap()
        {
            var bs = new List<int>();
            for (int b = 0x21; b <= 0x7E; b++) bs.Add(b);           // ! ..
            for (int b = 0xA1; b <= 0xAC; b++) bs.Add(b);           // ¡ .. ¬
            for (int b = 0xAE; b <= 0xFF; b++) bs.Add(b);           // ® .. ÿ

            var cs = bs.ToList();
            int n = 0;
            for (int b = 0; b < 256; b++)
            {
                if (!bs.Contains(b))
                {
                    bs.Add(b);
                    cs.Add(256 + n);
                    n++;
                }
            }

            var result = new Dictionary<int, int>();
            for (int i = 0; i < bs.Count; i++)
                result[bs[i]] = cs[i];
            return result;
        }

        private static Dictionary<char, int> BuildUnicodeToBytesMap()
        {
            var result = new Dictionary<char, int>();
            foreach (var kvp in BytesToUnicodeMap)
                result[(char)kvp.Value] = kvp.Key;
            return result;
        }

        /// <summary>Converts raw text to the ByteLevel representation by applying bytes-to-unicode over its UTF-8 bytes.</summary>
        private static string BytesToUnicode(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            var sb = new StringBuilder(bytes.Length);
            foreach (byte b in bytes)
                sb.Append((char)BytesToUnicodeMap[b]);
            return sb.ToString();
        }

        /// <summary>Reverses ByteLevel representation back into raw text. Invalid sequences are skipped.</summary>
        private static string UnicodeToBytes(string value)
        {
            var bytes = new List<byte>(value.Length);
            foreach (char c in value)
            {
                if (UnicodeToBytesMap.TryGetValue(c, out var byteVal))
                    bytes.Add((byte)byteVal);
            }
            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        public int VocabularySize => _vocab.Count;
        public int MergeCount => _mergeRanks.Count;

        public static BPETokenizer FromJson(string json)
        {
            var tokenizer = new BPETokenizer();
            tokenizer.LoadFromJson(json);
            return tokenizer;
        }

        public static BPETokenizer FromFile(string path)
        {
            var json = File.ReadAllText(path);
            return FromJson(json);
        }

        public static BPETokenizer FromEmbeddedResource(string resourceName)
        {
            var assembly = Assembly.GetExecutingAssembly();
            var fullName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(resourceName, StringComparison.OrdinalIgnoreCase));

            if (fullName == null)
                throw new FileNotFoundException($"Tokenizer resource not found: {resourceName}");

            using var stream = assembly.GetManifestResourceStream(fullName);
            if (stream is null)
                throw new FileNotFoundException($"Tokenizer resource not found: {resourceName}");
            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            return FromJson(json);
        }

        private BPETokenizer()
        {
            _vocab = new Dictionary<string, int>();
            _mergeRanks = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
            _specialTokens = new HashSet<string>();
            _specialTokenIds = new Dictionary<string, int>(StringComparer.Ordinal);
            _cache = new Dictionary<string, int>();
            _preTokenizerRegex = new Regex(
                @"'(?:[sdmt]|ll|ve|re)| ?\p{L}+| ?\p{N}+| ?[^\s\p{L}\p{N}]+|\s+(?!\S)|\s+",
                RegexOptions.Compiled
            );
        }

        private void LoadFromJson(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("model", out var modelElement))
            {
                if (modelElement.TryGetProperty("vocab", out var vocabElement))
                {
                    _vocab = new Dictionary<string, int>(StringComparer.Ordinal);
                    foreach (var property in vocabElement.EnumerateObject())
                    {
                        _vocab[property.Name] = property.Value.GetInt32();
                    }
                }

                if (modelElement.TryGetProperty("merges", out var mergesElement))
                {
                    int mergeCount = mergesElement.GetArrayLength();
                    _mergeRanks = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);

                    int i = 0;
                    foreach (var merge in mergesElement.EnumerateArray())
                    {
                        string? first = null;
                        string? second = null;

                        if (merge.ValueKind == JsonValueKind.Array && merge.GetArrayLength() == 2)
                        {
                            first = merge[0].GetString();
                            second = merge[1].GetString();
                        }
                        else if (merge.ValueKind == JsonValueKind.String)
                        {
                            string? mergeStr = merge.GetString();
                            if (mergeStr != null)
                            {
                                int spaceIdx = mergeStr.IndexOf(' ');
                                if (spaceIdx > 0)
                                {
                                    first = mergeStr.Substring(0, spaceIdx);
                                    second = mergeStr.Substring(spaceIdx + 1);
                                }
                            }
                        }

                        if (first != null && second != null)
                        {
                            if (!_mergeRanks.TryGetValue(first, out var inner))
                            {
                                inner = new Dictionary<string, int>(StringComparer.Ordinal);
                                _mergeRanks[first] = inner;
                            }
                            inner[second] = i;
                        }

                        i++;
                    }
                }
            }

            if (root.TryGetProperty("added_tokens", out var addedTokensElement))
            {
                foreach (var token in addedTokensElement.EnumerateArray())
                {
                    if (token.TryGetProperty("content", out var contentElement))
                    {
                        string? content = contentElement.GetString();
                        if (content != null)
                        {
                            _specialTokens.Add(content);
                            if (token.TryGetProperty("id", out var idEl))
                                _specialTokenIds[content] = idEl.GetInt32();
                        }
                    }
                }
            }

            if (root.TryGetProperty("normalizer", out var normalizerElement))
            {
                ParseNormalizer(normalizerElement);
            }

            if (root.TryGetProperty("pre_tokenizer", out var preTokenizerElement))
            {
                ParsePreTokenizer(preTokenizerElement);
            }
        }

        private void ParseNormalizer(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object)
                return;

            string? type = element.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;

            if (type == "NFC")
            {
                _useNfc = true;
                return;
            }

            if (type != "Replace")
                return;

            if (element.TryGetProperty("pattern", out var patternEl) &&
                element.TryGetProperty("content", out var contentEl))
            {
                // Pattern can be { "String": "..." } or just a string
                _normalizePattern = patternEl.ValueKind == JsonValueKind.Object
                    ? patternEl.TryGetProperty("String", out var s) ? s.GetString() : null
                    : patternEl.GetString();
                _normalizeReplacement = contentEl.GetString();
            }
        }

        private void ParsePreTokenizer(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object)
                return;

            string? type = element.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;

            if (type == "Sequence")
            {
                if (element.TryGetProperty("pretokenizers", out var listEl))
                {
                    foreach (var pt in listEl.EnumerateArray())
                        ParsePreTokenizer(pt);
                }
                return;
            }

            if (type == "Split")
            {
                // Split pre-tokenizer. In real tokenizer.json the pattern is a Regex,
                // not a literal string. behavior "Isolated" keeps the matches as tokens.
                if (element.TryGetProperty("pattern", out var patternEl))
                {
                    string? pattern = null;
                    if (patternEl.ValueKind == JsonValueKind.Object)
                    {
                        if (patternEl.TryGetProperty("Regex", out var regexEl))
                            pattern = regexEl.GetString();
                        else if (patternEl.TryGetProperty("String", out var s))
                            pattern = s.GetString();
                    }
                    else if (patternEl.ValueKind == JsonValueKind.String)
                    {
                        pattern = patternEl.GetString();
                    }

                    if (!string.IsNullOrEmpty(pattern))
                    {
                        _preTokenizerRegex = new Regex(pattern, RegexOptions.Compiled);
                        _splitPattern = null;
                    }
                    else
                    {
                        _splitPattern = null;
                    }
                }
            }
            else if (type == "ByteLevel")
            {
                _useByteLevel = true;
            }
            else
            {
                // Try to extract Regex pattern for unknown types
                if (element.TryGetProperty("pattern", out var patternEl))
                {
                    string? pattern = null;
                    if (patternEl.ValueKind == JsonValueKind.Object &&
                        patternEl.TryGetProperty("Regex", out var regexEl))
                    {
                        pattern = regexEl.GetString();
                    }
                    else if (patternEl.ValueKind == JsonValueKind.String)
                    {
                        pattern = patternEl.GetString();
                    }
                    if (!string.IsNullOrEmpty(pattern))
                        _preTokenizerRegex = new Regex(pattern, RegexOptions.Compiled);
                }
            }
        }

        public int CountTokens(string text)
        {
            if (string.IsNullOrEmpty(text))
                return 0;

            // Apply normalizer
            if (_useNfc)
                text = text.Normalize(NormalizationForm.FormC);

            if (!string.IsNullOrEmpty(_normalizePattern) && !string.IsNullOrEmpty(_normalizeReplacement))
            {
                text = text.Replace(_normalizePattern, _normalizeReplacement);
            }

            if (text.Length < 100 && _cache.TryGetValue(text, out var cached))
                return cached;

            int count = 0;

            // Special tokens (added_tokens) are matched as whole units first.
            if (_specialTokens.Count > 0)
            {
                _specialTokenRegex ??= BuildSpecialTokenRegex();
                int lastIndex = 0;
                foreach (Match m in _specialTokenRegex.Matches(text))
                {
                    if (m.Index > lastIndex)
                        count += CountByPretokenizers(text.Substring(lastIndex, m.Index - lastIndex));
                    count += 1;
                    lastIndex = m.Index + m.Length;
                }
                if (lastIndex < text.Length)
                    count += CountByPretokenizers(text.Substring(lastIndex));
            }
            else
            {
                count = CountByPretokenizers(text);
            }

            if (text.Length < 100)
            {
                if (_cache.Count >= MAX_CACHE_SIZE)
                    _cache.Clear();
                _cache[text] = count;
            }

            return count;
        }

        private int CountByPretokenizers(string text)
        {
            int count = 0;

            if (!string.IsNullOrEmpty(_splitPattern))
            {
                // Legacy literal-split pre-tokenizer
                var parts = text.Split(_splitPattern, StringSplitOptions.None);
                foreach (var word in parts)
                {
                    if (string.IsNullOrEmpty(word))
                        continue;
                    string bl = _useByteLevel ? BytesToUnicode(word) : word;
                    count += CountWordTokens(bl);
                }
                return count;
            }

            // Regex pre-tokenizer (Split / Sequence)
            foreach (Match match in _preTokenizerRegex.Matches(text))
            {
                string word = match.Value;
                string bl = _useByteLevel ? BytesToUnicode(word) : word;
                count += CountWordTokens(bl);
            }
            return count;
        }

        private Regex BuildSpecialTokenRegex()
        {
            var escaped = _specialTokens
                .Where(s => !string.IsNullOrEmpty(s))
                .OrderByDescending(s => s.Length)
                .Select(Regex.Escape);
            return new Regex(string.Join("|", escaped), RegexOptions.Compiled);
        }

        private int CountWordTokens(string word)
        {
            if (word.Length == 0)
                return 0;

            var parts = new List<string>(word.Length);
            foreach (char c in word)
            {
                parts.Add(c.ToString());
            }

            while (parts.Count > 1)
            {
                int bestRank = int.MaxValue;
                int bestIdx = -1;

                for (int i = 0; i < parts.Count - 1; i++)
                {
                    string first = parts[i];
                    string second = parts[i + 1];

                    if (_mergeRanks.TryGetValue(first, out var inner) &&
                        inner.TryGetValue(second, out var rank) &&
                        rank < bestRank)
                    {
                        bestRank = rank;
                        bestIdx = i;
                    }
                }

                if (bestIdx == -1)
                    break;

                parts[bestIdx] = parts[bestIdx] + parts[bestIdx + 1];
                parts.RemoveAt(bestIdx + 1);
            }

            return parts.Count;
        }

        public List<int> Encode(string text)
        {
            var result = new List<int>();

            if (string.IsNullOrEmpty(text))
                return result;

            // Apply normalizer
            if (_useNfc)
                text = text.Normalize(NormalizationForm.FormC);

            if (!string.IsNullOrEmpty(_normalizePattern) && !string.IsNullOrEmpty(_normalizeReplacement))
            {
                text = text.Replace(_normalizePattern, _normalizeReplacement);
            }

            if (_specialTokens.Count > 0)
            {
                _specialTokenRegex ??= BuildSpecialTokenRegex();
                int lastIndex = 0;
                foreach (Match m in _specialTokenRegex.Matches(text))
                {
                    if (m.Index > lastIndex)
                        EncodeByPretokenizers(text.Substring(lastIndex, m.Index - lastIndex), result);
                    if (_specialTokenIds.TryGetValue(m.Value, out var spId))
                        result.Add(spId);
                    lastIndex = m.Index + m.Length;
                }
                if (lastIndex < text.Length)
                    EncodeByPretokenizers(text.Substring(lastIndex), result);
                return result;
            }

            EncodeByPretokenizers(text, result);
            return result;
        }

        private void EncodeByPretokenizers(string text, List<int> result)
        {
            if (!string.IsNullOrEmpty(_splitPattern))
            {
                var parts = text.Split(_splitPattern, StringSplitOptions.None);
                foreach (var word in parts)
                {
                    if (string.IsNullOrEmpty(word))
                        continue;
                    string bl = _useByteLevel ? BytesToUnicode(word) : word;
                    if (_vocab.TryGetValue(bl, out var fullId))
                    {
                        result.Add(fullId);
                        continue;
                    }
                    foreach (var id in EncodeWord(bl))
                        result.Add(id);
                }
                return;
            }

            foreach (Match match in _preTokenizerRegex.Matches(text))
            {
                string word = match.Value;
                string bl = _useByteLevel ? BytesToUnicode(word) : word;
                if (_vocab.TryGetValue(bl, out var fullId))
                {
                    result.Add(fullId);
                    continue;
                }
                foreach (var id in EncodeWord(bl))
                    result.Add(id);
            }
        }

        private List<int> EncodeWord(string word)
        {
            if (_vocab.TryGetValue(word, out var fullId))
                return new List<int> { fullId };

            var parts = new List<string>(word.Length);
            foreach (char c in word)
                parts.Add(c.ToString());

            while (parts.Count > 1)
            {
                int bestRank = int.MaxValue;
                int bestIdx = -1;

                for (int i = 0; i < parts.Count - 1; i++)
                {
                    if (_mergeRanks.TryGetValue(parts[i], out var inner) &&
                        inner.TryGetValue(parts[i + 1], out var rank) &&
                        rank < bestRank)
                    {
                        bestRank = rank;
                        bestIdx = i;
                    }
                }

                if (bestIdx == -1)
                    break;

                parts[bestIdx] = parts[bestIdx] + parts[bestIdx + 1];
                parts.RemoveAt(bestIdx + 1);
            }

            var result = new List<int>(parts.Count);
            foreach (var part in parts)
            {
                if (_vocab.TryGetValue(part, out var id))
                    result.Add(id);
                else
                    result.Add(0);
            }
            return result;
        }

        public string Decode(int[] tokenIds)
        {
            var sb = new StringBuilder();
            var inverseVocab = _vocab.ToDictionary(kvp => kvp.Value, kvp => kvp.Key);
            foreach (var id in tokenIds)
            {
                if (inverseVocab.TryGetValue(id, out var text))
                {
                    sb.Append(_useByteLevel ? UnicodeToBytes(text) : text);
                }
                else if (TryGetSpecialTokenById(id, out var spText))
                {
                    sb.Append(spText);
                }
            }
            return sb.ToString();
        }

        private bool TryGetSpecialTokenById(int id, out string content)
        {
            content = "";
            foreach (var kvp in _specialTokenIds)
            {
                if (kvp.Value == id)
                {
                    content = kvp.Key;
                    return true;
                }
            }
            return false;
        }
    }
}
