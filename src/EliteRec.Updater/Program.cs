using EliteRec.Updater;
using EliteRec.Updates;

// eliterec-updater: один такт обновления (служба eliterec-updater.service, таймер — раз в 10 минут).
// Аргументов нет, как у EliteSIP; пути — пакета (docs/R6-PACKAGING.md).
const string ProvisioningPath = "/opt/eliterec/provisioning.json", StateDirectory = "/var/lib/eliterec/updater", LockPath = "/run/eliterec-updater/lock";
void Log(string message) => Console.WriteLine(message);

if (Environment.UserName != "root") { Log("обновляльщик запускается службой от root"); return 1; }
if (!File.Exists(ProvisioningPath)) { Log($"нет {ProvisioningPath} — пакет собран без канала обновлений"); return 0; }
Directory.CreateDirectory(StateDirectory);
Directory.CreateDirectory(Path.GetDirectoryName(LockPath)!);
// Одна копия: FileShare.None на Unix — это flock.
using var lockFile = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

var provisioning = Provisioning.Read(ProvisioningPath);
using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
http.DefaultRequestHeaders.UserAgent.ParseAdd($"EliteRec-Updater/{typeof(UpdaterRun).Assembly.GetName().Version?.ToString(3)} (Linux)");
var run = new UpdaterRun(provisioning, new SystemdMachine(Log), http, new UpdateCoordination(), StateDirectory, Log);
var outcome = await run.RunAsync(CancellationToken.None);
return outcome == Outcome.Failed ? 1 : 0;
