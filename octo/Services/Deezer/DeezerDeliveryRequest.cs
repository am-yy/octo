using System.Globalization;

namespace Octo.Services.Deezer;

/// <summary>The finite set of client output profiles supported by progressive playback.</summary>
public sealed record DeezerDeliveryRequest(string? Codec, int BitrateKbps, double Offset)
{
    private static readonly int[] Mp3Rates = [32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
    public string ContentType(string sourceType) => Codec switch
    {
        "mp3" => "audio/mpeg", "opus" => "audio/ogg", _ => sourceType,
    };

    public static DeezerDeliveryRequest Parse(IReadOnlyDictionary<string, string> parameters,
        string sourceQuality, bool download = false)
    {
        if (download) return new(null, 0, 0);
        var format = parameters.GetValueOrDefault("format", "").Trim().ToLowerInvariant();
        var cap = 0;
        if (parameters.TryGetValue("maxBitRate", out var rate)
            && (!int.TryParse(rate, NumberStyles.None, CultureInfo.InvariantCulture, out cap) || cap < 0 || cap > 512))
            throw new ArgumentException("maxBitRate must be an integer between 0 and 512 kbps.");
        var offset = 0d;
        if (parameters.TryGetValue("timeOffset", out var time)
            && (!double.TryParse(time, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out offset)
                || !double.IsFinite(offset) || offset < 0 || offset > 86400))
            throw new ArgumentException("timeOffset must be between 0 and 86400 seconds.");
        string? codec = format switch
        {
            "raw" => null,
            "" => cap > 0 ? "mp3" : null,
            "mp3" => "mp3",
            "opus" or "ogg" => "opus",
            _ => throw new ArgumentException("format must be raw, mp3, or opus."),
        };
        if (codec is null)
        {
            if (offset > 0) throw new ArgumentException("timeOffset requires MP3 or Opus transcoding.");
            return new(null, 0, 0);
        }
        var bitrate = cap > 0 ? cap : 128;
        if (codec == "mp3")
        {
            bitrate = Mp3Rates.LastOrDefault(value => value <= bitrate);
            if (bitrate == 0) throw new ArgumentException("MP3 requires a bitrate cap of at least 32 kbps.");
            if (sourceQuality == "MP3_320" && bitrate == 320 && offset == 0)
                return new(null, 0, 0);
        }
        else if (bitrate < 6) throw new ArgumentException("Opus requires a bitrate cap of at least 6 kbps.");
        return new(codec, bitrate, offset);
    }
}
