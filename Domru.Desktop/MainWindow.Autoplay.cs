using System.Windows;
namespace Domru.Desktop;
public partial class MainWindow
{
    private long navigationVersion;
    private string navigationTarget="camera";
    private long BeginNavigation(string page){navigationTarget=page;return ++navigationVersion;}
    private async Task AutoplayCameraAsync()
    {
        if(api is null||video is null||closing||closed||!IsVisible||NavCamera.Tag is not "Active"||VideoPanel.Visibility!=Visibility.Visible||Door is not {AllowVideo:true,CameraId:not null})return;
        if(CameraAutoplayBox.IsChecked!=true)
        {
            if(!video.Player.IsPlaying){VideoState.Text="Камера остановлена";VideoPlaceholder.Text="Нажмите Play, чтобы посмотреть камеру";VideoEmptyState.Visibility=Visibility.Visible;VideoView.Visibility=Visibility.Hidden;}
            return;
        }
        if(video.Player.IsPlaying&&archiveTimestamp is null)return;
        try{await PlayAsync();}catch(Exception ex){if(NavCamera.Tag is "Active")StatusLabel.Text="Камера: "+ex.Message;}
    }
    private async void CameraAutoplayChanged(object sender,RoutedEventArgs e)
    {
        if(store is null)return;
        try{store.SaveCameraAutoplay(CameraAutoplayBox.IsChecked==true);}catch{StatusLabel.Text="Не удалось сохранить настройку автовоспроизведения";}
        if(CameraAutoplayBox.IsChecked==true)await AutoplayCameraAsync();
    }
    private async Task VerifyAutoplayAsync()
    {
        var original=CameraAutoplayBox.IsChecked==true;
        var initiallyVisible=IsVisible;var stage="startup";
        await RestoreFromTrayAsync(false);
        async Task WaitPlayingAsync()
        {
            for(int i=0;i<100;i++){if(video!.Player.IsPlaying)return;await Task.Delay(100);}
            throw new InvalidOperationException($"Камера не запустилась автоматически: {stage}, visible={IsVisible}, state={video?.Player.State}, auto={CameraAutoplayBox.IsChecked}");
        }
        try
        {
            CameraAutoplayBox.IsChecked=true;await AutoplayCameraAsync();await WaitPlayingAsync();
            var pages=new System.Text.Json.Nodes.JsonArray();
            foreach(var page in new[]{"events","archive","settings","keys","payments"})
            {
                stage=page;
                switch(page)
                {
                    case "events":await OpenEventsTabAsync();break;
                    case "archive":await OpenArchiveTabAsync();break;
                    case "settings":await OpenSettingsTabAsync();break;
                    default:await ShowServicePageAsync(page);if(page=="keys")await LoadKeysAsync();else await LoadAccountAsync();break;
                }
                if(video!.Player.IsPlaying)throw new InvalidOperationException("Камера не остановилась при переходе на "+page);
                await OpenCameraTabAsync();await WaitPlayingAsync();
                pages.Add(new System.Text.Json.Nodes.JsonObject{["from"]=page,["livePlaying"]=video.Player.IsPlaying&&archiveTimestamp is null});
            }
            CameraAutoplayBox.IsChecked=false;await OpenSettingsTabAsync();await OpenCameraTabAsync();await Task.Delay(600);
            if(video!.Player.IsPlaying)throw new InvalidOperationException("Отключённое автовоспроизведение запускает камеру");
            if(store!.LoadCameraAutoplay())throw new InvalidOperationException("Настройка выключения не сохранена");
            stage="enable";CameraAutoplayBox.IsChecked=true;await WaitPlayingAsync();
            if(!store.LoadCameraAutoplay())throw new InvalidOperationException("Настройка включения не сохранена");
            stage="rapid";var leaving=OpenSettingsTabAsync();var returning=OpenCameraTabAsync();await Task.WhenAll(leaving,returning);await WaitPlayingAsync();
            if(NavCamera.Tag is not "Active"||VideoPanel.Visibility!=Visibility.Visible)throw new InvalidOperationException("Быстрое переключение нарушает автовоспроизведение");
            var root=Environment.GetEnvironmentVariable("DOMRU_SMOKE_DIR")??System.IO.Path.Combine(Environment.CurrentDirectory,"artifacts");System.IO.Directory.CreateDirectory(root);
            await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(root,"autoplay-verification.json"),new System.Text.Json.Nodes.JsonObject{["initiallyVisible"]=initiallyVisible,["pages"]=pages,["disabledReturnStaysStopped"]=true,["enableStartsImmediately"]=true,["settingPersisted"]=true,["rapidReturnWorks"]=true}.ToJsonString(new(){WriteIndented=true}));
        }
        finally{CameraAutoplayBox.IsChecked=original;store?.SaveCameraAutoplay(original);}
        Close();
    }
}
