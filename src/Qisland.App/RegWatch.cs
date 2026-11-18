using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Qisland.App;

/// <summary>
/// 注册表变化通知（RegNotifyChangeKeyValue）：一个后台线程睡在内核事件上，键真的变了才醒，平时零开销。
/// 回调在这个后台线程上，调用方自己投递到 UI 线程、自己去抖。
/// </summary>
public sealed class RegWatch : IDisposable
{
    [DllImport("advapi32.dll")]
    private static extern int RegNotifyChangeKeyValue(SafeRegistryHandle key, bool subtree, int filter, SafeWaitHandle ev, bool async);
    private const int NameAndValue = 0x1 | 0x4;   // REG_NOTIFY_CHANGE_NAME | REG_NOTIFY_CHANGE_LAST_SET
    private const int ThreadAgnostic = 0x10000000;

    private readonly List<(RegistryKey key, bool subtree, AutoResetEvent ev)> _keys = [];
    private readonly ManualResetEvent _stop = new(false);
    private readonly Action _changed;
    private readonly Thread? _thread;
    public string Name { get; }

    public RegWatch(string name, IEnumerable<(RegistryKey root, string path, bool subtree)> keys, Action changed)
    {
        Name = name;
        _changed = changed;
        foreach (var (root, path, subtree) in keys)
        {
            try
            {
                var k = root.OpenSubKey(path, false);
                if (k == null) { App.Log($"{name}: 注册表键不存在 {path}"); continue; }
                var ev = new AutoResetEvent(false);
                if (!Arm(k, subtree, ev)) { k.Dispose(); ev.Dispose(); continue; }
                _keys.Add((k, subtree, ev));
            }
            catch (Exception ex) { App.Log($"{name}: 打开注册表失败 {ex.Message}"); }
        }
        if (_keys.Count == 0) return;
        _thread = new Thread(Run) { IsBackground = true, Name = "RegWatch " + name, Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    public bool Active => _thread != null;

    private bool Arm(RegistryKey k, bool subtree, AutoResetEvent ev)
    {
        var rc = RegNotifyChangeKeyValue(k.Handle, subtree, NameAndValue | ThreadAgnostic, ev.SafeWaitHandle, true);
        if (rc == 87) rc = RegNotifyChangeKeyValue(k.Handle, subtree, NameAndValue, ev.SafeWaitHandle, true);   // 老系统不认 THREAD_AGNOSTIC
        if (rc != 0) App.Log($"{Name}: RegNotifyChangeKeyValue 失败 {rc}");
        return rc == 0;
    }

    private void Run()
    {
        var handles = _keys.Select(k => (WaitHandle)k.ev).Append(_stop).ToArray();
        while (true)
        {
            int i;
            try { i = WaitHandle.WaitAny(handles); } catch { return; }
            if (i == handles.Length - 1) return;
            var (key, subtree, ev) = _keys[i];
            Arm(key, subtree, ev);   // 先重新挂上，回调期间的变化也不会漏
            Counters.Hit("reg." + Name);
            try { _changed(); } catch (Exception ex) { App.Log($"{Name}: {ex.Message}"); }
        }
    }

    public void Dispose()
    {
        _stop.Set();
        _thread?.Join(500);
        foreach (var (k, _, ev) in _keys) { k.Dispose(); ev.Dispose(); }
        _keys.Clear();
    }
}
