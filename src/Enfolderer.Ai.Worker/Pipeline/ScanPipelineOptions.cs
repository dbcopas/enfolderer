using Enfolderer.Ai.Contracts;

namespace Enfolderer.Ai.Worker.Pipeline;

/// <summary>Worker-side configuration for the two Foundry projects and the crop step.</summary>
public sealed class ScanPipelineOptions
{
    public const string SectionName = "ScanPipeline";

    /// <summary>
    /// Team A's Foundry project endpoint (project <c>cardgeo</c>), e.g.
    /// <c>https://cardgeo.services.ai.azure.com/api/projects/cardgeo</c>.
    /// Leave empty to run the offline stub boundary agent.
    /// </summary>
    public string? GeometryProjectEndpoint { get; set; }

    /// <summary>Agent id/name of Team A's boundary agent within the geometry project.</summary>
    public string BoundaryAgentId { get; set; } = "CardBoundaryAgent";

    /// <summary>
    /// Team B's Foundry project endpoint (project <c>cardid</c>). Leave empty to run the offline
    /// stub identification agents.
    /// </summary>
    public string? IdentificationProjectEndpoint { get; set; }

    /// <summary>
    /// Agent id per game, e.g. <c>{"mtg": "MtgCardIdAgent", "pokemon": "PokemonCardIdAgent"}</c>.
    /// A game with no entry here has no deployed agent and is reported per card in the result.
    /// </summary>
    public Dictionary<string, string> IdentificationAgentIds { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        [CardGames.Magic] = "MtgCardIdAgent",
        [CardGames.Pokemon] = "PokemonCardIdAgent"
    };

    /// <summary>Data-plane API version used for the Foundry Agents endpoints.</summary>
    public string FoundryApiVersion { get; set; } = "v1";

    /// <summary>Game assumed when neither the caller nor the boundary agent offers a usable hint.</summary>
    public string DefaultGame { get; set; } = CardGames.Magic;

    /// <summary>Lifetime of the read SAS URLs handed to agents.</summary>
    public TimeSpan ReadUrlLifetime { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Height in pixels of each perspective-corrected card crop.</summary>
    public int CropHeight { get; set; } = 1024;

    /// <summary>How long a single agent run may take before the job fails.</summary>
    public TimeSpan AgentRunTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Interval between Foundry run status polls.</summary>
    public TimeSpan AgentPollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Interval between queue polls.</summary>
    public TimeSpan QueuePollInterval { get; set; } = TimeSpan.FromSeconds(2);

    public bool UsesFoundryGeometry => !string.IsNullOrWhiteSpace(GeometryProjectEndpoint);

    public bool UsesFoundryIdentification => !string.IsNullOrWhiteSpace(IdentificationProjectEndpoint);
}
