using DiagnosisApi.Models;
using DiagnosisApi.Services;
using DiagnosisApi.Data;
using Microsoft.AspNetCore.Mvc;
using Azure.AI.OpenAI;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Linq;

namespace DiagnosisApi.Controllers;

[ApiController]
public class HumanDiagnosisController : ControllerBase
{
    private readonly AzureOpenAIService _ai;
    public HumanDiagnosisController(AzureOpenAIService ai)
    {
        _ai = ai;
    }

    private async Task<ActionResult?> ValidatePasswordAsync(string password)
    {
        if (string.IsNullOrWhiteSpace(password) ||
            !await DatabaseHelper.PasswordExistsAsync(password))
            return Unauthorized("Invalid password");
        return null;
    }

    private ActionResult? ValidateNotes(string notes)
        => string.IsNullOrWhiteSpace(notes)
            ? BadRequest("Request 'clinical_notes' is empty")
            : null;

    private ActionResult? ValidateNotesAndDiagnosis(string notes, string diagnosis)
        => string.IsNullOrWhiteSpace(notes) || string.IsNullOrWhiteSpace(diagnosis)
            ? BadRequest("Missing notes or diagnosis.")
            : null;

    private async Task<string> CallOpenAiAsync(
        string systemPrompt,
        string userPrompt,
        float temperature = 0.2f,
        ChatCompletionsFunctionToolDefinition? tool = null,
        string? functionName = null)
    {
        var tools = tool == null ? null : new[] { tool };
        return await _ai.GetChatCompletionAsync(
            new[]
            {
                new ChatMessage(ChatRole.System, systemPrompt),
                new ChatMessage(ChatRole.User, userPrompt)
            },
            temperature,
            tools,
            functionName);
    }

    private const string HUMAN_GUIDELINES = """
Human Quick Reference:
1. Severe sepsis indicators: altered mental status, hypotension, elevated lactate.
2. Stroke: acute neurological deficit with imaging confirmation or high suspicion.
3. Myocardial infarction: typical chest pain, ECG changes, elevated troponin.
4. Surgeries are indicated if there's perforation, hemorrhage, or acute abdomen requiring intervention.
INTERNAL REASONING: reason step-by-step privately; do not reveal chain-of-thought.
""";

    private static readonly string SYS_EXTRACT = $"""
You are HumanGPT, an expert clinical scribe. Refer to the guidelines:
{HUMAN_GUIDELINES}
Task: read the raw clinical note below and output a JSON object with:
  • demographics
  • symptoms
  • exam_findings
  • lab_results
  • imaging
  • treatments
Only include data explicitly present in the note.
Return via the extract_features_human function.
""";

    private static readonly string SYS_DIAGNOSE = $"""
You are a clinical-decision assistant for physicians. Refer to these guidelines:
{HUMAN_GUIDELINES}
Rules:
1. Propose 1-5 plausible differential diagnoses.
2. EACH must cite ≥2 specific findings from the note.
3. Suggest 1-3 evidence-based first-line treatments per diagnosis.
4. Highlight surgical/life-threatening conditions if supported.
5. Down-rank diagnoses contradicted by negative/normal findings.
INTERNAL REASONING: reason step-by-step privately, do not show chain-of-thought.
Return JSON via the diagnose_human function with fields:
  diagnosis, confidence (0-1), reason, treatments[].
""";

    private const string META_REVIEW = """
You are a senior physician reviewer supervising an AI assistant.
Tasks:
1. Review the ORIGINAL clinical note and the AI's INITIAL differential JSON.
2. Identify missing or mis-prioritised diagnoses, absent severity grading, or key treatment omissions/contraindications.
3. Produce an improved differential: max 5 diagnoses, each with ≥2 explicit findings from the note and 1-3 first-line treatments. Adjust confidence (0-1) as needed.
4. Explicitly consider syndromes that integrate abnormal labs and weigh them against infection.
5. If imaging shows bilateral non-lobar opacities without fever ≥38.0 °C, suggest non-infectious causes (e.g. pulmonary oedema) before pneumonia.
6. Keep identical JSON schema (diagnoses[ diagnosis, confidence, reason, treatments ]).
Return ONLY the JSON via the diagnose_human function. Do NOT add explanatory prose.
""";

    private static readonly string[] _EMERGENT_KEYWORDS = new[]
    {
        "sepsis","septic shock","ruptured","perforation",
        "stroke","myocardial infarction","subarachnoid hemorrhage",
        "meningitis","intracranial hemorrhage"
    };

    private static readonly ChatCompletionsFunctionToolDefinition EXTRACT_TOOL_HUMAN =
        new(new FunctionDefinition("extract_features_human")
        {
            Description = "Extract structured features from a human clinical note.",
            Parameters = BinaryData.FromString(
            """
{
  "type": "object",
  "properties": {
    "demographics": {"type": "string"},
    "symptoms": {"type": "array", "items": {"type": "string"}},
    "exam_findings": {"type": "array", "items": {"type": "string"}},
    "lab_results": {"type": "array", "items": {"type": "string"}},
    "imaging": {"type": "array", "items": {"type": "string"}},
    "treatments": {"type": "array", "items": {"type": "string"}}
  }
}
""")
        });

    private static readonly ChatCompletionsFunctionToolDefinition DIAGNOSE_TOOL_HUMAN =
        new(new FunctionDefinition("diagnose_human")
        {
            Description = "Return possible human differential diagnoses.",
            Parameters = BinaryData.FromString(
            """
{
  "type": "object",
  "properties": {
    "diagnoses": {
      "type": "array",
      "items": {
        "type": "object",
        "properties": {
          "diagnosis": {"type": "string"},
          "confidence": {"type": "number"},
          "reason": {"type": "string"},
          "treatments": {"type": "array", "items": {"type": "string"}}
        },
        "required": ["diagnosis", "confidence", "reason", "treatments"]
      },
      "minItems": 1,
      "maxItems": 5
    }
  },
  "required": ["diagnoses"]
}
""")
        });

    private static readonly ChatCompletionsFunctionToolDefinition INVESTIGATE_TOOL_HUMAN =
        new(new FunctionDefinition("investigate_human")
        {
            Description = "Return recommended investigations (diagnostic tests) as an array of strings.",
            Parameters = BinaryData.FromString(
            """
{
  "type": "object",
  "properties": {
    "investigations": {"type": "array", "items": {"type": "string"}}
  },
  "required": ["investigations"]
}
""")
        });

    private static readonly ChatCompletionsFunctionToolDefinition NONMEDICAL_TOOL_HUMAN =
        new(new FunctionDefinition("manage_non_medical_human")
        {
            Description = "Return recommended non-medical management suggestions as an array of strings.",
            Parameters = BinaryData.FromString(
            """
{
  "type": "object",
  "properties": {
    "management": {"type": "array", "items": {"type": "string"}}
  },
  "required": ["management"]
}
""")
        });
    private const string INVESTIGATION_SYS = """
You are a clinical decision support assistant. The user provides:
1) Original clinical notes
2) A specific diagnosis
Return a JSON object with an array 'investigations' listing recommended diagnostic investigations or labs to confirm/assess the given diagnosis. Provide 3-7 suggestions.
Name only the relevant tests. Do NOT include chain-of-thought.
Return via the investigate_human function.
""";
    private const string NONMEDICAL_SYS = """
You are a clinical decision support assistant. The user provides:
1) Original clinical notes
2) A specific diagnosis
Return a JSON object with an array 'management' listing non-medical management suggestions (e.g. lifestyle, dietary changes, supportive care) for the specific diagnosis. Provide 3-7 suggestions.
Do NOT include chain-of-thought.
Return via the manage_non_medical_human function.
""";

    private static List<HumanDiagnosisItem> ApplyHeuristics(List<HumanDiagnosisItem> preds, JsonElement summary)
    {
        var imagingJoined = string.Empty;
        if (summary.TryGetProperty("imaging", out var imagingEl) && imagingEl.ValueKind == JsonValueKind.Array)
        {
            imagingJoined = string.Join(" ", imagingEl.EnumerateArray().Select(e => e.GetString() ?? "")).ToLowerInvariant();
        }
        var labsJoined = string.Empty;
        if (summary.TryGetProperty("lab_results", out var labsEl) && labsEl.ValueKind == JsonValueKind.Array)
        {
            labsJoined = string.Join(" ", labsEl.EnumerateArray().Select(e => e.GetString() ?? "")).ToLowerInvariant();
        }
        var emergentFlag = _EMERGENT_KEYWORDS.Any(k => imagingJoined.Contains(k) || labsJoined.Contains(k));
        foreach (var p in preds)
        {
            var nameLower = p.Diagnosis.ToLowerInvariant();
            if (emergentFlag && _EMERGENT_KEYWORDS.Any(k => nameLower.Contains(k)))
            {
                p.Confidence = Math.Min(p.Confidence + 0.15, 1.0);
            }
        }
        return preds.OrderByDescending(p => p.Confidence).ToList();
    }

    [HttpPost("diagnose_human")]
    public async Task<ActionResult<HumanDiagnosisResponse>> DiagnoseHumanAsync(HumanDiagnosisRequest req)
    {
        if (await ValidatePasswordAsync(req.Password) is ActionResult err)
            return err;
        if (ValidateNotes(req.ClinicalNotes) is ActionResult bad)
            return bad;
        try
        {
            var summaryJson = await CallOpenAiAsync(
                SYS_EXTRACT,
                req.ClinicalNotes,
                tool: EXTRACT_TOOL_HUMAN,
                functionName: "extract_features_human");
            using var summaryDoc = JsonDocument.Parse(summaryJson);
            var diagJson = await CallOpenAiAsync(
                SYS_DIAGNOSE,
                summaryJson,
                0.25f,
                DIAGNOSE_TOOL_HUMAN,
                "diagnose_human");
            var preds = JsonSerializer.Deserialize<HumanDiagnosisResponse>(diagJson)?.Diagnoses ?? new();
            var refineJson = await CallOpenAiAsync(
                META_REVIEW,
                $"ORIGINAL_CLINICAL_NOTE:\n{req.ClinicalNotes}\n\nINITIAL_DIFFERENTIAL_JSON:\n{JsonSerializer.Serialize(new HumanDiagnosisResponse { Diagnoses = preds })}",
                0.2f,
                DIAGNOSE_TOOL_HUMAN,
                "diagnose_human");
            preds = JsonSerializer.Deserialize<HumanDiagnosisResponse>(refineJson)?.Diagnoses ?? new();
            preds = ApplyHeuristics(preds, summaryDoc.RootElement);
            var respJson = JsonSerializer.Serialize(new HumanDiagnosisResponse { Diagnoses = preds });
            await DatabaseHelper.LogRequestResponseAsync(req.ClinicalNotes, respJson);
            return Ok(new HumanDiagnosisResponse { Diagnoses = preds });
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Human diagnosis error: {ex.Message}");
        }
    }

    [HttpPost("human_investigations")]
    public async Task<ActionResult<HumanInvestigationResponse>> InvestigationsAsync(HumanInvestigationRequest req)
    {
        if (await ValidatePasswordAsync(req.Password) is ActionResult err)
            return err;
        if (ValidateNotesAndDiagnosis(req.ClinicalNotes, req.Diagnosis) is ActionResult bad)
            return bad;
        try
        {
            var userBlob = $"CLINICAL_NOTES:\n{req.ClinicalNotes}\n\nDIAGNOSIS: {req.Diagnosis}";
            var resultJson = await CallOpenAiAsync(
                INVESTIGATION_SYS,
                userBlob,
                tool: INVESTIGATE_TOOL_HUMAN,
                functionName: "investigate_human");
            var parsed = JsonSerializer.Deserialize<HumanInvestigationResponse>(resultJson);
            var respJson = JsonSerializer.Serialize(parsed);
            await DatabaseHelper.LogRequestResponseAsync(userBlob, respJson);
            return Ok(parsed);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error generating investigations: {ex.Message}");
        }
    }

    [HttpPost("human_non_medical")]
    public async Task<ActionResult<HumanNonMedicalManagementResponse>> NonMedicalAsync(HumanNonMedicalManagementRequest req)
    {
        if (await ValidatePasswordAsync(req.Password) is ActionResult err)
            return err;
        if (ValidateNotesAndDiagnosis(req.ClinicalNotes, req.Diagnosis) is ActionResult bad)
            return bad;
        try
        {
            var userBlob = $"CLINICAL_NOTES:\n{req.ClinicalNotes}\n\nDIAGNOSIS: {req.Diagnosis}";
            var resultJson = await CallOpenAiAsync(
                NONMEDICAL_SYS,
                userBlob,
                tool: NONMEDICAL_TOOL_HUMAN,
                functionName: "manage_non_medical_human");
            var parsed = JsonSerializer.Deserialize<HumanNonMedicalManagementResponse>(resultJson);
            var respJson = JsonSerializer.Serialize(parsed);
            await DatabaseHelper.LogRequestResponseAsync(userBlob, respJson);
            return Ok(parsed);
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Error generating non-medical management: {ex.Message}");
        }
    }
}
