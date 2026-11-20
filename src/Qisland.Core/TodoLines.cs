using System.Security.Cryptography;
using System.Text;

namespace Qisland.Core;

/// <summary>
/// todo.md 里的一条。身份 = 文字 + 状态 + 所在标题路径（Section）+ 该段内同样一条里的第几个（Occurrence）；
/// Line 是读出来时的行号，Fingerprint 是读出来（打开编辑）时整个文件的指纹。
/// </summary>
public sealed record TodoFileItem(int Line, string Text, bool Done, string Section = "", int Occurrence = 0, string Fingerprint = "");

public enum TodoEditResult { Ok, Conflict, WriteFailed }

/// <summary>
/// todo.md 的行级编辑。编辑窗口开着时文件可能被改（上面插了一行、加了一段等），所以写回前重新定位：
/// 文件指纹没变就原地改（同一段里有重名也改的是打开的那条）；
/// 变了只接受「同一标题路径 + 同文字 + 同状态」正好一条，找不到或有多条都算冲突，什么都不改。
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
        var fp = Fingerprint(lines);
        var seen = new Dictionary<(string, string, bool), int>();
        var sections = Sections(lines);
        for (var i = 0; i < lines.Count; i++)
        {
            if (Parse(lines[i]) is not { ok: true } p) continue;
            var key = (sections[i], p.text, p.done);
            var n = seen.GetValueOrDefault(key);
            seen[key] = n + 1;
            list.Add(new TodoFileItem(i, p.text, p.done, sections[i], n, fp));
        }
        return list;
    }

    /// <summary>整个文件内容的指纹（打开编辑时记下，写回前比对）。</summary>
    public static string Fingerprint(IReadOnlyList<string> lines) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines))));

    /// <summary>每一行所在的标题路径（「# 待办」下「## 工作」= 待办/工作；标题之前为空）。</summary>
    public static string[] Sections(IReadOnlyList<string> lines)
    {
        var stack = new List<(int level, string title)>();
        var result = new string[lines.Count];
        for (var i = 0; i < lines.Count; i++)
        {
            if (Heading(lines[i]) is { } h)
            {
                while (stack.Count > 0 && stack[^1].level >= h.level) stack.RemoveAt(stack.Count - 1);
                stack.Add(h);
            }
            result[i] = string.Join("/", stack.Select(s => s.title));
        }
        return result;
    }

    private static (int level, string title)? Heading(string raw)
    {
        var l = raw.Trim();
        var n = 0;
        while (n < l.Length && l[n] == '#') n++;
        if (n == 0 || n > 6 || (n < l.Length && l[n] != ' ')) return null;
        return (n, l[n..].Trim());
    }

    /// <summary>重新定位这一条；找不到或不唯一（文件变过时）返回 null。</summary>
    public static int? Locate(IReadOnlyList<string> lines, TodoFileItem it)
    {
        bool Match(int i) => Parse(lines[i]) is { ok: true } p && p.text == it.Text && p.done == it.Done;
        // 文件没变：就是打开时那一行
        if (it.Fingerprint.Length > 0 && it.Fingerprint == Fingerprint(lines))
            return it.Line >= 0 && it.Line < lines.Count && Match(it.Line) ? it.Line : null;
        // 变了：只认同一段里同文字同状态的唯一一条
        var sections = Sections(lines);
        int? found = null;
        for (var i = 0; i < lines.Count; i++)
            if (sections[i] == it.Section && Match(i)) { if (found != null) return null; found = i; }
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
