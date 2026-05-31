using System.Globalization;
using System.Net;
using System.Text;
using BattleScribeSpec.Roster;

namespace BattleScribeSpec.Debugger;

/// <summary>
/// Collects step data during a spec run and generates a self-contained HTML timeline report.
/// </summary>
public sealed class TimelineReport
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _specId;
    private readonly List<StepRecord> _steps = [];

    public TimelineReport(string specId)
    {
        _specId = specId;
    }

    /// <summary>Records data for a completed step.</summary>
    public void AddStep(int stepIndex, StepDef step, RosterState? state, IReadOnlyList<ValidationErrorState>? errors, byte[]? screenshotPng)
    {
        _steps.Add(new StepRecord(stepIndex, step, state, errors, screenshotPng));
    }

    /// <summary>Generates and writes the HTML report to the specified path.</summary>
    public void Write(string filePath, bool passed, IReadOnlyList<string>? failures)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(filePath, GenerateHtml(passed, failures), Utf8NoBom);
    }

    private string GenerateHtml(bool passed, IReadOnlyList<string>? failures)
    {
        var timestamp = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
        var builder = new StringBuilder();

        builder.AppendLine("<!DOCTYPE html>");
        builder.AppendLine("<html lang=\"en\">");
        builder.AppendLine("<head>");
        builder.AppendLine("  <meta charset=\"utf-8\" />");
        builder.AppendLine("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\" />");
        builder.AppendLine($"  <title>{Encode(_specId)} timeline report</title>");
        builder.AppendLine("  <style>");
        builder.AppendLine("""
            :root {
                color-scheme: dark;
                --bg: #0b1020;
                --panel: #141a2e;
                --panel-alt: #1a223b;
                --border: #2a355a;
                --text: #edf2ff;
                --muted: #9ba7c7;
                --accent: #6ea8fe;
                --success: #2ecc71;
                --danger: #ff6b6b;
                --warning: #f7b955;
                --shadow: 0 18px 40px rgba(0, 0, 0, 0.28);
            }

            * { box-sizing: border-box; }
            body {
                margin: 0;
                font-family: Inter, Segoe UI, Arial, sans-serif;
                background: linear-gradient(180deg, #0b1020 0%, #0e1529 100%);
                color: var(--text);
            }

            a { color: inherit; }
            code {
                font-family: Consolas, Menlo, Monaco, monospace;
                background: rgba(255, 255, 255, 0.06);
                border: 1px solid rgba(255, 255, 255, 0.08);
                border-radius: 6px;
                padding: 0.1rem 0.35rem;
            }

            .page {
                max-width: 1400px;
                margin: 0 auto;
                padding: 24px;
            }

            .hero,
            .step-card,
            .failures {
                background: rgba(20, 26, 46, 0.96);
                border: 1px solid var(--border);
                border-radius: 18px;
                box-shadow: var(--shadow);
            }

            .hero {
                padding: 24px;
                margin-bottom: 24px;
            }

            .hero-top {
                display: flex;
                flex-wrap: wrap;
                justify-content: space-between;
                gap: 16px;
                align-items: center;
            }

            .title {
                margin: 0;
                font-size: clamp(1.6rem, 2vw, 2.4rem);
                line-height: 1.15;
            }

            .subtitle {
                margin: 8px 0 0;
                color: var(--muted);
            }

            .badge {
                display: inline-flex;
                align-items: center;
                gap: 8px;
                border-radius: 999px;
                padding: 0.5rem 0.9rem;
                font-weight: 700;
                letter-spacing: 0.02em;
                border: 1px solid transparent;
            }

            .badge-pass {
                background: rgba(46, 204, 113, 0.14);
                color: #b8f7cf;
                border-color: rgba(46, 204, 113, 0.35);
            }

            .badge-fail {
                background: rgba(255, 107, 107, 0.14);
                color: #ffc2c2;
                border-color: rgba(255, 107, 107, 0.35);
            }

            .badge-assert {
                background: rgba(247, 185, 85, 0.14);
                color: #ffe1a7;
                border-color: rgba(247, 185, 85, 0.3);
            }

            .badge-action {
                background: rgba(110, 168, 254, 0.14);
                color: #cde1ff;
                border-color: rgba(110, 168, 254, 0.3);
            }

            .metrics {
                display: grid;
                grid-template-columns: repeat(auto-fit, minmax(160px, 1fr));
                gap: 12px;
                margin-top: 20px;
            }

            .metric {
                background: var(--panel-alt);
                border: 1px solid var(--border);
                border-radius: 14px;
                padding: 14px 16px;
            }

            .metric-label {
                color: var(--muted);
                font-size: 0.85rem;
                margin-bottom: 6px;
            }

            .metric-value {
                font-size: 1.1rem;
                font-weight: 700;
            }

            .timeline {
                display: grid;
                gap: 18px;
            }

            .step-card {
                overflow: hidden;
            }

            .step-header {
                display: flex;
                justify-content: space-between;
                gap: 12px;
                align-items: flex-start;
                padding: 18px 20px 0;
                flex-wrap: wrap;
            }

            .step-title {
                margin: 0;
                font-size: 1.15rem;
            }

            .step-subtitle {
                margin: 6px 0 0;
                color: var(--muted);
            }

            .params {
                display: flex;
                flex-wrap: wrap;
                gap: 8px;
                margin-top: 14px;
            }

            .param {
                background: rgba(255, 255, 255, 0.05);
                border: 1px solid rgba(255, 255, 255, 0.08);
                border-radius: 999px;
                padding: 0.35rem 0.7rem;
                color: var(--muted);
                font-size: 0.92rem;
            }

            .step-body {
                display: grid;
                grid-template-columns: minmax(0, 1.2fr) minmax(320px, 0.8fr);
                gap: 18px;
                padding: 18px 20px 20px;
            }

            .screenshot,
            .summary-panel {
                background: var(--panel-alt);
                border: 1px solid var(--border);
                border-radius: 14px;
                padding: 14px;
            }

            .screenshot h3,
            .summary-panel h3,
            .summary-panel h4,
            .failures h3 {
                margin-top: 0;
            }

            .screenshot img {
                display: block;
                width: 100%;
                max-width: 600px;
                height: auto;
                border-radius: 12px;
                border: 1px solid rgba(255, 255, 255, 0.12);
                background: #0a0f1f;
            }

            .screenshot a {
                display: inline-block;
            }

            .empty {
                color: var(--muted);
                font-style: italic;
            }

            .summary-grid {
                display: grid;
                grid-template-columns: repeat(auto-fit, minmax(150px, 1fr));
                gap: 10px;
                margin-bottom: 14px;
            }

            .summary-item {
                background: rgba(255, 255, 255, 0.04);
                border: 1px solid rgba(255, 255, 255, 0.06);
                border-radius: 12px;
                padding: 12px;
            }

            .summary-item .label {
                color: var(--muted);
                font-size: 0.82rem;
                margin-bottom: 4px;
            }

            .summary-item .value {
                font-weight: 700;
            }

            details {
                margin-top: 12px;
                background: rgba(255, 255, 255, 0.03);
                border: 1px solid rgba(255, 255, 255, 0.06);
                border-radius: 12px;
                padding: 10px 12px;
            }

            summary {
                cursor: pointer;
                font-weight: 700;
            }

            ul {
                margin: 10px 0 0 20px;
                padding: 0;
            }

            li {
                margin: 6px 0;
            }

            .muted {
                color: var(--muted);
            }

            .error-list li,
            .failures li {
                color: #ffc2c2;
            }

            .failures {
                margin-top: 24px;
                padding: 20px;
                border-color: rgba(255, 107, 107, 0.45);
                background: rgba(58, 20, 28, 0.88);
            }

            @media (max-width: 980px) {
                .step-body {
                    grid-template-columns: 1fr;
                }
            }
        """);
        builder.AppendLine("  </style>");
        builder.AppendLine("</head>");
        builder.AppendLine("<body>");
        builder.AppendLine("  <main class=\"page\">");
        builder.AppendLine("    <section class=\"hero\">");
        builder.AppendLine("      <div class=\"hero-top\">");
        builder.AppendLine("        <div>");
        builder.AppendLine($"          <h1 class=\"title\">Timeline report: {Encode(_specId)}</h1>");
        builder.AppendLine("          <p class=\"subtitle\">Self-contained debugger timeline with screenshots and roster summaries.</p>");
        builder.AppendLine("        </div>");
        builder.AppendLine($"        <div class=\"badge {(passed ? "badge-pass" : "badge-fail")}\">{(passed ? "PASS" : "FAIL")}</div>");
        builder.AppendLine("      </div>");
        builder.AppendLine("      <div class=\"metrics\">");
        AppendMetric(builder, "Spec ID", _specId);
        AppendMetric(builder, "Steps", _steps.Count.ToString(CultureInfo.InvariantCulture));
        AppendMetric(builder, "Generated", timestamp);
        AppendMetric(builder, "Failures", failures?.Count.ToString(CultureInfo.InvariantCulture) ?? "0");
        builder.AppendLine("      </div>");
        builder.AppendLine("    </section>");
        builder.AppendLine("    <section class=\"timeline\">");

        foreach (var step in _steps.OrderBy(s => s.Index))
        {
            AppendStep(builder, step);
        }

        builder.AppendLine("    </section>");

        if (failures is { Count: > 0 })
        {
            builder.AppendLine("    <section class=\"failures\">");
            builder.AppendLine("      <h3>Failures</h3>");
            builder.AppendLine("      <ul>");
            foreach (var failure in failures)
            {
                builder.AppendLine($"        <li>{Encode(failure)}</li>");
            }

            builder.AppendLine("      </ul>");
            builder.AppendLine("    </section>");
        }

        builder.AppendLine("  </main>");
        builder.AppendLine("</body>");
        builder.AppendLine("</html>");
        return builder.ToString();
    }

    private static void AppendMetric(StringBuilder builder, string label, string value)
    {
        builder.AppendLine("        <div class=\"metric\">");
        builder.AppendLine($"          <div class=\"metric-label\">{Encode(label)}</div>");
        builder.AppendLine($"          <div class=\"metric-value\">{Encode(value)}</div>");
        builder.AppendLine("        </div>");
    }

    private static void AppendStep(StringBuilder builder, StepRecord step)
    {
        var isAssert = string.IsNullOrWhiteSpace(step.Step.Action);
        var actionName = isAssert ? "Assert" : step.Step.Action!;
        var actionDescription = DescribeStep(step.Step);
        var errors = step.Errors ?? step.State?.ValidationErrors;
        var screenshotDataUrl = step.ScreenshotPng is null ? null : $"data:image/png;base64,{Convert.ToBase64String(step.ScreenshotPng)}";

        builder.AppendLine("      <article class=\"step-card\">");
        builder.AppendLine("        <div class=\"step-header\">");
        builder.AppendLine("          <div>");
        builder.AppendLine($"            <h2 class=\"step-title\">Step {step.Index}: {Encode(actionName)}</h2>");
        builder.AppendLine($"            <p class=\"step-subtitle\">{Encode(actionDescription)}</p>");

        var parameters = GetStepParameters(step.Step);
        if (parameters.Count > 0)
        {
            builder.AppendLine("            <div class=\"params\">");
            foreach (var parameter in parameters)
            {
                builder.AppendLine($"              <span class=\"param\"><strong>{Encode(parameter.Name)}:</strong> {Encode(parameter.Value)}</span>");
            }

            builder.AppendLine("            </div>");
        }

        builder.AppendLine("          </div>");
        builder.AppendLine($"          <div class=\"badge {(isAssert ? "badge-assert" : "badge-action")}\">{Encode(isAssert ? "Assert" : "Action")}</div>");
        builder.AppendLine("        </div>");
        builder.AppendLine("        <div class=\"step-body\">");
        builder.AppendLine("          <section class=\"screenshot\">");
        builder.AppendLine("            <h3>Screenshot</h3>");
        if (screenshotDataUrl is not null)
        {
            builder.AppendLine($"              <img src=\"{screenshotDataUrl}\" alt=\"Screenshot for step {step.Index}\" style=\"cursor:zoom-in\" onclick=\"window.open(this.src)\" />");
        }
        else
        {
            builder.AppendLine("            <p class=\"empty\">No screenshot captured for this step.</p>");
        }

        builder.AppendLine("          </section>");
        builder.AppendLine("          <section class=\"summary-panel\">");
        builder.AppendLine("            <h3>State summary</h3>");
        AppendStateSummary(builder, step.State, errors);
        builder.AppendLine("          </section>");
        builder.AppendLine("        </div>");
        builder.AppendLine("      </article>");
    }

    private static void AppendStateSummary(StringBuilder builder, RosterState? state, IReadOnlyList<ValidationErrorState>? errors)
    {
        var effectiveErrors = errors ?? state?.ValidationErrors;
        if (state is null)
        {
            builder.AppendLine("            <p class=\"empty\">No roster state captured for this step.</p>");
            if (effectiveErrors is { Count: > 0 })
            {
                AppendErrors(builder, effectiveErrors, indent: "            ");
            }

            return;
        }

        builder.AppendLine("            <div class=\"summary-grid\">");
        AppendSummaryItem(builder, "Roster", state.Name);
        AppendSummaryItem(builder, "Costs", FormatCosts(state.Costs));
        AppendSummaryItem(builder, "Forces", state.Forces.Count.ToString(CultureInfo.InvariantCulture));
        AppendSummaryItem(builder, "Validation errors", (effectiveErrors?.Count ?? 0).ToString(CultureInfo.InvariantCulture));
        builder.AppendLine("            </div>");

        builder.AppendLine("            <details>");
        builder.AppendLine("              <summary>Force details</summary>");
        if (state.Forces.Count == 0)
        {
            builder.AppendLine("              <p class=\"muted\">No forces in roster.</p>");
        }
        else
        {
            builder.AppendLine("              <ul>");
            foreach (var force in state.Forces)
            {
                AppendForce(builder, force, "                ");
            }

            builder.AppendLine("              </ul>");
        }

        builder.AppendLine("            </details>");

        if (effectiveErrors is { Count: > 0 })
        {
            AppendErrors(builder, effectiveErrors, indent: "            ");
        }
    }

    private static void AppendSummaryItem(StringBuilder builder, string label, string value)
    {
        builder.AppendLine("              <div class=\"summary-item\">");
        builder.AppendLine($"                <div class=\"label\">{Encode(label)}</div>");
        builder.AppendLine($"                <div class=\"value\">{Encode(value)}</div>");
        builder.AppendLine("              </div>");
    }

    private static void AppendForce(StringBuilder builder, ForceState force, string indent)
    {
        var totalSelections = CountSelections(force.Selections);
        builder.Append($"{indent}<li><strong>{Encode(force.Name)}</strong> <span class=\"muted\">({totalSelections} selections)</span>");
        if (force.Selections.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine($"{indent}  <ul>");
            foreach (var selection in force.Selections)
            {
                AppendSelection(builder, selection, indent + "    ");
            }

            builder.AppendLine($"{indent}  </ul>");
            builder.AppendLine($"{indent}</li>");
        }
        else
        {
            builder.AppendLine("</li>");
        }
    }

    private static void AppendSelection(StringBuilder builder, SelectionState selection, string indent)
    {
        var label = selection.Number != 1 ? $"{selection.Name} ×{selection.Number}" : selection.Name;
        builder.Append($"{indent}<li>{Encode(label)}");
        if (selection.Children.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine($"{indent}  <ul>");
            foreach (var child in selection.Children)
            {
                AppendSelection(builder, child, indent + "    ");
            }

            builder.AppendLine($"{indent}  </ul>");
            builder.AppendLine($"{indent}</li>");
        }
        else
        {
            builder.AppendLine("</li>");
        }
    }

    private static void AppendErrors(StringBuilder builder, IReadOnlyList<ValidationErrorState> errors, string indent)
    {
        builder.AppendLine($"{indent}<details>");
        builder.AppendLine($"{indent}  <summary>Validation errors ({errors.Count})</summary>");
        builder.AppendLine($"{indent}  <ul class=\"error-list\">");
        foreach (var error in errors)
        {
            var owner = error.OwnerType is null
                ? null
                : error.OwnerId is null
                    ? error.OwnerType
                    : $"{error.OwnerType} {error.OwnerId}";
            var detail = owner is null ? error.Message : $"{error.Message} ({owner})";
            builder.AppendLine($"{indent}    <li>{Encode(detail)}</li>");
        }

        builder.AppendLine($"{indent}  </ul>");
        builder.AppendLine($"{indent}</details>");
    }

    private static int CountSelections(IReadOnlyList<SelectionState> selections)
    {
        var count = 0;
        foreach (var selection in selections)
        {
            count += 1 + CountSelections(selection.Children);
        }

        return count;
    }

    private static string FormatCosts(IReadOnlyList<CostState> costs)
    {
        if (costs.Count == 0)
        {
            return "None";
        }

        var parts = new List<string>(costs.Count);
        foreach (var cost in costs)
        {
            var hidden = cost.Hidden ? " [hidden]" : "";
            parts.Add($"{cost.Name}: {cost.Value.ToString(CultureInfo.InvariantCulture)}{hidden}");
        }

        return string.Join(", ", parts);
    }

    private static string DescribeStep(StepDef step)
    {
        if (step.Action is { } action)
        {
            return action;
        }

        return step.ExpectedState is not null ? "Assertion step" : "Unknown step";
    }

    private static List<(string Name, string Value)> GetStepParameters(StepDef step)
    {
        var parameters = new List<(string Name, string Value)>();

        AddParameter(parameters, "id", step.Id);
        AddParameter(parameters, "forceEntryId", step.ForceEntryId);
        AddParameter(parameters, "catalogueId", step.CatalogueId);
        AddParameter(parameters, "forceId", step.ForceId);
        AddParameter(parameters, "selectionId", step.SelectionId);
        AddParameter(parameters, "entryId", step.EntryId);
        AddParameter(parameters, "categoryEntryId", step.CategoryEntryId);
        AddParameter(parameters, "customName", step.CustomName);
        AddParameter(parameters, "customNotes", step.CustomNotes);
        AddParameter(parameters, "costTypeId", step.CostTypeId);
        AddParameter(parameters, "path", step.Path);

        if (step.Count is { } count)
        {
            parameters.Add(("count", count.ToString(CultureInfo.InvariantCulture)));
        }

        if (step.Value is { } value)
        {
            parameters.Add(("value", value.ToString(CultureInfo.InvariantCulture)));
        }

        return parameters;
    }

    private static void AddParameter(List<(string Name, string Value)> parameters, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parameters.Add((name, value));
        }
    }

    private static string Encode(string? value)
    {
        return WebUtility.HtmlEncode(value ?? string.Empty);
    }

    private sealed record StepRecord(int Index, StepDef Step, RosterState? State, IReadOnlyList<ValidationErrorState>? Errors, byte[]? ScreenshotPng);
}
