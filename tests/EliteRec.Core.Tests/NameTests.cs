using EliteRec.Core.Recording;
using EliteRec.Encoding;
using Xunit;

namespace EliteRec.Core.Tests;

public class NameTests
{
    [Fact]
    public void NewRecordingIsNamedByMinuteAndAvoidsTakenNames()
    {
        string root = Root();
        try
        {
            var start = new DateTime(2026, 10, 26, 14, 30, 59);
            string first = RecordingNames.NewBase(root, start, RecordingFormat.Mp3);
            Assert.Equal(Path.Combine(root, "2026-10-26_14-30"), first);
            File.WriteAllText(first + "_mono.mp3", ""); // занято и одним моно-файлом
            Assert.EndsWith("2026-10-26_14-30-2", RecordingNames.NewBase(root, start, RecordingFormat.Mp3));
            File.WriteAllText(Path.Combine(root, ".2026-10-26_14-30-2.mp3.part"), ""); // идёт запись — тоже занято
            Assert.EndsWith("2026-10-26_14-30-3", RecordingNames.NewBase(root, start, RecordingFormat.Mp3));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("  Иванов,  КП  ", "Иванов, КП")]
    [InlineData("ООО «Ромашка»/договор: 5?", "ООО «Ромашка» договор 5")]
    [InlineData("...", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void TitleIsSafeForFileNames(string? title, string? expected) => Assert.Equal(expected, RecordingNames.CleanTitle(title));

    [Fact]
    public void TitleRenamesPairAndEventsTogether()
    {
        string root = Root();
        try
        {
            string basePath = Path.Combine(root, "2026-10-26_14-30");
            var files = new RecordingFiles(basePath + ".mp3", basePath + "_mono.mp3");
            foreach (string path in files.All.Append(basePath + RecordingSink.EventsSuffix)) File.WriteAllText(path, path);
            var renamed = RecordingNames.AddTitle(files, "Иванов");
            Assert.Equal(Path.Combine(root, "2026-10-26_14-30_Иванов.mp3"), renamed.StereoPath);
            Assert.Equal(Path.Combine(root, "2026-10-26_14-30_Иванов_mono.mp3"), renamed.MonoPath);
            Assert.True(File.Exists(Path.Combine(root, "2026-10-26_14-30_Иванов" + RecordingSink.EventsSuffix)));
            Assert.Equal(3, Directory.GetFiles(root).Length);
            Assert.Same(renamed, RecordingNames.AddTitle(renamed, "  ")); // без названия — ничего не трогаем

            File.WriteAllText(basePath + ".mp3", ""); // по умолчанию — один стерео-файл
            var single = RecordingNames.AddTitle(new RecordingFiles(basePath + ".mp3"), "Иванов");
            Assert.Equal((Path.Combine(root, "2026-10-26_14-30_Иванов-2.mp3"), (string?)null), (single.StereoPath, single.MonoPath));
        }
        finally { Directory.Delete(root, true); }
    }

    private static string Root() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "eliterec-names-" + Guid.NewGuid())).FullName;
}
