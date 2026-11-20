namespace Glacier.Agent;

using System;
using System.Collections.Generic;
using System.Text.Json;

internal static class VerificationResultParser
{
    public static VerificationResult Parse(string? rawOutput, string verifierName)
    {
        if (string.IsNullOrWhiteSpace(rawOutput))
        {
            return VerificationResult.Fail("Verification tool returned an empty response.", verifierName: verifierName);
        }

        string trimmed = rawOutput.Trim();
        if (trimmed.StartsWith('{') && trimmed.EndsWith('}'))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (TryGetBool(root, "passed", out bool passedValue))
                    {
                        var deserialized = JsonSerializer.Deserialize(trimmed, AdkJsonContext.Default.VerificationResult);
                        if (deserialized != null)
                        {
                            if (deserialized.Passed)
                            {
                                return deserialized with
                                {
                                    Status = VerificationStatus.Passed,
                                    VerifierName = deserialized.VerifierName ?? verifierName,
                                    Summary = string.IsNullOrWhiteSpace(deserialized.Summary) ? "Verification passed." : deserialized.Summary,
                                    Issues = deserialized.Issues ?? new List<VerificationIssue>()
                                };
                            }

                            string reason = !string.IsNullOrWhiteSpace(deserialized.FailureReason)
                                ? deserialized.FailureReason!
                                : (!string.IsNullOrWhiteSpace(deserialized.Summary) ? deserialized.Summary : "Verification failed.");

                            return deserialized with
                            {
                                Status = deserialized.Status == VerificationStatus.None ? VerificationStatus.Failed : deserialized.Status,
                                FailureReason = reason,
                                Summary = string.IsNullOrWhiteSpace(deserialized.Summary) ? reason : deserialized.Summary,
                                VerifierName = deserialized.VerifierName ?? verifierName,
                                Issues = deserialized.Issues ?? new List<VerificationIssue>()
                            };
                        }

                        return passedValue
                            ? VerificationResult.Pass(verifierName: verifierName)
                            : VerificationResult.Fail("Verification failed.", verifierName: verifierName);
                    }

                    bool? inferredPass = null;
                    if (TryGetBool(root, "success", out bool sVal)) inferredPass = sVal;
                    else if (TryGetBool(root, "isValid", out bool ivVal)) inferredPass = ivVal;
                    else if (TryGetBool(root, "valid", out bool vVal)) inferredPass = vVal;

                    string? statusText = TryGetString(root, "status");
                    if (inferredPass == null && statusText != null)
                    {
                        if (statusText.Equals("passed", StringComparison.OrdinalIgnoreCase)
                            || statusText.Equals("pass", StringComparison.OrdinalIgnoreCase)
                            || statusText.Equals("ok", StringComparison.OrdinalIgnoreCase)
                            || statusText.Equals("success", StringComparison.OrdinalIgnoreCase))
                        {
                            inferredPass = true;
                        }
                        else if (statusText.Equals("failed", StringComparison.OrdinalIgnoreCase)
                            || statusText.Equals("fail", StringComparison.OrdinalIgnoreCase)
                            || statusText.Equals("error", StringComparison.OrdinalIgnoreCase)
                            || statusText.Equals("circuitBreakerTripped", StringComparison.OrdinalIgnoreCase)
                            || statusText.Equals("circuit_breaker_tripped", StringComparison.OrdinalIgnoreCase))
                        {
                            inferredPass = false;
                        }
                    }

                    string? message = TryGetString(root, "failureReason")
                        ?? TryGetString(root, "reason")
                        ?? TryGetString(root, "feedback")
                        ?? TryGetString(root, "message")
                        ?? TryGetString(root, "error")
                        ?? TryGetString(root, "summary");

                    string? suggestedFix = TryGetString(root, "suggestedFix");
                    var issues = ExtractIssues(root);

                    if (inferredPass == true)
                    {
                        return VerificationResult.Pass(message ?? "Verification passed.", verifierName);
                    }

                    if (string.Equals(statusText, "error", StringComparison.OrdinalIgnoreCase))
                    {
                        return VerificationResult.Error(message ?? trimmed, verifierName, suggestedFix, issues.Count > 0 ? issues : null);
                    }

                    return VerificationResult.Fail(
                        message ?? trimmed,
                        issues.Count > 0 ? issues : null,
                        suggestedFix,
                        verifierName);
                }
            }
            catch (JsonException)
            {
                // Fall through to plain-text handling.
            }
        }

        if (trimmed.Equals("PASS", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("PASSED", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("OK", StringComparison.OrdinalIgnoreCase))
        {
            return VerificationResult.Pass(trimmed, verifierName);
        }

        return VerificationResult.Fail(trimmed, verifierName: verifierName);
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement root, string name, out JsonElement value)
    {
        if (root.TryGetProperty(name, out value))
        {
            return true;
        }
        foreach (var prop in root.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static bool TryGetBool(JsonElement root, string name, out bool value)
    {
        if (TryGetPropertyIgnoreCase(root, name, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.True) { value = true; return true; }
            if (prop.ValueKind == JsonValueKind.False) { value = false; return true; }
        }
        value = false;
        return false;
    }

    private static string? TryGetString(JsonElement root, string name)
        => TryGetPropertyIgnoreCase(root, name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static List<VerificationIssue> ExtractIssues(JsonElement root)
    {
        var issues = new List<VerificationIssue>();
        if (TryGetPropertyIgnoreCase(root, "issues", out var issuesEl) && issuesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in issuesEl.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var text = item.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        issues.Add(new VerificationIssue("issue", text!));
                    }
                }
                else if (item.ValueKind == JsonValueKind.Object)
                {
                    var code = TryGetString(item, "code") ?? "issue";
                    var msg = TryGetString(item, "message") ?? item.GetRawText();
                    var field = TryGetString(item, "field");
                    var fix = TryGetString(item, "suggestedFix");
                    issues.Add(new VerificationIssue(code, msg, field, fix));
                }
            }
        }
        else if (TryGetPropertyIgnoreCase(root, "errors", out var errorsEl) && errorsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in errorsEl.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var text = item.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        issues.Add(new VerificationIssue("error", text!));
                    }
                }
            }
        }
        return issues;
    }
}
