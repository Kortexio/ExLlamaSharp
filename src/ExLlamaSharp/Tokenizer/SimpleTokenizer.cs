using System.Buffers.Binary;
using System.Text;

namespace ExLlamaSharp.Tokenizer;

/// <summary>
/// Lightweight BPE-like tokenizer that works without Rust / HF tokenizers.
/// Splits on whitespace, then maps each piece (and byte fallbacks) to stable
/// hash-derived token ids. Suitable for mock / stub paths; swap for a real
/// tokenizer later.
/// </summary>
public sealed class SimpleTokenizer
{
    public const int BosTokenId = 1;
    public const int EosTokenId = 2;
    public const int UnkTokenId = 0;
    public const int VocabSize = 50_000;

    private readonly bool _addSpecialTokens;

    public SimpleTokenizer(bool addSpecialTokens = false)
    {
        _addSpecialTokens = addSpecialTokens;
    }

    public int[] Encode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length == 0)
        {
            return _addSpecialTokens ? [BosTokenId, EosTokenId] : [];
        }

        var pieces = text.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var list = new List<int>(pieces.Length + 2);
        if (_addSpecialTokens)
        {
            list.Add(BosTokenId);
        }

        foreach (var piece in pieces)
        {
            list.Add(HashToken(piece));

            // Byte-level fallback for very long tokens (pseudo BPE merge residue).
            if (piece.Length > 24)
            {
                var prefix = piece[..Math.Min(8, piece.Length)];
                foreach (var b in Encoding.UTF8.GetBytes(prefix))
                {
                    list.Add(3 + (b % (VocabSize - 16)));
                }
            }
        }

        if (_addSpecialTokens)
        {
            list.Add(EosTokenId);
        }

        return list.ToArray();
    }

    public string Decode(ReadOnlySpan<int> tokens)
    {
        if (tokens.IsEmpty)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(tokens.Length * 4);
        for (var i = 0; i < tokens.Length; i++)
        {
            var id = tokens[i];
            if (id is BosTokenId or EosTokenId or UnkTokenId)
            {
                continue;
            }

            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            sb.Append(TokenToPiece(id));
        }

        return sb.ToString();
    }

    public string Decode(IReadOnlyList<int> tokens) => Decode(tokens is int[] arr ? arr.AsSpan() : tokens.ToArray());

    private static int HashToken(string piece)
    {
        // FNV-1a 32-bit → vocab range, avoid reserved special ids 0..15.
        unchecked
        {
            uint hash = 2166136261;
            foreach (var c in piece)
            {
                hash ^= c;
                hash *= 16777619;
            }

            var id = (int)(hash % (uint)(VocabSize - 16)) + 16;
            return id;
        }
    }

    private static string TokenToPiece(int tokenId)
    {
        // Deterministic reversible-ish printable form for debugging / mock detokenize.
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, tokenId);
        var a = (char)('a' + (bytes[0] % 26));
        var b = (char)('a' + (bytes[1] % 26));
        var c = (char)('a' + (bytes[2] % 26));
        return $"{a}{b}{c}{tokenId % 100:D2}";
    }
}
