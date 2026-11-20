namespace Qisland.Core;

/// <summary>todo.md 里的一条（Line = 读出来时的行号，只当线索用，写回前要重新定位）。</summary>
public sealed record TodoFileItem(int Line, string Text, bool Done);

public enum TodoEditResult { Ok, Conflict, WriteFailed }

/// <summary>
/// todo.md 的行级编辑。编辑窗口开着时文件可能被改（上面插了一行等），所以写回前按原文重新找到那一行：
/// 原行号上还是它就用原行号；否则按文字找，正好一条就用它；找不到或有多条一样的算冲突，什么都不改。
/// </summary>
public static class TodoLines
{
    public const string Sample = "在这里写下第一件事";

    public static (bool ok, bool done, string text) Parse(string raw)
    {
        var l = raw.Trim();
        if (l.Length < 5 || (l[0] != '-' && l[0] != '*') || l[1] != ' ' || l[2] != '[' || l[4] != ']') return (false, false, "");
        var done = l[3] is 'x' or 'X';
        if (!done && l[3] != ' ') return (false, false, "");
        var text = l[5..].Trim();
        return (text.Length > 0 && text != Sample, done, text);
    }

    public static List<TodoFileItem> Items(IReadOnlyList<string> lines)
    {
        var list = new List<TodoFileItem>();
        for (var i = 0; i < lines.Count; i++)
            if (Parse(lines[i]) is { ok: true } p) list.Add(new TodoFileItem(i, p.text, p.done));
        return list;
    }

    /// <summary>重新定位这一条；找不到或不唯一返回 null。</summary>
    public static int? Locate(IReadOnlyList<string> lines, TodoFileItem it)
    {
        bool Match(int i) => Parse(lines[i]) is { ok: true } p && p.text == it.Text && p.done == it.Done;
        if (it.Line >= 0 && it.Line < lines.Count && Match(it.Line)) return it.Line;
        int? found = null;
        for (var i = 0; i < lines.Count; i++)
            if (Match(i)) { if (found != null) return null; found = i; }
        return found;
    }

    public static string Clean(string text) => text.Replace("\r", " ").Replace("\n", " ").Trim();

    public static TodoEditResult SetText(List<string> lines, TodoFileItem it, string text)
    {
        text = Clean(text);
        if (text.Length == 0 || Locate(lines, it) is not { } at) return TodoEditResult.Conflict;
        lines[at] = (it.Done ? "- [x] " : "- [ ] ") + text;
        return TodoEditResult.Ok;
    }

    public static TodoEditResult Delete(List<string> lines, TodoFileItem it)
    {
        if (Locate(lines, it) is not { } at) return TodoEditResult.Conflict;
        lines.RemoveAt(at);
        return TodoEditResult.Ok;
    }

    /// <summary>勾选 / 取消。每周重复的勾掉 = 这次做完，⏰ 挪到下一次仍留在未完成里。</summary>
    public static TodoEditResult SetDone(List<string> lines, TodoFileItem it, bool done, DateTime now)
    {
        if (Locate(lines, it) is not { } at) return TodoEditResult.Conflict;
        if (done && TodoMeta.Parse(it.Text).Complete(now) is { } next) { lines[at] = "- [ ] " + next.ToText(); return TodoEditResult.Ok; }
        lines.RemoveAt(at);
        // 勾掉的挪到文件末尾（最近完成的在最后），取消勾选的放回未完成末尾
        if (done) lines.Add("- [x] " + it.Text);
        else
        {
            var pos = lines.Count;
            for (var i = 0; i < lines.Count; i++) if (Parse(lines[i]) is { ok: true, done: true }) { pos = i; break; }
            lines.Insert(pos, "- [ ] " + it.Text);
        }
        return TodoEditResult.Ok;
    }
}
