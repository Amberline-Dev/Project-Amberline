namespace Amberline.Core;

/// <summary>
/// Spawn-time checks kept per unit class. Diagnostics never escape this check
/// method; WorldState decides whether to throw or report live violations.
/// </summary>
public static class EntityInvariants
{
    public static IReadOnlyList<string> Check(Entity? entity)
    {
        var violations = new List<string>();
        try
        {
            if (entity is null)
            {
                violations.Add("entity is null");
                return violations;
            }

            Finite(violations, "position.x", entity.Position.X);
            Finite(violations, "position.z", entity.Position.Z);
            Finite(violations, "hp", entity.Health);
            Finite(violations, "max_hp", entity.MaxHealth);
            Finite(violations, "mana", entity.Mana);
            Finite(violations, "max_mana", entity.MaxMana);

            if (entity.Health > entity.MaxHealth)
            {
                violations.Add($"hp ({entity.Health.ToString()}) exceeds max_hp ({entity.MaxHealth.ToString()})");
            }

            if (entity.Mana > entity.MaxMana)
            {
                violations.Add($"mana ({entity.Mana.ToString()}) exceeds max_mana ({entity.MaxMana.ToString()})");
            }

            if (entity.Alive && entity.Health <= 0.0)
            {
                violations.Add($"hp ({entity.Health.ToString()}) must be positive for a unit registered alive=True");
            }

            switch (entity.UnitType)
            {
                case UnitType.Hero:
                    Positive(violations, entity, "armor", entity.Armor);
                    Positive(violations, entity, "spell_block", entity.SpellBlock);
                    Positive(violations, entity, "speed", entity.Speed);
                    Positive(violations, entity, "atk_range", entity.AttackRange);
                    Positive(violations, entity, "max_hp", entity.MaxHealth);
                    // A hero's pool may be empty: Garen, Katarina and Riven ship BaseMP 0 and MPPerLevel 0 (PARType "Shield"), which the client
                    // reads as 0/0 and shows with the shield bar style. Only a negative or non-finite maximum is wrong here.
                    if (entity.MaxMana < 0.0) violations.Add($"max_mana must not be negative for {entity.UnitType}, got {entity.MaxMana.ToString()}");
                    break;
                case UnitType.Minion:
                    Positive(violations, entity, "speed", entity.Speed);
                    Positive(violations, entity, "atk_range", entity.AttackRange);
                    Positive(violations, entity, "max_hp", entity.MaxHealth);
                    break;
                case UnitType.Turret:
                    Positive(violations, entity, "armor", entity.Armor);
                    Positive(violations, entity, "atk_range", entity.AttackRange);
                    Positive(violations, entity, "max_hp", entity.MaxHealth);
                    Zero(violations, entity, "speed", entity.Speed);
                    break;
                case UnitType.Monster:
                    if (!(entity.Kind == EntityKind.ShacoBox && entity.Name == "ShacoBox" && entity.Speed == 0.0) &&
                        !(entity.Kind == EntityKind.Neutral && entity.Name == "Worm" && entity.Speed == 0.0))
                    {
                        Positive(violations, entity, "speed", entity.Speed);
                    }

                    Positive(violations, entity, "atk_range", entity.AttackRange);
                    Positive(violations, entity, "max_hp", entity.MaxHealth);
                    break;
                case UnitType.Structure:
                    Positive(violations, entity, "max_hp", entity.MaxHealth);
                    Zero(violations, entity, "speed", entity.Speed);
                    break;
            }
        }
        catch (Exception exception)
        {
            violations.Add($"check_unit itself raised {exception.GetType().Name}: {exception.Message}");
        }

        return violations;
    }

    public static void Enforce(
        Entity entity,
        bool strict,
        Action<Entity, IReadOnlyList<string>>? reporter = null)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var violations = Check(entity);
        if (violations.Count == 0)
        {
            return;
        }

        if (strict)
        {
            throw new InvalidOperationException(
                $"Invariant violation for net ID 0x{entity.NetId:X8}: {string.Join("; ", violations)}");
        }

        reporter?.Invoke(entity, violations);
    }

    private static void Positive(List<string> violations, Entity entity, string name, double value)
    {
        if ((entity.IsWard || entity.Kind == EntityKind.NidaleeTrap) &&
            (name == "speed" || name == "atk_range") && value == 0.0)
        {
            return;
        }

        if (!double.IsFinite(value) || value <= 0.0)
        {
            violations.Add($"{name} must be positive for {entity.UnitType}, got {value.ToString()}");
        }
    }

    private static void Zero(List<string> violations, Entity entity, string name, double value)
    {
        if (!double.IsFinite(value) || value != 0.0)
        {
            violations.Add($"{name} must be exactly 0 for {entity.UnitType}, got {value.ToString()}");
        }
    }

    private static void Finite(List<string> violations, string name, double value)
    {
        if (!double.IsFinite(value))
        {
            violations.Add($"{name} must be finite, got {value.ToString()}");
        }
    }
}
