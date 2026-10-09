namespace Domru.Desktop.Core;
public static class ArchiveHistory
{
    public static DateTimeOffset FourDayStart(DateTimeOffset now)
        => new(now.Date.AddDays(-3), now.Offset);
    public static Task<List<HistoryEvent>> LoadAsync(DomruApi api,Door door,DateTimeOffset now,CancellationToken ct=default,IProgress<int>? progress=null)
        => LoadRangeAsync(api,door,FourDayStart(now),now,ct,progress);
    public static (DateTimeOffset From,DateTimeOffset To) DayRange(DateTimeOffset now,int daysAgo)
    {
        if(daysAgo is <0 or >2)throw new ArgumentOutOfRangeException(nameof(daysAgo));
        var from=new DateTimeOffset(now.Date.AddDays(-daysAgo),now.Offset);
        return (from,daysAgo==0?now:from.AddDays(1).AddMilliseconds(-1));
    }
    public static Task<List<HistoryEvent>> LoadDayAsync(DomruApi api,Door door,DateTimeOffset now,int daysAgo,CancellationToken ct=default,IProgress<int>? progress=null)
    {
        var range=DayRange(now,daysAgo);return LoadRangeAsync(api,door,range.From,range.To,ct,progress);
    }
    private static async Task<List<HistoryEvent>> LoadRangeAsync(DomruApi api,Door door,DateTimeOffset from,DateTimeOffset now,CancellationToken ct,IProgress<int>? progress)
    {
        var result=new List<HistoryEvent>();var seen=new HashSet<string>();
        for(int page=0;;page++)
        {
            ct.ThrowIfCancellationRequested();
            var response=await api.SearchEventsAsync(door.PlaceId,page,from,now,ct);
            if(response.Events.Count==0){if(!response.Last)throw new InvalidOperationException("Сервер вернул пустую страницу до конца истории");break;}
            var newCount=0;
            foreach(var item in response.Events)
            {
                var key=string.IsNullOrEmpty(item.Id)?$"{item.Time:O}|{item.Title}|{Json.Str(item.Raw["source"],"id")}":item.Id;
                if(!seen.Add(key))continue;newCount++;
                if(item.Time is null||item.Time<from||item.Time>now)continue;
                var source=Json.Str(item.Raw["source"],"id");
                if(source is not null&&source!=door.Id&&source!=door.CameraId&&!(item.CameraId is not null&&item.CameraId==door.CameraId))continue;
                result.Add(item);
            }
            progress?.Report(result.Count);
            if(response.Last)break;
            if(newCount==0)throw new InvalidOperationException("Сервер повторяет страницы истории; загрузка не завершена");
        }
        if(door.CameraId is not null)
        {
            var upper=now;var cameraSeen=new HashSet<string>();
            while(upper>=from)
            {
                ct.ThrowIfCancellationRequested();
                var rows=DomruApi.ParseEvents(await api.CameraEventsAsync(door.CameraId,from,upper,ct));
                if(rows.Count==0)break;var added=0;
                foreach(var item in rows)
                {
                    if(item.Time is null||item.Time<from||item.Time>now)continue;
                    var key=string.IsNullOrEmpty(item.Id)?$"{item.Time:O}|{item.Title}":item.Id;
                    if(!cameraSeen.Add(key))continue;
                    result.Add(item);added++;
                }
                progress?.Report(result.Count);
                if(rows.Count<200)break;
                var timed=rows.Where(x=>x.Time is not null).ToList();
                if(timed.Count==0||added==0)throw new InvalidOperationException("Сервер повторяет страницу записей камеры; загрузка не завершена");
                var oldest=timed.Min(x=>x.Time!.Value);upper=oldest.AddMilliseconds(-1);
            }
        }
        return result.OrderByDescending(x=>x.Time).ToList();
    }
}
