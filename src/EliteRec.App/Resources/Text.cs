using System.Globalization;
using System.Resources;

namespace EliteRec.App.Resources;

public static class Text
{
    private static readonly ResourceManager Resources = new("EliteRec.App.Resources.Strings", typeof(Text).Assembly);
    public static string Get(string key) => Resources.GetString(key, CultureInfo.CurrentUICulture) ?? key;
    public static string You => Get(nameof(You));
    public static string TitleCaption => Get(nameof(TitleCaption));
    public static string TitleSave => Get(nameof(TitleSave));
    public static string SaveRecording => Get(nameof(SaveRecording));
    public static string FolderCaption => Get(nameof(FolderCaption));
    public static string FolderChange => Get(nameof(FolderChange));
    public static string FolderDefault => Get(nameof(FolderDefault));
    public static string MixCaption => Get(nameof(MixCaption));
    public static string MixHint => Get(nameof(MixHint));
    public static string SaveMono => Get(nameof(SaveMono));
    public static string FilesHint => Get(nameof(FilesHint));
    public static string Settings => Get(nameof(Settings));
    public static string SelfTest => Get(nameof(SelfTest));
    public static string SelfTestRun => Get(nameof(SelfTestRun));
    public static string Listen => Get(nameof(Listen));
    public static string ListenHint => Get(nameof(ListenHint));
    public static string Save => Get(nameof(Save));
    public static string Cancel => Get(nameof(Cancel));
    public static string ReadyToRecord => Get(nameof(ReadyToRecord));
    public static string SystemAudio => Get(nameof(SystemAudio));
    public static string ManagerChannel => Get(nameof(ManagerChannel));
    public static string ClientChannel => Get(nameof(ClientChannel));
    public static string PreviewHint => Get(nameof(PreviewHint));
    public static string StartRecording => Get(nameof(StartRecording));
    public static string RecordingUnavailable => Get(nameof(RecordingUnavailable));
    public static string AwaitingAudio => Get(nameof(AwaitingAudio));
    public static string Stop => Get(nameof(Stop));
    public static string Recover => Get(nameof(Recover));
    public static string Discard => Get(nameof(Discard));
    public static string FormatLabel => Get(nameof(FormatLabel));
    public static string OpenFolder => Get(nameof(OpenFolder));
}
