using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Pickles_Playlist_Editor.Utils;
using System.Text;

namespace AnimHarness;

internal enum CallKind { Read, Write, Delete, EditGroups }

/// <summary>One host call; Index counts calls of this kind on this path.</summary>
internal sealed record Call(CallKind Kind, string Path, int Index);

/// <summary>
/// An <see cref="IAnimationHost"/> over a temporary mod copy, with a write log and fault injection.
/// After a kill every write is ignored, which is what process death looks like from the disk.
/// </summary>
internal sealed class TempModHost : IAnimationHost
{
    private readonly string _modRoot;
    private readonly Dictionary<(CallKind, string), int> _counts = new();
    private readonly Func<string>? _tokens;
    private int _nextToken = 1;
    private bool _killed;

    public string StateFolder { get; }
    public List<Call> Log { get; } = new();
    public Func<Call, bool>? ThrowOn { get; set; }
    public Func<Call, bool>? KillAfter { get; set; }

    public TempModHost(string modRoot, string stateFolder, Func<string>? tokens = null)
    {
        _modRoot = modRoot;
        StateFolder = stateFolder;
        _tokens = tokens;
    }

    public string NewToken() => _tokens?.Invoke() ?? $"t{_nextToken++:D7}";

    public byte[] ReadFile(string full)
    {
        Record(CallKind.Read, full, out var call);
        if (ThrowOn?.Invoke(call) == true) throw new IOException($"Injected read failure: {full}");
        return File.ReadAllBytes(full);
    }

    public void WriteFile(string full, byte[] bytes)
    {
        if (!Begin(CallKind.Write, full, out var call)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
        End(call);
    }

    public void DeleteFile(string full)
    {
        if (!Begin(CallKind.Delete, full, out var call)) return;
        if (File.Exists(full)) File.Delete(full);
        End(call);
    }

    public void DeleteDirectory(string full)
    {
        if (!Begin(CallKind.Delete, full, out var call)) return;
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        End(call);
    }

    public void EditGroups(Func<JObject, bool> edit)
    {
        if (!Begin(CallKind.EditGroups, "groups", out var call)) return;

        string meta = Path.Combine(_modRoot, "meta.json");
        var root = JObject.Parse(File.ReadAllText(meta));
        if (root["Groups"] is JArray inline)
        {
            bool changed = false;
            foreach (var group in inline.OfType<JObject>()) changed |= edit(group);
            if (changed) File.WriteAllText(meta, root.ToString(Formatting.Indented), new UTF8Encoding(false));
        }
        else
        {
            foreach (string file in Directory.GetFiles(_modRoot, "group_*.json"))
            {
                var group = JObject.Parse(File.ReadAllText(file));
                if (edit(group))
                    File.WriteAllText(file, group.ToString(Formatting.Indented).Replace("\n", "\r\n").Replace("\r\r\n", "\r\n"),
                        new UTF8Encoding(false));
            }
        }
        End(call);
    }

    public int Count(CallKind kind) => Log.Count(c => c.Kind == kind);

    private void Record(CallKind kind, string path, out Call call)
    {
        var key = (kind, Path.GetFullPath(path).ToLowerInvariant());
        _counts[key] = _counts.GetValueOrDefault(key) + 1;
        call = new Call(kind, path, _counts[key]);
        if (!_killed) Log.Add(call);
    }

    private bool Begin(CallKind kind, string path, out Call call)
    {
        Record(kind, path, out call);
        if (_killed) return false;
        if (ThrowOn?.Invoke(call) == true) throw new IOException($"Injected {kind} failure: {path}");
        return true;
    }

    private void End(Call call)
    {
        if (KillAfter?.Invoke(call) == true) _killed = true;
    }
}
