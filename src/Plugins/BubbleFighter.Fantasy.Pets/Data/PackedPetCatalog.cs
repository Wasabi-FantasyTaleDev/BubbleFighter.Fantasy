using BubbleFighter.Fantasy.Contracts.ClientData;
using MemoryPack;

namespace BubbleFighter.Fantasy.Pets.Data;

/// <summary>One pet item as kept in the compiled client data: every field of <see cref="PetItem"/>.</summary>
[MemoryPackable]
internal readonly partial record struct PackedPetItem(int ItemId, int Function, int Parameter, int Category,
    string? DurationType, int? MaximumDuration);

/// <summary>One skill definition as kept in the compiled client data, its references as an array.</summary>
[MemoryPackable]
internal readonly partial record struct PackedPetSkill(int Selector, int Category, int Grade, int Weight, int Value,
    int[] BuffIndices);

/// <summary>One buff definition as kept in the compiled client data; the decimals keep their scale.</summary>
[MemoryPackable]
internal readonly partial record struct PackedPetBuff(int BuffIndex, string BuffName, int RandomGroup, int Minimum,
    int Maximum, decimal Conversion, decimal RawMinimum, decimal RawMaximum);

/// <summary>
/// The pet catalog in the build's compiled client data: every item, skill and buff definition in key order, then the
/// pet subcategory. Reading builds the catalog through its constructor, so the catalog's own checks run on every start.
/// </summary>
[MemoryPackable]
internal sealed partial record PackedPetCatalog(PackedPetItem[] Items, PackedPetSkill[] Skills, PackedPetBuff[] Buffs,
    int PetCategory)
{
    internal static readonly ICompiledCatalog<PetCatalog> Catalog =
        new PackedCatalog<PetCatalog, PackedPetCatalog>("pets", Pack, Unpack);

    private static PackedPetCatalog Pack(PetCatalog catalog) => new(
        catalog.Items.Values.OrderBy(item => item.ItemId).Select(item => new PackedPetItem(item.ItemId, item.Function,
            item.Parameter, item.Category, item.DurationType, item.MaximumDuration)).ToArray(),
        catalog.Skills.Values.OrderBy(skill => skill.Selector).Select(skill => new PackedPetSkill(skill.Selector,
            skill.Category, skill.Grade, skill.Weight, skill.Value, skill.BuffIndices.ToArray())).ToArray(),
        catalog.Buffs.Values.OrderBy(buff => buff.BuffIndex).Select(buff => new PackedPetBuff(buff.BuffIndex,
            buff.BuffName, buff.RandomGroup, buff.Minimum, buff.Maximum, buff.Conversion, buff.RawMinimum,
            buff.RawMaximum)).ToArray(),
        catalog.PetCategory);

    private static PetCatalog Unpack(PackedPetCatalog packed)
    {
        if (packed.Items is null || packed.Skills is null || packed.Buffs is null)
            throw new InvalidDataException("The compiled pet catalog lacks a table.");
        var items = new Dictionary<int, PetItem>(packed.Items.Length);
        foreach (var item in packed.Items)
            Add(items, item.ItemId, new PetItem(item.ItemId, item.Function, item.Parameter, item.Category)
            {
                DurationType = item.DurationType,
                MaximumDuration = item.MaximumDuration,
            });
        var skills = new Dictionary<int, PetSkillDefinition>(packed.Skills.Length);
        foreach (var skill in packed.Skills)
            Add(skills, skill.Selector, new PetSkillDefinition(skill.Selector, skill.Category, skill.Grade, skill.Weight,
                skill.Value, [.. skill.BuffIndices ?? throw new InvalidDataException("A compiled pet skill lacks its references.")]));
        var buffs = new Dictionary<int, PetBuffDefinition>(packed.Buffs.Length);
        foreach (var buff in packed.Buffs)
            Add(buffs, buff.BuffIndex, new PetBuffDefinition(buff.BuffIndex,
                buff.BuffName ?? throw new InvalidDataException("A compiled pet buff lacks its name."), buff.RandomGroup,
                buff.Minimum, buff.Maximum, buff.Conversion, buff.RawMinimum, buff.RawMaximum));
        return new PetCatalog(items, skills, buffs, packed.PetCategory);
    }

    private static void Add<T>(Dictionary<int, T> rows, int key, T row)
    {
        if (!rows.TryAdd(key, row))
            throw new InvalidDataException($"The compiled pet catalog repeats key {key}.");
    }
}
