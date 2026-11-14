using System.ComponentModel.DataAnnotations;

namespace BubbleFighter.Fantasy.InGame;

public sealed partial class InGameOptions
{
    /// <summary>Gets or sets whether this row serves native custom options within audited existing modes.</summary>
    public bool CustomRoomRules { get; set; }

    /// <summary>Gets or sets the client-data workbook that defines available custom controls and their mode restrictions.</summary>
    [Required]
    public string CustomRoomRulesPath { get; set; } = "Config/CustomGameMode.xml";

    /// <summary>Gets or sets the maximum decoded workbook size in bytes.</summary>
    [Range(1, 33554432)]
    public int CustomRoomRulesMaxBytes { get; set; } = 8388608;

    /// <summary>Gets or sets the maximum total rows retained from the selected custom-rule worksheets.</summary>
    [Range(1, 65536)]
    public int CustomRoomRulesMaxRows { get; set; } = 4096;

    /// <summary>Gets or sets the maximum columns in each custom-rule worksheet.</summary>
    [Range(1, 1024)]
    public int CustomRoomRulesMaxColumns { get; set; } = 64;

    /// <summary>Gets or sets the maximum text length in each custom-rule cell.</summary>
    [Range(1, 65536)]
    public int CustomRoomRulesMaxCellCharacters { get; set; } = 8192;
}
