namespace SqlFlow.Acquire.Runtime.Protection;

/// <summary>The derived key material for the reversible <c>encrypt</c> transform: an independent cipher key and a
/// nonce-derivation key, both produced by HKDF from the rule's resolved secret with domain separation, so the two
/// are never the same bytes.</summary>
internal readonly record struct ProtectionKeys(byte[] CipherKey, byte[] NonceKey);
