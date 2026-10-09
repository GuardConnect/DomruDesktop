using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using Domru.Desktop.Core;
namespace Domru.Desktop;
public partial class MainWindow
{
    private async Task VerifyArchiveEventsAsync()
    {
        var root=Environment.GetEnvironmentVariable("DOMRU_SMOKE_DIR")??Path.Combine(Environment.CurrentDirectory,"artifacts","archive-events");Directory.CreateDirectory(root);
        try{
            var queries=new List<string>();var generalRequests=0;var day=DateTime.Today.AddDays(-1);var timestamp=new DateTimeOffset(day.AddHours(23).AddMinutes(56).AddSeconds(39)).ToUnixTimeSeconds();
            api=new DomruApi(Guid.NewGuid().ToString(),new("test","test","test"),new ArchiveVerificationHttp(request=>{
                if(request.RequestUri!.AbsolutePath.Contains("/forpost/cameras/")){queries.Add(request.RequestUri.Query);return new JsonObject{["data"]=new JsonArray(new JsonObject{["ID"]="test-event",["Time"]=timestamp,["Duration"]=30,["Message"]="--- 23:56:39 - 23:57:09"})}.ToJsonString();}
                generalRequests++;return "{\"data\":[]}";
            }));
            loading=true;var place=new Place("test-place","Тестовый адрес",new());var door=new Door("test-door",place.Id,"Тестовая камера","SIP","test-camera",null,"SIP",false,true,10800,new());
            PlaceBox.ItemsSource=new[]{place};PlaceBox.SelectedIndex=0;allDoors.Clear();allDoors.Add(door);DoorBox.ItemsSource=new[]{door};DoorBox.SelectedIndex=0;loading=false;
            HomePanel.Visibility=Visibility.Visible;LoginPanel.Visibility=Visibility.Collapsed;ArchiveDate.SelectedDate=day;
            selectedRecording=new("test-event","",DateTimeOffset.FromUnixTimeSeconds(timestamp),door.CameraId,new(){["Duration"]=30});
            ShowArchivePlayer(fromEvents:true);UpdateLayout();
            if(CameraControlsTitle.Visibility!=Visibility.Collapsed||VideoModesGroup.Visibility!=Visibility.Collapsed||WatchStreamButton.Content?.ToString()!="Просмотреть запись"||StopStreamButton.Content?.ToString()!="Закрыть запись")throw new InvalidOperationException("Неверные элементы управления записью");
            if(VideoPanelCard.BorderThickness.Left!=1||VideoImageCard.BorderThickness.Left!=0||VideoToolbar.BorderThickness.Left!=0)throw new InvalidOperationException("Просмотр и действия не объединены в один блок");
            if(WatchStreamButton.Parent!=VideoActionsGroup||StopStreamButton.Parent!=VideoActionsGroup||ArchiveDate.Visibility!=Visibility.Collapsed||!ArchiveInterval.Text.Contains(day.ToString("dd.MM.yyyy")))throw new InvalidOperationException("Неверное расположение кнопок и интервала записи");
            Capture(Path.Combine(root,"record-player.png"));
            await EnsureVideoHostAsync();ToggleFullscreen();UpdateLayout();
            if(VideoView.HasRoundedRegion||VideoImageCard.Padding.Left!=0||VideoPanelCard.Padding.Left!=0||VideoImageCard.BorderThickness.Left!=0||VideoStage.ActualWidth<ActualWidth-4||VideoStage.ActualHeight<ActualHeight-4)throw new InvalidOperationException("Полный экран имеет отступы или скругление");
            ExitFullscreen();UpdateLayout();
            if(!VideoView.HasRoundedRegion||VideoPanelCard.Padding.Left!=12)throw new InvalidOperationException("Обычное оформление не восстановлено после полного экрана");
            await OpenCameraDayEventsAsync();var description=((EventItem)EventsGrid.Items[0]).Title;
            if(description!="с 23:56:39 по 23:57:09")throw new InvalidOperationException("Некорректный интервал: "+description);
            cameraHistoryTo=cameraHistoryFrom.AddHours(12);await LoadEventsAsync();
            if(queries.Count!=2||queries[0]!=queries[1]||generalRequests!=0||!cameraEventMode||history.Count!=1)throw new InvalidOperationException("Обновление не сохранило выбранный день");
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Loaded);await Task.Delay(200);Capture(Path.Combine(root,"day-events.png"));ShowArchivePlayer(fromEvents:true);await CloseRecordingAsync();
            if(EventsPanel.Visibility!=Visibility.Visible||!cameraEventMode)throw new InvalidOperationException("Закрытие записи не возвращает к событиям дня");
            await File.WriteAllTextAsync(Path.Combine(root,"verification.json"),new JsonObject{["recordingControlsCorrect"]=true,["interval"]=description,["dayRequests"]=queries.Count,["generalRequests"]=generalRequests,["refreshPreservesDay"]=true,["closeReturnsToEvents"]=true,["fullscreenHasNoPaddingOrRoundedRegion"]=true,["normalAppearanceRestored"]=true}.ToJsonString(new(){WriteIndented=true}));
        }catch(Exception ex){await File.WriteAllTextAsync(Path.Combine(root,"error.txt"),ex.ToString());}
        finally{RequestExit();}
    }
    private sealed class ArchiveVerificationHttp(Func<HttpRequestMessage,string> reply):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(reply(request),Encoding.UTF8,"application/json")});
    }
}
