using BubbleFighter.Fantasy.Abstractions.Protocol;

namespace BubbleFighter.Fantasy.Contracts.Party;

/// <summary>Identifies a laboratory operation independently of its build's request and reply opcodes.</summary>
public enum LabOperation
{
    /// <summary>Combine registered materials into an item.</summary>
    Compound = 0,
    /// <summary>Combine materials for an unlimited-duration item.</summary>
    CompoundUnlimited = 1,
    /// <summary>Enchant the selected item.</summary>
    Enchant = 2,
    /// <summary>Combine registered pet materials.</summary>
    PetCompound = 3,
    /// <summary>Upgrade a selected item using registered materials.</summary>
    MakeItemAdvanced = 4,
    /// <summary>Change the selected item to unlimited duration.</summary>
    ChangeUnlimited = 5,
    /// <summary>Apply the selected modification parts.</summary>
    ModificationApply = 6,
    /// <summary>Remove a modification after the client's confirmation dialog.</summary>
    ModificationDelete = 7,
}

/// <summary>The laboratory request body: counted inventory keys and one operation-specific u64.</summary>
/// <param name="Operation">The operation identified by the dialect's routed opcode.</param>
/// <param name="Keys">Inventory or card keys in the order supplied by the client.</param>
/// <param name="Argument">The opaque operation argument, preserving all 64 wire bits.</param>
public sealed record LabOperationRequested(LabOperation Operation, IReadOnlyList<long> Keys, long Argument) : IMessage;

/// <summary>A rejected laboratory operation; successful inventory mutations are not implemented.</summary>
/// <param name="Operation">The operation whose reply opcode to use.</param>
/// <param name="Result">The build's audited closed-laboratory result code.</param>
public sealed record LabOperationAnswered(LabOperation Operation, int Result) : IMessage;
