using Qisland.Core;

namespace Qisland.App;

/// <summary>调试预览的纯解析函数：只说会去哪里，不启动下载、进程或窗口。</summary>
public static class DebugJumpTarget
{
    public static string Resolve(ActivityStore store, string id, MediaWatcher? media)
    {
        if (id == "media")
        {
            var current = media?.Current;
            return current == null ? "媒体：当前没有播放" : $"媒体：切到 {current.AppSource}";
        }

        if (store.Get(id) is not { } activity) return $"没有条目：{id}";
        return activity.Id switch
        {
            "update" => "更新：下载新版本并重启（仅执行模式会开始下载）",
            "health:water" => "岛内：记一杯水",
            "health:sit" => "岛内：收起久坐提醒",
            "call" => "通话：切到通话窗口；没有窗口时切到来源软件",
            _ when activity.Id.StartsWith("todo:remind:", StringComparison.Ordinal) => "岛内：打开待办页",
            _ when activity.Id is "ykt:expired" or "ykt:expired-note" => "登录：打开雨课堂扫码窗口",
            _ when activity.Id is "xxt:expired" or "xxt:expired-note" => "登录：打开学习通扫码窗口",
            _ when activity.Dispatch == "download" => "资源管理器：打开下载文件夹",
            _ => ResolvePlan(activity),
        };
    }

    private static string ResolvePlan(LiveActivity activity)
    {
        var plan = JumpPlan.For(activity.Dispatch, activity.OpenUri, activity.AppId, Launcher.CanOpen(activity.Dispatch));
        if (plan.Count == 0) return $"没有跳转目标：{activity.Id}";
        var steps = plan.Select(step => step switch
        {
            JumpStep.Uri => "直达链接",
            JumpStep.WeChat => "微信窗口 / weixin:// / 快捷键",
            JumpStep.App => "来源软件",
            JumpStep.Aumid => "按 AUMID 激活",
            JumpStep.Terminal => "终端 / 编辑器",
            JumpStep.Calendar => "系统日历",
            _ => step.ToString(),
        });
        return $"跳转 {activity.Id}：{string.Join(" → ", steps)}";
    }
}
