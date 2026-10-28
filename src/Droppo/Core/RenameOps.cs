using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace Droppo.Core;

/// <summary>What to rename to. Pattern tokens: {name} {date} and # for the number (### = 001).</summary>
public sealed record RenameRequest(string Pattern, int StartAt);

public sealed record RenameMove(string From, string To);

/// <summary>Batch rename with preview, collision checks and one-step undo.</summary>
public static class RenameOps
{
    public static string UndoFile => Path.Combine(Settings.Folder, "last-rename.json");

    public static WheelAction Action() => new()
    {
        Id = "any.rename", Label = "Rename", Hint = "batch rename", InPlace = true,
        Prepare = async (files, ctx) => await ctx.Ask.AskRenameAsync(Ordered(files)),
        Combined = Apply,
    };

    public static List<string> Ordered(IReadOnlyList<string> files) =>
        files.Where(f => File.Exists(f) || Directory.Exists(f))
             .OrderBy(f => Path.GetFileName(f.TrimEnd('\\', '/')), Paths.NaturalComparer.Instance).ToList();

    /// <summary>The new full paths, in the same order as <paramref name="files"/>. Throws with a readable reason if invalid.</summary>
    public static List<RenameMove> Plan(IReadOnlyList<string> files, RenameRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Pattern)) throw new ArgumentException("Type a name pattern.");
        var moves = new List<RenameMove>();
        for (int i = 0; i < files.Count; i++)
        {
            var from = files[i].TrimEnd('\\', '/');
            bool isDir = Directory.Exists(from);
            var dir = Path.GetDirectoryName(from) ?? ".";
            var ext = isDir ? "" : Path.GetExtension(from);
            var stem = isDir ? Path.GetFileName(from) : Path.GetFileNameWithoutExtension(from);
            var name = Expand(request.Pattern, stem, request.StartAt + i, files.Count, File.GetLastWriteTime(from)).Trim();
            if (name.Length == 0) throw new ArgumentException("The pattern makes an empty name.");
            if (name.IndexOfAny(Path.GetInvalidFileNameChars().Concat(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }).ToArray()) >= 0)
                throw new ArgumentException("Names can't contain \\ / : * ? \" < > |");
            moves.Add(new RenameMove(from, Path.Combine(dir, name + ext)));
        }

        var dupes = moves.GroupBy(m => m.To, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (dupes != null) throw new ArgumentException($"Two files would both be called \"{Path.GetFileName(dupes.Key)}\". Add # to number them.");

        var sources = new HashSet<string>(moves.Select(m => m.From), StringComparer.OrdinalIgnoreCase);
        var clash = moves.FirstOrDefault(m => !sources.Contains(m.To) && (File.Exists(m.To) || Directory.Exists(m.To)));
        if (clash != null) throw new ArgumentException($"\"{Path.GetFileName(clash.To)}\" already exists in that folder.");
        return moves;
    }

    private static string Expand(string pattern, string stem, int number, int total, DateTime date)
    {
        var sb = new StringBuilder();
        int digitsMin = Math.Max(1, total.ToString().Length);
        for (int i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] == '#')
            {
                int run = 0;
                while (i + run < pattern.Length && pattern[i + run] == '#') run++;
                sb.Append(number.ToString().PadLeft(run == 1 ? digitsMin : run, '0'));
                i += run - 1;
            }
            else sb.Append(pattern[i]);
        }
        return sb.ToString().Replace("{name}", stem, StringComparison.OrdinalIgnoreCase)
                            .Replace("{date}", date.ToString("yyyy-MM-dd"), StringComparison.OrdinalIgnoreCase);
    }

    public static Task<ActionOutput> Apply(IReadOnlyList<string> files, ActionContext ctx) => Task.Run(() =>
    {
        var request = ctx.State as RenameRequest ?? throw new InvalidOperationException("No new names chosen.");
        var moves = Plan(Ordered(files), request).Where(m => !string.Equals(m.From, m.To, StringComparison.Ordinal)).ToList();
        if (moves.Count == 0) return new ActionOutput(null, "The names didn't change.");
        Execute(moves);
        File.WriteAllText(UndoFile, JsonSerializer.Serialize(moves));
        return new ActionOutput(moves[0].To, $"Renamed {moves.Count} item{(moves.Count == 1 ? "" : "s")}. Changed your mind? Tray menu > Undo last rename");
    });

    /// <summary>Two steps (to temporary names, then final) so "a→b, b→a" swaps work. Rolls back if anything fails.</summary>
    private static void Execute(List<RenameMove> moves)
    {
        var temps = new List<(string Temp, RenameMove Move)>();
        try
        {
            foreach (var m in moves)
            {
                var temp = Path.Combine(Path.GetDirectoryName(m.From) ?? ".", $".cc-rename-{Guid.NewGuid():N}");
                MovePath(m.From, temp);
                temps.Add((temp, m));
            }
            for (int i = 0; i < temps.Count; i++)
            {
                MovePath(temps[i].Temp, temps[i].Move.To);
                temps[i] = ("", temps[i].Move);
            }
        }
        catch
        {
            foreach (var (temp, m) in temps)
            {
                try
                {
                    if (temp.Length > 0 && (File.Exists(temp) || Directory.Exists(temp))) MovePath(temp, m.From);
                    else if (temp.Length == 0 && (File.Exists(m.To) || Directory.Exists(m.To))) MovePath(m.To, m.From);
                }
                catch (Exception ex) { Log.Write("Rename rollback problem: " + ex.Message); }
            }
            throw;
        }
    }

    private static void MovePath(string from, string to)
    {
        if (Directory.Exists(from)) Directory.Move(from, to);
        else File.Move(from, to);
    }

    /// <summary>Puts back the names from the last batch rename. Returns how many were restored.</summary>
    public static int UndoLast()
    {
        if (!File.Exists(UndoFile)) return 0;
        var moves = JsonSerializer.Deserialize<List<RenameMove>>(File.ReadAllText(UndoFile)) ?? new();
        var back = moves.Where(m => (File.Exists(m.To) || Directory.Exists(m.To)) && !File.Exists(m.From) && !Directory.Exists(m.From))
                        .Select(m => new RenameMove(m.To, m.From)).ToList();
        if (back.Count > 0) Execute(back);
        File.Delete(UndoFile);
        return back.Count;
    }
}
