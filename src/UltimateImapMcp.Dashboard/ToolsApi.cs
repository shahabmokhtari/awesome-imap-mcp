using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace UltimateImapMcp.Dashboard;

public static class ToolsApi
{
    /// <summary>
    /// Cached tool metadata, built once on first request via assembly scanning.
    /// </summary>
    private static volatile IReadOnlyList<ToolMetadata>? _cachedTools;
    private static readonly object CacheLock = new();

    public static IEndpointRouteBuilder MapToolsApi(this IEndpointRouteBuilder app)
    {
        // GET /api/tools — returns metadata for all MCP tools
        app.MapGet("/api/tools", () =>
        {
            var tools = GetToolMetadata();
            return Results.Ok(tools);
        });

        // GET /api/tools/suggestions — returns autocomplete options for tool parameters
        // keyed by parameter name pattern (e.g. "accountId" -> [{value, label}])
        app.MapGet("/api/tools/suggestions", (
            UltimateImapMcp.ImapClient.Repositories.AccountRepository accountRepo,
            UltimateImapMcp.ImapClient.Repositories.FolderRepository folderRepo,
            UltimateImapMcp.ImapClient.Repositories.MessageRepository messageRepo) =>
        {
            var suggestions = new Dictionary<string, List<object>>();

            // Account IDs
            var accounts = accountRepo.GetAll();
            suggestions["accountId"] = accounts.Select(a => (object)new
            {
                value = a.Id,
                label = $"{a.Name} ({a.Provider})"
            }).ToList();

            // Folder paths and IDs per account (single query per account)
            var folderPaths = new List<object>();
            var folderIdSuggestions = new List<object>();
            foreach (var account in accounts)
            {
                var folders = folderRepo.GetByAccount(account.Id);
                foreach (var f in folders)
                {
                    folderPaths.Add(new
                    {
                        value = f.Path,
                        label = $"{f.DisplayName ?? f.Path} — {account.Name}",
                        accountId = account.Id
                    });
                    folderIdSuggestions.Add(new
                    {
                        value = f.Id,
                        label = $"{f.DisplayName ?? f.Path} (ID: {f.Id}) — {account.Name}",
                        accountId = account.Id
                    });
                }
            }
            suggestions["folderPath"] = folderPaths;
            suggestions["folderId"] = folderIdSuggestions;

            // Recent message UIDs per account (last 20 per account)
            var messageUids = new List<object>();
            foreach (var account in accounts)
            {
                var folders = folderRepo.GetByAccount(account.Id);
                var inbox = folders.FirstOrDefault(f =>
                    string.Equals(f.Role, "inbox", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(f.Path, "INBOX", StringComparison.OrdinalIgnoreCase));
                if (inbox is null) continue;
                var messages = messageRepo.GetByFolder(account.Id, inbox.Id, 20);
                foreach (var m in messages)
                {
                    messageUids.Add(new
                    {
                        value = m.Uid,
                        label = $"[{m.Uid}] {m.Subject ?? "(no subject)"} — {account.Name}",
                        accountId = account.Id
                    });
                }
            }
            suggestions["uid"] = messageUids;

            return Results.Ok(suggestions);
        });

        // POST /api/tools/{name}/execute — execute a tool by name
        app.MapPost("/api/tools/{name}/execute", async (
            string name, HttpContext ctx, IServiceProvider sp, ILogger<DashboardHost> logger) =>
        {
            var tools = GetToolMetadata();
            var tool = tools.FirstOrDefault(t =>
                string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

            if (tool is null)
                return Results.NotFound(new { error = $"Tool '{name}' not found." });

            // Parse the JSON body for parameter values
            Dictionary<string, JsonElement>? body = null;
            try
            {
                body = await ctx.Request.ReadFromJsonAsync<Dictionary<string, JsonElement>>()
                    .ConfigureAwait(false);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { error = "Invalid JSON in request body." });
            }

            body ??= new Dictionary<string, JsonElement>();

            try
            {
                var result = await InvokeToolAsync(tool, body, sp).ConfigureAwait(false);
                // The tool methods return JSON strings — parse and return as proper JSON
                try
                {
                    var parsed = JsonSerializer.Deserialize<JsonElement>(result);
                    return Results.Ok(parsed);
                }
                catch (JsonException)
                {
                    return Results.Ok(new { result });
                }
            }
            catch (ArgumentException ex)
            {
                logger.LogWarning(ex, "Tool '{Tool}' parameter error", name);
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Tool '{Tool}' execution failed", name);
                var message = ex.InnerException?.Message ?? ex.Message;
                return Results.Json(
                    new { error = message },
                    statusCode: 500,
                    contentType: "application/json");
            }
        });

        return app;
    }

    /// <summary>
    /// Scans loaded assemblies for MCP tool classes and methods, building metadata.
    /// Uses attribute names rather than direct type references since the Dashboard
    /// project does not reference the ModelContextProtocol package.
    /// </summary>
    private static IReadOnlyList<ToolMetadata> GetToolMetadata()
    {
        if (_cachedTools is not null)
            return _cachedTools;

        lock (CacheLock)
        {
            if (_cachedTools is not null)
                return _cachedTools;

            var tools = new List<ToolMetadata>();

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    // Recover partial types from assemblies with unresolvable dependencies
                    types = ex.Types.Where(t => t is not null).ToArray()!;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ToolsApi] Failed to load types from {assembly.GetName().Name}: {ex.Message}");
                    continue;
                }

                foreach (var type in types)
                {
                    // Look for [McpServerToolType] attribute by name
                    if (!type.GetCustomAttributes().Any(a =>
                        a.GetType().Name == "McpServerToolTypeAttribute"))
                        continue;

                    foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (!method.GetCustomAttributes().Any(a =>
                            a.GetType().Name == "McpServerToolAttribute"))
                            continue;

                        var desc = method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "";

                        // Use explicit Name from McpServerToolAttribute if set, otherwise derive from method name
                        var mcpToolAttr = method.GetCustomAttributes()
                            .First(a => a.GetType().Name == "McpServerToolAttribute");
                        var nameProp = mcpToolAttr.GetType().GetProperty("Name");
                        var explicitName = nameProp?.GetValue(mcpToolAttr) as string;
                        var toolName = !string.IsNullOrEmpty(explicitName) ? explicitName : ToSnakeCase(method.Name);

                        var parameters = new List<ToolParameterMetadata>();
                        foreach (var param in method.GetParameters())
                        {
                            var paramDesc = param.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "";
                            parameters.Add(new ToolParameterMetadata(
                                Name: param.Name ?? "",
                                Type: MapClrType(param.ParameterType),
                                Description: paramDesc,
                                Required: !param.HasDefaultValue,
                                DefaultValue: param.HasDefaultValue ? param.DefaultValue : null));
                        }

                        tools.Add(new ToolMetadata(
                            Name: toolName,
                            Description: desc,
                            ClassName: type.Name,
                            MethodName: method.Name,
                            ToolType: type,
                            Method: method,
                            Parameters: parameters.AsReadOnly()));
                    }
                }
            }

            _cachedTools = tools.OrderBy(t => t.Name).ToList().AsReadOnly();
            return _cachedTools;
        }
    }

    /// <summary>
    /// Invokes a tool method by constructing the tool class via DI and calling the method.
    /// Handles both sync and async (Task&lt;string&gt;) tool methods.
    /// </summary>
    private static async Task<string> InvokeToolAsync(
        ToolMetadata tool, Dictionary<string, JsonElement> body, IServiceProvider sp)
    {
        // Resolve constructor dependencies from DI using ActivatorUtilities
        var instance = ActivatorUtilities.CreateInstance(sp, tool.ToolType);
        try
        {
            // Build method arguments from the request body
            var methodParams = tool.Method.GetParameters();
            var args = new object?[methodParams.Length];

            for (var i = 0; i < methodParams.Length; i++)
            {
                var param = methodParams[i];
                var paramName = param.Name!;

                if (body.TryGetValue(paramName, out var jsonValue) &&
                    jsonValue.ValueKind != JsonValueKind.Null &&
                    jsonValue.ValueKind != JsonValueKind.Undefined)
                {
                    try
                    {
                        args[i] = DeserializeParam(jsonValue, param.ParameterType);
                    }
                    catch (JsonException ex)
                    {
                        throw new ArgumentException(
                            $"Parameter '{paramName}' expected type '{param.ParameterType.Name}' but got: {jsonValue}", ex);
                    }
                }
                else if (param.HasDefaultValue)
                {
                    args[i] = param.DefaultValue;
                }
                else
                {
                    throw new ArgumentException($"Required parameter '{paramName}' is missing.");
                }
            }

            var result = tool.Method.Invoke(instance, args);

            // Handle async methods (Task<T>)
            if (result is Task task)
            {
                await task.ConfigureAwait(false);
                var resultProp = task.GetType().GetProperty("Result");
                return resultProp?.GetValue(task)?.ToString() ?? "null";
            }

            return result?.ToString() ?? "null";
        }
        finally
        {
            if (instance is IAsyncDisposable asyncDisposable)
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            else
                (instance as IDisposable)?.Dispose();
        }
    }

    private static object? DeserializeParam(JsonElement element, Type targetType)
    {
        return JsonSerializer.Deserialize(element.GetRawText(), targetType);
    }

    private static string MapClrType(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        if (underlying == typeof(string)) return "string";
        if (underlying == typeof(int) || underlying == typeof(long)) return "integer";
        if (underlying == typeof(float) || underlying == typeof(double) || underlying == typeof(decimal)) return "number";
        if (underlying == typeof(bool)) return "boolean";
        return underlying.Name.ToLowerInvariant();
    }

    private static string ToSnakeCase(string name)
    {
        // PascalCase -> snake_case: "ListEmails" -> "list_emails", "GetHTTPStatus" -> "get_http_status"
        var chars = new List<char>();
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0)
                {
                    var prevIsUpper = char.IsUpper(name[i - 1]);
                    var nextIsLower = i + 1 < name.Length && char.IsLower(name[i + 1]);
                    // Insert underscore before: a new word (prev is lowercase), or
                    // the last letter of an acronym followed by a lowercase letter
                    if (!prevIsUpper || nextIsLower)
                        chars.Add('_');
                }
                chars.Add(char.ToLowerInvariant(c));
            }
            else
            {
                chars.Add(c);
            }
        }
        return new string(chars.ToArray());
    }
}

internal record ToolMetadata(
    string Name,
    string Description,
    string ClassName,
    string MethodName,
    [property: System.Text.Json.Serialization.JsonIgnore] Type ToolType,
    [property: System.Text.Json.Serialization.JsonIgnore] MethodInfo Method,
    IReadOnlyList<ToolParameterMetadata> Parameters);

internal record ToolParameterMetadata(
    string Name,
    string Type,
    string Description,
    bool Required,
    object? DefaultValue);
