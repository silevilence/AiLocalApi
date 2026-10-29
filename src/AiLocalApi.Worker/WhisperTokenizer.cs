using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AiLocalApi.Worker;

internal sealed class WhisperTokenizer
{
    private readonly Dictionary<string, int> vocabulary;
    private readonly Dictionary<int, byte[]> bytes;
    private readonly Dictionary<(string, string), int> merges = [];
    private static readonly char[] ByteCharacters = MakeByteCharacters();
    private static readonly Regex Pieces = new(@"'s|'t|'re|'ve|'m|'ll|'d| ?\p{L}+| ?\p{N}+| ?[^\s\p{L}\p{N}]+|\s+(?!\S)|\s+", RegexOptions.CultureInvariant);
    public int End => Id("<|endoftext|>");
    public int TimestampBegin => Id("<|notimestamps|>") + 1;
    public WhisperTokenizer(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var model = document.RootElement.GetProperty("model");
        vocabulary = model.GetProperty("vocab").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32());
        if (document.RootElement.TryGetProperty("added_tokens", out var added))
            foreach (var item in added.EnumerateArray()) vocabulary[item.GetProperty("content").GetString()!] = item.GetProperty("id").GetInt32();
        var rank = 0;
        foreach (var item in model.GetProperty("merges").EnumerateArray())
        {
            var pair = item.ValueKind == JsonValueKind.String ? item.GetString()!.Split(' ') : item.EnumerateArray().Select(v => v.GetString()!).ToArray();
            merges[(pair[0], pair[1])] = rank++;
        }
        var reverse = ByteCharacters.Select((c, i) => (c, i)).ToDictionary(x => x.c, x => (byte)x.i);
        bytes = vocabulary.Where(pair => !pair.Key.StartsWith("<|", StringComparison.Ordinal))
            .ToDictionary(pair => pair.Value, pair => pair.Key.Select(c => reverse[c]).ToArray());
    }
    public int Id(string token) => vocabulary.TryGetValue(token, out var id) ? id : throw new InvalidDataException($"Tokenizer lacks {token}");
    public byte[] Bytes(int token) => bytes.GetValueOrDefault(token) ?? [];
    public string Decode(IEnumerable<int> tokens) => new UTF8Encoding(false, true).GetString(tokens.SelectMany(Bytes).ToArray());
    public int[] Encode(string text)
    {
        var result = new List<int>();
        foreach (Match piece in Pieces.Matches(text))
        {
            var parts = Encoding.UTF8.GetBytes(piece.Value).Select(b => ByteCharacters[b].ToString()).ToList();
            while (parts.Count > 1)
            {
                var best = int.MaxValue; var position = -1;
                for (var i = 0; i < parts.Count - 1; i++)
                    if (merges.TryGetValue((parts[i], parts[i + 1]), out var rank) && rank < best) { best = rank; position = i; }
                if (position < 0) break;
                parts[position] += parts[position + 1]; parts.RemoveAt(position + 1);
            }
            result.AddRange(parts.Select(Id));
        }
        return result.ToArray();
    }
    private static char[] MakeByteCharacters()
    {
        var allowed = Enumerable.Range(33, 94).Concat(Enumerable.Range(161, 12)).Concat(Enumerable.Range(174, 82)).ToHashSet();
        var extra = 256; return Enumerable.Range(0, 256).Select(b => allowed.Contains(b) ? (char)b : (char)extra++).ToArray();
    }
}
