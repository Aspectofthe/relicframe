namespace RelicFrame.Core;

/// <summary>Discord application emojis owned by the RelicFrame bot.</summary>
public static class ApplicationEmojis
{
    public const string IconNarmerWhite = "<:IconNarmerxWhite:1545285921237114962>";
    public const string SortieNode = "<:SortieNode:1545288017063845928>";
    public const string BossNode = "<:BossNode:1545288015939895376>";
    public const string ArchonHuntNode = "<:ArchonHuntNode:1545288014689869884>";
    public const string ArbitrationNode = "<:ArbitrationNode:1545288013800546314>";
    public const string AlertNode = "<:AlertNode:1545288012651565056>";
    public const string VoidFissureNode = "<:VoidFissureNode:1545288018347294750>";
    public const string VoidTearBlack = "<:VoidTearIconxBlack:1545285929684701194>";
    public const string SurvivalWhite = "<:SurvivalWhite:1545285928010915891>";
    public const string SortieBlack = "<:SortiexBlack:1545285927298007040>";
    public const string VitusEssence = "<:VitusEssence:1545285926207492187>";
    public const string ThraxPlasm = "<:ThraxPlasm:1545285925246869584>";
    public const string OrokinDucats = "<:OrokinDucats:1545285923875586138>";

    private static readonly IReadOnlyDictionary<(string Era, Refinement Tier), string> Relics =
        new Dictionary<(string, Refinement), string>
        {
            [("lith", Refinement.Intact)] = "<:LithRelicIntact:1545285922617172018>",
            [("lith", Refinement.Exceptional)] = "<:LithRelicExceptional:1545285908272783411>",
            [("lith", Refinement.Flawless)] = "<:LithRelicFlawless:1545285909212307516>",
            [("lith", Refinement.Radiant)] = "<:LithRelicRadiant:1545285910445301821>",
            [("meso", Refinement.Intact)] = "<:MesoRelicIntact:1545285913574375525>",
            [("meso", Refinement.Exceptional)] = "<:MesoRelicExceptional:1545285911523364915>",
            [("meso", Refinement.Flawless)] = "<:MesoRelicFlawless:1545285912626335784>",
            [("meso", Refinement.Radiant)] = "<:MesoRelicRadiant:1545285914807504956>",
            [("neo", Refinement.Intact)] = "<:NeoRelicIntact:1545285917928067142>",
            [("neo", Refinement.Exceptional)] = "<:NeoRelicExceptional:1545285915855949834>",
            [("neo", Refinement.Flawless)] = "<:NeoRelicFlawless:1545285916858392597>",
            [("neo", Refinement.Radiant)] = "<:NeoRelicRadiant:1545285919077171251>",
            [("axi", Refinement.Intact)] = "<:AxiRelicIntact:1545285906385076224>",
            [("axi", Refinement.Exceptional)] = "<:AxiRelicExceptional:1545285903680012308>",
            [("axi", Refinement.Flawless)] = "<:AxiRelicFlawless:1545285904954949652>",
            [("axi", Refinement.Radiant)] = "<:AxiRelicRadiant:1545285907425263636>"
        };

    public static string Relic(string relicName, Refinement refinement)
    {
        var era = relicName.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant() ?? "";
        return Relics.GetValueOrDefault((era, refinement), VoidFissureNode);
    }

    public static string Relic(string relicName, string refinement) =>
        Enum.TryParse<Refinement>(refinement, true, out var tier) ? Relic(relicName, tier) : VoidFissureNode;

    public static string Mission(string missionType) =>
        missionType.Equals("Survival", StringComparison.OrdinalIgnoreCase) ? SurvivalWhite : "";
}
