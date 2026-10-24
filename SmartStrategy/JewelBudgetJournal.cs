using System.Globalization;
using System.Text;
using System.Text.Json;
using MelonLoader.Utils;

namespace SmartStrategy;

internal enum JewelPurchaseKind { Refinery, PaidSouls }

// Pure persistence boundary. Native calls are forbidden until a pending record has survived
// atomic replacement, disk readback and a flush. Unknown or legacy pending charges fail closed.
internal sealed class JewelBudgetJournal
{
    private const int MaximumFileBytes = 65536;
    private static readonly string[] LedgerFields =
        { "Schema", "Date", "Spent", "RefinerySpent", "PaidSoulsSpent", "Pending" };
    private static readonly string[] ChargeFields =
        { "Token", "Kind", "Cost", "BalanceBefore", "StateBefore", "Description" };

    internal sealed class Charge
    {
        public string Token { get; set; }
        public string Kind { get; set; }
        public double Cost { get; set; }
        public double BalanceBefore { get; set; }
        public string StateBefore { get; set; }
        public string Description { get; set; }
    }

    internal sealed class Ledger
    {
        public int Schema { get; set; } = 1;
        public string Date { get; set; } = "";
        public double Spent { get; set; }
        public double RefinerySpent { get; set; }
        public double PaidSoulsSpent { get; set; }
        public Charge Pending { get; set; }
        internal Ledger Copy() => (Ledger)MemberwiseClone();
    }

    private readonly string _path = Path.Combine(MelonEnvironment.UserDataDirectory, "SmartStrategy_JewelBudget.json");
    private readonly string _legacyPath = Path.Combine(MelonEnvironment.UserDataDirectory, "SmartStrategy_RefineryBudget.cfg");
    private bool _persisted;

    internal Ledger Load()
    {
        if (!File.Exists(_path))
        {
            if (_persisted) throw new IOException("The Jewel budget journal disappeared.");
            return ImportLegacy();
        }
        var ledger = Read(_path);
        _persisted = true;
        return ledger;
    }

    internal void Save(Ledger ledger)
    {
        Validate(ledger, requirePersistedDate: true);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(ledger);
        if (bytes.Length <= 0 || bytes.Length > MaximumFileBytes)
            throw new InvalidDataException("Invalid serialized Jewel journal size.");
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            output.Write(bytes);
            output.Flush(flushToDisk: true);
        }
        if (File.Exists(_path)) File.Replace(temporary, _path, null);
        else File.Move(temporary, _path);
        // A mismatched/failed write stops all paid actions, even if the original file still exists.
        using (var persisted = new FileStream(_path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            persisted.Flush(flushToDisk: true);
        if (!ReadBoundedBytes(_path).AsSpan().SequenceEqual(bytes))
            throw new IOException("Jewel budget journal readback did not match.");
        _persisted = true;
    }

    private Ledger ImportLegacy()
    {
        if (!File.Exists(_legacyPath)) return new Ledger();
        var bytes = ReadBoundedBytes(_legacyPath);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var inCategory = false;
        foreach (var raw in Encoding.UTF8.GetString(bytes).Split('\n'))
        {
            var line = raw.Trim().TrimStart('\uFEFF');
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith('[')) { inCategory = line == "[SmartStrategy_RefineryBudget]"; continue; }
            if (!inCategory) continue;
            var equals = line.IndexOf('=');
            if (equals <= 0) throw new InvalidDataException("Invalid legacy journal entry.");
            var key = line[..equals].Trim();
            if (key is "date" or "spent" or "pending")
                if (!values.TryAdd(key, line[(equals + 1)..].Trim())) throw new InvalidDataException("Duplicate legacy entry.");
        }
        if (values.Count != 3 || !double.TryParse(values["spent"], NumberStyles.Float,
                CultureInfo.InvariantCulture, out var spent)) throw new InvalidDataException("Incomplete legacy journal.");
        var pending = Unquote(values["pending"]);
        var ledger = new Ledger
        {
            Date = Unquote(values["date"]), Spent = spent, RefinerySpent = spent,
            Pending = pending.Length == 0 ? null : new Charge
            {
                Token = "legacy-refinery", Kind = "Legacy", Cost = 0, BalanceBefore = 0,
                StateBefore = pending, Description = "Unresolved legacy refinery charge; manual reconciliation required"
            }
        };
        Validate(ledger);
        return ledger;
    }

    private static Ledger Read(string path)
    {
        var bytes = ReadBoundedBytes(path);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        // Older ledgers contain this retired field. Accept and ignore it while preserving all
        // spending and pending-charge data; the next normal save omits it.
        RequireFields(document.RootElement, LedgerFields, optional: "LegacyDigest");
        var pending = document.RootElement.GetProperty("Pending");
        if (pending.ValueKind != JsonValueKind.Null) RequireFields(pending, ChargeFields);
        var ledger = JsonSerializer.Deserialize<Ledger>(bytes);
        Validate(ledger, requirePersistedDate: true);
        return ledger;
    }

    private static void RequireFields(JsonElement value, IReadOnlyList<string> required, string optional = null)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Jewel journal records must be JSON objects.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if ((!required.Contains(property.Name, StringComparer.Ordinal) && property.Name != optional) ||
                !seen.Add(property.Name))
                throw new InvalidDataException("Unknown or duplicate Jewel journal field: " + property.Name);
        if (required.Any(name => !seen.Contains(name)))
            throw new InvalidDataException("Required Jewel journal fields are missing.");
    }

    private static byte[] ReadBoundedBytes(string path)
    {
        // Hold a read-only share while reading so a replaced or growing file cannot bypass the
        // size check between FileInfo and ReadAllBytes. Do not allocate from unchecked input size.
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var length = input.Length;
        if (length <= 0 || length > MaximumFileBytes)
            throw new InvalidDataException("Invalid Jewel journal size.");
        var bytes = new byte[(int)length];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = input.Read(bytes, offset, bytes.Length - offset);
            if (read == 0) throw new EndOfStreamException("Jewel journal changed during its read.");
            offset += read;
        }
        if (input.ReadByte() != -1) throw new InvalidDataException("Jewel journal grew during its read.");
        return bytes;
    }

    private static void Validate(Ledger ledger, bool requirePersistedDate = false)
    {
        if (ledger == null || ledger.Schema != 1 || ledger.Date == null ||
            !NonNegative(ledger.Spent) || !NonNegative(ledger.RefinerySpent) || !NonNegative(ledger.PaidSoulsSpent) ||
            Math.Abs(ledger.Spent - ledger.RefinerySpent - ledger.PaidSoulsSpent) > 0.0001 ||
            ledger.Date.Length != 0 && !DateTime.TryParseExact(ledger.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _) || ledger.Date.Length == 0 &&
            (requirePersistedDate || ledger.Spent > 0 || ledger.Pending != null))
            throw new InvalidDataException("Invalid Jewel budget ledger.");
        var pending = ledger.Pending;
        if (pending != null && (string.IsNullOrWhiteSpace(pending.Token) ||
            pending.Kind is not ("Legacy" or "Refinery" or "PaidSouls") || !NonNegative(pending.Cost) ||
            !NonNegative(pending.BalanceBefore) || string.IsNullOrWhiteSpace(pending.StateBefore) ||
            string.IsNullOrWhiteSpace(pending.Description) || pending.Cost > ledger.Spent + 0.0001 ||
            pending.Kind != "Legacy" && (pending.Cost <= 0 || pending.Cost > pending.BalanceBefore + 0.0001) ||
            pending.Kind == "Refinery" && pending.Cost > ledger.RefinerySpent + 0.0001 ||
            pending.Kind == "PaidSouls" && pending.Cost > ledger.PaidSoulsSpent + 0.0001))
            throw new InvalidDataException("Invalid pending Jewel charge.");
    }
    private static string Unquote(string value) => value.Length >= 2 && value[0] == '\'' && value[^1] == '\''
        ? value[1..^1] : JsonSerializer.Deserialize<string>(value) ?? throw new InvalidDataException("Missing legacy string.");
    private static bool NonNegative(double value) => double.IsFinite(value) && value >= 0;
}
