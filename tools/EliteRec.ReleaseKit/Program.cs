using System.Security.Cryptography;
using System.Text.Json;
using EliteRec.Updates;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

// Инструмент выпуска EliteRec — как releasekit EliteSIP: ключи линии, подпись манифеста и его проверка
// кодом обновляльщика. Закрытый ключ в репозиторий не кладётся никогда.
return args switch
{
    ["keygen", var directory] => Keygen(directory),
    ["sign", var manifest, var key, var output] => Sign(manifest, key, output),
    ["verify", var envelope, var publicKey, var address] => Verify(envelope, publicKey, address),
    ["hash", var file] => Hash(file),
    ["public", var key] => PublicOf(key),
    _ => Usage(),
};

static int Usage()
{
    Console.Error.WriteLine("""
        releasekit — подпись линии выпусков EliteRec.

          keygen <каталог>                              новая пара ключей линии
          sign <манифест.json> <ключ> <выход>           подписать манифест
          verify <конверт.json> <ключ.pub> <адрес>      проверить кодом обновляльщика (адрес манифеста на канале)
          hash <файл>                                   SHA-256 файла, шестнадцатеричный
          public <ключ>                                 открытая половина ключа, base64
        """);
    return 2;
}

static int Keygen(string directory)
{
    Directory.CreateDirectory(directory);
    string privatePath = Path.Combine(directory, "releases.key"), publicPath = Path.Combine(directory, "releases.pub");
    if (File.Exists(privatePath)) { Console.Error.WriteLine($"{privatePath} уже есть — старый ключ не перезаписывается"); return 1; }
    var key = new Ed25519PrivateKeyParameters(new SecureRandom());
    File.WriteAllText(privatePath, Convert.ToBase64String(key.GetEncoded()));
    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(privatePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    File.WriteAllText(publicPath, Convert.ToBase64String(key.GeneratePublicKey().GetEncoded()));
    Console.WriteLine($"закрытый ключ: {privatePath} (0600, не коммитить)");
    Console.WriteLine($"открытый ключ: {publicPath} → releasesPublicKey в provisioning.json пакета");
    Console.WriteLine("Потеря закрытого ключа — установленные машины перестанут принимать выпуски.");
    return 0;
}

static int PublicOf(string keyPath)
{
    var key = new Ed25519PrivateKeyParameters(Convert.FromBase64String(File.ReadAllText(keyPath).Trim()), 0);
    Console.WriteLine(Convert.ToBase64String(key.GeneratePublicKey().GetEncoded()));
    return 0;
}

static int Sign(string manifestPath, string keyPath, string outputPath)
{
    byte[] payload = File.ReadAllBytes(manifestPath);
    var signer = new Ed25519Signer();
    signer.Init(forSigning: true, new Ed25519PrivateKeyParameters(Convert.FromBase64String(File.ReadAllText(keyPath).Trim()), 0));
    signer.BlockUpdate(payload, 0, payload.Length);
    File.WriteAllText(outputPath, JsonSerializer.Serialize(new
    {
        payload = Convert.ToBase64String(payload),
        signature = Convert.ToBase64String(signer.GenerateSignature()),
    }));
    Console.WriteLine($"подписано: {outputPath}");
    return 0;
}

static int Verify(string envelopePath, string publicKeyPath, string address)
{
    try
    {
        var manifest = ReleaseManifest.Verified(File.ReadAllBytes(envelopePath), File.ReadAllText(publicKeyPath));
        if (!manifest.IsServedFrom(new Uri(address))) throw new ReleaseException($"пакет {manifest.Url} не в каталоге EliteRec канала {address}");
        Console.WriteLine($"версия:    {manifest.Version.ToString(3)}");
        Console.WriteLine($"пакет:     {manifest.Url}");
        Console.WriteLine($"отпечаток: {manifest.Sha256}");
        Console.WriteLine($"размер:    {manifest.Size} байт");
        Console.WriteLine("проверено кодом обновляльщика.");
        return 0;
    }
    catch (ReleaseException error)
    {
        Console.Error.WriteLine($"манифест не прошёл проверку: {error.Message}");
        return 1;
    }
}

static int Hash(string path)
{
    using var file = File.OpenRead(path);
    Console.WriteLine(Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant());
    return 0;
}
