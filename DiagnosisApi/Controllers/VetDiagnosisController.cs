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
[Route("/diagnose")]
public class VetDiagnosisController : ControllerBase
{
    private readonly AzureOpenAIService _ai;
    public VetDiagnosisController(AzureOpenAIService ai)
    {
        _ai = ai;
    }

    private const string SYS_EXTRACT = "You are VetGPT, an expert medical scribe. Extract JSON from the note.";
    private const string SYS_DIAGNOSE = "You are a veterinary clinical-decision assistant. Return JSON differential.";
    private const string META_REVIEW = "You are a senior veterinary reviewer supervising an AI assistant. Return JSON only.";

    private static readonly Regex ANTIBIOTIC_PATTERN = new(
        @"\b(cef|cefazolin|amox|clav|augmentin|enroflox|baytril|metro|metronidazole)\w*",
        RegexOptions.IgnoreCase);

    private static readonly string[] _SURGICAL_KEYWORDS = new[]
    {
        "peritonitis","septic peritonitis","intestinal obstruction",
        "gastrointestinal obstruction","foreign body","perforation",
        "gdv","volvulus","mesenteric torsion"
    };

    private static readonly ChatCompletionsFunctionToolDefinition EXTRACT_TOOL_VET =
        new(new FunctionDefinition("extract_features")
        {
            Description = "Extract structured features from a vet record.",
            Parameters = BinaryData.FromObjectAsJson(new { type = "object" })
        });

    private static readonly ChatCompletionsFunctionToolDefinition DIAGNOSE_TOOL_VET =
        new(new FunctionDefinition("diagnose")
        {
            Description = "Return possible veterinary diagnoses.",
            Parameters = BinaryData.FromObjectAsJson(new { type = "object" })
        });

    private static List<DiagnosisItem> ApplyHeuristics(List<DiagnosisItem> preds, JsonElement summary)
    {
        var hasSurgery = summary.TryGetProperty("surgical_procedures", out var s) && s.ValueKind == JsonValueKind.Array && s.GetArrayLength() > 0;
        var hasImaging = summary.TryGetProperty("imaging", out var i) && i.ValueKind == JsonValueKind.Array && i.GetArrayLength() > 0;
        var treatmentsJoined = string.Empty;
        if (summary.TryGetProperty("treatments", out var t) && t.ValueKind == JsonValueKind.Array)
        {
            treatmentsJoined = string.Join(" ", t.EnumerateArray().Select(e => e.GetString() ?? "")).ToLowerInvariant();
        }
        var ivAbx = ANTIBIOTIC_PATTERN.IsMatch(treatmentsJoined);
        string parvoVal = string.Empty;
        if (summary.TryGetProperty("test_results", out var tests) && tests.ValueKind == JsonValueKind.Object && tests.TryGetProperty("parvo", out var pVal))
        {
            parvoVal = pVal.GetString()?.ToLowerInvariant() ?? string.Empty;
        }
        var parvoNeg = parvoVal.Contains("neg");
        var clinicalJoined = string.Empty;
        if (summary.TryGetProperty("other_clinical_clues", out var clues) && clues.ValueKind == JsonValueKind.Array)
        {
            clinicalJoined = string.Join(" ", clues.EnumerateArray().Select(e => e.GetString() ?? "")).ToLowerInvariant();
        }
        var shockFlag = clinicalJoined.Contains("shock") || treatmentsJoined.Contains("shock");
        var bloatFlag = clinicalJoined.Contains("bloat") || clinicalJoined.Contains("gdv");
        foreach (var p in preds)
        {
            var diagnosisLower = p.Diagnosis.ToLowerInvariant();
            if (hasSurgery && hasImaging && ivAbx && _SURGICAL_KEYWORDS.Any(k => diagnosisLower.Contains(k)))
                p.Confidence = Math.Min(p.Confidence + 0.15, 1.0);
            if (diagnosisLower.Contains("parvo") && parvoNeg)
                p.Confidence = Math.Max(p.Confidence - 0.30, 0.0);
            if (shockFlag && (diagnosisLower.Contains("sepsis") || diagnosisLower.Contains("septic")))
                p.Confidence = Math.Min(p.Confidence + 0.1, 1.0);
            if (bloatFlag && (diagnosisLower.Contains("gdv") || diagnosisLower.Contains("bloat")))
                p.Confidence = Math.Min(p.Confidence + 0.1, 1.0);
        }
        return preds.OrderByDescending(p => p.Confidence).ToList();
    }

    [HttpPost]
    public async Task<ActionResult<DiagnosisResponse>> DiagnoseAsync(DiagnosisRequest req)
    {
        if (!await DatabaseHelper.PasswordExistsAsync(req.Password))
            return Unauthorized("Invalid password");
        if (string.IsNullOrWhiteSpace(req.Text))
            return BadRequest("Request 'text' is empty");
        try
        {
            var summaryJson = await _ai.GetChatCompletionAsync(
                new[]
                {
                    new ChatMessage(ChatRole.System, SYS_EXTRACT),
                    new ChatMessage(ChatRole.User, req.Text)
                },
                tools: new[] { EXTRACT_TOOL_VET },
                functionName: "extract_features");
            using var summaryDoc = JsonDocument.Parse(summaryJson);
            var diagJson = await _ai.GetChatCompletionAsync(
                new[]
                {
                    new ChatMessage(ChatRole.System, SYS_DIAGNOSE),
                    new ChatMessage(ChatRole.User, summaryJson)
                },
                0.3f,
                new[] { DIAGNOSE_TOOL_VET },
                "diagnose");
            var preds = JsonSerializer.Deserialize<DiagnosisResponse>(diagJson)?.Diagnoses ?? new();
            var refineJson = await _ai.GetChatCompletionAsync(
                new[]
                {
                    new ChatMessage(ChatRole.System, META_REVIEW),
                    new ChatMessage(ChatRole.User, $"ORIGINAL_VETERINARY_RECORD:\n{req.Text}\n\nINITIAL_DIFFERENTIAL_JSON:\n{JsonSerializer.Serialize(new DiagnosisResponse{ Diagnoses = preds })}")
                },
                0.2f,
                new[] { DIAGNOSE_TOOL_VET },
                "diagnose");
            preds = JsonSerializer.Deserialize<DiagnosisResponse>(refineJson)?.Diagnoses ?? new();
            preds = ApplyHeuristics(preds, summaryDoc.RootElement);
            var respJson = JsonSerializer.Serialize(new DiagnosisResponse { Diagnoses = preds });
            await DatabaseHelper.LogRequestResponseAsync(req.Text, respJson);
            return Ok(new DiagnosisResponse { Diagnoses = preds });
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"Vet diagnosis error: {ex.Message}");
        }
    }
}
