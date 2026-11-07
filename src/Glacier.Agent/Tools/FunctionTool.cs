using System;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;

namespace Glacier.Agent.Tools;

public class FunctionTool<TArgs, TResult> : ITool
{
    private readonly Func<TArgs, Task<TResult>> _executeAsync;
    private readonly JsonTypeInfo<TArgs> _argsTypeInfo;
    private readonly JsonTypeInfo<TResult> _resultTypeInfo;
    private readonly JsonNode _parametersSchema;

    private static readonly JsonSchemaExporterOptions SchemaExporterOptions = new()
    {
        TreatNullObliviousAsNonNullable = true,
        TransformSchemaNode = TransformSchemaNode
    };

    public string Name { get; }
    public string Description { get; }

    /// <summary>
    /// Native AOT-compatible constructor using source-generated <see cref="JsonTypeInfo{T}"/> metadata.
    /// </summary>
    public FunctionTool(
        string name,
        string description,
        Func<TArgs, TResult> execute,
        JsonTypeInfo<TArgs> argsTypeInfo,
        JsonTypeInfo<TResult> resultTypeInfo,
        JsonNode? parametersSchema = null)
        : this(
            name,
            description,
            args => Task.FromResult(execute(args)),
            argsTypeInfo,
            resultTypeInfo,
            parametersSchema)
    {
        ArgumentNullException.ThrowIfNull(execute);
    }

    /// <summary>
    /// Native AOT-compatible asynchronous constructor using source-generated <see cref="JsonTypeInfo{T}"/> metadata.
    /// </summary>
    public FunctionTool(
        string name,
        string description,
        Func<TArgs, Task<TResult>> executeAsync,
        JsonTypeInfo<TArgs> argsTypeInfo,
        JsonTypeInfo<TResult> resultTypeInfo,
        JsonNode? parametersSchema = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(executeAsync);
        ArgumentNullException.ThrowIfNull(argsTypeInfo);
        ArgumentNullException.ThrowIfNull(resultTypeInfo);

        Name = name;
        Description = description;
        _executeAsync = executeAsync;
        _argsTypeInfo = argsTypeInfo;
        _resultTypeInfo = resultTypeInfo;
        _parametersSchema = parametersSchema?.DeepClone() ?? BuildSchema(argsTypeInfo);
    }

    /// <summary>
    /// Reflection-backed convenience constructor for JIT environments.
    /// For Native AOT deployments, prefer the overload accepting <see cref="JsonTypeInfo{TArgs}"/> and <see cref="JsonTypeInfo{TResult}"/>.
    /// </summary>
    [RequiresUnreferencedCode("Reflection-based JSON serialization and schema generation require unreferenced code. Use the overload accepting JsonTypeInfo<TArgs> and JsonTypeInfo<TResult> for Native AOT.")]
    [RequiresDynamicCode("Reflection-based JSON serialization and schema generation require dynamic code. Use the overload accepting JsonTypeInfo<TArgs> and JsonTypeInfo<TResult> for Native AOT.")]
    public FunctionTool(string name, string description, Func<TArgs, TResult> execute)
        : this(
            name,
            description,
            args => Task.FromResult(execute(args)),
            (JsonTypeInfo<TArgs>)CreateReflectionOptions().GetTypeInfo(typeof(TArgs)),
            (JsonTypeInfo<TResult>)CreateReflectionOptions().GetTypeInfo(typeof(TResult)))
    {
        ArgumentNullException.ThrowIfNull(execute);
    }

    /// <summary>
    /// Reflection-backed asynchronous convenience constructor for JIT environments.
    /// For Native AOT deployments, prefer the overload accepting <see cref="JsonTypeInfo{TArgs}"/> and <see cref="JsonTypeInfo{TResult}"/>.
    /// </summary>
    [RequiresUnreferencedCode("Reflection-based JSON serialization and schema generation require unreferenced code. Use the overload accepting JsonTypeInfo<TArgs> and JsonTypeInfo<TResult> for Native AOT.")]
    [RequiresDynamicCode("Reflection-based JSON serialization and schema generation require dynamic code. Use the overload accepting JsonTypeInfo<TArgs> and JsonTypeInfo<TResult> for Native AOT.")]
    public FunctionTool(string name, string description, Func<TArgs, Task<TResult>> executeAsync)
        : this(
            name,
            description,
            executeAsync,
            (JsonTypeInfo<TArgs>)CreateReflectionOptions().GetTypeInfo(typeof(TArgs)),
            (JsonTypeInfo<TResult>)CreateReflectionOptions().GetTypeInfo(typeof(TResult)))
    {
    }

    public JsonNode GetParametersSchema() => _parametersSchema.DeepClone();

    public async Task<string> ExecuteAsync(string arguments)
    {
        var payload = string.IsNullOrWhiteSpace(arguments) ? "{}" : arguments;
        var args = JsonSerializer.Deserialize(payload, _argsTypeInfo);
        if (args is null)
        {
            throw new ArgumentException("Invalid arguments", nameof(arguments));
        }

        var result = await _executeAsync(args).ConfigureAwait(false);
        return JsonSerializer.Serialize(result, _resultTypeInfo);
    }

    private static JsonNode BuildSchema(JsonTypeInfo<TArgs> argsTypeInfo)
    {
        var node = argsTypeInfo.GetJsonSchemaAsNode(SchemaExporterOptions);
        if (node is JsonObject obj)
        {
            obj["type"] = "object";
            obj["properties"] ??= new JsonObject();
            return obj;
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject()
        };
    }

    private static JsonNode TransformSchemaNode(JsonSchemaExporterContext context, JsonNode schema)
    {
        if (schema is not JsonObject schemaObj)
        {
            return schema;
        }

        var descriptionAttr = context.PropertyInfo?.AttributeProvider?
            .GetCustomAttributes(typeof(DescriptionAttribute), inherit: true)
            .OfType<DescriptionAttribute>()
            .FirstOrDefault();

        if (descriptionAttr is not null && !string.IsNullOrWhiteSpace(descriptionAttr.Description))
        {
            schemaObj["description"] = descriptionAttr.Description;
        }

        if (context.Path.IsEmpty)
        {
            schemaObj["type"] = "object";
            if (schemaObj["properties"] is JsonObject propsObj && schemaObj["required"] is null)
            {
                var required = new JsonArray();
                foreach (var prop in context.TypeInfo.Properties)
                {
                    if (prop.IsExtensionData || !propsObj.ContainsKey(prop.Name))
                    {
                        continue;
                    }

                    if (prop.IsRequired || !prop.IsSetNullable)
                    {
                        required.Add((JsonNode)prop.Name);
                    }
                }

                if (required.Count > 0)
                {
                    schemaObj["required"] = required;
                }
            }
        }

        return schemaObj;
    }

    private static JsonSerializerOptions? s_reflectionOptions;

    [RequiresUnreferencedCode("Uses DefaultJsonTypeInfoResolver.")]
    [RequiresDynamicCode("Uses DefaultJsonTypeInfoResolver.")]
    private static JsonSerializerOptions CreateReflectionOptions()
    {
        if (s_reflectionOptions is not null)
        {
            return s_reflectionOptions;
        }

        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver()
        };
        options.MakeReadOnly();
        s_reflectionOptions = options;
        return options;
    }
}
