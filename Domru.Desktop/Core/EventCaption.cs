using System.Net;
using System.Text.RegularExpressions;
namespace Domru.Desktop.Core;
public static class EventCaption
{
    public static string Clean(string text)
    {
        var decoded=Regex.Replace(WebUtility.HtmlDecode(text), "<[^>]+>", " ");
        decoded=Regex.Replace(decoded, "[\u25b6\u25ba\u25b8\u25b9\u25b7\u25bb\u27a4\u2192\u203a]+", " \u2014 ");
        return Regex.Replace(decoded,"\\s+"," ").Trim();
    }
    public static string Describe(HistoryEvent item) => Clean(item.Title);
    public static string CameraDayDescription(HistoryEvent item)
    {
        if(item.Time is { } timestamp&&long.TryParse(Json.Str(item.Raw,"Duration","duration"),out var seconds)&&seconds>0&&seconds<=86400)
        {
            var start=timestamp.ToLocalTime();var end=start.AddSeconds(seconds);
            return $"с {start:HH:mm:ss} по {end:HH:mm:ss}";
        }
        var text=Clean(item.Title);var range=Regex.Match(text,@"(?<start>\d{1,2}:\d{2}:\d{2})\s*(?:[-\u2014\u2013]+|по)\s*(?<end>\d{1,2}:\d{2}:\d{2})");
        return range.Success?$"с {range.Groups["start"].Value} по {range.Groups["end"].Value}":text.TrimStart(' ','-','\u2014','\u2013');
    }
    public static string ArchiveTitle(HistoryEvent item)
    {
        if(item.Raw.ContainsKey("Time") || item.Raw.ContainsKey("Start")) return "Запись камеры";
        var title=Clean(item.Title);
        title=Regex.Replace(title,@"\b\d{1,2}[./]\d{1,2}[./]\d{2,4}(?:\s+\d{1,2}:\d{2}(?::\d{2})?)?", "");
        title=Regex.Replace(title,@"\b\d{1,2}:\d{2}(?::\d{2})?\b", "");
        title=Regex.Replace(title,"\\s+"," ").Trim(' ', '\u2014', '-', '|', '\u00b7', ':');
        return string.IsNullOrWhiteSpace(title)?"Запись камеры":title;
    }
    public static string ArchiveTime(HistoryEvent item)
    {
        if(item.Time is null)return "";
        var start=item.Time.Value.ToLocalTime();
        if(long.TryParse(Json.Str(item.Raw,"Duration","duration"),out var seconds)&&seconds>0&&seconds<=86400)
        {
            var end=start.AddSeconds(seconds);
            return start.ToString("HH:mm:ss")+" \u2014 "+end.ToString("HH:mm:ss");
        }
        return start.ToString("HH:mm:ss");
    }
    public static string ArchiveDates(HistoryEvent item)
    {
        if(item.Time is null||!long.TryParse(Json.Str(item.Raw,"Duration","duration"),out var seconds)||seconds<=0||seconds>86400)return "";
        var start=item.Time.Value.ToLocalTime();var end=start.AddSeconds(seconds);
        return start.Date==end.Date?"":start.ToString("dd.MM.yyyy")+" \u2014 "+end.ToString("dd.MM.yyyy");
    }
    public static string FullRecordingInterval(HistoryEvent item)
    {
        var range=RecordingRange(item);
        return range is { } value?$"с {value.Start:dd.MM.yyyy HH:mm:ss} по {value.End:dd.MM.yyyy HH:mm:ss}":"Время события не указано";
    }
    public static (DateTimeOffset Start,DateTimeOffset End)? RecordingRange(HistoryEvent item)
    {
        if(item.Time is null)return null;
        var start=item.Time.Value.ToLocalTime();var end=start;
        if(long.TryParse(Json.Str(item.Raw,"Duration","duration"),out var seconds)&&seconds>0&&seconds<=86400)end=start.AddSeconds(seconds);
        else{
            var range=Regex.Match(Clean(item.Title),@"\d{1,2}:\d{2}:\d{2}\s*[-\u2014\u2013]+\s*(?<end>\d{1,2}:\d{2}:\d{2})");
            if(range.Success&&TimeSpan.TryParse(range.Groups["end"].Value,out var time)){end=new DateTimeOffset(start.Date.Add(time),start.Offset);if(end<start)end=end.AddDays(1);}
        }
        return (start,end);
    }
}
