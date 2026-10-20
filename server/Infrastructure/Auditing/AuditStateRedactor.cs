using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace EndpointPlatform.Infrastructure.Auditing;

/// <summary>
/// Builds the JSON documents that go into an audit entry's state columns, with
/// secrets removed.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Domain.Auditing.AuditLogEntry"/> has documented this as the
/// supported way to build its state columns since the audit trail was written,
/// and until now the class did not exist -- redaction was by convention, which
/// means by memory. This is that control, made real.
/// </para>
/// <para>
/// It matters more here than in most places because the audit trail is
/// <b>append-only and enforced by database triggers</b>. A secret written into an
/// audit row cannot be edited out afterwards; the row can only be dropped with the
/// whole table. Redaction has to happen before the write or not at all.
/// </para>
/// <para>
/// Two independent rules, because either alone is insufficient. A property whose
/// <em>name</em> denotes a secret is redacted regardless of its value, catching a
/// field somebody added without thinking. A string <em>value</em> that looks like a
/// secret is redacted regardless of its name, catching a key pasted into a field
/// called "note". Neither the name nor the value of a redacted item survives.
/// </para>
/// </remarks>
public static partial class AuditStateRedactor
{
    /// <summary>What replaces a redacted value. Deliberately not the empty string.</summary>
    public const string Placeholder = "[redacted]";

    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = false };

    /// <summary>
    /// Property names that denote a secret regardless of what they contain.
    /// </summary>
    /// <remarks>
    /// Matched on the whole name, case-insensitively. Substring matching is
    /// deliberately not used: it would redact <c>hasRecoveryPasswordProtector</c>,
    /// a boolean that reports whether a protector exists and is exactly the kind
    /// of fact an audit trail should keep.
    /// </remarks>
    private static readonly HashSet<string> SecretNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "currentPassword", "newPassword", "recoveryPassword", "recoveryKey",
        "numericalPassword", "secret", "secretRef", "apiKey", "token", "sessionToken",
        "credential", "privateKey", "passwordHash", "hash",
        "sealedRecoveryPassword", "ciphertext",
        "unattendedPassword", "rustdeskPassword", "sealedPassword",
    };

    /// <summary>
    /// Value shapes that are secrets whatever the property is called: a BitLocker
    /// recovery password, and any long unbroken digit run that could be one.
    /// </summary>
    [GeneratedRegex(@"\d{6}-\d{6}-\d{6}-\d{6}-\d{6}-\d{6}-\d{6}-\d{6}|\d{6}-\d{6}|\d{20,}",
        RegexOptions.CultureInvariant)]
    private static partial Regex SecretShape();

    /// <summary>
    /// Serialises <paramref name="state"/> to a redacted JSON document, or null
    /// when there is nothing to record.
    /// </summary>
    public static string? Redact(object? state)
    {
        if (state is null)
        {
            return null;
        }

        var node = JsonSerializer.SerializeToNode(state, state.GetType(), SerializerOptions);
        if (node is null)
        {
            return null;
        }

        var scrubbed = Scrub(node);
        return scrubbed?.ToJsonString(SerializerOptions);
    }

    /// <summary>
    /// Whether a document still contains anything that looks like a secret.
    /// </summary>
    /// <remarks>
    /// Exposed so tests can assert over a persisted row rather than trusting that
    /// the writer called <see cref="Redact"/>. A control nobody can check from the
    /// outside is a control nobody can rely on.
    /// </remarks>
    public static bool ContainsSecretShape(string? json) =>
        !string.IsNullOrEmpty(json) && SecretShape().IsMatch(json);

    private static JsonNode? Scrub(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var result = new JsonObject();
                foreach (var (name, value) in obj.ToList())
                {
                    result[name] = SecretNames.Contains(name)
                        ? JsonValue.Create(Placeholder)
                        : Scrub(value?.DeepClone());
                }

                return result;
            }

            case JsonArray array:
            {
                var result = new JsonArray();
                foreach (var item in array.ToList())
                {
                    result.Add(Scrub(item?.DeepClone()));
                }

                return result;
            }

            case JsonValue value when value.TryGetValue<string>(out var text):
                return SecretShape().IsMatch(text) ? JsonValue.Create(Placeholder) : JsonValue.Create(text);

            default:
                return node;
        }
    }

    /// <summary>
    /// Convenience for the common "one flat object" case, so callers do not build
    /// anonymous types that drift from the redactor's view of them.
    /// </summary>
    public static string? Redact(IEnumerable<KeyValuePair<string, object?>> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var obj = new JsonObject();
        foreach (var (name, value) in fields)
        {
            obj[name] = SecretNames.Contains(name)
                ? JsonValue.Create(Placeholder)
                : Scrub(JsonSerializer.SerializeToNode(value, value?.GetType() ?? typeof(object), SerializerOptions));
        }

        return obj.ToJsonString(SerializerOptions);
    }
}
