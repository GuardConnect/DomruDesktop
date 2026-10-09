using System.Windows;
using Domru.Desktop.Core;
namespace Domru.Desktop;
public partial class MainWindow
{
    private AudioPlayback? audioCheckPlayback;
    private CancellationTokenSource? audioCheckCancellation;
    private bool audioCheckRunning;
    private bool restoringAudioDevices;
    private async Task VerifyAudioChecksAsync()
    {
        var root=Environment.GetEnvironmentVariable("DOMRU_SMOKE_DIR")??Path.Combine(Environment.CurrentDirectory,"artifacts","base15-audio");Directory.CreateDirectory(root);
        try{
            store=new SessionStore(Environment.GetEnvironmentVariable("DOMRU_DATA_DIR"));RestoreAudioDevices();
            api=new DomruApi(Guid.NewGuid().ToString());HomePanel.Visibility=Visibility.Visible;LoginPanel.Visibility=Visibility.Collapsed;SelectNavigation("settings");VideoPanel.Visibility=Visibility.Collapsed;Capture(Path.Combine(root,"settings.png"));
            SettingsPanel.ScrollToBottom();await Task.Delay(100);Capture(Path.Combine(root,"audio-checks.png"));
            SelectNavigation("camera");VideoPanel.Visibility=Visibility.Visible;ApplyVideoLayout();
            if(PageHeader.Visibility!=Visibility.Visible||string.IsNullOrWhiteSpace(PageDescription.Text))throw new InvalidOperationException("Описание камеры не отображается");
            Capture(Path.Combine(root,"camera-header.png"));
            var stopBounds=StopStreamButton.TransformToAncestor(VideoToolbar).TransformBounds(new Rect(0,0,StopStreamButton.ActualWidth,StopStreamButton.ActualHeight));
            var muteBounds=VideoMuteBox.TransformToAncestor(VideoToolbar).TransformBounds(new Rect(0,0,VideoMuteBox.ActualWidth,VideoMuteBox.ActualHeight));
            if(muteBounds.Left<stopBounds.Right||Math.Abs(muteBounds.Top-stopBounds.Top)>2)throw new InvalidOperationException("Переключатели не стоят справа от остановки");
            var navigationBounds=NavigationBar.TransformToAncestor(HomePanel).TransformBounds(new Rect(0,0,NavigationBar.ActualWidth,NavigationBar.ActualHeight));
            if(Math.Abs(navigationBounds.Left+navigationBounds.Width/2-HomePanel.ActualWidth/2)>2)throw new InvalidOperationException("Меню не отцентрировано");
            var originalWidth=Width;var originalHeight=Height;Width=1080;Height=740;ApplyVideoLayout();await Task.Delay(100);Capture(Path.Combine(root,"camera-small.png"));
            var toggleBounds=LowLatencyBox.TransformToAncestor(VideoToolbar).TransformBounds(new Rect(0,0,LowLatencyBox.ActualWidth,LowLatencyBox.ActualHeight));
            if(toggleBounds.Right>VideoToolbar.ActualWidth-6||toggleBounds.Bottom>VideoToolbar.ActualHeight-6)throw new InvalidOperationException("Режимы воспроизведения выходят за пределы панели");
            Width=originalWidth;Height=originalHeight;ApplyVideoLayout();
            if(Math.Abs(VideoFrame.ActualWidth-VideoStage.ActualWidth)>1||Math.Abs(VideoFrame.ActualHeight-VideoStage.ActualHeight)>1)throw new InvalidOperationException("Видеополе не заполняет белый блок");
            var folder=Path.Combine(AppContext.BaseDirectory,"Assets","AudioTests");
            using var phones=new NAudio.Wave.WaveFileReader(Path.Combine(folder,"headphones.wav"));using var melody=AudioAssets.OpenRingtone();
            if(phones.WaveFormat.Channels!=2||melody.TotalTime.TotalSeconds<2)throw new InvalidOperationException("Не найдены звуковые файлы проверки");
            var door=new Door("test","test","Тестовый звонок себе","SIP",null,null,"LOCAL_TEST",false,true,10800,new());
            var audible=Environment.GetCommandLineArgs().Contains("--audible-diagnostic");
            var window=new CallWindow(api,door,null,Guid.NewGuid().ToString(),true,speakerDevice:(SpeakerBox.SelectedItem as DeviceItem)?.Id??-1,microphoneDevice:(MicrophoneBox.SelectedItem as DeviceItem)?.Id??-1){SilentDiagnostic=!audible,Topmost=false,ShowActivated=audible};
            if(!audible){window.WindowStartupLocation=WindowStartupLocation.Manual;window.Left=SystemParameters.VirtualScreenLeft-2000;window.Top=SystemParameters.VirtualScreenTop-2000;}
            window.Show();
            if(!window.TestDoorButtonEnabled)throw new InvalidOperationException("Тестовая кнопка открытия недоступна");
            await window.OpenTestDoorAsync();
            if(!window.TestDoorButtonEnabled||!window.TestDoorConfirmation.Contains("Команда домофону не отправлялась"))throw new InvalidOperationException("Нет тестового подтверждения открытия");
            for(int i=0;i<50&&!window.TestVideoPlaying;i++)await Task.Delay(100);
            if(!window.TestVideoPlaying)throw new InvalidOperationException("Тестовое видео не запустилось");
            if(audible&&!window.RingtoneStarted)throw new InvalidOperationException("Рингтон не запустился: "+window.AudioError);
            if(audible)await Task.Delay(4000);
            await window.AnswerTestAsync();await Task.Delay(17000);
            if(!window.TestVideoPlaying)throw new InvalidOperationException("Повтор видео прервался");
            if(audible&&!window.VoicePlaybackCompleted)throw new InvalidOperationException("Запись своего голоса не воспроизвелась: "+window.AudioError);
            if(!window.TestVideoRounded)throw new InvalidOperationException("Видеополе должно быть скруглено");
            var snapshot=Path.Combine(root,"video.png");window.SaveTestFrame(snapshot);
            for(int i=0;i<20&&!System.IO.File.Exists(snapshot);i++)await Task.Delay(100);
            await Task.Delay(300);
            var ended=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);window.Closed+=(_,_)=>ended.TrySetResult();window.Close();await ended.Task.WaitAsync(TimeSpan.FromSeconds(8));
            await File.WriteAllTextAsync(Path.Combine(root,"verification.json"),new System.Text.Json.Nodes.JsonObject{["baseVersion"]="0.1.15",["headphoneChannels"]=phones.WaveFormat.Channels,["ringtoneDurationSeconds"]=melody.TotalTime.TotalSeconds,["ringtoneEmbedded"]=true,["cameraDescriptionVisible"]=true,["videoRepeated"]=true,["videoFillsContainer"]=true,["roundedVideoRegion"]=true,["testWindowClosed"]=true,["physicalAudioDevicesUsed"]=audible,["recordedVoicePeak"]=window.RecordedVoicePeak,["recordedVoicePlayed"]=window.VoicePlaybackCompleted}.ToJsonString(new(){WriteIndented=true}));
        }catch(Exception ex){await File.WriteAllTextAsync(Path.Combine(root,"error.txt"),ex.ToString());}
        finally{RequestExit();}
    }
    private void RestoreAudioDevices()
    {
        if(store is null)return;restoringAudioDevices=true;
        try{var saved=store.LoadAudioDevices();MicrophoneBox.SelectedItem=MicrophoneBox.Items.OfType<DeviceItem>().FirstOrDefault(x=>x.Label==saved.Microphone)??MicrophoneBox.Items[0];SpeakerBox.SelectedItem=SpeakerBox.Items.OfType<DeviceItem>().FirstOrDefault(x=>x.Label==saved.Speaker)??SpeakerBox.Items[0];}
        finally{restoringAudioDevices=false;}
    }
    private void AudioDeviceChanged(object sender,System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if(store is null||restoringAudioDevices)return;
        store.SaveAudioDevices((MicrophoneBox.SelectedItem as DeviceItem)?.Label,(SpeakerBox.SelectedItem as DeviceItem)?.Label);
    }
    private async void CheckMicrophoneClick(object sender,RoutedEventArgs e)
    {
        if(audioCheckRunning)return;
        audioCheckRunning=true;MicrophoneTestButton.IsEnabled=false;HeadphonesTestButton.IsEnabled=false;
        audioCheckCancellation=new();var ct=audioCheckCancellation.Token;
        try{
            audioCheckPlayback?.Dispose();audioCheckPlayback=null;
            using var recorder=new MicrophoneRecorder((MicrophoneBox.SelectedItem as DeviceItem)?.Id??-1);
            recorder.Level+=level=>Dispatcher.BeginInvoke(()=>{MicrophoneLevel.Value=level;});recorder.Start();
            for(int remaining=4;remaining>0;remaining--){AudioCheckStatus.Text=$"Говорите в микрофон — запись {remaining} с";await Task.Delay(1000,ct);}
            var result=recorder.Finish();
            if(recorder.Error is not null)throw recorder.Error;
            if(result.CapturedBytes==0||result.Peak<64){AudioCheckStatus.Text="Сигнал микрофона не обнаружен. Выберите другое устройство и повторите проверку.";return;}
            audioCheckPlayback=AudioPlayback.Pcm(result.Pcm,(SpeakerBox.SelectedItem as DeviceItem)?.Id??-1);
            var finished=new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);audioCheckPlayback.Finished+=error=>finished.TrySetResult(error);audioCheckPlayback.Play();
            AudioCheckStatus.Text="Прослушайте запись своего голоса";var error=await finished.Task.WaitAsync(ct);if(error is not null)throw error;
            AudioCheckStatus.Text="Проверка завершена. Микрофон получил звук, запись воспроизведена.";
        }catch(OperationCanceledException){AudioCheckStatus.Text="Проверка остановлена";}catch(Exception ex){AudioCheckStatus.Text="Не удалось проверить микрофон: "+ex.Message;}
        finally{audioCheckPlayback?.Dispose();audioCheckPlayback=null;audioCheckCancellation?.Dispose();audioCheckCancellation=null;audioCheckRunning=false;MicrophoneTestButton.IsEnabled=true;HeadphonesTestButton.IsEnabled=true;MicrophoneLevel.Value=0;}
    }
    private void CheckHeadphonesClick(object sender,RoutedEventArgs e)
        =>StartOutputCheck();
    private void StartOutputCheck()
    {
        if(audioCheckRunning)return;
        try{
            audioCheckPlayback?.Dispose();audioCheckPlayback=AudioPlayback.File(Path.Combine(AppContext.BaseDirectory,"Assets","AudioTests","headphones.wav"),(SpeakerBox.SelectedItem as DeviceItem)?.Id??-1);
            var playback=audioCheckPlayback;playback.Finished+=error=>Dispatcher.BeginInvoke(()=>{if(audioCheckPlayback==playback){AudioCheckStatus.Text=error is null?"Проверка наушников завершена: левый канал, правый канал, оба канала.":"Ошибка воспроизведения: "+error.Message;playback.Dispose();audioCheckPlayback=null;}});
            audioCheckPlayback.Play();AudioCheckStatus.Text="Проверка наушников: левый канал → правый канал → оба канала";
        }catch(Exception ex){AudioCheckStatus.Text="Не удалось проверить наушники: "+ex.Message;}
    }
    private void StopAudioCheckClick(object sender,RoutedEventArgs e){audioCheckCancellation?.Cancel();audioCheckPlayback?.Dispose();audioCheckPlayback=null;AudioCheckStatus.Text="Проверка остановлена";}
    private void TestCallClick(object sender,RoutedEventArgs e)
    {
        if(api is null)return;
        if(callWindow is {IsEnded:false}){callWindow.Show();callWindow.Activate();return;}
        var door=new Door("local-test","local-test","Тестовый звонок себе","SIP",null,null,"LOCAL_TEST",false,true,10800,new());
        callWindow=new CallWindow(api,door,null,Guid.NewGuid().ToString(),true,(SpeakerBox.SelectedItem as DeviceItem)?.Id??-1,(MicrophoneBox.SelectedItem as DeviceItem)?.Id??-1);TrackCall(callWindow);callWindow.Show();callWindow.Activate();
    }
}
