using System.ComponentModel;
using System.Media;
using System.Windows;
using System.Windows.Threading;
using Domru.Desktop.Core;

namespace Domru.Desktop;
public partial class CallWindow : Window
{
    private readonly DomruApi api;
    private readonly Door door;
    private readonly VideoPlayer video = new();
    private AudioPlayback? ringtone,testSpeech;
    private readonly bool testCall;
    private readonly int speakerDevice;
    private readonly int microphoneDevice;
    private readonly CancellationTokenSource testLifetime=new();
    private bool checkingTestMicrophone;
    internal bool VoicePlaybackStarted {get;private set;}
    internal bool VoicePlaybackCompleted {get;private set;}
    internal int RecordedVoicePeak {get;private set;}
    private readonly DispatcherTimer microphoneStatus=new(){Interval=TimeSpan.FromMilliseconds(300)};
    private readonly DispatcherTimer timeout = new()
    {
        Interval = TimeSpan.FromSeconds(35)
    };
    private bool closed, finishing, ended, opening;
    private string? attachedCallId;
    public SipClient? Client { get; private set; }
    public string CallId { get; }
    public string DoorId => door.Id;
    public bool IsEnded => ended;
    internal bool PreviewMode { get; set; }
    internal bool SilentDiagnostic {get;set;}
    internal bool IsTestCall=>testCall;
    internal bool TestVideoPlaying=>!closed&&video.Player.IsPlaying;
    internal bool TestVideoRounded=>Video.HasRoundedRegion;
    internal bool RingtoneStarted=>ringtone is not null;
    internal string AudioError=>ErrorLabel.Text;

    public CallWindow(DomruApi api, Door door, SipClient? client, string id,bool testCall=false,int speakerDevice=-1,int microphoneDevice=-1)
    {
        InitializeComponent();
        this.api = api;
        this.door = door;
        this.testCall=testCall;this.speakerDevice=client?.OutputDevice??speakerDevice;
        this.microphoneDevice=client?.InputDevice??microphoneDevice;
        Client = client;
        attachedCallId = client?.CurrentCall?.Id;
        CallId = id;
        DoorTitle.Text = door.Name;
        
        AnswerButton.IsEnabled = testCall||client?.CurrentCall is not null;
        OpenButton.IsEnabled = door.AllowOpen;
        if(testCall){CallHeader.Text="ТЕСТОВЫЙ ЗВОНОК";CallBadge.Text="ПРОВЕРКА";CallState.Text="Ответьте, говорите 4 секунды и прослушайте свой голос";OpenButton.IsEnabled=true;OpenButton.ToolTip="Тестовое подтверждение — настоящий домофон не открывается";MuteBox.Visibility=Visibility.Collapsed;}
        video.State += s => Dispatcher.BeginInvoke(() =>
        {
            Placeholder.Visibility = s == "Воспроизведение" ? Visibility.Collapsed : Visibility.Visible;
            Placeholder.Text = s;
            CallEmptyState.Visibility = s == "Воспроизведение" ? Visibility.Collapsed : Visibility.Visible;
            if(s == "Воспроизведение")Video.Visibility=Visibility.Visible;
        });
        timeout.Tick += (_, _) => End("Время ожидания истекло");
        microphoneStatus.Tick+=(_,_)=>UpdateMicrophoneStatus();
        Loaded += OnLoaded;
    }
    private void TitleDrag(object sender,System.Windows.Input.MouseButtonEventArgs e)
    {
        if(e.LeftButton==System.Windows.Input.MouseButtonState.Pressed) DragMove();
    }
    private void CloseWindowClick(object sender,RoutedEventArgs e)=>Close();
    private void RoundPlaceholder(object sender,SizeChangedEventArgs e)=>((UIElement)sender).Clip=new System.Windows.Media.RectangleGeometry(new Rect(e.NewSize),18,18);

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (PreviewMode)
            return;
        if(!SilentDiagnostic)try{ringtone=AudioPlayback.Ringtone(speakerDevice);ringtone.Play();}catch(Exception ex){ErrorLabel.Text="Не удалось проиграть мелодию: "+ex.Message;}
        timeout.Start();
        try
        {
            if (!testCall&&door.CameraId is null)
                throw new InvalidOperationException("Нет камеры");
            var url = testCall?new Uri(Path.Combine(AppContext.BaseDirectory,"Assets","AudioTests","test-video.avi")).AbsoluteUri:await api.VideoAsync(door.CameraId!);
            if (!closed)
                { Video.Visibility=Visibility.Visible; CallEmptyState.Visibility=Visibility.Collapsed; UpdateLayout(); await Dispatcher.InvokeAsync(()=>{},DispatcherPriority.Loaded); if(Video.Handle==IntPtr.Zero)throw new InvalidOperationException("Видеополе звонка не готово"); await video.PlayAsync(url,Video.Handle,true,live:!testCall,repeat:testCall); }
        }
        catch (Exception ex)
        {
            ErrorLabel.Text = ex.Message;
            Placeholder.Text = "Камера недоступна";
        }
    }

    public void Attach(SipClient client)
    {
        Client = client;
        attachedCallId = client.CurrentCall?.Id;
        AnswerButton.IsEnabled = !ended;
    }

    private async void AnswerClick(object sender, RoutedEventArgs e)
        =>await AnswerCoreAsync();
    private async Task AnswerCoreAsync()
    {
        if (Client is null&&!testCall)
            return;
        AnswerButton.IsEnabled = false;
        try
        {
            ringtone?.Dispose();ringtone=null;
            if(testCall){if(!SilentDiagnostic)_=RunTestMicrophoneAsync();}
            else await Client!.AnswerAsync();
            timeout.Stop();
            if(!testCall||SilentDiagnostic)CallState.Text = testCall?"Тестовый вызов · проверка видео и звука":"Разговор · микрофон включён";
            if(!testCall){MicrophoneDiagnostics.Visibility=Visibility.Visible;microphoneStatus.Start();}
            Topmost = false;
        }
        catch (Exception ex)
        {
            ErrorLabel.Text = ex.Message;
            AnswerButton.IsEnabled = testCall||Client?.CurrentCall is not null;
        }
    }

    private async void OpenClick(object sender, RoutedEventArgs e) => await OpenDoorCoreAsync();
    internal Task OpenTestDoorAsync()=>OpenDoorCoreAsync();
    internal bool TestDoorButtonEnabled=>OpenButton.IsEnabled;
    internal string TestDoorConfirmation=>TestDoorStatus.Text;
    private async Task OpenDoorCoreAsync()
    {
        if(ended||closed)return;
        if (opening)
            return;
        opening = true;
        OpenButton.IsEnabled = false;
        try
        {
            if(testCall)
            {
                TestDoorStatus.Visibility=Visibility.Visible;
                TestDoorStatus.Text="Тестовое открытие…";
                await Task.Delay(300,testLifetime.Token);
                TestDoorStatus.Text="Тест: дверь открыта. Команда домофону не отправлялась.";
            }
            else
            {
                await api.OpenAsync(door);
                CallState.Text = "Команда открытия принята";
            }
        }
        catch (Exception ex)
        {
            ErrorLabel.Text = ex.Message;
        }
        finally
        {
            opening = false;
            OpenButton.IsEnabled = (testCall || door.AllowOpen) && !ended && !closed;
        }
    }

    private void MuteChanged(object sender, RoutedEventArgs e)
    {
        Client?.SetMute(MuteBox.IsChecked == true);
    }

    private async void HangupClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Client is not null)
                await Client.HangupAsync();
            Close();
        }
        catch (Exception ex)
        {
            ErrorLabel.Text = ex.Message;
        }
    }

    public void End(string reason)
    {
        if (closed)
            return;
        ended = true;
        ringtone?.Dispose();ringtone=null;testSpeech?.Dispose();testSpeech=null;
        testLifetime.Cancel();
        timeout.Stop();
        microphoneStatus.Stop();
        CallState.Text = reason;
        AnswerButton.IsEnabled = OpenButton.IsEnabled = false;
        _ = CloseLaterAsync();
    }

    private async Task CloseLaterAsync()
    {
        await Task.Delay(2500);
        if (!closed)
            Close();
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closed)
            return;
        e.Cancel = true;
        if (finishing)
            return;
        finishing = true;
        ringtone?.Dispose();ringtone=null;testSpeech?.Dispose();testSpeech=null;
        testLifetime.Cancel();
        timeout.Stop();
        microphoneStatus.Stop();
        try
        {
            if (Client is not null && Client.CurrentCall?.Id == attachedCallId && attachedCallId is not null)
                await Client.HangupAsync();
        }
        catch
        {
        }
        finally
        {
            closed = true;
            await video.DisposeAsync();
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
    }
    internal Task AnswerTestAsync()=>AnswerCoreAsync();
    private async void RepeatTestMicrophoneClick(object sender,RoutedEventArgs e)=>await RunTestMicrophoneAsync();
    private async Task RunTestMicrophoneAsync()
    {
        if(checkingTestMicrophone||closed||testLifetime.IsCancellationRequested)return;
        checkingTestMicrophone=true;VoicePlaybackStarted=false;VoicePlaybackCompleted=false;RepeatMicrophoneButton.Visibility=Visibility.Visible;RepeatMicrophoneButton.IsEnabled=false;MicrophoneDiagnostics.Visibility=Visibility.Visible;ErrorLabel.Text="";
        try{
            using var recorder=new MicrophoneRecorder(microphoneDevice);
            recorder.Level+=level=>Dispatcher.BeginInvoke(()=>{if(!closed)CallMicrophoneLevel.Value=level;});recorder.Start();
            for(int seconds=4;seconds>0;seconds--){CallState.Text=$"Говорите в микрофон · запись {seconds} с";CallAudioStatus.Text="После записи вы услышите свой голос";await Task.Delay(1000,testLifetime.Token);}
            var recording=recorder.Finish();if(recorder.Error is not null)throw recorder.Error;
            RecordedVoicePeak=recording.Peak;
            if(recording.CapturedBytes==0||recording.Peak<64){CallState.Text="Микрофон не получил звук";CallAudioStatus.Text="Выберите микрофон в настройках и повторите тест";return;}
            testSpeech?.Dispose();testSpeech=AudioPlayback.Pcm(recording.Pcm,speakerDevice);
            var finished=new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);testSpeech.Finished+=error=>finished.TrySetResult(error);testSpeech.Play();VoicePlaybackStarted=true;
            CallState.Text="Прослушайте запись своего голоса";CallAudioStatus.Text="Воспроизводится записанный микрофон";
            var error=await finished.Task.WaitAsync(testLifetime.Token);if(error is not null)throw error;
            VoicePlaybackCompleted=true;
            CallState.Text="Проверка микрофона завершена";CallAudioStatus.Text="Можно повторить запись или завершить вызов";
        }catch(OperationCanceledException){}catch(Exception ex){if(!closed){ErrorLabel.Text="Проверка микрофона: "+ex.Message;CallState.Text="Не удалось проверить микрофон";}}
        finally{testSpeech?.Dispose();testSpeech=null;checkingTestMicrophone=false;if(!closed){RepeatMicrophoneButton.IsEnabled=true;CallMicrophoneLevel.Value=0;}}
    }
    private void UpdateMicrophoneStatus()
    {
        if(Client?.AudioStats is not { } stats)return;
        CallMicrophoneLevel.Value=stats.Muted?0:stats.MicrophoneLevel;
        CallAudioStatus.Text=stats.CaptureError is not null?"Ошибка микрофона: "+stats.CaptureError:stats.Muted?"Микрофон выключен":stats.CapturedBytes==0?"Ожидаем данные микрофона":stats.SpeechPackets==0?"Микрофон работает · произнесите несколько слов":$"Звук микрофона передаётся · пакетов со звуком: {stats.SpeechPackets}";
    }
    internal bool SaveTestFrame(string path)=>video.Player.TakeSnapshot(0,path,0,0);
}


