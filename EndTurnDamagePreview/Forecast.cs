using MegaCrit.Sts2.Core.Entities.Creatures;

namespace EndTurnDamagePreview;

internal readonly record struct Forecast(int HpLost, int HpRemaining, int BlockLost, int BlockRemaining, bool Unknown, string Status)
{
    internal static Forecast Pending(string status) => new(0, 0, 0, 0, true, status);
    internal static Forecast FromResult(SimulationResult result, Creature creature)
    {
        if (!result.Verified) return Pending(result.Status);
        if (creature.IsPlayer) return Create(creature, result.Hp, result.Block, result.Status);
        if (!creature.IsPet || creature.PetOwner?.PlayerCombatState == null) return Pending("Unsupported creature");

        var pets = creature.PetOwner.PlayerCombatState.Pets;
        int index = -1;
        for (int i = 0; i < pets.Count; i++)
            if (ReferenceEquals(pets[i], creature)) { index = i; break; }
        if (index < 0) return Pending("Pet unavailable");

        string modelId = creature.Monster?.Id.ToString() ?? "";
        var pet = result.Pets.FirstOrDefault(p => p.Index == index && p.ModelId == modelId);
        return pet == null ? Pending("Pet forecast unavailable") : Create(creature, pet.Hp, pet.Block, result.Status);
    }

    private static Forecast Create(Creature creature, int hp, int block, string status) =>
        new(creature.CurrentHp - hp, hp, creature.Block - block, block, false, status);
}
