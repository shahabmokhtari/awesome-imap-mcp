using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace UltimateImapMcp.Dashboard;

public static class ToolsApi
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Cached tool metadata, built once on first request via assembly scanning.
    /// </summary>
    private static List<ToolMetadata>? _cachedTools;
    private static readonly object CacheLock = new();

    public static IEndpointRouteBuilder MapToolsApi(this IEndpointRouteBuilder app)
    {
        // GET /api/tools — returns metadata for all MCP tools
        app.MapGet("/api/tools", () =>
        {
            var tools = GetToolMetadata();
            return Results.Ok(tools);
        });

        // POST /api/tools/{name}/execute — execute a tool by name
        app.MapPost("/api/tools/{name}/execute", async (
            string name, HttpContext ctx, IServiceProvider sp) =>
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
            catch
            {
                // Body is optional for parameterless tools
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
                catch
                {
                    return Results.Ok(new { result });
                }
            }
            catch (Exception ex)
            {
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
    private static List<ToolMetadata> GetToolMetadata()
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
                catch
                {
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

                        // Convert method name to snake_case for the tool name
                        var toolName = ToSnakeCase(method.Name);

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
                            Parameters: parameters));
                    }
                }
            }

            _cachedTools = tools.OrderBy(t => t.Name).ToList();
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
        // Resolve constructor dependencies from DI to create the tool instance
        var ctors = tool.ToolType.GetConstructors();
        if (ctors.Length == 0)
            throw new InvalidOperationException($"Tool class '{tool.ClassName}' has no public constructor.");

        var ctor = ctors[0];
        var ctorParams = ctor.GetParameters()
            .Select(p => sp.GetService(p.ParameterType)
                ?? throw new InvalidOperationException(
                    $"Cannot resolve constructor parameter '{p.Name}' of type '{p.ParameterType.Name}'."))
            .ToArray();

        var instance = ctor.Invoke(ctorParams);
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
                    args[i] = DeserializeParam(jsonValue, param.ParameterType);
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
    List<ToolParameterMetadata> Parameters);

internal record ToolParameterMetadata(
    string Name,
    string Type,
    string Description,
    bool Required,
    object? DefaultValue);
