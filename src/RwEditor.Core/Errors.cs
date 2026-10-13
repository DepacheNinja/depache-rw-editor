namespace RwEditor.Core;

/// <summary>The editor refuses to act (forbidden folder, unknown layout, tampered locked files, invalid settings...). Nothing has been written when this is thrown.</summary>
public class EditorRefusalException : Exception
{
    public EditorRefusalException(string code, string message) : base(message) { Code = code; }
    public string Code { get; }
}

/// <summary>A write step failed. <see cref="Restored"/> says whether every touched file was put back to its bytes from before the attempt.</summary>
public sealed class ApplyFailedException : Exception
{
    public ApplyFailedException(string message, bool restored, Exception inner) : base(message, inner) { Restored = restored; }
    public bool Restored { get; }
}

public enum Severity { Error, Warning }

/// <summary>One validation finding. Errors block an apply; warnings are reported only.</summary>
public sealed record Finding(Severity Severity, string Code, string Message)
{
    public override string ToString() => $"{(Severity == Severity.Error ? "ERROR" : "WARN ")} {Code}: {Message}";
}
