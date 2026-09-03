using System.Text.Json;

namespace RelicFrame.Core;

public sealed class SellerBlacklist
{
    private readonly string path;
    private readonly object gate = new();
    private HashSet<string> names = new(StringComparer.Ordinal);
    // Callers must not mutate this snapshot. Every write publishes a new set after it is safely persisted.
    internal ISet<string> Snapshot => Volatile.Read(ref names);
    public string[] Names => Volatile.Read(ref names).Order(StringComparer.Ordinal).ToArray();
    public SellerBlacklist(string path)
    {
        this.path = path;
        if (!File.Exists(path)) return;
        using var file = File.OpenRead(path); using var doc = JsonDocument.Parse(file);
        names = doc.RootElement.Get("blacklisted_sellers").Rows().Select(v => v.Text().Trim().ToLowerInvariant()).Where(v => v.Length > 0).ToHashSet(StringComparer.Ordinal);
    }
    public bool Change(string seller, bool add)
    {
        var key = seller.Trim().ToLowerInvariant(); if (key.Length == 0 || key.Length > 100) throw new ArgumentException("Invalid seller name.");
        lock (gate)
        {
            var next = new HashSet<string>(names, StringComparer.Ordinal);
            var changed = add ? next.Add(key) : next.Remove(key); if (!changed) return false;
            Json.WriteAtomic(path, new { blacklisted_sellers = next.Order(StringComparer.Ordinal).ToArray() });
            Volatile.Write(ref names, next); return true;
        }
    }
}
