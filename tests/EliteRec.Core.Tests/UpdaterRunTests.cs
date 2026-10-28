using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using EliteRec.Updater;
using EliteRec.Updates;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
using Xunit;

namespace EliteRec.Core.Tests;

public class UpdaterRunTests
{
    private sealed class Machine : IMachine
    {
        public Version Installed = new(0, 1, 0);
        public bool InstallSucceeds = true;
        public HashSet<int> Recording = [];
        public List<string> Calls = [];
        public string? MarkerSeenAtInstall;
        public Func<string?> Marker = () => null;
        public Version? InstalledVersion() => Installed;
        public bool IsEliteRec(int processId) => Recording.Contains(processId);
        public IReadOnlyList<RunningApp> RunningApps() => [new RunningApp(77, "manager", new Dictionary<string, string> { ["DISPLAY"] = ":0" })];
        public void Stop(IReadOnlyList<RunningApp> apps) => Calls.Add("stop " + apps[0].User);
        public bool Install(string package, out string output)
        {
            Calls.Add("install " + Path.GetFileName(package)); MarkerSeenAtInstall = Marker(); output = ""; return InstallSucceeds;
        }
        public void Relaunch(IReadOnlyList<RunningApp> apps) => Calls.Add("relaunch " + apps[0].User);
    }

    private sealed class Channel(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        public List<string> Requests = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.AbsolutePath);
            Assert.Equal("Basic", request.Headers.Authorization?.Scheme);
            return Task.FromResult(files.TryGetValue(request.RequestUri.AbsolutePath, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static (UpdaterRun Run, Machine Machine, Channel Channel, UpdateCoordination Coordination, string Root) Setup(
        string version = "0.1.1", byte[]? served = null)
    {
        string root = Directory.CreateTempSubdirectory("eliterec-updater-").FullName;
        var key = new Ed25519PrivateKeyParameters(new SecureRandom());
        byte[] package = "пакет EliteRec"u8.ToArray();
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = 1, version, url = $"https://update.test:8081/releases/eliterec/linux/EliteRec-{version}.deb",
            sha256 = Convert.ToHexString(SHA256.HashData(package)), size = package.Length, published_at = "2026-10-28T12:00:00Z", notes = "",
        });
        var signer = new Ed25519Signer();
        signer.Init(true, key);
        signer.BlockUpdate(payload, 0, payload.Length);
        byte[] envelope = JsonSerializer.SerializeToUtf8Bytes(new { payload = Convert.ToBase64String(payload), signature = Convert.ToBase64String(signer.GenerateSignature()) });
        var channel = new Channel(new()
        {
            ["/releases/eliterec/linux/current.json"] = envelope,
            [$"/releases/eliterec/linux/EliteRec-{version}.deb"] = served ?? package,
        });
        var coordination = new UpdateCoordination(Directory.CreateDirectory(Path.Combine(root, "updates")).FullName, Path.Combine(root, "installing"));
        var machine = new Machine();
        machine.Marker = () => File.Exists(coordination.InstallingMarkerPath) ? "есть" : null;
        var provisioning = new Provisioning(new Uri("https://update.test:8081/"), "elitesip", "secret", Convert.ToBase64String(key.GeneratePublicKey().GetEncoded()));
        var run = new UpdaterRun(provisioning, machine, new HttpClient(channel), coordination, Directory.CreateDirectory(Path.Combine(root, "state")).FullName, _ => { });
        return (run, machine, channel, coordination, root);
    }

    [Fact]
    public async Task NewReleaseClosesInstallsAndRelaunchesUnderInstallingMarker()
    {
        var (run, machine, _, coordination, root) = Setup();
        try
        {
            Assert.Equal(Outcome.Installed, await run.RunAsync(default));
            Assert.Equal(["stop manager", "install EliteRec-0.1.1.deb", "relaunch manager"], machine.Calls);
            Assert.Equal("есть", machine.MarkerSeenAtInstall); // приложение не начнёт запись посреди установки
            Assert.False(File.Exists(coordination.InstallingMarkerPath));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RecordingDefersUpdateWithoutTouchingTheApp()
    {
        var (run, machine, _, coordination, root) = Setup();
        try
        {
            Assert.True(new UpdateCoordination(Path.Combine(root, "updates"), coordination.InstallingMarkerPath).TryBeginRecording(4242));
            machine.Recording.Add(4242);
            Assert.Equal(Outcome.Deferred, await run.RunAsync(default));
            Assert.Empty(machine.Calls);
            Assert.False(File.Exists(coordination.InstallingMarkerPath)); // запись после отложенной установки не блокируется
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task TamperedPackageIsNeverInstalled()
    {
        var (run, machine, _, _, root) = Setup(served: "подменённый пакет"u8.ToArray());
        try
        {
            Assert.Equal(Outcome.Failed, await run.RunAsync(default));
            Assert.Empty(machine.Calls);
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "state")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task SameVersionDoesNotDownload()
    {
        var (run, machine, channel, _, root) = Setup(version: "0.1.0");
        try
        {
            Assert.Equal(Outcome.UpToDate, await run.RunAsync(default));
            Assert.Equal(["/releases/eliterec/linux/current.json"], channel.Requests);
            Assert.Empty(machine.Calls);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task FailedInstallStillBringsTheOldAppBack()
    {
        var (run, machine, _, _, root) = Setup();
        machine.InstallSucceeds = false;
        try
        {
            Assert.Equal(Outcome.Failed, await run.RunAsync(default));
            Assert.Equal("relaunch manager", machine.Calls[^1]);
        }
        finally { Directory.Delete(root, true); }
    }
}
