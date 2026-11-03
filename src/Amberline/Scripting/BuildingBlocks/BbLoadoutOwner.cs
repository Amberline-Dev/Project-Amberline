using Amberline.Scripting.BuildingBlocks.Runtime;

namespace Amberline.Scripting.BuildingBlocks;

/// <summary>
/// Which kind of loadout slot a shipped script instance is bound to. The champion module owns a hero's character, spell
/// and buff scripts; the loadout owns everything else that ships as a BuildingBlocks script and belongs to the hero
/// rather than to a spell: its talents, the item copies in its inventory and its two summoner spell slots.
/// </summary>
public enum BbLoadoutKind
{
    /// <summary><c>DATA/Talents/&lt;id&gt;.luaobj</c>: one talent of the mastery page, at its rank (<c>TalentLevel</c>).</summary>
    Talent,
    /// <summary><c>DATA/Items/&lt;id&gt;.luaobj</c>: one inventory copy, with its own <c>InstanceVars</c>.</summary>
    Item,
    /// <summary><c>DATA/Spells/Summoner&lt;name&gt;.luaobj</c>: one of the two summoner spell slots.</summary>
    Summoner,
}

/// <summary>
/// One live script instance of a hero's loadout: a talent rank, an inventory copy, or a summoner spell slot. Each binding
/// carries the variable tables the shipped scripts read for "this instance" — <see cref="InstanceVars"/> for a copy that
/// outlives a hook run, and the shared <see cref="BbLoadoutOwner.AvatarVars"/> of its hero for the avatar variables the
/// summoner scripts and the talent feeders exchange.
/// </summary>
public sealed class BbLoadoutBinding
{
    public BbLoadoutBinding(BbLoadoutKind kind, int id, int slot, BbScript script, int rank)
    {
        Kind = kind;
        Id = id;
        Slot = slot;
        Script = script;
        Rank = rank;
    }

    public BbLoadoutKind Kind { get; }

    /// <summary>The talent id, the item id, or the summoner spell id.</summary>
    public int Id { get; }

    /// <summary>The inventory slot of an item copy (0..5), the summoner slot (0..1), or -1 for a talent.</summary>
    public int Slot { get; internal set; }

    public BbScript Script { get; }

    /// <summary>The talent's rank (1-based <c>TalentLevel</c>); 1 for an item copy and a summoner slot.</summary>
    public int Rank { get; internal set; }

    /// <summary>
    /// The instance's own <c>InstanceVars</c>. A talent keeps its damage block and a summoner slot its cooldown bookkeeping
    /// for as long as the binding lives; an item copy's table travels with the copy when it is moved between slots and is
    /// dropped when the copy is sold or consumed, which is what "per-copy" means for the shipped item scripts.
    /// </summary>
    public BbTable InstanceVars { get; } = new BbTable();

    /// <summary>False once the copy is gone (sold, consumed, the talent reallocated) or its rank is 0.</summary>
    public bool Active { get; internal set; } = true;

    public override string ToString() => $"{Kind}/{Id}@{Slot}r{Rank}";
}

/// <summary>
/// The script owner that is not a champion. A <c>BbChampionModule</c> owns one champion's closure and answers only for its
/// own hero; a loadout owns the scripts a hero wears or has allocated, which several champions' heroes hold at once and
/// which never identify a champion. It keeps the per-hero binding set and the hero's <c>AvatarVars</c>, and builds the
/// scope every loadout hook runs in (<c>TalentLevel</c>, the binding's <c>InstanceVars</c>, that shared <c>AvatarVars</c>).
/// </summary>
public sealed class BbLoadoutOwner
{
    private readonly Dictionary<(BbLoadoutKind Kind, int Id), BbLoadoutBinding> _talents = new();
    private readonly Dictionary<int, BbLoadoutBinding> _items = new();
    private readonly Dictionary<int, BbLoadoutBinding> _summoners = new();

    /// <summary>
    /// The hero's avatar variables, shared by every binding of that hero and by the summoner scripts that read them. An
    /// entry that was never written reads nil, as every Lua table does (batch 303): the summoner scripts' own
    /// <c>CO_NOT_EQUAL 0</c> branches and <c>GetMathNumber</c> fallbacks are written for it.
    /// </summary>
    public BbTable AvatarVars { get; } = new BbTable();

    public IEnumerable<BbLoadoutBinding> Bindings =>
        _talents.Values.Concat(_items.Values).Concat(_summoners.Values);

    /// <summary>Every binding that still runs its hooks: a live talent rank, a worn item, a summoner slot.</summary>
    public IEnumerable<BbLoadoutBinding> ActiveBindings => Bindings.Where(binding => binding.Active);

    // ------------------------------------------------------------------------------------------------------------
    // Talents — (hero, talent id) with TalentLevel
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>The hero's binding for <paramref name="id"/>, or null when the talent is not allocated.</summary>
    public BbLoadoutBinding? Talent(int id) => _talents.TryGetValue((BbLoadoutKind.Talent, id), out var binding) ? binding : null;

    /// <summary>
    /// Allocates a talent at <paramref name="rank"/>. Reallocating replaces the binding and its <c>InstanceVars</c>, the way a
    /// mastery page change starts the talent over; a rank of 0 leaves no binding, so the script never runs at rank 0 (its
    /// <c>BBElse</c> arms would otherwise add the rank-1 marker buffs of 135).
    /// </summary>
    public BbLoadoutBinding? SetTalent(BbScript script, int id, int rank)
    {
        if (rank <= 0)
        {
            _talents.Remove((BbLoadoutKind.Talent, id));
            return null;
        }
        var binding = new BbLoadoutBinding(BbLoadoutKind.Talent, id, -1, script, rank);
        _talents[(BbLoadoutKind.Talent, id)] = binding;
        return binding;
    }

    // ------------------------------------------------------------------------------------------------------------
    // Items — (hero, inventory slot) with per-copy InstanceVars and sale/move semantics
    // ------------------------------------------------------------------------------------------------------------

    public BbLoadoutBinding? Item(int slot) => _items.TryGetValue(slot, out var binding) ? binding : null;

    /// <summary>Puts a copy of <paramref name="id"/> in <paramref name="slot"/> with a fresh <c>InstanceVars</c>.</summary>
    public BbLoadoutBinding SetItem(BbScript script, int id, int slot)
    {
        var binding = new BbLoadoutBinding(BbLoadoutKind.Item, id, slot, script, 1);
        _items[slot] = binding;
        return binding;
    }

    /// <summary>
    /// Moves the copy in <paramref name="fromSlot"/> to <paramref name="toSlot"/>. The <c>InstanceVars</c> travels with the copy:
    /// the shipped item scripts keep their counters and clocks in it, and a sale or a move must not reset them. The slot that
    /// is swapped into keeps whatever it held before the move (an inventory swap), and an empty source does nothing.
    /// </summary>
    public void MoveItem(int fromSlot, int toSlot)
    {
        if (fromSlot == toSlot || !_items.TryGetValue(fromSlot, out var moving)) return;
        var swapped = _items.TryGetValue(toSlot, out var other) ? other : null;
        moving.Slot = toSlot;
        _items[toSlot] = moving;
        if (swapped is not null)
        {
            swapped.Slot = fromSlot;
            _items[fromSlot] = swapped;
        }
        else _items.Remove(fromSlot);
    }

    /// <summary>
    /// The copy leaves the inventory (sold, consumed, replaced). Its <c>InstanceVars</c> goes with it: a later copy of the
    /// same item is a new instance and starts clean, exactly as the shipped scripts' per-copy counters expect.
    /// </summary>
    public void RemoveItem(int slot)
    {
        if (_items.TryGetValue(slot, out var binding)) binding.Active = false;
        _items.Remove(slot);
    }

    // ------------------------------------------------------------------------------------------------------------
    // Summoner spells — (hero, summoner slot)
    // ------------------------------------------------------------------------------------------------------------

    public BbLoadoutBinding? Summoner(int slot) => _summoners.TryGetValue(slot, out var binding) ? binding : null;

    public BbLoadoutBinding SetSummoner(BbScript script, int id, int slot)
    {
        var binding = new BbLoadoutBinding(BbLoadoutKind.Summoner, id, slot, script, 1);
        _summoners[slot] = binding;
        return binding;
    }

    /// <summary>
    /// Drops the talent bindings only, keeping the summoner slots and the hero's <c>AvatarVars</c>: a page change or a
    /// level-up starts the talents over (fresh <c>InstanceVars</c>, as before) without touching the two summoner spell
    /// slots, which belong to the hero's spellbook rather than to its mastery page.
    /// </summary>
    public void ClearTalents()
    {
        foreach (var binding in _talents.Values) binding.Active = false;
        _talents.Clear();
    }

    /// <summary>Drops every binding (the hero left the game, or the whole loadout was replaced).</summary>
    public void Clear()
    {
        foreach (var binding in Bindings) binding.Active = false;
        _talents.Clear();
        _items.Clear();
        _summoners.Clear();
    }

    // ------------------------------------------------------------------------------------------------------------
    // Scopes
    // ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The scope a loadout hook of <paramref name="binding"/> runs in. The unit slots are the hero's (a loadout script's
    /// <c>Owner</c> is the wearer), <c>InstanceVars</c> is the binding's own table, <c>AvatarVars</c> the hero's shared one,
    /// and <c>TalentLevel</c> the binding's rank: the host sets it before every hook of a talent, and the script copies it
    /// to root <c>Level</c> itself before any <c>*ByLevel</c> read (as <c>123</c> does inside its damage hook). No level is
    /// preset, so a script that never copies <c>TalentLevel</c> indexes no <c>*ByLevel</c> table.
    /// </summary>
    public BbScope NewScope(BbLoadoutBinding binding, object? owner, object? attacker = null, object? target = null)
    {
        var scope = BbScope.ForHook(owner, attacker, target, owner);
        scope.AvatarVars = AvatarVars;
        scope.InstanceVars = binding.InstanceVars;
        scope.Vars["TalentLevel"] = (double)binding.Rank;
        return scope;
    }
}
