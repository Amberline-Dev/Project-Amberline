using System.Text.Json;

namespace Amberline.Platform;

/// <summary>
/// NOT RETAIL: retail's chat server (an XMPP server on pvp.net) kept rosters and privacy lists in its own database;
/// this is the local JSON equivalent behind <see cref="AirXmppHost"/>.
/// The small persistent part of the AIR social protocol. It deliberately contains
/// account ids and preferences only; chat credentials and stanza data never enter
/// this store.
/// </summary>
public sealed class PlatformSocialStore : IDisposable
{
    // ChatController.MAXIMUM_BUDDY_LIST_SIZE: the stock client refuses a 301st buddy itself, so the store must hold 300.
    public const int MaxContacts = 300;

    public sealed record Contact(long OwnerId, long ContactId, string Group, string Subscription, bool Ask);
    public sealed record Block(long OwnerId, long BlockedId);

    private sealed record State(Contact[] Contacts, Block[] Blocks);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.General)
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly FileStream _lease;
    private State _state;

    public PlatformSocialStore(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("A social-store directory is required.", nameof(directory));
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "social.json");
        _lease = new FileStream(Path.Combine(directory, "social.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        try
        {
            _state = File.Exists(_path)
                ? JsonSerializer.Deserialize<State>(File.ReadAllText(_path), Json) ?? throw new InvalidDataException("Empty social store.")
                : new State([], []);
            Validate(_state);
        }
        catch
        {
            _lease.Dispose();
            throw;
        }
    }

    public Contact[] Contacts(long ownerId)
    {
        lock (_gate) return _state.Contacts.Where(x => x.OwnerId == ownerId).ToArray();
    }

    internal Contact[] AllContacts()
    {
        lock (_gate) return _state.Contacts.ToArray();
    }

    public bool TryGetContact(long ownerId, long contactId, out Contact contact)
    {
        lock (_gate)
        {
            contact = _state.Contacts.FirstOrDefault(x => x.OwnerId == ownerId && x.ContactId == contactId)!;
            return contact is not null;
        }
    }

    /// <summary>Whether <paramref name="ownerId"/> can still hold a contact row for <paramref name="contactId"/>.</summary>
    public bool HasRoom(long ownerId, long contactId)
    {
        lock (_gate)
            return _state.Contacts.Any(x => x.OwnerId == ownerId && x.ContactId == contactId) ||
                _state.Contacts.Count(x => x.OwnerId == ownerId) < MaxContacts;
    }

    public void SetContact(long ownerId, long contactId, string group, string subscription, bool ask)
    {
        if (ownerId <= 0 || contactId <= 0 || ownerId == contactId) throw new ArgumentException("Invalid contact identity.");
        if (group.Length is < 1 or > 64 || group.Any(char.IsControl)) throw new ArgumentException("Invalid contact group.");
        if (subscription is not ("none" or "to" or "from" or "both")) throw new ArgumentException("Invalid subscription.");
        lock (_gate)
        {
            var current = _state.Contacts.FirstOrDefault(x => x.OwnerId == ownerId && x.ContactId == contactId);
            if (current is null && _state.Contacts.Count(x => x.OwnerId == ownerId) >= MaxContacts)
                throw new InvalidOperationException("Social contact capacity reached.");
            var value = new Contact(ownerId, contactId, group, subscription, ask);
            Save(_state with { Contacts = [.. _state.Contacts.Where(x => x != current), value] });
        }
    }

    public void RemoveContact(long ownerId, long contactId)
    {
        lock (_gate) Save(_state with
        {
            Contacts = _state.Contacts.Where(x => x.OwnerId != ownerId || x.ContactId != contactId).ToArray()
        });
    }

    public Block[] Blocks(long ownerId)
    {
        lock (_gate) return _state.Blocks.Where(x => x.OwnerId == ownerId).ToArray();
    }

    public bool IsBlocked(long ownerId, long blockedId)
    {
        lock (_gate) return _state.Blocks.Any(x => x.OwnerId == ownerId && x.BlockedId == blockedId);
    }

    public void SetBlocked(long ownerId, long blockedId, bool blocked)
    {
        if (ownerId <= 0 || blockedId <= 0 || ownerId == blockedId) throw new ArgumentException("Invalid block identity.");
        lock (_gate)
        {
            var blocks = _state.Blocks.Where(x => x.OwnerId != ownerId || x.BlockedId != blockedId).ToList();
            if (blocked) blocks.Add(new Block(ownerId, blockedId));
            Save(_state with { Blocks = [.. blocks] });
        }
    }

    public void ReplaceBlocks(long ownerId, IEnumerable<long> blockedIds)
    {
        var ids = blockedIds.Where(id => id > 0 && id != ownerId).Distinct().ToArray();
        lock (_gate)
        {
            var blocks = _state.Blocks.Where(x => x.OwnerId != ownerId).Concat(ids.Select(id => new Block(ownerId, id))).ToArray();
            Save(_state with { Blocks = blocks });
        }
    }

    /// <summary>
    /// NOT RETAIL: the host deleted the login (batch 748, the Users page): every contact row it holds or is named in, and every block either
    /// way, leave the store; the file is written only when one left.
    /// </summary>
    public void RemoveAccount(long accountId)
    {
        lock (_gate)
        {
            var contacts = _state.Contacts.Where(x => x.OwnerId != accountId && x.ContactId != accountId).ToArray();
            var blocks = _state.Blocks.Where(x => x.OwnerId != accountId && x.BlockedId != accountId).ToArray();
            if (contacts.Length != _state.Contacts.Length || blocks.Length != _state.Blocks.Length)
                Save(_state with { Contacts = contacts, Blocks = blocks });
        }
    }

    private void Save(State state)
    {
        Validate(state);
        // ponytail: the social snapshot is intentionally one tiny atomic JSON file.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, Json);
        var temporary = _path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(true);
        }
        File.Move(temporary, _path, true);
        _state = state;
    }

    private static void Validate(State state)
    {
        if (state.Contacts.Any(x => x.OwnerId <= 0 || x.ContactId <= 0 || x.OwnerId == x.ContactId ||
            x.Subscription is not ("none" or "to" or "from" or "both")))
            throw new InvalidDataException("Invalid social contact.");
        if (state.Contacts.GroupBy(x => (x.OwnerId, x.ContactId)).Any(g => g.Count() != 1))
            throw new InvalidDataException("Duplicate social contact.");
        if (state.Contacts.GroupBy(x => x.OwnerId).Any(g => g.Count() > MaxContacts))
            throw new InvalidDataException("Too many social contacts.");
        if (state.Blocks.Any(x => x.OwnerId <= 0 || x.BlockedId <= 0 || x.OwnerId == x.BlockedId))
            throw new InvalidDataException("Invalid social block.");
        if (state.Blocks.Distinct().Count() != state.Blocks.Length)
            throw new InvalidDataException("Duplicate social block.");
    }

    public void Dispose() => _lease.Dispose();
}
