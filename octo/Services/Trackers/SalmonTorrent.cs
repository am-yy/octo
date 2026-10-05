using System.Security.Cryptography;
using System.Text;

namespace Octo.Services.Trackers;

/// <summary>Strict v1 torrent reader. Hashes the original info bytes, never re-encodes official torrents.</summary>
public sealed class SalmonTorrent
{
    public string InfoHash { get; }
    public string RootName { get; }
    public string Announce { get; }
    public IReadOnlyList<(string Path, long Size)> Files { get; }
    private readonly int _pieceLength;
    private readonly byte[] _pieces;

    public SalmonTorrent(byte[] bytes, string target)
    {
        if (bytes.Length is 0 or > 8 * 1024 * 1024) throw new InvalidDataException("Torrent size is invalid.");
        var reader = new Reader(bytes);
        var root = reader.Read() as Dictionary<string, object> ?? throw new InvalidDataException("Invalid torrent.");
        if (reader.Position != bytes.Length || reader.InfoStart < 0) throw new InvalidDataException("Invalid torrent info.");
        var info = root["info"] as Dictionary<string, object> ?? throw new InvalidDataException("Invalid torrent info.");
        InfoHash = Convert.ToHexStringLower(SHA1.HashData(bytes.AsSpan(reader.InfoStart, reader.InfoLength)));
        RootName = Text(info["name"]);
        SalmonPayload.ValidatePath(RootName, root: true);
        Announce = Text(root["announce"]);
        var host = target == "red" ? "flacsfor.me" : target == "ops" ? "home.opsfet.ch" : "";
        if (!Uri.TryCreate(Announce, UriKind.Absolute, out var announce) || announce.Scheme != "https"
            || announce.Host != host || !announce.IsDefaultPort || announce.UserInfo.Length != 0
            || announce.Query.Length != 0 || announce.Fragment.Length != 0
            || !System.Text.RegularExpressions.Regex.IsMatch(announce.AbsolutePath, @"^/[a-zA-Z0-9]{20,64}/announce$"))
            throw new InvalidDataException("Torrent announce does not match destination.");
        if (root.TryGetValue("announce-list", out var tiers)
            && (tiers is not List<object> list || list.Any(t => t is not List<object> urls
                || urls.Any(u => Text(u) != Announce))))
            throw new InvalidDataException("Torrent has additional announce URLs.");
        if ((long)info["private"] != 1 || Text(info["source"]) != target.ToUpperInvariant()
            || info.ContainsKey("meta version") || !info.TryGetValue("files", out var rawFiles)
            || rawFiles is not List<object> fileList || fileList.Count is 0 or > 2000)
            throw new InvalidDataException("Only private multi-file v1 destination torrents are supported.");
        Files = fileList.Select(f =>
        {
            var item = f as Dictionary<string, object> ?? throw new InvalidDataException("Invalid torrent file.");
            if (item.ContainsKey("attr") || item.ContainsKey("symlink path")) throw new InvalidDataException("Torrent links are unsupported.");
            var components = ((List<object>)item["path"]).Select(Text).ToList();
            foreach (var component in components) SalmonPayload.ValidatePath(component, root: true);
            var path = string.Join('/', components);
            SalmonPayload.ValidatePath(path);
            var size = (long)item["length"];
            if (size <= 0) throw new InvalidDataException("Empty torrent file.");
            return (path, size);
        }).ToList();
        if (Files.Select(f => f.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Files.Count)
            throw new InvalidDataException("Duplicate torrent paths.");
        _pieceLength = checked((int)(long)info["piece length"]);
        _pieces = (byte[])info["pieces"];
        if (_pieceLength is < 16384 or > 16 * 1024 * 1024 || (_pieceLength & (_pieceLength - 1)) != 0
            || _pieces.LongLength != ((Files.Sum(f => f.Size) + _pieceLength - 1) / _pieceLength) * 20)
            throw new InvalidDataException("Invalid torrent pieces.");
    }

    public void Match(SalmonSubmission submission)
    {
        if (RootName != submission.RootName || Files.Count != submission.Files.Count
            || Files.Any(f => !submission.Files.Any(s => s.Path == f.Path && s.Size == f.Size)))
            throw new InvalidDataException("Torrent file manifest differs from reviewed payload.");
    }

    public async Task VerifyPiecesAsync(string directory, CancellationToken ct)
    {
        var buffer = new byte[_pieceLength];
        var used = 0;
        var index = 0;
        foreach (var file in Files)
        {
            using var input = File.OpenRead(Path.Combine(directory, file.Path));
            while (true)
            {
                var read = await input.ReadAsync(buffer.AsMemory(used), ct);
                if (read == 0) break;
                used += read;
                if (used == buffer.Length) { CheckPiece(); used = 0; }
            }
        }
        if (used > 0) CheckPiece();
        if (index * 20 != _pieces.Length) throw new InvalidDataException("Torrent piece count differs from payload.");
        void CheckPiece()
        {
            if (index * 20 >= _pieces.Length || !SHA1.HashData(buffer.AsSpan(0, used)).AsSpan()
                .SequenceEqual(_pieces.AsSpan(index++ * 20, 20)))
                throw new InvalidDataException("Torrent pieces differ from payload.");
        }
    }

    private static string Text(object value) => new UTF8Encoding(false, true).GetString((byte[])value);

    private sealed class Reader(byte[] bytes)
    {
        public int Position { get; private set; }
        public int InfoStart { get; private set; } = -1;
        public int InfoLength { get; private set; }
        public object Read(int depth = 0)
        {
            if (depth > 12 || Position >= bytes.Length) throw new InvalidDataException("Invalid bencode.");
            var start = Position;
            var type = bytes[Position++];
            if (type == 'i')
            {
                var end = Array.IndexOf(bytes, (byte)'e', Position);
                if (end < 0) throw new InvalidDataException("Invalid bencode integer.");
                var text = Encoding.ASCII.GetString(bytes, Position, end - Position);
                if (!long.TryParse(text, out var value) || text != value.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    throw new InvalidDataException("Non-canonical bencode integer.");
                Position = end + 1;
                return value;
            }
            if (type == 'l' || type == 'd')
            {
                var list = new List<object>();
                var dict = new Dictionary<string, object>(StringComparer.Ordinal);
                string? previous = null;
                while (Position < bytes.Length && bytes[Position] != 'e')
                {
                    if (type == 'l') list.Add(Read(depth + 1));
                    else
                    {
                        var key = Text(Read(depth + 1));
                        if (previous is not null && string.CompareOrdinal(previous, key) >= 0)
                            throw new InvalidDataException("Unordered or duplicate bencode keys.");
                        previous = key;
                        var valueStart = Position;
                        dict.Add(key, Read(depth + 1));
                        if (depth == 0 && key == "info") { InfoStart = valueStart; InfoLength = Position - valueStart; }
                    }
                }
                if (Position >= bytes.Length) throw new InvalidDataException("Unterminated bencode.");
                Position++;
                return type == 'l' ? list : dict;
            }
            var colon = Array.IndexOf(bytes, (byte)':', start);
            if (colon < 0 || colon - start > 9) throw new InvalidDataException("Invalid bencode string.");
            var lengthText = Encoding.ASCII.GetString(bytes, start, colon - start);
            if (!int.TryParse(lengthText, out var length) || length < 0 || lengthText != length.ToString()
                || length > bytes.Length - colon - 1) throw new InvalidDataException("Invalid bencode string length.");
            Position = colon + 1 + length;
            return bytes.AsSpan(colon + 1, length).ToArray();
        }
    }
}
