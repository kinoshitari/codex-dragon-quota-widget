using System.Text.RegularExpressions;

namespace DragonQuotaWidget;

internal static class ActivityPresentation
{
    // Select the visible Chinese progress, not the model's raw reasoning.
    public static string? Chinese(string? text, int maxLength = 96)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = Regex.Replace(text, @"```[\s\S]*?```", "");
        var lines = Regex.Split(text, @"\r?\n|(?<=[。！？])")
            .Where(line => Regex.IsMatch(line, @"[\u3400-\u9fff]"));
        var result = Plain(string.Join(" ", lines));
        return result.Length == 0 ? null : Shorten(result, maxLength);
    }

    public static string Plain(string text)
    {
        text = Regex.Replace(text, @"\[([^\]]+)\]\([^)]*\)", "$1");
        text = Regex.Replace(text, @"(?m)^\s*[#>*•-]+\s*", "");
        text = text.Replace("**", "").Replace("`", "");
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    public static string Shorten(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";

    public static string Tool(string? name, bool completed = false)
    {
        name = name?.ToLowerInvariant() ?? "";
        var action = name switch
        {
            var x when x.Contains("exec") || x is "bash" or "shell" => completed ? "已运行命令" : "正在运行命令",
            var x when x.Contains("patch") || x is "write" or "edit" or "multiedit" => completed ? "已修改文件" : "正在修改文件",
            var x when x.Contains("search") || x is "grep" or "glob" => completed ? "已完成检索" : "正在检索",
            var x when x.Contains("read") || x.Contains("view") => completed ? "已读取内容" : "正在读取内容",
            _ => completed ? "工具执行结束" : "正在使用工具"
        };
        // Tool identifiers are enough to recognize the action; arguments may
        // contain credentials or large scripts and are never echoed here.
        var label = Regex.IsMatch(name, @"^[a-z0-9_.:]+$") ? name.Split('.').Last() : "";
        return Shorten(label.Length == 0 ? action : $"{action} · {label}", 48);
    }
}
