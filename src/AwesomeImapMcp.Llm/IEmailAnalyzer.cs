namespace AwesomeImapMcp.Llm;

/// <summary>
/// Abstraction for email analysis. Implementations may call external APIs,
/// spawn ACP agents, or return data for in-context analysis by the calling LLM.
/// </summary>
public interface IEmailAnalyzer
{
    /// <summary>
    /// Analyze an email for the given analysis type.
    /// </summary>
    Task<AnalysisResult> AnalyzeAsync(EmailContent email, AnalysisType type, CancellationToken ct = default);

    /// <summary>
    /// Whether this analyzer can run in the background (API/ACP) or requires the
    /// calling LLM to perform the analysis in-context.
    /// </summary>
    bool SupportsBackgroundAnalysis { get; }
}
