using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SqlFlow.Acquire.Runtime.Protection;

/// <summary>
/// The pure, stateless data-protection transforms applied to a single scalar value at landing time. Each mirrors a
/// standard de-identification primitive; keyed transforms take pre-derived key material (see
/// <see cref="ProtectionKeys"/>) so this class never touches secrets or resolves references. All methods are
/// culture-invariant and safe on empty/unicode input.
/// </summary>
internal static class PayloadTransforms
{
    /// <summary>Replace a value with a constant or a suppression pattern (full/partial/email/phone/card).</summary>
    public static string Redact(string value, IReadOnlyDictionary<string, string> p)
    {
        var mode = Param(p, "mode", "full").ToLowerInvariant();
        var maskChar = ParamChar(p, "maskChar", '*');
        return mode switch
        {
            "full" => Param(p, "replacement", "[REDACTED]"),
            "partial" => Partial(value, ParamInt(p, "keepFirst", 0), ParamInt(p, "keepLast", 0), maskChar),
            "email" => RedactEmail(value, maskChar),
            "phone" => KeepLastDigits(value, 4, maskChar),
            "card" => KeepLastDigits(value, 4, maskChar),
            _ => throw new ArgumentException($"unknown redact mode '{mode}' (full|partial|email|phone|card)."),
        };
    }

    /// <summary>Keep the first <c>show</c> (and optional last <c>showLast</c>) characters, mask the rest.</summary>
    public static string Mask(string value, IReadOnlyDictionary<string, string> p)
    {
        var show = ParamInt(p, "show", 4);
        var showLast = ParamInt(p, "showLast", 0);
        return Partial(value, show, showLast, ParamChar(p, "maskChar", '*'));
    }

    /// <summary>Unkeyed one-way SHA-256 fingerprint (lowercase hex).</summary>
    public static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    /// <summary>Keyed one-way pseudonym: HMAC-SHA256 (default) or PBKDF2-SHA256, truncated to <c>outputLength</c> hex chars.</summary>
    public static string Hmac(string value, byte[] key, IReadOnlyDictionary<string, string> p)
    {
        var algorithm = Param(p, "algorithm", "hmac_sha256").ToLowerInvariant();
        byte[] digest = algorithm switch
        {
            "hmac_sha256" => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value)),
            "pbkdf2" => Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(value), key, ParamInt(p, "iterations", 100_000), HashAlgorithmName.SHA256, 32),
            _ => throw new ArgumentException($"unknown hmac algorithm '{algorithm}' (hmac_sha256|pbkdf2)."),
        };

        var hex = Convert.ToHexString(digest).ToLowerInvariant();
        var length = ParamInt(p, "outputLength", hex.Length);
        return length > 0 && length < hex.Length ? hex[..length] : hex;
    }

    /// <summary>
    /// Reversible deterministic authenticated encryption: AES-256-GCM with a synthetic nonce derived as an HMAC of
    /// the plaintext (the SIV construction), so the same plaintext+key always yields the same ciphertext (joins
    /// survive) while the key holder can decrypt. Output is base64url of <c>nonce(12) || tag(16) || ciphertext</c>.
    /// </summary>
    public static string Encrypt(string value, ProtectionKeys keys)
    {
        var plaintext = Encoding.UTF8.GetBytes(value);
        var nonce = new byte[AesGcm.NonceByteSizes.MaxSize];
        HMACSHA256.HashData(keys.NonceKey, plaintext).AsSpan(0, nonce.Length).CopyTo(nonce);

        var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        var ciphertext = new byte[plaintext.Length];
        using var gcm = new AesGcm(keys.CipherKey, tag.Length);
        gcm.Encrypt(nonce, plaintext, ciphertext, tag);

        var packed = new byte[nonce.Length + tag.Length + ciphertext.Length];
        nonce.CopyTo(packed, 0);
        tag.CopyTo(packed, nonce.Length);
        ciphertext.CopyTo(packed, nonce.Length + tag.Length);
        return Base64Url(packed);
    }

    /// <summary>Decrypt a value produced by <see cref="Encrypt"/> with the same key material. For verification/round-trip.</summary>
    public static string Decrypt(string token, ProtectionKeys keys)
    {
        var packed = FromBase64Url(token);
        var nonceLen = AesGcm.NonceByteSizes.MaxSize;
        var tagLen = AesGcm.TagByteSizes.MaxSize;
        if (packed.Length < nonceLen + tagLen)
        {
            throw new ArgumentException("ciphertext is too short to be a valid encrypted value.");
        }

        var nonce = packed.AsSpan(0, nonceLen);
        var tag = packed.AsSpan(nonceLen, tagLen);
        var ciphertext = packed.AsSpan(nonceLen + tagLen);
        var plaintext = new byte[ciphertext.Length];
        using var gcm = new AesGcm(keys.CipherKey, tagLen);
        gcm.Decrypt(nonce, ciphertext, tag, plaintext);
        return Encoding.UTF8.GetString(plaintext);
    }

    /// <summary>Reduce precision: date to year/month/quarter/decade, an age to a bucket range, a zip to a prefix, or a number rounded.</summary>
    public static string Generalize(string value, IReadOnlyDictionary<string, string> p)
    {
        var mode = Param(p, "mode", "year").ToLowerInvariant();
        switch (mode)
        {
            case "year":
            case "month":
            case "quarter":
            case "decade":
                var date = ParseDate(value);
                return mode switch
                {
                    "year" => date.Year.ToString(CultureInfo.InvariantCulture),
                    "month" => $"{date.Year:D4}-{date.Month:D2}",
                    "quarter" => $"{date.Year}Q{(date.Month - 1) / 3 + 1}",
                    _ => $"{date.Year / 10 * 10}s",
                };
            case "age_range":
                var bucket = Math.Max(1, ParamInt(p, "bucket", 5));
                var dob = ParseDate(value);
                var today = DateTime.UtcNow.Date;
                var age = today.Year - dob.Year - (today.DayOfYear < dob.DayOfYear ? 1 : 0);
                age = Math.Max(0, age);
                var lower = age / bucket * bucket;
                return $"{lower}-{lower + bucket - 1}";
            case "zip3":
            case "zip2":
                var take = mode == "zip3" ? 3 : 2;
                var digits = new string(value.Where(char.IsAsciiDigit).ToArray());
                return digits.Length >= take ? digits[..take] + new string('X', 5 - take) : new string('X', 5);
            case "round":
                var step = ParseDecimal(Param(p, "step", "1"));
                if (step <= 0)
                {
                    throw new ArgumentException("generalize round requires a positive 'step'.");
                }

                var number = ParseDecimal(value);
                return (Math.Round(number / step, MidpointRounding.AwayFromZero) * step).ToString(CultureInfo.InvariantCulture);
            default:
                throw new ArgumentException($"unknown generalize mode '{mode}' (year|month|quarter|decade|age_range|zip3|zip2|round).");
        }
    }

    private static string Partial(string value, int keepFirst, int keepLast, char maskChar)
    {
        var runes = value.EnumerateRunes().ToArray();
        var len = runes.Length;
        if (keepFirst < 0 || keepLast < 0)
        {
            throw new ArgumentException("mask keep counts must be non-negative.");
        }

        if (len <= keepFirst + keepLast)
        {
            return new string(maskChar, len);
        }

        var builder = new StringBuilder(len);
        for (var i = 0; i < keepFirst; i++)
        {
            builder.Append(runes[i].ToString());
        }

        builder.Append(maskChar, len - keepFirst - keepLast);
        for (var i = len - keepLast; i < len; i++)
        {
            builder.Append(runes[i].ToString());
        }

        return builder.ToString();
    }

    private static string RedactEmail(string value, char maskChar)
    {
        var at = value.IndexOf('@', StringComparison.Ordinal);
        if (at <= 0)
        {
            return new string(maskChar, Math.Max(value.Length, 8));
        }

        var domain = value[at..];
        return $"{value[0]}{new string(maskChar, Math.Max(at - 1, 3))}{domain}";
    }

    private static string KeepLastDigits(string value, int keep, char maskChar)
    {
        var digits = new string(value.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length <= keep)
        {
            return new string(maskChar, value.Length);
        }

        return new string(maskChar, digits.Length - keep) + digits[^keep..];
    }

    private static DateTime ParseDate(string value)
        => DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date)
            ? date
            : throw new ArgumentException($"generalize could not parse '{value}' as a date.");

    private static decimal ParseDecimal(string value)
        => decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var number)
            ? number
            : throw new ArgumentException($"generalize could not parse '{value}' as a number.");

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch { 2 => padded + "==", 3 => padded + "=", _ => padded };
        return Convert.FromBase64String(padded);
    }

    private static string Param(IReadOnlyDictionary<string, string> p, string key, string fallback)
        => p.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : fallback;

    private static int ParamInt(IReadOnlyDictionary<string, string> p, string key, int fallback)
        => p.TryGetValue(key, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;

    private static char ParamChar(IReadOnlyDictionary<string, string> p, string key, char fallback)
        => p.TryGetValue(key, out var v) && v.Length > 0 ? v[0] : fallback;
}
