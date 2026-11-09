
namespace Amberline.Core.Buffs;

/// <summary>Mutable lifecycle record for one active buff slot.</summary>
public sealed class BuffState
{
    public BuffState(
        string name,
        BuffPayload? payload = null,
        BuffType type = BuffType.Internal,
        int slot = -1,
        double expiresAt = 0.0,
        uint casterNetId = 0,
        int stacks = 1,
        int? maxStack = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (slot < -1) throw new ArgumentOutOfRangeException(nameof(slot));
        if (stacks < 0) throw new ArgumentOutOfRangeException(nameof(stacks));
        if (maxStack is < 0) throw new ArgumentOutOfRangeException(nameof(maxStack));
        ExpiresAt = Finite(expiresAt, nameof(expiresAt));
        Name = name;
        Payload = payload ?? new BuffPayload();
        Type = type;
        Slot = slot;
        WireSlot = slot;
        CasterNetId = casterNetId;
        Stacks = stacks;
        MaxStack = maxStack;
    }

    public string Name { get; }
    public int Slot { get; set; }

    /// <summary>
    /// Batch 399 (Reconstruction): an overlapping add (BUFF_STACKS_AND_OVERLAPS) of a name the unit already carries from the same caster joins
    /// that copy's client slot. Every live instance of such a group names <see cref="WireSlot"/> on the wire, and the client shows one icon whose
    /// count is the group's size; <see cref="Slot"/> stays this instance's own entry in the server's table.
    /// </summary>
    public bool Overlapping { get; set; }

    /// <summary>The client slot this instance's wire packets name: its own <see cref="Slot"/>, or the slot of its overlapping group.</summary>
    public int WireSlot { get; set; }

    public BuffType Type { get; set; }
    public double ExpiresAt
    {
        get => _expiresAt;
        set => _expiresAt = Finite(value, nameof(ExpiresAt));
    }
    public uint CasterNetId { get; set; }
    public int Stacks
    {
        get => _stacks;
        set => _stacks = value < 0 ? throw new ArgumentOutOfRangeException(nameof(Stacks)) : value;
    }
    public int? MaxStack
    {
        get => _maxStack;
        set => _maxStack = value is < 0
            ? throw new ArgumentOutOfRangeException(nameof(MaxStack))
            : value;
    }
    public BuffPayload Payload
    {
        get => _payload;
        set => _payload = value ?? throw new ArgumentNullException(nameof(value));
    }

    public bool HasName(string name) => string.Equals(Name, name, StringComparison.Ordinal);

    /// <summary>
    /// The <c>isHidden</c> byte every 0xBF of this buff carries, the one place the three senders (the live add, the observer replay and
    /// the neutral camp buffs) read it from. The stock client stores it in the buff instance,
    /// and its HUD buff bar draws an instance only when the byte is clear, the type is not
    /// <see cref="BuffType.Internal"/>, the buff is active and its <c>BuffName</c> is not the character's <c>Passive1LuaName</c> (that buff
    /// is the passive slot's); the icon is the script's <c>BuffTextureName</c> (an instance with none is drawn as a blank square). Retail
    /// sends the add's <c>IsHiddenOnClient</c> here; the 0xBF row of docs/network/duel-implementation.md records it.
    /// </summary>
    public byte HiddenOnClientByte => Payload.HiddenOnClient ? (byte)1 : (byte)0;

    /// <summary>
    /// Whether the holder cannot act: a stun, a stasis (the emulator treats every <see cref="BuffType.Invulnerability"/> buff as
    /// Zhonya's Hourglass stasis) or a payload that disables actions. A BuildingBlocks script's own invulnerability buff only
    /// protects its holder (<c>SetInvulnerable</c> sets no action restriction in the shipped scripts, Kayle's Intervention), so its
    /// payload carries <see cref="BuffPayload.ActionsAllowed"/>.
    /// </summary>
    public bool DisablesActions => (Type == BuffType.Stun && !Payload.ActionsAllowed) ||
        (Type == BuffType.Invulnerability && !Payload.ActionsAllowed) || Payload.ActionDisabled;

    private static double Finite(double value, string name) => double.IsFinite(value)
        ? value
        : throw new ArgumentOutOfRangeException(name, value, "buff state values must be finite");

    private double _expiresAt;
    private int _stacks;
    private int? _maxStack;
    private BuffPayload _payload = null!;
}
