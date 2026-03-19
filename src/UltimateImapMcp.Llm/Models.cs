namespace UltimateImapMcp.Llm;

/// <summary>Email content passed to analyzers.</summary>
public record EmailContent(string Subject, string From, string? Body, string? Snippet);

/// <summary>Types of analysis that can be performed on an email.</summary>
public enum AnalysisType
{
    SpamScore,
    Category,
    Priority,
    Summary,
    Custom
}

/// <summary>Result of an email analysis operation.</summary>
public record AnalysisResult
{
    /// <summary>The type of analysis that was performed.</summary>
    public required AnalysisType Type { get; init; }

    /// <summary>JSON result with score/label/explanation depending on type.</summary>
    public required string ResultJson { get; init; }

    /// <summary>The LLM model used for analysis, if any.</summary>
    public string? ModelUsed { get; init; }

    /// <summary>Number of input tokens consumed.</summary>
    public int? TokensInput { get; init; }

    /// <summary>Number of output tokens generated.</summary>
    public int? TokensOutput { get; init; }

    /// <summary>Estimated cost in USD.</summary>
    public decimal? CostUsd { get; init; }
}
