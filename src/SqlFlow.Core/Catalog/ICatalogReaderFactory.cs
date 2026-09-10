using SqlFlow.Core.Connections;

namespace SqlFlow.Core.Catalog;

/// <summary>Selects the <see cref="ICatalogReader"/> for a resolved connection's provider. Throws for a kind
/// with no reader; never returns null.</summary>
public interface ICatalogReaderFactory
{
    ICatalogReader ReaderFor(ResolvedConnection connection);
}
