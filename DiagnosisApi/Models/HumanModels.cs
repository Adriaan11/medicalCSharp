namespace DiagnosisApi.Models;

public class HumanDiagnosisRequest
{
    public string Password { get; set; } = string.Empty;
    public string ClinicalNotes { get; set; } = string.Empty;
}

public class HumanDiagnosisItem
{
    public string Diagnosis { get; set; } = string.Empty;
    public double Confidence { get; set; }
    public string Reason { get; set; } = string.Empty;
    public List<string> Treatments { get; set; } = new();
}

public class HumanDiagnosisResponse
{
    public List<HumanDiagnosisItem> Diagnoses { get; set; } = new();
}

public class HumanInvestigationRequest
{
    public string Password { get; set; } = string.Empty;
    public string ClinicalNotes { get; set; } = string.Empty;
    public string Diagnosis { get; set; } = string.Empty;
}

public class HumanInvestigationResponse
{
    public List<string> Investigations { get; set; } = new();
}

public class HumanNonMedicalManagementRequest
{
    public string Password { get; set; } = string.Empty;
    public string ClinicalNotes { get; set; } = string.Empty;
    public string Diagnosis { get; set; } = string.Empty;
}

public class HumanNonMedicalManagementResponse
{
    public List<string> Management { get; set; } = new();
}
