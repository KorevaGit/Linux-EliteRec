using System.Text.Json;
using System.Text.Json.Serialization;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace EliteRec.Updates;

public sealed class ReleaseException(string message) : Exception(message);

/// <summary>
/// Подписанный манифест выпуска на канале — тот же формат, что у EliteSIP (PanelLink.ReleaseManifest):
/// конверт <c>{"payload": base64, "signature": base64}</c>, подпись Ed25519 по байтам payload, внутри
/// format, version, url, sha256, size, published_at, notes. Ключ подписи — свой у линии EliteRec.
/// </summary>
public sealed record ReleaseManifest
{
    public const int SupportedFormat = 1;
    public required int Format { get; init; }
    public required Version Version { get; init; }
    public required Uri Url { get; init; }
    public required string Sha256 { get; init; }
    public long Size { get; init; }
    public DateTimeOffset PublishedAt { get; init; }
    public string Notes { get; init; } = "";

    /// <summary>Проверяет подпись и разбирает. Любое несоответствие — <see cref="ReleaseException"/>.</summary>
    public static ReleaseManifest Verified(byte[] data, string publicKeyBase64)
    {
        byte[] payload = Open(data, publicKeyBase64);
        Wire? wire;
        try { wire = JsonSerializer.Deserialize<Wire>(payload); }
        catch (JsonException) { throw new ReleaseException("манифест не разбирается"); }
        if (wire?.Version is null || wire.Url is null || wire.Sha256 is null) throw new ReleaseException("в манифесте нет версии, адреса или отпечатка");
        if (wire.Format > SupportedFormat) throw new ReleaseException($"формат манифеста {wire.Format} новее понятного ({SupportedFormat})");
        if (!Version.TryParse(wire.Version, out var version) || version.Build < 0) throw new ReleaseException("версия — не три числа");
        // Только https: адрес в манифесте подписан, но канал до установщика — нет.
        if (!Uri.TryCreate(wire.Url, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps) throw new ReleaseException("адрес пакета не https");
        if (wire.Sha256.Length != 64 || !wire.Sha256.All(Uri.IsHexDigit)) throw new ReleaseException("отпечаток — не SHA-256");
        return new ReleaseManifest
        {
            Format = wire.Format, Version = version, Url = url, Sha256 = wire.Sha256.ToLowerInvariant(), Size = wire.Size,
            PublishedAt = DateTimeOffset.TryParse(wire.PublishedAt, out var published) ? published : default, Notes = wire.Notes ?? "",
        };
    }

    /// <summary>Строго новее установленного: равная — та же сборка, меньшая — откат, его делает человек.</summary>
    public bool IsNewerThan(Version installed) => Version > installed;

    /// <summary>
    /// Пакет — на том же хосте, что манифест (пара Basic общая — чужому хосту её не отдаём), и именно
    /// пакет EliteRec в каталоге EliteRec: манифест EliteSIP с чужим адресом сюда не пройдёт.
    /// </summary>
    public bool IsServedFrom(Uri manifestAddress) =>
        Uri.Compare(Url, manifestAddress, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) == 0
        && Url.AbsolutePath == "/" + ReleaseChannel.PackageKey(Version);

    internal static byte[] Open(byte[] data, string publicKeyBase64)
    {
        Envelope? envelope;
        try { envelope = JsonSerializer.Deserialize<Envelope>(data); }
        catch (JsonException) { throw new ReleaseException("конверт манифеста не разбирается"); }
        Span<byte> key = stackalloc byte[32];
        if (!Convert.TryFromBase64String(publicKeyBase64.Trim(), key, out int written) || written != 32) throw new ReleaseException("испорченный открытый ключ");
        byte[] payload, signature;
        try
        {
            payload = Convert.FromBase64String(envelope?.Payload ?? throw new ReleaseException("в конверте нет payload"));
            signature = Convert.FromBase64String(envelope.Signature ?? throw new ReleaseException("в конверте нет подписи"));
        }
        catch (FormatException) { throw new ReleaseException("конверт не в base64"); }
        var verifier = new Ed25519Signer();
        verifier.Init(forSigning: false, new Ed25519PublicKeyParameters(key.ToArray(), 0));
        verifier.BlockUpdate(payload, 0, payload.Length);
        if (signature.Length != 64 || !verifier.VerifySignature(signature)) throw new ReleaseException("подпись не сошлась");
        return payload;
    }

    private sealed class Envelope
    {
        [JsonPropertyName("payload")] public string? Payload { get; init; }
        [JsonPropertyName("signature")] public string? Signature { get; init; }
    }

    private sealed class Wire
    {
        [JsonPropertyName("format")] public int Format { get; init; }
        [JsonPropertyName("version")] public string? Version { get; init; }
        [JsonPropertyName("url")] public string? Url { get; init; }
        [JsonPropertyName("sha256")] public string? Sha256 { get; init; }
        [JsonPropertyName("size")] public long Size { get; init; }
        [JsonPropertyName("published_at")] public string? PublishedAt { get; init; }
        [JsonPropertyName("notes")] public string? Notes { get; init; }
    }
}

/// <summary>Раскладка линии EliteRec на канале (сервер elitesip_update, docs/contract.md).</summary>
public static class ReleaseChannel
{
    public const string ManifestKey = "releases/eliterec/linux/current.json";
    public static string PackageKey(Version version) => $"releases/eliterec/linux/{PackageName(version)}";
    public static string PackageName(Version version) => $"EliteRec-{version.ToString(3)}.deb";
}
