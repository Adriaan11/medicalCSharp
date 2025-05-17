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

    private async Task<ActionResult?> ValidateRequestAsync(DiagnosisRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Text))
            return BadRequest("Request 'text' is empty");
        if (!await DatabaseHelper.PasswordExistsAsync(req.Password))
            return Unauthorized("Invalid password");
        return null;
    }

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

    private const string VET_GUIDELINES = """
Veterinary Quick Reference:
1. Common acute abdomen causes: GI foreign body, GDV, septic peritonitis, intestinal obstruction.
2. Key parvovirus signs: hemorrhagic diarrhea, low WBC, vomiting, positive parvo test.
3. Sepsis often includes tachycardia, hypotension (shock), fever, bounding pulses or weak pulses.
4. Surgical emergencies: GDV, ruptured GI tract, severe hemorrhage, septic peritonitis, foreign object that can't pass.
5. Always confirm negative test results before excluding infectious diseases.
INTERNAL REASONING: reason step-by-step privately; do not reveal chain-of-thought.
""";

    private static readonly string SYS_EXTRACT = $"""
You are VetGPT, an expert medical scribe. Use the guidelines below to inform your extraction:
{VET_GUIDELINES}
Task: read the raw veterinary record below and output a JSON object with:
  • signalment
  • imaging
  • surgical_procedures
  • test_results
  • treatments
  • other_clinical_clues
Only include information explicitly present in the text. Omit null fields.
Return via the extract_features function.
""";

    private static readonly string SYS_DIAGNOSE = $"""
You are a veterinary clinical-decision assistant. Refer to the guidelines below:
{VET_GUIDELINES}
Rules:
1. Propose 1-5 plausible differential diagnoses.
2. EACH diagnosis must be backed by ≥2 distinct positive findings.
3. Assume a test is NEGATIVE unless stated otherwise.
4. If imaging + surgery + broad-spectrum IV antibiotics all appear,
   give higher confidence to surgical conditions.
5. Down-rank or discard diagnoses whose required test is negative.
INTERNAL REASONING: reason step-by-step privately, do not show chain-of-thought in final answer.
Return JSON via the diagnose function with fields:
  diagnosis, confidence (0-1), reason.
""";

    private const string META_REVIEW = """
You are a senior veterinary reviewer supervising an AI assistant.
Tasks:
1. Review the ORIGINAL VETERINARY RECORD and the AI's INITIAL differential JSON.
2. Identify missing or mis-prioritized diagnoses, or key treatments (especially antibiotic or surgical) that may be missing or incorrectly recommended.
3. Produce an improved differential: max 5 diagnoses, each with ≥2 explicit findings from the note and reasons for confidence. Keep confidence 0-1.
4. Increase confidence for surgical emergencies (e.g. GDV, foreign body, septic peritonitis) if test results, imaging, or surgeries were done.
5. Down-rank diagnoses contradicted by negative tests (e.g. parvo test negative).
6. Keep identical JSON schema (diagnoses[ diagnosis, confidence, reason ]).
Return ONLY the JSON via the diagnose function. Do NOT add explanatory prose.
""";

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
            Parameters = BinaryData.FromString(
            """
{
  "type": "object",
  "properties": {
    "signalment": {"type": "string"},
    "imaging": {"type": "array", "items": {"type": "string"}},
    "surgical_procedures": {"type": "array", "items": {"type": "string"}},
    "test_results": {"type": "object"},
    "treatments": {"type": "array", "items": {"type": "string"}},
    "other_clinical_clues": {"type": "array", "items": {"type": "string"}}
  }
}
""")
        });

    private static readonly ChatCompletionsFunctionToolDefinition DIAGNOSE_TOOL_VET =
        new(new FunctionDefinition("diagnose")
        {
            Description = "Return possible veterinary diagnoses.",
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
          "reason": {"type": "string"}
        },
        "required": ["diagnosis", "confidence", "reason"]
      },
      "minItems": 1,
      "maxItems": 5
    }
  },
  "required": ["diagnoses"]
}
""")
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
        if (await ValidateRequestAsync(req) is ActionResult error)
            return error;
        try
        {
            var summaryJson = await CallOpenAiAsync(
                SYS_EXTRACT,
                req.Text,
                tool: EXTRACT_TOOL_VET,
                functionName: "extract_features");
            using var summaryDoc = JsonDocument.Parse(summaryJson);
            var diagJson = await CallOpenAiAsync(
                SYS_DIAGNOSE,
                summaryJson,
                0.3f,
                DIAGNOSE_TOOL_VET,
                "diagnose");
            var preds = JsonSerializer.Deserialize<DiagnosisResponse>(diagJson)?.Diagnoses ?? new();
            var refineJson = await CallOpenAiAsync(
                META_REVIEW,
                $"ORIGINAL_VETERINARY_RECORD:\n{req.Text}\n\nINITIAL_DIFFERENTIAL_JSON:\n{JsonSerializer.Serialize(new DiagnosisResponse{ Diagnoses = preds })}",
                0.2f,
                DIAGNOSE_TOOL_VET,
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
