namespace GenshinBrowser.Models;

/// <summary>
/// 收藏 / 浏览记录条目的公共形状，供 <see cref="Services.UrlEntryStore{T}"/> 统一处理。
/// <see cref="Timestamp"/> 在各条目里显式实现、映射到各自的时间字段：JSON 不序列化它，落盘字段名（VisitedAt / SavedAt）不变。
/// </summary>
public interface IUrlEntry
{
    string Url { get; set; }

    string Title { get; set; }

    DateTime Timestamp { get; set; }
}
