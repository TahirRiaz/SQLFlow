namespace SqlFlow.Core.Secrets;

/// <summary>
/// Read/write access to a single secret store (e.g. one Azure Key Vault), addressed by secret name. This is the
/// write-capable companion to <see cref="ISecretProvider"/> (which only resolves <c>${scheme:locator}</c>
/// references on the read path). It is what a migration or provisioning step uses to push a secret into a vault
/// and then store only a <c>${...}</c> reference to it, so a secret value never rests in the control database.
/// The vault is bound at construction; operations are by secret name within that vault.
/// </summary>
public interface ISecretVault
{
    /// <summary>Reads the current value of a secret, or null if no secret with that name exists.</summary>
    Task<string?> GetSecretAsync(string name, CancellationToken ct = default);

    /// <summary>Creates the secret or adds a new version, returning the new version identifier.</summary>
    Task<string> SetSecretAsync(string name, string value, CancellationToken ct = default);

    /// <summary>Begins deleting a secret. Returns true if it existed, false if there was nothing to delete.</summary>
    Task<bool> DeleteSecretAsync(string name, CancellationToken ct = default);
}
