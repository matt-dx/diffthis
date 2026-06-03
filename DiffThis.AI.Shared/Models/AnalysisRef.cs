using DiffThis.AI.Shared.Services;

namespace DiffThis.AI.Shared.Models;

public enum RefCategory { Bug, LogicError, Security, Performance, Maintainability, Other }

public enum RefSeverity { Critical, High, Medium, Low, Unknown }

/// A single file/line reference extracted from an AI analysis response.
public record AnalysisRef(
    AiRunKey    RunKey,
    string      FilePath,   // raw text from the model (may be partial path)
    int?        LineFrom,
    int?        LineTo,
    RefCategory Category,
    RefSeverity Severity = RefSeverity.Unknown
);
