namespace SqlFlow.Core.Abstractions;

/// <summary>
/// Decides, in the target dialect's type system, whether an existing target column must widen to
/// accommodate a desired type. Returns the widened SQL type the column should be altered to, or null when
/// the existing type already fits (or the change is incompatible and must be left alone). This keeps the
/// pure, dialect-agnostic schema diff able to widen columns without embedding SQL Server type knowledge.
/// </summary>
public interface IColumnTypeReconciler
{
    /// <summary>The widened SQL type to ALTER <paramref name="existingType"/> to so it accepts
    /// <paramref name="desiredType"/>, or null when the existing type already accommodates the desired one
    /// (no change) or the two are incompatible (left alone; never narrowed or force-changed).</summary>
    string? WidenTo(string existingType, string desiredType);
}
