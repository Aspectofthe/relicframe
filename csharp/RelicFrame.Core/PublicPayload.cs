using System.Globalization;
using System.Text;
using System.Text.Json;

namespace RelicFrame.Core;

// DE's weekly endpoint can contain JavaScript object literals instead of JSON.
// This deliberately accepts literal data only. It cannot evaluate code, calls or properties.
public static class PublicPayload
{
    public static JsonDocument Parse(string text)
    {
        if (text.Length > 8 * 1024 * 1024) throw new InvalidDataException("Public literal feed exceeded 8 MiB.");
        try
        {
            var json = JsonDocument.Parse(text);
            if (json.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array) return json;
            json.Dispose(); throw new JsonException("Public feed must be an object or array.");
        }
        catch (JsonException)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream)) new Parser(text, writer).Run();
            return JsonDocument.Parse(stream.ToArray());
        }
    }
    private sealed class Parser(string text, Utf8JsonWriter writer)
    {
        private int offset;
        private void Space() { while (offset < text.Length && char.IsWhiteSpace(text[offset])) offset++; }
        private char Peek() { Space(); return offset < text.Length ? text[offset] : '\0'; }
        private bool Take(char c) { if (Peek() != c) return false; offset++; return true; }
        private void Require(char c) { if (!Take(c)) throw new JsonException("Invalid public object literal."); }
        public void Run()
        {
            if (Peek() is not ('{' or '[')) throw new JsonException("Expected object or array.");
            Value(0); Space(); if (offset != text.Length) throw new JsonException("Unexpected trailing content.");
        }
        private string Quoted()
        {
            var quote = text[offset++]; var result = new StringBuilder();
            while (offset < text.Length)
            {
                var c = text[offset++]; if (c == quote) return result.ToString();
                if (c != '\\') { if (c < 32) throw new JsonException("Control character in string."); result.Append(c); continue; }
                if (offset == text.Length) throw new JsonException("Incomplete string escape.");
                var escape = text[offset++];
                if (escape == 'u')
                {
                    if (offset + 4 > text.Length || !ushort.TryParse(text.AsSpan(offset, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code)) throw new JsonException("Invalid Unicode escape.");
                    result.Append((char)code); offset += 4;
                }
                else result.Append(escape switch { '\\' => '\\', '/' => '/', '\'' => '\'', '"' => '"', 'b' => '\b', 'f' => '\f', 'n' => '\n', 'r' => '\r', 't' => '\t', _ => throw new JsonException("Invalid string escape.") });
            }
            throw new JsonException("Unterminated string.");
        }
        private void Value(int depth)
        {
            if (depth > 64) throw new JsonException("Public object literal too deeply nested.");
            var c = Peek();
            if (c == '{')
            {
                offset++; writer.WriteStartObject();
                if (!Take('}')) while (true)
                {
                    string key;
                    if (Peek() is '\'' or '"') key = Quoted();
                    else
                    {
                        var start = offset;
                        if (!(char.IsAsciiLetter(Peek()) || Peek() is '_' or '$')) throw new JsonException("Invalid object key.");
                        offset++;
                        while (offset < text.Length && (char.IsAsciiLetterOrDigit(text[offset]) || text[offset] is '_' or '$')) offset++;
                        key = text[start..offset];
                    }
                    writer.WritePropertyName(key); Require(':'); Value(depth + 1);
                    if (Take('}')) break; Require(','); if (Take('}')) break;
                }
                writer.WriteEndObject(); return;
            }
            if (c == '[')
            {
                offset++; writer.WriteStartArray();
                if (!Take(']')) while (true) { Value(depth + 1); if (Take(']')) break; Require(','); if (Take(']')) break; }
                writer.WriteEndArray(); return;
            }
            if (c is '\'' or '"') { writer.WriteStringValue(Quoted()); return; }
            var begin = offset;
            while (offset < text.Length && !char.IsWhiteSpace(text[offset]) && text[offset] is not (',' or '}' or ']')) offset++;
            var token = text[begin..offset];
            if (token == "true") writer.WriteBooleanValue(true);
            else if (token == "false") writer.WriteBooleanValue(false);
            else if (token == "null") writer.WriteNullValue();
            else
            {
                // JSON's numeric lexer also rejects NaN, Infinity and executable expressions.
                using var number = JsonDocument.Parse(token);
                if (number.RootElement.ValueKind != JsonValueKind.Number || !number.RootElement.TryGetDouble(out var d) || !double.IsFinite(d)) throw new JsonException("Invalid literal value.");
                number.RootElement.WriteTo(writer);
            }
        }
    }
}
