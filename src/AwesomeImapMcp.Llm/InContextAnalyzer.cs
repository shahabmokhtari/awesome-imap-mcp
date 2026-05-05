using System.Text.Json;

namespace AwesomeImapMcp.Llm;

/// <summary>
/// Returns the email data formatted for the calling LLM to analyze in-context.
/// Does not make any external API calls — the MCP host LLM performs the analysis.
/// </summary>
public class InContextAnalyzer : IEmailAnalyzer
{
    public bool SupportsBackgroundAnalysis => false;

    public Task<AnalysisResult> AnalyzeAsync(EmailContent email, AnalysisType type, CancellationToken ct = default)
    {
        var prompt = BuildPrompt(email, type);

        var result = new AnalysisResult
        {
            Type = type,
            ResultJson = JsonSerializer.Serialize(new
            {
                mode = "in_context",
                instruction = prompt,
                email = new
                {
                    subject = email.Subject,
                    from = email.From,
                    body = email.Body ?? email.Snippet
                }
            }),
            ModelUsed = "in_context"
        };

        return Task.FromResult(result);
    }

    public static string BuildPrompt(EmailContent email, AnalysisType type) => type switch
    {
        AnalysisType.SpamScore =>
            "Analyze this email and return a JSON object with: " +
            "{ \"score\": <0-100 spam score>, \"label\": \"spam\"|\"likely_spam\"|\"not_spam\", " +
            "\"explanation\": \"<brief reason>\" }",

        AnalysisType.Category =>
            "Categorize this email and return a JSON object with: " +
            "{ \"category\": \"newsletter\"|\"transactional\"|\"personal\"|\"work\"|\"spam\"|\"social\"|\"promotions\"|\"updates\", " +
            "\"confidence\": <0.0-1.0>, \"explanation\": \"<brief reason>\" }",

        AnalysisType.Priority =>
            "Assess the priority of this email and return a JSON object with: " +
            "{ \"priority\": \"low\"|\"normal\"|\"high\"|\"urgent\", " +
            "\"explanation\": \"<brief reason>\" }",

        AnalysisType.Summary =>
            "Summarize this email in one paragraph and return a JSON object with: " +
            "{ \"summary\": \"<one paragraph summary>\", \"key_points\": [\"<point1>\", \"<point2>\"] }",

        AnalysisType.Custom =>
            "Analyze this email according to the user's custom criteria and return a JSON result.",

        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown analysis type")
    };
}
