using SqlFlow.Core.Model;

namespace SqlFlow.Core.Abstractions;

/// <summary>Generates the DDL that realizes a <see cref="SchemaDelta"/> against a target.</summary>
public interface IDdlGenerator
{
    IReadOnlyList<string> Generate(TargetSpec target, SchemaDelta delta);
}
