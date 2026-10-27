using System.Globalization;
using System.Resources;

namespace EliteRec.App.Resources;

public static class Text
{
    private static readonly ResourceManager Resources = new("EliteRec.App.Resources.Strings", typeof(Text).Assembly);
    public static string Get(string key) => Resources.GetString(key, CultureInfo.CurrentUICulture) ?? key;
    public static string You => Get(nameof(You));
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
}
