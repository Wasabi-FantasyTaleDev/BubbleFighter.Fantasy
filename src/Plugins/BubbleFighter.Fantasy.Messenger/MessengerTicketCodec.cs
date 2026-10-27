using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BubbleFighter.Fantasy.Messenger;

/// <summary>The assertions a messenger ticket carries.</summary>
/// <param name="Tenant">Issuing tenant key.</param>
/// <param name="Subject">Tenant account key.</param>
/// <param name="Nickname">Display nickname.</param>
/// <param name="GameCode">Game and region code of the launched client.</param>
/// <param name="Build">Launched client build.</param>
/// <param name="IssuedAt">Unix seconds of issue.</param>
/// <param name="ExpiresAt">Unix seconds of expiry.</param>
/// <param name="Id">Random single-use identifier.</param>
internal sealed record MessengerTicketClaims(
    [property: JsonPropertyName("iss")] string Tenant,
    [property: JsonPropertyName("sub")] string Subject,
    [property: JsonPropertyName("nick")] string Nickname,
    [property: JsonPropertyName("game")] uint GameCode,
    [property: JsonPropertyName("build")] string Build,
    [property: JsonPropertyName("iat")] long IssuedAt,
    [property: JsonPropertyName("exp")] long ExpiresAt,
    [property: JsonPropertyName("jti")] string Id);

/// <summary>
/// Ticket text format <c>v1.&lt;claims&gt;.&lt;signature&gt;</c>: base64url JSON claims and a base64url ECDSA P-256
/// SHA-256 signature (IEEE P1363) over the ASCII prefix <c>v1.&lt;claims&gt;</c>. Asymmetric signatures let a messenger on
/// another host verify a server's tickets without being able to mint them.
/// </summary>
internal static class MessengerTicketCodec
{
    internal const int MaxTicketLength = 2048;
    private const string Version = "v1.";

    internal static string Sign(ECDsa key, MessengerTicketClaims claims)
    {
        var body = Version + Base64Url(JsonSerializer.SerializeToUtf8Bytes(claims));
        var signature = key.SignData(Encoding.ASCII.GetBytes(body), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var ticket = body + "." + Base64Url(signature);
        return ticket.Length <= MaxTicketLength ? ticket : throw new InvalidOperationException("Messenger ticket exceeds its length bound.");
    }

    /// <summary>Returns the claims when the ticket is well formed and signed by the key of the tenant it names; expiry is checked by the caller.</summary>
    internal static MessengerTicketClaims? Verify(string ticket, Func<string, ECDsa?> keyForTenant)
    {
        if (ticket.Length is 0 or > MaxTicketLength || !ticket.StartsWith(Version, StringComparison.Ordinal)) return null;
        var dot = ticket.LastIndexOf('.');
        if (dot <= Version.Length) return null;
        var body = ticket[..dot];
        try
        {
            var claims = JsonSerializer.Deserialize<MessengerTicketClaims>(FromBase64Url(body[Version.Length..]));
            if (claims is null || claims.Tenant is null || claims.Subject is null || claims.Nickname is null || claims.Build is null || claims.Id is null)
                return null;
            var key = keyForTenant(claims.Tenant);
            if (key is null) return null;
            var signature = FromBase64Url(ticket[(dot + 1)..]);
            // One key object serves every concurrent sign-in of its tenant.
            lock (key)
                return key.VerifyData(Encoding.ASCII.GetBytes(body), signature, HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation) ? claims : null;
        }
        catch (Exception e) when (e is FormatException or JsonException or CryptographicException)
        {
            return null;
        }
    }

    /// <summary>Imports a P-256 key from PEM text or base64 DER (SubjectPublicKeyInfo, PKCS#8 or SEC1).</summary>
    internal static ECDsa ImportKey(string text, bool requirePrivate)
    {
        var key = ECDsa.Create();
        try
        {
            if (text.Contains("-----BEGIN", StringComparison.Ordinal)) key.ImportFromPem(text);
            else
            {
                var der = Convert.FromBase64String(text.Trim());
                if (requirePrivate) key.ImportPkcs8PrivateKey(der, out _);
                else key.ImportSubjectPublicKeyInfo(der, out _);
            }
            var parameters = key.ExportParameters(false);
            if (parameters.Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
                throw new CryptographicException("Messenger keys must use the P-256 curve.");
            if (requirePrivate) key.ExportParameters(true);
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    private static string Base64Url(ReadOnlySpan<byte> data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string text)
    {
        if (text.Length == 0 || text.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))) throw new FormatException();
        var padded = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '='));
    }
}
