using SqlFlow.Core.Model;

namespace SqlFlow.Core.Abstractions;

/// <summary>Maps a dialect-agnostic <see cref="SourceColumn"/> to a target column, honoring overrides.</summary>
public interface ISqlTypeMapper
{
    ColumnDefinition Map(SourceColumn column, ColumnOverride? columnOverride, string defaultColumnType);
}
