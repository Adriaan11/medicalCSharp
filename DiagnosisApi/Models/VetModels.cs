namespace DiagnosisApi.Models;

public class DiagnosisRequest
{
    public string Password { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
}

public class DiagnosisItem
{
    public string Diagnosis { get; set; } = string.Empty;
    public double Confidence { get; set; }
    public string Reason { get; set; } = string.Empty;
}

public class DiagnosisResponse
{
    public List<DiagnosisItem> Diagnoses { get; set; } = new();
}
