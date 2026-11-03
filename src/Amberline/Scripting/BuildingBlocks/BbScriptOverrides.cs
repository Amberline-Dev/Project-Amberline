namespace Amberline.Scripting.BuildingBlocks;

/// <summary>
/// Owner decision, 26 October 2026 ("Wiki value per spell"): where a shipped script carries a number table older than patch
/// 1.0.0.126 and the League wiki shows that the inibin and tooltip value was the live one, the wiki value replaces the script's
/// table when the script loads (<see cref="BbScriptLoader"/>), so every hook reads it. A row names the script, the block
/// parameter that holds the table, the variable the block writes (<c>DestVar</c>; null for a table with no destination, such as
/// <c>BBApplyDamage</c>'s <c>DamageByLevel</c>), the shipped values and the wiki values. A row replaces only a table that still
/// holds exactly the shipped values; a row of one shipped value replaces a literal parameter of that name holding exactly it. The audit behind the rows is batch 322 (script tables against inibin, tooltip and wiki);
/// the rows the wiki decides for the script, or whose table never runs, are listed in the batch 335 record, not here.
/// </summary>
public static class BbScriptOverrides
{
    public sealed record Row(string Script, string Table, string? DestVar, double[] Shipped, double[] Wiki, string Citation);

    public static readonly IReadOnlyList<Row> Rows =
    [
        // Reconstruction: the script's 48.75 to 198.75 (four BBApplyDamage calls, one table) is older than the live value; the inibin
        // effect 2 and the tooltip say 60/105/150/195/240, as the wiki does from V1.0.0.118 until after 1.0.0.126.
        new("NocturneDuskbringer", "DamageByLevel", null,
            [48.75, 86.25, 123.75, 161.25, 198.75], [60, 105, 150, 195, 240],
            "Nocturne/Patch_history V1.0.0.118: \"Base damage reduced to 60 / 105 / 150 / 195 / 240 from 60 / 110 / 160 / 210 / 260.\""),
        // Reconstruction: SelfExecute writes SpellCooldown 12 to 8 and OnBuffDeactivate sets the slot's cooldown from it
        // (BBSetSlotSpellCooldownTime); the inibin's Cooldown is 11/10/9/8/7, as the wiki has it from V1.0.0.123.
        new("LeonaShieldOfDaybreak", "SrcValueByLevel", "SpellCooldown",
            [12, 11, 10, 9, 8], [11, 10, 9, 8, 7],
            "Leona/Patch_history V1.0.0.123: \"Cooldown reduced to 11 / 10 / 9 / 8 / 7 seconds from 12 / 11 / 10 / 9 / 8.\""),
        // Reconstruction (batch 350): a scalar row (one shipped value). The second press detonates the missile in OnBuffDeactivate with
        // BBApplyStun Duration 0.75; the flight's end stuns for 1, as the tooltip and inibin effect 4 say. The wiki dates the fix before 1.0.0.126.
        new("FlashFrost", "Duration", null,
            [0.75], [1.0],
            "Anivia/Patch_history V1.0.0.111: \"Fixed a bug where it stunned for 0.75 seconds instead of 1 second as the tooltip stated.\""),
        // Reconstruction (batch 352): a scalar row. OnBuffDeactivate adds CassiopeiaNoxiousBlastPoison for 3.25 s, and its BuffOnUpdateActions
        // ticks at once and every second (BBExecutePeriodically, ExecuteImmediately), so 3.25 s are four ticks at 0, 1, 2 and 3 s. The tooltip
        // ("over @Effect3Amount@ seconds", the three seconds of the haste), the per-tick table (25/38.33/51.66/65/78.33, a third of the tooltip's
        // 75/115/155/195/235) and the wiki's ratio (90% AP until V1.0.0.134: three ticks of the poison's 0.3) are three ticks. The engine's cadence
        // is not the cause: MissFortuneScatterAoE divides its total by seven (one wave at the activation, six of its 0.25 s periodic in 1.75 s)
        // and comes out whole under it; a first update one quarter second after the add would give six.
        new("CassiopeiaNoxiousBlast", "Duration", null,
            [3.25], [3.0],
            "Cassiopeia/Patch_history V1.0.0.134: \"Ability power ratio reduced to (+ 80% AP) from (+ 90% AP).\" (three ticks of 0.3)"),
    ];

    /// <summary>The blocks of script <paramref name="script"/> with its rows applied (the same list when no row matches).</summary>
    public static IReadOnlyList<BbBlock> Apply(string script, IReadOnlyList<BbBlock> blocks)
    {
        var rows = Rows.Where(row => string.Equals(row.Script, script, StringComparison.OrdinalIgnoreCase)).ToArray();
        return rows.Length == 0 ? blocks : Apply(blocks, rows);
    }

    private static IReadOnlyList<BbBlock> Apply(IReadOnlyList<BbBlock> blocks, Row[] rows)
    {
        BbBlock[]? copy = null;
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = Apply(blocks[i], rows);
            if (ReferenceEquals(block, blocks[i])) continue;
            copy ??= blocks.ToArray();
            copy[i] = block;
        }
        return copy ?? blocks;
    }

    private static BbBlock Apply(BbBlock block, Row[] rows)
    {
        var subBlocks = Apply(block.SubBlocks, rows);
        BbField[]? parameters = null;
        foreach (var row in rows)
        {
            if (row.DestVar is not null && block.Text("DestVar") != row.DestVar) continue;
            for (var i = 0; i < block.Params.Count; i++)
                if (block.Params[i] is { Key: var key, Value: BbValue.Table table } && key == row.Table && Holds(table, row.Shipped))
                {
                    parameters ??= block.Params.ToArray();
                    parameters[i] = new BbField(key, new BbValue.Table(row.Wiki.Select(v => (BbValue)new BbValue.Number(v)).ToArray(), table.Fields));
                }
                else if (block.Params[i] is { Key: var literalKey, Value: BbValue.Number number } && literalKey == row.Table &&
                         row.Shipped.Length == 1 && number.Value == row.Shipped[0])
                {
                    parameters ??= block.Params.ToArray();
                    parameters[i] = new BbField(literalKey, new BbValue.Number(row.Wiki[0]));
                }
        }
        return parameters is null && ReferenceEquals(subBlocks, block.SubBlocks)
            ? block
            : block with { Params = parameters ?? block.Params, SubBlocks = subBlocks };
    }

    private static bool Holds(BbValue.Table table, double[] values) =>
        table.Items.Count == values.Length &&
        table.Items.Select((item, i) => item is BbValue.Number number && number.Value == values[i]).All(match => match);
}
