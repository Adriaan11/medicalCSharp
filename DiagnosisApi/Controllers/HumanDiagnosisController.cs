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

    private const string SYS_EXTRACT = "You are HumanGPT, an expert clinical scribe. Extract JSON.";
    private const string SYS_DIAGNOSE = "You are a clinical-decision assistant for physicians. Return JSON differential.";
    private const string META_REVIEW = "You are a senior physician reviewer supervising an AI assistant. Return JSON only.";

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
            Parameters = BinaryData.FromObjectAsJson(new { type = "object" })
        });

    private static readonly ChatCompletionsFunctionToolDefinition DIAGNOSE_TOOL_HUMAN =
        new(new FunctionDefinition("diagnose_human")
        {
            Description = "Return possible human differential diagnoses.",
            Parameters = BinaryData.FromObjectAsJson(new { type = "object" })
        });

    private static readonly ChatCompletionsFunctionToolDefinition INVESTIGATE_TOOL_HUMAN =
        new(new FunctionDefinition("investigate_human")
        {
            Description = "Return recommended investigations.",
            Parameters = BinaryData.FromObjectAsJson(new { type = "object" })
        });

    private static readonly ChatCompletionsFunctionToolDefinition NONMEDICAL_TOOL_HUMAN =
        new(new FunctionDefinition("manage_non_medical_human")
        {
            Description = "Return non-medical management suggestions.",
            Parameters = BinaryData.FromObjectAsJson(new { type = "object" })
        });
    private const string INVESTIGATION_SYS = "You are a clinical decision support assistant. Return JSON investigations.";
    private const string NONMEDICAL_SYS = "You are a clinical decision support assistant. Return JSON non-medical management.";

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
        if (!await DatabaseHelper.PasswordExistsAsync(req.Password))
            return Unauthorized("Invalid password");
        if (string.IsNullOrWhiteSpace(req.ClinicalNotes))
            return BadRequest("Request 'clinical_notes' is empty");
        try
        {
            var summaryJson = await _ai.GetChatCompletionAsync(
                new[]
                {
                    new ChatMessage(ChatRole.System, SYS_EXTRACT),
                    new ChatMessage(ChatRole.User, req.ClinicalNotes)
                },
                tools: new[] { EXTRACT_TOOL_HUMAN },
                functionName: "extract_features_human");
            using var summaryDoc = JsonDocument.Parse(summaryJson);
            var diagJson = await _ai.GetChatCompletionAsync(
                new[]
                {
                    new ChatMessage(ChatRole.System, SYS_DIAGNOSE),
                    new ChatMessage(ChatRole.User, summaryJson)
                },
                0.25f,
                new[] { DIAGNOSE_TOOL_HUMAN },
                "diagnose_human");
            var preds = JsonSerializer.Deserialize<HumanDiagnosisResponse>(diagJson)?.Diagnoses ?? new();
            var refineJson = await _ai.GetChatCompletionAsync(
                new[]
                {
                    new ChatMessage(ChatRole.System, META_REVIEW),
                    new ChatMessage(ChatRole.User, $"ORIGINAL_CLINICAL_NOTE:\n{req.ClinicalNotes}\n\nINITIAL_DIFFERENTIAL_JSON:\n{JsonSerializer.Serialize(new HumanDiagnosisResponse { Diagnoses = preds })}")
                },
                0.2f,
                new[] { DIAGNOSE_TOOL_HUMAN },
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
        if (!await DatabaseHelper.PasswordExistsAsync(req.Password))
            return Unauthorized("Invalid password");
        if (string.IsNullOrWhiteSpace(req.ClinicalNotes) || string.IsNullOrWhiteSpace(req.Diagnosis))
            return BadRequest("Missing notes or diagnosis.");
        try
        {
            var userBlob = $"CLINICAL_NOTES:\n{req.ClinicalNotes}\n\nDIAGNOSIS: {req.Diagnosis}";
            var resultJson = await _ai.GetChatCompletionAsync(
                new[]
                {
                    new ChatMessage(ChatRole.System, INVESTIGATION_SYS),
                    new ChatMessage(ChatRole.User, userBlob)
                },
                tools: new[] { INVESTIGATE_TOOL_HUMAN },
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
        if (!await DatabaseHelper.PasswordExistsAsync(req.Password))
            return Unauthorized("Invalid password");
        if (string.IsNullOrWhiteSpace(req.ClinicalNotes) || string.IsNullOrWhiteSpace(req.Diagnosis))
            return BadRequest("Missing notes or diagnosis.");
        try
        {
            var userBlob = $"CLINICAL_NOTES:\n{req.ClinicalNotes}\n\nDIAGNOSIS: {req.Diagnosis}";
            var resultJson = await _ai.GetChatCompletionAsync(
                new[]
                {
                    new ChatMessage(ChatRole.System, NONMEDICAL_SYS),
                    new ChatMessage(ChatRole.User, userBlob)
                },
                tools: new[] { NONMEDICAL_TOOL_HUMAN },
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
