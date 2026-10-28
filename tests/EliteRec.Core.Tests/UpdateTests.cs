
using System.Text.Json;
using EliteRec.Updates;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
using Xunit;

namespace EliteRec.Core.Tests;

public class UpdateTests
{
    private static readonly Uri Channel = new("https://update.elitesip.vip:8081/releases/eliterec/linux/current.json");

    private static (Ed25519PrivateKeyParameters Key, string Public) Pair()
    {
        var key = new Ed25519PrivateKeyParameters(new SecureRandom());
        return (key, Convert.ToBase64String(key.GeneratePublicKey().GetEncoded()));
    }

    private static byte[] Envelope(Ed25519PrivateKeyParameters key, object manifest, Func<byte[], byte[]>? tamper = null)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(manifest);
        var signer = new Ed25519Signer();
        signer.Init(true, key);
        signer.BlockUpdate(payload, 0, payload.Length);
        byte[] signature = signer.GenerateSignature();
        payload = tamper?.Invoke(payload) ?? payload;
        return JsonSerializer.SerializeToUtf8Bytes(new { payload = Convert.ToBase64String(payload), signature = Convert.ToBase64String(signature) });
    }

    private static object Manifest(string version = "0.1.1", string url = "https://update.elitesip.vip:8081/releases/eliterec/linux/EliteRec-0.1.1.deb") =>
        new { format = 1, version, url, sha256 = new string('a', 64), size = 123, published_at = "2026-10-28T12:00:00Z", notes = "проба" };

    [Fact]
    public void SignedManifestIsReadAndChecksItsLine()
    {
        var (key, publicKey) = Pair();
        var manifest = ReleaseManifest.Verified(Envelope(key, Manifest()), publicKey);
        Assert.Equal(new Version(0, 1, 1), manifest.Version);
        Assert.True(manifest.IsNewerThan(new Version(0, 1, 0)));
        Assert.False(manifest.IsNewerThan(new Version(0, 1, 1))); // та же сборка
        Assert.True(manifest.IsServedFrom(Channel));
        // Пакет EliteSIP, чужой хост, чужой каталог — не наш выпуск.
        foreach (string url in new[] { "https://update.elitesip.vip:8081/releases/linux/EliteSIP-0.1.1.deb",
                     "https://evil.example/releases/eliterec/linux/EliteRec-0.1.1.deb",
                     "https://update.elitesip.vip:8081/releases/eliterec/linux/EliteRec-0.1.2.deb" })
            Assert.False(ReleaseManifest.Verified(Envelope(key, Manifest(url: url)), publicKey).IsServedFrom(Channel));
    }

    [Fact]
    public void ForgedOrForeignManifestIsRejected()
    {
        var (key, publicKey) = Pair();
        var (otherKey, _) = Pair();
        Assert.Throws<ReleaseException>(() => ReleaseManifest.Verified(Envelope(otherKey, Manifest()), publicKey)); // чужой ключ (EliteSIP)
        Assert.Throws<ReleaseException>(() => ReleaseManifest.Verified(Envelope(key, Manifest(), p =>
            System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(p).Replace("0.1.1", "9.9.9"))), publicKey)); // подменили версию
        Assert.Throws<ReleaseException>(() => ReleaseManifest.Verified(Envelope(key, Manifest(url: "http://update.elitesip.vip/x.deb")), publicKey));
        Assert.Throws<ReleaseException>(() => ReleaseManifest.Verified("не json"u8.ToArray(), publicKey));
    }

    [Fact]
    public void RecordingAndInstallingSeeEachOther()
    {
        string root = Directory.CreateTempSubdirectory("eliterec-update-").FullName;
        try
        {
            string shared = Directory.CreateDirectory(Path.Combine(root, "updates")).FullName, installing = Path.Combine(root, "installing");
            var app = new UpdateCoordination(shared, installing);
            var updater = new UpdateCoordination(shared, installing);
            // Пишем — служба видит запись и откладывает установку.
            Assert.True(app.TryBeginRecording(4242));
            Assert.Equal([4242], updater.RecordingProcesses(pid => pid == 4242));
            Assert.Empty(updater.RecordingProcesses(_ => false)); // отметка от умершего процесса не мешает
            app.EndRecording();
            Assert.Empty(updater.RecordingProcesses(_ => true));
            // Ставится обновление — запись не начинается и отметку за собой убирает.
            File.WriteAllText(installing, "");
            Assert.False(app.TryBeginRecording(4242));
            Assert.Empty(Directory.GetFiles(shared));
            // Забытая отметка установки (старше RuntimeMaxSec) записи не мешает.
            File.SetLastWriteTimeUtc(installing, DateTime.UtcNow - TimeSpan.FromHours(1));
            Assert.True(app.TryBeginRecording(4242));
            // Нет общего каталога (запуск не из пакета) — запись идёт.
            Assert.True(new UpdateCoordination(Path.Combine(root, "нет"), Path.Combine(root, "нет-отметки")).TryBeginRecording(1));
        }
        finally { Directory.Delete(root, true); }
    }
}
