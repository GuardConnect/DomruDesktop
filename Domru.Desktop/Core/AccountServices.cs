using System.Globalization;
using System.Text.Json.Nodes;
namespace Domru.Desktop.Core;
public record AccessKeyInfo(string Name,string Code,string State,string Doors,string? Id=null);
public static class AccountServices
{
    public static List<AccessKeyInfo> ParseKeys(JsonNode? response)=>Json.Array(response).OfType<JsonObject>().Select(row=>
    {
        var key=row["accessKey"]??row;
        var code=Json.Str(key,"accessKeyCode","code")??"—";
        var state=Json.Str(key,"state","status")??Json.Str(key["accessKeyBind"],"status")??"";
        state=state switch {"ACTIVE" or "ACTIVATED"=>"Активен","INACTIVE" or "DEACTIVATED"=>"Неактивен","BLOCKED"=>"Заблокирован","NEW"=>"Новый",_=>state};
        var doors=string.Join(", ",Json.Array(key["accessControls"]).Select(x=>Json.Str(x,"name")).Where(x=>!string.IsNullOrWhiteSpace(x)));
        return new AccessKeyInfo(Json.Str(key,"name") is {Length:>0} name?name:"Ключ домофона",code,state,doors,Json.Str(row,"id","ID")??Json.Str(key,"id","ID"));
    }).OrderBy(x=>long.TryParse(x.Id,NumberStyles.Integer,CultureInfo.InvariantCulture,out _)?0:1)
      .ThenBy(x=>long.TryParse(x.Id,NumberStyles.Integer,CultureInfo.InvariantCulture,out var id)?id:long.MaxValue).ToList();
    public static string Money(JsonNode? data,string field)
        =>decimal.TryParse(Json.Str(data,field),NumberStyles.Any,CultureInfo.InvariantCulture,out var amount)?amount.ToString("N2",CultureInfo.GetCultureInfo("ru-RU"))+" ₽":"Нет данных";
    public static string Date(JsonNode? data,string field)=>Json.Time(data?[field])?.ToLocalTime().ToString("dd.MM.yyyy")??"Не указан";
    public static bool IsHttps(string? url)=>Uri.TryCreate(url,UriKind.Absolute,out var uri)&&uri.Scheme==Uri.UriSchemeHttps&&string.IsNullOrEmpty(uri.UserInfo);
    public static string Provider(string? url)=>Uri.TryCreate(url,UriKind.Absolute,out var uri)&&(uri.Host=="cyfral-group.ru"||uri.Host.EndsWith(".cyfral-group.ru",StringComparison.OrdinalIgnoreCase))?"Цифрал-Сервис":"Дом.ру";
}
